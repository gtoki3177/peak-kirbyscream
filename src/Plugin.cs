using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace KirbyScream
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "toiletking.peak.kirbyscream";
        public const string NAME = "KirbyScream";
        public const string VERSION = "1.1.1";

        internal static ManualLogSource Log;
        internal static string PluginDir;

        // --- Audio ---
        internal static ConfigEntry<string> AudioFile;
        internal static ConfigEntry<float> Volume;
        internal static ConfigEntry<bool> Loop;
        internal static ConfigEntry<bool> UseGameSfxMixer;

        // --- Trigger ---
        internal static ConfigEntry<float> MinFallTime;
        internal static ConfigEntry<float> MinDownSpeed;
        internal static ConfigEntry<bool> TriggerOnRagdollFall;

        // --- Multiplayer ---
        internal static ConfigEntry<bool> ShareWithOthers;
        internal static ConfigEntry<bool> HearOthers;
        internal static ConfigEntry<float> RemoteVolume;
        internal static ConfigEntry<float> MaxHearingDistance;
        internal static ConfigEntry<float> FalloffNearDistance;
        internal static ConfigEntry<float> DopplerLevel;
        internal static ConfigEntry<int> NetworkEventCode;

        // --- Stop ---
        internal static ConfigEntry<float> StopFadeSeconds;
        internal static ConfigEntry<float> StopGraceSeconds;
        internal static ConfigEntry<bool> StopWhenParachuteOpens;
        internal static ConfigEntry<bool> StopWhenClimbing;

        // --- Misc ---
        internal static ConfigEntry<bool> DebugLog;

        private void Awake()
        {
            Log = Logger;
            PluginDir = Path.GetDirectoryName(Info.Location);

            AudioFile = Config.Bind("Audio", "AudioFile", "",
                "File name of the scream (relative to the mod folder, or an absolute path). " +
                "Leave empty to use the first .ogg / .wav / .mp3 found next to the plugin dll.");
            Volume = Config.Bind("Audio", "Volume", 0.6f,
                new ConfigDescription("Playback volume.", new AcceptableValueRange<float>(0f, 1f)));
            Loop = Config.Bind("Audio", "Loop", true,
                "Keep looping the clip until you land or die. If false it plays once per fall.");
            UseGameSfxMixer = Config.Bind("Audio", "UseGameSfxMixer", true,
                "Route through the game's SFX mixer so the in-game SFX volume slider applies.");

            MinFallTime = Config.Bind("Trigger", "MinFallTime", 0.3f,
                new ConfigDescription(
                    "Seconds of free fall (the game's own FallTime, which already discounts jumps and bounces) before screaming. " +
                    "The game uses 1.5 for fall damage; lower = earlier scream but may fire on big jumps.",
                    new AcceptableValueRange<float>(0f, 3f)));
            MinDownSpeed = Config.Bind("Trigger", "MinDownSpeed", 5f,
                new ConfigDescription("Minimum downward speed (m/s) required to scream. Max fall gravity is 20 m/s^2.",
                    new AcceptableValueRange<float>(0f, 30f)));
            TriggerOnRagdollFall = Config.Bind("Trigger", "TriggerOnRagdollFall", true,
                "Also scream immediately when the game puts you into a ragdoll fall (tripped, thrown, knocked off), " +
                "as long as you are moving downward.");

            ShareWithOthers = Config.Bind("Multiplayer", "ShareWithOthers", true,
                "Tell other players when you start and stop screaming, so they hear it coming from you. " +
                "Only players who also have this mod installed will hear anything.");
            HearOthers = Config.Bind("Multiplayer", "HearOthers", true,
                "Play other players' screams, positioned on them like proximity voice chat.");
            RemoteVolume = Config.Bind("Multiplayer", "RemoteVolume", 0.8f,
                new ConfigDescription("Volume of other players' screams before distance falloff.",
                    new AcceptableValueRange<float>(0f, 1f)));
            MaxHearingDistance = Config.Bind("Multiplayer", "MaxHearingDistance", 1000f,
                new ConfigDescription(
                    "Far end of the falloff curve, matching the game's voice chat. Screams fade to silence " +
                    "around a fifth of this distance, so 1000 means audible out to roughly 200 m.",
                    new AcceptableValueRange<float>(50f, 2000f)));
            FalloffNearDistance = Config.Bind("Multiplayer", "FalloffNearDistance", 10f,
                new ConfigDescription("Inside this distance a scream plays at full volume.",
                    new AcceptableValueRange<float>(1f, 100f)));
            DopplerLevel = Config.Bind("Multiplayer", "DopplerLevel", 0f,
                new ConfigDescription("Pitch shift from the falling player's speed. 0 = off, 1 = full Doppler.",
                    new AcceptableValueRange<float>(0f, 1f)));
            NetworkEventCode = Config.Bind("Multiplayer", "NetworkEventCode", 177,
                new ConfigDescription(
                    "Photon event code used to sync screams. Only change it if it clashes with another mod, " +
                    "and make sure everyone in the lobby uses the same number.",
                    new AcceptableValueRange<int>(1, 199)));

            StopFadeSeconds = Config.Bind("Stop", "StopFadeSeconds", 0.05f,
                new ConfigDescription("Fade-out length when the scream stops. 0 = hard cut on impact.",
                    new AcceptableValueRange<float>(0f, 2f)));
            StopGraceSeconds = Config.Bind("Stop", "StopGraceSeconds", 0.35f,
                new ConfigDescription(
                    "If you are still airborne but no longer falling fast (updraft, bounce pad, balloon...), " +
                    "wait this long before stopping so the scream does not stutter.",
                    new AcceptableValueRange<float>(0f, 2f)));
            StopWhenParachuteOpens = Config.Bind("Stop", "StopWhenParachuteOpens", true,
                "Stop screaming once the parachute deploys.");
            StopWhenClimbing = Config.Bind("Stop", "StopWhenClimbing", true,
                "Stop screaming if you grab a wall, rope or vine mid-fall.");

            DebugLog = Config.Bind("Misc", "DebugLog", false,
                "Log every scream start/stop with the reason to the BepInEx console.");

            var go = new GameObject("KirbyScreamController");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<FallScreamController>();

            Log.LogInfo($"{NAME} {VERSION} loaded.");
        }
    }
}
