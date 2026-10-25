using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Camera path ("path" shot type): the camera flies through keys laid down in the viewer's Camera Studio (or
    // written by hand), with the maths in CameraPath.cs: a centripetal Catmull-Rom spline for position, squad for
    // rotation, a monotone curve for the lens, and timing by progress along the path.
    //
    // Keys: [{ "t": seconds after the shot's t, "pos": [x, y, z], "yaw": deg, "pitch": deg, "roll": deg, "fov": deg }]
    // Options (defaults):
    //   space        world | target (world). world: pos is a global position and yaw a compass heading. target:
    //                pos is metres right, up and forward of the target, in a frame that turns with the target's
    //                level heading (smoothed by `lag`, 0.3 s), and yaw is relative to that heading.
    //   timing       even | keys (even). even: constant speed from the first key to the last, eased by `ease`.
    //                keys: reaches every key at its own time.
    //   ease         0.5 (0 to 1)
    //   aim          keys | target | path (keys). keys: the keyed rotations. target: looks at the target. path: looks
    //                along the direction of travel. look_smooth (0.25 s) smooths target and path aim.
    //   fov          50, used for keys without a fov
    //   min_agl      1 m
    // The path's clock is the replay's, so a seek into the middle of the shot lands on the same pose. Before the
    // first key the camera waits on it; after the last it holds there.
    internal sealed partial class CineCamera
    {
        private CameraPath pPath;
        private bool pTargetSpace;
        private string pAim;
        private double pOffset;
        private Vector3 pDir, pPrevPos;

        private bool IsPath => mode == "path";

        private void SetupPath(double replayTime, double shotT)
        {
            pOffset = replayTime - shotT;
            pTargetSpace = string.Equals(S("space") ?? "world", "target", System.StringComparison.OrdinalIgnoreCase) && unit != null;
            pAim = (S("aim") ?? "keys").ToLowerInvariant();
            bool keyTiming = string.Equals(S("timing") ?? "even", "keys", System.StringComparison.OrdinalIgnoreCase);
            var list = new List<CameraPath.Key>();
            foreach (var item in raw["keys"] as JArray ?? new JArray())
            {
                Vector3 p = V3(item["pos"]);
                list.Add(new CameraPath.Key
                {
                    T = (double?)item["t"] ?? 0,
                    Pos = new System.Numerics.Vector3(p.x, p.y, p.z),
                    Rot = CameraPath.Euler((float?)item["pitch"] ?? 0f, (float?)item["yaw"] ?? 0f, (float?)item["roll"] ?? 0f),
                    Fov = (float?)item["fov"] ?? fov,
                });
            }
            if (list.Count == 0)
            {
                // No keys: a still camera behind the target (or wherever the camera is).
                pTargetSpace = unit != null;
                Vector3 p = pTargetSpace ? new Vector3(0, 5, -40) : camPos;
                list.Add(new CameraPath.Key { T = 0, Pos = new System.Numerics.Vector3(p.x, p.y, p.z), Rot = System.Numerics.Quaternion.Identity, Fov = fov });
                if (pAim == "keys") pAim = unit != null ? "target" : "keys";
            }
            pPath = new CameraPath(list, keyTiming, Mathf.Clamp01(F("ease", 0.5f)));
            pDir = Vector3.zero;
            log.LogInfo(string.Format(CultureInfo.InvariantCulture, "Cine path: {0} keys, {1} space, {2} timing, aim {3}, {4:F2} to {5:F2} s of the shot, {6:F0} m",
                list.Count, pTargetSpace ? "target" : "world", keyTiming ? "keys" : "even", pAim, pPath.Start, pPath.End, pPath.Length));
        }

        private string S(string name) => raw[name] != null && raw[name].Type == JTokenType.String ? (string)raw[name] : null;

        private void StepPath(CameraStateManager cam, float dt, bool preview)
        {
            double t = pOffset + shotTime;
            pPath.Evaluate(t, out var np, out var nq, out var nfov);
            var nd = pPath.Direction(t);
            Vector3 pos = new Vector3(np.X, np.Y, np.Z);
            Quaternion rot = new Quaternion(nq.X, nq.Y, nq.Z, nq.W);
            Vector3 travel = new Vector3(nd.X, nd.Y, nd.Z);
            bool live = unit != null && !unit.disabled;
            if (!live) targetVel = Vector3.zero;

            if (pTargetSpace)
            {
                if (targetVel.sqrMagnitude > 4f)
                {
                    Vector3 want = Flat(targetVel, 1f);
                    heading = snap ? want : Vector3.SmoothDamp(heading, want, ref headingVel, F("lag", 0.3f), float.MaxValue, Mathf.Max(dt, 1e-4f));
                }
                Quaternion frame = Quaternion.LookRotation(Flat(heading, 1f), Vector3.up);
                pos = targetPos + frame * pos;
                rot = frame * rot;
                travel = frame * travel;
            }

            float floor = GroundY(pos) + minAgl;
            if (pos.y < floor) pos.y = floor;
            Vector3 camVelocity = snap || dt <= 0 ? Vector3.zero : (pos - pPrevPos) / dt;
            pPrevPos = pos;
            camPos = pos;

            // Aim: the keyed rotation, the target, or the direction of travel (both smoothed by look_smooth).
            if (pAim == "target" && unit != null)
            {
                Vector3 aimWant = targetPos + targetVel * lookSmooth;
                if (snap) { lookPos = targetPos; lookVel = targetVel; }
                else if (dt > 0) lookPos = Vector3.SmoothDamp(lookPos, aimWant, ref lookVel, lookSmooth, float.MaxValue, dt);
                Vector3 toLook = lookPos - camPos;
                if (toLook.sqrMagnitude > 0.01f) rot = Quaternion.LookRotation(toLook.normalized, Vector3.up);
            }
            else if (pAim == "path")
            {
                Vector3 want = travel.sqrMagnitude > 0.5f ? travel : (pDir.sqrMagnitude > 0.5f ? pDir : rot * Vector3.forward);
                pDir = snap || pDir.sqrMagnitude < 0.5f ? want : Vector3.Slerp(pDir, want, dt > 0 ? Mathf.Min(1f, dt / Mathf.Max(0.01f, lookSmooth)) : 0f).normalized;
                rot = Quaternion.LookRotation(pDir, Vector3.up);
                lookPos = camPos + pDir * 50f;
            }
            else lookPos = camPos + rot * Vector3.forward * 50f;

            curFov = Mathf.Clamp(nfov, 5f, 120f);
            rot = Shake(rot);
            cam.transform.SetPositionAndRotation(new GlobalPosition(camPos).ToLocalPosition(), rot);
            ApplyFov(cam, curFov);
            cam.cameraVelocity = camVelocity;
            SetShadowDistance(live ? (targetPos - camPos).magnitude : 500f);
            if (!preview && shotTime >= nextLog)
            {
                nextLog = shotTime + 0.5f;
                log.LogInfo(string.Format(CultureInfo.InvariantCulture, "Cine path t {0:F2} (shot +{1:F2}): cam {2} ground {3:F1} fov {4:F1} aim {5}",
                    t, shotTime, Fmt(camPos), floor - minAgl, curFov, pAim));
            }
        }

        // ---------- shake (any cine camera) ----------
        // Shot options handheld, rumble, buffet (0 = off, 1 = normal) and shake_speed (1); see CameraShake.cs. The
        // noise runs on the replay's clock from the shot's t, so a clip renders the same every time.
        private float shHandheld, shRumble, shBuffet, shSpeed;
        private int shSeed;
        private double shOffset;

        private void SetupShake(Director.Shot shot, double replayTime)
        {
            shHandheld = Mathf.Max(0f, F("handheld", 0f));
            shRumble = Mathf.Max(0f, F("rumble", 0f));
            shBuffet = Mathf.Max(0f, F("buffet", 0f));
            shSpeed = Mathf.Max(0.05f, F("shake_speed", 1f));
            shSeed = unit != null ? unit.persistentID.GetHashCode() : (int)(shot.T * 1000);
            shOffset = replayTime - shot.T;
        }

        private void NoShake() { shHandheld = shRumble = shBuffet = 0f; }

        private Quaternion Shake(Quaternion rot)
        {
            if (shHandheld <= 0f && shRumble <= 0f && shBuffet <= 0f) return rot;
            CameraShake.Angles(shOffset + shotTime, shSeed, shHandheld, shRumble, shBuffet, shSpeed, out float p, out float y, out float r);
            return rot * Quaternion.Euler(p, y, r);
        }
    }
}
