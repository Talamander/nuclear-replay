namespace NuclearReplay.Playback
{
    // A second seek must not tear down a scene while the first seek is still loading it.
    // Keep only the latest request, and consume it once the client world is complete.
    internal sealed class PendingViewerSeek
    {
        private bool pending, keepClip;
        private double target, requestedAt;

        public void Request(double time, bool preserveClip, double now)
        {
            pending = true;
            target = time;
            keepClip = preserveClip;
            requestedAt = now;
        }

        public bool TryTake(bool sceneReady, bool sendingBurst, bool settlingBurst,
            out double time, out bool preserveClip, out double requested)
        {
            time = target;
            preserveClip = keepClip;
            requested = requestedAt;
            if (!pending || !SeekMath.CanStopJump(sceneReady, sendingBurst, settlingBurst)) return false;
            pending = false;
            return true;
        }

        public void Clear() => pending = false;
    }
}
