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
    }
}
