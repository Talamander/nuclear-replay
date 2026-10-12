using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NuclearReplay.Cli;
using NuclearReplay.Core.Clips;
using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Game;
using NuclearReplay.Core.Preview;
using NuclearReplay.Preview;

namespace NuclearReplay.Core.Tests;

/// <summary>Live preview and thumbnails: wire format, listener, the simulated game end to end, the DVR timeline, nr preview/thumbs.</summary>
public class PreviewTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private string WriteDemo(string folder, string name = "20261004T021932Z-Escalation.ncdemo")
    {
        var builder = new DemoFile.Builder("""{"format":"nuclear-context-network-demo","format_version":1,"mission_name":"Escalation","game_state":"Multiplayer"}""")
            .Annotation(10, """{"event":"ghost_authenticated"}""")
            .Message(10.5, new byte[] { 0x65, 0xaa, 1, 2, 3 })
            .Annotation(11, """{"event":"mission","name":"Escalation","map":"Heartland"}""")
            .Annotation(40, """{"event":"launch","shooter":{"id":169,"name":"Revoker","player":"Talamander"},"target":{"id":201,"name":"Ifrit"},"weapon":"AAM-29","missile":4021}""")
            .Annotation(45, """{"event":"kill","victim":{"id":201,"name":"Ifrit"},"killer":{"id":169,"name":"Revoker","player":"Talamander"},"weapon":"AAM-29"}""")
            .Message(110, new byte[] { 0x65, 0xaa }).Annotation(110, """{"event":"end","reason":"shutdown"}""");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, builder.ToArray());
        return path;
    }

    private (NcEnvironment Env, SimulatedGame Sim, GameInstall Game) Simulated(SimulatedGame sim)
    {
        var env = new NcEnvironment(new AppPaths(dir.Combine("home", "config"), dir.Combine("home", "data")), sim.CreateLauncher())
        {
            GameRunning = () => sim.Running,
            StopGame = sim.Kill,
        };
        return (env, sim, SimulatedGame.StubInstall(dir.Combine("game")));
    }

    private static async Task Until(Func<bool> condition, double seconds = 20, string what = "condition")
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    // ---------- wire ----------

    [Fact]
    public void WireRoundTrip()
    {
        var stream = new MemoryStream();
        PreviewWire.Write(stream, PreviewWire.Message, """{"type":"status","state":"loading"}""");
        var image = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        PreviewWire.Write(stream, PreviewWire.Frame, """{"type":"frame","t":1.5}""", image, 1000);
        stream.Position = 0;

        Assert.True(PreviewWire.TryRead(stream, out var kind, out var header, out var payload));
        Assert.Equal(PreviewWire.Message, kind);
        Assert.Equal("loading", (string?)JsonNode.Parse(header)!["state"]);
        Assert.Empty(payload ?? Array.Empty<byte>());
        Assert.True(PreviewWire.TryRead(stream, out kind, out header, out payload));
        Assert.Equal(PreviewWire.Frame, kind);
        Assert.Equal(image.Take(1000), payload);
        Assert.False(PreviewWire.TryRead(stream, out _, out _, out _)); // clean end
        Assert.Equal("\"a\\\"b\\\\c\\n\"", PreviewWire.Quote("a\"b\\c\n"));
    }

    [Fact]
    public void WireRejectsBadData()
    {
        var truncated = new MemoryStream();
        PreviewWire.Write(truncated, PreviewWire.Message, """{"type":"hello"}""");
        var cut = new MemoryStream(truncated.ToArray()[..^3]);
        Assert.ThrowsAny<IOException>(() => PreviewWire.TryRead(cut, out _, out _, out _));

        // A header length beyond the limit is refused before anything is allocated.
        var huge = new MemoryStream(new byte[] { 1, 0x7f, 0xff, 0xff, 0xff, 0, 0, 0, 0 });
        Assert.Throws<InvalidDataException>(() => PreviewWire.TryRead(huge, out _, out _, out _));
        var badKind = new MemoryStream(new byte[] { 9, 0, 0, 0, 2, 0, 0, 0, 0, (byte)'{', (byte)'}' });
        Assert.Throws<InvalidDataException>(() => PreviewWire.TryRead(badKind, out _, out _, out _));
    }

    [Fact]
    public async Task ListenerDropsWrongTokenAndAcceptsTheRightOne()
    {
        using var listener = new PreviewListener();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var accept = listener.AcceptAsync(cts.Token);

        using var intruder = new TcpClient();
        await intruder.ConnectAsync("127.0.0.1", listener.Port);
        PreviewWire.Write(intruder.GetStream(), PreviewWire.Message, """{"type":"hello","token":"wrong"}""");
        await Task.Delay(300);
        Assert.False(accept.IsCompleted);

        using var plugin = new TcpClient();
        await plugin.ConnectAsync("127.0.0.1", listener.Port);
        PreviewWire.Write(plugin.GetStream(), PreviewWire.Message, new JsonObject { ["type"] = "hello", ["token"] = listener.Token, ["mode"] = "preview" }.ToJsonString());
        using var connection = await accept;
        Assert.Equal("preview", (string?)connection.Hello["mode"]);

        var frames = new ConcurrentQueue<PreviewFrame>();
        var messages = new ConcurrentQueue<JsonObject>();
        connection.FrameReceived += frames.Enqueue;
        connection.MessageReceived += messages.Enqueue;
        connection.Start();
        PreviewWire.Write(plugin.GetStream(), PreviewWire.Frame, """{"type":"frame","t":12.5,"seq":3,"w":854,"h":480,"state":"playing"}""", new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
        PreviewWire.Write(plugin.GetStream(), PreviewWire.Message, """{"type":"status","state":"playing","t":12.5}""");
        await Until(() => frames.Count == 1 && messages.Count == 1, what: "frame and status");
        Assert.True(frames.TryPeek(out var frame));
        Assert.Equal((12.5, 3L, 854, 4), (frame!.T, frame.Seq, frame.Width, frame.Image.Length));

        Assert.True(connection.Send(PreviewCommands.Pause()));
        Assert.True(PreviewWire.TryRead(plugin.GetStream(), out _, out var header, out _));
        Assert.Equal("pause", (string?)JsonNode.Parse(header)!["type"]);
    }

    // ---------- job files ----------

    [Fact]
    public void PreviewJobValidatesAndMapsPaths()
    {
        var demo = WriteDemo(dir.Combine("demos"));
        var request = new PreviewRequest { DemoPath = demo, From = 35, To = 51, ShotsJson = """{"shots":[{"t":35,"camera":"chase","target":"host"}]}""" };
        var job = PreviewJobWriter.PreparePreview(request, dir.Combine("jobs"), new PathMapper(proton: true), 4242, "abc");
        var read = PreviewJobFile.FromJson(File.ReadAllText(job.JobFile))!;
        Assert.Equal("preview", read.Mode);
        Assert.StartsWith("Z:\\", read.Demo);
        Assert.Equal(4242, read.Preview!.Port);
        Assert.Equal("abc", read.Preview.Token);
        Assert.Equal("offscreen", read.Window);
        Assert.True(File.Exists(Path.Combine(job.JobDir, "shots.json")));
        Assert.Equal(new[] { "-ncPreview", "-screen-fullscreen", "0", "-screen-width", "854", "-screen-height", "480" },
            job.GameArgs.Where((_, i) => i != 1));

        Assert.Equal("bad_fps", Assert.Throws<NcError>(() => PreviewJobWriter.PreparePreview(request with { Fps = 60 }, dir.Combine("jobs"), new PathMapper(false), 1, "t")).Code);
        Assert.Equal("bad_range", Assert.Throws<NcError>(() => PreviewJobWriter.PreparePreview(request with { To = 30 }, dir.Combine("jobs"), new PathMapper(false), 1, "t")).Code);
        Assert.Equal("bad_size", Assert.Throws<NcError>(() => PreviewJobWriter.PreparePreview(request with { Width = 3840, Height = 2160 }, dir.Combine("jobs"), new PathMapper(false), 1, "t")).Code);
    }

    // ---------- simulated game end to end ----------

    [Fact]
    public async Task SimulatedPreviewLoadsPlaysAndTakesCommands()
    {
        var (env, sim, game) = Simulated(new SimulatedGame { Speed = 10, LoadSeconds = 4, DemoLength = 110 });
        var demo = WriteDemo(dir.Combine("demos"));
        await using var source = new GamePreviewSource(env, game) { Simulated = true, TickInterval = TimeSpan.FromMilliseconds(50) };
        var states = new ConcurrentQueue<string>();
        var frames = new ConcurrentQueue<PreviewFrame>();
        source.StatusChanged += s => { if (states.LastOrDefault() != s.State) states.Enqueue(s.State); };
        source.FrameReceived += frames.Enqueue;

        await source.StartAsync(new PreviewRequest { DemoPath = demo, From = 40, To = 52, Fps = 10 });
        Assert.Equal(1, sim.Launches);
        Assert.Contains("-ncPreview", sim.LastArguments);
        await Until(() => source.Status.State == "playing", what: "playing");
        Assert.Equal(new[] { "launching", "starting", "loading", "seeking", "playing" }, states.Take(5));
        await Until(() => frames.Any(f => f.State == "playing" && f.T >= 41), what: "playing frames");
        Assert.All(frames.Where(f => f.State == "playing"), f => Assert.InRange(f.T, 39.9, 52.1));

        Assert.True(source.Send(PreviewCommands.Pause()));
        await Until(() => source.Status.State == "paused", what: "paused");
        Assert.True(source.Send(PreviewCommands.Shots("""{"shots":[{"t":0,"camera":"orbit","target":"auto"}]}""")));
        Assert.True(source.Send(PreviewCommands.Play()));
        await Until(() => source.Status.State == "ended", what: "ended");
        Assert.Contains(sim.Commands, c => c.Contains("\"shots\""));

        // Going back in time reloads the match ("restarting"), then plays the new range.
        Assert.True(source.Send(PreviewCommands.Range(20, 24)));
        await Until(() => states.Contains("restarting"), what: "restarting");
        await Until(() => source.Status.State is "playing" or "ended" && source.Status.From == 20, what: "new range");

        await source.StopAsync();
        Assert.Equal("closed", source.Status.State);
        await Until(() => !sim.Running, what: "simulator to quit");
    }

    [Fact]
    public async Task SimulatedPreviewReportsLoadErrors()
    {
        var (env, sim, game) = Simulated(new SimulatedGame { Speed = 20, LoadSeconds = 2, FailWhileLoading = "could not load the recording: format version 9 is newer than this mod" });
        var demo = WriteDemo(dir.Combine("demos"));
        await using var source = new GamePreviewSource(env, game) { TickInterval = TimeSpan.FromMilliseconds(50), QuitGrace = TimeSpan.FromSeconds(2) };
        await source.StartAsync(new PreviewRequest { DemoPath = demo, From = 40, To = 52 });
        await Until(() => source.Status.State == "error", what: "error");
        Assert.Contains("format version 9", source.Status.Error);
        Assert.False(string.IsNullOrEmpty(source.Status.Fix));
        Assert.StartsWith("Preview failed:", source.Status.Describe());
        await Until(() => !sim.Running, what: "game closed after the error");
    }

    [Fact]
    public async Task PreviewGivesUpWhenTheGameNeverConnects()
    {
        var (env, sim, game) = Simulated(new SimulatedGame { Speed = 20, NeverConnect = true });
        var demo = WriteDemo(dir.Combine("demos"));
        await using var source = new GamePreviewSource(env, game) { TickInterval = TimeSpan.FromMilliseconds(50), ConnectTimeout = TimeSpan.FromSeconds(1) };
        await source.StartAsync(new PreviewRequest { DemoPath = demo, From = 40, To = 52 });
        await Until(() => source.Status.State == "error", what: "connect timeout");
        Assert.Contains("did not start the preview", source.Status.Error);
        await Until(() => !sim.Running, what: "game closed");
    }

    [Fact]
    public async Task PreviewRefusesWhenTheGameIsAlreadyRunning()
    {
        var env = new NcEnvironment(new AppPaths(dir.Combine("c"), dir.Combine("d")), new GameLauncher(() => true, () => true, _ => null)) { GameRunning = () => true };
        var source = new GamePreviewSource(env, SimulatedGame.StubInstall(dir.Combine("game")));
        var error = await Assert.ThrowsAsync<NcError>(() => source.StartAsync(new PreviewRequest { DemoPath = WriteDemo(dir.Combine("demos")), From = 1, To = 2 }));
        Assert.Equal("game_running", error.Code);
    }

    [Fact]
    public void StatusDescribesTheWaitInPlainEnglish()
    {
        Assert.Equal("Loading the match. This takes a few minutes (1:12)", new PreviewStatus { State = "loading", Elapsed = TimeSpan.FromSeconds(72) }.Describe());
        Assert.Equal("Skipping ahead to 4:12 (37%)", new PreviewStatus { State = "seeking", From = 252, Fraction = 0.37 }.Describe());
        Assert.Equal("1:01:05", PreviewStatus.Clock(3665));
        var status = PreviewStatus.FromMessage(JsonNode.Parse("""{"state":"paused","t":12.25,"from":10,"to":20,"paused":true,"end":300}""")!.AsObject(), TimeSpan.Zero);
        Assert.True(status.IsLive);
        Assert.True(status.Paused);
        Assert.Equal(300, status.End);
    }

    // ---------- thumbnails ----------

    [Fact]
    public async Task ThumbnailsArePlannedRunAndCached()
    {
        var (env, sim, game) = Simulated(new SimulatedGame { Speed = 20, LoadSeconds = 1 });
        var demo = WriteDemo(dir.Combine("demos"));
        var details = env.Library.Get(demo);
        var service = new ThumbnailService(env);
        var plan = service.Plan(details);
        // launch and kill, and the match card reuses the kill picture
        Assert.Equal(new[] { "event-2", "event-3" }, plan.Items.Select(i => i.Id));
        Assert.Equal(3, plan.MatchFromEvent);
        var kill = plan.Items.Single(i => i.Id == "event-3");
        Assert.Equal(("orbit", "unit:201"), (kill.Camera, kill.Target));
        Assert.Equal(35.4, kill.T, 3); // replay time: the kill annotation is 35 s after the first message
        var shots = JsonNode.Parse(plan.ShotsJson)!["shots"]!.AsArray();
        Assert.Equal(2, shots.Count);
        Assert.True((double)shots[1]!["t"]! > (double)plan.Items[0].T); // never moves the camera before the previous picture

        var run = await service.RunAsync(plan, game);
        Assert.True(run.Ok, run.Final.Description);
        Assert.Equal(2, run.Written.Count);
        Assert.NotNull(service.EventThumb(details.Summary, 3));
        Assert.NotNull(service.MatchThumb(details.Summary));
        Assert.Empty(service.Plan(details).Items); // cached
        Assert.Equal(2, service.Plan(details, onlyMissing: false).Items.Count);

        // A changed file gets a new cache key.
        var key = ThumbnailService.Key(details.Summary);
        File.SetLastWriteTimeUtc(demo, DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(key, ThumbnailService.Key(env.Library.Get(demo).Summary));

        Assert.Equal("bad_event", Assert.Throws<NcError>(() => service.Plan(details, new[] { 99 })).Code);
    }

    // ---------- nr preview / nr thumbs ----------

    private (int Code, JsonObject Result, List<JsonObject> Progress) Nc(NcEnvironment env, TextReader? stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var code = Program.Run(args.Append("--json").ToArray(), env, stdout, new StringWriter(), stdin ?? new StringReader(""));
        var lines = stdout.ToString().Split('\n');
        var progress = lines.Where(l => l.StartsWith("{\"type\":\"progress\"")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        var result = JsonNode.Parse(string.Join('\n', lines.Where(l => !l.StartsWith("{\"type\":\"progress\""))))!.AsObject();
        Assert.Equal(code == 0, result["ok"]!.GetValue<bool>());
        return (code, result, progress);
    }

    private NcEnvironment CliEnv() => new(new AppPaths(dir.Combine("home", "config"), dir.Combine("home", "data")),
        new GameLauncher(() => false, () => true, _ => throw new InvalidOperationException("tests never start the game"))) { GameRunning = () => false };

    [Fact]
    public void NcPreviewSimulatedWritesFrames()
    {
        var env = CliEnv();
        var demo = WriteDemo(dir.Combine("demos"));
        var (code, result, progress) = Nc(env, null, "preview", demo, "--event", "3", "--pre", "2", "--post", "2", "--simulate", "--simulate-speed", "20", "--fps", "10", "--mjpeg", "0");
        Assert.Equal(0, code);
        var data = result["data"]!;
        Assert.True(data["simulated"]!.GetValue<bool>());
        Assert.Equal("ended", data["stopped_by"]!.GetValue<string>());
        Assert.InRange(data["frames"]!.GetValue<long>(), 5, 1000);
        Assert.True(File.Exists(data["latest"]!.GetValue<string>()));
        Assert.Contains(Directory.GetFiles(data["out_dir"]!.GetValue<string>()), f => Path.GetFileName(f) == "frame-000001.jpg");
        Assert.StartsWith("http://127.0.0.1:", data["mjpeg"]!.GetValue<string>());
        Assert.Contains(progress, p => (string?)p["state"] == "loading" && ((string?)p["message"])!.StartsWith("Loading the match"));
        Assert.Contains(progress, p => (string?)p["state"] == "playing");
    }

    [Fact]
    public void NcPreviewTakesCommandsOnStdin()
    {
        var env = CliEnv();
        var demo = WriteDemo(dir.Combine("demos"));
        // Commands wait until the game is connected: the reader below blocks until the first frame file exists.
        var reader = new GatedReader(() => Directory.Exists(dir.Combine("home", "data", "jobs")) &&
                Directory.GetFiles(dir.Combine("home", "data", "jobs"), "latest.jpg", SearchOption.AllDirectories).Length > 0,
            "{\"type\":\"pause\"}", "not json", "{\"type\":\"shots\",\"shots\":[{\"t\":0,\"camera\":\"orbit\",\"target\":\"auto\"}]}", "{\"type\":\"quit\"}");
        var (code, result, progress) = Nc(env, reader, "preview", demo, "--at", "45", "--simulate", "--simulate-speed", "20", "--commands", "--latest-only");
        Assert.Equal(0, code);
        Assert.Equal("quit", result["data"]!["stopped_by"]!.GetValue<string>());
        Assert.Contains(progress, p => (string?)p["state"] == "warning" && ((string?)p["message"])!.Contains("not json"));
        Assert.DoesNotContain(Directory.GetFiles(result["data"]!["out_dir"]!.GetValue<string>()), f => Path.GetFileName(f).StartsWith("frame-"));
    }

    [Fact]
    public void NcPreviewDryRunAndErrors()
    {
        var env = CliEnv();
        var demo = WriteDemo(dir.Combine("demos"));
        var (code, result, _) = Nc(env, null, "preview", demo, "--event", "3", "--simulate", "--dry-run", "--style", "orbit");
        Assert.Equal(0, code);
        Assert.Contains("-ncPreview", result["data"]!["launch"]!.GetValue<string>());
        Assert.Equal("preview", result["data"]!["job"]!["mode"]!.GetValue<string>());

        (code, result, _) = Nc(env, null, "preview", demo, "--simulate", "--dry-run");
        Assert.Equal(2, code);
        Assert.Equal("no_moment", result["error"]!["code"]!.GetValue<string>());
        (code, result, _) = Nc(env, null, "preview", demo, "--event", "3", "--simulate", "--fps", "60");
        Assert.Equal(2, code);
        Assert.Equal("bad_fps", result["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void NcThumbsSimulatedUsesItsOwnCache()
    {
        var env = CliEnv();
        var demo = WriteDemo(dir.Combine("demos"));
        var (code, result, progress) = Nc(env, null, "thumbs", demo, "--simulate", "--simulate-speed", "20");
        Assert.Equal(0, code);
        var data = result["data"]!;
        Assert.Equal(2, data["written"]!.AsArray().Count);
        Assert.Contains("thumbs-simulated", data["cache_dir"]!.GetValue<string>());
        Assert.True(File.Exists(data["match"]!.GetValue<string>()));
        Assert.Contains(progress, p => (string?)p["state"] == "planned");

        (code, result, _) = Nc(env, null, "thumbs", demo, "--simulate", "--simulate-speed", "20");
        Assert.Equal(0, code);
        Assert.Empty(result["data"]!["planned"]!.AsArray());

        // The real cache is untouched; --list never starts anything.
        (code, result, _) = Nc(env, null, "thumbs", demo, "--list");
        Assert.Equal(0, code);
        Assert.Null(result["data"]!["match"]);
        Assert.All(result["data"]!["events"]!.AsArray(), e => Assert.Null(e!["path"]));
    }

    /// <summary>A stdin stand-in that hands out its lines only once a condition holds (the game is streaming).</summary>
    private sealed class GatedReader : TextReader
    {
        private readonly Func<bool> ready;
        private readonly Queue<string> lines;
        public GatedReader(Func<bool> ready, params string[] lines)
        {
            this.ready = ready;
            this.lines = new Queue<string>(lines);
        }

        public override string? ReadLine()
        {
            var end = DateTime.UtcNow.AddSeconds(30);
            while (!ready() && DateTime.UtcNow < end) Thread.Sleep(20);
            Thread.Sleep(50);
            return lines.Count > 0 ? lines.Dequeue() : null;
        }
    }
}
