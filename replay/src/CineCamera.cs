using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Logging;
using NuclearOption.NetworkTransforms;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace NuclearReplay.Playback
{
    // Director-owned cinematic camera. It is a camera state of the game's own CameraStateManager,
    // so audio, LOD, floating origin and rendering stay the game's, but every pose comes from here.
    //
    // Modes (shot "camera"):
    //   flyby       camera parked beside/below/above the target's path a few seconds ahead, pans to follow
    //   chase_cine  smooth chase at a configurable offset, lag and slow orbit drift, low angles allowed
    //   track_from  fixed vantage (world point or a ground unit) with a long lens
    //   keyframe    camera path through 2+ keys relative to the target or the world
    // All positions are kept in GlobalPosition space (floating-origin safe). Every camera position is
    // clamped above terrain and sea. Pose smoothing is critically damped (Unity SmoothDamp form).
    // horizon_lock (default true) keeps roll at zero.
    //   mount       camera rigidly attached in the target's local frame (close-ups; see CineMount.cs)
    //   action      chase on a springy arm that swings out in turns and leads the aim (see CineAction.cs)
    //   path        camera path from the viewer's Camera Studio, in world or target space (see CinePath.cs)
    // Every mode takes the shake options handheld, rumble, buffet and shake_speed (see CinePath.cs).
    internal sealed partial class CineCamera
    {
        internal static bool IsCine(string camera)
        {
            switch ((camera ?? "").ToLowerInvariant())
            {
                case "flyby": case "chase_cine": case "track_from": case "keyframe": case "mount": case "action": case "path": return true;
                default: return false;
            }
        }

        private readonly ManualLogSource log;
        internal readonly CineState State;
        internal bool Active;

        // Shot settings
        private string mode;
        private JObject raw;
        private float fov, lookSmooth, minAgl, lead, posSmooth;
        private Vector2? frame;
        private bool horizonLock;
        private float sizeFrac, fovMin, fovMax, zoomSmooth;

        // Rig state (global space)
        private Unit unit;
        private Vector3 targetPos, targetVel, lastTargetRaw;
        private bool haveLastTarget;
        private Vector3 camPos, camVel, lookPos, lookVel;
        private Vector3 camRel;            // smoothed camera offset from the target (moving rigs)
        private Vector3 anchor;            // flyby / track_from fixed camera position
        private Unit fromUnit;
        private Vector3 fromOffset;
        private float shotTime, nextLog;
        private float leadX, leadXVel, curFov, fovVel, floorY, floorVel;
        private Vector3 heading, headingVel;  // smoothed travel direction
        private float driftDeg;
        private Vector3 chaseOffset, chaseOffsetVel;
        private List<Key> keys;
        private bool snap;
        private float savedFov = -1;
        private float savedShadow = -1;

        // Per-frame trace of exported frames (diagnostics for jitter checks). ReplayPlugin sets these each frame.
        internal static int CaptureFrame = -1;
        internal static double ReplayTime;
        internal static string TraceDir;
        private StreamWriter trace;
        private double shotStart;
        private SendTransformBatcher batcher;
        private static readonly HashSet<string> traceFiles = new HashSet<string>();

        private sealed class Key
        {
            public float T;
            public Vector3 Pos;
            public bool World;
            public Vector3? Look;      // null = the target
            public bool LookWorld;
            public float? Fov;
        }

        public CineCamera(ManualLogSource log)
        {
            this.log = log;
            State = new CineState(this);
        }

        // ---------- shot setup ----------

        public void Begin(Director.Shot shot, Unit target, double replayTime)
        {
            raw = shot.Raw ?? new JObject();
            mode = shot.Camera.ToLowerInvariant();
            CloseTrace();
            EndMount(CameraStateManager.i);
            shotStart = replayTime;
            unit = target;
            Active = true;
            snap = true;
            fovLogged = false;
            shotTime = 0; nextLog = 0;
            haveLastTarget = false;
            float defaultFov = mode == "mount" ? -1f : mode == "track_from" ? 15f : mode == "flyby" ? 30f : mode == "chase_cine" ? 45f : mode == "action" ? 55f : mode == "path" ? 50f : 40f;
            fov = shot.Fov ?? defaultFov;
            lookSmooth = F("look_smooth", mode == "flyby" ? 0.12f : mode == "track_from" ? 0.3f : 0.25f);
            posSmooth = F("pos_smooth", 0.2f);
            minAgl = F("min_agl", mode == "path" ? 1f : 3f);
            horizonLock = B("horizon_lock", true);
            lead = F("lead", 0.33f);
            frame = null;
            if (raw["frame"] is JArray fr && fr.Count == 2) frame = new Vector2((float)fr[0], (float)fr[1]);
            sizeFrac = F("size", 0f);
            // A lens set in mm (focal_mm) may go longer or wider than the auto-zoom's usual range.
            bool mm = raw["focal_mm"] != null;
            fovMin = F("fov_min", mm ? 1f : 4f);
            fovMax = F("fov_max", mm ? 120f : 70f);
            zoomSmooth = F("zoom_smooth", 0.8f);
            driftDeg = F("azimuth", 0f);
            fromUnit = null;
            keys = null;
            targetSmooth = Mathf.Max(0f, F("target_smooth", 0.3f));
            smoothRender = B("smooth_render", true);

            if (unit == null) targetVel = Vector3.zero;
            ReadTarget(0f);
            heading = Flat(targetVel.sqrMagnitude > 1f ? targetVel : unit != null ? unit.transform.forward : Vector3.forward, 1f);
            headingVel = Vector3.zero;
            BacklightBegin();

            switch (mode)
            {
                case "flyby": SetupFlyby(replayTime); break;
                case "track_from": SetupTrackFrom(); break;
                case "keyframe": SetupKeys(); break;
                case "chase_cine": chaseOffset = ChaseOffsetWorld(); chaseOffsetVel = Vector3.zero; break;
                case "mount": SetupMount(); break;
                case "action": SetupAction(); break;
                case "path": SetupPath(replayTime, shot.T); break;
            }
            SetupShake(shot, replayTime);
            log.LogInfo($"Cine {mode} -> {(unit != null ? unit.unitName : "no target")} at {replayTime:F2}s: target global {Fmt(targetPos)} vel {Fmt(targetVel)}"
                + (mode == "flyby" || mode == "track_from" ? $", camera {Fmt(anchor)}" : ""));
        }

        public void End() { EndMount(CameraStateManager.i); Active = false; unit = null; CloseTrace(); }

        // A still camera on a point. The director uses it when a missile it follows with one of the game's own
        // cameras detonates: the game's chase camera would swing to the sky, so the view stays where it was and
        // keeps looking at the missile's last position (the explosion).
        public void BeginHold(Vector3 cameraGlobal, Vector3 lookGlobal, float fieldOfView, double replayTime)
        {
            raw = new JObject();
            mode = "hold";
            NoShake();
            CloseTrace();
            EndMount(CameraStateManager.i);
            shotStart = replayTime;
            unit = null;
            Active = true;
            snap = true;
            fovLogged = false;
            shotTime = 0; nextLog = 0;
            haveLastTarget = false;
            fov = fieldOfView;
            lookSmooth = 0.25f; posSmooth = 0.2f; minAgl = 3f; horizonLock = true; lead = 0f;
            frame = null; sizeFrac = 0f; fovMin = 4f; fovMax = 70f; zoomSmooth = 0.8f; driftDeg = 0f;
            fromUnit = null; keys = null; targetSmooth = 0f; smoothRender = false; backlight = false;
            targetPos = lookGlobal; rawTargetPos = lookGlobal; targetVel = Vector3.zero;
            // The game's chase camera sits a few metres behind a missile, which would put the hold inside the
            // smoke; back off along the same line so the explosion and the falling target read.
            Vector3 back = cameraGlobal - lookGlobal;
            float minDistance = 45f;
            if (back.sqrMagnitude < 1f) back = Vector3.back;
            camPos = back.magnitude < minDistance ? lookGlobal + back.normalized * minDistance : cameraGlobal;
            heading = Flat(lookGlobal - camPos, 1f);
            headingVel = Vector3.zero;
            log.LogInfo($"Cine hold at {replayTime:F2}s: camera {Fmt(camPos)} looking at {Fmt(lookGlobal)}");
        }

        private float F(string name, float fallback) => raw[name] != null && raw[name].Type != JTokenType.Null ? (float)raw[name] : fallback;
        private bool B(string name, bool fallback) => raw[name] != null && raw[name].Type == JTokenType.Boolean ? (bool)raw[name] : fallback;
        private static string Fmt(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:F1}, {1:F1}, {2:F1})", v.x, v.y, v.z);
        private static Vector3 V3(JToken t) => t is JArray a && a.Count == 3 ? new Vector3((float)a[0], (float)a[1], (float)a[2]) : Vector3.zero;

        private void SetupFlyby(double replayTime)
        {
            float ahead = F("ahead", 2.5f);
            Vector3 pass, dir;
            if (raw["pass"] is JArray)
            {
                pass = V3(raw["pass"]);
                dir = targetVel;
            }
            else if (raw["track"] != null && TrackLookup((string)raw["track"], F("track_t0", 0f), replayTime + ahead, out pass, out dir)) { }
            else
            {
                // Constant-turn prediction from the current velocity and its smoothed change.
                pass = targetPos + targetVel * ahead;
                dir = targetVel;
            }
            Vector3 flatDir = Flat(dir.sqrMagnitude > 1f ? dir : unit.transform.forward, 1f);
            Vector3 right = Vector3.Cross(Vector3.up, flatDir).normalized;
            if (BacklightFlyby(pass)) return;
            float offset = F("offset", 80f);
            float height = F("height", 0f);
            string side = ((string)raw["side"] ?? "left").ToLowerInvariant();
            Vector3 a = pass;
            switch (side)
            {
                case "right": a += right * offset; break;
                case "below": a += Vector3.down * offset; break;
                case "above": a += Vector3.up * offset; break;
                default: a -= right * offset; break;
            }
            a += Vector3.up * height;
            a += flatDir * F("along", 0f);
            if (raw["agl"] != null) a.y = GroundY(a) + F("agl", 2f);
            a.y = Mathf.Max(a.y, GroundY(a) + minAgl);
            anchor = a;

            // Line-of-sight check: sample the target's path around the pass and, if terrain hides it from the
            // chosen spot, search nearby spots (other side, wider offsets, higher) for the most visible one.
            if (!B("visibility", true)) return;
            var samples = new List<Vector3>();
            float span = F("visible_span", 2.5f);
            for (float dtS = -span; dtS <= span + 1e-3f; dtS += span / 4f)
            {
                Vector3 sp, sd;
                if (raw["track"] != null && raw["pass"] == null && TrackLookup((string)raw["track"], F("track_t0", 0f), replayTime + ahead + dtS, out sp, out sd)) samples.Add(sp);
                else samples.Add(pass + (dir.sqrMagnitude > 1f ? dir : flatDir * 200f) * dtS);
            }
            int bestScore = Visible(anchor, samples);
            if (bestScore == samples.Count) { log.LogInfo($"Cine flyby: spot sees {bestScore}/{samples.Count} path samples"); return; }
            Vector3 best = anchor;
            string[] sides = side == "right" ? new[] { "right", "left" } : side == "left" ? new[] { "left", "right" } : new[] { side, "left", "right" };
            foreach (var sName in sides)
                foreach (float mul in new[] { 1f, 1.5f, 2.2f, 3f })
                    foreach (float up in new[] { 0f, 15f, 40f, 80f })
                    {
                        Vector3 c = pass + Vector3.up * height + flatDir * F("along", 0f);
                        float off = offset * mul;
                        c += sName == "right" ? right * off : sName == "below" ? Vector3.down * off : sName == "above" ? Vector3.up * off : -right * off;
                        if (raw["agl"] != null) c.y = GroundY(c) + F("agl", 2f);
                        c.y = Mathf.Max(c.y, GroundY(c) + minAgl) + up;
                        int sc = Visible(c, samples);
                        if (sc > bestScore) { bestScore = sc; best = c; }
                        if (sc == samples.Count) goto done;
                    }
            done:
            log.LogInfo($"Cine flyby: moved spot {Fmt(anchor)} -> {Fmt(best)}, sees {bestScore}/{samples.Count} path samples");
            anchor = best;
        }

        private static int Visible(Vector3 from, List<Vector3> samples)
        {
            Vector3 a = new GlobalPosition(from).ToLocalPosition();
            int n = 0;
            foreach (var s in samples)
            {
                Vector3 b = new GlobalPosition(s).ToLocalPosition();
                Vector3 d = b - a;
                // stop a few metres short so the aircraft's own colliders never count
                if (!Physics.Linecast(a, b - d.normalized * Mathf.Min(30f, d.magnitude * 0.5f), PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)) n++;
            }
            return n;
        }

        private void SetupTrackFrom()
        {
            fromOffset = raw["from_offset"] != null ? V3(raw["from_offset"]) : new Vector3(0, 4, 0);
            if (BacklightTrackFrom()) return;
            var from = raw["from"];
            if (from is JArray)
            {
                anchor = V3(from);
                if (raw["agl"] != null) anchor.y = GroundY(anchor) + F("agl", 2f);
                anchor.y = Mathf.Max(anchor.y, GroundY(anchor) + minAgl);
                return;
            }
            string spec = (string)from ?? "";
            fromUnit = FindUnit(spec, targetPos);
            if (fromUnit != null)
            {
                anchor = FromAnchor();
                log.LogInfo($"Cine track_from {spec} -> {fromUnit.unitName}");
            }
            else
            {
                // Fallback: a point beside and behind the target's track at the given distance.
                float d = F("distance", 1500f);
                anchor = targetPos - heading * d * 0.7f + Vector3.Cross(Vector3.up, heading).normalized * d * 0.7f;
                log.LogWarning($"Cine track_from: no unit for '{spec}', using a point {d} m away");
            }
            anchor.y = Mathf.Max(anchor.y, GroundY(anchor) + minAgl);
        }

        // Vantage on a ground unit: its position plus from_offset, shifted `from_toward` metres
        // horizontally toward the target so the unit's own superstructure and smoke stay out of frame.
        private Vector3 FromAnchor()
        {
            Vector3 basePos = fromUnit.transform.position.ToGlobalPosition().AsVector3();
            Vector3 a = basePos + fromOffset;
            float toward = F("from_toward", 0f);
            if (toward != 0f)
            {
                Vector3 d = targetPos - basePos; d.y = 0;
                if (d.sqrMagnitude > 1f) a += d.normalized * toward;
            }
            return a;
        }

        private void SetupKeys()
        {
            keys = new List<Key>();
            foreach (var item in raw["keys"] as JArray ?? new JArray())
            {
                var k = new Key { T = (float?)item["t"] ?? 0, Pos = V3(item["pos"]), World = ((string)item["space"] ?? "target") == "world", Fov = LensShots.FocalFov(item, (string)raw["sensor"]) ?? (float?)item["fov"] };
                if (item["look"] is JArray) { k.Look = V3(item["look"]); k.LookWorld = ((string)item["look_space"] ?? (k.World ? "world" : "target")) == "world"; }
                keys.Add(k);
            }
            keys.Sort((x, y) => x.T.CompareTo(y.T));
            if (keys.Count == 0) keys.Add(new Key { T = 0, Pos = new Vector3(0, 3, -40) });
            if (keys.Count == 1) keys.Add(new Key { T = keys[0].T + 1, Pos = keys[0].Pos, World = keys[0].World, Look = keys[0].Look, LookWorld = keys[0].LookWorld, Fov = keys[0].Fov });
        }

        private static Unit FindUnit(string spec, Vector3 near)
        {
            if (spec.StartsWith("unit:"))
            {
                var id = spec.Substring(5);
                foreach (var u in UnitRegistry.allUnits) if (u != null && u.persistentID.ToString() == id) return u;
                return null;
            }
            bool nearest = spec.StartsWith("nearest:");
            string name = spec.StartsWith("name:") ? spec.Substring(5) : nearest ? spec.Substring(8) : spec;
            Unit best = null; float bestD = float.MaxValue;
            foreach (var u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled) continue;
                string n = (u.unitName ?? "") + " " + (u.definition != null ? u.definition.unitName + " " + u.definition.code : "");
                if (n.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!nearest) return u;
                float d = (u.transform.position.ToGlobalPosition().AsVector3() - near).sqrMagnitude;
                if (d < bestD) { bestD = d; best = u; }
            }
            return best;
        }

        // ---------- track CSV (future trajectory) ----------
        // Columns: a time column (t or rt), x, y or alt, z in global metres. replay time = csv time + track_t0.
        private static readonly Dictionary<string, List<float[]>> trackCache = new Dictionary<string, List<float[]>>();

        private bool TrackLookup(string path, float t0, double replayTime, out Vector3 pos, out Vector3 dir)
        {
            pos = dir = Vector3.zero;
            try
            {
                if (path.StartsWith("/") && Path.DirectorySeparatorChar == '\\') path = "Z:" + path.Replace('/', '\\');
                if (!trackCache.TryGetValue(path, out var rows))
                {
                    rows = new List<float[]>();
                    var lines = File.ReadAllLines(path);
                    var head = lines[0].Split(',');
                    int it = Array.FindIndex(head, h => h == "t" || h == "rt"), ix = Array.IndexOf(head, "x"),
                        iy = Array.FindIndex(head, h => h == "y" || h == "alt"), iz = Array.IndexOf(head, "z");
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var c = lines[i].Split(',');
                        if (c.Length <= Math.Max(Math.Max(it, ix), Math.Max(iy, iz))) continue;
                        rows.Add(new[] { P(c[it]), P(c[ix]), P(c[iy]), P(c[iz]) });
                    }
                    trackCache[path] = rows;
                }
                float want = (float)replayTime - t0;
                for (int i = 1; i < rows.Count; i++)
                {
                    if (rows[i][0] < want) continue;
                    var a = rows[i - 1]; var b = rows[i];
                    if (b[0] - a[0] > 2f) return false;
                    float u = Mathf.InverseLerp(a[0], b[0], want);
                    var pa = new Vector3(a[1], a[2], a[3]); var pb = new Vector3(b[1], b[2], b[3]);
                    pos = Vector3.Lerp(pa, pb, u);
                    dir = (pb - pa) / Mathf.Max(0.01f, b[0] - a[0]);
                    return true;
                }
            }
            catch (Exception e) { log.LogWarning("Cine track lookup failed: " + e.Message); }
            return false;
        }

        private static float P(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        // ---------- per frame ----------

        // Target state. A replayed aircraft is placed every frame from 20 Hz network snapshots (lerped, then
        // extrapolated), and its position wobbles about 1 m (up to ~8 m) along its path from frame to frame,
        // while the snapshot velocity is smooth. So the target position is a complementary filter: integrate the
        // velocity, and pull toward the raw position with time constant `target_smooth` (default 0.3 s; 0 = raw).
        // No lag on straight or steady turning flight; the residual is under the noise. With `smooth_render`
        // (default true) the target is also drawn at the filtered position, so the jet and the camera agree.
        private Vector3 rawTargetPos, smoothVelPrev;
        private float targetSmooth;
        private bool smoothRender;
        private void ReadTarget(float dt)
        {
            if (unit == null || unit.disabled || unit.transform == null) return;  // keep the last pose
            Vector3 p = unit.transform.position.ToGlobalPosition().AsVector3();
            Vector3 v = unit.rb != null ? unit.rb.velocity : Vector3.zero;
            if (v.sqrMagnitude < 1f && haveLastTarget && dt > 0) v = (p - lastTargetRaw) / dt;
            rawTargetPos = p;
            if (!haveLastTarget || dt <= 0 || targetSmooth <= 0 || (p - targetPos).sqrMagnitude > 30f * 30f)
                targetPos = p;
            else
            {
                targetPos += 0.5f * (v + smoothVelPrev) * dt;
                targetPos += (p - targetPos) * Mathf.Min(1f, dt / targetSmooth);
            }
            lastTargetRaw = p; haveLastTarget = true; smoothVelPrev = v;
            targetVel = v;
        }

        // Draws the target where the camera thinks it is. The network transform sets it again next frame.
        private void PlaceTarget()
        {
            if (!smoothRender || targetSmooth <= 0 || unit == null || unit.disabled || unit.transform == null) return;
            unit.transform.position = new GlobalPosition(targetPos).ToLocalPosition();
        }

        internal void Step(CameraStateManager cam, bool preview = false)
        {
            float dt = preview ? 0f : Time.deltaTime;
            shotTime += dt;
            ReadTarget(dt);
            if (!preview) PlaceTarget();
            if (IsMount || IsAction || IsPath)
            {
                if (IsMount) StepMount(cam, dt, preview);
                else if (IsAction) StepAction(cam, dt, preview);
                else StepPath(cam, dt, preview);
                if (!preview) WriteTrace(cam, dt);
                if (!preview) LightingOverride.CameraPosed(cam.transform);
                if (!preview) snap = false;
                return;
            }
            bool targetLive = unit != null && !unit.disabled;
            if (!targetLive) targetVel = Vector3.zero;

            // Smoothed travel direction (frame for chase / keyframe offsets).
            float followPitch = F("follow_pitch", mode == "chase_cine" ? 0.5f : 0f);
            if (targetVel.sqrMagnitude > 4f)
            {
                Vector3 want = Flat(targetVel, 1f - followPitch);
                heading = snap ? want : Vector3.SmoothDamp(heading, want, ref headingVel, F("lag", 0.6f), float.MaxValue, Mathf.Max(dt, 1e-4f));
            }
            Vector3 fwd = heading.sqrMagnitude > 1e-4f ? heading.normalized : Vector3.forward;
            Vector3 flatFwd = Flat(fwd, 1f);
            Vector3 right = Vector3.Cross(Vector3.up, flatFwd).normalized;
            Vector3 up = Vector3.Cross(fwd, right).normalized;

            Vector3 desiredCam = camPos;
            Vector3 lookTarget = targetPos;
            float wantFov = fov;
            switch (mode)
            {
                case "flyby":
                    desiredCam = anchor;
                    break;
                case "track_from":
                    if (fromUnit != null && !fromUnit.disabled) { anchor = FromAnchor(); anchor.y = Mathf.Max(anchor.y, GroundY(anchor) + minAgl); }
                    desiredCam = anchor;
                    break;
                case "chase_cine":
                {
                    driftDeg += F("drift", 0f) * dt;
                    Vector3 want = ChaseOffsetWorld(fwd, right, up);
                    chaseOffset = snap ? want : Vector3.SmoothDamp(chaseOffset, want, ref chaseOffsetVel, F("lag", 0.6f), float.MaxValue, Mathf.Max(dt, 1e-4f));
                    desiredCam = targetPos + chaseOffset;
                    if (raw["aim"] is JArray) lookTarget = targetPos + Rel(V3(raw["aim"]), fwd, right, up);
                    break;
                }
                case "keyframe":
                    if (backlight) BacklightFrame(out fwd, out right, out up);
                    EvalKeys(fwd, right, up, out desiredCam, out lookTarget, out wantFov);
                    break;
            }

            // Terrain / sea clamp with a softened floor that can only push up quickly, never under the hard minimum.
            float ground = GroundY(desiredCam);
            float floor = ground + minAgl;
            floorY = snap ? floor : Mathf.SmoothDamp(floorY, floor, ref floorVel, 0.15f, float.MaxValue, Mathf.Max(dt, 1e-4f));
            if (desiredCam.y < Mathf.Max(floorY, floor - minAgl * 0.5f)) desiredCam.y = Mathf.Max(floorY, floor - minAgl * 0.5f);

            // Position smoothing. Moving rigs smooth relative to the target, so fast targets do not drag the camera behind.
            if (snap) { camPos = desiredCam; camVel = Vector3.zero; camRel = desiredCam - targetPos; }
            else if (mode == "flyby" || mode == "track_from") camPos = Vector3.SmoothDamp(camPos, desiredCam, ref camVel, posSmooth, float.MaxValue, Mathf.Max(dt, 1e-4f));
            else
            {
                // Smooth the offset itself (kept between frames), not camPos - targetPos: a 300 m/s target
                // would otherwise drag the camera tens of metres behind its keyed position.
                camRel = Vector3.SmoothDamp(camRel, desiredCam - targetPos, ref camVel, mode == "keyframe" ? posSmooth : 0.08f, float.MaxValue, Mathf.Max(dt, 1e-4f));
                camPos = targetPos + camRel;
            }
            camPos.y = Mathf.Max(camPos.y, GroundY(camPos) + Mathf.Min(1.0f, minAgl));

            // Look point: critically damped with velocity feed-forward (no steady-state lag on a straight pass).
            Vector3 aimWant = lookTarget + targetVel * lookSmooth;
            if (snap) { lookPos = lookTarget; lookVel = targetVel; }
            else lookPos = Vector3.SmoothDamp(lookPos, aimWant, ref lookVel, lookSmooth, float.MaxValue, Mathf.Max(dt, 1e-4f));

            // Lens: fixed, keyframed, or auto-zoom to keep the target at `size` of the frame height.
            if (sizeFrac > 0 && unit != null)
            {
                float len = unit.definition != null ? Mathf.Max(unit.definition.length, unit.definition.width) : unit.maxRadius * 2f;
                float d = Mathf.Max(1f, (targetPos - camPos).magnitude);
                wantFov = 2f * Mathf.Atan(len / (2f * sizeFrac * d)) * Mathf.Rad2Deg;
            }
            wantFov = Mathf.Clamp(wantFov, fovMin, fovMax);
            curFov = snap ? wantFov : Mathf.Exp(Mathf.SmoothDamp(Mathf.Log(curFov), Mathf.Log(wantFov), ref fovVel, zoomSmooth, float.MaxValue, Mathf.Max(dt, 1e-4f)));

            // Composition: subject on a third with lead room in the direction of screen motion.
            Vector3 toLook = lookPos - camPos;
            if (toLook.sqrMagnitude < 0.01f) toLook = fwd;
            Quaternion baseRot = Quaternion.LookRotation(toLook.normalized, Vector3.up);
            Vector2 place;
            if (frame.HasValue) place = frame.Value;
            else
            {
                float wantX = 0;
                if (lead > 0 && targetVel.sqrMagnitude > 4f)
                {
                    Vector3 camRight = baseRot * Vector3.right;
                    Vector3 lateral = targetVel - Vector3.Dot(targetVel, toLook.normalized) * toLook.normalized;
                    float lat = Vector3.Dot(lateral, camRight);
                    if (Mathf.Abs(lat) > 0.15f * targetVel.magnitude) wantX = -Mathf.Sign(lat) * lead;
                    else wantX = leadX;   // head-on or tail-on: keep the current side
                }
                leadX = snap ? wantX : Mathf.SmoothDamp(leadX, wantX, ref leadXVel, F("lead_smooth", 1.2f), float.MaxValue, Mathf.Max(dt, 1e-4f));
                place = new Vector2(leadX, F("frame_y", 0f));
            }
            float vHalf = curFov * 0.5f * Mathf.Deg2Rad;
            float hHalf = Mathf.Atan(Mathf.Tan(vHalf) * cam.mainCamera.aspect);
            Vector3 e = baseRot.eulerAngles;
            float yaw = e.y - Mathf.Atan(place.x * Mathf.Tan(hHalf)) * Mathf.Rad2Deg;
            float pitch = e.x + Mathf.Atan(place.y * Mathf.Tan(vHalf)) * Mathf.Rad2Deg;
            float roll = horizonLock ? 0f : F("roll", 0f);
            Quaternion rot = Shake(Quaternion.Euler(pitch, yaw, roll));

            Vector3 local = new GlobalPosition(camPos).ToLocalPosition();
            cam.transform.SetPositionAndRotation(local, rot);
            ApplyFov(cam, curFov);
            cam.cameraVelocity = snap || dt <= 0 ? Vector3.zero : (mode == "flyby" || mode == "track_from" ? Vector3.zero : targetVel);
            SetShadowDistance((targetPos - camPos).magnitude);
            if (!preview && shotTime >= nextLog)
            {
                nextLog = shotTime + 0.5f;
                log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "Cine {0} t+{1:F2}: cam {2} target {3} dist {4:F0} ground {5:F1} fov {6:F1} (want {7:F1}, camera {8:F1}) place ({9:F2},{10:F2}) pitch {11:F1}",
                    mode, shotTime, Fmt(camPos), Fmt(targetPos), (targetPos - camPos).magnitude, ground, curFov, wantFov, cam.mainCamera.fieldOfView, place.x, place.y, pitch));
            }
            if (!preview) WriteTrace(cam, dt);
            if (!preview) LightingOverride.CameraPosed(cam.transform);
            if (!preview) snap = false;
        }

        private void CloseTrace()
        {
            if (trace == null) return;
            try { trace.Close(); } catch (Exception) { }
            trace = null;
        }

        private void WriteTrace(CameraStateManager cam, float dt)
        {
            if (CaptureFrame < 0 || TraceDir == null || unit == null) return;
            try
            {
                if (trace == null)
                {
                    Directory.CreateDirectory(TraceDir);
                    string path = Path.Combine(TraceDir, string.Format(CultureInfo.InvariantCulture, "cine_{0:F2}_{1}.csv", shotStart, mode));
                    bool first = traceFiles.Add(path);   // a shot that re-enters keeps appending to its file
                    trace = new StreamWriter(path, !first) { AutoFlush = false };
                    if (first) trace.WriteLine("frame,replay,shot_t,dt,udt,fixed_dt,interp_t,tx,ty,tz,vx,vy,vz,cx,cy,cz,lx,ly,lz,yaw,pitch,fov,sx,sy,sz,rx,ry,rz");
                    if (batcher == null) batcher = UnityEngine.Object.FindObjectOfType<SendTransformBatcher>();
                }
                var rc = RenderCamera(cam.mainCamera);
                Vector3 vp = rc.WorldToViewportPoint(unit.transform.position);
                Vector3 e = cam.transform.rotation.eulerAngles;
                double interp = batcher != null ? batcher.ServerSmoothTime : -1;
                var ic = CultureInfo.InvariantCulture;
                trace.WriteLine(string.Format(ic,
                    "{0},{1:F4},{2:F4},{3:F5},{4:F5},{5:F4},{6:F4},{7:F3},{8:F3},{9:F3},{10:F3},{11:F3},{12:F3},{13:F3},{14:F3},{15:F3},{16:F3},{17:F3},{18:F3},{19:F4},{20:F4},{21:F3},{22:F5},{23:F5},{24:F3},{25:F3},{26:F3},{27:F3}",
                    CaptureFrame, ReplayTime, shotTime, dt, Time.unscaledDeltaTime, Time.fixedDeltaTime, interp,
                    targetPos.x, targetPos.y, targetPos.z, targetVel.x, targetVel.y, targetVel.z,
                    camPos.x, camPos.y, camPos.z, lookPos.x, lookPos.y, lookPos.z, e.y, e.x, curFov, vp.x, vp.y, vp.z,
                    rawTargetPos.x, rawTargetPos.y, rawTargetPos.z));
            }
            catch (Exception ex)
            {
                log.LogWarning("Cine trace failed: " + ex.Message);
                CloseTrace();
                TraceDir = null;
            }
        }

        // The camera that actually renders. With the NOVR mod installed the game's main camera is disabled and
        // a child camera ("NOVR Main Camera", tagged MainCamera) renders from the same pose; otherwise it is the
        // main camera itself.
        internal static Camera RenderCamera(Camera main)
        {
            var m = Camera.main;
            if (m != null && main != null && m != main && m.enabled && m.transform.IsChildOf(main.transform)) return m;
            return main;
        }

        // Sets the lens (LensFov in Lens.cs handles NOVR, which blocks Camera.fieldOfView's setter). All of it is
        // undone when the cine shot ends.
        private LensFov lensFov;
        private LensFov Lens => lensFov ?? (lensFov = new LensFov(log, "Cine"));
        private bool usingMatrix => lensFov != null && lensFov.UsingMatrix;
        private bool fovLogged { set { if (!value) lensFov?.LogNext(); } }
        private void ApplyFov(CameraStateManager cam, float v) => Lens.Apply(cam, v);
        private void ResetLens(Camera main) => Lens.Reset(main);

        private Vector3 ChaseOffsetWorld()
        {
            Vector3 fwd = heading.sqrMagnitude > 1e-4f ? heading.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, Flat(fwd, 1f)).normalized;
            return ChaseOffsetWorld(fwd, right, Vector3.Cross(fwd, right).normalized);
        }

        private Vector3 ChaseOffsetWorld(Vector3 fwd, Vector3 right, Vector3 up)
        {
            if (backlight) return BacklightChaseOffset();
            float dist = F("distance", 35f), side = F("side", 8f), height = F("height", -3f);
            Vector3 local = new Vector3(side, height, -dist);
            local = Quaternion.AngleAxis(driftDeg, Vector3.up) * local;
            return Rel(local, fwd, right, up);
        }

        private static Vector3 Rel(Vector3 local, Vector3 fwd, Vector3 right, Vector3 up) => right * local.x + up * local.y + fwd * local.z;

        private void EvalKeys(Vector3 fwd, Vector3 right, Vector3 up, out Vector3 pos, out Vector3 look, out float keyFov)
        {
            float t0 = keys[0].T, tn = keys[keys.Count - 1].T;
            float t = Mathf.Clamp(shotTime, t0, tn);
            if (B("ease", true) && tn > t0)
            {
                float s = (t - t0) / (tn - t0);
                float eased = s * s * s * (s * (s * 6f - 15f) + 10f);    // smootherstep
                float w = F("ease_amount", 0.5f);
                t = t0 + Mathf.Lerp(s, eased, w) * (tn - t0);
            }
            int i = 0;
            while (i < keys.Count - 2 && t > keys[i + 1].T) i++;
            float u = Mathf.InverseLerp(keys[i].T, keys[i + 1].T, t);
            Vector3 KeyPos(int k) => keys[Mathf.Clamp(k, 0, keys.Count - 1)].World ? keys[Mathf.Clamp(k, 0, keys.Count - 1)].Pos
                : targetPos + Rel(keys[Mathf.Clamp(k, 0, keys.Count - 1)].Pos, fwd, right, up);
            Vector3 p0 = KeyPos(i - 1), p1 = KeyPos(i), p2 = KeyPos(i + 1), p3 = KeyPos(i + 2);
            if (i == 0) p0 = p1 + (p1 - p2);
            if (i + 2 >= keys.Count) p3 = p2 + (p2 - p1);
            pos = CatmullRom(p0, p1, p2, p3, u);
            Vector3 LookOf(Key k) => k.Look == null ? targetPos : k.LookWorld ? k.Look.Value : targetPos + Rel(k.Look.Value, fwd, right, up);
            float su = u * u * (3f - 2f * u);
            look = Vector3.Lerp(LookOf(keys[i]), LookOf(keys[i + 1]), su);
            float f1 = keys[i].Fov ?? fov, f2 = keys[i + 1].Fov ?? fov;
            keyFov = Mathf.Exp(Mathf.Lerp(Mathf.Log(f1), Mathf.Log(f2), su));
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private static Vector3 Flat(Vector3 v, float flatten)
        {
            Vector3 f = v; f.y *= Mathf.Clamp01(1f - flatten);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            return f.normalized;
        }

        // Highest of terrain/statics and sea level under a global point.
        internal static float GroundY(Vector3 global)
        {
            float y = 0f;   // sea level is global y = 0
            Vector3 local = new GlobalPosition(global).ToLocalPosition();
            if (Physics.Linecast(local + Vector3.up * 6000f, local - Vector3.up * 6000f, out var hit, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                y = Mathf.Max(y, hit.point.ToGlobalPosition().y);
            return y;
        }

        private static System.Reflection.PropertyInfo shadowProp;
        private void SetShadowDistance(float distance)
        {
            try
            {
                var asset = GraphicsSettings.currentRenderPipeline;
                if (asset == null) return;
                if (shadowProp == null) shadowProp = asset.GetType().GetProperty("shadowDistance");
                if (shadowProp == null) return;
                if (savedShadow < 0) savedShadow = (float)shadowProp.GetValue(asset);
                shadowProp.SetValue(asset, Mathf.Clamp(distance * 1.5f + 500f, 2000f, 8000f));
            }
            catch (Exception) { }
        }

        internal void OnEnter(CameraStateManager cam)
        {
            cam.cameraPivot.SetParent(null);
            cam.transform.SetParent(null, worldPositionStays: true);
            CameraStateManager.cameraMode = CameraMode.tv;
            cam.cockpitCamRender.enabled = false;
            cam.mainCamera.nearClipPlane = 1f;
            FlightHud.EnableCanvas(enable: false);
            if (savedFov < 0) savedFov = cam.desiredFOV;
            if (curFov <= 0) curFov = fov > 0 ? fov : cam.mainCamera.fieldOfView;
            if (unit != null && !unit.disabled) unit.SetDoppler(enabled: true);
            // Pose this very frame so nothing renders from the previous state's position.
            if (unit != null || mode == "hold" || mode == "path") Step(cam, preview: true);
        }

        internal void OnLeave(CameraStateManager cam)
        {
            CloseTrace();
            EndMount(cam);
            if (usingMatrix) ResetLens(cam.mainCamera);
            if (savedFov > 0) { cam.mainCamera.fieldOfView = savedFov; cam.desiredFOV = savedFov; }
            savedFov = -1;
            try
            {
                var asset = GraphicsSettings.currentRenderPipeline;
                if (asset != null && shadowProp != null && savedShadow > 0) shadowProp.SetValue(asset, savedShadow);
            }
            catch (Exception) { }
            savedShadow = -1;
        }

        internal sealed class CineState : CameraBaseState
        {
            private readonly CineCamera owner;
            private bool inStep;
            public CineState(CineCamera owner) { this.owner = owner; }
            public override void EnterState(CameraStateManager cam) => owner.OnEnter(cam);
            public override void LeaveState(CameraStateManager cam) => owner.OnLeave(cam);
            public override void UpdateState(CameraStateManager cam)
            {
                if (inStep || !owner.Active) return;
                inStep = true;
                try { owner.Step(cam); }
                finally { inStep = false; }
            }
            public override void FixedUpdateState(CameraStateManager cam) { }
        }
    }
}
