using System.Globalization;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Action camera ("action" shot type): a chase camera on a springy arm that reacts to how the aircraft flies.
    //
    // The arm holds the camera `distance` m behind and `height` m above the aircraft, in a frame that follows the
    // flight path forward and the airframe's up (`bank`), so it rides through rolls and loops. The camera itself is
    // a mass on that arm: in the aircraft's accelerating frame it feels the opposite of the aircraft's
    // acceleration, so it swings to the outside of a turn, drops back under afterburner and closes in when the
    // aircraft slows, then springs back with a little overshoot. The aim leads: it looks `look_ahead` m ahead
    // along the flight path plus the acceleration times `lead` (s²), so in a turn the view looks into the turn.
    // The horizon follows part of the airframe's roll (`roll`), the lens widens a little under thrust
    // (`fov_kick`) and the view shakes a little with G (`shake`; the noise is a function of shot time, so a clip
    // renders the same every time).
    //
    // Options (defaults): distance 24, height 5, side 0, spring 3.2 (rad/s), damping 0.6 (ratio), inertia 1,
    // max_swing 0.6 (of distance), look_ahead 40, lead 0.1, look_smooth 0.1, bank 0.8, roll 0.35 (horizon_lock
    // true forces 0), fov 55, fov_kick 0.35 (degrees per m/s² of forward acceleration, -5 to +8), shake 1 (0 = off),
    // acc_smooth 0.25 s, rot_smooth 0.15 s.
    internal sealed partial class CineCamera
    {
        private float aDistance, aHeight, aSide, aSpring, aDamping, aInertia, aMaxSwing, aLookAhead, aLead, aLookSmooth;
        private float aBank, aRoll, aFovKick, aShake, aAccSmooth, aKick, aKickVel, aSeed;
        private Vector3 aRel, aRelVel, aAcc, aPrevVel, aFwd, aArm;
        private bool aHavePrev;

        private bool IsAction => mode == "action";

        private void SetupAction()
        {
            aDistance = F("distance", 24f);
            aHeight = F("height", 5f);
            aSide = F("side", 0f);
            aSpring = Mathf.Max(0.5f, F("spring", 3.2f));
            aDamping = Mathf.Max(0.05f, F("damping", 0.6f));
            aInertia = F("inertia", 1f);
            aMaxSwing = Mathf.Max(0f, F("max_swing", 0.6f));
            aLookAhead = F("look_ahead", 40f);
            aLead = F("lead", 0.1f);
            aLookSmooth = Mathf.Max(0.01f, F("look_smooth", 0.1f));
            aBank = Mathf.Clamp01(F("bank", 0.8f));
            aRoll = B("horizon_lock", false) ? 0f : Mathf.Clamp01(F("roll", 0.35f));
            aFovKick = F("fov_kick", 0.35f);
            aShake = Mathf.Max(0f, F("shake", 1f));
            aAccSmooth = Mathf.Max(0.02f, F("acc_smooth", 0.25f));
            rotSmooth = Mathf.Max(0f, F("rot_smooth", 0.15f));
            haveRot = false;
            aHavePrev = false;
            aAcc = Vector3.zero; aKick = 0; aKickVel = 0;
            aSeed = unit != null ? (unit.persistentID.GetHashCode() & 1023) * 0.37f : 0f;
            aFwd = targetVel.sqrMagnitude > 25f ? targetVel.normalized : unit != null ? unit.transform.forward : Vector3.forward;
        }

        private void StepAction(CameraStateManager cam, float dt, bool preview)
        {
            ReadRotation(dt);
            bool live = unit != null && !unit.disabled && unit.transform != null;
            if (!preview && live && smoothRender && rotSmooth > 0) unit.transform.rotation = rotS;
            // A long hitch (or the first frame) starts the arm at rest.
            bool rest = snap || dt > 0.5f;

            // Acceleration from the snapshot velocity, low-passed (the network velocity moves in 20 Hz steps).
            if (!live) { targetVel = Vector3.zero; aAcc = Vector3.zero; aHavePrev = false; }
            else if (dt > 0 && aHavePrev && !rest)
            {
                Vector3 raw = (targetVel - aPrevVel) / dt;
                if (raw.sqrMagnitude > 200f * 200f) raw = raw.normalized * 200f;   // respawn or teleport
                aAcc += (raw - aAcc) * Mathf.Min(1f, dt / aAccSmooth);
            }
            if (live) { aPrevVel = targetVel; aHavePrev = true; }

            // Arm frame: forward along the flight path, up between world up and the airframe's up.
            if (live && targetVel.sqrMagnitude > 25f)
                aFwd = rest ? targetVel.normalized : Vector3.Slerp(aFwd, targetVel.normalized, Mathf.Min(1f, dt / 0.1f)).normalized;
            Vector3 airUp = rotS * Vector3.up;
            Vector3 levelUp = Vector3.ProjectOnPlane(Vector3.up, aFwd);
            Vector3 bodyUp = Vector3.ProjectOnPlane(airUp, aFwd);
            if (bodyUp.sqrMagnitude < 1e-4f) bodyUp = levelUp;
            if (levelUp.sqrMagnitude < 1e-4f) levelUp = bodyUp;   // flying straight up or down
            Vector3 armUp = Vector3.Slerp(levelUp.normalized, bodyUp.normalized, aBank).normalized;
            Vector3 armRight = Vector3.Cross(armUp, aFwd).normalized;
            Vector3 arm = -aFwd * aDistance + armUp * aHeight + armRight * aSide;

            // The camera as a damped mass on the arm, in the aircraft's frame (so it also feels -acceleration).
            if (rest || !live)
            {
                if (rest) { aRel = arm; aRelVel = Vector3.zero; }
                else aRelVel = Vector3.zero;   // target gone: hold where the camera is
            }
            else
            {
                Vector3 armVel = dt > 0 ? (arm - aArm) / dt : Vector3.zero;
                float w = aSpring, z = aDamping;
                int steps = Mathf.Clamp(Mathf.CeilToInt(dt * 240f), 1, 120);
                float h = dt / steps;
                for (int i = 0; i < steps; i++)
                {
                    Vector3 accel = w * w * (arm - aRel) - 2f * z * w * (aRelVel - armVel) - aInertia * aAcc;
                    aRelVel += accel * h;
                    aRel += aRelVel * h;
                }
                Vector3 swing = aRel - arm;
                float maxSwing = aMaxSwing * aDistance;
                if (swing.magnitude > maxSwing)
                {
                    Vector3 dir = swing.normalized;
                    aRel = arm + dir * maxSwing;
                    float outward = Vector3.Dot(aRelVel - armVel, dir);
                    if (outward > 0) aRelVel -= dir * outward;
                }
            }
            aArm = arm;
            if (live || rest) camPos = targetPos + aRel;

            // Never under the ground or the sea.
            float floor = GroundY(camPos) + minAgl;
            if (camPos.y < floor) { camPos.y = floor; aRel = camPos - targetPos; }

            // Aim ahead along the flight path and into the turn, critically damped with velocity feed-forward.
            Vector3 lookWant = targetPos + (live ? aFwd * aLookAhead + aAcc * aLead : Vector3.zero);
            if (rest) { lookPos = lookWant; lookVel = targetVel; }
            else if (dt > 0) lookPos = Vector3.SmoothDamp(lookPos, lookWant + targetVel * aLookSmooth, ref lookVel, aLookSmooth, float.MaxValue, dt);

            // Lens: wider under thrust, a little tighter when slowing.
            float kickWant = Mathf.Clamp(Vector3.Dot(aAcc, aFwd) * aFovKick, -5f, 8f);
            aKick = rest ? kickWant : dt > 0 ? Mathf.SmoothDamp(aKick, kickWant, ref aKickVel, 0.4f, float.MaxValue, dt) : aKick;
            curFov = Mathf.Clamp(fov + aKick, fovMin, fovMax);

            Vector3 toLook = lookPos - camPos;
            if (toLook.sqrMagnitude < 0.01f) toLook = aFwd;
            Vector3 viewUp = Vector3.Slerp(Vector3.up, live ? bodyUp.normalized : Vector3.up, aRoll);
            Quaternion rot = Quaternion.LookRotation(toLook.normalized, viewUp);
            if (aShake > 0)
            {
                float g = Mathf.Clamp01(aAcc.magnitude / (9.81f * 7f));
                float amp = aShake * (0.05f + 0.45f * g);
                float t = shotTime;
                float Noise(float lane) => (Mathf.PerlinNoise(t * 6.5f + aSeed, lane) - 0.5f) * 1.3f + (Mathf.PerlinNoise(t * 1.7f + aSeed, lane + 7.3f) - 0.5f) * 0.7f;
                rot *= Quaternion.Euler(Noise(0.1f) * amp, Noise(3.7f) * amp, Noise(9.2f) * amp * 0.6f);
            }

            rot = Shake(rot);
            cam.transform.SetPositionAndRotation(new GlobalPosition(camPos).ToLocalPosition(), rot);
            ApplyFov(cam, curFov);
            cam.cameraVelocity = rest || dt <= 0 ? Vector3.zero : targetVel;
            SetShadowDistance((targetPos - camPos).magnitude);
            if (!preview && shotTime >= nextLog)
            {
                nextLog = shotTime + 0.5f;
                log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "Cine action t+{0:F2}: cam {1} target {2} dist {3:F1} swing {4:F1} m, acc {5:F1} g (fwd {6:F1}), fov {7:F1}",
                    shotTime, Fmt(camPos), Fmt(targetPos), (targetPos - camPos).magnitude, (aRel - arm).magnitude,
                    aAcc.magnitude / 9.81f, Vector3.Dot(aAcc, aFwd) / 9.81f, curFov));
            }
        }
    }
}
