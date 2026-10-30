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
    // A kill with a recorded hit ("hit_ago", recorders since 0.5.1) is placed at the hit, as nr does
    // (DemoEvent.Parse); older kills stay at the kill feed time.
    internal sealed class ViewerEvent
    {
        public double T;
        // Kills only: seconds from the hit to the kill feed, or null when only the kill feed time is known.
        public double? HitAgo;
        public const double MaxHitAgo = 30;

        public static ViewerEvent Create(double replayTime, JObject data)
        {
            string name = (string)data["event"];
            var e = new ViewerEvent { T = replayTime, Event = name, Data = data };
            var token = name == "kill" ? data["hit_ago"] : null;
            if (token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer))
            {
                double ago = (double)token;
                if (!double.IsNaN(ago) && !double.IsInfinity(ago) && ago >= 0 && ago <= MaxHitAgo)
                {
                    e.HitAgo = Math.Min(ago, replayTime);
                    e.T = Math.Max(0, replayTime - ago);
                }
            }
            return e;
        }
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

    // What the Replays menu shows about one recording. A row starts from the file alone (name, size, date); the
    // rest (Detailed) comes from the list cache or a background read. Events are loaded only for the recording that
    // opens (ViewerCatalog.GetFull).
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
        // Mission, map, length, players and counts are known (written by the reader thread before this is set).
        public volatile bool Detailed;
        public bool EventsLoaded;

        public string Title => Mission ?? ViewerText.TitleFromFileName(FileName);
        public IEnumerable<ViewerEvent> Moments => Events.Where(e => e.IsMoment);
        public DateTime SortTime => Created ?? Modified;

        // Copies what the list shows from a full read (not the events).
        internal void TakeSummary(ViewerDemo from)
        {
            Mission = from.Mission; Map = from.Map; Duration = from.Duration; Players = from.Players;
            Kills = from.Kills; Launches = from.Launches; Truncated = from.Truncated; Error = from.Error;
            // The file name's time stamp, when there is one, stays: it is the same moment as the header's (the
            // recorder writes both), and keeping it means rows never jump as details fill in.
            if (!Created.HasValue && from.Created.HasValue) Created = from.Created;
            Detailed = true;
        }

        internal void TakeSummary(ReplayIndex.Entry e)
        {
            Mission = e.Mission; Map = e.Map; Duration = e.Duration; Players = e.Players ?? new List<string>();
            Kills = e.Kills; Launches = e.Launches; Truncated = e.Truncated; Error = e.Error;
            if (!Created.HasValue && e.CreatedTicks.HasValue) Created = new DateTime(e.CreatedTicks.Value, DateTimeKind.Utc);
            Detailed = true;
        }

        internal ReplayIndex.Entry ToEntry() => new ReplayIndex.Entry
        {
            Path = Path, Size = Size, ModifiedTicks = Modified.Ticks, CreatedTicks = Created?.Ticks,
            Mission = Mission, Map = Map, Duration = Duration, Players = Players.ToList(),
            Kills = Kills, Launches = Launches, Truncated = Truncated, Error = Error,
        };
    }

    // Finds and reads recordings for the in-game Replays menu, without making the player wait for a big library:
    //   1. Listing: the folders' file names, sizes and dates only. Rows show at once, newest first.
    //   2. Details: mission, map, length, players and counts come from the list cache (ReplayIndex, keyed by path,
    //      size and write time) or, for new and changed recordings, from a read on this background thread, the rows
    //      on screen first (ReadFocus). Rows fill in as each one is read; the cache is saved every few seconds.
    // Opening a recording reads its events (GetFull).
    internal sealed class ViewerCatalog
    {
        private readonly ManualLogSource log;
        private readonly string indexPath;
        private readonly object gate = new object();
        // The recordings in the list by path: kept across refreshes so details already read stay.
        private Dictionary<string, ViewerDemo> known = new Dictionary<string, ViewerDemo>(StringComparer.OrdinalIgnoreCase);
        // Fully read recordings (with events) for the viewer; only the last few.
        private readonly Dictionary<string, ViewerDemo> full = new Dictionary<string, ViewerDemo>(StringComparer.OrdinalIgnoreCase);
        private ReplayIndex index;
        private volatile bool busy, listing, again;
        private List<ViewerDemo> latest = new List<ViewerDemo>();
        private readonly List<string> directories = new List<string>();
        private int version, detailVersion, pending;

        public bool Scanning => listing;        // the folders are being listed (rows not there yet)
        public bool Busy => busy;               // listing or reading details
        public int Pending => Math.Max(0, Volatile.Read(ref pending));   // recordings still to read
        public List<ViewerDemo> Demos { get { lock (gate) return latest; } }
        public int Version => Volatile.Read(ref version);              // the list itself changed
        public int DetailVersion => Volatile.Read(ref detailVersion);  // a row got its details
        // The first row on screen: the reader starts there, so what the player sees fills in first.
        public volatile int ReadFocus;

        // Last pass, for the log and the status file.
        public double ListMs, DetailsMs;
        public int FromCache, FromDisk;

        public ViewerCatalog(ManualLogSource log, string indexPath)
        {
            this.log = log;
            this.indexPath = indexPath;
        }

        // Adds a folder to scan. Empty paths and folders already in the list are ignored.
        public void AddDirectory(string dir)
        {
            lock (gate)
                if (!string.IsNullOrEmpty(dir) && !directories.Contains(dir, StringComparer.OrdinalIgnoreCase)) directories.Add(dir);
        }

        // Starts a background rescan, or asks the running one to list again once it can.
        public void Refresh()
        {
            if (busy) { again = true; return; }
            busy = true; listing = true;
            new Thread(Run) { IsBackground = true, Name = "nr-replays-list", Priority = ThreadPriority.BelowNormal }.Start();
        }

        private void Run()
        {
            try
            {
                do
                {
                    again = false;
                    listing = true;
                    List();
                    listing = false;
                    ReadDetails();
                }
                while (again);
            }
            catch (Exception e) { log.LogWarning("Replays menu: scan failed: " + e); }
            finally { listing = false; busy = false; }
        }

        private void List()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (index == null) index = ReplayIndex.Load(indexPath);
            List<string> dirs;
            Dictionary<string, ViewerDemo> before;
            lock (gate) { dirs = directories.ToList(); before = known; }
            var found = new List<ViewerDemo>();
            int cached = 0;
            foreach (var dir in dirs)
            {
                string[] files;
                try { if (!Directory.Exists(dir)) continue; files = Directory.GetFiles(dir, "*" + DemoFormat.Extension); }
                catch (Exception e) { log.LogWarning("Replays menu: cannot list " + dir + ": " + e.Message); continue; }
                foreach (var file in files)
                {
                    FileInfo info;
                    try { info = new FileInfo(file); if (!info.Exists) continue; } catch (Exception) { continue; }
                    long size = info.Length;
                    var modified = info.LastWriteTimeUtc;
                    if (before.TryGetValue(file, out var d) && d.Size == size && d.Modified == modified) { found.Add(d); continue; }
                    d = new ViewerDemo { Path = file, FileName = System.IO.Path.GetFileName(file), Size = size, Modified = modified, Created = ViewerText.StampFromFileName(System.IO.Path.GetFileName(file)) };
                    ReplayIndex.Entry entry;
                    lock (gate) entry = index.Find(file, size, modified.Ticks);
                    if (entry != null) { d.TakeSummary(entry); cached++; }
                    found.Add(d);
                }
            }
            // The same folder can be reached by two paths (the game's S: drive and Z:), so drop repeats.
            found = found.GroupBy(d => d.FileName + "|" + d.Size + "|" + d.Modified.Ticks, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            found.Sort((a, b) => b.SortTime.CompareTo(a.SortTime));
            lock (gate)
            {
                latest = found;
                known = found.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
                pending = found.Count(d => !d.Detailed);
                version++;
            }
            ListMs = watch.Elapsed.TotalMilliseconds;
            FromCache = cached;
            log.LogInfo($"Replays menu: listed {found.Count} recordings in {ListMs:F0} ms ({cached} from the list cache, {pending} to read)");
        }

        private void ReadDetails()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var lastSave = watch.Elapsed.TotalSeconds;
            int read = 0;
            long bytes = 0;
            while (!again)
            {
                ViewerDemo next = null;
                lock (gate)
                {
                    int n = latest.Count, from = Math.Max(0, Math.Min(ReadFocus, n));
                    for (int k = 0; k < n && next == null; k++)
                    {
                        var d = latest[(from + k) % n];
                        if (!d.Detailed) next = d;
                    }
                }
                if (next == null) break;
                ViewerDemo r;
                try { r = Read(next.Path); }
                catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
                {
                    // Deleted meanwhile (the list's Delete, or outside the game): the next listing drops it.
                    next.Error = "the file is gone";
                    next.Detailed = true;
                    Interlocked.Decrement(ref pending);
                    Interlocked.Increment(ref detailVersion);
                    continue;
                }
                catch (Exception e) { r = new ViewerDemo { Error = e.Message }; }
                next.TakeSummary(r);
                read++; bytes += next.Size;
                Interlocked.Decrement(ref pending);
                Interlocked.Increment(ref detailVersion);
                lock (gate) index.Put(next.ToEntry());
                if (watch.Elapsed.TotalSeconds - lastSave > 3) { SaveIndex(); lastSave = watch.Elapsed.TotalSeconds; }
            }
            if (again) return;
            // Forget recordings that are gone from the folders this listing covered.
            HashSet<string> present, dirs;
            lock (gate)
            {
                present = new HashSet<string>(latest.Select(d => d.Path), StringComparer.OrdinalIgnoreCase);
                dirs = new HashSet<string>(directories.Select(d => d.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
                index.Prune(p => present.Contains(p) || !dirs.Contains((System.IO.Path.GetDirectoryName(p) ?? "").TrimEnd('\\', '/')));
            }
            SaveIndex();
            DetailsMs = watch.Elapsed.TotalMilliseconds;
            FromDisk = read;
            if (read > 0)
                log.LogInfo($"Replays menu: read {read} recordings ({ViewerText.Size(bytes)}) in {DetailsMs / 1000:F1} s; list cache {indexPath}");
        }

        private void SaveIndex()
        {
            string error;
            lock (gate)
            {
                if (index == null || !index.Dirty) return;
                error = index.Save(indexPath);
            }
            if (error != null) log.LogWarning("Replays menu: could not save the list cache: " + error);
        }

        // Deletes a recording for good (the demo and its sidecar files, ViewerText.RecordingFiles) and drops it
        // from the list at once. Returns null, or why it could not (a file still open elsewhere, say). Safe from any
        // thread. When the demo itself is gone but a sidecar stays (in use), the recording still leaves the list.
        public string Delete(ViewerDemo demo, bool saveIndex = true)
        {
            if (demo == null || string.IsNullOrEmpty(demo.Path)) return "nothing selected";
            var files = ViewerText.RecordingFiles(demo.Path);
            try
            {
                if (File.Exists(files[0])) File.Delete(files[0]);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                log.LogWarning("Replays menu: could not delete " + demo.Path + ": " + e.Message);
                return e is UnauthorizedAccessException ? "the file is read-only or not yours" : "the file is in use";
            }
            for (int i = 1; i < files.Length; i++)
            {
                try { if (File.Exists(files[i])) File.Delete(files[i]); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    log.LogWarning("Replays menu: deleted " + demo.Path + " but not " + files[i] + ": " + e.Message);
                }
            }
            log.LogInfo("Replays menu: deleted " + demo.Path);
            lock (gate)
            {
                full.Remove(demo.Path);
                index?.Remove(demo.Path);
                // The same recording can be listed under a second path (S: and Z:); drop every copy of it.
                var drop = latest.Where(d => d == demo || d.FileName == demo.FileName && d.Size == demo.Size && d.Modified == demo.Modified).ToList();
                foreach (var d in drop) { known.Remove(d.Path); index?.Remove(d.Path); if (!d.Detailed) Interlocked.Decrement(ref pending); }
                latest = latest.Except(drop).ToList();
                version++;
            }
            if (saveIndex) SaveIndex();
            return null;
        }

        // Bulk delete on a background thread. `done` runs on the game thread with the recordings deleted and the
        // ones that were not, each with why.
        public volatile int DeleteTotal, DeleteDone;
        public bool Deleting => DeleteTotal > 0;

        public void DeleteMany(List<ViewerDemo> demos, Action<List<ViewerDemo>, List<KeyValuePair<ViewerDemo, string>>> done)
        {
            if (Deleting || demos == null || demos.Count == 0) return;
            DeleteDone = 0;
            DeleteTotal = demos.Count;
            new Thread(() =>
            {
                var deleted = new List<ViewerDemo>();
                var failed = new List<KeyValuePair<ViewerDemo, string>>();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    foreach (var d in demos)
                    {
                        string error;
                        try { error = Delete(d, saveIndex: false); }
                        catch (Exception e) { error = e.Message; }
                        if (error == null) deleted.Add(d); else failed.Add(new KeyValuePair<ViewerDemo, string>(d, error));
                        DeleteDone++;
                    }
                    SaveIndex();
                }
                finally
                {
                    log.LogInfo($"Replays menu: bulk delete of {demos.Count}: {deleted.Count} deleted, {failed.Count} failed, {watch.Elapsed.TotalMilliseconds:F0} ms");
                    UnityMainThread.Post(() => { DeleteTotal = 0; done(deleted, failed); });
                }
            }) { IsBackground = true, Name = "nr-replays-delete" }.Start();
        }

        // Reads one recording with its events, for the viewer (cached). Never throws; a broken file comes back with
        // Error set.
        public ViewerDemo GetFull(string path)
        {
            var info = new FileInfo(path);
            lock (gate)
                if (info.Exists && full.TryGetValue(path, out var hit) && hit.Size == info.Length && hit.Modified == info.LastWriteTimeUtc) return hit;
            ViewerDemo demo;
            try { demo = Read(path); }
            catch (Exception e)
            {
                demo = new ViewerDemo { Path = path, FileName = System.IO.Path.GetFileName(path), Error = e.Message };
            }
            demo.Size = info.Exists ? info.Length : 0;
            demo.Modified = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            demo.Detailed = true;
            demo.EventsLoaded = demo.Error == null;
            lock (gate)
            {
                if (full.Count >= 4) full.Clear();
                full[path] = demo;
                if (index != null && info.Exists) index.Put(demo.ToEntry());
            }
            return demo;
        }

        public static ViewerDemo Read(string path)
        {
            var demo = new ViewerDemo { Path = path, FileName = System.IO.Path.GetFileName(path) };
            var scan = DemoQuickScan.Read(path);
            var header = JObject.Parse(scan.HeaderJson);
            var annotations = scan.Annotations;
            double? first = scan.First, last = scan.Last;
            demo.Truncated = scan.Truncated;
            double origin = first ?? 0;
            demo.Duration = first.HasValue ? last.Value - first.Value : 0;
            foreach (var a in annotations)
            {
                try
                {
                    if (!(JToken.Parse(a.Value) is JObject o) || string.IsNullOrEmpty((string)o["event"])) continue;
                    demo.Events.Add(ViewerEvent.Create(Math.Max(0, a.Key - origin), o));
                }
                catch (Exception) { }
            }
            // A kill placed at its hit can be earlier than events logged before its kill feed; keep time order (stable).
            demo.Events = demo.Events.OrderBy(e => e.T).ToList();
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
