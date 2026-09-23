using System.Collections;
using UnityEngine;

namespace KirbyScream
{
    /// <summary>
    /// One screaming mouth. Owns an AudioSource that follows a character's head and attenuates with
    /// distance using the same curve PEAK applies to proximity voice chat, so a falling teammate
    /// sounds like they are shouting from wherever they actually are.
    ///
    /// The local player's own scream is played flat (2D) instead, so you always hear yourself clearly.
    /// </summary>
    public class ScreamVoice : MonoBehaviour
    {
        /// Safety net: if the owner disconnects mid-fall we never get a stop event.
        private const float MaxScreamSeconds = 60f;

        private Character _character;
        private AudioSource _source;
        private bool _spatial;
        private float _playingFor;
        private Coroutine _fade;
        private bool _fading;

        public bool IsPlaying => _source != null && _source.isPlaying;

        /// True while this voice is still attached to that exact, living character.
        public bool IsFor(Character character) => _character != null && _character == character;

        public static ScreamVoice Create(Character character, AudioClip clip, bool spatial)
        {
            var go = new GameObject("KirbyScreamVoice");
            go.hideFlags = HideFlags.HideAndDontSave;

            var voice = go.AddComponent<ScreamVoice>();
            voice._character = character;
            voice._spatial = spatial;

            AudioSource src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.playOnAwake = false;
            src.priority = 0;
            src.dopplerLevel = Plugin.DopplerLevel.Value;
            src.spatialBlend = spatial ? 1f : 0f;

            if (Plugin.UseGameSfxMixer.Value && SFX_Player.instance != null && SFX_Player.instance.defaultMixerGroup != null)
                src.outputAudioMixerGroup = SFX_Player.instance.defaultMixerGroup;

            if (spatial)
            {
                // Let Unity handle direction only; volume is driven manually below to match the game's
                // voice falloff. A constant rolloff curve keeps Unity from attenuating on top of that.
                src.rolloffMode = AudioRolloffMode.Custom;
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Constant(0f, 1f, 1f));
                src.minDistance = 1f;
                src.maxDistance = Plugin.MaxHearingDistance.Value;
            }

            voice._source = src;
            voice.SyncPosition();
            return voice;
        }

        public void Play()
        {
            if (_source == null || _source.clip == null) return;
            if (_fade != null) { StopCoroutine(_fade); _fade = null; }
            _fading = false;
            _playingFor = 0f;
            _source.loop = Plugin.Loop.Value;
            _source.volume = CurrentVolume();
            _source.time = 0f;
            _source.Play();
        }

        public void Stop()
        {
            if (_source == null || !_source.isPlaying || _fading) return;

            float fade = Plugin.StopFadeSeconds.Value;
            if (fade <= 0f)
            {
                _source.Stop();
                return;
            }

            if (_fade != null) StopCoroutine(_fade);
            _fade = StartCoroutine(FadeOutAndStop(fade));
        }

        private IEnumerator FadeOutAndStop(float seconds)
        {
            _fading = true;
            float start = _source.volume;
            float t = 0f;
            while (t < seconds && _source.isPlaying)
            {
                t += Time.unscaledDeltaTime;
                _source.volume = Mathf.Lerp(start, 0f, t / seconds);
                yield return null;
            }
            _source.Stop();
            _fading = false;
            _fade = null;
        }

        public void Dispose()
        {
            if (this != null && gameObject != null) Destroy(gameObject);
        }

        private float BaseVolume()
        {
            return _spatial ? Plugin.RemoteVolume.Value : Plugin.Volume.Value;
        }

        private float CurrentVolume()
        {
            if (!_spatial) return BaseVolume();

            Transform listener = ListenerTransform();
            if (listener == null) return BaseVolume();

            // Mirrors CharacterVoiceHandler: audible out to roughly 200 m, gentle near falloff.
            float distance = Vector3.Distance(transform.position, listener.position);
            float t = Mathf.InverseLerp(Plugin.FalloffNearDistance.Value, Plugin.MaxHearingDistance.Value, distance);
            float falloff = Mathf.Clamp01(1f - Mathf.Log(Mathf.Lerp(1f, 10f, t)));
            return BaseVolume() * falloff;
        }

        private static Transform ListenerTransform()
        {
            if (MainCamera.instance != null) return MainCamera.instance.transform;
            AudioListener listener = FindAnyObjectByType<AudioListener>();
            return listener != null ? listener.transform : null;
        }

        private void SyncPosition()
        {
            if (_character == null) return;
            transform.position = _character.Head;
        }

        private void LateUpdate()
        {
            if (_character == null || _character.refs == null)
            {
                Stop();
                return;
            }

            SyncPosition();

            if (!_source.isPlaying) return;

            _playingFor += Time.deltaTime;
            if (_playingFor > MaxScreamSeconds)
            {
                Plugin.Log.LogWarning("Scream ran past its time limit, stopping (did the owner disconnect?).");
                Stop();
                return;
            }

            // Belt and braces: a remote owner can drop out before sending the stop event.
            if (_spatial && (_character.data.isGrounded || _character.data.dead))
            {
                Stop();
                return;
            }

            if (!_fading) _source.volume = CurrentVolume();
        }
    }
}
