#nullable disable
using System;
using System.Globalization;

namespace NuclearReplay.Playback
{
    // Viewer timeline jumps (ReplayPlugin.Seek.cs), the parts that need no Unity, so NuclearReplay.Core.Tests can
    // check them (it links this file).
    //
    // A jump fast-forwards the replay in fixed steps (FastForwardSpeed / FastForwardStepFps replay seconds per game
    // frame, the same steps as before 0.6). What changed in 0.6 is how many of those frames are drawn: while a viewer
    // jump runs, only a few frames a second are rendered (Unity's OnDemandRendering) and physics runs at a coarser
    // step (JumpPhysicsStep), so the game spends less time on frames nobody watches.
    internal static class SeekMath
    {
        // Replay seconds per game frame while jumping: 8/60 s. Unchanged from 0.5 so the game's per-frame state
        // (physics steps per frame, Update delta) during a jump is the same as before; only drawing is skipped.
        public const float FastForwardSpeed = 8f;
        public const int FastForwardStepFps = 60;

        // Physics step while a viewer jump fast-forwards: 1/20 s instead of the game's 1/60 s, so a jump runs a third
        // of the physics steps. Physics is most of a jump's cost (about two thirds at 1/60 s). The replay's network
        // messages still set every unit's position, state, targets and kills; measured 3 s after the target, units,
        // states, missile targets, the followed unit and the Target HUD match a jump at the game's own step
        // (seek/RESULTS.md, 2026-10-30). Clip recordings and renders keep the game's step.
        public const float JumpPhysicsStep = 0.05f;

        // The physics step to use during a jump: never smaller than the game's own.
        public static float PhysicsStep(float gameStep, float jumpStep)
        {
            if (float.IsNaN(jumpStep) || jumpStep <= 0) return gameStep;
            return Math.Max(gameStep, jumpStep);
        }

        // Frames drawn per second of wall time while jumping: enough for the progress text and a moving picture.
        public const double DrawnPerSecond = 12;
        public const int MaxRenderInterval = 60;

        // Draw one frame in this many while jumping, from the game's current frame rate (frames per wall second).
        public static int RenderInterval(double framesPerSecond)
        {
            if (double.IsNaN(framesPerSecond) || framesPerSecond <= DrawnPerSecond) return 1;
            int n = (int)Math.Round(framesPerSecond / DrawnPerSecond);
            return Math.Max(1, Math.Min(MaxRenderInterval, n));
        }

        // How far a jump is, 0 to 1. A backward jump reloads the mission and plays from the start, so `from` is 0
        // once the replay runs again.
        public static double Fraction(double from, double now, double target)
        {
            if (target <= from) return 1;
            double f = (now - from) / (target - from);
            return f < 0 ? 0 : f > 1 ? 1 : f;
        }

        // The label over the timeline while a jump runs.
        //   "Jumping to 25:00 (reloading the mission)..."         backward jump, before the replay runs again
        //   "Jumping to 25:00...  43%   Space stops here"         fast-forwarding
        public static string Label(string targetClock, bool reloading, double fraction, bool canStop)
        {
            if (reloading) return "Jumping to " + targetClock + " (reloading the mission)...";
            int pct = (int)Math.Floor(Math.Max(0, Math.Min(1, fraction)) * 100);
            return "Jumping to " + targetClock + "...  " + pct.ToString(CultureInfo.InvariantCulture) + "%" + (canStop ? "   Space stops here" : "");
        }

        // Replay seconds per wall second, for the log line at the end of a jump.
        public static double Rate(double replaySeconds, double wallSeconds) => wallSeconds > 0 ? replaySeconds / wallSeconds : 0;
    }
}
