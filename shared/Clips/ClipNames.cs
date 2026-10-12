#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NuclearReplay.Shared
{
    // File names of clips, the same in nr and the in-game viewer: "<mission> m-ss to m-ss.mp4" in whole replay
    // seconds ("05. Furball 0-14 to 0-22.mp4", "Escalation 1-02-05 to 1-02-16.mp4" past an hour), with " (2)", " (3)", ...
    // when that name is taken. Compiled into the replay plugin (netstandard2.1, the game's Mono) and into
    // NuclearReplay.Core. Existing files are never renamed or overwritten.
    internal static class ClipNames
    {
        // Characters Windows does not allow in file names, also on Linux (the game runs under Proton).
        private static readonly HashSet<char> Invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }));

        public static string Sanitize(string name)
        {
            var clean = new string((name ?? "").Select(c => Invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
            return clean.Length == 0 ? "clip" : clean;
        }

        // Replay seconds as a clock for a file name, rounded to whole seconds: 14.3 -> "0-14", 3725 -> "1-02-05".
        public static string Clock(double seconds)
        {
            long s = (long)Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero);
            return s >= 3600 ? $"{s / 3600}-{s / 60 % 60:D2}-{s % 60:D2}" : $"{s / 60}-{s % 60:D2}";
        }

        // "<title> m-ss to m-ss", without the extension.
        public static string Stem(string title, double from, double to) => Sanitize(title) + " " + Clock(from) + " to " + Clock(to);

        // <dir>/<stem>.mp4, or "<stem> (2).mp4" and up when that file exists or `taken` says the path is already used.
        public static string UniquePath(string dir, string stem, Func<string, bool> taken = null)
        {
            string path = Path.Combine(dir, stem + ".mp4");
            for (int i = 2; File.Exists(path) || taken != null && taken(path); i++) path = Path.Combine(dir, $"{stem} ({i}).mp4");
            return path;
        }
    }
}
