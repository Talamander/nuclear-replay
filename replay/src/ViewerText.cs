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

        // Header of the Replays list while recordings are still being read in the background.
        public static string LibrarySummary(int count, long bytes, int pending) =>
            pending <= 0 ? LibrarySummary(count, bytes) : LibrarySummary(count, bytes) + ", reading details (" + pending + " to go)";

        // Under the list: how many recordings are ticked and how much disk they take. Empty when none are.
        public static string SelectionSummary(int count, long bytes) =>
            count <= 0 ? "" : count + " selected, " + Size(bytes);

        // The Delete button: "Delete" for the highlighted recording, "Delete 3" when recordings are ticked.
        public static string DeleteLabel(int ticked) => ticked > 0 ? "Delete " + ticked : "Delete";

        // The question before a bulk delete: how many files and how much disk.
        public static string BulkDeleteQuestion(int count, long bytes) =>
            "Delete " + count + (count == 1 ? " recording (" : " recordings (") + Size(bytes) + ") for good? Clips made from them stay.";

        // After a bulk delete: what went, and each recording that could not be deleted with why (the first three).
        public static string BulkDeleteResult(int deleted, long bytes, System.Collections.Generic.IList<System.Collections.Generic.KeyValuePair<string, string>> failed)
        {
            string text = deleted > 0 ? "Deleted " + deleted + (deleted == 1 ? " recording (" : " recordings (") + Size(bytes) + ")." : "Nothing was deleted.";
            int n = failed?.Count ?? 0;
            if (n == 0) return text;
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < n && i < 3; i++) parts.Add(failed[i].Key + " (" + failed[i].Value + ")");
            return text + " " + (n == 1 ? "1 could not be deleted: " : n + " could not be deleted: ") + string.Join(", ", parts)
                + (n > 3 ? " and " + (n - 3) + " more" : "") + ". They stay ticked.";
        }

        // The recorder names files <yyyyMMddTHHmmssZ>-<mission>.ncdemo. Until a recording's header is read, the list
        // shows the mission part as its title and the time stamp as its date.
        public static string TitleFromFileName(string fileName)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(fileName ?? "");
            return StampFromFileName(fileName) != null && name.Length > 17 && name[16] == '-' ? name.Substring(17) : name;
        }

        public static System.DateTime? StampFromFileName(string fileName)
        {
            if (fileName == null || fileName.Length < 16) return null;
            return System.DateTime.TryParseExact(fileName.Substring(0, 16), "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var stamp) ? stamp : (System.DateTime?)null;
        }

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
