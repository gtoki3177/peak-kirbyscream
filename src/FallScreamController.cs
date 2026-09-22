using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace KirbyScream
{
    /// <summary>
    /// Watches the local player every frame. Starts the scream when a real fall begins and
    /// stops it the moment the player lands, dies, grabs something or opens a parachute.
    /// </summary>
    public class FallScreamController : MonoBehaviour
    {
        private static readonly string[] AudioExtensions = { ".ogg", ".wav", ".mp3" };

        // CharacterBalloons._isParachuteOpen is private; read it through Harmony's field accessor.
        private static readonly AccessTools.FieldRef<CharacterBalloons, bool> ParachuteOpenField = MakeParachuteRef();

        private AudioSource _source;
        private AudioClip _clip;
        private Coroutine _fade;

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
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;   // 2D: it's the local player's own scream
            _source.priority = 0;
            StartCoroutine(LoadClip());
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
                _source.clip = clip;
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
                if (!_screaming) Begin();
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

        // ------------------------------------------------------------------ playback

        private void Begin()
        {
            _screaming = true;
            if (_clip == null) return;

            if (_fade != null) { StopCoroutine(_fade); _fade = null; }

            if (Plugin.UseGameSfxMixer.Value && SFX_Player.instance != null && SFX_Player.instance.defaultMixerGroup != null)
                _source.outputAudioMixerGroup = SFX_Player.instance.defaultMixerGroup;
            else
                _source.outputAudioMixerGroup = null;

            _source.loop = Plugin.Loop.Value;
            _source.volume = Plugin.Volume.Value;
            _source.time = 0f;
            _source.Play();

            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo("SCREAM start");
        }

        private void Stop(string reason)
        {
            _screaming = false;
            _notFallingFor = 0f;
            if (_clip == null || !_source.isPlaying) return;

            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"SCREAM stop ({reason})");

            float fade = Plugin.StopFadeSeconds.Value;
            if (fade <= 0f)
            {
                _source.Stop();
                return;
            }

            if (_fade != null) StopCoroutine(_fade);
            _fade = StartCoroutine(FadeOut(fade));
        }

        private IEnumerator FadeOut(float seconds)
        {
            float start = _source.volume;
            float t = 0f;
            while (t < seconds && _source.isPlaying)
            {
                t += Time.unscaledDeltaTime;
                _source.volume = Mathf.Lerp(start, 0f, t / seconds);
                yield return null;
            }
            _source.Stop();
            _source.volume = start;
            _fade = null;
        }
    }
}
