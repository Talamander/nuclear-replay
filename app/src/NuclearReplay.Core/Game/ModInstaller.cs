using System.IO.Compression;

namespace NuclearReplay.Core.Game;

public sealed record InstallStep(string Action, string Path, string? Detail = null);

public sealed record InstallResult
{
    public required bool Ok { get; init; }
    public required List<InstallStep> Steps { get; init; }
    public string? BackupDir { get; init; }
    public string? Error { get; init; }
    public string? Fix { get; init; }
    public string? InstalledVersion { get; init; }
}

public sealed class InstallOptions
{
    public required string GameDir { get; init; }
    /// <summary>Folder holding NuclearReplay.dll. Default: bundled/plugin next to nr.</summary>
    public string? PluginSourceDir { get; init; }
    /// <summary>BepInEx 5 x64 zip. Default: bundled/BepInEx*.zip next to nr.</summary>
    public string? BepInExZip { get; init; }
    public required string BackupRoot { get; init; }
    /// <summary>Proton prefix of the game. When set, the installer makes sure Wine loads BepInEx's winhttp.dll.</summary>
    public string? CompatDataDir { get; init; }
    /// <summary>
    /// When set, the installer fills the replay plugin's [Viewer] FfmpegPath and ClipsDirectory where they are missing or
    /// empty, so "Clip this" works in a game started from Steam. See <see cref="ReplayConfig"/>.
    /// </summary>
    public ViewerDefaults? Viewer { get; init; }
    public bool CheckGameRunning { get; init; } = true;
}

/// <summary>Installs BepInEx 5 (when missing) and the Nuclear Replay mod, replacing each file atomically and keeping a backup.</summary>
public sealed class ModInstaller
{
    /// <summary>The mod's files. NuclearReplay.dll holds both plugins (recorder and replay player).</summary>
    public static readonly string[] PluginFiles = { "NuclearReplay.dll" };

    /// <summary>
    /// Where the mod lived before it was renamed from Nuclear Context (NuclearContext.dll and NuclearContext.Replay.dll).
    /// BepInEx would load both copies, so the installer moves this folder into the backup.
    /// </summary>
    public const string LegacyPluginFolder = "NuclearContext";

    /// <summary><see cref="InstallStep.Detail"/> of the step that moved <see cref="LegacyPluginFolder"/> away.</summary>
    public const string LegacyStepDetail = "old Nuclear Context plugins";

    public static string LegacyPluginDir(string gameDir) => Path.Combine(gameDir, "BepInEx", "plugins", LegacyPluginFolder);

    /// <summary>
    /// The folder under BepInEx/plugins. It must equal the mod id in the NOMNOM registry
    /// (packaging/nomnom): NOMM installs a mod into BepInEx/plugins/&lt;id&gt;/ with the plugins zip's files at its root
    /// (plus a meta.json that this installer leaves alone), and treats a folder without meta.json as the mod named
    /// like the folder. With the same name, each tool recognizes and updates the other's install.
    /// </summary>
    public const string PluginFolder = "NuclearReplay";

    public static string PluginDir(string gameDir) => Path.Combine(gameDir, "BepInEx", "plugins", PluginFolder);

    /// <summary>Where NOMM moves the plugin folder when the player turns the mod off there.</summary>
    public static string DisabledPluginDir(string gameDir) => Path.Combine(gameDir, "BepInEx", "disabledPlugins", PluginFolder);
    public static string DefaultPluginSource => Path.Combine(AppPaths.BundledDir, "plugin");

    public static string? DefaultBepInExZip()
    {
        var dir = AppPaths.BundledDir;
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "BepInEx*.zip").OrderDescending().FirstOrDefault();
    }

    private readonly Func<bool> gameRunning;
    public ModInstaller(Func<bool>? gameRunning = null) => this.gameRunning = gameRunning ?? ProcessInfo.IsGameRunning;

    public InstallResult Install(InstallOptions options)
    {
        var steps = new List<InstallStep>();
        InstallResult Fail(string error, string fix) => new() { Ok = false, Steps = steps, Error = error, Fix = fix };

        if (!GameInfo.LooksLikeGameDir(options.GameDir))
            return Fail($"Nuclear Option was not found in \"{options.GameDir}\".", "Choose the folder that contains NuclearOption.exe.");
        if (options.CheckGameRunning && gameRunning())
            return Fail("Nuclear Option is running.", "Close the game, then install again.");

        var source = options.PluginSourceDir ?? DefaultPluginSource;
        var missing = PluginFiles.Where(f => !File.Exists(Path.Combine(source, f))).ToList();
        if (missing.Count > 0)
            return Fail($"The mod files are missing from \"{source}\" ({string.Join(", ", missing)}).",
                "Reinstall Nuclear Replay, or point --from at a folder with NuclearReplay.dll.");

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var backupDir = Path.Combine(options.BackupRoot, stamp);

        try
        {
            if (!BepInExInstalled(options.GameDir))
            {
                var zip = options.BepInExZip ?? DefaultBepInExZip();
                if (zip == null || !File.Exists(zip))
                    return Fail("BepInEx 5 (the mod loader) is not installed and its installer zip is missing.",
                        "Reinstall Nuclear Replay, or download BepInEx 5 x64 for Windows and pass it with --bepinex.");
                InstallBepInEx(zip, options.GameDir, backupDir, steps);
            }
            else steps.Add(new InstallStep("kept", Path.Combine(options.GameDir, "BepInEx"), "BepInEx is already installed"));

            MoveLegacyPlugins(options.GameDir, backupDir, steps);
            var target = PluginDir(options.GameDir);
            Directory.CreateDirectory(target);
            foreach (var file in PluginFiles)
                ReplaceFile(Path.Combine(source, file), Path.Combine(target, file), backupDir, Path.Combine("plugins", file), steps);

            if (options.CompatDataDir != null)
            {
                var userReg = ProtonPrefix.UserReg(options.CompatDataDir);
                if (File.Exists(userReg))
                {
                    var text = File.ReadAllText(userReg);
                    if (!ProtonPrefix.HasWinhttpOverride(text))
                    {
                        Backup(userReg, backupDir, "user.reg");
                        Json.WriteFileAtomic(userReg, ProtonPrefix.WithWinhttpOverride(text, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                        steps.Add(new InstallStep("updated", userReg, "Proton loads BepInEx (winhttp=native,builtin)"));
                    }
                    else steps.Add(new InstallStep("kept", userReg, "Proton already loads BepInEx"));
                }
                else steps.Add(new InstallStep("skipped", userReg, "Proton prefix not created yet; start the game once from Steam, then install again"));
            }

            if (options.Viewer != null)
                ReplayConfig.WriteViewerDefaults(options.GameDir, options.Viewer,
                    file => Backup(file, backupDir, Path.Combine("config", ReplayConfig.FileName)), steps);
        }
        catch (UnauthorizedAccessException e)
        {
            return Fail("Windows did not allow writing to the game folder: " + e.Message,
                "Close the game, or run Nuclear Replay as administrator if the game is under Program Files.");
        }
        catch (InvalidDataException e)
        {
            return Fail("The BepInEx zip cannot be used: " + e.Message, "Download BepInEx 5 x64 for Windows again and pass it with --bepinex.");
        }
        catch (IOException e)
        {
            return Fail("A file could not be written: " + e.Message, "Close the game and any program using its files, then try again.");
        }

        return new InstallResult
        {
            Ok = true, Steps = steps, BackupDir = Directory.Exists(backupDir) ? backupDir : null,
            InstalledVersion = PluginInfoReader.Read(Path.Combine(PluginDir(options.GameDir), PluginFiles[0]))?.Version,
        };
    }

    /// <summary>
    /// What an install did, in plain sentences for people (nr install). The raw steps stay in
    /// <see cref="InstallResult.Steps"/> for scripts.
    /// </summary>
    public static List<string> Describe(InstallResult result)
    {
        var lines = new List<string>();
        var bepinex = result.Steps.Where(s => s.Detail == "BepInEx").ToList();
        if (bepinex.Count > 0) lines.Add($"Installed the BepInEx 5 mod loader ({bepinex.Count} files) into the game folder.");
        else if (result.Steps.Any(s => s.Action == "kept" && s.Detail == "BepInEx is already installed")) lines.Add("The BepInEx mod loader was already installed, so it was left as is.");
        foreach (var s in result.Steps.Where(s => s.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && s.Detail != "BepInEx"))
        {
            var name = Path.GetFileName(s.Path);
            lines.Add(s.Action switch
            {
                "installed" => $"Added {name}{(s.Detail != null ? " " + s.Detail : "")} to BepInEx{Path.DirectorySeparatorChar}plugins.",
                "updated" => $"Updated {name} ({s.Detail}).",
                "unchanged" => $"{name} is already up to date{(s.Detail != null ? " (" + s.Detail + ")" : "")}.",
                _ => $"{name}: {s.Action}.",
            });
        }
        foreach (var s in result.Steps.Where(s => s.Detail == LegacyStepDetail))
            lines.Add($"Moved the old Nuclear Context plugins (BepInEx{Path.DirectorySeparatorChar}plugins{Path.DirectorySeparatorChar}{LegacyPluginFolder}) out of the game into the backup, so the game does not load the mod twice. Your recordings stay where they are.");
        foreach (var s in result.Steps.Where(s => s.Path.EndsWith("user.reg", StringComparison.OrdinalIgnoreCase)))
            lines.Add(s.Action switch
            {
                "updated" => "Set the game's Proton prefix to load BepInEx (winhttp override). No Steam launch options are needed.",
                "kept" => "The game's Proton prefix already loads BepInEx.",
                _ => "The game's Proton prefix does not exist yet. Start Nuclear Option once from Steam, close it, then install again.",
            });
        foreach (var s in result.Steps.Where(s => s.Detail?.StartsWith(ReplayConfig.StepDetail, StringComparison.Ordinal) == true))
        {
            if (s.Action == "kept")
            {
                lines.Add("The in-game replay viewer already knows its video encoder and clips folder, so those settings were left as is.");
                continue;
            }
            var told = new List<string>();
            if (s.Detail!.Contains(ReplayConfig.FfmpegKey)) told.Add("where the video encoder (ffmpeg) is");
            if (s.Detail.Contains(ReplayConfig.ClipsKey)) told.Add("where to save clips");
            lines.Add($"Told the in-game replay viewer {string.Join(" and ", told)}, so \"Clip this\" works when you start the game from Steam.");
        }
        int kept = result.Steps.Count(s => s.Detail == "existing BepInEx setting");
        if (kept > 0) lines.Add($"Kept your existing BepInEx settings ({kept} file{(kept == 1 ? "" : "s")}).");
        if (result.BackupDir != null) lines.Add($"Saved the files it replaced in {result.BackupDir}.");
        return lines;
    }

    public static bool BepInExInstalled(string gameDir) =>
        File.Exists(Path.Combine(gameDir, "BepInEx", "core", "BepInEx.dll"))
        && File.Exists(Path.Combine(gameDir, "winhttp.dll"))
        && File.Exists(Path.Combine(gameDir, "doorstop_config.ini"));

    /// <summary>Extracts the BepInEx zip into the game folder. Existing BepInEx/config files are never overwritten.</summary>
    public static void InstallBepInEx(string zipPath, string gameDir, string backupDir, List<InstallStep> steps)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        // Accept zips that wrap everything in one top-level folder.
        string prefix = "";
        if (!files.Any(e => Norm(e.FullName) == "winhttp.dll"))
        {
            var wrapped = files.FirstOrDefault(e => Norm(e.FullName).EndsWith("/winhttp.dll") && Norm(e.FullName).Count(c => c == '/') == 1);
            if (wrapped != null) prefix = Norm(wrapped.FullName)[..(Norm(wrapped.FullName).IndexOf('/') + 1)];
        }
        string Rel(ZipArchiveEntry e) => Norm(e.FullName)[prefix.Length..];
        var relative = files.Where(e => Norm(e.FullName).StartsWith(prefix)).Select(e => (Entry: e, Rel: Rel(e))).ToList();
        if (!relative.Any(r => r.Rel == "winhttp.dll") || !relative.Any(r => r.Rel == "BepInEx/core/BepInEx.dll"))
            throw new InvalidDataException("This zip is not BepInEx 5 for Windows x64 (winhttp.dll or BepInEx/core/BepInEx.dll missing).");
        var root = Path.GetFullPath(gameDir) + Path.DirectorySeparatorChar;
        // Check every path before writing anything.
        var planned = relative.Select(r => (r.Entry, r.Rel, Dest: Path.GetFullPath(Path.Combine(gameDir, r.Rel.Replace('/', Path.DirectorySeparatorChar))))).ToList();
        foreach (var p in planned)
            if (!p.Dest.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Unsafe path in zip: " + p.Entry.FullName);
        foreach (var (entry, rel, dest) in planned)
        {
            if (rel.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase) && File.Exists(dest))
            {
                steps.Add(new InstallStep("kept", dest, "existing BepInEx setting"));
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var temp = dest + ".nc-next";
            entry.ExtractToFile(temp, overwrite: true);
            if (File.Exists(dest)) Backup(dest, backupDir, Path.Combine("game", rel));
            File.Move(temp, dest, overwrite: true);
            steps.Add(new InstallStep("installed", dest, "BepInEx"));
        }
    }

    private static string Norm(string zipPath) => zipPath.Replace('\\', '/');

    /// <summary>
    /// Moves BepInEx/plugins/NuclearContext (the mod before its rename) to &lt;backup&gt;/plugins-legacy/NuclearContext:
    /// copies it there, then deletes it from the game. The backup may be on another drive, so it is a copy and a delete.
    /// Recordings in &lt;game&gt;/NuclearContext are not touched.
    /// </summary>
    public static void MoveLegacyPlugins(string gameDir, string backupDir, List<InstallStep> steps)
    {
        var legacy = LegacyPluginDir(gameDir);
        if (!Directory.Exists(legacy)) return;
        var dest = Path.Combine(backupDir, "plugins-legacy", LegacyPluginFolder);
        foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(dest, Path.GetRelativePath(legacy, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
        }
        Directory.CreateDirectory(dest);
        Directory.Delete(legacy, recursive: true);
        steps.Add(new InstallStep("moved", legacy, LegacyStepDetail));
    }

    /// <summary>Copies to "&lt;target&gt;.next" and renames over the target, so the game never sees a half-written DLL.</summary>
    public static void ReplaceFile(string source, string target, string backupDir, string backupName, List<InstallStep> steps)
    {
        bool existed = File.Exists(target);
        if (existed && FilesEqual(source, target))
        {
            steps.Add(new InstallStep("unchanged", target, PluginInfoReader.Read(target)?.Version));
            return;
        }
        var previous = existed ? PluginInfoReader.Read(target)?.Version : null;
        if (existed) Backup(target, backupDir, backupName);
        var temp = target + ".next";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);
        var version = PluginInfoReader.Read(target)?.Version;
        steps.Add(new InstallStep(existed ? "updated" : "installed", target,
            existed ? $"{previous ?? "unknown"} -> {version ?? "unknown"}" : version));
    }

    private static void Backup(string file, string backupDir, string name)
    {
        var dest = Path.Combine(backupDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(file, dest, overwrite: true);
    }

    private static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
