using System.Text.Json.Nodes;
using NuclearReplay.Core.Game;
using NuclearReplay.Core.Render;

namespace NuclearReplay.Core.Tests;

public class RenderTests
{
    [Fact]
    public void ParsesRanges()
    {
        Assert.Equal(new[] { new TimeRange(40, 48) }, TimeRange.ParseList("40:48"));
        Assert.Equal(new[] { new TimeRange(1929, 1952.5), new TimeRange(0, 3) }, TimeRange.ParseList(" 1929:1952.5 , 0:3 "));
        Assert.Equal(2, TimeRange.ParseList("0:10,10:20").Count); // touching is fine
        foreach (var bad in new[] { "", "40", "48:40", "a:b", "1:2:3", "-1:4", "5:5", "0:10,5:20", "30:40,0:31" })
            Assert.Throws<FormatException>(() => TimeRange.ParseList(bad));
    }

    [Fact]
    public void JobFileFollowsContractUnderProton()
    {
        using var dir = new TempDir();
        var demo = dir.Write("demos/match one.ncdemo", "x");
        var job = RenderJobWriter.Prepare(new RenderRequest
        {
            DemoPath = demo,
            Ranges = new() { new TimeRange(1929, 1952), new TimeRange(10, 20) },
            Width = 1080, Height = 1920, Fps = 30,
            ShotsJson = """{"shots":[{"t":1929,"camera":"chase","target":"auto"}]}""",
            OutputDir = dir.Combine("out"),
            FfmpegPath = "/opt/ffmpeg/ffmpeg.exe",
        }, dir.Combine("jobs"), new PathMapper(proton: true), missionName: "Escalation: Co-op");

        var json = JsonNode.Parse(File.ReadAllText(job.JobFile))!.AsObject();
        Assert.Equal(new[] { "version", "demo", "width", "height", "shots", "hud", "ffmpeg", "encoder", "quality", "progress", "ranges" },
            json.Select(p => p.Key).ToArray());
        Assert.Equal(1, json["version"]!.GetValue<int>());
        Assert.Equal("Z:" + demo.Replace('/', '\\'), json["demo"]!.GetValue<string>());
        Assert.Equal(@"Z:\opt\ffmpeg\ffmpeg.exe", json["ffmpeg"]!.GetValue<string>());
        Assert.StartsWith("Z:", json["shots"]!.GetValue<string>());
        Assert.StartsWith("Z:", json["progress"]!.GetValue<string>());
        Assert.False(json["hud"]!.GetValue<bool>());
        Assert.Equal("auto", json["encoder"]!.GetValue<string>());
        var range = json["ranges"]![0]!;
        Assert.Equal(1929, range["from"]!.GetValue<double>());
        Assert.Equal(30, range["fps"]!.GetValue<int>());
        Assert.EndsWith(@"\out\Escalation_ Co-op 32-09 to 32-32.mp4", range["out"]!.GetValue<string>());
        Assert.Equal(2, job.Outputs.Count);
        Assert.True(File.Exists(Path.Combine(job.JobDir, "shots.json")));
        Assert.Equal(new[] { "-ncRenderJob", "Z:" + job.JobFile.Replace('/', '\\') }, job.GameArgs);
    }

    [Fact]
    public void NullShotsAndFfmpegAreWrittenAsNull()
    {
        using var dir = new TempDir();
        var job = RenderJobWriter.Prepare(new RenderRequest
        {
            DemoPath = dir.Write("d.ncdemo", "x"), Ranges = new() { new TimeRange(0, 1) }, OutputDir = dir.Combine("out"),
        }, dir.Combine("jobs"), new PathMapper(proton: false));
        var json = JsonNode.Parse(File.ReadAllText(job.JobFile))!.AsObject();
        Assert.True(json.ContainsKey("shots"));
        Assert.Null(json["shots"]);
        Assert.True(json.ContainsKey("ffmpeg"));
        Assert.Equal(Path.GetFullPath(dir.Combine("d.ncdemo")), json["demo"]!.GetValue<string>());
    }

    [Fact]
    public void RejectsBadSettings()
    {
        using var dir = new TempDir();
        RenderRequest Req(int fps = 60, string encoder = "auto", int w = 1920) => new()
        {
            DemoPath = dir.Write("d.ncdemo", "x"), Ranges = new() { new TimeRange(0, 1) }, OutputDir = dir.Combine("out"), Fps = fps, Encoder = encoder, Width = w,
        };
        Assert.Throws<ArgumentException>(() => RenderJobWriter.Prepare(Req(fps: 0), dir.Combine("jobs"), new PathMapper(false)));
        Assert.Throws<ArgumentException>(() => RenderJobWriter.Prepare(Req(encoder: "h265"), dir.Combine("jobs"), new PathMapper(false)));
        Assert.Throws<ArgumentException>(() => RenderJobWriter.Prepare(Req(w: 8), dir.Combine("jobs"), new PathMapper(false)));
        var overlapping = new RenderRequest { DemoPath = dir.Write("d.ncdemo", "x"), Ranges = new() { new TimeRange(0, 10), new TimeRange(9, 12) }, OutputDir = dir.Combine("out") };
        Assert.Throws<ArgumentException>(() => RenderJobWriter.Prepare(overlapping, dir.Combine("jobs"), new PathMapper(false)));
    }

    [Fact]
    public async Task WatcherFollowsProgressToDone()
    {
        using var dir = new TempDir();
        var file = dir.Combine("progress.json");
        var seen = new List<string>();
        var watcher = new ProgressWatcher(file, () => true) { PollInterval = TimeSpan.FromMilliseconds(20) };
        var task = watcher.WatchAsync(p => { lock (seen) seen.Add(p.State); });
        await Task.Delay(60);
        Json.WriteFileAtomic(file, """{"state":"rendering","range":0,"ranges":2,"frame":120,"frames":690,"error":null}""");
        await Task.Delay(80);
        Json.WriteFileAtomic(file, """{"state":"done","range":1,"ranges":2,"frame":690,"frames":690,"error":null}""");
        var final = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("done", final.State);
        Assert.Equal(1.0, final.Fraction);
        Assert.Equal(new[] { "waiting", "rendering", "done" }, seen);
    }

    [Fact]
    public async Task WatcherReportsGameExitAsLost()
    {
        using var dir = new TempDir();
        var file = dir.Combine("progress.json");
        Json.WriteFileAtomic(file, """{"state":"rendering","range":0,"ranges":1,"frame":10,"frames":100,"error":null}""");
        var watcher = new ProgressWatcher(file, () => false) { PollInterval = TimeSpan.FromMilliseconds(10), ExitGrace = TimeSpan.FromMilliseconds(50) };
        var final = await watcher.WatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("lost", final.State);
        Assert.Equal(10, final.Frame);
    }

    [Fact]
    public async Task WatcherTimesOutWhenGameNeverStarts()
    {
        using var dir = new TempDir();
        var watcher = new ProgressWatcher(dir.Combine("p.json"), () => false) { PollInterval = TimeSpan.FromMilliseconds(10), StartupTimeout = TimeSpan.FromMilliseconds(50) };
        var final = await watcher.WatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("error", final.State);
        Assert.Contains("did not start", final.Error);
    }

    [Fact]
    public void ProgressFraction()
    {
        Assert.Equal(0.75, new RenderProgress { State = "rendering", Range = 1, Ranges = 2, Frame = 50, Frames = 100 }.Fraction, 6);
        Assert.Equal(0.5, new RenderProgress { State = "encoding", Range = 0, Ranges = 2 }.Fraction, 6);
        Assert.Equal(0, new RenderProgress().Fraction);
        Assert.Contains("frame 120 of 690", new RenderProgress { State = "rendering", Ranges = 1, Frame = 120, Frames = 690 }.Description);
    }

    [Fact]
    public void LauncherPlansPerPlatform()
    {
        var windows = new GameInstall { GameDir = @"C:\Games\NO", Source = "manual", UsesProton = false };
        var plan = new GameLauncher(() => false, () => true, _ => null).Plan(windows, new[] { "-ncRenderJob", @"C:\j.json" });
        Assert.EndsWith("NuclearOption.exe", plan.FileName);
        Assert.Equal(new[] { "-ncRenderJob", @"C:\j.json" }, plan.Arguments);
        Assert.Equal("2168680", plan.Environment["SteamAppId"]);

        var linux = windows with { GameDir = "/games/NO", UsesProton = true };
        var p2 = new GameLauncher(() => false, () => true, _ => null).Plan(linux, new[] { "-ncReplay", @"Z:\a b.ncdemo" });
        Assert.Equal(new[] { "-applaunch", "2168680", "-ncReplay", @"Z:\a b.ncdemo" }, p2.Arguments.SkipWhile(a => a != "-applaunch"));
    }

    [Fact]
    public void LauncherRefusesWhenGameRunsOrSteamIsMissingOnWindows()
    {
        using var dir = new TempDir();
        var game = GameLocator.Describe(dir.FakeGame(), "manual", null) with { UsesProton = false };
        var started = new List<LaunchPlan>();
        Assert.Throws<LaunchException>(() => new GameLauncher(() => true, () => true, p => { started.Add(p); return null; }).Launch(game, new[] { "-x" }));
        Assert.Throws<LaunchException>(() => new GameLauncher(() => false, () => false, p => { started.Add(p); return null; }).Launch(game, new[] { "-x" }));
        Assert.Empty(started);
        new GameLauncher(() => false, () => true, p => { started.Add(p); return null; }).Launch(game, new[] { "-x" });
        Assert.Single(started);
    }

    [Fact]
    public void ViewerArgsFollowThePluginContract()
    {
        // Seconds use invariant culture with up to 3 decimals.
        var args = GameLauncher.ViewerArgs(new PathMapper(proton: true), "/demos/a b.ncdemo", 12.34567, 17.5, "/videos/clips");
        Assert.Equal(new[] { "-ncReplay", @"Z:\demos\a b.ncdemo", "-ncViewer", "-ncAt", "12.346", "-ncFocus", "17.5", "-ncClipsDir", @"Z:\videos\clips" }, args);

        Assert.Equal(new[] { "-ncReplay", "/d/x.ncdemo", "-ncViewer" }, GameLauncher.ViewerArgs(new PathMapper(false), "/d/x.ncdemo", 0, null, null));

        // Clip encoder settings come last: ffmpeg (game path, only when set), quality, encoder.
        var withEncoder = GameLauncher.ViewerArgs(new PathMapper(proton: true), "/d/x.ncdemo", 5, null, "/v", "/tools/ffmpeg.exe", 23, "vulkan");
        Assert.Equal(new[] { "-ncReplay", @"Z:\d\x.ncdemo", "-ncViewer", "-ncAt", "5", "-ncClipsDir", @"Z:\v",
            "-ncFfmpeg", @"Z:\tools\ffmpeg.exe", "-ncQuality", "23", "-ncEncoder", "vulkan" }, withEncoder);
        var noFfmpeg = GameLauncher.ViewerArgs(new PathMapper(false), "/d/x.ncdemo", null, null, "/v", null, 19, "auto");
        Assert.DoesNotContain("-ncFfmpeg", noFfmpeg);
        Assert.Equal(new[] { "-ncClipsDir", "/v", "-ncQuality", "19", "-ncEncoder", "auto" }, noFfmpeg.Skip(3));
        Assert.Contains("vulkan", RenderJobWriter.Encoders);
        Assert.Equal(new[] { "-ncReplay", "/d/x.ncdemo", "-ncViewer", "-ncFocus", "0" }, GameLauncher.ViewerArgs(new PathMapper(false), "/d/x.ncdemo", null, 0, null));
    }

    [Fact]
    public void ViewerWrapperChoice()
    {
        Assert.Null(GameLauncher.EffectiveViewerWrapper(null, "/w/launch.sh"));
        Assert.Equal("/w/launch.sh", GameLauncher.EffectiveViewerWrapper("headless", "/w/launch.sh"));
        Assert.Null(GameLauncher.EffectiveViewerWrapper("headless", null));
        Assert.Equal("/w/view.sh", GameLauncher.EffectiveViewerWrapper("/w/view.sh", "/w/launch.sh"));

        var linux = new GameInstall { GameDir = "/games/NO", Source = "manual", UsesProton = true };
        var args = new[] { "-ncReplay", @"Z:\a.ncdemo", "-ncViewer" };

        // NC_LAUNCH_WRAPPER alone: renders use it, the visible viewer still goes through Steam.
        var launchOnly = new GameLauncher(() => false, () => true, _ => null) { Wrapper = "/w/launch.sh", ViewerWrapper = null };
        Assert.Equal("/w/launch.sh", launchOnly.Plan(linux, args).FileName);
        var viaSteam = launchOnly.PlanViewer(linux, args);
        Assert.NotEqual("/w/launch.sh", viaSteam.FileName);
        Assert.Equal(new[] { "-applaunch", "2168680" }.Concat(args), viaSteam.Arguments.SkipWhile(a => a != "-applaunch"));

        // NC_VIEWER_WRAPPER: its own program for viewer launches; renders keep NC_LAUNCH_WRAPPER.
        var both = new GameLauncher(() => false, () => true, _ => null) { Wrapper = "/w/launch.sh", ViewerWrapper = "/w/view.sh" };
        Assert.Equal("/w/view.sh", both.PlanViewer(linux, args).FileName);
        Assert.Equal(args, both.PlanViewer(linux, args).Arguments);
        Assert.Equal("/w/launch.sh", both.Plan(linux, args).FileName);

        // "headless" sends viewer launches to NC_LAUNCH_WRAPPER (tests and agents keep the game off the desktop).
        var headless = new GameLauncher(() => false, () => true, _ => null) { Wrapper = "/w/launch.sh", ViewerWrapper = "headless" };
        Assert.Equal("/w/launch.sh", headless.PlanViewer(linux, args).FileName);
    }

    [Fact]
    public void LaunchViewerKeepsTheGameRunningChecks()
    {
        using var dir = new TempDir();
        var game = GameLocator.Describe(dir.FakeGame(), "manual", null) with { UsesProton = false };
        var started = new List<LaunchPlan>();
        var running = new GameLauncher(() => true, () => true, p => { started.Add(p); return null; }) { Wrapper = null, ViewerWrapper = null };
        Assert.Equal("Nuclear Option is already running.", Assert.Throws<LaunchException>(() => running.LaunchViewer(game, new[] { "-ncViewer" })).Message);
        var noSteam = new GameLauncher(() => false, () => false, p => { started.Add(p); return null; }) { Wrapper = "/w/launch.sh", ViewerWrapper = null };
        Assert.Throws<LaunchException>(() => noSteam.LaunchViewer(game, new[] { "-ncViewer" }));
        Assert.Empty(started);

        var ok = new GameLauncher(() => false, () => true, p => { started.Add(p); return null; }) { Wrapper = "/w/launch.sh", ViewerWrapper = null };
        ok.LaunchViewer(game, new[] { "-ncViewer" });
        Assert.EndsWith("NuclearOption.exe", Assert.Single(started).FileName);
        // The viewer is the player's game: the launcher keeps nothing to stop.
        Assert.Equal(0, ok.StopStarted());
    }
}
