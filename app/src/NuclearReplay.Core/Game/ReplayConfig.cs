using System.Text.RegularExpressions;
using NuclearReplay.Shared;

namespace NuclearReplay.Core.Game;

/// <summary>
/// The ffmpeg and clips folder the installer gives the in-game replay viewer, as this app sees them. The mapper turns
/// both into paths the game can open (Z:\ paths under Proton).
/// </summary>
/// <param name="FfmpegPath">
/// The ffmpeg the viewer should use: the ffmpeg_path setting, else the shared copy in the game
/// (&lt;game&gt;/NuclearReplay/tools/ffmpeg/ffmpeg.exe), which may not be downloaded yet. A missing file other than the
/// shared copy is not written.
/// </param>
/// <param name="ClipsDir">nr's clips folder (the output folder from the settings).</param>
/// <param name="SharedFfmpegPath">The shared copy's path for this game; null when unknown.</param>
public sealed record ViewerDefaults(string? FfmpegPath, string? ClipsDir, PathMapper Mapper, string? SharedFfmpegPath = null)
{
    public static ViewerDefaults For(Settings settings, GameInstall game)
    {
        var shared = FfmpegPackage.ExePath(game.GameDir);
        var (ffmpeg, source) = StatusService.ResolveFfmpegSource(settings, game.GameDir);
        // A bundled copy next to nr (development builds) is not given to the game; the shared copy is where
        // nr and the viewer both download to, so the viewer finds it once it is there.
        if (source is null or "bundled") ffmpeg = shared;
        return new(ffmpeg, Path.GetFullPath(settings.EffectiveOutputDir), PathMapper.For(game), shared);
    }

    /// <summary>The ffmpeg path to write: an existing file, or the shared copy even before it is downloaded.</summary>
    internal string? WritableFfmpeg =>
        string.IsNullOrWhiteSpace(FfmpegPath) ? null
        : File.Exists(FfmpegPath) || SharedFfmpegPath != null && PathsEqual(FfmpegPath, SharedFfmpegPath) ? FfmpegPath
        : null;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>
/// Reads and fills the replay plugin's BepInEx config (BepInEx/config/&lt;plugin GUID&gt;.cfg).
/// </summary>
/// <remarks>
/// BepInEx 5 reads every "key = value" line under a [Section] when the plugin's config file is opened, and
/// Config.Bind takes a value it already read for that section and key. So the installer can write [Viewer]
/// FfmpegPath and ClipsDirectory before the plugin has ever run, and a game started from Steam finds them.
/// The plugin itself writes the -ncFfmpeg / -ncClipsDir values nr passes on viewer launches into the same keys.
/// </remarks>
public static class ReplayConfig
{
    /// <summary>The replay plugin's GUID (ReplayPlugin.Id in replay/src/ReplayPlugin.cs).</summary>
    public const string PluginGuid = PluginIds.Player;
    public const string FileName = PluginGuid + ".cfg";
    public const string ViewerSection = "Viewer";
    public const string FfmpegKey = "FfmpegPath";
    public const string ClipsKey = "ClipsDirectory";
    /// <summary>Prefix of <see cref="InstallStep.Detail"/> for the viewer settings step.</summary>
    public const string StepDetail = "replay viewer clip settings";

    public static string FilePath(string gameDir) => Path.Combine(gameDir, "BepInEx", "config", FileName);

    /// <summary>
    /// Fills [Viewer] FfmpegPath and ClipsDirectory where they are missing or empty. Values already set (by the player or
    /// by an earlier viewer launch from nr) are never changed. Adds one step: installed (new file), updated or kept.
    /// FfmpegPath is left out when it names a missing file other than the shared copy. When only a config under an old GUID exists, the newest one is first copied to the
    /// new name (as the plugin itself would on its next start) and the copy is filled, so there is one config with the
    /// player's settings; the old file is left as it is.
    /// </summary>
    internal static void WriteViewerDefaults(string gameDir, ViewerDefaults viewer, Action<string> backup, List<InstallStep> steps)
    {
        var values = new List<KeyValuePair<string, string>>();
        if (viewer.WritableFfmpeg is { } ffmpeg) values.Add(new(FfmpegKey, viewer.Mapper.ToGamePath(ffmpeg)));
        if (!string.IsNullOrWhiteSpace(viewer.ClipsDir)) values.Add(new(ClipsKey, viewer.Mapper.ToGamePath(viewer.ClipsDir)));
        if (values.Count == 0) return;

        var path = FilePath(gameDir);
        var copiedFrom = PluginIds.CopyLegacyConfig(Path.GetDirectoryName(path)!, PluginIds.LegacyPlayers, PluginGuid);
        bool copied = copiedFrom != null;
        var copyNote = copied ? "; other settings copied from " + copiedFrom : "";
        bool existed = File.Exists(path);
        var text = existed ? File.ReadAllText(path) : null;
        var filled = new List<string>();
        var updated = Fill(text, ViewerSection, values, filled);
        if (filled.Count == 0)
        {
            steps.Add(new InstallStep("kept", path, StepDetail + " already set" + copyNote));
            return;
        }
        if (existed && !copied) backup(path); // a fresh copy needs no backup; the old file stays as it was
        Json.WriteFileAtomic(path, updated);
        steps.Add(new InstallStep(existed && !copied ? "updated" : "installed", path, StepDetail + ": " + string.Join(", ", filled) + copyNote));
    }

    /// <summary>
    /// Returns <paramref name="text"/> (null for a new file) with each value written where its key is missing or empty
    /// in <paramref name="section"/>, and adds the keys it wrote to <paramref name="filled"/>. Other lines, comments and
    /// line endings stay as they are. Parsing follows BepInEx 5's ConfigFile.Reload.
    /// </summary>
    public static string Fill(string? text, string section, IReadOnlyList<KeyValuePair<string, string>> values, List<string> filled)
    {
        var nl = text == null ? Environment.NewLine : text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text == null ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
        bool endsWithNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (endsWithNewline) lines.RemoveAt(lines.Count - 1);

        var present = new HashSet<string>();
        string? current = null;
        int sectionEnd = -1; // last non-blank line that belongs to the section
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1];
                if (current == section) sectionEnd = i;
                continue;
            }
            if (current != section) continue;
            if (line.Length > 0) sectionEnd = i;
            if (line.StartsWith('#') || SplitKey(line) is not { } kv) continue;
            var wanted = values.FirstOrDefault(v => v.Key == kv.Key);
            if (wanted.Key == null) continue;
            present.Add(kv.Key);
            if (kv.Value.Length > 0) continue;
            lines[i] = kv.Key + " = " + Serialize(wanted.Value);
            if (!filled.Contains(kv.Key)) filled.Add(kv.Key);
        }

        var missing = values.Where(v => !present.Contains(v.Key)).Select(v => v.Key + " = " + Serialize(v.Value)).ToList();
        if (missing.Count > 0)
        {
            if (sectionEnd >= 0) lines.InsertRange(sectionEnd + 1, missing);
            else
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add("");
                lines.AddRange(missing);
            }
            filled.AddRange(values.Where(v => !present.Contains(v.Key)).Select(v => v.Key));
        }
        if (filled.Count == 0) return text ?? "";
        return string.Join(nl, lines) + (endsWithNewline || text == null ? nl : "");
    }

    private static (string Key, string Value)? SplitKey(string line)
    {
        int eq = line.IndexOf('=');
        return eq < 0 ? null : (line[..eq].Trim(), line[(eq + 1)..].Trim());
    }

    // BepInEx 5 keeps a value verbatim when it looks like a drive path ("C:\x", single backslashes); anything else
    // goes through its unescaping, where "\\" stands for one backslash. Drive paths are written as is, the way the
    // plugin's own saves write them.
    private static readonly Regex DrivePath = new(@"^""?\w:\\(?!\\)(?!.+\\\\)");

    private static string Serialize(string value) =>
        DrivePath.IsMatch(value) ? value : value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
