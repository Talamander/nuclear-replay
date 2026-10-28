using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Ride-along camera ("ride" shot type): the camera flies with a unit at a fixed offset in the unit's own frame,
    // like a camera ship holding formation a few metres off a wing. The viewer makes one from the free camera: fly it
    // to the spot next to the unit, press R, and the pose it has relative to the unit becomes the shot.
    //
    // The unit's pose is the filtered pose (position: ReadTarget's complementary filter; rotation: the quaternion
    // tracker of CineMount.cs, `rot_smooth`), and the unit is drawn at that pose, so network snapshot noise never
    // shows between the unit and the camera. The offset then follows the unit's turns through a critically damped
    // spring (`lag`), so in a hard roll the camera floats a little instead of being bolted on.
    //
    // Options (defaults):
    //   offset [x,y,z]    metres in the unit's frame (x right, y up, z forward); default off the right side and a little
    //                     behind and above, scaled to the unit's size (about [16, 3, -7] for a fighter)
    //   look_dir [x,y,z]  direction to look in, in the unit's frame; or
    //   look [x,y,z]      a point in the unit's frame to look at; or
    //   look_at           another unit to look at (same selectors as target: unit:<id>, name:, type:, host ...)
    //                     With none of them the camera looks at the unit itself.
    //   roll              "unit" (the horizon banks with the unit, the default), "level" (the horizon stays level),
    //                     or a number from 0 (level) to 1 (unit)
    //   lag 0.12 s (0 = rigid), rot_smooth 0.15 s, look_smooth 0.25 s (look_at only), min_agl 1 m, fov 50
    //   handheld, rumble, buffet, shake_speed: light shake on top (CinePath.cs), off by default
    internal sealed partial class CineCamera
    {
        // Selector resolver and the "who is this unit fighting" lookup, both from the director.
        internal System.Func<string, Unit> Resolve;
        internal System.Func<Unit, Unit> Opponent;

        private Vector3 rOffset, rLookDir, rLookPoint, rRel, rRelVel;
        private int rLook;            // 0 the unit itself, 1 look_dir, 2 look (a point), 3 look_at (another unit)
        private Unit rLookUnit;
        private float rRoll, rLag, rNextLookup;

        private bool IsRide => mode == "ride";

        private void SetupRide()
        {
            float size = Units.Size(unit);
            rOffset = raw["offset"] is JArray ? V3(raw["offset"]) : new Vector3(0.9f, 0.16f, -0.4f) * size;
            rLook = 0;
            rLookUnit = null;
            rNextLookup = 0;
            if (raw["look_dir"] is JArray) { rLook = 1; rLookDir = V3(raw["look_dir"]); if (rLookDir.sqrMagnitude < 1e-6f) rLook = 0; else rLookDir.Normalize(); }
            else if (raw["look"] is JArray) { rLook = 2; rLookPoint = V3(raw["look"]); }
            else if (S("look_at") != null) { rLook = 3; rLookUnit = Resolve?.Invoke(S("look_at")); }
            var roll = raw["roll"];
            rRoll = roll != null && (roll.Type == JTokenType.Float || roll.Type == JTokenType.Integer) ? Mathf.Clamp01((float)roll) : CameraFraming.RollAmount(S("roll"), 1f);
            rLag = Mathf.Max(0f, F("lag", 0.12f));
            rotSmooth = Mathf.Max(0f, F("rot_smooth", 0.15f));
            minAgl = F("min_agl", 1f);
            haveRot = false;
            rRelVel = Vector3.zero;
            log.LogInfo(string.Format(CultureInfo.InvariantCulture, "Cine ride: offset {0}, look {1}, roll {2:F2}, lag {3:F2}",
                Fmt(rOffset), rLook == 1 ? "dir " + Fmt(rLookDir) : rLook == 2 ? "point " + Fmt(rLookPoint) : rLook == 3 ? (rLookUnit != null ? rLookUnit.unitName : "(no unit)") : "the unit", rRoll, rLag));
        }

        private void StepRide(CameraStateManager cam, float dt, bool preview)
        {
            ReadRotation(dt);
            bool live = unit != null && !unit.disabled && unit.transform != null;
            if (!preview && live && smoothRender && rotSmooth > 0) unit.transform.rotation = rotS;
            if (!live) targetVel = Vector3.zero;
            bool rest = snap || dt > 0.5f;

            Quaternion q = rotS;
            Vector3 want = q * rOffset;
            if (rest || rLag <= 0) { rRel = want; rRelVel = Vector3.zero; }
            else rRel = Vector3.SmoothDamp(rRel, want, ref rRelVel, rLag, float.MaxValue, Mathf.Max(dt, 1e-4f));
            camPos = targetPos + rRel;
            float floor = GroundY(camPos) + minAgl;
            if (camPos.y < floor) camPos.y = floor;

            // The other unit of look_at: looked up again (once a second) when it is gone.
            if (rLook == 3 && (rLookUnit == null || rLookUnit.disabled) && shotTime >= rNextLookup)
            {
                rNextLookup = shotTime + 1f;
                rLookUnit = Resolve?.Invoke(S("look_at"));
            }
            Vector3 fwd;
            switch (rLook)
            {
                case 1: fwd = q * rLookDir; break;
                case 2: fwd = targetPos + q * rLookPoint - camPos; break;
                case 3:
                {
                    Vector3 at = rLookUnit != null && rLookUnit.transform != null ? rLookUnit.transform.position.ToGlobalPosition().AsVector3() : targetPos;
                    Vector3 vel = rLookUnit != null && rLookUnit.rb != null ? rLookUnit.rb.velocity : Vector3.zero;
                    if (rest) { lookPos = at; lookVel = vel; }
                    else if (dt > 0) lookPos = Vector3.SmoothDamp(lookPos, at + vel * lookSmooth, ref lookVel, lookSmooth, float.MaxValue, dt);
                    fwd = lookPos - camPos;
                    break;
                }
                default: fwd = targetPos - camPos; break;
            }
            if (fwd.sqrMagnitude < 1e-4f) fwd = q * Vector3.forward;
            Vector3 viewUp = Vector3.Slerp(Vector3.up, q * Vector3.up, rRoll);
            Quaternion rot = Shake(Quaternion.LookRotation(fwd.normalized, viewUp));
            if (rLook != 3) lookPos = camPos + fwd;

            curFov = fov > 0 ? fov : 50f;
            cam.transform.SetPositionAndRotation(new GlobalPosition(camPos).ToLocalPosition(), rot);
            ApplyFov(cam, curFov);
            cam.cameraVelocity = rest || dt <= 0 ? Vector3.zero : targetVel;
            SetShadowDistance((targetPos - camPos).magnitude);
            if (!preview && shotTime >= nextLog)
            {
                nextLog = shotTime + 0.5f;
                log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "Cine ride t+{0:F2}: cam {1} target {2} offset now {3} (want {4}), rot filter offset {5:F2} deg, pos filter offset {6:F2} m",
                    shotTime, Fmt(camPos), Fmt(targetPos), Fmt(Quaternion.Inverse(q) * rRel), Fmt(rOffset),
                    Quaternion.Angle(rawRot, rotS), (rawTargetPos - targetPos).magnitude));
            }
        }
    }
}
