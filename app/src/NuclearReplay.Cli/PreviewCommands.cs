using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NuclearReplay.Core;
using NuclearReplay.Core.Clips;
using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Game;
using NuclearReplay.Core.Preview;
using NuclearReplay.Core.Render;

namespace NuclearReplay.Cli;

/// <summary>nr preview and nr thumbs: frames streamed from a replay, and moment pictures, for scripts and agents.</summary>
public sealed partial class Commands
{
    // ---------- shared ----------

    /// <summary>
    /// The environment and game a preview or thumbnail run uses. With --simulate a <see cref="SimulatedGame"/> stands in
    /// for Nuclear Option: same job files, same socket protocol, labelled pictures, no game start.
    /// </summary>
    private (NcEnvironment Env, GameInstall Game, SimulatedGame? Sim) PreviewTarget(Settings settings, bool simulate, double speed, double demoLength)
    {
        if (!simulate) return (env, RequireGame(env, settings), null);
        if (speed <= 0 || !double.IsFinite(speed)) throw new CliError("bad_speed", "--simulate-speed must be more than 0.", null, ExitCodes.Invalid);
        var sim = new SimulatedGame { Speed = speed, DemoLength = demoLength > 0 ? demoLength : 3600 };
        var simEnv = new NcEnvironment(env.Paths, sim.CreateLauncher(), env.Status) { GameRunning = () => sim.Running, StopGame = sim.Kill };
        var game = SimulatedGame.StubInstall(Path.Combine(env.Paths.DataDir, "simulated-game"));
        return (simEnv, game, sim);
    }

    private static JsonObject StatusJson(PreviewStatus s)
    {
        var o = new JsonObject
        {
            ["state"] = s.State,
            ["t"] = Math.Round(s.T, 3),
            ["from"] = Math.Round(s.From, 3),
            ["to"] = Math.Round(s.To, 3),
            ["paused"] = s.Paused,
            ["elapsed_s"] = Math.Round(s.Elapsed.TotalSeconds, 1),
            ["message"] = s.Describe(),
        };
        if (s.End is { } end) o["end"] = Math.Round(end, 3);
        if (s.Fraction is { } f) o["fraction"] = Math.Round(f, 3);
        if (s.Error != null) o["error"] = s.Error;
        if (s.Fix != null) o["fix"] = s.Fix;
        if (s.GrabMs is { } grab) o["grab_ms"] = grab;
        if (s.EncodeMs is { } encode) o["encode_ms"] = encode;
        if (s.GameFps is { } gameFps) o["game_fps"] = gameFps;
        return o;
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var part = path + ".part";
        File.WriteAllBytes(part, data);
        File.Move(part, path, overwrite: true);
    }

    // ---------- preview ----------

    private Command PreviewCommand()
    {
        var demo = new Argument<string>("demo") { Description = "Recording: path, file name from 'nr list', or 'latest'." };
        var eventIndex = new Option<int?>("--event") { Description = "Preview around this event index (from 'nr events --moments')." };
        var at = new Option<double?>("--at") { Description = "Preview around this replay second." };
        var range = new Option<string?>("--range") { Description = "Instead of a moment: replay seconds as from:to." };
        var pre = new Option<double>("--pre") { Description = "With --event/--at: seconds before the moment.", DefaultValueFactory = _ => 5 };
        var post = new Option<double>("--post") { Description = "With --event/--at: seconds after the moment.", DefaultValueFactory = _ => 6 };
        var style = new Option<string?>("--style") { Description = "Camera style: " + Core.Shots.ShotPresets.StyleList + ". Default cinematic_chase." };
        var shots = new Option<string?>("--shots") { Description = "Shot list JSON instead of --style." };
        var size = new Option<string>("--size") { Description = "Frame size WxH, up to 1920x1080.", DefaultValueFactory = _ => "854x480" };
        var fps = new Option<int>("--fps") { Description = "Frames per second from the game, 1 to 30.", DefaultValueFactory = _ => 20 };
        var quality = new Option<int>("--quality") { Description = "JPEG quality, 10 to 95.", DefaultValueFactory = _ => 70 };
        var hud = new Option<bool>("--hud") { Description = "Keep the game HUD in the frames." };
        var vflip = new Option<bool?>("--vflip") { Description = "Flip frames upside down (true/false). Default: the plugin decides from the graphics API." };
        var outDir = new Option<string?>("--out") { Description = "Folder for frames: latest.jpg plus frame-NNNNNN.jpg. Default: frames/ in the job folder." };
        var latestOnly = new Option<bool>("--latest-only") { Description = "Only keep latest.jpg (no numbered frames)." };
        var mjpeg = new Option<int?>("--mjpeg") { Description = "Also serve the frames as MJPEG on http://127.0.0.1:<port>/ (0 picks a free port); /latest.jpg is the newest frame." };
        var maxFrames = new Option<int?>("--max-frames") { Description = "Stop after this many saved frames." };
        var seekFrames = new Option<bool>("--seek-frames") { Description = "Also save the occasional frames sent while the match loads and skips ahead (the MJPEG stream always has them)." };
        var commands = new Option<bool>("--commands") { Description = "Read JSON commands from stdin, one per line ({\"type\":\"pause\"}, {\"type\":\"shots\",\"shots\":[...]} ...). Runs until quit, end of input, or an error." };
        var paused = new Option<bool>("--paused") { Description = "Stop at the start of the range and wait for a play command (with --commands)." };
        var simulate = new Option<bool>("--simulate") { Description = "Use the built-in simulated game instead of Nuclear Option (labelled test pictures, same protocol). For scripts and tests." };
        var simulateSpeed = new Option<double>("--simulate-speed") { Description = "With --simulate: how much faster than real time the simulator runs.", DefaultValueFactory = _ => 10 };
        var showWindow = new Option<bool>("--show-window") { Description = "Leave the game window where it opens instead of moving it off-screen." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Write the job file and print the launch command without starting the game." };
        var cmd = new Command("preview", "Play part of a recording in the game in the background and stream small frames out.");
        cmd.Arguments.Add(demo);
        foreach (var o in new Option[] { eventIndex, at, range, pre, post, style, shots, size, fps, quality, hud, vflip, outDir, latestOnly, mjpeg, maxFrames, seekFrames, commands, paused, simulate, simulateSpeed, showWindow, dryRun })
            cmd.Options.Add(o);
        cmd.SetAction(parse => Execute(parse, "preview", output =>
        {
            var settings = LoadSettings(parse);
            bool simulated = parse.GetValue(simulate);
            var demoPath = RequireDemo(parse.GetValue(demo)!, settings, simulated ? env.FindGame(settings) : RequireGame(env, settings));
            var details = env.Library.Get(demoPath);
            var spec = new ClipSpec
            {
                Demo = demoPath,
                EventIndex = parse.GetValue(eventIndex),
                At = parse.GetValue(at),
                Ranges = parse.GetValue(range),
                Pre = parse.GetValue(pre),
                Post = parse.GetValue(post),
                ShotsPath = parse.GetValue(shots),
                Style = parse.GetValue(shots) == null ? parse.GetValue(style) ?? "cinematic_chase" : null,
            };
            var clip = ClipPlanner.Plan(spec, demoPath, details, settings);
            var (width, height) = ClipPlanner.ParseSize(parse.GetValue(size)!);
            int q = parse.GetValue(quality);
            if (q < 10 || q > 95) throw new CliError("bad_quality", $"JPEG quality {q} is out of range.", "Use 10 to 95.", ExitCodes.Invalid);
            int? max = parse.GetValue(maxFrames);
            if (max is <= 0) throw new CliError("bad_max_frames", "--max-frames must be 1 or more.", null, ExitCodes.Invalid);
            bool readCommands = parse.GetValue(commands);
            var request = new PreviewRequest
            {
                DemoPath = clip.DemoPath,
                From = clip.Ranges[0].From,
                To = clip.Ranges[^1].To,
                ShotsJson = clip.ShotsJson,
                ShotsPath = clip.ShotsPath,
                Width = width, Height = height,
                Fps = parse.GetValue(fps),
                Quality = q,
                Hud = parse.GetValue(hud),
                VFlip = parse.GetValue(vflip),
                Autoplay = !parse.GetValue(paused),
                ShowWindow = parse.GetValue(showWindow),
            };
            var (runEnv, game, sim) = PreviewTarget(settings, simulated, parse.GetValue(simulateSpeed), details.Summary.DurationS);

            if (parse.GetValue(dryRun))
            {
                // The real run picks its port when it starts; the dry run shows the job with a placeholder port.
                var job = PreviewJobWriter.PreparePreview(request, runEnv.Paths.JobsDir, PathMapper.For(game), 0, PreviewJobWriter.NewToken());
                var plan = runEnv.Launcher.Plan(game, job.GameArgs);
                var data = new JsonObject
                {
                    ["dry_run"] = true,
                    ["simulated"] = simulated,
                    ["demo"] = clip.DemoPath,
                    ["from"] = request.From,
                    ["to"] = request.To,
                    ["job_dir"] = job.JobDir,
                    ["job_file"] = job.JobFile,
                    ["launch"] = plan.CommandLine,
                    ["job"] = JsonNode.Parse(job.Job.ToJson()),
                    ["warnings"] = new JsonArray(clip.Warnings.Select(w => (JsonNode)w).ToArray()),
                };
                output.Result("preview", data, w =>
                {
                    w.WriteLine("Dry run. Would run: " + plan.CommandLine);
                    w.WriteLine("Job file: " + job.JobFile);
                });
                return ExitCodes.Ok;
            }

            foreach (var warning in clip.Warnings) output.Progress(new { state = "warning", message = warning }, "Warning: " + warning);
            var source = new GamePreviewSource(runEnv, game) { Simulated = simulated };
            MjpegServer? server = null;
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            long frames = 0;
            string? framesDir = parse.GetValue(outDir) is { } o ? Path.GetFullPath(o) : null;
            bool keepNumbered = !parse.GetValue(latestOnly);
            bool saveSeekFrames = parse.GetValue(seekFrames);
            string lastState = "";
            DateTime lastLine = DateTime.MinValue;
            var frameGate = new object();
            ConsoleCancelEventHandler? onCancel = null;
            try
            {
                if (parse.GetValue(mjpeg) is { } port)
                {
                    if (port < 0 || port > 65535) throw new CliError("bad_port", $"{port} is not a port.", "Use 0 to pick a free one, or 1024 to 65535.", ExitCodes.Invalid);
                    try { server = new MjpegServer(port); }
                    catch (System.Net.Sockets.SocketException e)
                    {
                        throw new CliError("bad_port", $"Cannot serve on port {port}: {e.Message}", "Pick another port, or 0 for a free one.", ExitCodes.Invalid);
                    }
                }
                source.StatusChanged += s =>
                {
                    bool changed = s.State != lastState;
                    if (changed || DateTime.UtcNow - lastLine > TimeSpan.FromSeconds(5))
                    {
                        lastState = s.State;
                        lastLine = DateTime.UtcNow;
                        var line = StatusJson(s);
                        line["frames"] = Interlocked.Read(ref frames);
                        output.Progress(line, $"[{s.State}] {s.Describe()}");
                    }
                    if (s.IsFinal) done.TrySetResult(s.State);
                    else if (s.State == "ended" && !readCommands) done.TrySetResult("ended");
                };
                source.FrameReceived += frame =>
                {
                    if (frame.Image.Length == 0) return;
                    server?.Push(frame.Image);
                    if (!saveSeekFrames && frame.State is not ("ready" or "playing" or "paused" or "ended")) return;
                    long n;
                    lock (frameGate)
                    {
                        if (framesDir == null) return;
                        n = ++frames;
                        if (max != null && n > max) return;
                        WriteAtomic(Path.Combine(framesDir, "latest.jpg"), frame.Image);
                        if (keepNumbered) File.WriteAllBytes(Path.Combine(framesDir, $"frame-{n:000000}.jpg"), frame.Image);
                    }
                    if (max != null && n >= max) done.TrySetResult("max_frames");
                };

                source.StartAsync(request).GetAwaiter().GetResult();
                lock (frameGate)
                {
                    framesDir ??= Path.Combine(source.Job!.JobDir, "frames");
                    Directory.CreateDirectory(framesDir);
                }
                var started = new JsonObject
                {
                    ["state"] = "started",
                    ["simulated"] = simulated,
                    ["job_dir"] = source.Job!.JobDir,
                    ["out_dir"] = framesDir,
                    ["launch"] = source.Plan?.CommandLine,
                    ["from"] = request.From,
                    ["to"] = request.To,
                };
                if (server != null) started["mjpeg"] = server.Url;
                output.Progress(started, (simulated ? "Simulated game started. " : "Nuclear Option is starting in the background. ")
                    + "Frames go to " + framesDir + (server != null ? ", stream at " + server.Url : ""));

                if (readCommands)
                {
                    var reader = stdin ?? Console.In;
                    new Thread(() =>
                    {
                        try
                        {
                            string? line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                JsonObject? command = null;
                                try { command = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
                                string? type = (string?)command?["type"];
                                if (command == null || type == null || !PreviewCommands.Known.Contains(type))
                                {
                                    output.Progress(new { state = "warning", message = "Ignored a command that is not JSON with a known \"type\": " + line },
                                        "Ignored command: " + line);
                                    continue;
                                }
                                if (type == "quit") { done.TrySetResult("quit"); return; }
                                if (!source.Send(command))
                                    output.Progress(new { state = "warning", message = $"The game is not connected yet; \"{type}\" was not sent." }, $"Not connected yet; {type} not sent.");
                            }
                            done.TrySetResult("end_of_input");
                        }
                        catch (Exception e) when (e is IOException or ObjectDisposedException) { done.TrySetResult("end_of_input"); }
                    }) { IsBackground = true, Name = "nc-preview-stdin" }.Start();
                }
                if (stdin == null)
                {
                    onCancel = (_, e) => { e.Cancel = true; done.TrySetResult("cancelled"); };
                    Console.CancelKeyPress += onCancel;
                }

                var reason = done.Task.GetAwaiter().GetResult();
                var last = source.Status;
                source.StopAsync().GetAwaiter().GetResult();
                var result = new JsonObject
                {
                    ["simulated"] = simulated,
                    ["stopped_by"] = reason,
                    ["frames"] = Math.Min(Interlocked.Read(ref frames), max ?? long.MaxValue),
                    ["out_dir"] = framesDir,
                    ["latest"] = File.Exists(Path.Combine(framesDir!, "latest.jpg")) ? Path.Combine(framesDir!, "latest.jpg") : null,
                    ["job_dir"] = source.Job.JobDir,
                    ["job_file"] = source.Job.JobFile,
                    ["launch"] = source.Plan?.CommandLine,
                    ["status"] = StatusJson(last),
                };
                if (server != null) result["mjpeg"] = server.Url;
                if (last.State == "error")
                    throw new CliError("preview_failed", last.Error ?? "The preview failed.", last.Fix ?? "Try again. If it keeps failing, run nr diagnostics.");
                if (last.State == "closed" && reason == "closed" && Interlocked.Read(ref frames) == 0)
                    throw new CliError("preview_failed", last.Message ?? "The game closed before the preview started.", "Try again. If it keeps closing, run nr diagnostics.");
                output.Result("preview", result, w =>
                {
                    w.WriteLine($"{result["frames"]} frames in {framesDir} ({reason}).");
                    if (simulated) w.WriteLine("Simulated: these are test pictures, not game footage.");
                });
                return ExitCodes.Ok;
            }
            finally
            {
                if (onCancel != null) Console.CancelKeyPress -= onCancel;
                if (!source.Status.IsFinal && source.Job != null) source.StopAsync().GetAwaiter().GetResult();
                server?.Dispose();
                _ = sim; // the simulator stops with the source (quit or Kill)
            }
        }));
        return cmd;
    }

    // ---------- thumbs ----------

    private Command ThumbsCommand()
    {
        var demo = new Argument<string>("demo") { Description = "Recording: path, file name from 'nr list', or 'latest'." };
        var events = new Option<int[]>("--event") { Description = "Only these event indexes (repeat the option). Default: every launch and kill.", AllowMultipleArgumentsPerToken = true };
        var noMatch = new Option<bool>("--no-match") { Description = "Skip the match card picture." };
        var all = new Option<bool>("--all") { Description = "Make every picture again, not only the missing ones." };
        var list = new Option<bool>("--list") { Description = "Only list the cached pictures; never starts the game." };
        var size = new Option<string>("--size") { Description = "Picture size WxH.", DefaultValueFactory = _ => "320x180" };
        var simulate = new Option<bool>("--simulate") { Description = "Use the built-in simulated game (labelled test pictures, kept in a separate cache)." };
        var simulateSpeed = new Option<double>("--simulate-speed") { Description = "With --simulate: how much faster than real time the simulator runs.", DefaultValueFactory = _ => 10 };
        var showWindow = new Option<bool>("--show-window") { Description = "Leave the game window where it opens instead of moving it off-screen." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Write the job file and print the launch command without starting the game." };
        var noWait = new Option<bool>("--no-wait") { Description = "Start the game and return; follow progress.json yourself." };
        var cmd = new Command("thumbs", "Make still pictures of a recording's moments and its match card, cached per recording (starts the game in the background).");
        cmd.Arguments.Add(demo);
        foreach (var o in new Option[] { events, noMatch, all, list, size, simulate, simulateSpeed, showWindow, dryRun, noWait }) cmd.Options.Add(o);
        cmd.SetAction(parse => Execute(parse, "thumbs", output =>
        {
            var settings = LoadSettings(parse);
            bool simulated = parse.GetValue(simulate);
            bool listOnly = parse.GetValue(list);
            var demoPath = RequireDemo(parse.GetValue(demo)!, settings, simulated || listOnly ? env.FindGame(settings) : RequireGame(env, settings));
            var details = env.Library.Get(demoPath);
            if (details.Summary.Error != null) throw new CliError("unreadable", details.Summary.Error, "The file is not a Nuclear Replay recording or is damaged.");
            var (width, height) = ClipPlanner.ParseSize(parse.GetValue(size)!);
            var picked = parse.GetValue(events) is { Length: > 0 } e ? e : null;

            NcEnvironment runEnv = env;
            GameInstall? game = null;
            if (!listOnly) (runEnv, game, _) = PreviewTarget(settings, simulated, parse.GetValue(simulateSpeed), details.Summary.DurationS);
            var service = new ThumbnailService(runEnv)
            {
                Width = width, Height = height,
                RootDir = simulated ? ThumbnailService.SimulatedRoot(env.Paths) : null,
            };

            JsonObject Cached()
            {
                var summary = details.Summary;
                var moments = new JsonArray();
                for (int i = 0; i < details.Events.Count; i++)
                {
                    if (picked != null ? !picked.Contains(i) : !details.Events[i].IsMoment) continue;
                    moments.Add(new JsonObject
                    {
                        ["index"] = i, ["t"] = Math.Round(details.Events[i].T, 3), ["event"] = details.Events[i].Event,
                        ["summary"] = details.Events[i].Summary, ["path"] = service.EventThumb(summary, i),
                    });
                }
                return new JsonObject
                {
                    ["demo"] = summary.Path,
                    ["cache_dir"] = service.CacheDir(summary),
                    ["simulated"] = simulated,
                    ["match"] = service.MatchThumb(summary),
                    ["events"] = moments,
                };
            }

            void PrintCached(TextWriter w, JsonObject data)
            {
                w.WriteLine("Match: " + ((string?)data["match"] ?? "(none yet)"));
                foreach (var m in data["events"]!.AsArray())
                    w.WriteLine($"  #{m!["index"],-4} {Output.Duration((double)m["t"]!),8}  {(string?)m["path"] ?? "(none yet)"}");
            }

            if (listOnly)
            {
                var data = Cached();
                output.Result("thumbs", data, w => PrintCached(w, data));
                return ExitCodes.Ok;
            }

            var plan = service.Plan(details, picked, includeMatch: !parse.GetValue(noMatch), onlyMissing: !parse.GetValue(all));
            JsonArray Items(IEnumerable<ThumbTarget> items) => new(items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id, ["event"] = i.EventIndex, ["t"] = i.T, ["out"] = i.Out, ["camera"] = i.Camera, ["target"] = i.Target,
            }).ToArray());
            if (plan.Items.Count == 0)
            {
                var data = Cached();
                data["planned"] = new JsonArray();
                data["message"] = "Every picture is already cached. Pass --all to make them again.";
                output.Result("thumbs", data, w => { w.WriteLine("Every picture is already cached."); PrintCached(w, data); });
                return ExitCodes.Ok;
            }
            bool dry = parse.GetValue(dryRun);
            if (dry || parse.GetValue(noWait))
            {
                if (!dry && runEnv.GameRunning())
                    throw new CliError("game_running", "Nuclear Option is running.", "Close the game, then try again. Thumbnails start it in the background.");
                var job = service.Prepare(plan, game!, parse.GetValue(showWindow));
                var launch = dry ? runEnv.Launcher.Plan(game!, job.GameArgs) : runEnv.Launcher.Launch(game!, job.GameArgs);
                var data = new JsonObject
                {
                    ["dry_run"] = dry,
                    ["simulated"] = simulated,
                    ["planned"] = Items(plan.Items),
                    ["job_dir"] = job.JobDir,
                    ["job_file"] = job.JobFile,
                    ["progress_file"] = job.ProgressFile,
                    ["launch"] = launch.CommandLine,
                    ["job"] = JsonNode.Parse(job.Job.ToJson()),
                };
                if (simulated && !dry) data["note"] = "The simulator runs inside this nr process and stops when it exits; use it without --no-wait.";
                output.Result("thumbs", data, w =>
                {
                    w.WriteLine((dry ? "Dry run. Would run: " : "Started: ") + launch.CommandLine);
                    w.WriteLine($"{plan.Items.Count} pictures planned. Progress: {job.ProgressFile}");
                });
                return ExitCodes.Ok;
            }

            output.Progress(new { state = "planned", count = plan.Items.Count, simulated, planned = Items(plan.Items) },
                $"Making {plan.Items.Count} pictures" + (simulated ? " with the simulated game" : "; Nuclear Option starts in the background and loads the match (a few minutes)") + ".");
            var run = service.RunAsync(plan, game!, p =>
                output.Progress(new { p.State, done = p.Frame, total = p.Frames, p.Error, fraction = Math.Round(p.Fraction, 4) },
                    $"{p.Fraction * 100,5:0.0}%  {p.Description}")).GetAwaiter().GetResult();
            var result = Cached();
            result["written"] = Items(run.Written);
            result["missing"] = Items(run.Missing);
            result["job_dir"] = run.Job?.JobDir;
            result["final"] = JsonSerializer.SerializeToNode(run.Final);
            if (!run.Ok)
                throw new CliError("thumbs_failed", run.Final.Error ?? run.Final.Description,
                    "Try again. If it keeps failing, run nr diagnostics and check the job folder: " + (run.Job?.JobDir ?? "?"));
            output.Result("thumbs", result, w =>
            {
                w.WriteLine($"Made {run.Written.Count} pictures" + (run.Missing.Count > 0 ? $", {run.Missing.Count} missing (after the end of the recording?)" : "") + ".");
                if (simulated) w.WriteLine("Simulated: test pictures, kept apart from the real cache.");
                PrintCached(w, result);
            });
            return ExitCodes.Ok;
        }));
        return cmd;
    }
}
