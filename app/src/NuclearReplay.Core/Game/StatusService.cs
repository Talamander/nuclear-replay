using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Steam;
using NuclearReplay.Shared;

namespace NuclearReplay.Core.Game;

public enum CheckStatus { Ok, Info, Warning, Error }

/// <summary>One setup check with a plain-English fix. <see cref="Action"/> names what a front end can do about it (install, choose_game_dir, ...).</summary>
public sealed record StatusCheck(string Id, string Title, CheckStatus Status, string Message, string? Fix = null, string? Action = null);

public sealed class StatusReport
{
    public required List<StatusCheck> Checks { get; init; }
    public GameInstall? Game { get; init; }
    public string? SteamRoot { get; init; }
    public string? DemoDir { get; init; }
    public int DemoCount { get; init; }
    public string? InstalledPluginVersion { get; init; }
    public string? InstalledReplayVersion { get; init; }
    public string? BundledPluginVersion { get; init; }
    public string? FfmpegPath { get; init; }
    public bool GameRunning { get; init; }
    public bool SteamRunning { get; init; }
    /// <summary>Version of nr (version.props).</summary>
    public string AppVersion { get; init; } = NuclearReplay.Core.AppVersion.Current;
    /// <summary>Folder nr reads the bundled plugin, BepInEx and ffmpeg files from.</summary>
    public string BundledDir { get; init; } = AppPaths.BundledDir;
    public string Platform { get; init; } = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other";
    /// <summary>The mod is installed so hosted matches get recorded.</summary>
    public bool ReadyToRecord => Checks.Where(c => c.Id is "game" or "bepinex" or "plugin").All(c => c.Status != CheckStatus.Error);
    /// <summary>Clips can be rendered (replay plugin and ffmpeg present).</summary>
    public bool ReadyToRender => ReadyToRecord && Checks.Where(c => c.Id is "replay_plugin" or "ffmpeg").All(c => c.Status != CheckStatus.Error);
}

public sealed class StatusService
{
    private readonly SteamLocator steam;
    private readonly GameLocator locator;
    private readonly Func<bool> gameRunning;
    private readonly Func<bool> steamRunning;

    public StatusService(SteamLocator? steam = null, Func<bool>? gameRunning = null, Func<bool>? steamRunning = null)
    {
        this.steam = steam ?? new SteamLocator();
        locator = new GameLocator(this.steam);
        this.gameRunning = gameRunning ?? ProcessInfo.IsGameRunning;
        this.steamRunning = steamRunning ?? ProcessInfo.IsSteamRunning;
    }

    /// <summary>
    /// ffmpeg the plugin should use: the ffmpeg_path setting, then the copy downloaded into the game
    /// (&lt;game&gt;/NuclearReplay/tools/ffmpeg/ffmpeg.exe, see <see cref="FfmpegInstaller"/>), then bundled/ffmpeg/ffmpeg.exe
    /// next to nr (development builds). Under Proton the game needs the Windows build.
    /// </summary>
    public static string? ResolveFfmpeg(Settings settings, GameInstall? game = null) => ResolveFfmpegSource(settings, game?.GameDir).Path;

    /// <summary>The ffmpeg <see cref="ResolveFfmpeg"/> picks and where it came from: "setting", "shared", "bundled" or null.</summary>
    public static (string? Path, string? Source) ResolveFfmpegSource(Settings settings, string? gameDir)
    {
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath)) return (settings.FfmpegPath, "setting");
        if (!string.IsNullOrWhiteSpace(gameDir))
        {
            // <game>/NuclearReplay/tools/ffmpeg, else the copy an install from before the rename put in <game>/NuclearContext.
            var shared = FfmpegPackage.FindExe(gameDir);
            if (shared != null) return (shared, "shared");
        }
        var bundled = Path.Combine(AppPaths.BundledDir, "ffmpeg", "ffmpeg.exe");
        return File.Exists(bundled) ? (bundled, "bundled") : (null, null);
    }

    public StatusReport Check(Settings settings)
    {
        var checks = new List<StatusCheck>();
        var roots = steam.FindSteamRoots(settings.SteamDir);
        var steamRoot = roots.FirstOrDefault();
        bool isSteamRunning = steamRunning();
        bool isGameRunning = gameRunning();

        checks.Add(steamRoot != null
            ? new StatusCheck("steam", "Steam", isSteamRunning ? CheckStatus.Ok : CheckStatus.Info,
                isSteamRunning ? "Steam is installed and running." : "Steam is installed but not running.",
                isSteamRunning ? null : "Start Steam before watching or rendering a match.")
            : new StatusCheck("steam", "Steam", CheckStatus.Warning, "Steam was not found.",
                "Install Steam, or set the Steam folder: nr settings set steam_dir <folder>.", "choose_steam_dir"));

        var game = locator.Locate(settings);
        if (game == null)
        {
            checks.Add(new StatusCheck("game", "Nuclear Option", CheckStatus.Error,
                string.IsNullOrWhiteSpace(settings.GameDir)
                    ? "Nuclear Option was not found in your Steam libraries."
                    : $"\"{settings.GameDir}\" does not contain NuclearOption.exe.",
                "Set the game folder: nr settings set game_dir <folder>. In Steam: right-click Nuclear Option > Manage > Browse local files.", "choose_game_dir"));
            return new StatusReport
            {
                Checks = checks, SteamRoot = steamRoot, GameRunning = isGameRunning, SteamRunning = isSteamRunning,
                FfmpegPath = ResolveFfmpeg(settings),
            };
        }
        checks.Add(new StatusCheck("game", "Nuclear Option", CheckStatus.Ok, $"Found in {game.GameDir}."));
        if (isGameRunning)
            checks.Add(new StatusCheck("game_running", "Game running", CheckStatus.Info, "Nuclear Option is running.",
                "Close the game before installing the mod or rendering a clip."));

        bool bepinex = ModInstaller.BepInExInstalled(game.GameDir);
        checks.Add(bepinex
            ? new StatusCheck("bepinex", "Mod loader (BepInEx)", CheckStatus.Ok, "BepInEx 5 is installed.")
            : new StatusCheck("bepinex", "Mod loader (BepInEx)", CheckStatus.Error, "The mod loader is not installed.",
                "Run nr install to add it. Nothing else in your game changes.", "install"));

        var pluginDir = ModInstaller.PluginDir(game.GameDir);
        var plugins = PluginInfoReader.ReadAll(Path.Combine(pluginDir, ModInstaller.PluginFiles[0]));
        var installed = plugins.FirstOrDefault(p => PluginIds.IsRecorder(p.Guid));
        var replay = plugins.FirstOrDefault(p => PluginIds.IsPlayer(p.Guid));
        var bundled = PluginInfoReader.Read(Path.Combine(ModInstaller.DefaultPluginSource, ModInstaller.PluginFiles[0]));
        if (installed == null && File.Exists(Path.Combine(ModInstaller.DisabledPluginDir(game.GameDir), ModInstaller.PluginFiles[0])))
            checks.Add(new StatusCheck("plugin", "Match recorder", CheckStatus.Error,
                "The mod is turned off in a mod manager (NOMM moved it to BepInEx/disabledPlugins).",
                "Turn Nuclear Replay on again in NOMM, or run nr install.", "install"));
        else if (installed == null)
            checks.Add(new StatusCheck("plugin", "Match recorder", CheckStatus.Error, "The Nuclear Replay mod is not installed.",
                "Run nr install.", "install"));
        else if (bundled != null && IsNewer(bundled.Version, installed.Version))
            checks.Add(new StatusCheck("plugin", "Match recorder", CheckStatus.Warning,
                $"Version {installed.Version} is installed; {bundled.Version} is available.", "Run nr install to update.", "install"));
        else
            checks.Add(new StatusCheck("plugin", "Match recorder", CheckStatus.Ok, $"Version {installed.Version} is installed."));
        checks.Add(replay == null
            ? new StatusCheck("replay_plugin", "Replay and render", CheckStatus.Error, "The replay part of the mod is not installed.",
                "Run nr install.", "install")
            : new StatusCheck("replay_plugin", "Replay and render", CheckStatus.Ok, $"Version {replay.Version} is installed."));

        var modLoaded = CheckModLoaded(game);
        if (game.UsesProton)
        {
            var proton = CheckProtonLoader(game, steamRoot);
            // Another launcher or wrapper may set the override. If the game loaded the mod, Proton clearly loads it.
            if (proton.Status == CheckStatus.Warning && modLoaded.Status == CheckStatus.Ok)
                proton = new StatusCheck("proton_loader", proton.Title, CheckStatus.Ok, "The game loaded the mod last time, so Proton loads BepInEx.");
            checks.Add(proton);
        }
        checks.Add(modLoaded);

        var demoDir = DemoLibrary.ResolveDemoDir(settings, game);
        int demoCount = new[] { demoDir }.Concat(DemoLibrary.ExtraDemoDirs(settings, game, demoDir))
            .Where(d => d != null && Directory.Exists(d)).Sum(d => Directory.EnumerateFiles(d!, "*" + DemoFile.Extension).Count());
        checks.Add(demoCount > 0
            ? new StatusCheck("recordings", "Recordings", CheckStatus.Ok, $"{demoCount} recorded match{(demoCount == 1 ? "" : "es")} in {demoDir}.")
            : new StatusCheck("recordings", "Recordings", CheckStatus.Info, "No recorded matches yet.",
                "Host a mission (single-player counts). Every hosted match is recorded automatically."));

        var ffmpeg = ResolveFfmpeg(settings, game);
        checks.Add(ffmpeg != null && File.Exists(ffmpeg)
            ? new StatusCheck("ffmpeg", "Video encoder (ffmpeg)", CheckStatus.Ok, $"Using {ffmpeg}.")
            : new StatusCheck("ffmpeg", "Video encoder (ffmpeg)", CheckStatus.Error,
                ffmpeg == null ? "The video encoder (ffmpeg) is not downloaded yet." : $"\"{ffmpeg}\" does not exist.",
                $"Run nr ffmpeg install. It is a free tool, about {FfmpegPackage.Pinned.SizeText} from {FfmpegPackage.Pinned.Host}."
                    + (game.UsesProton
                        // The render plugin runs inside Proton and starts ffmpeg itself; a Linux ffmpeg cannot be driven from there.
                        ? " To use your own, run nr settings set ffmpeg_path <path to a Windows ffmpeg.exe> (the game runs under Proton, so it needs the Windows build)."
                        : " To use your own, run nr settings set ffmpeg_path <path to ffmpeg.exe>."),
                "download_ffmpeg"));

        var output = settings.EffectiveOutputDir;
        checks.Add(CanWrite(output)
            ? new StatusCheck("output", "Video folder", CheckStatus.Ok, $"Clips are saved to {output}.")
            : new StatusCheck("output", "Video folder", CheckStatus.Error, $"Cannot save files to {output}.",
                "Choose another video folder: nr settings set output_dir <folder>.", "choose_output_dir"));

        return new StatusReport
        {
            Checks = checks, Game = game, SteamRoot = steamRoot, DemoDir = demoDir, DemoCount = demoCount,
            InstalledPluginVersion = installed?.Version, InstalledReplayVersion = replay?.Version, BundledPluginVersion = bundled?.Version,
            FfmpegPath = ffmpeg, GameRunning = isGameRunning, SteamRunning = isSteamRunning,
        };
    }

    private static StatusCheck CheckProtonLoader(GameInstall game, string? steamRoot)
    {
        const string title = "Proton loads the mod";
        if (game.CompatDataDir != null)
        {
            var userReg = ProtonPrefix.UserReg(game.CompatDataDir);
            try
            {
                if (File.Exists(userReg) && ProtonPrefix.HasWinhttpOverride(File.ReadAllText(userReg)))
                    return new StatusCheck("proton_loader", title, CheckStatus.Ok, "The game's Proton prefix loads BepInEx.");
            }
            catch (IOException) { }
        }
        if (steamRoot != null && SteamLaunchOptions.Read(steamRoot, GameInfo.AppId).Any(o => o.Contains("winhttp", StringComparison.OrdinalIgnoreCase)))
            return new StatusCheck("proton_loader", title, CheckStatus.Ok, "Steam launch options load BepInEx.");
        return new StatusCheck("proton_loader", title, CheckStatus.Warning,
            "Proton may not load the mod loader.",
            game.CompatDataDir == null
                ? "Start the game once from Steam, then run nr install. Or set the launch options to: WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"
                : "Run nr install to fix it, or set the Steam launch options to: WINEDLLOVERRIDES=\"winhttp=n,b\" %command%",
            "install");
    }

    private static StatusCheck CheckModLoaded(GameInstall game)
    {
        var log = Path.Combine(game.GameDir, "BepInEx", "LogOutput.log");
        const string title = "Mod loaded in game";
        if (!File.Exists(log))
            return new StatusCheck("mod_loaded", title, CheckStatus.Info, "The game has not run with the mod loader yet.",
                "Start Nuclear Option once from Steam to finish setup.");
        try
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            int lines = 0;
            while ((line = reader.ReadLine()) != null && lines++ < 5000)
                if (line.Contains("Loading [Nuclear Replay ", StringComparison.Ordinal))
                    return new StatusCheck("mod_loaded", title, CheckStatus.Ok, "The game loaded Nuclear Replay the last time it ran.");
        }
        catch (IOException) { }
        return new StatusCheck("mod_loaded", title, CheckStatus.Info, "The last game session did not load Nuclear Replay.",
            "Start Nuclear Option once after installing. If this stays, check BepInEx/LogOutput.log in the game folder.");
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            // Probe the folder, or its nearest existing parent, without creating anything permanent.
            var existing = Path.GetFullPath(dir);
            while (!Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (parent == null) return false;
                existing = parent;
            }
            var probe = Path.Combine(existing, ".nc-write-test-" + Guid.NewGuid().ToString("N")[..6]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Compares versions like 0.4.1, 0.5.0-beta.1 and 0.5.0 (a pre-release sorts before its release).</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static (Version? Core, string Pre) Split(string v)
        {
            var text = v.Split('+')[0];
            var dash = text.IndexOf('-');
            var core = dash < 0 ? text : text[..dash];
            return (Version.TryParse(core, out var parsed) ? parsed : null, dash < 0 ? "" : text[(dash + 1)..]);
        }
        var (a, aPre) = Split(candidate);
        var (b, bPre) = Split(current);
        if (a == null || b == null) return !string.Equals(candidate, current, StringComparison.Ordinal);
        if (a != b) return a > b;
        if (aPre == bPre) return false;
        if (aPre.Length == 0) return true;   // release beats its pre-release
        if (bPre.Length == 0) return false;
        return ComparePre(aPre, bPre) > 0;
    }

    private static int ComparePre(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool xn = int.TryParse(x[i], out var xi), yn = int.TryParse(y[i], out var yi);
            int c = xn && yn ? xi.CompareTo(yi) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }
}

/// <summary>Reads per-user Steam launch options for an app from userdata/*/config/localconfig.vdf.</summary>
public static class SteamLaunchOptions
{
    public static List<string> Read(string steamRoot, string appId)
    {
        var result = new List<string>();
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return result;
        foreach (var user in Directory.EnumerateDirectories(userdata))
        {
            var file = Path.Combine(user, "config", "localconfig.vdf");
            if (!File.Exists(file)) continue;
            try
            {
                var options = FromLocalConfig(Vdf.ParseFile(file), appId);
                if (!string.IsNullOrWhiteSpace(options)) result.Add(options!);
            }
            catch (Exception) { }
        }
        return result;
    }

    public static string? FromLocalConfig(VdfNode root, string appId) =>
        root.Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", appId)?.GetString("LaunchOptions")
        ?? root.Path("UserLocalConfigStore", "Software", "Valve", "Steam", "Apps", appId)?.GetString("LaunchOptions");
}
