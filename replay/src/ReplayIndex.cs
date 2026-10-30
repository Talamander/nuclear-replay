#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NuclearReplay.Playback
{
    // What the Replays list knows about each recording, kept on disk (<game>/NuclearReplay/replay-list-cache.txt) so
    // the next time the list opens it needs no recording read again. An entry is valid only while the file's path,
    // size and last write time are the ones it was made from; any change and the recording is read again. A missing
    // or damaged cache file is simply rebuilt. Plain text, one recording per line, tab-separated, so no JSON library
    // is needed and the tests can link this file.
    internal sealed class ReplayIndex
    {
        public const string FileName = "replay-list-cache.txt";
        public const string Magic = "nuclear-replay list cache 1";

        internal sealed class Entry
        {
            public string Path;
            public long Size;
            public long ModifiedTicks;    // UTC
            public long? CreatedTicks;    // UTC, from the header (or the file name)
            public string Mission, Map, Error;
            public double Duration;
            public List<string> Players = new List<string>();
            public int Kills, Launches;
            public bool Truncated;
        }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        public bool Dirty { get; private set; }
        public int Count => entries.Count;
        public IEnumerable<string> Paths => entries.Keys;

        // The entry for this file, or null when there is none or the file changed since (size or write time).
        public Entry Find(string path, long size, long modifiedTicks) =>
            path != null && entries.TryGetValue(path, out var e) && e.Size == size && e.ModifiedTicks == modifiedTicks ? e : null;

        public void Put(Entry entry)
        {
            if (entry?.Path == null) return;
            entries[entry.Path] = entry;
            Dirty = true;
        }

        public bool Remove(string path)
        {
            if (path == null || !entries.Remove(path)) return false;
            Dirty = true;
            return true;
        }

        // Drops entries `keep` rejects (recordings that are gone). Returns how many went.
        public int Prune(Func<string, bool> keep)
        {
            var gone = entries.Keys.Where(p => !keep(p)).ToList();
            foreach (var p in gone) entries.Remove(p);
            if (gone.Count > 0) Dirty = true;
            return gone.Count;
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append(Magic).Append('\n');
            var c = CultureInfo.InvariantCulture;
            foreach (var e in entries.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(Escape(e.Path)).Append('\t')
                  .Append(e.Size.ToString(c)).Append('\t')
                  .Append(e.ModifiedTicks.ToString(c)).Append('\t')
                  .Append(e.CreatedTicks.HasValue ? e.CreatedTicks.Value.ToString(c) : "-").Append('\t')
                  .Append(e.Duration.ToString("R", c)).Append('\t')
                  .Append(e.Kills.ToString(c)).Append('\t')
                  .Append(e.Launches.ToString(c)).Append('\t')
                  .Append(e.Truncated ? '1' : '0').Append('\t')
                  .Append(Escape(e.Mission)).Append('\t')
                  .Append(Escape(e.Map)).Append('\t')
                  .Append(string.Join("\u001f", e.Players.Select(Escape))).Append('\t')
                  .Append(Escape(e.Error)).Append('\n');
            }
            return sb.ToString();
        }

        // Never throws: a file from another version, or a damaged line, gives an empty index or skips the line.
        public static ReplayIndex Parse(string text)
        {
            var index = new ReplayIndex();
            if (string.IsNullOrEmpty(text)) return index;
            var lines = text.Split('\n');
            if (lines[0].TrimEnd('\r') != Magic) return index;
            var c = CultureInfo.InvariantCulture;
            for (int i = 1; i < lines.Length; i++)
            {
                var f = lines[i].TrimEnd('\r').Split('\t');
                if (f.Length != 12) continue;
                try
                {
                    var e = new Entry
                    {
                        Path = Unescape(f[0]),
                        Size = long.Parse(f[1], NumberStyles.Integer, c),
                        ModifiedTicks = long.Parse(f[2], NumberStyles.Integer, c),
                        CreatedTicks = f[3] == "-" ? (long?)null : long.Parse(f[3], NumberStyles.Integer, c),
                        Duration = double.Parse(f[4], NumberStyles.Float, c),
                        Kills = int.Parse(f[5], NumberStyles.Integer, c),
                        Launches = int.Parse(f[6], NumberStyles.Integer, c),
                        Truncated = f[7] == "1",
                        Mission = Unescape(f[8]),
                        Map = Unescape(f[9]),
                        Players = f[10].Length == 0 ? new List<string>() : f[10].Split('\u001f').Select(Unescape).ToList(),
                        Error = Unescape(f[11]),
                    };
                    if (string.IsNullOrEmpty(e.Path) || e.Size < 0) continue;
                    index.entries[e.Path] = e;
                }
                catch (FormatException) { }
                catch (OverflowException) { }
            }
            return index;
        }

        public static ReplayIndex Load(string file)
        {
            try { return File.Exists(file) ? Parse(File.ReadAllText(file, Encoding.UTF8)) : new ReplayIndex(); }
            catch (Exception) { return new ReplayIndex(); }
        }

        // Writes the whole index (a temporary file, then a swap). Returns null, or why it could not.
        public string Save(string file)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                string temp = file + ".tmp";
                File.WriteAllText(temp, Serialize(), new UTF8Encoding(false));
                if (File.Exists(file)) File.Delete(file);
                File.Move(temp, file);
                Dirty = false;
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        // Empty strings and null both come back as null (no mission name, no map, no error).
        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\u001f': sb.Append("\\u"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        internal static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch != '\\' || i + 1 >= s.Length) { sb.Append(ch); continue; }
                char n = s[++i];
                sb.Append(n == 't' ? '\t' : n == 'n' ? '\n' : n == 'r' ? '\r' : n == 'u' ? '\u001f' : n);
            }
            return sb.ToString();
        }
    }
}
