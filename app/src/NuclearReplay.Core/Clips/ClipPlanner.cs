using System.Globalization;
using System.Text;
using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Game;
using NuclearReplay.Core.Render;
using NuclearReplay.Core.Shots;

namespace NuclearReplay.Core.Clips;

/// <summary>
/// A user-facing error with a stable code and a one-line fix. The CLI prints it as {ok:false,error:{code,message,fix}}. <see cref="Invalid"/> marks bad input (CLI exit code 2).
/// </summary>
public sealed class NcError : Exception
{
    public string Code { get; }
    public string? Fix { get; }
    public bool Invalid { get; }

    public NcError(string code, string message, string? fix = null, bool invalid = false) : base(message)
    {
        Code = code;
        Fix = fix;
        Invalid = invalid;
    }
}

/// <summary>
/// One clip, as <c>nr render</c> describes it. <see cref="ClipCommand"/> turns a spec into the equivalent <c>nr</c>
/// command line, and <see cref="ClipPlanner"/> turns it into a render request.
/// </summary>
public sealed record ClipSpec
{
    /// <summary>Recording: a path, a file name in the recordings folder, part of a name, or "latest".</summary>
    public required string Demo { get; init; }
    /// <summary>Index into the recording's events (as <c>nr events</c> prints it) to frame the clip around.</summary>
    public int? EventIndex { get; init; }
    /// <summary>Replay second to frame the clip around, when there is no event.</summary>
    public double? At { get; init; }
    /// <summary>Explicit ranges "from:to[,from:to]" instead of a moment.</summary>
    public string? Ranges { get; init; }
    public double Pre { get; init; } = 5;
    public double Post { get; init; } = 6;
    /// <summary>Camera style preset (see <see cref="ShotPresets.CameraStyles"/>). Ignored when <see cref="ShotsPath"/> is set.</summary>
    public string? Style { get; init; }
    /// <summary>Hand-written shot list file.</summary>
    public string? ShotsPath { get; init; }
    /// <summary>1080p, vertical, 4k or WxH.</summary>
    public string Size { get; init; } = "1080p";
    public int Fps { get; init; } = 60;
    /// <summary>Keep the game HUD. The saved setting also turns it on.</summary>
    public bool Hud { get; init; }
    /// <summary>Output folder; null means the video folder from settings.</summary>
    public string? OutDir { get; init; }
    /// <summary>auto, nvenc, vulkan or x264; null means the saved setting.</summary>
    public string? Encoder { get; init; }
    /// <summary>Encoder quality; null means the saved setting.</summary>
    public int? Quality { get; init; }
    /// <summary>File name prefix; null means "&lt;mission&gt; &lt;from&gt;-&lt;to&gt;".</summary>
    public string? Name { get; init; }
}

/// <summary>A checked clip: everything needed to write the render job, plus warnings to show.</summary>
public sealed class ClipPlan
{
    public required string DemoPath { get; init; }
    public required DemoDetails Details { get; init; }
    public DemoEvent? Moment { get; init; }
    public required List<TimeRange> Ranges { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Fps { get; init; }
    /// <summary>Generated shot list (style presets), or null.</summary>
    public string? ShotsJson { get; init; }
    /// <summary>Shot list file given by the user, or null.</summary>
    public string? ShotsPath { get; init; }
    public required bool Hud { get; init; }
    public required string OutputDir { get; init; }
    public string? FfmpegPath { get; init; }
    public required string Encoder { get; init; }
    public required int Quality { get; init; }
    public string? Name { get; init; }
    public List<string> Warnings { get; init; } = new();

    public string? MissionName => Details.Summary.Mission;
    public bool FfmpegMissing => FfmpegPath == null || !File.Exists(FfmpegPath);

    public RenderRequest ToRequest() => new()
    {
        DemoPath = DemoPath,
        Ranges = Ranges,
        Width = Width,
        Height = Height,
        Fps = Fps,
        ShotsPath = ShotsPath,
        ShotsJson = ShotsJson,
        Hud = Hud,
        OutputDir = OutputDir,
        FfmpegPath = FfmpegPath,
        Encoder = Encoder,
        Quality = Quality,
        Name = Name,
    };
}

/// <summary>Turns a <see cref="ClipSpec"/> into a <see cref="ClipPlan"/>. Shared by <c>nr render</c> and <c>nr shots make</c>.</summary>
public static class ClipPlanner
{
    /// <summary>Plans a clip of an already-resolved recording. Throws <see cref="NcError"/> for bad input.</summary>
    public static ClipPlan Plan(ClipSpec spec, string demoPath, DemoDetails details, Settings settings, GameInstall? game = null)
    {
        if (details.Summary.Error != null) throw new NcError("unreadable", details.Summary.Error, "The file is not a Nuclear Replay recording or is damaged.");
        var duration = details.Summary.DurationS;
        var warnings = new List<string>();

        DemoEvent? moment = null;
        List<TimeRange> ranges;
        if (spec.Ranges is { } rangeText)
        {
            if (spec.EventIndex != null || spec.At != null)
                throw new NcError("bad_range", "Use --range, --event or --at, not more than one.", null, invalid: true);
            try { ranges = TimeRange.ParseList(rangeText); }
            catch (FormatException e) { throw new NcError("bad_range", e.Message, null, invalid: true); }
        }
        else
        {
            var (m, r) = Moment(details, spec.EventIndex, spec.At, spec.Pre, spec.Post);
            moment = m;
            ranges = new List<TimeRange> { r };
        }
        for (int i = 0; i < ranges.Count; i++)
        {
            if (ranges[i].From >= duration)
                throw new NcError("bad_range", $"Range {ranges[i]} starts after the recording ends ({duration.ToString("F1", CultureInfo.InvariantCulture)}s).", "Pick times from 'nr events'.", invalid: true);
            if (ranges[i].To > duration + 0.5)
            {
                warnings.Add($"Range {ranges[i]} ends after the recording ({duration.ToString("F1", CultureInfo.InvariantCulture)}s); clipped.");
                ranges[i] = ranges[i] with { To = Math.Round(duration, 2) };
            }
        }

        string? shotsJson = null;
        string? shotsPath = spec.ShotsPath;
        if (shotsPath != null)
        {
            if (!File.Exists(shotsPath)) throw new NcError("file_not_found", $"Shot list \"{shotsPath}\" does not exist.", null, invalid: true);
            var validation = ShotValidator.ValidateFile(shotsPath, duration);
            if (!validation.Valid)
                throw new NcError("invalid_shots", "The shot list has errors: " + string.Join(" ", validation.Issues.Where(x => x.IsError).Select(x => $"{x.Path}: {x.Message}")),
                    "Run nr shots validate <file> for details.", invalid: true);
            warnings.AddRange(validation.Issues.Select(x => $"shots {x.Path}: {x.Message}"));
            shotsPath = Path.GetFullPath(shotsPath);
        }
        else if (spec.Style is { } styleId)
        {
            var list = BuildShots(styleId, moment, ranges, details.Events);
            shotsJson = list.ToJson();
        }

        var (width, height) = ParseSize(spec.Size);
        if (spec.Fps < 1 || spec.Fps > 240) throw new NcError("bad_fps", $"{spec.Fps} fps is not supported.", "Use 30 or 60.", invalid: true);
        var encoder = spec.Encoder ?? settings.Encoder;
        if (!RenderJobWriter.Encoders.Contains(encoder)) throw new NcError("bad_encoder", $"Unknown encoder \"{encoder}\".", "Use auto, nvenc, vulkan or x264.", invalid: true);
        var quality = spec.Quality ?? settings.Quality;
        if (quality < 0 || quality > 51) throw new NcError("bad_quality", $"Quality {quality} is out of range.", "Use 0 to 51; 19 is the default, lower is better.", invalid: true);
        var ffmpeg = StatusService.ResolveFfmpeg(settings, game);

        return new ClipPlan
        {
            DemoPath = Path.GetFullPath(demoPath),
            Details = details,
            Moment = moment,
            Ranges = ranges,
            Width = width,
            Height = height,
            Fps = spec.Fps,
            ShotsJson = shotsJson,
            ShotsPath = shotsPath,
            Hud = spec.Hud || settings.ShowHud,
            OutputDir = Path.GetFullPath(spec.OutDir ?? settings.EffectiveOutputDir),
            FfmpegPath = ffmpeg,
            Encoder = encoder,
            Quality = quality,
            Name = string.IsNullOrWhiteSpace(spec.Name) ? null : spec.Name.Trim(),
            Warnings = warnings,
        };
    }

    /// <summary>The shot list a style makes for these ranges (one preset per range, joined).</summary>
    public static ShotList BuildShots(string styleId, DemoEvent? moment, IReadOnlyList<TimeRange> ranges, IReadOnlyList<DemoEvent>? events)
    {
        if (ShotPresets.Style(styleId) == null)
            throw new NcError("bad_style", $"Unknown style \"{styleId}\".", $"Use {ShotPresets.StyleList}.", invalid: true);
        var list = new ShotList();
        foreach (var r in ranges) list.Shots.AddRange(ShotPresets.Build(styleId, moment, r.From, r.To, events).Shots);
        return list;
    }

    /// <summary>The moment (event or second) a clip is framed around, and its range.</summary>
    public static (DemoEvent? Moment, TimeRange Range) Moment(DemoDetails details, int? eventIndex, double? at, double pre, double post)
    {
        DemoEvent? moment = null;
        double t;
        if (eventIndex != null && at != null)
            throw new NcError("no_moment", "Use --event or --at, not both.", null, invalid: true);
        if (eventIndex is { } idx)
        {
            if (idx < 0 || idx >= details.Events.Count)
                throw new NcError("bad_event", $"Event #{idx} does not exist (this recording has {details.Events.Count}).", "Run nr events <demo> to see the indexes.", invalid: true);
            moment = details.Events[idx];
            t = moment.T;
        }
        else if (at is { } a)
        {
            if (a < 0 || !double.IsFinite(a)) throw new NcError("bad_start", $"{a} is not a replay time.", "Use seconds from 'nr events', 0 or more.", invalid: true);
            t = a;
        }
        else throw new NcError("no_moment", "Say which moment: --event <index>, --at <seconds> or --range <from:to>.", "Run nr events <demo> --moments to see the indexes.", invalid: true);
        if (pre < 0 || post <= 0) throw new NcError("bad_range", "--pre must be 0 or more and --post more than 0.", null, invalid: true);
        var (from, to) = ShotPresets.RangeAround(t, pre, post, details.Summary.DurationS);
        return (moment, new TimeRange(from, to));
    }

    public static (int Width, int Height) ParseSize(string text)
    {
        if (ShotPresets.Resolution(text) is { } preset) return (preset.Width, preset.Height);
        var parts = text.ToLowerInvariant().Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 16 && h >= 16 && w <= 7680 && h <= 7680)
            return (w, h);
        throw new NcError("bad_size", $"\"{text}\" is not a size.", "Use WxH like 1920x1080, or 1080p, vertical, 4k.", invalid: true);
    }
}

/// <summary>
/// The <c>nr</c> command line for a clip, so a clip can be repeated, scripted or changed by an agent. Only options that differ from the defaults are written.
/// </summary>
public static class ClipCommand
{
    /// <summary>Arguments for <c>nr render</c> (without "nr").</summary>
    public static List<string> RenderArgs(ClipSpec spec, string? demoDir = null)
    {
        var args = new List<string> { "render", DemoArg(spec.Demo, demoDir) };
        AddMoment(args, spec);
        if (spec.ShotsPath != null) { args.Add("--shots"); args.Add(spec.ShotsPath); }
        else if (spec.Style != null) { args.Add("--style"); args.Add(spec.Style); }
        if (!spec.Size.Equals("1080p", StringComparison.OrdinalIgnoreCase)) { args.Add("--size"); args.Add(spec.Size); }
        if (spec.Fps != 60) { args.Add("--fps"); args.Add(spec.Fps.ToString(CultureInfo.InvariantCulture)); }
        if (spec.Hud) args.Add("--hud");
        if (spec.Encoder != null) { args.Add("--encoder"); args.Add(spec.Encoder); }
        if (spec.Quality != null) { args.Add("--quality"); args.Add(spec.Quality.Value.ToString(CultureInfo.InvariantCulture)); }
        if (!string.IsNullOrWhiteSpace(spec.Name)) { args.Add("--name"); args.Add(spec.Name.Trim()); }
        if (spec.OutDir != null) { args.Add("--out"); args.Add(spec.OutDir); }
        return args;
    }

    /// <summary>Arguments for <c>nr shots make</c>: the shot list the style generates for this clip.</summary>
    public static List<string> ShotsMakeArgs(ClipSpec spec, string? demoDir = null)
    {
        var args = new List<string> { "shots", "make", DemoArg(spec.Demo, demoDir) };
        AddMoment(args, spec);
        args.Add("--style");
        args.Add(spec.Style ?? "cinematic_chase");
        return args;
    }

    /// <summary>Arguments for <c>nr watch</c> starting at <paramref name="startAt"/> with an optional shot list.</summary>
    public static List<string> WatchArgs(string demo, double? startAt, string? shotsPath, string? demoDir = null)
    {
        var args = new List<string> { "watch", DemoArg(demo, demoDir) };
        if (startAt is > 0) { args.Add("--at"); args.Add(Num(startAt.Value)); }
        if (shotsPath != null) { args.Add("--shots"); args.Add(shotsPath); }
        return args;
    }

    /// <summary>Arguments for <c>nr open</c>: the in-game replay viewer at <paramref name="at"/>, focused on a moment.</summary>
    public static List<string> OpenArgs(string demo, double? at = null, double? focus = null, string? demoDir = null)
    {
        var args = new List<string> { "open", DemoArg(demo, demoDir) };
        if (at is > 0) { args.Add("--at"); args.Add(Seconds(at.Value)); }
        if (focus is >= 0) { args.Add("--focus"); args.Add(Seconds(focus.Value)); }
        return args;
    }

    public static string Open(string demo, double? at = null, double? focus = null, string? demoDir = null) => Line(OpenArgs(demo, at, focus, demoDir));

    public static string Render(ClipSpec spec, string? demoDir = null) => Line(RenderArgs(spec, demoDir));
    public static string ShotsMake(ClipSpec spec, string? demoDir = null) => Line(ShotsMakeArgs(spec, demoDir));

    /// <summary>"nr" plus the arguments, quoted for the current platform's shell.</summary>
    public static string Line(IEnumerable<string> args, bool? windows = null) =>
        "nr " + string.Join(' ', args.Select(a => Quote(a, windows ?? OperatingSystem.IsWindows())));

    /// <summary>Quotes one argument: double quotes on Windows (cmd and PowerShell), single quotes elsewhere (POSIX shells).</summary>
    public static string Quote(string arg, bool windows)
    {
        if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_.:,/=+@".Contains(c))) return arg;
        if (!windows) return "'" + arg.Replace("'", "'\\''") + "'";
        // Windows argument rules: backslashes are literal unless a quote follows, so a folder like D:\ needs its
        // trailing backslash doubled or it escapes the closing quote.
        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>Splits a command line written by <see cref="Line"/> back into arguments (used by tests and the docs examples).</summary>
    public static List<string> Split(string line)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool any = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c))
            {
                if (any) { args.Add(current.ToString()); current.Clear(); any = false; }
            }
            else if (c == '\'')
            {
                any = true;
                int end = line.IndexOf('\'', i + 1);
                if (end < 0) end = line.Length;
                current.Append(line, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '"')
            {
                any = true;
                i++;
                while (i < line.Length && line[i] != '"')
                {
                    int run = 0;
                    while (i < line.Length && line[i] == '\\') { run++; i++; }
                    if (i < line.Length && line[i] == '"')
                    {
                        // 2n backslashes before a quote are n backslashes and the closing quote; 2n+1 are n and a literal quote.
                        current.Append('\\', run / 2);
                        if (run % 2 == 0) break;
                        current.Append('"');
                        i++;
                    }
                    else if (run > 0) current.Append('\\', run);
                    else current.Append(line[i++]);
                }
            }
            else if (c == '\\' && i + 1 < line.Length) { any = true; current.Append(line[++i]); }
            else { any = true; current.Append(c); }
        }
        if (any) args.Add(current.ToString());
        if (args.Count > 0 && args[0] == "nr") args.RemoveAt(0);
        return args;
    }

    private static void AddMoment(List<string> args, ClipSpec spec)
    {
        if (spec.Ranges != null) { args.Add("--range"); args.Add(spec.Ranges); return; }
        if (spec.EventIndex is { } idx) { args.Add("--event"); args.Add(idx.ToString(CultureInfo.InvariantCulture)); }
        else if (spec.At is { } at) { args.Add("--at"); args.Add(Num(at)); }
        if (spec.Pre != 5) { args.Add("--pre"); args.Add(Num(spec.Pre)); }
        if (spec.Post != 6) { args.Add("--post"); args.Add(Num(spec.Post)); }
    }

    /// <summary>A recording in the recordings folder is named by its file name; anything else by its full path.</summary>
    private static string DemoArg(string demo, string? demoDir)
    {
        if (demoDir == null || !Path.IsPathRooted(demo)) return demo;
        var dir = Path.GetDirectoryName(Path.GetFullPath(demo));
        return dir != null && string.Equals(Path.GetFullPath(demoDir).TrimEnd(Path.DirectorySeparatorChar), dir.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? Path.GetFileName(demo)
            : demo;
    }

    private static string Seconds(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
    private static string Num(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
