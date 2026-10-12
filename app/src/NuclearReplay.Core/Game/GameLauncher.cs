using System.Diagnostics;

namespace NuclearReplay.Core.Game;

/// <summary>How the game will be started. Shown by --dry-run and in errors.</summary>
public sealed record LaunchPlan
{
    public required string FileName { get; init; }
    public required List<string> Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new();

    public string CommandLine => string.Join(' ', new[] { FileName }.Concat(Arguments).Select(Quote));
    private static string Quote(string s) => s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
}

public sealed class LaunchException : Exception
{
    public string Fix { get; }
    public LaunchException(string message, string fix) : base(message) => Fix = fix;
}

/// <summary>Starts Nuclear Option with replay/render arguments.</summary>
/// <remarks>
/// Windows: NuclearOption.exe is started directly; Steam must already run so Steamworks initializes.
/// Linux: "steam -applaunch 2168680 &lt;args&gt;" so Steam starts the game through Proton with the player's launch options.
/// </remarks>
public sealed class GameLauncher
{
    private readonly Func<bool> gameRunning;
    private readonly Func<bool> steamRunning;
    private readonly Func<LaunchPlan, Process?> start;

    public GameLauncher(Func<bool>? gameRunning = null, Func<bool>? steamRunning = null, Func<LaunchPlan, Process?>? start = null)
    {
        this.gameRunning = gameRunning ?? ProcessInfo.IsGameRunning;
        this.steamRunning = steamRunning ?? ProcessInfo.IsSteamRunning;
        this.start = start ?? StartProcess;
    }

    /// <summary>
    /// Arguments for an interactive replay. With <paramref name="startAt"/> (replay seconds, &gt; 0) the plugin skips
    /// ahead with the same fixed-step fast-forward as renders and plays at 1x from about one second before it.
    /// </summary>
    public static List<string> WatchArgs(PathMapper mapper, string demoPath, string? shotsPath, double? startAt = null)
    {
        var args = new List<string> { "-ncReplay", mapper.ToGamePath(demoPath) };
        if (shotsPath != null) args.AddRange(new[] { "-ncShots", mapper.ToGamePath(shotsPath) });
        if (startAt is > 0) args.AddRange(new[] { "-ncStart", Math.Round(startAt.Value, 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) });
        return args;
    }

    /// <summary>
    /// Arguments that open a recording in the in-game replay viewer: the game window is visible and the player
    /// controls the replay (play, pause, skip, speed, cameras, record clips).
    /// </summary>
    /// <param name="at">Replay second where the viewer starts (the plugin skips ahead to it). Null or 0 starts at the beginning.</param>
    /// <param name="focus">A moment's replay second: the viewer selects that moment and sets its clip in and out points around it.</param>
    /// <param name="clipsDir">Host folder where clips recorded in the viewer are saved (the output folder from the settings).</param>
    /// <param name="ffmpegPath">ffmpeg the plugin encodes viewer clips with (the effective setting); omitted when null.</param>
    /// <param name="quality">Encoder quality from the settings.</param>
    /// <param name="encoder">auto, nvenc, vulkan or x264 from the settings.</param>
    /// <remarks>The plugin keeps the last ffmpeg, quality and encoder it was given, so its own Replays menu uses them too.</remarks>
    public static List<string> ViewerArgs(PathMapper mapper, string demoPath, double? at = null, double? focus = null, string? clipsDir = null,
        string? ffmpegPath = null, int? quality = null, string? encoder = null)
    {
        var args = new List<string> { "-ncReplay", mapper.ToGamePath(demoPath), "-ncViewer" };
        if (at is > 0) args.AddRange(new[] { "-ncAt", Seconds(at.Value) });
        if (focus is >= 0) args.AddRange(new[] { "-ncFocus", Seconds(focus.Value) });
        if (clipsDir != null) args.AddRange(new[] { "-ncClipsDir", mapper.ToGamePath(clipsDir) });
        if (!string.IsNullOrWhiteSpace(ffmpegPath)) args.AddRange(new[] { "-ncFfmpeg", mapper.ToGamePath(ffmpegPath) });
        if (quality != null) args.AddRange(new[] { "-ncQuality", quality.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (!string.IsNullOrWhiteSpace(encoder)) args.AddRange(new[] { "-ncEncoder", encoder });
        return args;
    }

    private static string Seconds(double s) => Math.Round(s, 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Test hook: NC_LAUNCH_WRAPPER names a program that is run with the game arguments instead of Steam or the exe
    /// (scripts/launch-test-copy.sh runs an isolated game copy headless). Null when not set. Used by renders and
    /// pictures; viewer launches use <see cref="ViewerWrapper"/> instead.
    /// </summary>
    public string? Wrapper { get; init; } = System.Environment.GetEnvironmentVariable("NC_LAUNCH_WRAPPER") is { Length: > 0 } w ? w : null;

    /// <summary>
    /// NC_VIEWER_WRAPPER: a program run with the game arguments instead of Steam for viewer launches (the visible
    /// game). The literal value "headless" means "use <see cref="Wrapper"/>" so tests and agents can keep the game
    /// off the desktop. Null when not set: viewer launches then go through Steam even when NC_LAUNCH_WRAPPER is set.
    /// </summary>
    public string? ViewerWrapper { get; init; } = System.Environment.GetEnvironmentVariable("NC_VIEWER_WRAPPER") is { Length: > 0 } v ? v : null;

    /// <summary>The program viewer launches run instead of Steam or the exe, or null for the normal Steam launch.</summary>
    public static string? EffectiveViewerWrapper(string? viewerWrapper, string? launchWrapper) =>
        viewerWrapper == null ? null
        : viewerWrapper.Equals("headless", StringComparison.OrdinalIgnoreCase) ? launchWrapper
        : viewerWrapper;

    private Process? started;

    public LaunchPlan Plan(GameInstall install, IEnumerable<string> gameArgs) => Plan(install, gameArgs, Wrapper);

    /// <summary>How a viewer launch (visible game) would start the game.</summary>
    public LaunchPlan PlanViewer(GameInstall install, IEnumerable<string> gameArgs) =>
        Plan(install, gameArgs, EffectiveViewerWrapper(ViewerWrapper, Wrapper));

    private static LaunchPlan Plan(GameInstall install, IEnumerable<string> gameArgs, string? wrapper)
    {
        var args = gameArgs.ToList();
        if (wrapper != null)
            return new LaunchPlan { FileName = wrapper, Arguments = args };
        if (!install.UsesProton)
        {
            return new LaunchPlan
            {
                FileName = install.ExePath,
                Arguments = args,
                WorkingDirectory = install.GameDir,
                // Lets Steamworks attach to the running client without a relaunch through Steam.
                Environment = new Dictionary<string, string> { ["SteamAppId"] = GameInfo.AppId, ["SteamGameId"] = GameInfo.AppId },
            };
        }
        var (steam, prefix) = FindLinuxSteam();
        return new LaunchPlan
        {
            FileName = steam,
            Arguments = prefix.Concat(new[] { "-applaunch", GameInfo.AppId }).Concat(args).ToList(),
        };
    }

    /// <summary>Checks that the game can start now, then starts it. Throws <see cref="LaunchException"/> with a plain-English fix.</summary>
    public LaunchPlan Launch(GameInstall install, IEnumerable<string> gameArgs) => Launch(install, gameArgs, Wrapper, track: Wrapper != null);

    /// <summary>
    /// Starts the game visibly with the replay viewer (<see cref="ViewerArgs"/>). The game is the player's session:
    /// the launcher does not keep the process, so ending nr or canceling a render never closes it.
    /// </summary>
    public LaunchPlan LaunchViewer(GameInstall install, IEnumerable<string> gameArgs) =>
        Launch(install, gameArgs, EffectiveViewerWrapper(ViewerWrapper, Wrapper), track: false);

    private LaunchPlan Launch(GameInstall install, IEnumerable<string> gameArgs, string? wrapper, bool track)
    {
        if (gameRunning())
            throw new LaunchException("Nuclear Option is already running.", "Close the game first. Nuclear Replay starts it for you.");
        if (wrapper == null && !install.UsesProton && !steamRunning())
            throw new LaunchException("Steam is not running.", "Start Steam and sign in, then try again.");
        if (!File.Exists(install.ExePath))
            throw new LaunchException("NuclearOption.exe was not found.", "Check the game folder: nr settings set game_dir <folder>.");
        var plan = Plan(install, gameArgs, wrapper);
        try
        {
            var process = start(plan);
            if (track)
            {
                lock (this) { started?.Dispose(); started = process; }
            }
            else process?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new LaunchException($"Could not start the game: {e.Message}",
                install.UsesProton ? "Make sure the steam command works in a terminal." : "Check the game folder: nr settings set game_dir <folder>.");
        }
        return plan;
    }

    /// <summary>
    /// With a launch wrapper: stops the wrapper process this launcher started, with everything it started, and
    /// nothing else. Returns 1 when it was still running.
    /// </summary>
    public int StopStarted()
    {
        Process? process;
        lock (this) { process = started; started = null; }
        if (process == null) return 0;
        try
        {
            if (process.HasExited) return 0;
            process.Kill(entireProcessTree: true);
            return 1;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
        finally { process.Dispose(); }
    }

    private static Process? StartProcess(LaunchPlan plan)
    {
        var info = new ProcessStartInfo(plan.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = plan.WorkingDirectory ?? "",
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var arg in plan.Arguments) info.ArgumentList.Add(arg);
        foreach (var (key, value) in plan.Environment) info.Environment[key] = value;
        return Process.Start(info);
    }

    /// <summary>The steam command, or flatpak's when only the Flatpak client is installed.</summary>
    public static (string FileName, string[] Prefix) FindLinuxSteam()
    {
        foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "steam");
            if (File.Exists(candidate)) return (candidate, Array.Empty<string>());
        }
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam")))
            return ("flatpak", new[] { "run", "com.valvesoftware.Steam" });
        return ("steam", Array.Empty<string>());
    }
}
