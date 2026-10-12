using NuclearReplay.Core.Game;
using NuclearReplay.Shared;

namespace NuclearReplay.Core.Demos;

/// <summary>Finds recordings and reads their summaries, with a small on-disk cache keyed by path, size and modification time.</summary>
public sealed class DemoLibrary
{
    private readonly string? cacheFile;
    private Dictionary<string, CacheEntry>? cache;
    private readonly object gate = new();

    public DemoLibrary(AppPaths? paths)
    {
        cacheFile = paths == null ? null : Path.Combine(paths.CacheDir, "demos.json");
    }

    /// <summary>Bumped when summaries gain fields, so older cache entries are read again.</summary>
    private const int CacheVersion = 3; // 3: kills placed at their hit (DemoEvent.HitAgo, 0.5.1)

    private sealed class CacheEntry
    {
        public int Version { get; set; }
        public long Size { get; set; }
        public long ModifiedTicks { get; set; }
        public required DemoDetails Details { get; set; }
    }

    /// <summary>Recording folder: settings override, then the recorder's [Demo] OutputDirectory, then &lt;game&gt;/NuclearReplay/demos.</summary>
    public static string? ResolveDemoDir(Settings settings, GameInstall? install)
    {
        if (!string.IsNullOrWhiteSpace(settings.DemoDir)) return settings.DemoDir;
        if (install == null) return null;
        var configured = ReadRecorderDemoDir(install.GameDir);
        if (configured != null)
        {
            var host = PathMapper.For(install).ToHostPath(configured);
            if (Directory.Exists(host)) return host;
        }
        return ModFolders.Demos(install.GameDir);
    }

    /// <summary>
    /// Folders read besides the recording folder: &lt;game&gt;/NuclearContext/demos, where the mod recorded before it was
    /// renamed, when it exists. That folder is only read, never moved or changed. Empty when the settings name a folder.
    /// </summary>
    public static IReadOnlyList<string> ExtraDemoDirs(Settings settings, GameInstall? install, string? demoDir)
    {
        if (!string.IsNullOrWhiteSpace(settings.DemoDir) || install == null) return Array.Empty<string>();
        var legacy = ModFolders.LegacyDemos(install.GameDir);
        if (!Directory.Exists(legacy) || demoDir != null && ModFolders.SamePath(legacy, demoDir)) return Array.Empty<string>();
        return new[] { legacy };
    }

    /// <summary>
    /// [Demo] OutputDirectory from the recorder's config (BepInEx/config/talamander.nuclearreplay.recorder.cfg, or the newest
    /// file under an old GUID until the plugin has copied it), as the game sees it.
    /// </summary>
    public static string? ReadRecorderDemoDir(string gameDir)
    {
        var cfg = PluginIds.ExistingConfig(Path.Combine(gameDir, "BepInEx", "config"), PluginIds.Recorder, PluginIds.LegacyRecorders);
        if (!File.Exists(cfg)) return null;
        string? section = null;
        try
        {
            foreach (var raw in File.ReadLines(cfg))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
                if (section != "Demo" || line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim() == "OutputDirectory")
                {
                    var value = line[(eq + 1)..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>Recordings in <paramref name="demoDir"/> and <paramref name="extraDirs"/>, newest first (names start with the UTC time).</summary>
    public IReadOnlyList<string> FindFiles(string? demoDir, IEnumerable<string>? extraDirs = null)
    {
        var dirs = new[] { demoDir }.Concat(extraDirs ?? Array.Empty<string>()).Where(d => d != null && Directory.Exists(d));
        return dirs.SelectMany(d => Directory.EnumerateFiles(d!, "*" + DemoFile.Extension, SearchOption.TopDirectoryOnly))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
    }

    public List<DemoSummary> List(string? demoDir, IEnumerable<string>? extraDirs = null)
    {
        var result = new List<DemoSummary>();
        foreach (var file in FindFiles(demoDir, extraDirs))
        {
            try { result.Add(Get(file).Summary); }
            catch (FileNotFoundException) { /* deleted while listing */ }
        }
        return result;
    }

    /// <summary>Summary and events of one recording. Unreadable files come back with Summary.Error set.</summary>
    public DemoDetails Get(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("Recording not found.", full);
        lock (gate)
        {
            LoadCache();
            if (cache!.TryGetValue(full, out var hit) && hit.Version == CacheVersion && hit.Size == info.Length && hit.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
                return hit.Details;
        }
        DemoDetails details;
        try { details = DemoAnalyzer.Read(full); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new DemoDetails
            {
                Summary = new DemoSummary
                {
                    Path = full, FileName = info.Name, SizeBytes = info.Length, ModifiedUtc = info.LastWriteTimeUtc,
                    Error = e.Message,
                },
                Header = new System.Text.Json.Nodes.JsonObject(),
                Events = new List<DemoEvent>(),
            };
        }
        lock (gate)
        {
            cache![full] = new CacheEntry { Version = CacheVersion, Size = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks, Details = details };
            SaveCache();
        }
        return details;
    }

    private void LoadCache()
    {
        if (cache != null) return;
        cache = new Dictionary<string, CacheEntry>();
        if (cacheFile == null || !File.Exists(cacheFile)) return;
        try { cache = Json.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(cacheFile)) ?? cache; }
        catch (Exception) { cache = new Dictionary<string, CacheEntry>(); }
    }

    private void SaveCache()
    {
        if (cacheFile == null) return;
        try
        {
            // Drop entries for files that no longer exist.
            foreach (var key in cache!.Keys.Where(k => !File.Exists(k)).ToList()) cache.Remove(key);
            Json.WriteFileAtomic(cacheFile, Json.Serialize(cache, indented: false));
        }
        catch (Exception) { /* the cache is an optimization only */ }
    }
}
