using System.IO.Compression;
using System.Text.Json.Nodes;
using NuclearReplay.Cli;
using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Game;
using NuclearReplay.Core.Shots;

namespace NuclearReplay.Core.Tests;

/// <summary>Runs the nr command tree in-process against a fake game and a private settings folder.</summary>
public class CliTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly NcEnvironment env;
    private readonly string game;

    public CliTests()
    {
        game = dir.FakeGame();
        env = new NcEnvironment(new AppPaths(dir.Combine("home", "config"), dir.Combine("home", "data")),
            new GameLauncher(() => false, () => true, _ => throw new InvalidOperationException("tests never start the game")),
            // Never the real network: nr install would otherwise download ffmpeg (FfmpegCliTests covers that with a fake).
            httpHandler: new FakeHttp { Offline = true })
        {
            GameRunning = () => false,
        };
    }

    public void Dispose() => dir.Dispose();

    private (int Code, JsonObject Json) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var code = Program.Run(args.Append("--json").ToArray(), env, stdout, new StringWriter());
        // NDJSON progress lines (single-line, "type":"progress") come first; the result document is everything after them.
        var lines = stdout.ToString().Split('\n').Where(l => !l.StartsWith("{\"type\":\"progress\"")).ToArray();
        var result = JsonNode.Parse(string.Join('\n', lines))!.AsObject();
        Assert.Equal(code == 0, result["ok"]!.GetValue<bool>());
        return (code, result);
    }

    private string DemoDir()
    {
        var demos = Path.Combine(game, "NuclearReplay", "demos");
        Directory.CreateDirectory(demos);
        return demos;
    }

    private string WriteDemo(string name, bool events, bool hitTime = false)
    {
        var builder = new DemoFile.Builder("""{"format":"nuclear-context-network-demo","format_version":1,"mission_name":"Escalation","game_state":"Multiplayer"}""")
            .Annotation(10, """{"event":"ghost_authenticated"}""")
            .Message(10.5, new byte[] { 0x65, 0xaa, 1, 2, 3 });
        if (events)
        {
            builder.Annotation(11, """{"event":"mission","name":"Escalation","map":"Heartland"}""")
                .Annotation(40, """{"event":"launch","shooter":{"id":169,"name":"Revoker","player":"Talamander"},"target":{"id":201,"name":"Ifrit"},"weapon":"AAM-29","missile":4021}""")
                .Annotation(45, hitTime
                    ? """{"event":"kill","victim":{"id":201,"name":"Ifrit"},"killer":{"id":169,"name":"Revoker","player":"Talamander"},"weapon":"AAM-29","hit_ago":2.5}"""
                    : """{"event":"kill","victim":{"id":201,"name":"Ifrit"},"killer":{"id":169,"name":"Revoker","player":"Talamander"},"weapon":"AAM-29"}""");
        }
        builder.Message(110, new byte[] { 0x65, 0xaa }).Annotation(110, """{"event":"end","reason":"shutdown"}""");
        var path = Path.Combine(DemoDir(), name);
        File.WriteAllBytes(path, builder.ToArray());
        return path;
    }

    [Fact]
    public void InstallIntoFakeGame()
    {
        var plugins = dir.Combine("plugins");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "NuclearReplay.dll"), "r");
        var zip = dir.Combine("bepinex.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "winhttp.dll", "doorstop_config.ini", "BepInEx/core/BepInEx.dll" })
                using (var w = new StreamWriter(archive.CreateEntry(name).Open())) w.Write(name);
        }

        var (code, json) = Run("install", "--game-dir", game, "--from", plugins, "--bepinex", zip, "--no-proton-fix");
        Assert.Equal(0, code);
        Assert.Equal(game, json["data"]!["game_dir"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(ModInstaller.PluginDir(game), "NuclearReplay.dll")));
        var summary = json["data"]!["summary"]!.AsArray().Select(l => l!.GetValue<string>()).ToList();
        Assert.Contains("Installed the BepInEx 5 mod loader (3 files) into the game folder.", summary);
        // --game-dir is not saved.
        Assert.Null(env.LoadSettings().GameDir);
    }

    [Fact]
    public void InstallReportsPlainEnglishErrors()
    {
        var (code, json) = Run("install", "--game-dir", dir.Combine("nowhere"));
        Assert.Equal(1, code);
        Assert.Equal("game_not_found", json["error"]!["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(json["error"]!["fix"]!.GetValue<string>()));
    }

    [Fact]
    public void ListAlsoReadsRecordingsFromBeforeTheRename()
    {
        WriteDemo("20261004T021932Z-Escalation.ncdemo", events: true);
        var legacy = Path.Combine(game, "NuclearContext", "demos");
        Directory.CreateDirectory(legacy);
        File.Move(WriteDemo("20260901T100000Z-Old.ncdemo", events: false), Path.Combine(legacy, "20260901T100000Z-Old.ncdemo"));
        WriteDemo("20261005T100000Z-New.ncdemo", events: false);

        var (code, list) = Run("list", "--game-dir", game);
        Assert.Equal(0, code);
        Assert.Equal(3, list["data"]!["count"]!.GetValue<int>());
        Assert.Equal(legacy, list["data"]!["extra_dirs"]![0]!.GetValue<string>());
        var names = list["data"]!["demos"]!.AsArray().Select(d => d!["file_name"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "20261005T100000Z-New.ncdemo", "20261004T021932Z-Escalation.ncdemo", "20260901T100000Z-Old.ncdemo" }, names);
        Assert.True(File.Exists(Path.Combine(legacy, "20260901T100000Z-Old.ncdemo"))); // read, never moved

        // "latest" and names resolve in the old folder too; --dir lists only that folder.
        Assert.Equal(Path.Combine(legacy, "20260901T100000Z-Old.ncdemo"), env.ResolveDemo("Old", env.LoadSettings(), new GameInstall { GameDir = game, Source = "manual" }));
        var (_, only) = Run("list", "--dir", DemoDir());
        Assert.Equal(2, only["data"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public void ListAndEvents()
    {
        WriteDemo("20261001T100000Z-Old.ncdemo", events: false);
        WriteDemo("20261004T021932Z-Escalation.ncdemo", events: true);

        var (code, list) = Run("list", "--game-dir", game);
        Assert.Equal(0, code);
        Assert.Equal(2, list["data"]!["count"]!.GetValue<int>());
        var first = list["data"]!["demos"]![0]!;
        Assert.Equal("20261004T021932Z-Escalation.ncdemo", first["file_name"]!.GetValue<string>());
        Assert.Equal("Heartland", first["map"]!.GetValue<string>());
        Assert.Equal(100, first["duration_s"]!.GetValue<double>(), 3);
        Assert.Equal(1, first["missile_hits"]!.GetValue<int>());

        var (_, events) = Run("events", "latest", "--game-dir", game, "--moments");
        var moments = events["data"]!["events"]!.AsArray();
        Assert.Equal(2, moments.Count);
        Assert.Equal(30, moments[0]!["t"]!.GetValue<double>(), 3);
        Assert.Equal("unit:169", moments[0]!["camera_target"]!.GetValue<string>());

        var (_, old) = Run("events", "Old", "--game-dir", game);
        Assert.NotNull(old["data"]!["note"]);
    }

    [Fact]
    public void ShotsValidateUsesExitCodeTwoForInvalidFiles()
    {
        var good = dir.Write("good.json", """{"shots":[{"t":0,"camera":"orbit","target":"host"}]}""");
        var bad = dir.Write("bad.json", """{"shots":[{"t":0,"camera":"drone","target":"host"}]}""");
        Assert.Equal(0, Run("shots", "validate", good).Code);
        var (code, json) = Run("shots", "validate", bad);
        Assert.Equal(2, code);
        Assert.Contains(json["data"]!["issues"]!.AsArray(), i => i!["path"]!.GetValue<string>() == "$.shots[0].camera");
    }

    [Fact]
    public void RenderDryRunWritesJobWithoutStartingTheGame()
    {
        var demo = WriteDemo("20261004T021932Z-Escalation.ncdemo", events: true);
        var outDir = dir.Combine("clips");
        var (code, json) = Run("render", "latest", "--game-dir", game, "--event", "0", "--style", "cinematic_chase",
            "--size", "vertical", "--fps", "30", "--out", outDir, "--dry-run");
        Assert.Equal(0, code);
        var data = json["data"]!;
        Assert.True(data["dry_run"]!.GetValue<bool>());
        Assert.False(Directory.Exists(outDir)); // dry runs create no output folder
        var job = data["job"]!;
        Assert.Equal(1080, job["width"]!.GetValue<int>());
        Assert.Equal(1920, job["height"]!.GetValue<int>());
        Assert.Equal(30, job["ranges"]![0]!["fps"]!.GetValue<int>());
        Assert.True(File.Exists(data["job_file"]!.GetValue<string>()));
        if (!OperatingSystem.IsWindows())
        {
            Assert.StartsWith("Z:", job["demo"]!.GetValue<string>());
            Assert.Contains("-applaunch 2168680 -ncRenderJob", data["launch"]!.GetValue<string>());
        }
        Assert.Equal(Path.GetFullPath(demo), Path.GetFullPath(new PathMapper(!OperatingSystem.IsWindows()).ToHostPath(job["demo"]!.GetValue<string>())));
    }

    [Fact]
    public void RenderRefusesWhileTheGameIsRunning()
    {
        WriteDemo("20261004T021932Z-Escalation.ncdemo", events: true);
        var ffmpeg = dir.Combine("tools", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ffmpeg)!);
        File.WriteAllText(ffmpeg, "");
        var running = new NcEnvironment(new AppPaths(dir.Combine("home", "config"), dir.Combine("home", "data")),
            new GameLauncher(() => true, () => true, _ => throw new InvalidOperationException("tests never start the game")))
        {
            GameRunning = () => true,
        };
        Assert.Equal(0, Program.Run(new[] { "settings", "set", "ffmpeg_path", ffmpeg, "--json" }, running, new StringWriter(), new StringWriter()));
        var stdout = new StringWriter();
        var code = Program.Run(new[] { "render", "latest", "--game-dir", game, "--event", "0", "--json" }, running, stdout, new StringWriter());
        var json = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(1, code);
        Assert.Equal("game_running", json["error"]!["code"]!.GetValue<string>());
        Assert.Contains("Close the game", json["error"]!["fix"]!.GetValue<string>());
        // A dry run only writes the job, so it still works while someone plays.
        Assert.Equal(0, Program.Run(new[] { "render", "latest", "--game-dir", game, "--event", "0", "--dry-run", "--json" }, running, new StringWriter(), new StringWriter()));
    }

    [Fact]
    public void RenderAtFramesTheClipAroundASecond()
    {
        WriteDemo("20261004T021932Z-Escalation.ncdemo", events: true);
        var (code, json) = Run("render", "Escalation", "--game-dir", game, "--at", "50", "--pre", "2", "--post", "4", "--style", "orbit", "--dry-run");
        Assert.Equal(0, code);
        var range = json["data"]!["job"]!["ranges"]![0]!;
        Assert.Equal(48, range["from"]!.GetValue<double>(), 3);
        Assert.Equal(54, range["to"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void KillWithHitTimeIsListedAndFramedAtTheImpact()
    {
        WriteDemo("20261004T021932Z-Escalation.ncdemo", events: true, hitTime: true);
        var (_, events) = Run("events", "latest", "--game-dir", game, "--moments");
        var kill = events["data"]!["events"]!.AsArray().First(e => e!["event"]!.GetValue<string>() == "kill")!;
        // Replay time starts at the first record (10 s): the kill feed is at 35 s, the hit 2.5 s earlier.
        Assert.Equal(32.5, kill["t"]!.GetValue<double>(), 3);
        Assert.Equal(35, kill["kill_feed_t"]!.GetValue<double>(), 3);
        Assert.Equal(2.5, kill["data"]!["hit_ago"]!.GetValue<double>(), 3);

        var (code, json) = Run("render", "latest", "--game-dir", game, "--event", kill["index"]!.ToString(), "--style", "victim_cut", "--dry-run");
        Assert.Equal(0, code);
        var range = json["data"]!["job"]!["ranges"]![0]!;
        Assert.Equal(27.5, range["from"]!.GetValue<double>(), 3);
        Assert.Equal(38.5, range["to"]!.GetValue<double>(), 3);

        // An old recording without the hit keeps the kill feed time and no kill_feed_t.
        WriteDemo("20261003T021932Z-Old.ncdemo", events: true);
        var (_, old) = Run("events", "Old", "--game-dir", game, "--moments");
        var oldKill = old["data"]!["events"]!.AsArray().First(e => e!["event"]!.GetValue<string>() == "kill")!;
        Assert.Equal(35, oldKill["t"]!.GetValue<double>(), 3);
        Assert.Null(oldKill["kill_feed_t"]);
    }

    [Fact]
    public void ShotsMakeWritesTheStyleShotsToAFile()
    {
        WriteDemo("20261004T021932Z-Escalation.ncdemo", events: true);
        var (_, events) = Run("events", "latest", "--game-dir", game, "--moments");
        var kill = events["data"]!["events"]!.AsArray().First(e => e!["event"]!.GetValue<string>() == "kill")!;
        var outFile = dir.Combine("out", "kill.shots.json");
        var (code, _) = Run("shots", "make", "latest", "--game-dir", game, "--event", kill["index"]!.ToString(), "--style", "victim_cut", "--out", outFile);
        Assert.Equal(0, code);
        var shots = ShotValidator.Validate(File.ReadAllText(outFile));
        Assert.True(shots.Valid);
        Assert.Contains(shots.ShotList!.Shots, s => s.Camera == "orbit" && s.Target == "unit:201");
        Assert.Contains(shots.ShotList.Shots, s => s.Target == "unit:4021");

        var (bad, err) = Run("shots", "make", "latest", "--game-dir", game, "--event", "0", "--style", "nope");
        Assert.Equal(2, bad);
        Assert.False(string.IsNullOrEmpty(err["error"]!["fix"]?.GetValue<string>()));
    }

    [Fact]
    public void WatchAtPassesStartToThePlugin()
    {
        WriteDemo("w.ncdemo", events: true);
        var (code, json) = Run("watch", "w", "--game-dir", game, "--at", "42.5", "--dry-run");
        Assert.Equal(0, code);
        var args = json["data"]!["arguments"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        int i = args.IndexOf("-ncStart");
        Assert.True(i >= 0);
        Assert.Equal("42.5", args[i + 1]);
        Assert.Equal(42.5, json["data"]!["start_at"]!.GetValue<double>());
    }

    [Fact]
    public void WatchEventStartsBeforeTheMoment()
    {
        WriteDemo("w.ncdemo", events: true);
        var (_, events) = Run("events", "w", "--game-dir", game, "--moments");
        var launch = events["data"]!["events"]!.AsArray().First(e => e!["event"]!.GetValue<string>() == "launch")!;
        int index = launch["index"]!.GetValue<int>();
        double t = launch["t"]!.GetValue<double>();
        var (code, json) = Run("watch", "w", "--game-dir", game, "--event", index.ToString(), "--pre", "3", "--dry-run");
        Assert.Equal(0, code);
        Assert.Equal(t - 3, json["data"]!["start_at"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void WatchWithoutStartPlaysFromTheBeginning()
    {
        WriteDemo("w.ncdemo", events: false);
        var (code, json) = Run("watch", "w", "--game-dir", game, "--dry-run");
        Assert.Equal(0, code);
        Assert.DoesNotContain("-ncStart", json["data"]!["arguments"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void WatchRejectsStartAfterTheEnd()
    {
        WriteDemo("w.ncdemo", events: false);
        var (code, json) = Run("watch", "w", "--game-dir", game, "--at", "5000", "--dry-run");
        Assert.Equal(2, code);
        Assert.Equal("bad_start", json["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void OpenDryRunPassesViewerArguments()
    {
        WriteDemo("w.ncdemo", events: true);
        var (code, json) = Run("open", "w", "--game-dir", game, "--at", "35", "--focus", "40.25", "--dry-run");
        Assert.Equal(0, code);
        Assert.Equal("open", json["command"]!.GetValue<string>());
        var data = json["data"]!;
        Assert.True(data["dry_run"]!.GetValue<bool>());
        Assert.Equal(35, data["at"]!.GetValue<double>());
        Assert.Equal(40.25, data["focus"]!.GetValue<double>());
        Assert.EndsWith("w.ncdemo", data["demo"]!.GetValue<string>());
        var clipsDir = data["clips_dir"]!.GetValue<string>();
        Assert.True(Path.IsPathRooted(clipsDir));
        Assert.False(string.IsNullOrEmpty(data["launch"]!.GetValue<string>()));
        var args = data["arguments"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        int replay = args.IndexOf("-ncReplay");
        Assert.True(replay >= 0);
        Assert.EndsWith("w.ncdemo", args[replay + 1]);
        Assert.Contains("-ncViewer", args);
        Assert.Equal("35", args[args.IndexOf("-ncAt") + 1]);
        Assert.Equal("40.25", args[args.IndexOf("-ncFocus") + 1]);
        Assert.EndsWith(Path.GetFileName(clipsDir), args[args.IndexOf("-ncClipsDir") + 1].Replace('\\', '/'));
        Assert.DoesNotContain("-ncShots", args);
        Assert.DoesNotContain("-ncStart", args);
        // The plugin encodes viewer clips itself with the saved encoder settings.
        Assert.Equal("19", args[args.IndexOf("-ncQuality") + 1]);
        Assert.Equal("auto", args[args.IndexOf("-ncEncoder") + 1]);
        Assert.True(args.IndexOf("-ncQuality") > args.IndexOf("-ncClipsDir"));
    }

    [Fact]
    public void OpenPassesTheFfmpegSetting()
    {
        WriteDemo("w.ncdemo", events: false);
        var ffmpeg = dir.Write("tools/ffmpeg.exe", "x");
        Run("settings", "set", "ffmpeg_path", ffmpeg);
        Run("settings", "set", "encoder", "vulkan");
        var (code, json) = Run("open", "w", "--game-dir", game, "--dry-run");
        Assert.Equal(0, code);
        var args = json["data"]!["arguments"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.EndsWith("ffmpeg.exe", args[args.IndexOf("-ncFfmpeg") + 1]);
        Assert.Equal("vulkan", args[args.IndexOf("-ncEncoder") + 1]);
    }

    [Fact]
    public void OpenWithoutTimesStartsAtTheBeginning()
    {
        WriteDemo("w.ncdemo", events: false);
        var (code, json) = Run("open", "w", "--game-dir", game, "--dry-run");
        Assert.Equal(0, code);
        var args = json["data"]!["arguments"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Contains("-ncViewer", args);
        Assert.Contains("-ncClipsDir", args);
        Assert.DoesNotContain("-ncAt", args);
        Assert.DoesNotContain("-ncFocus", args);
        Assert.Null(json["data"]!["at"]);
    }

    [Fact]
    public void OpenRejectsBadTimesAndARunningGame()
    {
        WriteDemo("w.ncdemo", events: false);
        var (code, json) = Run("open", "w", "--game-dir", game, "--focus", "5000", "--dry-run");
        Assert.Equal(2, code);
        Assert.Equal("bad_start", json["error"]!["code"]!.GetValue<string>());
        (code, json) = Run("open", "missing-match", "--game-dir", game, "--dry-run");
        Assert.NotEqual(0, code);

        var running = new NcEnvironment(env.Paths, new GameLauncher(() => true, () => true, _ => throw new InvalidOperationException("tests never start the game")))
        {
            GameRunning = () => true,
        };
        var stdout = new StringWriter();
        code = Program.Run(new[] { "open", "w", "--game-dir", game, "--json" }, running, stdout, new StringWriter());
        Assert.Equal("game_running", JsonNode.Parse(stdout.ToString())!["error"]!["code"]!.GetValue<string>());
        Assert.NotEqual(0, code);
    }

    [Fact]
    public void OpenLineMatchesTheCommand()
    {
        Assert.Equal("nr open w.ncdemo --at 35.125 --focus 40", Clips.ClipCommand.Line(Clips.ClipCommand.OpenArgs("w.ncdemo", 35.125, 40), windows: false));
        Assert.Equal("nr open w.ncdemo", Clips.ClipCommand.Line(Clips.ClipCommand.OpenArgs("w.ncdemo", 0, null), windows: false));
        Assert.Equal(Clips.ClipCommand.Line(Clips.ClipCommand.OpenArgs("w.ncdemo", 3, 8)), Clips.ClipCommand.Open("w.ncdemo", 3, 8));
    }

    [Fact]
    public void RenderRejectsRangesPastTheEnd()
    {
        WriteDemo("x.ncdemo", events: false);
        var (code, json) = Run("render", "x", "--game-dir", game, "--range", "500:510", "--dry-run");
        Assert.NotEqual(0, code);
        Assert.False(json["ok"]!.GetValue<bool>());
    }
}
