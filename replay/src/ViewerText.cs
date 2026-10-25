#nullable disable
namespace NuclearReplay.Playback
{
    // Viewer text and rules that need no Unity, so NuclearReplay.Core.Tests can check them (it links this file).
    internal static class ViewerText
    {
        // The status line names the selected moment only while the playhead is near it. Jumping to a moment starts
        // 5 s before it and its clip ends 6 s after; a few seconds more keep the label from blinking at the edges.
        public const double MomentBefore = 8, MomentAfter = 9;

        public static bool MomentInView(double now, double momentTime) =>
            now >= momentTime - MomentBefore && now <= momentTime + MomentAfter;

        // The message after "Clip this". Encoder and frame rates go to the BepInEx log only (ClipRecorder).
        public static string ClipToast(bool saved, string fileName, bool hasSound, string error)
        {
            if (!saved) return "Clip failed: " + (string.IsNullOrEmpty(error) ? "unknown error" : error);
            return (hasSound ? "Clip saved: " : "Clip saved without sound: ") + fileName;
        }

        // Disk size for the Replays list: "820 KB", "312 MB", "1.4 GB".
        public static string Size(long bytes)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString(bytes >= 10L << 30 ? "0" : "0.0", c) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0", c) + " MB";
            return System.Math.Max(1, (bytes + 1023) / 1024).ToString(c) + " KB";
        }

        // Header of the Replays list: how many recordings and how much disk they take.
        public static string LibrarySummary(int count, long bytes) =>
            count == 0 ? "No recordings" : count + (count == 1 ? " recording, " : " recordings, ") + Size(bytes) + " on disk";

        // The question the Delete button asks before it removes a recording for good.
        public static string DeleteQuestion(string title, long bytes) =>
            "Delete \"" + title + "\" (" + Size(bytes) + ") for good? Clips made from it stay.";

        // Files that belong to a recording: the demo, the shot list the director picks up beside it
        // (<demo>.shots.json, ReplayPlugin) and the Camera Studio path (<demo>.path.json). Clips are the player's
        // own videos and are never deleted.
        public static string[] RecordingFiles(string demoPath) =>
            new[] { demoPath, System.IO.Path.ChangeExtension(demoPath, ".shots.json"), StudioPathFile(demoPath) };

        // Where Camera Studio keeps a recording's camera path.
        public static string StudioPathFile(string demoPath) => System.IO.Path.ChangeExtension(demoPath, ".path.json");
    }
}
