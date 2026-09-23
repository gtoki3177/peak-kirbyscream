using System;
using Photon.Voice;
using UnityEngine;

namespace KirbyScream
{
    /// <summary>
    /// Mixes the scream into the outgoing voice-chat stream.
    ///
    /// Photon Voice calls the processors below on its own audio thread for every chunk of microphone
    /// audio, before voice detection, noise suppression and encoding. Everything the audio thread
    /// touches lives here behind one lock; the main thread only flips state and swaps sample buffers.
    /// </summary>
    internal static class VoiceScreamMixer
    {
        private static readonly object Gate = new object();

        // Scream samples already converted to the voice stream's rate and channel layout (interleaved).
        private static float[] _samples;
        private static int _rate;
        private static int _channels;

        private static bool _playing;
        private static bool _loop;
        private static float _gain;
        private static int _pos;
        private static int _fadeLen;
        private static int _fadeLeft;

        // Sample position when the scream last stopped, and when (main-thread unscaled time).
        private static int _resumePos = -1;
        private static float _stoppedAt;

        // When true the real microphone is silenced and only the scream goes out. Used while
        // transmission is held open for a push-to-talk player who is not holding the key.
        private static bool _muteMic;

        public static bool HasSamplesFor(int rate, int channels)
        {
            lock (Gate) return _samples != null && _rate == rate && _channels == channels;
        }

        public static void SetSamples(float[] samples, int rate, int channels)
        {
            lock (Gate)
            {
                // Same buffer again: keep the position and resume point untouched.
                if (ReferenceEquals(samples, _samples) && rate == _rate && channels == _channels) return;

                _samples = samples;
                _rate = rate;
                _channels = channels;
                _pos = 0;
                _playing = false;
                _resumePos = -1;   // positions from the old buffer do not map onto the new one
            }
        }

        public static bool MuteMic
        {
            get { lock (Gate) return _muteMic; }
            set { lock (Gate) _muteMic = value; }
        }

        /// <param name="now">Main-thread Time.unscaledTime.</param>
        /// <param name="resumeWindow">Continue from the last stop if it was at most this many seconds ago. 0 = always restart.</param>
        public static void Start(float gain, bool loop, float now, float resumeWindow)
        {
            lock (Gate)
            {
                if (_samples == null) return;

                int resume = _resumePos;
                _resumePos = -1;
                bool canResume = resumeWindow > 0f && resume >= 0 && resume < _samples.Length
                                 && now - _stoppedAt <= resumeWindow;

                _gain = gain;
                _loop = loop;
                // Keep channel frames aligned when resuming mid-buffer.
                _pos = canResume ? resume - resume % Math.Max(1, _channels) : 0;
                _fadeLen = 0;
                _fadeLeft = 0;
                _playing = true;
            }
        }

        /// <param name="now">Main-thread Time.unscaledTime.</param>
        public static void Stop(float fadeSeconds, float now)
        {
            lock (Gate)
            {
                // A scream that already played to its end has nothing to resume, so the next one starts over.
                if (!_playing) return;

                // Record the spot on the first stop only; a repeat call during the fade must not move it.
                if (_fadeLen == 0)
                {
                    _resumePos = _pos;
                    _stoppedAt = now;
                }

                int fade = (int)(fadeSeconds * _rate) * Math.Max(1, _channels);
                if (fade <= 0)
                {
                    _playing = false;
                    return;
                }
                if (_fadeLen == 0)
                {
                    _fadeLen = fade;
                    _fadeLeft = fade;
                }
            }
        }

        // ------------------------------------------------------------------ audio thread

        /// Returns the next scream sample scaled by gain and fade, or 0 once finished. Caller holds the lock.
        private static float NextSample()
        {
            if (!_playing) return 0f;

            float g = _gain;
            if (_fadeLen > 0)
            {
                g *= (float)_fadeLeft / _fadeLen;
                if (--_fadeLeft <= 0) _playing = false;
            }

            float v = _samples[_pos] * g;
            if (++_pos >= _samples.Length)
            {
                if (_loop) _pos = 0;
                else _playing = false;
            }
            return v;
        }

        public static void Mix(float[] buf)
        {
            if (buf == null) return;
            lock (Gate)
            {
                if (_muteMic) Array.Clear(buf, 0, buf.Length);
                if (!_playing || _samples == null) return;

                for (int i = 0; i < buf.Length && _playing; i++)
                {
                    float v = buf[i] + NextSample();
                    buf[i] = v > 1f ? 1f : (v < -1f ? -1f : v);
                }
            }
        }

        public static void Mix(short[] buf)
        {
            if (buf == null) return;
            lock (Gate)
            {
                if (_muteMic) Array.Clear(buf, 0, buf.Length);
                if (!_playing || _samples == null) return;

                for (int i = 0; i < buf.Length && _playing; i++)
                {
                    int v = buf[i] + (int)(NextSample() * short.MaxValue);
                    buf[i] = (short)(v > short.MaxValue ? short.MaxValue : (v < short.MinValue ? short.MinValue : v));
                }
            }
        }

        // ------------------------------------------------------------------ main thread helpers

        /// Converts a clip to mono, resamples it to the voice rate, then fans it out to the voice channel count.
        /// Must run on the main thread because AudioClip.GetData does.
        public static float[] ConvertClip(AudioClip clip, int dstRate, int dstChannels)
        {
            int srcChannels = Math.Max(1, clip.channels);
            int srcFrames = clip.samples;
            int srcRate = clip.frequency;
            if (srcFrames <= 0 || srcRate <= 0 || dstRate <= 0) return null;

            var interleaved = new float[srcFrames * srcChannels];
            if (!clip.GetData(interleaved, 0)) return null;

            var mono = new float[srcFrames];
            for (int f = 0; f < srcFrames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < srcChannels; c++) sum += interleaved[f * srcChannels + c];
                mono[f] = sum / srcChannels;
            }

            int dstFrames = (int)((long)srcFrames * dstRate / srcRate);
            dstChannels = Math.Max(1, dstChannels);
            var dst = new float[dstFrames * dstChannels];
            double step = (double)srcRate / dstRate;

            for (int f = 0; f < dstFrames; f++)
            {
                // Average over the source span each output sample covers, so downsampling does not alias badly.
                double start = f * step;
                double end = start + step;
                int i0 = (int)start;
                int i1 = Math.Min(srcFrames - 1, (int)Math.Ceiling(end) - 1);
                float v;
                if (i1 <= i0)
                {
                    int n = Math.Min(i0 + 1, srcFrames - 1);
                    double frac = start - i0;
                    v = (float)(mono[i0] * (1 - frac) + mono[n] * frac);
                }
                else
                {
                    float sum = 0f;
                    for (int i = i0; i <= i1; i++) sum += mono[i];
                    v = sum / (i1 - i0 + 1);
                }

                for (int c = 0; c < dstChannels; c++) dst[f * dstChannels + c] = v;
            }

            return dst;
        }
    }

    /// Photon Voice pre-processor for float voice streams.
    internal sealed class FloatScreamProcessor : IProcessor<float>
    {
        public float[] Process(float[] buf)
        {
            VoiceScreamMixer.Mix(buf);
            return buf;
        }

        public void Dispose() { }
    }

    /// Photon Voice pre-processor for 16-bit voice streams (used when WebRTC audio processing is on).
    internal sealed class ShortScreamProcessor : IProcessor<short>
    {
        public short[] Process(short[] buf)
        {
            VoiceScreamMixer.Mix(buf);
            return buf;
        }

        public void Dispose() { }
    }
}
