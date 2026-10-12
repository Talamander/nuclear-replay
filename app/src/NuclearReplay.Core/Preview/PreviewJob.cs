using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NuclearReplay.Core.Clips;
using NuclearReplay.Core.Game;

namespace NuclearReplay.Core.Preview;

/// <summary>
/// The preview job file read by the replay plugin (-ncPreview, docs/PRODUCT-CONTRACT.md section 4). Paths are game-side.
/// Mode "preview" streams frames to nr over 127.0.0.1; mode "thumbs" writes one JPEG per item and quits.
/// </summary>
public sealed class PreviewJobFile
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("mode")] public string Mode { get; init; } = "preview";
    [JsonPropertyName("demo")] public required string Demo { get; init; }
    [JsonPropertyName("shots")] public string? Shots { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
    [JsonPropertyName("fps")] public int Fps { get; init; }
    [JsonPropertyName("quality")] public int Quality { get; init; }
    [JsonPropertyName("hud")] public bool Hud { get; init; }
    [JsonPropertyName("mute")] public bool Mute { get; init; } = true;
    /// <summary>"offscreen" (default) or "normal".</summary>
    [JsonPropertyName("window")] public string Window { get; init; } = "offscreen";
    /// <summary>Flip rows before JPEG encoding; null lets the plugin decide from the graphics API.</summary>
    [JsonPropertyName("vflip")] public bool? VFlip { get; init; }
    [JsonPropertyName("preroll")] public double Preroll { get; init; } = 1;
    [JsonPropertyName("progress")] public required string Progress { get; init; }
    [JsonPropertyName("preview")] public PreviewSection? Preview { get; init; }
    [JsonPropertyName("thumbs")] public List<ThumbItem>? Thumbs { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static PreviewJobFile? FromJson(string json) => JsonSerializer.Deserialize<PreviewJobFile>(json, Options);
}

public sealed class PreviewSection
{
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
    [JsonPropertyName("from")] public double From { get; init; }
    [JsonPropertyName("to")] public double To { get; init; }
    [JsonPropertyName("autoplay")] public bool Autoplay { get; init; } = true;
    /// <summary>Allow the plugin to go back in time by reloading the match without restarting the game.</summary>
    [JsonPropertyName("softRestart")] public bool SoftRestart { get; init; } = true;
}

public sealed class ThumbItem
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("t")] public double T { get; init; }
    [JsonPropertyName("out")] public required string Out { get; init; }
}

/// <summary>What to preview. Host-side paths.</summary>
public sealed record PreviewRequest
{
    public required string DemoPath { get; init; }
    public required double From { get; init; }
    public required double To { get; init; }
    /// <summary>Shot list content (a camera style's list); written next to the job.</summary>
    public string? ShotsJson { get; init; }
    public string? ShotsPath { get; init; }
    public int Width { get; init; } = 854;
    public int Height { get; init; } = 480;
    public int Fps { get; init; } = 20;
    public int Quality { get; init; } = 70;
    public bool Hud { get; init; }
    public bool? VFlip { get; init; }
    public bool Autoplay { get; init; } = true;
    /// <summary>Keep the game window where it opens instead of moving it off-screen (debugging).</summary>
    public bool ShowWindow { get; init; }
}

/// <summary>Thumbnails to make in one game run. Host-side paths.</summary>
public sealed record ThumbsRequest
{
    public required string DemoPath { get; init; }
    public required List<(string Id, double T, string Out)> Items { get; init; }
    public string? ShotsJson { get; init; }
    public int Width { get; init; } = 320;
    public int Height { get; init; } = 180;
    public int Quality { get; init; } = 80;
    public bool? VFlip { get; init; }
    public bool ShowWindow { get; init; }
}

public sealed class PreparedPreview
{
    public required string Id { get; init; }
    public required string JobDir { get; init; }
    public required string JobFile { get; init; }
    public required string ProgressFile { get; init; }
    public required PreviewJobFile Job { get; init; }
    public required List<string> GameArgs { get; init; }
}

public static class PreviewJobWriter
{
    /// <summary>A random token the plugin must send back, so another program on the machine cannot pose as the game.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static PreparedPreview PreparePreview(PreviewRequest request, string jobsDir, PathMapper mapper, int port, string token)
    {
        CheckSize(request.Width, request.Height);
        if (request.Fps < 1 || request.Fps > 30) throw new NcError("bad_fps", $"{request.Fps} fps is not supported for previews.", "Use 1 to 30.", invalid: true);
        if (request.From < 0 || request.To <= request.From || !double.IsFinite(request.To))
            throw new NcError("bad_range", "The preview needs a start of 0 or more and an end after it.", null, invalid: true);
        var (id, jobDir) = NewJobDir(jobsDir, "preview");
        var shots = WriteShots(jobDir, request.ShotsJson, request.ShotsPath);
        var progress = Path.Combine(jobDir, "progress.json");
        var job = new PreviewJobFile
        {
            Mode = "preview",
            Demo = mapper.ToGamePath(request.DemoPath),
            Shots = shots != null ? mapper.ToGamePath(shots) : null,
            Width = request.Width, Height = request.Height, Fps = request.Fps, Quality = request.Quality,
            Hud = request.Hud, VFlip = request.VFlip, Window = request.ShowWindow ? "normal" : "offscreen",
            Progress = mapper.ToGamePath(progress),
            Preview = new PreviewSection { Port = port, Token = token, From = request.From, To = request.To, Autoplay = request.Autoplay },
        };
        return Finish(id, jobDir, progress, job, mapper, request.Width, request.Height);
    }

    public static PreparedPreview PrepareThumbs(ThumbsRequest request, string jobsDir, PathMapper mapper)
    {
        CheckSize(request.Width, request.Height);
        if (request.Items.Count == 0) throw new NcError("no_thumbs", "There is nothing to make thumbnails of.", "Pick a recording with moments, or pass --event.", invalid: true);
        var (id, jobDir) = NewJobDir(jobsDir, "thumbs");
        var shots = WriteShots(jobDir, request.ShotsJson, null);
        var progress = Path.Combine(jobDir, "progress.json");
        var job = new PreviewJobFile
        {
            Mode = "thumbs",
            Demo = mapper.ToGamePath(request.DemoPath),
            Shots = shots != null ? mapper.ToGamePath(shots) : null,
            Width = request.Width, Height = request.Height, Fps = 1, Quality = request.Quality,
            VFlip = request.VFlip, Window = request.ShowWindow ? "normal" : "offscreen",
            Progress = mapper.ToGamePath(progress),
            Thumbs = request.Items.Select(i => new ThumbItem { Id = i.Id, T = Math.Round(i.T, 3), Out = mapper.ToGamePath(i.Out) }).ToList(),
        };
        // The plugin renders thumbnails at twice their size and scales down.
        return Finish(id, jobDir, progress, job, mapper, request.Width * 2, request.Height * 2);
    }

    private static PreparedPreview Finish(string id, string jobDir, string progress, PreviewJobFile job, PathMapper mapper, int windowWidth, int windowHeight)
    {
        var jobFile = Path.Combine(jobDir, "job.json");
        Json.WriteFileAtomic(jobFile, job.ToJson());
        return new PreparedPreview
        {
            Id = id, JobDir = jobDir, JobFile = jobFile, ProgressFile = progress, Job = job,
            GameArgs = GameArgs(mapper.ToGamePath(jobFile), windowWidth, windowHeight),
        };
    }

    /// <summary>The preview arguments plus Unity's own window options, so the game opens as a small window, not full screen.</summary>
    public static List<string> GameArgs(string gameJobPath, int width, int height) => new()
    {
        "-ncPreview", gameJobPath,
        "-screen-fullscreen", "0",
        "-screen-width", width.ToString(CultureInfo.InvariantCulture),
        "-screen-height", height.ToString(CultureInfo.InvariantCulture),
    };

    private static void CheckSize(int width, int height)
    {
        if (width < 16 || height < 16 || width > 1920 || height > 1080)
            throw new NcError("bad_size", $"{width}x{height} is not a preview size.", "Use up to 1920x1080; 854x480 is the default.", invalid: true);
    }

    private static (string Id, string Dir) NewJobDir(string jobsDir, string kind)
    {
        var id = kind + "-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(jobsDir, id);
        Directory.CreateDirectory(dir);
        return (id, dir);
    }

    private static string? WriteShots(string jobDir, string? json, string? path)
    {
        if (json == null) return path != null ? Path.GetFullPath(path) : null;
        var shots = Path.Combine(jobDir, "shots.json");
        Json.WriteFileAtomic(shots, json);
        return shots;
    }
}
