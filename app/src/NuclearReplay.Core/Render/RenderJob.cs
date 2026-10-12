using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NuclearReplay.Core.Game;
using NuclearReplay.Shared;

namespace NuclearReplay.Core.Render;

/// <summary>The render job file read by the replay plugin (-ncRenderJob, docs/PRODUCT-CONTRACT.md §2). All paths are game-side paths.</summary>
public sealed class RenderJobFile
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("demo")] public required string Demo { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
    [JsonPropertyName("shots")] public string? Shots { get; init; }
    [JsonPropertyName("hud")] public bool Hud { get; init; }
    [JsonPropertyName("ffmpeg")] public string? Ffmpeg { get; init; }
    [JsonPropertyName("encoder")] public string Encoder { get; init; } = "auto";
    [JsonPropertyName("quality")] public int Quality { get; init; } = 19;
    [JsonPropertyName("progress")] public required string Progress { get; init; }
    [JsonPropertyName("ranges")] public required List<RenderJobRange> Ranges { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // shots and ffmpeg are written as null when absent, as the contract shows.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static RenderJobFile? FromJson(string json) => JsonSerializer.Deserialize<RenderJobFile>(json, Options);
}

public sealed class RenderJobRange
{
    [JsonPropertyName("from")] public double From { get; init; }
    [JsonPropertyName("to")] public double To { get; init; }
    [JsonPropertyName("fps")] public int Fps { get; init; }
    [JsonPropertyName("out")] public required string Out { get; init; }
}

/// <summary>A clip range in replay seconds.</summary>
public readonly record struct TimeRange(double From, double To)
{
    public double Length => To - From;

    /// <summary>Parses "a:b[,c:d]" in seconds, e.g. "40:48,1929.5:1952".</summary>
    public static List<TimeRange> ParseList(string text)
    {
        var ranges = new List<TimeRange>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':');
            if (bits.Length != 2
                || !double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var from)
                || !double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var to))
                throw new FormatException($"\"{part}\" is not a range. Use seconds as from:to, e.g. 40:48 or 40:48,120:130.");
            if (!double.IsFinite(from) || !double.IsFinite(to) || from < 0) throw new FormatException($"\"{part}\": times must be 0 or more seconds.");
            if (to <= from) throw new FormatException($"\"{part}\": the end must be after the start.");
            ranges.Add(new TimeRange(from, to));
        }
        if (ranges.Count == 0) throw new FormatException("No range given. Use seconds as from:to, e.g. 40:48.");
        if (FindOverlap(ranges) is { } overlap)
            throw new FormatException($"Ranges {overlap.A} and {overlap.B} overlap. Make them separate, or join them into one range.");
        return ranges;
    }

    /// <summary>The first pair of ranges that share time, or null. The game rejects jobs with overlapping ranges.</summary>
    public static (TimeRange A, TimeRange B)? FindOverlap(IReadOnlyList<TimeRange> ranges)
    {
        for (int i = 0; i < ranges.Count; i++)
            for (int j = i + 1; j < ranges.Count; j++)
                if (ranges[i].From < ranges[j].To && ranges[j].From < ranges[i].To) return (ranges[i], ranges[j]);
        return null;
    }

    public override string ToString() =>
        $"{From.ToString("0.###", CultureInfo.InvariantCulture)}:{To.ToString("0.###", CultureInfo.InvariantCulture)}";
}

/// <summary>Everything needed to render clips of one demo. Host-side paths.</summary>
public sealed class RenderRequest
{
    public required string DemoPath { get; init; }
    public required List<TimeRange> Ranges { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int Fps { get; init; } = 60;
    public string? ShotsPath { get; init; }
    /// <summary>Shot list content to write next to the job instead of <see cref="ShotsPath"/>.</summary>
    public string? ShotsJson { get; init; }
    public bool Hud { get; init; }
    public required string OutputDir { get; init; }
    public string? FfmpegPath { get; init; }
    public string Encoder { get; init; } = "auto";
    public int Quality { get; init; } = 19;
    /// <summary>File name stem for clips; default "&lt;mission&gt; &lt;from&gt;-&lt;to&gt;".</summary>
    public string? Name { get; init; }
}

/// <summary>A prepared job: its folder, job file and expected outputs.</summary>
public sealed class PreparedJob
{
    public required string Id { get; init; }
    public required string JobDir { get; init; }
    public required string JobFile { get; init; }
    public required string ProgressFile { get; init; }
    public required List<string> Outputs { get; init; }
    public required RenderJobFile Job { get; init; }
    /// <summary>Arguments passed to the game.</summary>
    public required List<string> GameArgs { get; init; }
}

public static class RenderJobWriter
{
    public static readonly string[] Encoders = { "auto", "nvenc", "vulkan", "x264" };

    /// <summary>Writes job.json (and shots.json when given inline) into a new folder under <paramref name="jobsDir"/>.</summary>
    public static PreparedJob Prepare(RenderRequest request, string jobsDir, PathMapper mapper, string? missionName = null, bool createOutputDir = true)
    {
        if (!Encoders.Contains(request.Encoder)) throw new ArgumentException($"Unknown encoder \"{request.Encoder}\". Use auto, nvenc, vulkan or x264.");
        if (request.Width < 16 || request.Height < 16 || request.Width > 7680 || request.Height > 7680)
            throw new ArgumentException("Width and height must be between 16 and 7680 pixels.");
        if (request.Fps < 1 || request.Fps > 240) throw new ArgumentException("fps must be between 1 and 240.");
        if (request.Ranges.Count == 0) throw new ArgumentException("No time range to render.");
        if (TimeRange.FindOverlap(request.Ranges) is { } overlap)
            throw new ArgumentException($"Ranges {overlap.A} and {overlap.B} overlap. Make them separate, or join them into one range.");

        var id = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N")[..6];
        var jobDir = Path.Combine(jobsDir, id);
        Directory.CreateDirectory(jobDir);
        if (createOutputDir) Directory.CreateDirectory(request.OutputDir);

        string? shots = request.ShotsPath != null ? Path.GetFullPath(request.ShotsPath) : null;
        if (request.ShotsJson != null)
        {
            shots = Path.Combine(jobDir, "shots.json");
            Json.WriteFileAtomic(shots, request.ShotsJson);
        }
        var stem = Sanitize(request.Name ?? missionName ?? Path.GetFileNameWithoutExtension(request.DemoPath));
        var outputs = new List<string>();
        var ranges = new List<RenderJobRange>();
        foreach (var range in request.Ranges)
        {
            var output = ClipNames.UniquePath(request.OutputDir, ClipNames.Stem(stem, range.From, range.To), outputs.Contains);
            outputs.Add(output);
            ranges.Add(new RenderJobRange { From = range.From, To = range.To, Fps = request.Fps, Out = mapper.ToGamePath(output) });
        }
        var progress = Path.Combine(jobDir, "progress.json");
        var job = new RenderJobFile
        {
            Demo = mapper.ToGamePath(request.DemoPath),
            Width = request.Width,
            Height = request.Height,
            Shots = shots != null ? mapper.ToGamePath(shots) : null,
            Hud = request.Hud,
            Ffmpeg = request.FfmpegPath != null ? mapper.ToGamePath(request.FfmpegPath) : null,
            Encoder = request.Encoder,
            Quality = request.Quality,
            Progress = mapper.ToGamePath(progress),
            Ranges = ranges,
        };
        var jobFile = Path.Combine(jobDir, "job.json");
        Json.WriteFileAtomic(jobFile, job.ToJson());
        return new PreparedJob
        {
            Id = id, JobDir = jobDir, JobFile = jobFile, ProgressFile = progress, Outputs = outputs, Job = job,
            GameArgs = new List<string> { "-ncRenderJob", mapper.ToGamePath(jobFile) },
        };
    }

    public static string Sanitize(string name) => ClipNames.Sanitize(name);
}
