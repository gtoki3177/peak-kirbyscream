using System;
using HarmonyLib;
using Photon.Voice;
using Photon.Voice.Unity;
using UnityEngine;

namespace KirbyScream
{
    /// <summary>
    /// Lives on the local player's Photon Voice Recorder. Installs the scream mixer into every voice
    /// stream the Recorder creates, and while a scream is on air it holds transmission open for
    /// push-to-talk players and pauses echo cancellation so the scream is not filtered back out.
    ///
    /// Invariant: the real microphone is never sent when the game itself would not be transmitting.
    /// Whenever we open transmission on the game's behalf, the mixer silences the mic first.
    /// </summary>
    public class VoiceInjector : MonoBehaviour
    {
        /// The injector on the local player's Recorder, or null while there is none.
        public static VoiceInjector Current { get; private set; }

        /// Read by the PushToTalk patch. True while a scream wants transmission held open.
        internal static volatile bool ForceTransmit;

        private static AccessTools.FieldRef<Recorder, LocalVoice> _recorderVoice;
        private static bool _reflectionReady;

        private Recorder _recorder;
        private CharacterVoiceHandler _handler;
        private WebRtcAudioDsp _dsp;
        private LocalVoice _installedOn;

        private bool _onAir;
        private bool _pausedAec;

        public int SamplingRate { get; private set; }
        public int Channels { get; private set; }

        /// True once a real voice stream exists and the mixer is installed on it.
        public bool Ready => _installedOn != null && _recorder != null && _recorder.isActiveAndEnabled;

        private static void InitReflection()
        {
            if (_reflectionReady) return;
            _reflectionReady = true;
            try { _recorderVoice = AccessTools.FieldRefAccess<Recorder, LocalVoice>("voice"); }
            catch (Exception e) { Plugin.Log.LogWarning("Could not bind Recorder.voice; will wait for PhotonVoiceCreated instead. " + e.Message); }
        }

        private void Awake()
        {
            InitReflection();
            _recorder = GetComponent<Recorder>();
            _handler = GetComponent<CharacterVoiceHandler>();
            _dsp = GetComponent<WebRtcAudioDsp>();
            Current = this;

            if (Plugin.DebugLog.Value)
            {
                string dsp = _dsp == null
                    ? "none"
                    : $"bypass={_dsp.Bypass} aec={_dsp.AEC} ns={_dsp.NoiseSuppression} agc={_dsp.AGC} vad={_dsp.VAD}";
                Plugin.Log.LogInfo($"Voice injector attached. WebRTC DSP: {dsp}");
            }
        }

        private void Start()
        {
            // The Recorder may have created its voice before we were attached.
            if (_recorderVoice != null && _recorder != null)
                Install(_recorderVoice(_recorder));
        }

        private void OnDestroy()
        {
            EndScream(0f);
            if (Current == this) Current = null;
        }

        // Sent by Recorder via SendMessage whenever it (re)creates its outgoing stream.
        private void PhotonVoiceCreated(PhotonVoiceCreatedParams p) => Install(p?.Voice);

        private void PhotonVoiceRemoved()
        {
            _installedOn = null;
        }

        private void Install(LocalVoice voice)
        {
            if (voice == null || ReferenceEquals(voice, _installedOn)) return;

            if (voice is LocalVoiceAudioFloat f) f.AddPreProcessor(new FloatScreamProcessor());
            else if (voice is LocalVoiceAudioShort s) s.AddPreProcessor(new ShortScreamProcessor());
            else return;   // the placeholder dummy before a real stream exists, or an unknown format

            _installedOn = voice;
            SamplingRate = voice.Info.SamplingRate;
            Channels = voice.Info.Channels;
            Plugin.Log.LogInfo($"Scream mixer installed on voice stream ({voice.GetType().Name}, {SamplingRate} Hz, {Channels} ch).");
        }

        // ------------------------------------------------------------------ scream control

        public bool BeginScream()
        {
            if (!Ready || !VoiceScreamMixer.HasSamplesFor(SamplingRate, Channels)) return false;

            if (Plugin.PauseEchoCancellation.Value && _dsp != null && !_dsp.Bypass && _dsp.AEC)
            {
                _dsp.AEC = false;
                _pausedAec = true;
            }

            VoiceScreamMixer.Start(Plugin.VoiceChatVolume.Value, Plugin.Loop.Value);
            ForceTransmit = Plugin.ForceTransmitWithPushToTalk.Value;
            _onAir = true;
            return true;
        }

        public void EndScream(float fadeSeconds)
        {
            if (!_onAir) return;
            _onAir = false;

            VoiceScreamMixer.Stop(fadeSeconds);
            ForceTransmit = false;

            // Hand transmission back to the game's own decision before letting the mic through again.
            if (_recorder != null && _handler != null)
                _recorder.TransmitEnabled = _handler.transmitting && PushToTalkPatch.AllowedToCommunicate(_handler);
            VoiceScreamMixer.MuteMic = false;

            if (_pausedAec && _dsp != null) _dsp.AEC = true;
            _pausedAec = false;
        }
    }

    /// <summary>
    /// Runs after the game decides whether the local player is transmitting this frame. While a
    /// scream is on air, keeps transmission open even if push-to-talk is not held, with the real
    /// microphone muted. The game's own "blocked / not allowed to talk" check runs after this in the
    /// same Update and still wins.
    /// </summary>
    [HarmonyPatch(typeof(CharacterVoiceHandler), "PushToTalk")]
    internal static class PushToTalkPatch
    {
        private static readonly AccessTools.FieldRef<CharacterVoiceHandler, Recorder> RecorderRef = Bind<Recorder>("m_Recorder");
        private static readonly AccessTools.FieldRef<CharacterVoiceHandler, Character> CharacterRef = Bind<Character>("m_character");
        private static readonly AccessTools.FieldRef<CharacterVoiceHandler, bool> AllowedRef = Bind<bool>("m_allowedToCommunicate");

        private static AccessTools.FieldRef<CharacterVoiceHandler, T> Bind<T>(string field)
        {
            try { return AccessTools.FieldRefAccess<CharacterVoiceHandler, T>(field); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not bind CharacterVoiceHandler.{field}; push-to-talk players will not transmit screams. {e.Message}");
                return null;
            }
        }

        internal static bool AllowedToCommunicate(CharacterVoiceHandler handler)
        {
            if (AllowedRef == null) return false;
            if (!AllowedRef(handler)) return false;
            Character c = CharacterRef?.Invoke(handler);
            return c == null || c.data == null || !c.data.isBlocked;
        }

        private static void Postfix(CharacterVoiceHandler __instance)
        {
            if (!VoiceInjector.ForceTransmit || RecorderRef == null || CharacterRef == null) return;

            Character c = CharacterRef(__instance);
            if (c == null || !c.IsLocal || c.isBot) return;
            if (!AllowedToCommunicate(__instance)) return;

            Recorder recorder = RecorderRef(__instance);
            if (recorder == null) return;

            // Mute first, then open: never a moment where the real mic is live against the player's choice.
            VoiceScreamMixer.MuteMic = !__instance.transmitting;
            recorder.TransmitEnabled = true;
        }
    }
}
