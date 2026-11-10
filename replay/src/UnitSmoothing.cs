using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.NetworkTransforms;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Draws every replayed aircraft and missile (bombs included) at a filtered position, not only the cine camera's
    // target.
    //
    // A replayed unit is a remote unit: every frame SendTransformBatcher lerps between its 20 Hz snapshots and
    // extrapolates with the snapshot velocity, and the result wobbles about 1 m (up to 5-8 m) along the path from
    // frame to frame (replay/CINE-CAMERA.md, "Jitter fix"). Each unit wobbles on its own. A camera that follows one
    // unit (the game's orbit, chase and map cameras) moves with that unit's wobble, so the unit itself looks steady
    // and everything near it shakes: a bomb just released shakes next to its launcher, and the launcher shakes once
    // the camera follows the bomb.
    //
    // The fix is the cine camera's complementary filter (CineCamera.ReadTarget) for every such unit near the camera:
    // integrate the snapshot velocity, pull toward the game's position with a 0.3 s time constant, and snap on a jump
    // of more than 30 m (respawn, timeline jump). It runs right after the batcher places the units and before the
    // camera (CameraStateManager.LateUpdate has execution order 2), so the camera and every unit agree. The batcher
    // places them again the next frame, so the game's own state is not changed. [Viewer] SmoothUnits = false turns
    // it off.
    [HarmonyPatch(typeof(SendTransformBatcher), "VisualUpdate")]
    internal static class UnitSmoothing
    {
        internal const float TimeConstant = 0.3f, SnapDistance = 30f, Range = 4000f;
        internal static bool Enabled = true;

        // The unit the cine camera already draws at its own filtered pose (smooth_render), and the frame it did.
        internal static Unit CineSmoothed;
        internal static int CineSmoothedFrame = -10;

        private sealed class Track { public Vector3 Pos, VelPrev; public int Frame; }
        private static readonly Dictionary<Unit, Track> tracks = new Dictionary<Unit, Track>();
        private static readonly List<Unit> stale = new List<Unit>();

        [HarmonyPostfix]
        private static void After()
        {
            var replay = ReplayPlugin.Instance;
            if (!Enabled || replay == null || !replay.Running) { if (tracks.Count > 0) tracks.Clear(); return; }
            var camera = Camera.main;
            if (camera == null) return;
            Vector3 cameraAt = camera.transform.position;
            // The replay clock's step this frame, the one the network interpolation clock advanced by (0 when paused).
            float dt = replay.TransformDelta(Time.unscaledDeltaTime);
            int frame = Time.frameCount;
            var units = UnitRegistry.allUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (!(unit is Aircraft) && !(unit is Missile)) continue;
                if (unit == null || unit.disabled || unit.LocalSim || unit.transform == null) continue;
                var rb = unit.rb;
                // Kinematic: on its pylon or parked; the network transform does not move it either.
                if (rb == null || rb.isKinematic) continue;
                if (unit == CineSmoothed && frame - CineSmoothedFrame <= 1) continue;
                Vector3 local = unit.transform.position;
                if ((local - cameraAt).sqrMagnitude > Range * Range) continue;
                Vector3 raw = local.ToGlobalPosition().AsVector3();
                Vector3 v = rb.velocity;
                if (!tracks.TryGetValue(unit, out var track))
                {
                    track = new Track { Pos = raw, VelPrev = v };
                    tracks[unit] = track;
                }
                else if (dt <= 0f || track.Frame < frame - 1 || (raw - track.Pos).sqrMagnitude > SnapDistance * SnapDistance)
                    track.Pos = raw;
                else
                {
                    track.Pos += 0.5f * (v + track.VelPrev) * dt;
                    track.Pos += (raw - track.Pos) * Mathf.Min(1f, dt / TimeConstant);
                    unit.transform.position = new GlobalPosition(track.Pos).ToLocalPosition();
                }
                track.VelPrev = v;
                track.Frame = frame;
            }
            if (tracks.Count > 0 && (frame & 63) == 0)
            {
                stale.Clear();
                foreach (var pair in tracks) if (pair.Key == null || frame - pair.Value.Frame > 60) stale.Add(pair.Key);
                foreach (var unit in stale) tracks.Remove(unit);
            }
        }
    }
}
