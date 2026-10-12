using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Game;
using NuclearReplay.Core.Render;
using NuclearReplay.Core.Shots;
using NuclearReplay.Shared;

namespace NuclearReplay.Core;

/// <summary>What nr works with: settings, game discovery, recordings, rendering.</summary>
public sealed class NcEnvironment
{
    public AppPaths Paths { get; }
    public SettingsStore SettingsStore { get; }
    public DemoLibrary Library { get; }
    public StatusService Status { get; }
    public GameLauncher Launcher { get; }

    /// <summary>Reports whether Nuclear Option is running. Replaceable for tests.</summary>
    public Func<bool> GameRunning { get; init; } = ProcessInfo.IsGameRunning;

    /// <summary>Closes the game (cancel of a running render). Replaceable for tests.</summary>
    /// <remarks>With a launch wrapper (NC_LAUNCH_WRAPPER) it stops only the processes the launcher started.</remarks>
    public Func<int> StopGame { get; init; }

    public ModInstaller CreateInstaller() => new(GameRunning);

    /// <summary>The ffmpeg build to download (shared/Ffmpeg/FfmpegPackage.cs). Tests replace it with a small fake.</summary>
    internal FfmpegPackage FfmpegPackage { get; init; } = FfmpegPackage.Pinned;

    /// <summary>Downloads ffmpeg into a game (nr install, nr ffmpeg install).</summary>
    public FfmpegInstaller CreateFfmpegInstaller() => new(Http, FfmpegPackage);

    /// <summary>Shared HTTP client for the ffmpeg download.</summary>
    public HttpClient Http { get; }

    /// <param name="httpHandler">Replaces the network (tests use a fake handler).</param>
    public NcEnvironment(AppPaths? paths = null, GameLauncher? launcher = null, StatusService? status = null,
        HttpMessageHandler? httpHandler = null)
    {
        Paths = paths ?? AppPaths.Default();
        SettingsStore = new SettingsStore(Paths);
        Library = new DemoLibrary(Paths);
        Status = status ?? new StatusService();
        Launcher = launcher ?? new GameLauncher();
        var startedBy = Launcher;
        StopGame = () => startedBy.Wrapper != null ? startedBy.StopStarted() : ProcessInfo.StopGame();
        Http = httpHandler != null ? new HttpClient(httpHandler, disposeHandler: false) : new HttpClient();
        Http.Timeout = TimeSpan.FromSeconds(30);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"NuclearReplay/{AppVersion.Current.Split('-')[0]}");
    }

    public Settings LoadSettings() => SettingsStore.Load();

    public GameInstall? FindGame(Settings settings) => new GameLocator().Locate(settings);

    public string? DemoDir(Settings settings, GameInstall? game) => DemoLibrary.ResolveDemoDir(settings, game);

    /// <summary>Old recording folders read besides <see cref="DemoDir"/> (see <see cref="DemoLibrary.ExtraDemoDirs"/>).</summary>
    public IReadOnlyList<string> ExtraDemoDirs(Settings settings, GameInstall? game) =>
        DemoLibrary.ExtraDemoDirs(settings, game, DemoDir(settings, game));

    /// <summary>Resolves a demo argument: a path, a file name in the recordings folder, or "latest".</summary>
    public string? ResolveDemo(string arg, Settings settings, GameInstall? game)
    {
        if (File.Exists(arg)) return Path.GetFullPath(arg);
        var dir = DemoDir(settings, game);
        if (dir == null) return null;
        var files = Library.FindFiles(dir, ExtraDemoDirs(settings, game));
        if (arg.Equals("latest", StringComparison.OrdinalIgnoreCase)) return files.FirstOrDefault();
        return files.FirstOrDefault(f => Path.GetFileName(f).Equals(arg, StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(arg, StringComparison.OrdinalIgnoreCase))
            // Part of a name ("Furball"): the newest matching recording.
            ?? files.FirstOrDefault(f => Path.GetFileName(f).Contains(arg, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes the job and starts the game. Returns the prepared job and the launch command.</summary>
    public (PreparedJob Job, LaunchPlan Plan) StartRender(RenderRequest request, GameInstall game, bool dryRun, string? missionName = null)
    {
        var mapper = PathMapper.For(game);
        var job = RenderJobWriter.Prepare(request, Paths.JobsDir, mapper, missionName, createOutputDir: !dryRun);
        var plan = dryRun ? Launcher.Plan(game, job.GameArgs) : Launcher.Launch(game, job.GameArgs);
        return (job, plan);
    }

    public static string ShotListSchema => ReadResource("NuclearReplay.Core.shotlist.schema.json");
    public static string RenderJobSchema => ReadResource("NuclearReplay.Core.render-job.schema.json");

    private static string ReadResource(string name)
    {
        using var stream = typeof(NcEnvironment).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
