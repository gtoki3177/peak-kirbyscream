using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace KirbyScream
{
    /// <summary>
    /// Watches the local player every frame. Starts the scream when a real fall begins and stops it
    /// the moment the player lands, dies, grabs something or opens a parachute.
    ///
    /// The scream goes out through voice chat (or a network message, depending on BroadcastMode) so
    /// other players hear it from the falling player.
    /// </summary>
    public class FallScreamController : MonoBehaviour, IOnEventCallback
    {
        // CharacterBalloons._isParachuteOpen is private; read it through Harmony's field accessor.
        private static readonly AccessTools.FieldRef<CharacterBalloons, bool> ParachuteOpenField = MakeParachuteRef();

        private ScreamVoice _localVoice;
        private readonly Dictionary<int, ScreamVoice> _remoteVoices = new Dictionary<int, ScreamVoice>();

        private enum BroadcastPath { None, VoiceChat, ModNetwork }

        private bool _screaming;
        private float _notFallingFor;
        private BroadcastPath _path;
        private bool _warnedVoiceFallback;

        // Sound selection. _currentClip is what the last scream used; a quick follow-up fall keeps it
        // so the scream can resume. _nextRandom is rolled ahead of time in Random mode so its voice
        // buffer can be converted before it is needed.
        private AudioClip _currentClip;
        private AudioClip _nextRandom;
        private float _lastStopAt = float.NegativeInfinity;

        // Scream buffers already converted to the local voice stream's format, keyed by clip.
        private readonly Dictionary<AudioClip, float[]> _voiceBuffers = new Dictionary<AudioClip, float[]>();
        private int _voiceBufferRate;
        private int _voiceBufferChannels;

        private static AccessTools.FieldRef<CharacterBalloons, bool> MakeParachuteRef()
        {
            try { return AccessTools.FieldRefAccess<CharacterBalloons, bool>("_isParachuteOpen"); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not bind CharacterBalloons._isParachuteOpen, falling back to parachute GameObject state. " + e.Message);
                return null;
            }
        }

        private void Awake()
        {
            StartCoroutine(SoundLibrary.LoadAll());
            Plugin.Sound.SettingChanged += OnSoundSettingChanged;
        }

        private void OnDestroy()
        {
            if (Plugin.Sound != null) Plugin.Sound.SettingChanged -= OnSoundSettingChanged;
        }

        private void OnEnable() => PhotonNetwork.AddCallbackTarget(this);

        private void OnDisable()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
            VoiceInjector.Current?.EndScream(0f);
            StopAllVoices();
        }

        // A new choice takes effect on the next fall, even inside the resume window.
        private void OnSoundSettingChanged(object sender, EventArgs e)
        {
            _currentClip = null;
            _nextRandom = null;
            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"Sound changed to {Plugin.Sound.Value}");
        }

        // ------------------------------------------------------------------ sound selection

        private static bool RandomMode =>
            string.Equals(Plugin.Sound.Value, SoundLibrary.RandomChoice, StringComparison.OrdinalIgnoreCase);

        /// The clip a brand-new scream should use.
        private AudioClip PickClipForNewScream()
        {
            if (RandomMode)
            {
                AudioClip pick = _nextRandom != null ? _nextRandom : SoundLibrary.PickRandom(_currentClip);
                _nextRandom = SoundLibrary.PickRandom(pick);
                return pick;
            }
            return SoundLibrary.Get(Plugin.Sound.Value) ?? SoundLibrary.Fallback;
        }

        /// The clip the next fresh scream will most likely use, for converting ahead of time.
        private AudioClip UpcomingClip()
        {
            if (!RandomMode) return SoundLibrary.Get(Plugin.Sound.Value) ?? SoundLibrary.Fallback;
            if (_nextRandom == null) _nextRandom = SoundLibrary.PickRandom(_currentClip);
            return _nextRandom;
        }

        private bool WithinResumeWindow()
        {
            float window = Plugin.ResumeWindowSeconds.Value;
            return window > 0f && Time.unscaledTime - _lastStopAt <= window;
        }

        // ------------------------------------------------------------------ detection

        private static bool IsParachuteOpen(Character c)
        {
            CharacterBalloons balloons = c.refs?.balloons;
            if (balloons != null && ParachuteOpenField != null)
                return ParachuteOpenField(balloons);

            Parachute chute = c.refs?.parachute;
            return chute != null && chute.gameObject.activeSelf;
        }

        private void Update()
        {
            Character c = Character.localCharacter;
            if (c == null || c.data == null || c.refs == null)
            {
                if (_screaming) Stop("no local character");
                return;
            }

            MaintainVoiceInjection(c);

            CharacterData d = c.data;

            // Hard-stop conditions: the fall is over (or never counts as a fall).
            string stopReason = null;
            if (d.dead) stopReason = "died";
            else if (d.isGrounded) stopReason = "landed";
            else if (Plugin.StopWhenClimbing.Value && d.isClimbingAnything) stopReason = "grabbed something";
            else if (d.isCarried || d.carrier != null) stopReason = "being carried";
            else if (Plugin.StopWhenParachuteOpens.Value && IsParachuteOpen(c)) stopReason = "parachute open";

            bool falling = false;
            if (stopReason == null)
            {
                float vy = d.avarageVelocity.y;
                bool movingDown = vy < -Plugin.MinDownSpeed.Value;
                if (movingDown)
                {
                    bool freeFall = c.refs.movement != null && c.refs.movement.FallTime() >= Plugin.MinFallTime.Value;
                    bool ragdollFall = Plugin.TriggerOnRagdollFall.Value && d.fallSeconds > 0f;
                    falling = freeFall || ragdollFall;
                }
            }

            if (falling)
            {
                _notFallingFor = 0f;
                if (!_screaming) Begin(c);
                return;
            }

            if (!_screaming) return;

            if (stopReason != null)
            {
                Stop(stopReason);
                return;
            }

            // Still airborne but not falling fast: give it a moment before cutting the scream.
            _notFallingFor += Time.deltaTime;
            if (_notFallingFor >= Plugin.StopGraceSeconds.Value)
                Stop("no longer falling");
        }

        // ------------------------------------------------------------------ local playback

        private void Begin(Character local)
        {
            _screaming = true;

            // Keep the same sound for a quick follow-up fall so it can pick up where it stopped.
            if (_currentClip == null || !WithinResumeWindow())
                _currentClip = PickClipForNewScream();
            if (_currentClip == null) return;   // nothing has finished loading yet

            // The local character object is replaced on every scene load; rebind when that happens.
            if (_localVoice == null || !_localVoice.IsFor(local))
            {
                if (_localVoice != null) _localVoice.Dispose();
                _localVoice = ScreamVoice.Create(local, _currentClip, spatial: false);
            }

            _localVoice.SetClip(_currentClip);
            _localVoice.Play();
            _path = StartBroadcast(local);

            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"SCREAM start: {_currentClip.name} (sent via {_path})");
        }

        private void Stop(string reason)
        {
            bool wasScreaming = _screaming;
            _screaming = false;
            _notFallingFor = 0f;

            if (_localVoice != null) _localVoice.Stop();

            if (wasScreaming)
            {
                _lastStopAt = Time.unscaledTime;
                EndBroadcast();
                if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"SCREAM stop ({reason})");
            }
        }

        // ------------------------------------------------------------------ outgoing

        private BroadcastPath StartBroadcast(Character local)
        {
            switch (Plugin.Broadcast.Value)
            {
                case BroadcastMode.Off:
                    return BroadcastPath.None;

                case BroadcastMode.VoiceChat:
                    VoiceInjector injector = VoiceInjector.Current;
                    if (injector != null && injector.Ready)
                    {
                        float[] buffer = VoiceBufferFor(_currentClip, injector);
                        if (buffer != null)
                        {
                            // A no-op when the buffer is unchanged, which keeps the mixer's resume point.
                            VoiceScreamMixer.SetSamples(buffer, injector.SamplingRate, injector.Channels);
                            if (injector.BeginScream()) return BroadcastPath.VoiceChat;
                        }
                    }

                    if (!_warnedVoiceFallback)
                    {
                        _warnedVoiceFallback = true;
                        Plugin.Log.LogWarning("Voice chat is not available (no microphone, voice not connected yet, " +
                            "or the stream format is unsupported). Falling back to ModNetwork for now.");
                    }
                    goto case BroadcastMode.ModNetwork;

                case BroadcastMode.ModNetwork:
                    if (!CanBroadcast()) return BroadcastPath.None;
                    Broadcast(local, start: true);
                    return BroadcastPath.ModNetwork;
            }
            return BroadcastPath.None;
        }

        private void EndBroadcast()
        {
            switch (_path)
            {
                case BroadcastPath.VoiceChat:
                    VoiceInjector.Current?.EndScream(Plugin.StopFadeSeconds.Value);
                    break;
                case BroadcastPath.ModNetwork:
                    Broadcast(Character.localCharacter, start: false);
                    break;
            }
            _path = BroadcastPath.None;
        }

        /// A clip converted to the voice stream's format, from the cache or converted right now.
        private float[] VoiceBufferFor(AudioClip clip, VoiceInjector injector)
        {
            if (clip == null) return null;
            SyncVoiceBufferFormat(injector);
            if (_voiceBuffers.TryGetValue(clip, out float[] cached)) return cached;
            return ConvertForVoice(clip, injector);
        }

        private void SyncVoiceBufferFormat(VoiceInjector injector)
        {
            if (_voiceBufferRate == injector.SamplingRate && _voiceBufferChannels == injector.Channels) return;
            _voiceBuffers.Clear();
            _voiceBufferRate = injector.SamplingRate;
            _voiceBufferChannels = injector.Channels;
        }

        private float[] ConvertForVoice(AudioClip clip, VoiceInjector injector)
        {
            float[] samples = VoiceScreamMixer.ConvertClip(clip, injector.SamplingRate, injector.Channels);
            if (samples == null)
            {
                Plugin.Log.LogWarning($"Could not read the samples of '{clip.name}' for voice chat.");
                return null;
            }

            _voiceBuffers[clip] = samples;
            if (Plugin.DebugLog.Value)
                Plugin.Log.LogInfo($"Converted '{clip.name}' for voice chat: {injector.SamplingRate} Hz, {injector.Channels} ch.");
            return samples;
        }

        /// Keeps the voice injector attached to the local player's Recorder, and converts the sounds
        /// the next scream may need ahead of time so it goes out with no delay.
        private void MaintainVoiceInjection(Character local)
        {
            if (Plugin.Broadcast.Value != BroadcastMode.VoiceChat) return;

            CharacterVoiceHandler handler = local.refs.voice;
            if (handler == null) return;

            VoiceInjector injector = VoiceInjector.Current;
            if (injector == null || injector.gameObject != handler.gameObject)
            {
                if (handler.GetComponent<Photon.Voice.Unity.Recorder>() == null) return;
                // Explicit Unity null checks: the ?? operator bypasses Unity's destroyed-object semantics.
                injector = handler.GetComponent<VoiceInjector>();
                if (injector == null) injector = handler.gameObject.AddComponent<VoiceInjector>();
            }

            if (!injector.Ready) return;
            SyncVoiceBufferFormat(injector);

            // At most one conversion per frame, and only for the sounds that can come up next:
            // the current one (a resume) and the upcoming one. Anything else is dropped to save memory.
            AudioClip upcoming = UpcomingClip();
            if (_currentClip != null && !_voiceBuffers.ContainsKey(_currentClip))
                ConvertForVoice(_currentClip, injector);
            else if (upcoming != null && !_voiceBuffers.ContainsKey(upcoming))
                ConvertForVoice(upcoming, injector);

            if (_voiceBuffers.Count > 2)
            {
                var stale = new List<AudioClip>();
                foreach (AudioClip clip in _voiceBuffers.Keys)
                    if (clip != _currentClip && clip != upcoming) stale.Add(clip);
                foreach (AudioClip clip in stale) _voiceBuffers.Remove(clip);
            }
        }

        // ------------------------------------------------------------------ mod network

        private static bool CanBroadcast()
        {
            return PhotonNetwork.IsConnected && PhotonNetwork.InRoom;
        }

        private void Broadcast(Character local, bool start)
        {
            if (!CanBroadcast() || local == null || local.photonView == null) return;

            // The sound name rides along so listeners who have it play the same sound. Older versions
            // only read the first two fields and ignore it.
            var options = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
            PhotonNetwork.RaiseEvent(
                (byte)Plugin.NetworkEventCode.Value,
                new object[] { local.photonView.ViewID, start, _currentClip != null ? _currentClip.name : "" },
                options,
                SendOptions.SendReliable);
        }

        public void OnEvent(EventData photonEvent)
        {
            if (photonEvent.Code != (byte)Plugin.NetworkEventCode.Value) return;
            if (!Plugin.HearOthers.Value) return;

            if (!(photonEvent.CustomData is object[] payload) || payload.Length < 2) return;
            if (!(payload[0] is int viewId) || !(payload[1] is bool start)) return;
            string soundName = payload.Length > 2 ? payload[2] as string : null;

            if (!Character.GetCharacterWithPhotonID(viewId, out Character character) || character == null)
                return;

            if (character.IsLocal) return;   // our own echo, already handled locally

            if (start) StartRemote(viewId, character, soundName);
            else StopRemote(viewId);
        }

        private void StartRemote(int viewId, Character character, string soundName)
        {
            // Their sound if we have it too, otherwise whatever we would scream ourselves.
            AudioClip clip = SoundLibrary.Get(soundName)
                             ?? (RandomMode ? SoundLibrary.PickRandom(null) : SoundLibrary.Get(Plugin.Sound.Value))
                             ?? SoundLibrary.Fallback;
            if (clip == null) return;

            if (!_remoteVoices.TryGetValue(viewId, out ScreamVoice voice) || voice == null || !voice.IsFor(character))
            {
                if (voice != null) voice.Dispose();
                voice = ScreamVoice.Create(character, clip, spatial: true);
                _remoteVoices[viewId] = voice;
            }

            voice.SetClip(clip);
            voice.Play();
            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"SCREAM start (remote {character.characterName}, {clip.name})");
        }

        private void StopRemote(int viewId)
        {
            if (!_remoteVoices.TryGetValue(viewId, out ScreamVoice voice)) return;

            if (voice != null) voice.Stop();
            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"SCREAM stop (remote {viewId})");
        }

        private void StopAllVoices()
        {
            if (_localVoice != null) _localVoice.Stop();

            foreach (ScreamVoice voice in _remoteVoices.Values)
                if (voice != null) voice.Dispose();

            _remoteVoices.Clear();
        }
    }
}
