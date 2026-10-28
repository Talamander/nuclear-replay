using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Two-shot camera ("two_shot" shot type): frames the target and a second unit together, for a dogfight or a
    // chase. The camera sits on the target's side of the line between the two, swung off that line to one side and
    // a little above, and looks between them with the narrowest lens that holds both (CameraFraming.TwoShot). When
    // the lens limit is not enough it backs off. The line between the two turns slowly and at a capped rate, so the
    // camera does not swing around when the two pass close to each other, and the side is kept for the whole shot,
    // so the camera never crosses the line.
    //
    // Options (defaults):
    //   second        the second unit, same selectors as target (unit:<id>, name:, type:, host ...). Default: the unit
    //                 the target is fighting (Director.Opponent: its newest missile's target, the shooter of a missile
    //                 aimed at it, its target list, the nearest enemy aircraft, the nearest aircraft). When the second
    //                 is gone the camera keeps its last position for second_hold (2 s), then picks again.
    //   side          left | right | auto (auto: chosen at the start, away from the target's sideways motion)
    //   distance      metres from the target (2.5 times its size, at least 30)
    //   distance_max  metres the camera may back off to so both fit (6 times distance)
    //   azimuth 35    degrees off the line between the two;  elevation 0.15  camera height as a share of the distance
    //   fov_min 20, fov_max 65   lens limits;  margin 0.3  spare frame around the two
    //   swing_smooth 1.0 s and max_swing 40 deg/s   how the line between the two may turn
    //   pos_smooth 0.35 s, look_smooth 0.25 s, zoom_smooth 0.8 s (zooming out goes three times faster)
    //   handheld, rumble, buffet, shake_speed: light shake on top (CinePath.cs), off by default
    internal sealed partial class CineCamera
    {
        private Unit tSecond;
        private Vector3 tB, tBVel, tAxis, tRelVel;
        private bool tHaveB, tHaveAxis;
        private float tSide, tDistance, tDistanceMax, tAzimuth, tElevation, tMargin, tSwingSmooth, tMaxSwing, tSecondHold, tLostAt, tNextLookup;

        private bool IsTwoShot => mode == "two_shot";

        private void SetupTwoShot()
        {
            float size = Units.Size(unit);
            tDistance = Mathf.Max(1f, F("distance", Mathf.Max(30f, 2.5f * size)));
            tDistanceMax = Mathf.Max(tDistance, F("distance_max", tDistance * 6f));
            tAzimuth = F("azimuth", 35f);
            tElevation = F("elevation", 0.15f);
            tMargin = Mathf.Max(0f, F("margin", 0.3f));
            tSwingSmooth = Mathf.Max(0f, F("swing_smooth", 1f));
            tMaxSwing = Mathf.Max(1f, F("max_swing", 40f));
            tSecondHold = Mathf.Max(0f, F("second_hold", 2f));
            fovMin = F("fov_min", 20f);
            fovMax = Mathf.Max(fovMin, F("fov_max", 65f));
            posSmooth = F("pos_smooth", 0.35f);
            tLostAt = -1; tNextLookup = 0;
            tHaveAxis = false;
            tRelVel = Vector3.zero;
            tSecond = PickSecond();
            tHaveB = false;
            ReadSecond(0f);
            string side = (S("side") ?? "auto").ToLowerInvariant();
            Vector3 axis = tHaveB ? tB - targetPos : heading;
            tSide = side == "left" ? -1f : side == "right" ? 1f : CameraFraming.AutoSide(N(axis), N(targetVel));
            log.LogInfo(string.Format(CultureInfo.InvariantCulture, "Cine two_shot: {0} and {1}, side {2}, distance {3:F0} m (max {4:F0}), lens {5:F0}-{6:F0}",
                unit != null ? unit.unitName : "(none)", tSecond != null ? tSecond.unitName : "(no second unit)", tSide > 0 ? "right" : "left",
                tDistance, tDistanceMax, fovMin, fovMax));
        }

        // The shot's second unit, else the one the target is fighting; never the target itself.
        private Unit PickSecond()
        {
            Unit second = null;
            string spec = S("second");
            if (spec != null) second = Resolve?.Invoke(spec);
            if (second == null || second == unit || second.disabled) second = Opponent?.Invoke(unit);
            return second == unit ? null : second;
        }

        // Second unit's position, filtered like the target's (ReadTarget) so its network jitter does not move the frame.
        private void ReadSecond(float dt)
        {
            if (tSecond == null || tSecond.disabled || tSecond.transform == null) return;   // keep the last position
            Vector3 p = tSecond.transform.position.ToGlobalPosition().AsVector3();
            Vector3 v = tSecond.rb != null ? tSecond.rb.velocity : Vector3.zero;
            if (!tHaveB || dt <= 0 || (p - tB).sqrMagnitude > 30f * 30f) tB = p;
            else
            {
                tB += v * dt;
                tB += (p - tB) * Mathf.Min(1f, dt / Mathf.Max(0.05f, targetSmooth));
            }
            tBVel = v;
            tHaveB = true;
        }

        private void StepTwoShot(CameraStateManager cam, float dt, bool preview)
        {
            bool live = unit != null && !unit.disabled;
            if (!live) targetVel = Vector3.zero;
            bool rest = snap || dt > 0.5f;

            // A second unit that is gone: hold on its last position for a moment, then pick again (once a second).
            if (tSecond == null || tSecond.disabled)
            {
                if (tLostAt < 0) tLostAt = shotTime;
                if (live && shotTime - tLostAt >= tSecondHold && shotTime >= tNextLookup)
                {
                    tNextLookup = shotTime + 1f;
                    var next = PickSecond();
                    if (next != null && !next.disabled)
                    {
                        log.LogInfo($"Cine two_shot: second unit now {next.unitName} at t+{shotTime:F2}");
                        tSecond = next; tHaveB = false; tLostAt = -1;
                    }
                }
            }
            else tLostAt = -1;
            ReadSecond(dt);
            if (tSecond == null || tSecond.disabled) tBVel = Vector3.zero;

            // The line from the target to the second unit, turned slowly; the target's heading without a second.
            Vector3 travel = targetVel.sqrMagnitude > 4f ? targetVel.normalized : heading.normalized;
            Vector3 axisWant = tHaveB && (tB - targetPos).sqrMagnitude > 1f ? (tB - targetPos).normalized : travel;
            if (rest || !tHaveAxis) { tAxis = axisWant; tHaveAxis = true; }
            else tAxis = U(CameraFraming.TurnToward(N(tAxis), N(axisWant), dt, tSwingSmooth, tMaxSwing));

            var rc = RenderCamera(cam.mainCamera);
            float aspect = rc != null ? rc.aspect : 16f / 9f;
            float rA = Units.Size(unit) * 0.5f, rB = tSecond != null ? Units.Size(tSecond) * 0.5f : rA;
            var frame = CameraFraming.TwoShot(N(targetPos), N(tB), tHaveB ? N(tAxis) : System.Numerics.Vector3.Zero, N(travel), tSide,
                tDistance, tAzimuth, tElevation, rA, rB, aspect, fovMin, fovMax, tDistanceMax, tMargin);

            // Camera: its offset from the target, critically damped (a fast target does not drag it behind).
            Vector3 relWant = U(frame.Camera) - targetPos;
            if (rest) { camRel = relWant; tRelVel = Vector3.zero; }
            else camRel = Vector3.SmoothDamp(camRel, relWant, ref tRelVel, posSmooth, float.MaxValue, Mathf.Max(dt, 1e-4f));
            camPos = targetPos + camRel;
            float floor = GroundY(camPos) + minAgl;
            if (camPos.y < floor) { camPos.y = floor; camRel = camPos - targetPos; }

            // Look and lens from where the camera really is, so both stay in frame while it moves.
            float need = CameraFraming.NeededFov(N(camPos), N(targetPos), rA, tHaveB ? N(tB) : (System.Numerics.Vector3?)null, rB, aspect, tMargin, out var look);
            Vector3 lookWant = U(look);
            Vector3 lookVelWant = tHaveB ? (targetVel + tBVel) * 0.5f : targetVel;
            if (rest) { lookPos = lookWant; lookVel = lookVelWant; }
            else if (dt > 0) lookPos = Vector3.SmoothDamp(lookPos, lookWant + lookVelWant * lookSmooth, ref lookVel, lookSmooth, float.MaxValue, dt);
            float wantFov = Mathf.Clamp(need, fovMin, fovMax);
            if (rest || curFov <= 0) { curFov = wantFov; fovVel = 0; }
            else if (dt > 0) curFov = Mathf.Exp(Mathf.SmoothDamp(Mathf.Log(curFov), Mathf.Log(wantFov), ref fovVel, wantFov > curFov ? zoomSmooth / 3f : zoomSmooth, float.MaxValue, dt));

            Vector3 toLook = lookPos - camPos;
            if (toLook.sqrMagnitude < 1e-4f) toLook = tAxis;
            Quaternion rot = Shake(Quaternion.LookRotation(toLook.normalized, Vector3.up));
            cam.transform.SetPositionAndRotation(new GlobalPosition(camPos).ToLocalPosition(), rot);
            ApplyFov(cam, curFov);
            cam.cameraVelocity = rest || dt <= 0 ? Vector3.zero : targetVel;
            SetShadowDistance(Mathf.Max((targetPos - camPos).magnitude, tHaveB ? (tB - camPos).magnitude : 0f));
            if (!preview && shotTime >= nextLog)
            {
                nextLog = shotTime + 0.5f;
                log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "Cine two_shot t+{0:F2}: cam {1} target {2} second {3} gap {4:F0} m, dist {5:F0} m, fov {6:F1} (need {7:F1}{8})",
                    shotTime, Fmt(camPos), Fmt(targetPos), tHaveB ? Fmt(tB) : "none", tHaveB ? (tB - targetPos).magnitude : 0f,
                    (camPos - targetPos).magnitude, curFov, need, frame.Fits ? "" : ", backed off to the limit"));
            }
        }

        private static System.Numerics.Vector3 N(Vector3 v) => new System.Numerics.Vector3(v.x, v.y, v.z);
        private static Vector3 U(System.Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
