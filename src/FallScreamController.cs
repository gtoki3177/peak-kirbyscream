using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.Networking;

namespace KirbyScream
{
    /// <summary>
    /// Watches the local player every frame. Starts the scream when a real fall begins and stops it
    /// the moment the player lands, dies, grabs something or opens a parachute.
    ///
    /// Start and stop are broadcast over Photon, so everyone else running this mod hears the scream
    /// positioned on the falling player, the same way proximity voice chat works.
    /// </summary>
    public class FallScreamController : MonoBehaviour, IOnEventCallback
    {
        private static readonly string[] AudioExtensions = { ".ogg", ".wav", ".mp3" };

        // CharacterBalloons._isParachuteOpen is private; read it through Harmony's field accessor.
        private static readonly AccessTools.FieldRef<CharacterBalloons, bool> ParachuteOpenField = MakeParachuteRef();

        private AudioClip _clip;

        private ScreamVoice _localVoice;
        private readonly Dictionary<int, ScreamVoice> _remoteVoices = new Dictionary<int, ScreamVoice>();

        private bool _screaming;
        private float _notFallingFor;

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
            StartCoroutine(LoadClip());
        }

        private void OnEnable() => PhotonNetwork.AddCallbackTarget(this);

        private void OnDisable()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
            StopAllVoices();
        }

        // ------------------------------------------------------------------ loading

        private static string ResolveAudioPath()
        {
            string configured = Plugin.AudioFile.Value?.Trim();
            if (!string.IsNullOrEmpty(configured))
            {
                string p = Path.IsPathRooted(configured) ? configured : Path.Combine(Plugin.PluginDir, configured);
                if (File.Exists(p)) return p;
                Plugin.Log.LogWarning($"AudioFile '{configured}' not found, scanning the mod folder instead.");
            }

            return Directory.GetFiles(Plugin.PluginDir)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static AudioType AudioTypeFor(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".ogg": return AudioType.OGGVORBIS;
                case ".wav": return AudioType.WAV;
                case ".mp3": return AudioType.MPEG;
                default: return AudioType.UNKNOWN;
            }
        }

        private IEnumerator LoadClip()
        {
            string path = ResolveAudioPath();
            if (path == null)
            {
                Plugin.Log.LogError($"No scream audio found. Put a .ogg/.wav/.mp3 in: {Plugin.PluginDir}");
                yield break;
            }

            string uri = new Uri(path).AbsoluteUri;
            Plugin.Log.LogInfo($"Loading scream audio: {path}");

            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioTypeFor(path)))
            {
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogError($"Failed to load '{path}': {req.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null || clip.length <= 0f)
                {
                    Plugin.Log.LogError($"'{path}' loaded but produced an empty AudioClip.");
                    yield break;
                }

                clip.name = Path.GetFileNameWithoutExtension(path);
                _clip = clip;
                Plugin.Log.LogInfo($"Scream ready: {clip.name} ({clip.length:0.00}s)");
            }
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
            if (_clip == null) return;

            if (_localVoice == null)
                _localVoice = ScreamVoice.Create(local, _clip, spatial: false);

            _localVoice.Play();
            Broadcast(local, start: true);

            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo("SCREAM start");
        }

        private void Stop(string reason)
        {
            bool wasScreaming = _screaming;
            _screaming = false;
            _notFallingFor = 0f;

            if (_localVoice != null) _localVoice.Stop();

            if (wasScreaming)
            {
                Broadcast(Character.localCharacter, start: false);
                if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"SCREAM stop ({reason})");
            }
        }

        // ------------------------------------------------------------------ networking

        private static bool CanBroadcast()
        {
            return Plugin.ShareWithOthers.Value && PhotonNetwork.IsConnected && PhotonNetwork.InRoom;
        }

        private void Broadcast(Character local, bool start)
        {
            if (!CanBroadcast() || local == null || local.photonView == null) return;

            var options = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
            PhotonNetwork.RaiseEvent(
                (byte)Plugin.NetworkEventCode.Value,
                new object[] { local.photonView.ViewID, start },
                options,
                SendOptions.SendReliable);
        }

        public void OnEvent(EventData photonEvent)
        {
            if (photonEvent.Code != (byte)Plugin.NetworkEventCode.Value) return;
            if (!Plugin.HearOthers.Value) return;

            if (!(photonEvent.CustomData is object[] payload) || payload.Length < 2) return;
            if (!(payload[0] is int viewId) || !(payload[1] is bool start)) return;

            if (!Character.GetCharacterWithPhotonID(viewId, out Character character) || character == null)
                return;

            if (character.IsLocal) return;   // our own echo, already handled locally

            if (start) StartRemote(viewId, character);
            else StopRemote(viewId);
        }

        private void StartRemote(int viewId, Character character)
        {
            if (_clip == null) return;

            if (!_remoteVoices.TryGetValue(viewId, out ScreamVoice voice) || voice == null)
            {
                voice = ScreamVoice.Create(character, _clip, spatial: true);
                _remoteVoices[viewId] = voice;
            }

            voice.Play();
            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"SCREAM start (remote {character.characterName})");
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
