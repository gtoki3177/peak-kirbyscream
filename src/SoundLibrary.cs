using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;

namespace KirbyScream
{
    /// <summary>
    /// Every scream sound the player can pick from.
    ///
    /// Presets ship inside the mod folder, under sounds/. Players add their own under
    /// BepInEx/config/KirbyScream/, which Thunderstore Mod Manager leaves alone when it updates the mod
    /// (it replaces the whole plugin folder, so anything dropped in there would be lost).
    /// A sound's name is its file name without the extension. A custom sound with the same name as a
    /// preset replaces it.
    /// </summary>
    internal static class SoundLibrary
    {
        public const string RandomChoice = "Random";
        public const string PreferredDefault = "kirby_fall";

        private static readonly string[] Extensions = { ".ogg", ".wav", ".mp3" };

        // name -> file path, and name -> loaded clip. Names compare case-insensitively.
        private static readonly SortedDictionary<string, string> Files =
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AudioClip> Clips =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> CustomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly System.Random Rng = new System.Random();

        public static string PresetDir => Path.Combine(Plugin.PluginDir, "sounds");
        public static string CustomDir => Path.Combine(Paths.ConfigPath, "KirbyScream");

        public static IReadOnlyCollection<string> Names => Files.Keys;

        public static bool IsCustom(string name) => CustomNames.Contains(name);

        /// The name the Sound setting defaults to.
        public static string DefaultName =>
            Files.ContainsKey(PreferredDefault) ? PreferredDefault : Files.Keys.FirstOrDefault() ?? "";

        // ------------------------------------------------------------------ discovery

        /// Lists sound files on disk. Synchronous and cheap, so it can run before the config is bound.
        public static void Scan()
        {
            Files.Clear();
            CustomNames.Clear();

            // Presets: sounds/ first, then loose files next to the dll (the 1.x layout and manual installs).
            AddFrom(PresetDir, custom: false);
            AddFrom(Plugin.PluginDir, custom: false);

            EnsureCustomDir();
            AddFrom(CustomDir, custom: true);
        }

        private static void AddFrom(string dir, bool custom)
        {
            if (!Directory.Exists(dir)) return;

            IEnumerable<string> found = Directory.GetFiles(dir)
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (string path in found)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (string.Equals(name, RandomChoice, StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.LogWarning($"Skipping '{path}': '{RandomChoice}' is reserved for random selection.");
                    continue;
                }

                if (Files.TryGetValue(name, out string existing))
                {
                    bool existingIsCustom = CustomNames.Contains(name);
                    if (custom && !existingIsCustom)
                    {
                        Plugin.Log.LogInfo($"Custom sound '{name}' replaces the preset of the same name.");
                    }
                    else
                    {
                        // Same name twice in one place (e.g. foo.ogg and foo.mp3) is worth flagging. A preset in
                        // both sounds/ and the old top-level spot is just a leftover and not worth the noise.
                        if (custom || Plugin.DebugLog.Value)
                            Plugin.Log.LogWarning($"Two sounds are named '{name}'; using '{existing}', ignoring '{path}'.");
                        continue;
                    }
                }

                Files[name] = path;
                if (custom) CustomNames.Add(name);
            }
        }

        private static void EnsureCustomDir()
        {
            try
            {
                Directory.CreateDirectory(CustomDir);
                string readme = Path.Combine(CustomDir, "README.txt");
                if (!File.Exists(readme))
                {
                    File.WriteAllText(readme,
                        "KirbyScream custom sounds\r\n" +
                        "=========================\r\n\r\n" +
                        "Drop .ogg, .wav or .mp3 files in this folder, then restart the game.\r\n" +
                        "Each file becomes a choice for the Sound setting, named after the file without\r\n" +
                        "its extension. For example 'wilhelm.ogg' shows up as 'wilhelm'.\r\n\r\n" +
                        "Set Sound = Random to pick a different sound for every fall.\r\n\r\n" +
                        "A file here with the same name as a built-in sound replaces it.\r\n" +
                        "This folder is kept when the mod updates.\r\n");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not prepare the custom sound folder '{CustomDir}': {e.Message}");
            }
        }

        // ------------------------------------------------------------------ loading

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

        public static IEnumerator LoadAll()
        {
            if (Files.Count == 0)
            {
                Plugin.Log.LogError($"No scream sounds found. Put a .ogg/.wav/.mp3 in: {CustomDir}");
                yield break;
            }

            foreach (KeyValuePair<string, string> entry in Files.ToList())
            {
                string name = entry.Key;
                string path = entry.Value;

                using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioTypeFor(path)))
                {
                    yield return req.SendWebRequest();

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Plugin.Log.LogError($"Failed to load sound '{name}' from '{path}': {req.error}");
                        continue;
                    }

                    AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                    if (clip == null || clip.length <= 0f)
                    {
                        Plugin.Log.LogError($"Sound '{name}' ('{path}') loaded but is empty.");
                        continue;
                    }

                    clip.name = name;
                    Clips[name] = clip;
                    Plugin.Log.LogInfo($"Sound ready: {name} ({clip.length:0.00}s, {(IsCustom(name) ? "custom" : "preset")})");
                }
            }
        }

        // ------------------------------------------------------------------ lookup

        public static AudioClip Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return Clips.TryGetValue(name, out AudioClip clip) ? clip : null;
        }

        /// The clip to use when the chosen one is missing: the preferred default, else anything loaded.
        public static AudioClip Fallback => Get(PreferredDefault) ?? Clips.Values.FirstOrDefault();

        /// A random loaded clip, avoiding <paramref name="avoid"/> when there is any other choice.
        public static AudioClip PickRandom(AudioClip avoid)
        {
            if (Clips.Count == 0) return null;
            List<AudioClip> pool = Clips.Values.Where(c => c != avoid).ToList();
            if (pool.Count == 0) return avoid;
            return pool[Rng.Next(pool.Count)];
        }
    }
}
