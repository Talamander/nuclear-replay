using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Drives the game's own camera system from a shot list.
    //
    // Shot list JSON: { "shots": [ { "t": 0, "camera": "orbit", "target": "host" }, ... ] }
    //   t       seconds of demo time since replay start
    //   camera  orbit | chase | cockpit | tv | free | relative
    //           director-owned cinematic cameras (CineCamera.cs): flyby | chase_cine | track_from | keyframe | mount | action
    //           | path | ride | two_shot
    //   target  host | auto | latest_missile | latest_host_missile | unit:<persistentID> | name:<substring of unit name>
    //           | type:<text> | none
    //           auto follows the host, cuts to each newly launched missile and back to the nearest aircraft
    //           unit: and name: work for every kind of unit (aircraft, helicopters, ships, ground vehicles, missiles,
    //           buildings); name: also matches a player's name. type: picks a live unit whose type or code contains
    //           the text, or whose kind is the text (ship, ground, helicopter, aircraft, missile, building), the one
    //           nearest the last view when several match (UnitList.cs)
    //           none: no unit at all, for a camera path in world space (other cameras never start with it)
    //   fov     optional field of view in degrees
    //   hold    optional seconds to stay on a destroyed target before cutting away (default 3)
    //   then    optional shot object (camera, target, fov, hold) to cut to when the target is gone and the hold
    //           is over, instead of the nearest aircraft (victim_cut: the victim at the moment the missile hits)
    //
    // When a target is destroyed the camera holds on the wreck for `hold` seconds, then follows the
    // live aircraft nearest to where the target died, so the replay never stares at empty ground. A missile
    // followed with a game camera gets a still cine "hold" view on its explosion for that time, because the
    // game's chase camera swings to the sky when the missile is gone.
    internal sealed class Director
    {
        internal sealed class Shot
        {
            public double T;
            public string Camera = "orbit";
            public string Target = "host";
            public float? Fov;
            public double Hold = 3;
            public JObject Raw;
            public string[] Mattes = new string[0];
            public override string ToString() => $"{T:F2}s {Camera} {Target}";
        }

        private readonly ManualLogSource log;
        private readonly List<Shot> shots = new List<Shot>();
        private readonly List<Missile> missiles = new List<Missile>();
        private readonly HashSet<Missile> seenMissiles = new HashSet<Missile>();
        // Every unit seen so far, so the viewer's unit list can show destroyed ones (the registry drops them).
        private readonly List<Unit> seenUnits = new List<Unit>();
        private readonly HashSet<Unit> seenSet = new HashSet<Unit>();
        private int next;
        private Shot current;
        private Unit currentUnit;
        private int autoIndex;
        private bool hadTarget, holding;
        private double lostAt = -1, lastCut = -10;
        private Vector3 lastPosition;
        private bool haveLastPosition;
        private Missile newestMissile;
        private readonly CineCamera cine;

        // Mattes requested by the shot in effect (engine matte pass); empty for ordinary shots.
        public string[] ActiveMattes => current != null ? current.Mattes : new string[0];
        public Unit MatteUnit => currentUnit != null && !currentUnit.disabled ? currentUnit : null;
        public bool AnyMattes => shots.Any(s => s.Mattes.Length > 0);

        public string Status => current == null ? "director idle" : "shot " + current + (currentUnit != null ? " -> " + currentUnit.unitName : " (no target yet)");

        public Director(ManualLogSource log, string shotListPath)
        {
            this.log = log;
            cine = new CineCamera(log) { Resolve = Resolve, Opponent = Opponent };
            if (string.IsNullOrEmpty(shotListPath) || !File.Exists(shotListPath))
            {
                shots.Add(new Shot { T = 0, Camera = "orbit", Target = "auto" });
                return;
            }
            var root = JObject.Parse(File.ReadAllText(shotListPath));
            foreach (var item in (JArray)root["shots"] ?? new JArray()) shots.Add(ParseShot(item));
            shots.Sort((a, b) => a.T.CompareTo(b.T));
            log.LogInfo($"Director loaded {shots.Count} shots from {shotListPath}");
        }

        private static Shot ParseShot(JToken item) => new Shot
        {
            T = (double?)item["t"] ?? 0, Camera = (string)item["camera"] ?? "orbit",
            Target = (string)item["target"] ?? "host", Fov = (float?)item["fov"], Hold = (double?)item["hold"] ?? 3,
            Raw = item as JObject,
            Mattes = item["mattes"] is JArray m ? m.Select(x => ((string)x ?? "").ToLowerInvariant()).ToArray() : new string[0]
        };

        // Live preview: swaps the whole shot list while the replay runs. The shot that the new list has in
        // effect at replayTime takes over at once (the camera cuts on the next Update); later shots follow
        // their times as usual.
        public void ReplaceShots(JArray list, double replayTime)
        {
            var parsed = (list ?? new JArray()).Select(ParseShot).ToList();
            parsed.Sort((a, b) => a.T.CompareTo(b.T));
            if (parsed.Count == 0) parsed.Add(new Shot { T = 0, Camera = "orbit", Target = "auto" });
            shots.Clear();
            shots.AddRange(parsed);
            next = 0; current = null; currentUnit = null; hadTarget = false; holding = false; lostAt = -1;
            if (cine.Active) cine.End();
            while (next < shots.Count && shots[next].T <= replayTime) current = shots[next++];
            log.LogInfo($"Director: shot list replaced at {replayTime:F2}s ({shots.Count} shots, now {(current != null ? current.ToString() : "waiting")})");
        }

        // Soft restart of the preview: leaves the director's cinematic camera before the scene goes away.
        public void Shutdown()
        {
            holding = false;
            try { if (cine.Active) cine.End(); } catch (Exception) { }
        }

        private double lastReplayTime;

        public void Update(double replayTime, bool sceneLive)
        {
            lastReplayTime = replayTime;
            TrackMissiles();
            if (!sceneLive) return;
            var cameras = CameraStateManager.i;
            if (cameras == null || cameras.currentState == null) return;
            while (next < shots.Count && shots[next].T <= replayTime)
            {
                var previous = current;
                current = shots[next++];
                // A shot that repeats the camera and target already on screen (say, the fallback cut to a victim
                // that a "then" cut already showed) keeps the camera as it is instead of cutting again.
                if (previous != null && currentUnit != null && !currentUnit.disabled && !holding && !CineCamera.IsCine(current.Camera)
                    && string.Equals(previous.Camera, current.Camera, StringComparison.OrdinalIgnoreCase) && previous.Target == current.Target)
                    continue;
                currentUnit = null; hadTarget = false; holding = false;
            }
            if (current == null) return;
            bool cineShot = CineCamera.IsCine(current.Camera);
            if (!cineShot && !holding && cine.Active) cine.End();
            // A cine shot keeps its own camera while the target is gone (the game drops to its free camera
            // when the followed unit dies); the rig holds on the last known position.
            if ((cineShot || holding) && cine.Active && cameras.currentState != cine.State) cameras.SwitchState(cine.State);
            // A camera path with no target starts once and runs on its own.
            if (current.Target == "none" && string.Equals(current.Camera, "path", StringComparison.OrdinalIgnoreCase))
            {
                if (!hadTarget) { Apply(cameras, current, null); hadTarget = true; lastCut = replayTime; }
                newestMissile = null;
                return;
            }
            bool alive = currentUnit != null && !currentUnit.disabled;
            if (alive)
            {
                lastPosition = currentUnit.transform.position;
                haveLastPosition = true;
                lostAt = -1;
                // A missile shot with a "then" and no hold cuts a moment before the missile reaches the "then"
                // target, so the cut lands on the hit instead of a frame or two of the explosion seen from the
                // game's chase camera.
                if (currentUnit is Missile && current.Hold <= 0 && current.Raw?["then"] is JObject soon && AboutToHit(currentUnit, soon))
                {
                    log.LogInfo($"Director: {current.Target} about to hit at {replayTime:F2}s");
                    TakeThen(soon, replayTime);
                    var victim = Resolve(current.Target);
                    if (victim != null && !victim.disabled) { Cut(cameras, victim, replayTime); newestMissile = null; return; }
                    alive = false;
                }
            }
            if (alive)
            {
                // Auto mode cuts to a fresh launch unless it is already riding a live missile.
                if (current.Target == "auto" && newestMissile != null && newestMissile != currentUnit && !newestMissile.disabled
                    && !(currentUnit is Missile) && replayTime - lastCut > 2)
                    Cut(cameras, newestMissile, replayTime);
                newestMissile = null;
                return;
            }
            newestMissile = null;
            if (hadTarget)
            {
                // Show the destruction for a moment before cutting away.
                if (lostAt < 0)
                {
                    lostAt = replayTime;
                    log.LogInfo($"Director: target {current.Target} gone at {replayTime:F2}s");
                    // The game's chase camera swings to the sky when the missile it follows detonates. Hold the
                    // current view on the missile's last position instead, until the hold ends.
                    if (!cineShot && currentUnit is Missile && current.Hold > 0)
                    {
                        try
                        {
                            var camGlobal = cameras.transform.position.ToGlobalPosition().AsVector3();
                            var lookGlobal = lastPosition.ToGlobalPosition().AsVector3();
                            cine.BeginHold(camGlobal, lookGlobal, cameras.mainCamera.fieldOfView, replayTime);
                            holding = true;
                            if (cameras.currentState != cine.State) cameras.SwitchState(cine.State);
                        }
                        catch (Exception e) { log.LogWarning("Director hold failed: " + e.Message); }
                    }
                }
                if (replayTime - lostAt < current.Hold) return;
                // "then": where to go once the target is gone (a victim cut at the moment a missile hits),
                // instead of the nearest aircraft.
                if (current.Raw?["then"] is JObject then)
                {
                    log.LogInfo($"Director: target gone, then {(string)then["camera"]} {(string)then["target"]}");
                    TakeThen(then, replayTime);
                }
            }
            var unit = Resolve(current.Target);
            if (unit == null || unit == currentUnit || unit.disabled) unit = hadTarget ? NearestAircraft(lastPosition) : null;
            if (unit != null) Cut(cameras, unit, replayTime);
        }

        // Makes a shot's "then" the current shot. hadTarget stays set: if the "then" target is gone too, the
        // nearest aircraft follows at once.
        private void TakeThen(JObject then, double replayTime)
        {
            var follow = ParseShot(then);
            follow.T = replayTime;
            current = follow;
            currentUnit = null;
            hadTarget = true;
            if (holding) { holding = false; if (!CineCamera.IsCine(current.Camera)) cine.End(); }
        }

        // True when the missile reaches the "then" target within about a frame and a half (or is inside a
        // 20 m proximity-fuse distance of it).
        private bool AboutToHit(Unit missile, JObject then)
        {
            var target = Resolve((string)then["target"] ?? "");
            if (target == null || target.disabled || target == missile) return false;
            Vector3 rel = target.transform.position - missile.transform.position;
            float distance = rel.magnitude;
            if (distance < 1f) return true;
            Vector3 relVel = (missile.rb != null ? missile.rb.velocity : Vector3.zero) - (target.rb != null ? target.rb.velocity : Vector3.zero);
            float closing = Mathf.Max(0f, Vector3.Dot(relVel, rel / distance));
            float dt = Mathf.Max(Time.deltaTime, 1f / 120f);
            return distance <= 20f + closing * dt * 1.5f;
        }

        private void Cut(CameraStateManager cameras, Unit unit, double replayTime)
        {
            if (holding)
            {
                holding = false;
                if (!CineCamera.IsCine(current.Camera)) cine.End();
            }
            Apply(cameras, current, unit);
            hadTarget = true;
            lastCut = replayTime;
            lostAt = -1;
            lastPosition = unit.transform.position;
            haveLastPosition = true;
        }

        private static Unit NearestAircraft(Vector3 position)
        {
            Unit best = null;
            float bestDistance = float.MaxValue;
            foreach (var aircraft in UnitRegistry.allAircraft)
            {
                if (aircraft == null || aircraft.disabled) continue;
                float distance = (aircraft.transform.position - position).sqrMagnitude;
                if (distance < bestDistance) { best = aircraft; bestDistance = distance; }
            }
            return best;
        }

        // Viewer: the unit and camera on screen now.
        public Unit CurrentUnit => currentUnit != null && !currentUnit.disabled ? currentUnit : null;

        // Viewer "next / previous unit": follows the live unit `step` places after (or before) the one on screen, among
        // the units in `filter` (UnitList.Filters; aircraft by default), by replacing the shot list with one manual
        // shot (FollowUnit).
        public Unit Follow(int step, double replayTime, out int position, out int count) => Follow(step, replayTime, "aircraft", out position, out count);

        public Unit Follow(int step, double replayTime, string filter, out int position, out int count)
        {
            position = 0;
            var units = (filter == "aircraft" ? UnitRegistry.allAircraft.Cast<Unit>() : UnitRegistry.allUnits)
                .Where(u => u != null && !u.disabled && UnitList.Matches(Units.Kind(u), filter, cycle: true))
                .OrderBy(u => u.persistentID.ToString()).ToList();
            count = units.Count;
            if (units.Count == 0) return null;
            int at = currentUnit != null ? units.IndexOf(currentUnit) : -1;
            int index = at < 0 ? (step > 0 ? 0 : units.Count - 1) : ((at + step) % units.Count + units.Count) % units.Count;
            var unit = units[index];
            position = index + 1;
            FollowUnit(unit, replayTime);
            return unit;
        }

        // Follows one unit from now on with the game camera of the current shot (chase when the shot is a cinematic
        // camera other than action, ride or two_shot, which follow any unit and keep their options: the ride offset,
        // the two-shot's second unit; orbit instead of cockpit for a unit that is not an aircraft), by replacing the
        // shot list with one manual shot.
        public void FollowUnit(Unit unit, double replayTime)
        {
            string cam = current?.Camera?.ToLowerInvariant();
            bool keep = cam == "action" || cam == "two_shot" || cam == "ride";
            string camera = keep ? cam
                : current != null && !CineCamera.IsCine(current.Camera) && State(CameraStateManager.i, current.Camera) != null ? current.Camera : "chase";
            if (camera == "cockpit" && !(unit is Aircraft)) camera = "orbit";
            var shot = keep && current.Raw != null ? (JObject)current.Raw.DeepClone() : new JObject();
            shot.Remove("then");
            shot["t"] = replayTime; shot["camera"] = camera; shot["target"] = "unit:" + unit.persistentID; shot["hold"] = 3;
            // A second unit (or look_at) that is the new target itself would frame the unit with itself.
            if (shot["second"]?.ToString() == "unit:" + unit.persistentID) shot.Remove("second");
            ReplaceShots(new JArray(shot), replayTime);
        }

        // Viewer unit list: every live unit plus, with `destroyed`, the ones seen earlier that are gone now.
        public List<UnitEntry> UnitEntries(bool destroyed, Vector3 from)
        {
            var list = new List<UnitEntry>();
            var added = new HashSet<Unit>();
            foreach (var u in UnitRegistry.allUnits)
                if (u != null && !u.disabled && added.Add(u)) list.Add(Units.Entry(u, from));
            if (destroyed)
                foreach (var u in seenUnits)
                    if (u != null && added.Add(u)) { var e = Units.Entry(u, from); e.Alive = false; list.Add(e); }
            return list;
        }

        public void CycleTarget()
        {
            var aircraft = UnitRegistry.allAircraft.Where(a => a != null && !a.disabled).ToList();
            if (aircraft.Count == 0 || CameraStateManager.i == null) return;
            autoIndex = (autoIndex + 1) % aircraft.Count;
            CameraStateManager.i.SetFollowingUnit(aircraft[autoIndex]);
        }

        public void CycleCamera()
        {
            var cameras = CameraStateManager.i;
            if (cameras == null || cameras.followingUnit == null) return;
            var order = new CameraBaseState[] { cameras.orbitState, cameras.chaseState, cameras.cockpitState, cameras.TVState, cameras.freeState };
            int at = Array.IndexOf(order, cameras.currentState);
            cameras.SwitchState(order[(at + 1) % order.Length]);
        }

        private void Apply(CameraStateManager cameras, Shot shot, Unit unit)
        {
            try
            {
                currentUnit = unit;
                if (unit != null) cameras.SetFollowingUnit(unit);
                if (CineCamera.IsCine(shot.Camera))
                {
                    cine.Begin(shot, unit, lastReplayTime);
                    if (cameras.currentState != cine.State) cameras.SwitchState(cine.State);
                    log.LogInfo("Director: " + shot + " -> " + (unit != null ? unit.unitName : "no target"));
                    return;
                }
                if (unit == null) return;
                // The cockpit camera only works in an aircraft; anything else gets the orbit camera.
                var state = State(cameras, !(unit is Aircraft) && string.Equals(shot.Camera, "cockpit", StringComparison.OrdinalIgnoreCase) ? "orbit" : shot.Camera);
                if (state != null && cameras.currentState != state) cameras.SwitchState(state);
                if (shot.Fov.HasValue) cameras.SetDesiredFoV(shot.Fov.Value, shot.Fov.Value);
                log.LogInfo("Director: " + shot + " -> " + unit.unitName);
            }
            catch (Exception e) { log.LogWarning("Director shot failed: " + shot + ": " + e.Message); }
        }

        private static CameraBaseState State(CameraStateManager cameras, string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "orbit": return cameras.orbitState;
                case "chase": return cameras.chaseState;
                case "cockpit": return cameras.cockpitState;
                case "tv": return cameras.TVState;
                case "free": return cameras.freeState;
                case "relative": return cameras.relativeState;
                default: return null;
            }
        }

        private void TrackMissiles()
        {
            var units = UnitRegistry.allUnits;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] is Missile missile && missile != null && seenMissiles.Add(missile)) { missiles.Add(missile); newestMissile = missile; }
                if (units[i] != null && seenSet.Add(units[i])) seenUnits.Add(units[i]);
            }
            missiles.RemoveAll(m => m == null);
            if (seenUnits.Count > 4000) { seenUnits.RemoveAll(u => u == null); seenSet.RemoveWhere(u => u == null); }
        }

        // Also the selector grammar of the ride camera's look_at and the two_shot camera's second (CineCamera.Resolve).
        internal Unit Resolve(string target)
        {
            target = target ?? "host";
            if (target == "auto")
            {
                // Auto follows the host, or once the host is down the aircraft nearest to the last view.
                var host = HostAircraft();
                return host != null && !host.disabled ? host : (Unit)NearestAircraft(lastPosition);
            }
            if (target == "host") return HostAircraft() ?? (Unit)NearestAircraft(lastPosition);
            if (target == "latest_missile") return missiles.LastOrDefault(m => m != null && !m.disabled);
            if (target == "latest_host_missile")
            {
                var host = HostAircraft();
                return host == null ? null : missiles.LastOrDefault(m => m != null && !m.disabled && m.owner == host);
            }
            if (target.StartsWith("unit:"))
            {
                var id = target.Substring(5);
                return UnitRegistry.allUnits.FirstOrDefault(u => u != null && u.persistentID.ToString() == id);
            }
            if (target.StartsWith("name:"))
            {
                var name = target.Substring(5);
                return UnitRegistry.allUnits.FirstOrDefault(u => u != null && !u.disabled && u.unitName != null
                    && u.unitName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    // A player's name when no unit is called that.
                    ?? UnitRegistry.allUnits.FirstOrDefault(u => u is Aircraft && !u.disabled
                        && (Units.Player(u) ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (target.StartsWith("type:"))
            {
                var near = lastPosition;
                bool haveNear = haveLastPosition;
                var entries = UnitRegistry.allUnits.Where(u => u != null && !u.disabled).Select(u => Units.Entry(u)).ToList();
                var pick = UnitList.PickType(entries, target.Substring(5),
                    haveNear ? e => (((Unit)e.Unit).transform.position - near).magnitude : (Func<UnitEntry, float>)null);
                return pick?.Unit as Unit;
            }
            return null;
        }

        // The unit `subject` is fighting, for a two_shot without a second: the target of its newest live missile, else
        // the shooter of the newest live missile aimed at it, else the first live unit on its own target list, else
        // the nearest enemy aircraft, else the nearest other aircraft. Null when there is none.
        internal Unit Opponent(Unit subject)
        {
            if (subject == null) return null;
            for (int i = missiles.Count - 1; i >= 0; i--)
            {
                var m = missiles[i];
                if (m == null || m.disabled || m.owner != subject) continue;
                if (UnitRegistry.TryGetUnit(m.targetID, out Unit target) && target != null && !target.disabled && target != subject) return target;
            }
            for (int i = missiles.Count - 1; i >= 0; i--)
            {
                var m = missiles[i];
                if (m == null || m.disabled || m.owner == null || m.owner.disabled || m.owner == subject) continue;
                if (UnitRegistry.TryGetUnit(m.targetID, out Unit target) && target == subject) return m.owner;
            }
            try
            {
                if (subject is Aircraft aircraft && aircraft.weaponManager != null)
                    foreach (var t in aircraft.weaponManager.GetTargetList())
                        if (t != null && !t.disabled && t != subject) return t;
            }
            catch (Exception) { }
            Unit enemy = null, any = null;
            float enemyD = float.MaxValue, anyD = float.MaxValue;
            Vector3 at = subject.transform.position;
            foreach (var a in UnitRegistry.allAircraft)
            {
                if (a == null || a.disabled || a == subject) continue;
                float d = (a.transform.position - at).sqrMagnitude;
                if (Units.Enemies(subject, a)) { if (d < enemyD) { enemyD = d; enemy = a; } }
                else if (d < anyD) { anyD = d; any = a; }
            }
            return enemy ?? any;
        }

        internal static Aircraft HostAircraft()
        {
            foreach (var player in UnityEngine.Object.FindObjectsOfType<Player>())
                if (player != null && player.IsHostPlayer && player.Aircraft != null) return player.Aircraft;
            return null;
        }
    }
}
