using System.Globalization;
using System.Text.Json.Nodes;

namespace NuclearReplay.Core.Preview;

/// <summary>
/// Where a live preview is. The plugin sends starting, loading, seeking, ready, playing, paused, ended, restarting and
/// error; nr adds launching (game not connected yet) and closed (the game went away).
/// </summary>
public sealed record PreviewStatus
{
    public string State { get; init; } = "idle";
    /// <summary>Replay seconds where the game is now.</summary>
    public double T { get; init; }
    public double From { get; init; }
    public double To { get; init; }
    /// <summary>Length of the recording once the game has loaded it.</summary>
    public double? End { get; init; }
    /// <summary>0..1 while skipping ahead.</summary>
    public double? Fraction { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public string? Fix { get; init; }
    public bool Paused { get; init; }
    public int Fps { get; init; }
    /// <summary>Game-side frame cost in milliseconds (averaged): capture on the main thread, JPEG encode on a worker.</summary>
    public double? GrabMs { get; init; }
    public double? EncodeMs { get; init; }
    /// <summary>Frames per second the game itself renders (averaged).</summary>
    public double? GameFps { get; init; }
    /// <summary>Time since the preview was started (app side).</summary>
    public TimeSpan Elapsed { get; init; }

    public bool IsLive => State is "ready" or "playing" or "paused" or "ended";
    public bool IsWaiting => State is "launching" or "starting" or "loading" or "seeking" or "restarting";
    public bool IsFinal => State is "error" or "closed";

    public static PreviewStatus FromMessage(JsonObject o, TimeSpan elapsed) => new()
    {
        State = (string?)o["state"] ?? "loading",
        T = Num(o["t"]) ?? 0,
        From = Num(o["from"]) ?? 0,
        To = Num(o["to"]) ?? 0,
        End = Num(o["end"]),
        Fraction = Num(o["fraction"]),
        Message = (string?)o["message"],
        Error = (string?)o["error"],
        Paused = o["paused"] is JsonValue p && p.TryGetValue<bool>(out var b) && b,
        Fps = (int)(Num(o["fps"]) ?? 0),
        GrabMs = Num(o["grab_ms"]),
        EncodeMs = Num(o["encode_ms"]),
        GameFps = Num(o["game_fps"]),
        Elapsed = elapsed,
    };

    private static double? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    /// <summary>Plain-English text for the loading line, used by nr preview.</summary>
    public string Describe()
    {
        string waited = Elapsed.TotalSeconds >= 1 ? $" ({Clock(Elapsed.TotalSeconds)})" : "";
        return State switch
        {
            "idle" => "Not started",
            "launching" => "Starting Nuclear Option in the background" + waited,
            "starting" => "Nuclear Option is starting" + waited,
            "loading" => "Loading the match. This takes a few minutes" + waited,
            "seeking" => $"Skipping ahead to {Clock(From)}" + (Fraction is { } f ? $" ({Math.Round(f * 100):0}%)" : "") + waited,
            "restarting" => $"Going back to {Clock(From)}: reloading the match" + waited,
            "ready" => $"Ready at {Clock(T)}. Press play.",
            "playing" => $"Live from the game at {Clock(T)}",
            "paused" => $"Paused at {Clock(T)}",
            "ended" => "End of the clip",
            "closed" => Message ?? "The game closed.",
            "error" => "Preview failed: " + (Error ?? Message ?? "unknown error"),
            _ => State,
        };
    }

    /// <summary>m:ss for replay times.</summary>
    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}

/// <summary>One preview frame: an encoded image (JPEG from the game) and the replay time it shows.</summary>
public sealed record PreviewFrame(double T, long Seq, int Width, int Height, string State, byte[] Image)
{
    public static PreviewFrame FromMessage(JsonObject header, byte[] image) => new(
        header["t"] is JsonValue t && t.TryGetValue<double>(out var time) ? time : 0,
        header["seq"] is JsonValue s && s.TryGetValue<long>(out var seq) ? seq : 0,
        header["w"] is JsonValue w && w.TryGetValue<int>(out var width) ? width : 0,
        header["h"] is JsonValue h && h.TryGetValue<int>(out var height) ? height : 0,
        (string?)header["state"] ?? "playing",
        image);
}

/// <summary>Commands from nr to the plugin (JSON messages on the preview socket).</summary>
public static class PreviewCommands
{
    public static JsonObject Play() => new() { ["type"] = "play" };
    public static JsonObject Pause() => new() { ["type"] = "pause" };
    public static JsonObject Quit() => new() { ["type"] = "quit" };
    public static JsonObject Stream(int fps) => new() { ["type"] = "stream", ["fps"] = fps };
    public static JsonObject Seek(double t) => new() { ["type"] = "seek", ["t"] = Math.Round(t, 3) };
    public static JsonObject Range(double from, double to) => new() { ["type"] = "range", ["from"] = Math.Round(from, 3), ["to"] = Math.Round(to, 3) };

    public static JsonObject Restart(double? from = null, double? to = null)
    {
        var o = new JsonObject { ["type"] = "restart" };
        if (from != null) o["from"] = Math.Round(from.Value, 3);
        if (to != null) o["to"] = Math.Round(to.Value, 3);
        return o;
    }

    /// <summary>Replaces the camera shot list live. <paramref name="shotListJson"/> is a shot list file's content ({"shots":[...]}).</summary>
    public static JsonObject Shots(string shotListJson)
    {
        var root = JsonNode.Parse(shotListJson) as JsonObject ?? throw new ArgumentException("not a shot list");
        var o = new JsonObject { ["type"] = "shots", ["shots"] = root["shots"]?.DeepClone() ?? new JsonArray() };
        // The root "lighting" defaults (docs/AGENTS.md "Lighting") go along when the list has them.
        if (root["lighting"] is JsonObject lighting) o["lighting"] = lighting.DeepClone();
        return o;
    }

    public static readonly string[] Known = { "play", "pause", "shots", "seek", "range", "restart", "stream", "quit" };
}
