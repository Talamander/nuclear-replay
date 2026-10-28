using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NuclearReplay.Core;
using NuclearReplay.Core.Clips;
using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Game;
using NuclearReplay.Core.Render;
using NuclearReplay.Core.Shots;

namespace NuclearReplay.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var env = new NcEnvironment();
        Log.Init(env.Paths, "nr");
        Log.Info($"nr {AppVersion.Current}: {string.Join(' ', args)}");
        int code = Run(args, env);
        if (code != 0) Log.Warn($"nr exited with code {code}");
        return code;
    }

    public static int Run(string[] args, NcEnvironment env, TextWriter? stdout = null, TextWriter? stderr = null, TextReader? stdin = null)
    {
        var commands = new Commands(env, stdout, stderr, stdin);
        return commands.Build().Parse(args).Invoke();
    }
}

/// <summary>The nr command tree. Every command accepts --json and then prints exactly one JSON result document.</summary>
public sealed partial class Commands
{
    private readonly NcEnvironment env;
    private readonly Option<bool> json = new("--json") { Description = "Print machine-readable JSON (one result object; long commands also print NDJSON progress lines first).", Recursive = true };
    private readonly Option<string?> gameDirOverride = new("--game-dir") { Description = "Game folder for this command (not saved). Default: settings, then Steam libraries.", Recursive = true };

    private readonly TextWriter? stdout;
    private readonly TextWriter? stderr;
    private readonly TextReader? stdin;

    public Commands(NcEnvironment env, TextWriter? stdout = null, TextWriter? stderr = null, TextReader? stdin = null)
    {
        this.env = env;
        this.stdout = stdout;
        this.stderr = stderr;
        this.stdin = stdin;
    }

    public RootCommand Build()
    {
        var root = new RootCommand("Nuclear Replay: record Nuclear Option matches and turn them into cinematic clips.");
        root.Options.Add(json);
        root.Options.Add(gameDirOverride);
        root.Subcommands.Add(StatusCommand());
        root.Subcommands.Add(InstallCommand());
        root.Subcommands.Add(ListCommand());
        root.Subcommands.Add(EventsCommand());
        root.Subcommands.Add(ShotsCommand());
        root.Subcommands.Add(RenderCommand());
        root.Subcommands.Add(WatchCommand());
        root.Subcommands.Add(OpenCommand());
        root.Subcommands.Add(PreviewCommand());
        root.Subcommands.Add(ThumbsCommand());
        root.Subcommands.Add(PresetsCommand());
        root.Subcommands.Add(FfmpegCommand());
        root.Subcommands.Add(SettingsCommand());
        root.Subcommands.Add(DiagnosticsCommand());
        return root;
    }

    // ---------- helpers ----------

    private int Execute(ParseResult parse, string name, Func<Output, int> body)
    {
        var output = new Output(parse.GetValue(json), stdout, stderr);
        try { return body(output); }
        catch (CliError e) { output.Error(name, e); return e.ExitCode; }
        catch (NcError e)
        {
            var error = new CliError(e.Code, e.Message, e.Fix, e.Invalid ? ExitCodes.Invalid : ExitCodes.Failed);
            output.Error(name, error);
            return error.ExitCode;
        }
        catch (LaunchException e) { output.Error(name, new CliError("launch_failed", e.Message, e.Fix)); return ExitCodes.Failed; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException)
        {
            output.Error(name, new CliError("failed", e.Message));
            return ExitCodes.Failed;
        }
    }

    private Settings LoadSettings(ParseResult parse)
    {
        var settings = env.LoadSettings();
        var overrideDir = parse.GetValue(gameDirOverride);
        if (!string.IsNullOrWhiteSpace(overrideDir)) settings.GameDir = overrideDir;
        return settings;
    }

    private static GameInstall RequireGame(NcEnvironment env, Settings settings) =>
        env.FindGame(settings) ?? throw new CliError("game_not_found",
            string.IsNullOrWhiteSpace(settings.GameDir) ? "Nuclear Option was not found in your Steam libraries." : $"\"{settings.GameDir}\" does not contain NuclearOption.exe.",
            "Pass --game-dir <folder>, or save it: nr settings set game_dir <folder>.");

    private string RequireDemo(string arg, Settings settings, GameInstall? game) =>
        env.ResolveDemo(arg, settings, game) ?? throw new CliError("demo_not_found", $"Recording \"{arg}\" was not found.",
            "Use a path, a file name from 'nr list', or 'latest'.", ExitCodes.Invalid);

    private static JsonObject EventJson(DemoEvent e, int index)
    {
        var o = new JsonObject
        {
            ["index"] = index,
            ["t"] = Math.Round(e.T, 3),
            ["time"] = Output.Duration(e.T),
            ["event"] = e.Event,
            ["summary"] = e.Summary,
        };
        if (e.Subject?.ShotTarget is { } target) o["camera_target"] = target;
        if (e.Event == "launch" && e.Missile is { } missile) o["missile_target"] = $"unit:{missile}";
        // Kills with a recorded hit: "t" is the impact, "kill_feed_t" the game's kill feed message.
        if (e.HitAgo != null) o["kill_feed_t"] = Math.Round(e.LoggedT, 3);
        o["data"] = e.Data.DeepClone();
        return o;
    }

    // ---------- status ----------

    private Command StatusCommand()
    {
        var cmd = new Command("status", "Check the game, the mod and the video setup, with plain-English fixes.");
        cmd.SetAction(parse => Execute(parse, "status", output =>
        {
            var settings = LoadSettings(parse);
            var report = env.Status.Check(settings);
            output.Result("status", report, w =>
            {
                foreach (var c in report.Checks)
                {
                    var mark = c.Status switch { CheckStatus.Ok => "OK  ", CheckStatus.Info => "INFO", CheckStatus.Warning => "WARN", _ => "FAIL" };
                    w.WriteLine($"[{mark}] {c.Title}: {c.Message}");
                    if (c.Fix != null && c.Status != CheckStatus.Ok) w.WriteLine($"       Fix: {c.Fix}");
                }
                w.WriteLine();
                w.WriteLine(report.ReadyToRecord ? "Matches you host are recorded." : "Recording is not set up yet.");
                w.WriteLine(report.ReadyToRender ? "Clips can be rendered." : "Rendering is not ready yet.");
            });
            return report.ReadyToRecord ? ExitCodes.Ok : ExitCodes.Failed;
        }));
        return cmd;
    }

    // ---------- install ----------

    private Command InstallCommand()
    {
        var from = new Option<string?>("--from") { Description = "Folder with NuclearReplay.dll. Default: bundled/plugin next to nr." };
        var bepinex = new Option<string?>("--bepinex") { Description = "BepInEx 5 x64 zip to use when the loader is missing. Default: the bundled zip." };
        var noProton = new Option<bool>("--no-proton-fix") { Description = "Linux: do not write the winhttp DLL override into the game's Proton prefix." };
        var verboseSteps = new Option<bool>("--steps") { Description = "Also list every file written or kept." };
        var noFfmpeg = new Option<bool>("--no-ffmpeg") { Description = "Do not download ffmpeg when it is missing (clips and renders need it; nr ffmpeg install gets it later)." };
        var cmd = new Command("install", "Install or update the mod (and BepInEx 5 if missing), then download ffmpeg if it is missing. Close the game first.");
        cmd.Options.Add(verboseSteps);
        cmd.Options.Add(noFfmpeg);
        cmd.Options.Add(from);
        cmd.Options.Add(bepinex);
        cmd.Options.Add(noProton);
        cmd.SetAction(parse => Execute(parse, "install", output =>
        {
            var settings = LoadSettings(parse);
            var game = RequireGame(env, settings);
            var result = env.CreateInstaller().Install(new InstallOptions
            {
                GameDir = game.GameDir,
                PluginSourceDir = parse.GetValue(from),
                BepInExZip = parse.GetValue(bepinex),
                BackupRoot = env.Paths.BackupsDir,
                CompatDataDir = parse.GetValue(noProton) ? null : game.CompatDataDir,
                Viewer = ViewerDefaults.For(settings, game),
            });
            if (!result.Ok) throw new CliError("install_failed", result.Error!, result.Fix);
            var summary = ModInstaller.Describe(result);

            // ffmpeg is not shipped; download it from upstream unless the player has one or asked not to.
            var ffmpeg = env.CreateFfmpegInstaller();
            var state = ffmpeg.State(settings, game.GameDir);
            object ffmpegData;
            if (state.Present)
                ffmpegData = new { action = "kept", path = state.Path, source = state.Source };
            else if (state.Source == "setting")
            {
                ffmpegData = new { action = "missing", path = state.Path, source = state.Source };
                summary.Add($"The ffmpeg.exe chosen in your settings ({state.Path}) does not exist, so clips cannot be made yet. Choose it again: nr settings set ffmpeg_path <path>.");
            }
            else if (parse.GetValue(noFfmpeg))
            {
                ffmpegData = new { action = "skipped", path = (string?)null, source = (string?)null };
                summary.Add("ffmpeg was not downloaded (--no-ffmpeg). Clips need it: run nr ffmpeg install.");
            }
            else
            {
                try
                {
                    var path = DownloadFfmpeg(output, ffmpeg, game.GameDir, force: false);
                    ffmpegData = new { action = "installed", path = (string?)path, source = (string?)"shared", version = ffmpeg.Version };
                    summary.Add($"Downloaded the video encoder (ffmpeg {ffmpeg.Version}, from {ffmpeg.Host}) and checked its SHA-256.");
                }
                catch (NcError e)
                {
                    // The mod is installed; only clips wait for ffmpeg. Report it and let the player retry.
                    ffmpegData = new { action = "failed", error = new { code = e.Code, message = e.Message, fix = e.Fix } };
                    summary.Add("The mod is installed, but ffmpeg could not be downloaded: " + e.Message + " Run nr ffmpeg install to try again.");
                }
            }

            output.Result("install", new { game_dir = game.GameDir, result.InstalledVersion, result.BackupDir, result.Steps, summary, ffmpeg = ffmpegData }, w =>
            {
                foreach (var line in summary) w.WriteLine("- " + line);
                if (parse.GetValue(verboseSteps))
                    foreach (var s in result.Steps) w.WriteLine($"  {s.Action,-9} {s.Path}{(s.Detail != null ? "  (" + s.Detail + ")" : "")}");
                w.WriteLine($"Nuclear Replay {result.InstalledVersion ?? ""} is installed. Start the game from Steam as usual.");
            });
            return ExitCodes.Ok;
        }));
        return cmd;
    }

    // ---------- list ----------

    private Command ListCommand()
    {
        var dir = new Option<string?>("--dir") { Description = "Recordings folder. Default: the folder the recorder writes to." };
        var cmd = new Command("list", "List recorded matches, newest first.");
        cmd.Options.Add(dir);
        cmd.SetAction(parse => Execute(parse, "list", output =>
        {
            var settings = LoadSettings(parse);
            var game = env.FindGame(settings);
            var given = parse.GetValue(dir);
            var demoDir = given ?? env.DemoDir(settings, game)
                ?? throw new CliError("no_demo_dir", "The recordings folder is unknown because the game was not found.", "Pass --dir or --game-dir.");
            // Without --dir, recordings from before the rename (<game>/NuclearContext/demos) are listed too.
            var extra = given == null ? env.ExtraDemoDirs(settings, game) : Array.Empty<string>();
            var demos = env.Library.List(demoDir, extra);
            output.Result("list", new { demo_dir = demoDir, extra_dirs = extra, count = demos.Count, demos }, w =>
            {
                if (demos.Count == 0) { w.WriteLine($"No recordings in {demoDir}. Host a mission to record one."); return; }
                w.WriteLine($"{demos.Count} recording(s) in {demoDir}" + string.Concat(extra.Select(e => " and " + e)));
                foreach (var d in demos)
                {
                    var when = d.CreatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?";
                    var extra = d.Error != null ? "  UNREADABLE: " + d.Error
                        : d.HasEvents ? $"  {d.Launches} launches, {d.Kills} kills, {d.MissileHits} missile hits" : "  (no events)";
                    w.WriteLine($"{when}  {Output.Duration(d.DurationS),8}  {Output.Size(d.SizeBytes),9}  {d.Title}{extra}");
                    w.WriteLine($"    {d.FileName}");
                }
            });
            return ExitCodes.Ok;
        }));
        return cmd;
    }

    // ---------- events ----------

    private Command EventsCommand()
    {
        var demo = new Argument<string>("demo") { Description = "Recording: path, file name from 'nr list', or 'latest'." };
        var moments = new Option<bool>("--moments") { Description = "Only launches and kills." };
        var cmd = new Command("events", "Show a recording's details and its timeline of events (launches, kills, players).");
        cmd.Arguments.Add(demo);
        cmd.Options.Add(moments);
        cmd.SetAction(parse => Execute(parse, "events", output =>
        {
            var settings = LoadSettings(parse);
            var game = env.FindGame(settings);
            var path = RequireDemo(parse.GetValue(demo)!, settings, game);
            var details = env.Library.Get(path);
            if (details.Summary.Error != null) throw new CliError("unreadable", details.Summary.Error, "The file is not a Nuclear Replay recording or is damaged.");
            var events = details.Events.Select((e, i) => (e, i)).Where(x => !parse.GetValue(moments) || x.e.IsMoment).ToList();
            var data = new JsonObject
            {
                ["summary"] = JsonSerializer.SerializeToNode(details.Summary, Json.Options),
                ["header"] = details.Header.DeepClone(),
                ["events"] = new JsonArray(events.Select(x => (JsonNode)EventJson(x.e, x.i)).ToArray()),
            };
            if (!details.Summary.HasEvents)
                data["note"] = "This recording has no launch/kill events (recorded before event annotations). Pick times by hand.";
            output.Result("events", data, w =>
            {
                var s = details.Summary;
                w.WriteLine($"{s.Title}  {Output.Duration(s.DurationS)}  {s.GameState}  game {s.GameVersion}  mod {s.ModVersion}");
                if (s.Map != null) w.WriteLine($"Map: {s.Map}");
                if (s.Players.Count > 0) w.WriteLine("Players: " + string.Join(", ", s.Players));
                if (s.Truncated) w.WriteLine("Note: the end of the file is cut off (game closed or still recording); everything before it replays.");
                if (!s.HasEvents) w.WriteLine("Note: no launch/kill events in this recording (made before event annotations). Pick times by hand.");
                foreach (var (e, i) in events)
                    w.WriteLine($"#{i,-4} {Output.Duration(e.T),8}  ({e.T.ToString("0.0", CultureInfo.InvariantCulture)}s)  {e.Summary}");
            });
            return ExitCodes.Ok;
        }));
        return cmd;
    }

    // ---------- shots ----------

    private Command ShotsCommand()
    {
        var cmd = new Command("shots", "Shot lists: camera directions for replays and renders.");

        var file = new Argument<string>("file") { Description = "Shot list JSON file." };
        var demo = new Option<string?>("--demo") { Description = "Also check times against this recording's length." };
        var validate = new Command("validate", "Check a shot list. Exit code 2 when it has errors.");
        validate.Arguments.Add(file);
        validate.Options.Add(demo);
        validate.SetAction(parse => Execute(parse, "shots validate", output =>
        {
            double? duration = null;
            if (parse.GetValue(demo) is { } d)
            {
                var settings = LoadSettings(parse);
                duration = env.Library.Get(RequireDemo(d, settings, env.FindGame(settings))).Summary.DurationS;
            }
            var path = parse.GetValue(file)!;
            if (!File.Exists(path)) throw new CliError("file_not_found", $"\"{path}\" does not exist.", null, ExitCodes.Invalid);
            var result = ShotValidator.ValidateFile(path, duration);
            output.Result("shots validate", new { file = Path.GetFullPath(path), result.Valid, shots = result.ShotList?.Shots.Count ?? 0, result.Issues }, w =>
            {
                foreach (var i in result.Issues) w.WriteLine($"{i.Severity}: {i.Path}: {i.Message}");
                w.WriteLine(result.Valid ? $"Valid shot list ({result.ShotList?.Shots.Count ?? 0} shots)." : "The shot list has errors.");
            }, ok: result.Valid);
            return result.Valid ? ExitCodes.Ok : ExitCodes.Invalid;
        }));
        cmd.Subcommands.Add(validate);

        var schema = new Command("schema", "Print the JSON Schema for shot lists.");
        schema.SetAction(parse => Execute(parse, "shots schema", output =>
        {
            var node = JsonNode.Parse(NcEnvironment.ShotListSchema);
            if (output.JsonMode) output.Result("shots schema", node);
            else output.Text(NcEnvironment.ShotListSchema);
            return ExitCodes.Ok;
        }));
        cmd.Subcommands.Add(schema);

        var makeDemo = new Argument<string>("demo") { Description = "Recording: path, file name or 'latest'." };
        var eventIndex = new Option<int?>("--event") { Description = "Event index from 'nr events' to frame the clip around." };
        var at = new Option<double?>("--at") { Description = "Replay second to frame the clip around (when there is no event)." };
        var range = new Option<string?>("--range") { Description = "Instead of a moment: replay seconds as from:to[,from:to]." };
        var style = new Option<string>("--style") { Description = ShotPresets.StyleList + ".", DefaultValueFactory = _ => "cinematic_chase" };
        var pre = new Option<double>("--pre") { Description = "Seconds before the moment.", DefaultValueFactory = _ => 5 };
        var post = new Option<double>("--post") { Description = "Seconds after the moment.", DefaultValueFactory = _ => 6 };
        var outFile = new Option<string?>("--out") { Description = "Also write the shot list to this file." };
        var make = new Command("make", "Generate a shot list (and range) for a moment from a camera style preset. Edit it, then pass it to render --shots.");
        make.Arguments.Add(makeDemo);
        foreach (var o in new Option[] { eventIndex, at, range, style, pre, post, outFile }) make.Options.Add(o);
        make.SetAction(parse => Execute(parse, "shots make", output =>
        {
            var settings = LoadSettings(parse);
            var demoPath = RequireDemo(parse.GetValue(makeDemo)!, settings, env.FindGame(settings));
            var details = env.Library.Get(demoPath);
            var spec = new ClipSpec
            {
                Demo = demoPath,
                EventIndex = parse.GetValue(eventIndex),
                At = parse.GetValue(at),
                Ranges = parse.GetValue(range),
                Pre = parse.GetValue(pre),
                Post = parse.GetValue(post),
                Style = parse.GetValue(style)!,
            };
            var plan = ClipPlanner.Plan(spec, demoPath, details, settings);
            var shots = plan.ShotsJson!;
            string? written = null;
            if (parse.GetValue(outFile) is { } file)
            {
                written = Path.GetFullPath(file);
                if (Path.GetDirectoryName(written) is { } dir) Directory.CreateDirectory(dir);
                Json.WriteFileAtomic(written, shots);
            }
            var r = plan.Ranges.Count == 1 ? plan.Ranges[0] : new TimeRange(plan.Ranges[0].From, plan.Ranges[^1].To);
            var data = new JsonObject
            {
                ["range"] = string.Join(",", plan.Ranges.Select(x => x.ToString())),
                ["from"] = r.From,
                ["to"] = r.To,
                ["shot_list"] = JsonNode.Parse(shots),
            };
            if (written != null) data["file"] = written;
            output.Result("shots make", data, w =>
            {
                w.WriteLine(shots);
                if (written != null) w.WriteLine("Saved " + written);
            });
            return ExitCodes.Ok;
        }));
        cmd.Subcommands.Add(make);
        return cmd;
    }

    // ---------- render ----------

    private Command RenderCommand()
    {
        var demo = new Argument<string>("demo") { Description = "Recording: path, file name from 'nr list', or 'latest'." };
        var range = new Option<string?>("--range") { Description = "Replay seconds as from:to, several separated by commas: 40:48,120:130." };
        var eventIndex = new Option<int?>("--event") { Description = "Instead of --range: clip around this event index (from 'nr events')." };
        var at = new Option<double?>("--at") { Description = "Instead of --range: clip around this replay second." };
        var pre = new Option<double>("--pre") { Description = "With --event/--at: seconds before the moment.", DefaultValueFactory = _ => 5 };
        var post = new Option<double>("--post") { Description = "With --event/--at: seconds after the moment.", DefaultValueFactory = _ => 6 };
        var shots = new Option<string?>("--shots") { Description = "Shot list JSON. Default: --style preset, else <demo>.shots.json, else orbit/auto." };
        var style = new Option<string?>("--style") { Description = "Camera preset when there is no --shots: " + ShotPresets.StyleList + "." };
        var fps = new Option<int>("--fps") { Description = "Frames per second.", DefaultValueFactory = _ => 60 };
        var size = new Option<string>("--size") { Description = "WxH (1920x1080) or a preset: 1080p, vertical, 4k.", DefaultValueFactory = _ => "1080p" };
        var outDir = new Option<string?>("--out") { Description = "Folder for the MP4 files. Default: the video folder from settings." };
        var hud = new Option<bool>("--hud") { Description = "Keep the game HUD in the video." };
        var encoder = new Option<string?>("--encoder") { Description = "auto (NVENC, then Vulkan, then x264), nvenc, vulkan or x264." };
        var quality = new Option<int?>("--quality") { Description = "Encoder quality (lower is better, default 19)." };
        var name = new Option<string?>("--name") { Description = "File name prefix for the clips." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Write the job file and print the launch command without starting the game." };
        var noWait = new Option<bool>("--no-wait") { Description = "Start the game and return; follow progress.json yourself." };
        var cmd = new Command("render", "Render clips of a recording to MP4 inside the game (the game window opens and closes by itself).");
        cmd.Arguments.Add(demo);
        foreach (var o in new Option[] { range, eventIndex, at, pre, post, shots, style, fps, size, outDir, hud, encoder, quality, name, dryRun, noWait }) cmd.Options.Add(o);
        cmd.SetAction(parse => Execute(parse, "render", output =>
        {
            var settings = LoadSettings(parse);
            var game = RequireGame(env, settings);
            var demoPath = RequireDemo(parse.GetValue(demo)!, settings, game);
            var details = env.Library.Get(demoPath);
            var spec = new ClipSpec
            {
                Demo = demoPath,
                Ranges = parse.GetValue(range),
                EventIndex = parse.GetValue(eventIndex),
                At = parse.GetValue(at),
                Pre = parse.GetValue(pre),
                Post = parse.GetValue(post),
                ShotsPath = parse.GetValue(shots),
                Style = parse.GetValue(style),
                Size = parse.GetValue(size)!,
                Fps = parse.GetValue(fps),
                Hud = parse.GetValue(hud),
                OutDir = parse.GetValue(outDir),
                Encoder = parse.GetValue(encoder),
                Quality = parse.GetValue(quality),
                Name = parse.GetValue(name),
            };
            var clip = ClipPlanner.Plan(spec, demoPath, details, settings, game);
            var warnings = clip.Warnings;
            bool dry = parse.GetValue(dryRun);
            if (clip.FfmpegMissing)
            {
                if (!dry) throw new CliError("ffmpeg_missing", "The video encoder (ffmpeg.exe) is not downloaded yet.",
                    "Run nr ffmpeg install (downloads a free build, about " + env.CreateFfmpegInstaller().SizeText + "), or nr settings set ffmpeg_path <path to ffmpeg.exe>.");
                warnings.Add("ffmpeg is missing; the job file has no encoder path.");
            }
            if (!dry && env.GameRunning())
                throw new CliError("game_running", "Nuclear Option is running.",
                    "Close the game, then try again. The render starts the game by itself and closes it when the clip is saved.");
            var request = clip.ToRequest();
            var (job, plan) = env.StartRender(request, game, dry, details.Summary.Mission);
            var started = new JsonObject
            {
                ["job_id"] = job.Id,
                ["job_dir"] = job.JobDir,
                ["job_file"] = job.JobFile,
                ["progress_file"] = job.ProgressFile,
                ["outputs"] = new JsonArray(job.Outputs.Select(o => (JsonNode)o).ToArray()),
                ["launch"] = plan.CommandLine,
                ["dry_run"] = dry,
                ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w).ToArray()),
                ["job"] = JsonNode.Parse(job.Job.ToJson()),
            };
            if (dry || parse.GetValue(noWait))
            {
                output.Result("render", started, w =>
                {
                    foreach (var warning in warnings) w.WriteLine("Warning: " + warning);
                    w.WriteLine((dry ? "Dry run. Would run: " : "Started: ") + plan.CommandLine);
                    w.WriteLine("Job file: " + job.JobFile);
                    w.WriteLine("Progress: " + job.ProgressFile);
                    foreach (var o in job.Outputs) w.WriteLine("Output:   " + o);
                });
                return ExitCodes.Ok;
            }
            foreach (var warning in warnings) output.Progress(new { state = "warning", message = warning }, "Warning: " + warning);
            var final = new ProgressWatcher(job.ProgressFile).WatchAsync(p =>
                output.Progress(new { p.State, p.Range, p.Ranges, p.Frame, p.Frames, p.Error, fraction = Math.Round(p.Fraction, 4) },
                    $"{p.Fraction * 100,5:0.0}%  {p.Description}")).GetAwaiter().GetResult();
            started["final"] = JsonSerializer.SerializeToNode(final);
            bool ok = final.State == "done";
            if (!ok) throw new CliError("render_failed", final.Description, RenderFailure.Fix(game, job.JobDir, final.Description));
            output.Result("render", started, w => { foreach (var o in job.Outputs) w.WriteLine("Saved " + o); });
            return ExitCodes.Ok;
        }));
        return cmd;
    }

    public static (int Width, int Height) ParseSize(string text) => ClipPlanner.ParseSize(text);

    // ---------- watch ----------

    private Command WatchCommand()
    {
        var demo = new Argument<string>("demo") { Description = "Recording: path, file name from 'nr list', or 'latest'." };
        var shots = new Option<string?>("--shots") { Description = "Shot list JSON for the replay cameras." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the launch command without starting the game." };
        var at = new Option<double?>("--at") { Description = "Start at this replay second (skips ahead quickly, then plays at normal speed from about 1 s before it)." };
        var eventIndex = new Option<int?>("--event") { Description = "Start a few seconds before this moment (index from 'nr events --moments')." };
        var pre = new Option<double>("--pre") { Description = "Seconds before --event to start. Default 5.", DefaultValueFactory = _ => 5 };
        var cmd = new Command("watch", "Open a recording in the game to watch it (Pause, PageUp/PageDown speed, Home next aircraft, End next camera).");
        cmd.Arguments.Add(demo);
        cmd.Options.Add(shots);
        cmd.Options.Add(at);
        cmd.Options.Add(eventIndex);
        cmd.Options.Add(pre);
        cmd.Options.Add(dryRun);
        cmd.SetAction(parse => Execute(parse, "watch", output =>
        {
            var settings = LoadSettings(parse);
            var game = RequireGame(env, settings);
            var demoPath = RequireDemo(parse.GetValue(demo)!, settings, game);
            var shotsPath = parse.GetValue(shots);
            if (shotsPath != null)
            {
                var validation = ShotValidator.ValidateFile(shotsPath);
                if (!validation.Valid)
                    throw new CliError("invalid_shots", "The shot list has errors.", "Run nr shots validate <file> for details.", ExitCodes.Invalid);
                shotsPath = Path.GetFullPath(shotsPath);
            }
            double? startAt = null;
            var atValue = parse.GetValue(at);
            var eventValue = parse.GetValue(eventIndex);
            if (atValue != null && eventValue != null)
                throw new CliError("bad_start", "Use --at or --event, not both.", null, ExitCodes.Invalid);
            if (atValue != null || eventValue != null)
            {
                var details = env.Library.Get(demoPath);
                if (details.Summary.Error != null) throw new CliError("unreadable", details.Summary.Error);
                double t;
                if (eventValue is { } idx)
                {
                    if (idx < 0 || idx >= details.Events.Count)
                        throw new CliError("bad_event", $"Event #{idx} does not exist (this recording has {details.Events.Count}).", "Run nr events <demo> to see the indexes.", ExitCodes.Invalid);
                    t = Math.Max(0, details.Events[idx].T - Math.Max(0, parse.GetValue(pre)));
                }
                else t = atValue!.Value;
                if (t < 0 || double.IsNaN(t))
                    throw new CliError("bad_start", $"{t} is not a replay time.", "Use seconds from 'nr events', 0 or more.", ExitCodes.Invalid);
                if (t >= details.Summary.DurationS)
                    throw new CliError("bad_start", $"{t:F1}s is after the recording ends ({details.Summary.DurationS:F1}s).", "Pick a time from 'nr events'.", ExitCodes.Invalid);
                startAt = t > 0 ? t : null;
            }
            var args = GameLauncher.WatchArgs(PathMapper.For(game), demoPath, shotsPath, startAt);
            bool dry = parse.GetValue(dryRun);
            var plan = dry ? env.Launcher.Plan(game, args) : env.Launcher.Launch(game, args);
            output.Result("watch", new { demo = demoPath, launch = plan.CommandLine, dry_run = dry, arguments = plan.Arguments, start_at = startAt }, w =>
            {
                w.WriteLine((dry ? "Dry run. Would run: " : "Starting the game: ") + plan.CommandLine);
                if (startAt != null) w.WriteLine($"The replay skips ahead and plays at normal speed from {startAt:F1}s.");
            });
            return ExitCodes.Ok;
        }));
        return cmd;
    }

    // ---------- open ----------

    private Command OpenCommand()
    {
        var demo = new Argument<string>("demo") { Description = "Recording: path, file name from 'nr list', or 'latest'." };
        var at = new Option<double?>("--at") { Description = "Start the viewer at this replay second." };
        var focus = new Option<double?>("--focus") { Description = "Select the moment at this replay second and set the clip in and out points around it." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the launch command without starting the game." };
        var cmd = new Command("open", "Open a recording in the in-game replay viewer: play, pause, skip, change cameras and record clips in the game (F1 shows the keys).");
        cmd.Arguments.Add(demo);
        cmd.Options.Add(at);
        cmd.Options.Add(focus);
        cmd.Options.Add(dryRun);
        cmd.SetAction(parse => Execute(parse, "open", output =>
        {
            var settings = LoadSettings(parse);
            var game = RequireGame(env, settings);
            var demoPath = RequireDemo(parse.GetValue(demo)!, settings, game);
            var atValue = parse.GetValue(at);
            var focusValue = parse.GetValue(focus);
            if (atValue != null || focusValue != null)
            {
                var details = env.Library.Get(demoPath);
                if (details.Summary.Error != null) throw new CliError("unreadable", details.Summary.Error);
                foreach (var (name, value) in new[] { ("--at", atValue), ("--focus", focusValue) })
                {
                    if (value is not { } t) continue;
                    if (t < 0 || double.IsNaN(t))
                        throw new CliError("bad_start", $"{name} {t} is not a replay time.", "Use seconds from 'nr events', 0 or more.", ExitCodes.Invalid);
                    if (t >= details.Summary.DurationS)
                        throw new CliError("bad_start", $"{name} {t:F1}s is after the recording ends ({details.Summary.DurationS:F1}s).", "Pick a time from 'nr events'.", ExitCodes.Invalid);
                }
            }
            bool dry = parse.GetValue(dryRun);
            if (!dry && env.GameRunning())
                throw new CliError("game_running", "Nuclear Option is running.",
                    "Close the game, then try again. nr open starts the game with the replay viewer.");
            var clipsDir = Path.GetFullPath(settings.EffectiveOutputDir);
            if (!dry) Directory.CreateDirectory(clipsDir);
            double? startAt = atValue is > 0 ? atValue : null;
            var args = GameLauncher.ViewerArgs(PathMapper.For(game), demoPath, startAt, focusValue, clipsDir,
                StatusService.ResolveFfmpeg(settings, game), settings.Quality, settings.Encoder);
            var plan = dry ? env.Launcher.PlanViewer(game, args) : env.Launcher.LaunchViewer(game, args);
            output.Result("open", new
            {
                demo = demoPath,
                launch = plan.CommandLine,
                dry_run = dry,
                arguments = plan.Arguments,
                at = startAt,
                focus = focusValue,
                clips_dir = clipsDir,
            }, w =>
            {
                w.WriteLine((dry ? "Dry run. Would run: " : "Starting the game: ") + plan.CommandLine);
                w.WriteLine("In the game: Space plays and pauses, F1 shows all keys, C records a clip.");
                w.WriteLine($"Clips recorded in the game are saved to {clipsDir}.");
            });
            return ExitCodes.Ok;
        }));
        return cmd;
    }

    // ---------- presets ----------

    private Command PresetsCommand()
    {
        var cmd = new Command("presets", "List camera styles, sizes, cameras and targets.");
        cmd.SetAction(parse => Execute(parse, "presets", output =>
        {
            var data = new
            {
                camera_styles = ShotPresets.CameraStyles,
                sizes = ShotPresets.Resolutions,
                cameras = ShotValidator.Cameras,
                targets = ShotValidator.FixedTargets.Concat(new[] { "unit:<id>", "name:<text>", "type:<text>" }).ToArray(),
                encoders = RenderJobWriter.Encoders,
            };
            output.Result("presets", data, w =>
            {
                w.WriteLine("Camera styles (--style):");
                foreach (var s in ShotPresets.CameraStyles) w.WriteLine($"  {s.Id,-16} {s.Description}");
                w.WriteLine("Sizes (--size):");
                foreach (var r in ShotPresets.Resolutions) w.WriteLine($"  {r.Id,-16} {r.Label}");
                w.WriteLine("Shot list cameras: " + string.Join(", ", ShotValidator.Cameras));
                w.WriteLine("Shot list targets: " + string.Join(", ", data.targets));
            });
            return ExitCodes.Ok;
        }));
        return cmd;
    }

    // ---------- settings ----------

    // ---------- diagnostics ----------

    private Command DiagnosticsCommand()
    {
        var outFile = new Option<string?>("--out") { Description = "Write the report here instead of the diagnostics folder." };
        var cmd = new Command("diagnostics", "Save one text file for a bug report: versions, setup check, nr log and the game's BepInEx log.");
        cmd.Options.Add(outFile);
        cmd.SetAction(parse => Execute(parse, "diagnostics", output =>
        {
            var (path, text) = Diagnostics.Save(env, parse.GetValue(outFile), parse.GetValue(gameDirOverride));
            output.Result("diagnostics", new
            {
                file = path,
                bytes = new FileInfo(path).Length,
                app_log = Path.Combine(Log.LogDir(env.Paths), Log.FileName),
            }, w => w.WriteLine($"Saved {path}. Send this file with your bug report."));
            return ExitCodes.Ok;
        }));
        return cmd;
    }

    private Command SettingsCommand()
    {
        var cmd = new Command("settings", "Show saved settings.");
        cmd.SetAction(parse => Execute(parse, "settings", output =>
        {
            var settings = env.LoadSettings();
            var node = SettingsNode(settings);
            node["settings_file"] = env.Paths.SettingsFile;
            node["effective_output_dir"] = settings.EffectiveOutputDir;
            output.Result("settings", node, w => w.WriteLine(node.ToJsonString(Json.Options)));
            return ExitCodes.Ok;
        }));
        var key = new Argument<string>("key") { Description = "Setting name, e.g. game_dir, output_dir, demo_dir, ffmpeg_path, default_fps." };
        var value = new Argument<string>("value") { Description = "New value. Empty string clears it." };
        var set = new Command("set", "Change a setting.");
        set.Arguments.Add(key);
        set.Arguments.Add(value);
        set.SetAction(parse => Execute(parse, "settings set", output =>
        {
            var settings = env.LoadSettings();
            var node = SettingsNode(settings);
            var k = parse.GetValue(key)!;
            if (!node.ContainsKey(k) && typeof(Settings).GetProperties().All(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name) != k))
                throw new CliError("bad_key", $"Unknown setting \"{k}\".", "Run nr settings to see the names.", ExitCodes.Invalid);
            var v = parse.GetValue(value)!;
            JsonNode? parsed = v.Length == 0 ? null
                : double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && k is not ("game_dir" or "output_dir" or "demo_dir") ? JsonValue.Create(number)
                : bool.TryParse(v, out var flag) ? JsonValue.Create(flag)
                : JsonValue.Create(v);
            node[k] = parsed;
            Settings updated;
            try { updated = node.Deserialize<Settings>(Json.Options) ?? settings; }
            catch (JsonException e) { throw new CliError("bad_value", $"\"{v}\" is not a valid value for {k}: {e.Message}", null, ExitCodes.Invalid); }
            env.SettingsStore.Save(updated);
            output.Result("settings set", SettingsNode(updated), w => w.WriteLine($"{k} = {(parsed == null ? "(cleared)" : v)}"));
            return ExitCodes.Ok;
        }));
        cmd.Subcommands.Add(set);
        return cmd;
    }

    private static JsonObject SettingsNode(Settings settings)
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(settings, Json.Options)!;
        node.Remove("effective_output_dir");
        return node;
    }
}
