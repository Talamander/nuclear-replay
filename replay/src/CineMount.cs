using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Body-mounted close-up camera ("mount" shot type).
    //
    // The camera is rigidly attached in the target's LOCAL frame (x right, y up, z forward, metres), like a
    // GoPro or a camera-car rig, so it rides every roll and bank. The target pose is the filtered pose from the
    // jitter fix (position: velocity-integrating complementary filter; rotation: a second-order quaternion
    // tracker, `rot_smooth`), and the target is drawn at that same pose, so the airframe and the camera move as
    // one body and snapshot noise does not show at close range.
    //
    // Presets (shot "preset"): canopy_front, canopy_over_shoulder, wing_root, wingtip_back, belly_gear, nozzle,
    // weapon_release. Each preset is built from anchors discovered on the airframe at shot start (pilot eye point,
    // canopy, wing tips probed on the colliders, landing gear, hardpoints, jet nozzles, mesh bounds), then pushed
    // out of the hull with collider tests, so the same preset works on every airframe. Every value can be
    // overridden per shot:
    //   offset [x,y,z]      camera position in the local frame (replaces the preset's)
    //   nudge  [x,y,z]      added to the preset's camera position
    //   look   [x,y,z]      local point to look at;  look_dir [x,y,z]  local direction instead of a point
    //   look_nudge [x,y,z]  added to the preset's look point
    //   side "left"/"right" which wing / gear / shoulder (default right; weapon_release follows the weapon)
    //   fov, near (near clip, default 0.05 m), rot_smooth (default 0.12 s), horizon_lock (0..1 or bool; default 0)
    //   drift [x,y,z] metres and arc (degrees around the look point, about the local up axis), eased over
    //   drift_time seconds (default 6), so mounts are not static
    //   weapon "name" (weapon_release): station whose weapon name contains this; else the current station
    //   hull_check (default true), clearance (default 0.25 m)
    //   lod0 (default true): force the highest LOD on the target;  pilot (default true): pilot model visible
    //   interior (default false): also enable the first-person cockpit renderers
    // First-person presets (2026-10-03b): pov (the pilot's eye point, forward along the nose/flight path, cockpit
    // interior, HUD glass and canopy bow in frame) and pov_shoulder (inside the canopy behind one shoulder, the
    // helmet in the lower corner). They switch the aircraft to its cockpit model (Aircraft.SetCockpitRenderers),
    // enable the game's cockpit overlay camera, skip the hull check and hide the pilot (pov only). Extra options:
    //   pitch (deg, default 0 pov / -3 shoulder): look below (-) or above (+) the nose line
    //   flightpath (0..1, default 0.35): turn the look part way from the nose toward the velocity vector (max 12 deg)
    //   head (0..1, default 0.6): gentle head motion, looking into the turn from the body rates (max 8 deg yaw,
    //     6 deg pitch) and a few cm of G sag, smoothed over head_smooth (default 0.35 s)
    //   hud (default false, experimental): re-enable the game's FlightHud/CombatHUD canvases for the target
    //   cockpit_canvases (default true for pov presets): keep world-space canvases under the aircraft (MFDs) on
    // Discovery dumps airframe_<type>.json (anchors, parts, hardpoints, weapons, bounds) to the trace folder.
    internal sealed partial class CineCamera
    {
        private sealed class Airframe
        {
            public string Type;
            public Bounds Bounds;
            public Vector3 Eye;
            public bool HaveEye;
            public Vector3? Canopy;
            public Vector3? TipL, TipR;
            public List<Vector3> NoseGear = new List<Vector3>(), MainGear = new List<Vector3>();
            public List<Vector3> Nozzles = new List<Vector3>(), PartNozzles = new List<Vector3>();
            public List<KeyValuePair<string, Vector3>> Hardpoints = new List<KeyValuePair<string, Vector3>>();
        }

        // Mount state
        private Airframe air;
        private Vector3 mCam0, mLook0, mLookDir;
        private bool mUseLookDir;
        private Vector3 mDrift;
        private float mArc, mDriftTime, mHorizon, rotSmooth, mNear;
        private Quaternion rotS = Quaternion.identity, rawRot = Quaternion.identity;
        private Vector3 rotW;
        private bool haveRot, mountApplied;
        private readonly List<LODGroup> forcedLods = new List<LODGroup>();
        private Renderer[] enabledInterior;
        private Camera nearCam;
        private float savedRenderNear = -1, savedMainNear = -1;
        private string mPreset;
        private static readonly HashSet<string> dumpedTypes = new HashSet<string>();
        private static FieldInfo weaponIndexField, cockpitRenderersField;

        // First-person (pov / pov_shoulder) state
        private bool mPov, pilotHidden, povCockpitSet, povCamWasEnabled, povHud;
        private float mPitch, mFlightPath, mHead, mHeadSmooth, mSag, mSagVel;
        private Vector2 mHeadAng, mHeadVel;
        private Vector3 mPrevVel;
        private bool mHavePrevVel;
        private float savedCockpitNear = -1;
        private readonly List<Canvas> povCanvases = new List<Canvas>();
        private float nextCanvasScan;

        private bool IsMount => mode == "mount";

        // ---------- rotation filter ----------

        // Second-order (alpha-beta) tracker on the target rotation: predict with the estimated angular
        // velocity, then correct toward the raw rotation. Critically damped with natural frequency 1/rot_smooth,
        // so a steady roll or pull has no lag and frame-to-frame noise is averaged out.
        private void ReadRotation(float dt)
        {
            if (unit == null || unit.disabled || unit.transform == null) return;
            rawRot = unit.transform.rotation;
            if (!haveRot || dt <= 0 || rotSmooth <= 0) { rotS = rawRot; rotW = Vector3.zero; haveRot = true; return; }
            float wmag = rotW.magnitude;
            Quaternion pred = wmag > 1e-6f ? Quaternion.AngleAxis(wmag * dt * Mathf.Rad2Deg, rotW / wmag) * rotS : rotS;
            Quaternion err = rawRot * Quaternion.Inverse(pred);
            err.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            if (float.IsNaN(axis.x) || Mathf.Abs(ang) < 1e-5f) { rotS = pred; return; }
            if (Mathf.Abs(ang) > 30f) { rotS = rawRot; rotW = Vector3.zero; return; }   // teleport or respawn
            Vector3 e = axis.normalized * (ang * Mathf.Deg2Rad);
            float wn = 1f / rotSmooth;
            float k1 = Mathf.Min(1f, 2f * wn * dt);
            rotS = Quaternion.AngleAxis(k1 * ang, axis) * pred;
            rotW += e * (wn * wn * dt);
        }

        // ---------- discovery ----------

        private static Vector3 ToLocal(Transform root, Vector3 world) => Quaternion.Inverse(root.rotation) * (world - root.position);

        private Airframe Discover(Unit u)
        {
            var a = new Airframe { Type = u.definition != null ? u.definition.unitName : u.unitName };
            Transform root = u.transform;
            // Bounds from the damage-model parts (pivots and renderer centres), which cover the airframe; renderer
            // AABBs are not used directly because some renderers (skinned, effects) report far-away bounds.
            bool any = false;
            var b = new Bounds();
            void Add(Vector3 p) { if (p.sqrMagnitude > 60f * 60f) return; if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p); }
            foreach (var part in u.GetComponentsInChildren<UnitPart>(true))
            {
                Add(ToLocal(root, part.transform.position));
                var pr = part.GetComponent<Renderer>();
                if (pr != null) Add(ToLocal(root, pr.bounds.center));
                string pn = part.name.ToLowerInvariant();
                Vector3 pc = pr != null ? ToLocal(root, pr.bounds.center) : ToLocal(root, part.transform.position);
                if (pc.sqrMagnitude > 60f * 60f) continue;
                if (pn.StartsWith("wingtip")) { if (pc.x < 0) a.TipL = pc; else a.TipR = pc; }
                else if (pn.StartsWith("nozzle") || pn.StartsWith("hub")) a.PartNozzles.Add(ToLocal(root, part.transform.position));
            }
            if (!any) b = new Bounds(Vector3.zero, new Vector3(10f, 3f, 15f));
            b.Expand(1f);
            a.Bounds = b;

            if (u.cockpitViewPoint != null) { a.Eye = ToLocal(root, u.cockpitViewPoint.position); a.HaveEye = true; }
            var canopy = u.GetComponentInChildren<Canopy>(true);
            if (canopy != null) a.Canopy = ToLocal(root, canopy.transform.position);
            if (!a.HaveEye)
            {
                a.Eye = a.Canopy ?? new Vector3(0f, b.max.y - 0.7f, b.max.z - b.size.z * 0.3f);
                if (u is Aircraft ac0 && ac0.pilots != null && ac0.pilots.Length > 0 && ac0.pilots[0] != null)
                    a.Eye = ToLocal(root, ac0.pilots[0].transform.position) + Vector3.up * 0.7f;
            }
            var gears = u.GetComponentsInChildren<LandingGear>(true).Select(g => ToLocal(root, g.transform.position)).ToList();
            float noseZ = gears.Count > 0 ? gears.Max(g => g.z) : 0f;
            foreach (var p in gears)
                (gears.Count > 2 && Mathf.Abs(p.x) < 0.3f && p.z >= noseZ - 0.01f ? a.NoseGear : a.MainGear).Add(p);
            foreach (var n in u.GetComponentsInChildren<JetNozzle>(true)) a.Nozzles.Add(ToLocal(root, n.transform.position));
            // Lift nozzles (VTOL) sit well forward of the tail; keep the aft-most ones. Fall back to nozzle/hub parts.
            if (a.Nozzles.Count > 0) { float zmin = a.Nozzles.Min(p => p.z); a.Nozzles = a.Nozzles.Where(p => p.z < zmin + 1.5f).ToList(); }
            if (a.Nozzles.Count == 0) a.Nozzles.AddRange(a.PartNozzles);
            if (u is Aircraft ac && ac.weaponManager != null && ac.weaponManager.hardpointSets != null)
                foreach (var set in ac.weaponManager.hardpointSets)
                    if (set?.hardpoints != null)
                        foreach (var h in set.hardpoints)
                            if (h?.transform != null) a.Hardpoints.Add(new KeyValuePair<string, Vector3>(set.name, ToLocal(root, h.transform.position)));
            if (!a.TipL.HasValue) a.TipL = ProbeTip(u, root, b, -1f);
            if (!a.TipR.HasValue) a.TipR = ProbeTip(u, root, b, 1f);
            return a;
        }

        private static List<Collider> HullColliders(Unit u)
        {
            var list = new List<Collider>();
            foreach (var c in u.GetComponentsInChildren<Collider>(false))
                if (c != null && c.enabled && !c.isTrigger && c.GetComponentInParent<Weapon>() == null && c.GetComponentInParent<Pilot>() == null) list.Add(c);
            return list;
        }

        // Wing tip: probe straight down onto the unit's own colliders, sweeping inward from the outermost bound
        // until a station hits; the tip is the middle of the hit chord.
        private static Vector3? ProbeTip(Unit u, Transform root, Bounds b, float side)
        {
            var cols = HullColliders(u);
            if (cols.Count == 0) return null;
            float half = Mathf.Max(Mathf.Abs(b.max.x), Mathf.Abs(b.min.x));
            for (float f = 0.98f; f > 0.4f; f -= 0.03f)
            {
                float x = side * half * f;
                float zs = 0, ys = 0; int n = 0;
                for (int i = 0; i <= 40; i++)
                {
                    float z = Mathf.Lerp(b.min.z, b.max.z, i / 40f);
                    Vector3 o = root.TransformPoint(new Vector3(x, b.max.y + 2f, z));
                    var ray = new Ray(o, -root.up);
                    float best = float.MaxValue;
                    foreach (var c in cols) if (c.Raycast(ray, out var hit, b.size.y + 4f) && hit.distance < best) best = hit.distance;
                    if (best < float.MaxValue) { zs += z; ys += b.max.y + 2f - best; n++; }
                }
                if (n > 0) return new Vector3(x, ys / n, zs / n);
            }
            return null;
        }

        private void DumpAirframe(Unit u, Airframe a)
        {
            if (TraceDir == null || !dumpedTypes.Add(a.Type)) return;
            try
            {
                JArray V(Vector3 v) => new JArray(Math.Round(v.x, 3), Math.Round(v.y, 3), Math.Round(v.z, 3));
                var j = new JObject
                {
                    ["type"] = a.Type,
                    ["unit"] = u.unitName,
                    ["bounds_min"] = V(a.Bounds.min), ["bounds_max"] = V(a.Bounds.max),
                    ["definition_length"] = u.definition != null ? u.definition.length : 0f,
                    ["definition_width"] = u.definition != null ? u.definition.width : 0f,
                    ["eye"] = V(a.Eye), ["eye_from_cockpitViewPoint"] = a.HaveEye,
                    ["canopy"] = a.Canopy.HasValue ? (JToken)V(a.Canopy.Value) : null,
                    ["tip_left"] = a.TipL.HasValue ? (JToken)V(a.TipL.Value) : null,
                    ["tip_right"] = a.TipR.HasValue ? (JToken)V(a.TipR.Value) : null,
                    ["nose_gear"] = new JArray(a.NoseGear.Select(V)),
                    ["main_gear"] = new JArray(a.MainGear.Select(V)),
                    ["nozzles"] = new JArray(a.Nozzles.Select(V)),
                    ["hardpoints"] = new JArray(a.Hardpoints.Select(h => new JObject { ["set"] = h.Key, ["pos"] = V(h.Value) })),
                };
                if (u is Aircraft ac && ac.pilots != null)
                    j["pilots"] = new JArray(ac.pilots.Where(p => p != null).Select(p =>
                    {
                        var smr = p.GetComponentInChildren<SkinnedMeshRenderer>(true);
                        var o = new JObject { ["pos"] = V(ToLocal(u.transform, p.transform.position)) };
                        if (smr != null) { o["smr_enabled"] = smr.enabled; o["smr_center"] = V(ToLocal(u.transform, smr.bounds.center)); o["smr_size"] = V(smr.bounds.size); }
                        return o;
                    }));
                var st = new JArray();
                foreach (var s in u.weaponStations)
                {
                    if (s == null) continue;
                    st.Add(new JObject
                    {
                        ["number"] = s.Number,
                        ["weapon"] = s.WeaponInfo != null ? s.WeaponInfo.weaponName : null,
                        ["ammo"] = s.Ammo,
                        ["next_index"] = NextWeaponIndex(s),
                        ["weapons"] = new JArray(s.Weapons.Where(w => w != null).Select(w => V(ToLocal(u.transform, w.transform.position)))),
                    });
                }
                j["stations"] = st;
                var parts = new JArray();
                foreach (var p in u.GetComponentsInChildren<UnitPart>(true))
                {
                    var o = new JObject { ["name"] = p.name, ["pos"] = V(ToLocal(u.transform, p.transform.position)) };
                    var r = p.GetComponent<Renderer>();
                    if (r != null) { o["size"] = V(r.bounds.size); o["center"] = V(ToLocal(u.transform, r.bounds.center)); }
                    parts.Add(o);
                }
                j["parts"] = parts;
                j["lod_groups"] = u.GetComponentsInChildren<LODGroup>(true).Length;
                j["colliders"] = new JArray(HullColliders(u).Select(c => c.GetType().Name + ":" + c.name + (c is MeshCollider mc ? (mc.convex ? " convex" : " concave") : "")));
                Directory.CreateDirectory(TraceDir);
                File.WriteAllText(Path.Combine(TraceDir, "airframe_" + a.Type.Replace(' ', '_').Replace('/', '_') + ".json"), j.ToString());
            }
            catch (Exception ex) { log.LogWarning("Airframe dump failed: " + ex.Message); }
        }

        private static int NextWeaponIndex(WeaponStation s)
        {
            try
            {
                if (weaponIndexField == null) weaponIndexField = typeof(WeaponStation).GetField("weaponIndex", BindingFlags.Instance | BindingFlags.NonPublic);
                return weaponIndexField != null ? (int)weaponIndexField.GetValue(s) : -1;
            }
            catch (Exception) { return -1; }
        }

        // ---------- presets ----------

        private Vector3 VOr(string name, Vector3 fallback) => raw[name] is JArray a && a.Count == 3 ? V3(a) : fallback;

        // Camera distance from `subject` along `dir` so it sits `clearance` outside the outermost hull surface on
        // that line (found by casting back from far out onto the target's colliders), at least `minDist`.
        private float ProbeOut(List<Collider> cols, Vector3 subject, Vector3 dir, float clearance, float minDist, float nominal)
        {
            Transform root = unit.transform;
            dir = dir.normalized;
            Vector3 far = root.TransformPoint(subject + dir * 40f);
            var ray = new Ray(far, root.TransformDirection(-dir));
            float best = float.MaxValue;
            foreach (var c in cols) if (c.Raycast(ray, out var hit, 40f) && hit.distance < best) best = hit.distance;
            if (best == float.MaxValue) return Mathf.Max(minDist, nominal);
            return Mathf.Max(minDist, 40f - best + clearance);
        }

        private bool BuildPreset(List<Collider> cols, out Vector3 camL, out Vector3 lookL, out float presetFov)
        {
            var a = air;
            Bounds b = a.Bounds;
            float len = Mathf.Max(4f, b.size.z), halfSpan = Mathf.Max(Mathf.Abs(b.max.x), Mathf.Abs(b.min.x));
            string sideName = raw["side"]?.ToString().ToLowerInvariant();
            float s = sideName == "left" ? -1f : 1f;
            camL = Vector3.zero; lookL = Vector3.zero; presetFov = 50f;
            switch (mPreset)
            {
                case "canopy_front":
                {
                    Vector3 dir = new Vector3(0f, 0.3f, 1f);
                    float d = ProbeOut(cols, a.Eye, dir, 0.6f, 1.6f, 2.2f);
                    camL = a.Eye + dir.normalized * d;
                    lookL = a.Eye + new Vector3(0f, -0.05f, 0f);
                    presetFov = 45f;
                    break;
                }
                case "canopy_over_shoulder":
                {
                    Vector3 dir = new Vector3(0.35f * s, 0.5f, -1f);
                    float d = ProbeOut(cols, a.Eye, dir, 0.45f, 1.3f, 1.8f);
                    camL = a.Eye + dir.normalized * d;
                    lookL = a.Eye + new Vector3(0f, -0.1f, 10f);
                    presetFov = 62f;
                    break;
                }
                case "wing_root":
                {
                    // Under the wing root, looking out along the wing at the pylons and their ordnance.
                    var hps = a.Hardpoints.Where(h => h.Value.x * s > 0.8f).Select(h => h.Value).ToList();
                    Vector3 tip = (s < 0 ? a.TipL : a.TipR) ?? new Vector3(s * halfSpan * 0.9f, b.center.y, b.center.z);
                    Vector3 mid = hps.Count > 0 ? new Vector3(hps.Average(p => p.x), hps.Average(p => p.y), hps.Average(p => p.z)) : Vector3.Lerp(new Vector3(0f, tip.y, tip.z), tip, 0.5f);
                    float inboard = hps.Count > 0 ? hps.Min(p => Mathf.Abs(p.x)) : Mathf.Abs(mid.x) * 0.5f;
                    camL = new Vector3(s * Mathf.Max(0.8f, inboard * 0.45f), mid.y - 0.55f, mid.z - Mathf.Clamp(len * 0.18f, 2f, 3.5f));
                    lookL = new Vector3(s * Mathf.Abs(tip.x) * 0.8f, mid.y - 0.25f, Mathf.Lerp(mid.z, tip.z, 0.5f));
                    presetFov = 58f;
                    break;
                }
                case "wingtip_back":
                {
                    Vector3 tip = (s < 0 ? a.TipL : a.TipR) ?? new Vector3(s * halfSpan * 0.95f, b.center.y, b.center.z);
                    camL = tip + new Vector3(s * 0.7f, 0.7f, 1.6f);
                    lookL = new Vector3(s * Mathf.Abs(tip.x) * 0.15f, Mathf.Lerp(tip.y, a.Eye.y, 0.5f), b.center.z - len * 0.2f);
                    presetFov = 62f;
                    break;
                }
                case "belly_gear":
                {
                    var side = a.MainGear.Where(g => g.x * s > 0.2f).ToList();
                    Vector3 g = side.Count > 0 ? side[0] : a.MainGear.Count > 0 ? a.MainGear[0] : new Vector3(s * 1.2f, b.min.y + 0.6f, b.center.z - len * 0.1f);
                    camL = new Vector3(g.x + s * 0.35f, g.y - 0.75f, g.z + 3.4f);
                    lookL = new Vector3(g.x, g.y - 0.8f, g.z - 0.3f);
                    presetFov = 58f;
                    break;
                }
                case "nozzle":
                {
                    Vector3 n = a.Nozzles.Count > 0
                        ? new Vector3(a.Nozzles.Average(p => p.x), a.Nozzles.Average(p => p.y), a.Nozzles.Min(p => p.z))
                        : new Vector3(0f, b.center.y, b.min.z + 0.5f);
                    // Behind and above the exhaust, looking forward and down into the nozzles, so the plume and the
                    // afterburner run below the camera and the tail surfaces frame the shot.
                    camL = n + new Vector3(s * 0.8f, 1.0f, -3.2f);
                    lookL = n + new Vector3(0f, -0.1f, 0.3f);
                    presetFov = 58f;
                    break;
                }
                case "pov":
                {
                    // The pilot's eye point (Unit.cockpitViewPoint), looking along the nose.
                    camL = a.Eye;
                    lookL = a.Eye + new Vector3(0f, 0f, 20f);
                    presetFov = 50f;   // vertical; about 78 deg horizontal at 16:9
                    break;
                }
                case "pov_shoulder":
                {
                    // Inside the canopy, behind and above one shoulder: the helmet in the lower corner, the HUD
                    // glass and the panel ahead.
                    camL = a.Eye + new Vector3(0.2f * s, 0.2f, -0.3f);
                    lookL = a.Eye + new Vector3(0.05f * s, 0f, 20f);
                    presetFov = 58f;
                    break;
                }
                case "weapon_release":
                {
                    Vector3 h = ReleasePoint(a, b);
                    float hs = Mathf.Abs(h.x) > 0.6f ? Mathf.Sign(h.x) : s;
                    if (Mathf.Abs(h.x) > 0.6f) s = hs;
                    camL = Mathf.Abs(h.x) > 1.2f
                        ? h + new Vector3(-hs * 0.9f, -0.9f, -2.6f)       // under the wing, inboard and behind the pylon
                        : h + new Vector3(hs * 1.3f, -1.0f, -2.6f);      // belly or bay station: off to the side
                    lookL = h + new Vector3(0f, -0.15f, 1.2f);
                    presetFov = 55f;
                    break;
                }
                default:
                    return false;
            }
            return true;
        }

        private Vector3 ReleasePoint(Airframe a, Bounds b)
        {
            string want = raw["weapon"]?.ToString();
            WeaponStation station = null;
            if (!string.IsNullOrEmpty(want))
                station = unit.weaponStations.FirstOrDefault(s => s?.WeaponInfo != null && s.Weapons.Count > 0
                    && ((s.WeaponInfo.weaponName ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0
                        || (s.WeaponInfo.shortName ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0));
            if (station == null && unit is Aircraft ac && ac.weaponManager != null) station = ac.weaponManager.currentWeaponStation;
            if (station != null && station.Weapons.Count > 0)
            {
                int idx = raw["weapon_index"] != null ? (int)raw["weapon_index"] : NextWeaponIndex(station);
                if (idx < 0 || idx >= station.Weapons.Count) idx = 0;
                var w = station.Weapons[idx];
                if (w != null)
                {
                    Vector3 p = ToLocal(unit.transform, w.transform.position);
                    log.LogInfo($"Cine mount weapon_release: station {station.Number} {station.WeaponInfo?.weaponName} index {idx}/{station.Weapons.Count} at {Fmt(p)}");
                    return p;
                }
            }
            if (a.Hardpoints.Count > 0) return a.Hardpoints.OrderByDescending(h => Mathf.Abs(h.Value.x)).First().Value;
            return new Vector3(2f, b.min.y + 0.3f, b.center.z);
        }

        // ---------- hull test ----------

        private static bool InsideHull(List<Collider> cols, Vector3 world, float clearance)
        {
            foreach (var c in cols)
            {
                bool convex = !(c is MeshCollider mc) || mc.convex;
                if (convex)
                {
                    Vector3 cp = c.ClosestPoint(world);
                    if ((cp - world).sqrMagnitude <= clearance * clearance) return true;
                }
                else if (InsideConcave(c, world)) return true;
            }
            return false;
        }

        // Parity test for a concave mesh collider (raycasts only hit front faces): a point inside a closed mesh
        // sees one more surface entering from outside than leaving.
        private static bool InsideConcave(Collider c, Vector3 p)
        {
            Vector3[] dirs = { Vector3.up, Vector3.right, Vector3.forward };
            int votes = 0;
            foreach (var d in dirs)
            {
                int inward = Count(c, p + d * 60f, -d, 60f), outward = Count(c, p, d, 60f);
                if (inward > outward) votes++;
            }
            return votes >= 2;
        }

        private static int Count(Collider c, Vector3 from, Vector3 dir, float dist)
        {
            int n = 0; float travelled = 0;
            while (n < 16 && c.Raycast(new Ray(from + dir * travelled, dir), out var hit, dist - travelled))
            {
                n++; travelled += hit.distance + 0.01f;
                if (travelled >= dist) break;
            }
            return n;
        }

        // ---------- setup / step / teardown ----------

        private void SetupMount()
        {
            mPreset = (raw["preset"]?.ToString() ?? "canopy_front").ToLowerInvariant();
            mPov = mPreset == "pov" || mPreset == "pov_shoulder";
            mPitch = F("pitch", mPreset == "pov" ? 0f : -3f);
            mFlightPath = Mathf.Clamp01(F("flightpath", 0.35f));
            mHead = Mathf.Clamp01(F("head", 0.6f));
            mHeadSmooth = Mathf.Max(0.05f, F("head_smooth", 0.35f));
            mHeadAng = Vector2.zero; mHeadVel = Vector2.zero; mSag = 0; mSagVel = 0; mHavePrevVel = false;
            rotSmooth = Mathf.Max(0f, F("rot_smooth", 0.12f));
            mNear = Mathf.Clamp(F("near", 0.05f), 0.01f, 1f);
            mDrift = VOr("drift", Vector3.zero);
            mArc = F("arc", 0f);
            mDriftTime = Mathf.Max(0.5f, F("drift_time", 6f));
            mHorizon = raw["horizon_lock"] != null && raw["horizon_lock"].Type == JTokenType.Boolean ? ((bool)raw["horizon_lock"] ? 1f : 0f)
                : Mathf.Clamp01(F("horizon_lock", 0f));
            haveRot = false;
            ReadRotation(0f);

            Physics.SyncTransforms();
            air = Discover(unit);
            DumpAirframe(unit, air);
            var cols = HullColliders(unit);
            if (!BuildPreset(cols, out var camL, out var lookL, out float presetFov))
            {
                log.LogWarning($"Cine mount: unknown preset '{mPreset}', using canopy_front");
                mPreset = "canopy_front";
                BuildPreset(cols, out camL, out lookL, out presetFov);
            }
            if (raw["offset"] is JArray) camL = V3(raw["offset"]);
            camL += VOr("nudge", Vector3.zero);
            if (raw["look"] is JArray) lookL = V3(raw["look"]);
            lookL += VOr("look_nudge", Vector3.zero);
            mUseLookDir = raw["look_dir"] is JArray;
            mLookDir = mUseLookDir ? V3(raw["look_dir"]).normalized : Vector3.forward;
            if (fov <= 0) fov = presetFov;

            // Keep the whole drift path out of the hull: push out along the look-to-camera line.
            if (B("hull_check", !mPov) && cols.Count > 0)
            {
                float clr = F("clearance", 0.25f);
                Vector3 outDir = (camL - lookL).sqrMagnitude > 1e-4f ? (camL - lookL).normalized : Vector3.up;
                float pushed = 0;
                for (int k = 0; k < 40; k++)
                {
                    bool bad = false;
                    for (int i = 0; i <= 6 && !bad; i++)
                        bad = InsideHull(cols, unit.transform.TransformPoint(MountCamLocal(camL, lookL, i / 6f)), clr);
                    if (!bad) break;
                    camL += outDir * 0.1f; pushed += 0.1f;
                }
                if (pushed > 0) log.LogInfo($"Cine mount {mPreset}: camera was inside the hull, pushed out {pushed:F1} m");
            }
            mCam0 = camL; mLook0 = lookL;

            // Full detail at close range.
            forcedLods.Clear();
            if (B("lod0", true))
                foreach (var g in unit.GetComponentsInChildren<LODGroup>(true)) { g.ForceLOD(0); forcedLods.Add(g); }
            if (unit is Aircraft acp && acp.pilots != null)
            {
                bool showPilot = B("pilot", mPreset != "pov");
                foreach (var p in acp.pilots) if (p != null && !p.ejected && !p.dead) p.TogglePilotVisibility(showPilot);
                pilotHidden = !showPilot;
            }
            enabledInterior = null;
            povCockpitSet = false;
            if (mPov && B("interior", true) && unit is Aircraft acc)
            {
                try { acc.SetCockpitRenderers(true); povCockpitSet = true; }
                catch (Exception ex) { log.LogWarning("Cine pov SetCockpitRenderers: " + ex.Message); }
            }
            else if (B("interior", false) && unit is Aircraft aci)
            {
                try
                {
                    if (cockpitRenderersField == null) cockpitRenderersField = typeof(Aircraft).GetField("cockpitRenderers", BindingFlags.Instance | BindingFlags.NonPublic);
                    var rs = cockpitRenderersField?.GetValue(aci) as Renderer[];
                    if (rs != null) { enabledInterior = rs.Where(r => r != null && !r.enabled).ToArray(); foreach (var r in enabledInterior) r.enabled = true; }
                }
                catch (Exception ex) { log.LogWarning("Cine mount interior: " + ex.Message); }
            }
            if (mPov) SetupPovView();
            mountApplied = true;
            log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "Cine mount {0} on {1} ({2}): cam local {3} look local {4} fov {5:F0} near {6:F2} lods {7} bounds {8}..{9} eye {10}",
                mPreset, unit.unitName, air.Type, Fmt(camL), Fmt(lookL), fov, mNear, forcedLods.Count, Fmt(air.Bounds.min), Fmt(air.Bounds.max), Fmt(air.Eye)));
        }

        private Vector3 MountCamLocal(Vector3 camL, Vector3 lookL, float e)
        {
            Vector3 c = camL + mDrift * e;
            if (Mathf.Abs(mArc) > 1e-3f) c = lookL + Quaternion.AngleAxis(mArc * e, Vector3.up) * (c - lookL);
            return c;
        }

        // ---------- first person ----------

        // Look direction (local) for pov presets: the preset line pitched by `pitch`, turned part way toward the
        // flight path, plus gentle head motion into the turn. camL gets the G sag.
        private Vector3 PovLook(Vector3 baseL, Quaternion q, float dt, ref Vector3 camL)
        {
            Vector3 f = Quaternion.AngleAxis(-mPitch, Vector3.right) * baseL.normalized;
            Vector3 vL = Quaternion.Inverse(q) * targetVel;
            if (mFlightPath > 0 && vL.sqrMagnitude > 400f && vL.z > 0)
            {
                Vector3 vdir = vL.normalized;
                float ang = Vector3.Angle(Vector3.forward, vdir);
                if (ang > 12f) vdir = Vector3.Slerp(Vector3.forward, vdir, 12f / ang);
                f = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.forward, vdir), mFlightPath) * f;
            }
            Vector2 want = Vector2.zero;
            float sagWant = 0;
            if (mHead > 0 && dt > 0)
            {
                Vector3 wL = Quaternion.Inverse(q) * rotW;   // body rates, rad/s (x: nose down, y: nose right)
                want = new Vector2(Mathf.Clamp(wL.x * Mathf.Rad2Deg * 0.3f, -6f, 6f), Mathf.Clamp(wL.y * Mathf.Rad2Deg * 0.3f, -8f, 8f)) * mHead;
                if (mHavePrevVel)
                {
                    Vector3 aL = Quaternion.Inverse(q) * ((targetVel - mPrevVel) / dt + Vector3.up * 9.81f);
                    float g = aL.y / 9.81f;
                    sagWant = -Mathf.Clamp((g - 1f) * 0.012f, -0.03f, 0.07f) * mHead;
                }
            }
            if (dt > 0)
            {
                mPrevVel = targetVel; mHavePrevVel = true;
                mHeadAng = Vector2.SmoothDamp(mHeadAng, want, ref mHeadVel, mHeadSmooth, float.MaxValue, dt);
                mSag = Mathf.SmoothDamp(mSag, sagWant, ref mSagVel, mHeadSmooth, float.MaxValue, dt);
            }
            camL += Vector3.up * mSag;
            return Quaternion.Euler(mHeadAng.x, mHeadAng.y, 0f) * f;
        }

        private void PovDiag(string line)
        {
            log.LogInfo(line);
            try { if (TraceDir != null) { Directory.CreateDirectory(TraceDir); File.AppendAllText(Path.Combine(TraceDir, "pov_diag.txt"), $"{DateTime.Now:HH:mm:ss} {line}\n"); } }
            catch (Exception) { }
        }

        private void SetupPovView()
        {
            var cam = CameraStateManager.i;
            if (cam == null) return;
            povCamWasEnabled = cam.cockpitCamRender != null && cam.cockpitCamRender.enabled;
            if (cam.cockpitCamRender != null && B("cockpit_cam", true))
            {
                cam.cockpitCamRender.enabled = true;
                if (savedCockpitNear < 0) savedCockpitNear = cam.cockpitCamRender.nearClipPlane;
                cam.cockpitCamRender.nearClipPlane = mNear;
                var rc = RenderCamera(cam.mainCamera);
                PovDiag($"Cine pov {mPreset} on {unit.unitName} ({air.Type}) eye {Fmt(air.Eye)}: cockpit camera '{cam.cockpitCamRender.name}' parent '{cam.cockpitCamRender.transform.parent?.name}' "
                    + $"child of render camera {cam.cockpitCamRender.transform.IsChildOf(rc.transform)} mask 0x{cam.cockpitCamRender.cullingMask:X} depth {cam.cockpitCamRender.depth}; "
                    + $"render camera '{rc.name}' mask 0x{rc.cullingMask:X}");
            }
            povHud = B("hud", false);
            if (povHud && unit is Aircraft ah)
            {
                try
                {
                    SceneSingleton<FlightHud>.i?.SetAircraft(ah);
                    FlightHud.EnableCanvas(enable: true);
                    PovDiag("Cine pov: FlightHud enabled for " + ah.unitName + ", CombatHUD aircraft " + (SceneSingleton<CombatHUD>.i?.aircraft?.unitName ?? "none"));
                }
                catch (Exception ex) { log.LogWarning("Cine pov hud: " + ex.Message); }
            }
            nextCanvasScan = -1;
            povCanvases.Clear();
        }

        // Keeps the canvases a pov shot wants visible: the replay's -ncNoHud scan disables every root canvas once a
        // second in Update; this runs in LateUpdate, before rendering.
        private void StepPovView(CameraStateManager cam, Camera rc)
        {
            if (cam.cockpitCamRender != null && B("cockpit_cam", true))
            {
                if (!cam.cockpitCamRender.enabled) cam.cockpitCamRender.enabled = true;
                cam.cockpitCamRender.nearClipPlane = mNear;
                if (!cam.cockpitCamRender.transform.IsChildOf(rc.transform))
                    cam.cockpitCamRender.transform.SetPositionAndRotation(rc.transform.position, rc.transform.rotation);
            }
            if (shotTime >= nextCanvasScan)
            {
                nextCanvasScan = shotTime + 1f;
                povCanvases.Clear();
                if (B("cockpit_canvases", true) && unit != null)
                    foreach (var c in unit.GetComponentsInChildren<Canvas>(true)) if (c != null && c.isRootCanvas) povCanvases.Add(c);
                if (povHud)
                {
                    if (SceneSingleton<FlightHud>.i != null) foreach (var c in SceneSingleton<FlightHud>.i.GetComponentsInParent<Canvas>(true)) if (c.isRootCanvas) povCanvases.Add(c);
                    if (SceneSingleton<CombatHUD>.i != null) foreach (var c in SceneSingleton<CombatHUD>.i.GetComponentsInParent<Canvas>(true)) if (c.isRootCanvas) povCanvases.Add(c);
                    if (SceneSingleton<FlightHud>.i != null) foreach (var c in SceneSingleton<FlightHud>.i.GetComponentsInChildren<Canvas>(true)) if (c.isRootCanvas) povCanvases.Add(c);
                }
                if (nextLog <= 0.01f)
                    PovDiag("Cine pov canvases: " + string.Join(", ", povCanvases.Select(c => $"{c.name}({c.renderMode},{c.gameObject.activeInHierarchy})")));
            }
            foreach (var c in povCanvases) if (c != null && !c.enabled) c.enabled = true;
        }

        // The cockpit overlay camera needs the same lens as the render camera (NOVR blocks the fov setter).
        private void PovOverlayLens(CameraStateManager cam, Camera rc)
        {
            var oc = cam.cockpitCamRender;
            if (oc == null || !oc.enabled) return;
            oc.fieldOfView = curFov;
            if (Mathf.Abs(oc.fieldOfView - curFov) > 0.05f)
                oc.projectionMatrix = Matrix4x4.Perspective(curFov, rc.aspect, oc.nearClipPlane, oc.farClipPlane);
        }

        private void EndPovView(CameraStateManager cam)
        {
            if (povCockpitSet && unit is Aircraft ac && ac != null)
            {
                try { ac.SetCockpitRenderers(false); } catch (Exception) { }
            }
            povCockpitSet = false;
            if (pilotHidden && unit is Aircraft ap && ap != null && ap.pilots != null)
                foreach (var p in ap.pilots) if (p != null && !p.ejected && !p.dead) p.TogglePilotVisibility(true);
            pilotHidden = false;
            if (cam != null && cam.cockpitCamRender != null && mPov)
            {
                cam.cockpitCamRender.ResetProjectionMatrix();
                if (savedCockpitNear > 0) cam.cockpitCamRender.nearClipPlane = savedCockpitNear;
                cam.cockpitCamRender.enabled = povCamWasEnabled;
            }
            savedCockpitNear = -1;
            if (povHud) { try { FlightHud.EnableCanvas(enable: false); } catch (Exception) { } }
            povHud = false;
            povCanvases.Clear();
            mPov = false;
        }

        private void StepMount(CameraStateManager cam, float dt, bool preview)
        {
            ReadRotation(dt);
            if (!preview && smoothRender && unit != null && !unit.disabled && unit.transform != null)
            {
                if (targetSmooth > 0) unit.transform.position = new GlobalPosition(targetPos).ToLocalPosition();
                if (rotSmooth > 0) unit.transform.rotation = rotS;
            }
            float u = Mathf.Clamp01(shotTime / mDriftTime);
            float e = u * u * (3f - 2f * u);
            Vector3 camL = MountCamLocal(mCam0, mLook0, e);
            Quaternion q = rotS;
            camPos = targetPos + q * camL;
            Vector3 fwdL = mUseLookDir ? mLookDir : (mLook0 - camL);
            if (fwdL.sqrMagnitude < 1e-6f) fwdL = Vector3.forward;
            if (mPov) { fwdL = PovLook(fwdL, q, dt, ref camL); camPos = targetPos + q * camL; }
            lookPos = mUseLookDir || mPov ? camPos + q * fwdL * 10f : targetPos + q * mLook0;
            Vector3 fwdW = q * fwdL.normalized;
            Quaternion rot = Quaternion.LookRotation(fwdW, q * Vector3.up);
            if (mHorizon > 0) rot = Quaternion.Slerp(rot, Quaternion.LookRotation(fwdW, Vector3.up), mHorizon);

            // Near clip on both the main and the rendering camera (restored when the mount ends).
            var main = cam.mainCamera;
            var rc = RenderCamera(main);
            if (savedMainNear < 0) savedMainNear = main.nearClipPlane;
            if (rc != main && (nearCam != rc || savedRenderNear < 0)) { nearCam = rc; savedRenderNear = rc.nearClipPlane; }
            main.nearClipPlane = mNear;
            if (rc != main) rc.nearClipPlane = mNear;
            if (mPov) StepPovView(cam, rc);

            curFov = fov;
            cam.transform.SetPositionAndRotation(new GlobalPosition(camPos).ToLocalPosition(), rot);
            ApplyFov(cam, curFov);
            if (mPov) PovOverlayLens(cam, rc);
            cam.cameraVelocity = snap || dt <= 0 ? Vector3.zero : targetVel;
            SetShadowDistance(50f);
            if (!preview && shotTime >= nextLog)
            {
                nextLog = shotTime + 0.5f;
                float rawErr = Quaternion.Angle(rawRot, rotS);
                log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "Cine mount {0} t+{1:F2}: cam local {2} rot filter offset {3:F2} deg, pos filter offset {4:F2} m, near {5:F2}/{6:F2}",
                    mPreset, shotTime, Fmt(camL), rawErr, (rawTargetPos - targetPos).magnitude, main.nearClipPlane, rc.nearClipPlane));
            }
        }

        // Undo everything a mount shot changed: LOD force, interior renderers, near clip.
        private void EndMount(CameraStateManager cam)
        {
            if (!mountApplied) return;
            mountApplied = false;
            foreach (var g in forcedLods) if (g != null) g.ForceLOD(-1);
            forcedLods.Clear();
            if (enabledInterior != null) foreach (var r in enabledInterior) if (r != null) r.enabled = false;
            enabledInterior = null;
            EndPovView(cam);
            var main = cam != null ? cam.mainCamera : null;
            if (main != null && savedMainNear > 0) main.nearClipPlane = savedMainNear;
            if (nearCam != null && savedRenderNear > 0) nearCam.nearClipPlane = savedRenderNear;
            savedMainNear = -1; savedRenderNear = -1; nearCam = null;
        }
    }
}
