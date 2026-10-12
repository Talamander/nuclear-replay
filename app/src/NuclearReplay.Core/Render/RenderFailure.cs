using NuclearReplay.Core.Game;

namespace NuclearReplay.Core.Render;

/// <summary>Plain-language explanation of a failed render: what went wrong, what to try, and where the logs are.</summary>
public static class RenderFailure
{
    /// <summary>The game's BepInEx log, where the replay plugin writes render errors.</summary>
    public static string? GameLog(GameInstall? game) => game == null ? null : Path.Combine(game.GameDir, "BepInEx", "LogOutput.log");

    /// <summary>A one-paragraph fix for a failed render, naming the log and the job folder.</summary>
    public static string Fix(GameInstall? game, string? jobDir, string? error = null)
    {
        var parts = new List<string>();
        if (Advice(error) is { } advice) parts.Add(advice);
        if (GameLog(game) is { } log) parts.Add($"Details are in the game's mod log: {log}");
        if (jobDir != null) parts.Add($"Job files: {jobDir}");
        return string.Join(" ", parts);
    }

    /// <summary>What to try for the errors people actually hit, or null.</summary>
    public static string? Advice(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var e = error.ToLowerInvariant();
        if (e.Contains("did not start") || e.Contains("steam"))
            return "Start Steam, sign in, and make sure Nuclear Option is not already running. Then try again.";
        if (e.Contains("closed before") || e.Contains("lost"))
            return "Leave the game running until the clip is saved. If it crashed, try again; if it keeps crashing, send diagnostics.";
        if (e.Contains("ffmpeg") || e.Contains("encoder") || e.Contains("nvenc"))
            return "The video encoder failed. In Settings, set Encoder to x264 and try again.";
        if (e.Contains("disk") || e.Contains("space"))
            return "The disk may be full. Free some space or choose another video folder: nr settings set output_dir <folder>.";
        if (e.Contains("demo") || e.Contains("recording"))
            return "The recording could not be replayed. Check that the file still exists and that the mod version matches the one that recorded it.";
        return "Try again. If it fails again, run nr diagnostics and attach the file to your report.";
    }
}
