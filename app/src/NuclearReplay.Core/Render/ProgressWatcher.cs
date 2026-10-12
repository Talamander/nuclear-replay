using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuclearReplay.Core.Render;

/// <summary>progress.json written by the replay plugin (docs/PRODUCT-CONTRACT.md §2).</summary>
public sealed record RenderProgress
{
    /// <summary>loading, seeking, rendering, encoding, done or error. The watcher adds "waiting" (no file yet) and "lost" (game gone).</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "waiting";
    [JsonPropertyName("range")] public int Range { get; init; }
    [JsonPropertyName("ranges")] public int Ranges { get; init; }
    [JsonPropertyName("frame")] public long Frame { get; init; }
    [JsonPropertyName("frames")] public long Frames { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }

    [JsonIgnore] public bool IsFinal => State is "done" or "error" or "lost";

    /// <summary>0..1 across all ranges.</summary>
    [JsonIgnore]
    public double Fraction
    {
        get
        {
            if (State == "done") return 1;
            if (Ranges <= 0) return 0;
            double inRange = Frames > 0 ? Math.Clamp((double)Frame / Frames, 0, 1) : 0;
            if (State == "encoding") inRange = 1;
            return Math.Clamp((Range + inRange) / Ranges, 0, 1);
        }
    }

    /// <summary>Plain-English status for people.</summary>
    [JsonIgnore]
    public string Description => State switch
    {
        "waiting" => "Starting the game...",
        "loading" => "Loading the match...",
        "seeking" => Ranges > 1 ? $"Skipping ahead to clip {Range + 1} of {Ranges}..." : "Skipping ahead to your clip...",
        "rendering" => (Ranges > 1 ? $"Recording clip {Range + 1} of {Ranges}: " : "Recording: ") + (Frames > 0 ? $"frame {Frame} of {Frames}" : $"frame {Frame}"),
        "encoding" => "Saving the video...",
        "done" => "Finished",
        "error" => "Failed: " + (Error ?? "unknown error"),
        "lost" => "The game closed before the clip was finished.",
        _ => State,
    };

    public static RenderProgress? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<RenderProgress>(stream);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // mid-rename on some file systems; the next poll gets it
        }
    }
}

/// <summary>Polls progress.json until the job finishes, fails, or the game goes away.</summary>
public sealed class ProgressWatcher
{
    private readonly string progressFile;
    private readonly Func<bool> gameRunning;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>How long to wait for the game to appear (Steam can be slow to start it).</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>How long the game may be gone before the job counts as lost.</summary>
    public TimeSpan ExitGrace { get; init; } = TimeSpan.FromSeconds(10);

    public ProgressWatcher(string progressFile, Func<bool>? gameRunning = null)
    {
        this.progressFile = progressFile;
        this.gameRunning = gameRunning ?? Game.ProcessInfo.IsGameRunning;
    }

    public async Task<RenderProgress> WatchAsync(Action<RenderProgress>? onChange = null, CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        DateTime? goneSince = null;
        bool seenGame = false;
        RenderProgress last = new();
        string? lastJson = null;
        onChange?.Invoke(last);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = RenderProgress.TryRead(progressFile);
            if (current != null)
            {
                var json = JsonSerializer.Serialize(current);
                if (json != lastJson)
                {
                    lastJson = json;
                    last = current;
                    onChange?.Invoke(current);
                }
                if (current.IsFinal) return current;
            }
            bool running = gameRunning();
            if (running) { seenGame = true; goneSince = null; }
            else if (seenGame || current != null)
            {
                goneSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - goneSince > ExitGrace)
                {
                    // One last read: the plugin may have written "done" just before quitting.
                    var final = RenderProgress.TryRead(progressFile);
                    if (final is { IsFinal: true }) { onChange?.Invoke(final); return final; }
                    var lost = last with { State = "lost" };
                    onChange?.Invoke(lost);
                    return lost;
                }
            }
            else if (DateTime.UtcNow - started > StartupTimeout)
            {
                var timeout = last with { State = "error", Error = "The game did not start. Make sure Steam is running and signed in." };
                onChange?.Invoke(timeout);
                return timeout;
            }
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
