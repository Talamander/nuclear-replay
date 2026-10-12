#nullable disable
using System;
using System.Collections.Generic;
using System.IO;

namespace NuclearReplay.Shared
{
    // The mod's data folder in the game folder: <game>/NuclearReplay (demos, clips, viewer-clips, renders, tools/ffmpeg).
    //
    // Before the rename the mod was called Nuclear Context and wrote to <game>/NuclearContext. That folder is never moved
    // or deleted (it can hold over 100 GB of recordings, and other tools read it). New files go only to the new folder;
    // readers also look in the old one: the recordings list (nr and the in-game Replays menu) and the ffmpeg lookup.
    internal static class ModFolders
    {
        public const string Data = "NuclearReplay";
        public const string LegacyData = "NuclearContext";

        public static string DataDir(string gameDir) => Path.Combine(gameDir, Data);
        public static string LegacyDataDir(string gameDir) => Path.Combine(gameDir, LegacyData);

        // <game>/NuclearReplay/demos, the default recording folder.
        public static string Demos(string gameDir) => Path.Combine(DataDir(gameDir), "demos");

        // <game>/NuclearContext/demos, read when it exists.
        public static string LegacyDemos(string gameDir) => Path.Combine(LegacyDataDir(gameDir), "demos");

        // A configured path equal to an old default (<game>/NuclearContext/<parts>), the way an older version wrote it into
        // its config file, maps to the same place under the new folder. Returns null for any other path.
        public static string ReplaceLegacyDefault(string value, string gameDir, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var legacy = new List<string> { LegacyDataDir(gameDir) };
            legacy.AddRange(parts);
            if (!SamePath(value, Path.Combine(legacy.ToArray()))) return null;
            var current = new List<string> { DataDir(gameDir) };
            current.AddRange(parts);
            return Path.Combine(current.ToArray());
        }

        // Compares two paths as strings: separators and a trailing separator do not matter, case does not either (the
        // game runs on Windows or under Proton).
        public static bool SamePath(string a, string b) =>
            string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
    }
}
