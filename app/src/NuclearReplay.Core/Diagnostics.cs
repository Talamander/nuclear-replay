using System.Runtime.InteropServices;
using System.Text;
using NuclearReplay.Core.Game;

namespace NuclearReplay.Core;

/// <summary>
/// One text file for bug reports: versions, the setup check (as nr status --json prints it), settings,
/// the end of the nr log, the end of the game's BepInEx/LogOutput.log and the latest render job's progress.
/// Nothing secret is stored in settings, so they are included as they are.
/// </summary>
public static class Diagnostics
{
    public const int AppLogLines = 400;
    public const int GameLogLines = 400;

    public static string DiagnosticsDir(AppPaths paths) => Path.Combine(paths.DataDir, "diagnostics");

    /// <param name="gameDirOverride">Game folder to report on instead of the saved one (nr --game-dir).</param>
    public static string Build(NcEnvironment env, string? gameDirOverride = null)
    {
        var sb = new StringBuilder();
        void Section(string title)
        {
            sb.AppendLine();
            sb.AppendLine("==== " + title + " ====");
        }

        sb.AppendLine("Nuclear Replay diagnostics");
        sb.AppendLine($"Created:   {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Version:   {AppVersion.Current}");
        sb.AppendLine($"OS:        {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Runtime:   {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Program:   {AppContext.BaseDirectory}");
        sb.AppendLine($"Bundled:   {AppPaths.BundledDir} ({(Directory.Exists(AppPaths.BundledDir) ? "found" : "MISSING")})");
        sb.AppendLine($"Settings:  {env.Paths.SettingsFile}");
        sb.AppendLine($"Data:      {env.Paths.DataDir}");

        Settings settings;
        try { settings = env.LoadSettings(); }
        catch (Exception e) { settings = new Settings(); sb.AppendLine("Settings could not be read: " + e.Message); }
        if (!string.IsNullOrWhiteSpace(gameDirOverride)) settings.GameDir = gameDirOverride;

        StatusReport? report = null;
        Section("Status (nr status --json)");
        try
        {
            report = env.Status.Check(settings);
            sb.AppendLine(Json.Serialize(report));
        }
        catch (Exception e) { sb.AppendLine("Status check failed: " + e); }

        Section("Settings");
        sb.AppendLine(Json.Serialize(settings));

        Section("Bundled files");
        try
        {
            var bundled = AppPaths.BundledDir;
            if (Directory.Exists(bundled))
                foreach (var f in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories).Order())
                    sb.AppendLine($"{Path.GetRelativePath(bundled, f)}  {new FileInfo(f).Length} bytes"
                        + (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && PluginInfoReader.Read(f) is { } p ? $"  {p.Name} {p.Version}" : ""));
            else sb.AppendLine("(no bundled folder)");
        }
        catch (Exception e) { sb.AppendLine("Could not list: " + e.Message); }

        var game = report?.Game;
        if (game != null)
        {
            Section("Game plugins folder");
            try
            {
                var plugins = Path.Combine(game.GameDir, "BepInEx", "plugins");
                if (Directory.Exists(plugins))
                    foreach (var f in Directory.EnumerateFiles(plugins, "*.dll", SearchOption.AllDirectories).Order())
                        sb.AppendLine($"{Path.GetRelativePath(plugins, f)}  {new FileInfo(f).Length} bytes  {new FileInfo(f).LastWriteTime:yyyy-MM-dd HH:mm}"
                            + (PluginInfoReader.Read(f) is { } p ? $"  {p.Name} {p.Version}" : ""));
                else sb.AppendLine("(no BepInEx/plugins folder)");
            }
            catch (Exception e) { sb.AppendLine("Could not list: " + e.Message); }

            var gameLog = Path.Combine(game.GameDir, "BepInEx", "LogOutput.log");
            Section($"Game log, last {GameLogLines} lines ({gameLog})");
            sb.AppendLine(Log.Tail(gameLog, GameLogLines) ?? "(not found: the game has not run with BepInEx yet)");
        }

        Section("Latest render job");
        try
        {
            var jobs = env.Paths.JobsDir;
            var latest = Directory.Exists(jobs)
                ? Directory.EnumerateFiles(jobs, "progress.json", SearchOption.AllDirectories).Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (latest == null) sb.AppendLine("(none)");
            else
            {
                sb.AppendLine(latest.FullName + "  " + latest.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(Log.Tail(latest.FullName, 50));
                var job = Directory.EnumerateFiles(latest.DirectoryName!, "job.json").FirstOrDefault();
                if (job != null) sb.AppendLine(Log.Tail(job, 80));
            }
        }
        catch (Exception e) { sb.AppendLine("Could not read: " + e.Message); }

        var appLog = Path.Combine(Log.LogDir(env.Paths), Log.FileName);
        Section($"nr log, last {AppLogLines} lines ({appLog})");
        var previous = Log.Tail(Log.Rolled(appLog, 1), AppLogLines);
        var current = Log.Tail(appLog, AppLogLines);
        var combined = string.Join(Environment.NewLine, new[] { previous, current }.Where(s => !string.IsNullOrEmpty(s)))
            .Split(Environment.NewLine);
        sb.AppendLine(combined.Length == 0 || (combined.Length == 1 && combined[0].Length == 0)
            ? "(empty)"
            : string.Join(Environment.NewLine, combined.Skip(Math.Max(0, combined.Length - AppLogLines))));
        return sb.ToString();
    }

    /// <summary>Writes the report to <c>&lt;data&gt;/diagnostics/nuclear-replay-diagnostics-&lt;time&gt;.txt</c> (or <paramref name="path"/>).</summary>
    public static (string Path, string Text) Save(NcEnvironment env, string? path = null, string? gameDirOverride = null)
    {
        var text = Build(env, gameDirOverride);
        path ??= Path.Combine(DiagnosticsDir(env.Paths), $"nuclear-replay-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text);
        return (path, text);
    }
}
