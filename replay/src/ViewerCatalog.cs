using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using NuclearReplay.Demo;
using Newtonsoft.Json.Linq;

namespace NuclearReplay.Playback
{
    // One event annotation of a recording (launch, kill, player, mission, ...), on the replay clock
    // (record time minus the first record's time), the same clock nr, shot lists and render ranges use.
    internal sealed class ViewerEvent
    {
        public double T;
        public string Event;
        public JObject Data;

        public bool IsMoment => Event == "launch" || Event == "kill";
        public JObject Shooter => Data["shooter"] as JObject;
        public JObject Target => Data["target"] as JObject;
        public JObject Killer => Data["killer"] as JObject;
        public JObject Victim => Data["victim"] as JObject;
        public long? Missile => Id(Data["missile"]);

        public JObject Subject => Event == "launch" ? Shooter : Event == "kill" ? Killer ?? Victim : null;

        public static long? Id(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token is JObject o) return Id(o["id"]);
            try { return (long)token; } catch (Exception) { return null; }
        }

        public static string ShotTarget(JObject unit) => Id(unit) is long id ? "unit:" + id.ToString(CultureInfo.InvariantCulture) : null;

        public static string Label(JObject unit)
        {
            if (unit == null) return null;
            string player = (string)unit["player"], name = (string)unit["name"];
            if (!string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(name))
                return name.Contains(player) ? name : player + " (" + name + ")";
            if (!string.IsNullOrEmpty(player)) return player;
            if (!string.IsNullOrEmpty(name)) return name;
            return Id(unit) is long id ? "unit " + id : "unknown";
        }

        // Plain one-liner for the timeline and the overlay, the same wording as nr events.
        public string Summary
        {
            get
            {
                string weapon = (string)Data["weapon"];
                if (Event == "launch")
                    return (Label(Shooter) ?? "Someone") + " fired " + (weapon ?? "a weapon") + (Target != null ? " at " + Label(Target) : "");
                if (Event == "kill")
                    return Killer != null
                        ? Label(Killer) + " destroyed " + (Label(Victim) ?? "a unit") + (weapon != null ? " with " + weapon : "")
                        : (Label(Victim) ?? "A unit") + " was destroyed";
                return Event;
            }
        }
    }

    // What the Replays menu shows about one recording.
    internal sealed class ViewerDemo
    {
        public string Path;
        public string FileName;
        public long Size;
        public DateTime Modified;
        public DateTime? Created;
        public string Mission;
        public string Map;
        public double Duration;
        public List<string> Players = new List<string>();
        public int Kills, Launches;
        public bool Truncated;
        public string Error;
        public List<ViewerEvent> Events = new List<ViewerEvent>();

        public string Title => Mission ?? System.IO.Path.GetFileNameWithoutExtension(FileName);
        public IEnumerable<ViewerEvent> Moments => Events.Where(e => e.IsMoment);
    }

    // Finds and reads recordings for the in-game Replays menu. Reading skips message payloads (seek), so a
    // 150 MB match is scanned in well under a second; results are cached by path, size and write time.
    internal sealed class ViewerCatalog
    {
        private readonly ManualLogSource log;
        private readonly Dictionary<string, ViewerDemo> cache = new Dictionary<string, ViewerDemo>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        private volatile bool scanning;
        private List<ViewerDemo> latest = new List<ViewerDemo>();

        private readonly List<string> directories = new List<string>();
        public bool Scanning => scanning;
        public List<ViewerDemo> Demos { get { lock (gate) return latest; } }
        public int Version { get; private set; }

        public ViewerCatalog(ManualLogSource log) { this.log = log; }

        // Adds a folder to scan. Empty paths and folders already in the list are ignored.
        public void AddDirectory(string dir)
        {
            if (!string.IsNullOrEmpty(dir) && !directories.Contains(dir, StringComparer.OrdinalIgnoreCase)) directories.Add(dir);
        }

        // Starts a background rescan unless one is running. Demos updates when it finishes.
        public void Refresh()
        {
            if (scanning) return;
            scanning = true;
            var dirs = directories.ToList();
            new Thread(() =>
            {
                try
                {
                    var found = new List<ViewerDemo>();
                    foreach (var dir in dirs)
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var file in Directory.GetFiles(dir, "*" + DemoFormat.Extension))
                            found.Add(Get(file));
                    }
                    // The same folder can be reached by two paths (the game's S: drive and Z:), so drop repeats.
                    found = found.GroupBy(d => d.FileName + "|" + d.Size + "|" + d.Modified.Ticks, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                    found.Sort((a, b) => (b.Created ?? b.Modified).CompareTo(a.Created ?? a.Modified));
                    lock (gate) { latest = found; Version++; }
                }
                catch (Exception e) { log.LogWarning("Replays menu: scan failed: " + e.Message); }
                finally { scanning = false; }
            }) { IsBackground = true, Name = "nc-viewer-catalog" }.Start();
        }

        // Reads one recording (cached). Never throws; a broken file comes back with Error set.
        public ViewerDemo Get(string path)
        {
            var info = new FileInfo(path);
            lock (gate)
                if (cache.TryGetValue(path, out var hit) && hit.Size == info.Length && hit.Modified == info.LastWriteTimeUtc) return hit;
            ViewerDemo demo;
            try { demo = Read(path); }
            catch (Exception e)
            {
                demo = new ViewerDemo { Path = path, FileName = System.IO.Path.GetFileName(path), Error = e.Message };
            }
            demo.Size = info.Exists ? info.Length : 0;
            demo.Modified = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            lock (gate) cache[path] = demo;
            return demo;
        }

        public static ViewerDemo Read(string path)
        {
            var demo = new ViewerDemo { Path = path, FileName = System.IO.Path.GetFileName(path) };
            JObject header;
            var annotations = new List<KeyValuePair<double, string>>();
            double? first = null, last = null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                var input = new BinaryReader(stream);
                var magic = input.ReadBytes(DemoFormat.Magic.Length);
                for (int i = 0; i < DemoFormat.Magic.Length - 1; i++)
                    if (magic.Length != DemoFormat.Magic.Length || magic[i] != DemoFormat.Magic[i]) throw new InvalidDataException("not a Nuclear Replay demo");
                int headerLength = input.ReadInt32();
                if (headerLength < 0 || headerLength > 16 << 20) throw new InvalidDataException("damaged header");
                header = JObject.Parse(Encoding.UTF8.GetString(input.ReadBytes(headerLength)));
                long length = stream.Length;
                while (true)
                {
                    var head = input.ReadBytes(DemoFormat.RecordHeaderBytes);
                    if (head.Length == 0) break;
                    if (head.Length < DemoFormat.RecordHeaderBytes) { demo.Truncated = true; break; }
                    double time = BitConverter.ToDouble(head, 1);
                    int payload = BitConverter.ToInt32(head, 18);
                    if (payload < 0 || stream.Position + payload > length) { demo.Truncated = true; break; }
                    if (head[0] == DemoFormat.KindAnnotation) annotations.Add(new KeyValuePair<double, string>(time, Encoding.UTF8.GetString(input.ReadBytes(payload))));
                    else stream.Seek(payload, SeekOrigin.Current);
                    if (first == null) first = time;
                    last = time;
                }
            }
            double origin = first ?? 0;
            demo.Duration = first.HasValue ? last.Value - first.Value : 0;
            foreach (var a in annotations)
            {
                try
                {
                    if (!(JToken.Parse(a.Value) is JObject o) || string.IsNullOrEmpty((string)o["event"])) continue;
                    demo.Events.Add(new ViewerEvent { T = Math.Max(0, a.Key - origin), Event = (string)o["event"], Data = o });
                }
                catch (Exception) { }
            }
            var mission = demo.Events.FirstOrDefault(e => e.Event == "mission");
            demo.Mission = (string)mission?.Data["name"] ?? NonEmpty((string)header["mission_name"]);
            string map = (string)mission?.Data["map"];
            if (IsGeneric(map))
            {
                var keyPath = (string)header["map_key_path"];
                map = string.IsNullOrEmpty(keyPath) ? null : System.IO.Path.GetFileNameWithoutExtension(keyPath);
            }
            demo.Map = IsGeneric(map) ? null : map;
            if (DateTime.TryParse((string)header["created_utc"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
                demo.Created = created;
            else if (demo.FileName.Length >= 16 && DateTime.TryParseExact(demo.FileName.Substring(0, 16), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp))
                demo.Created = stamp;
            foreach (var e in demo.Events.Where(x => x.Event == "player"))
            {
                string name = (string)e.Data["name"] ?? (string)(e.Data["unit"] as JObject)?["player"];
                if (!string.IsNullOrEmpty(name) && !demo.Players.Contains(name)) demo.Players.Add(name);
            }
            demo.Kills = demo.Events.Count(e => e.Event == "kill");
            demo.Launches = demo.Events.Count(e => e.Event == "launch");
            return demo;
        }

        private static string NonEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static bool IsGeneric(string map) => string.IsNullOrWhiteSpace(map) || map.Equals("GameWorld", StringComparison.OrdinalIgnoreCase);

        public static string Clock(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int s = (int)Math.Floor(seconds);
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:D2}:{s % 60:D2}" : $"{s / 60}:{s % 60:D2}";
        }
    }
}
