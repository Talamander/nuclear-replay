using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Viewer keys for any unit as a target, the ride-along camera and the two-shot camera.
    //
    //   [ / ]      previous / next unit in the unit filter (aircraft by default; the unit list sets it)
    //   B          unit list: type to search (callsign, player, type, side), Up / Down and Enter or a click pick the
    //              unit, Tab changes the filter (aircraft, ground, sea, all), Escape closes; destroyed units only
    //              with the list's "Destroyed" button
    //   Shift+B    the same list, but the pick becomes the two-shot's second unit
    //   9          two-shot (a camera style): the unit on screen and its opponent, or the Shift+B pick
    //   0          ride-along: the first time, pauses and puts the free camera next to the unit on screen; fly it
    //              to the spot and press R. Once locked, 0 plays the ride-along again.
    //   R          free camera: lock a ride-along on the unit being placed for (or the nearest unit); during a
    //              ride-along: unlock it (back to the free camera at the ride pose, paused) to place it again
    //   Shift+R    ride-along roll: the horizon banks with the unit, or stays level
    //
    // The shots are the same "ride" and "two_shot" shots a shot list uses (CineRide.cs, CineTwoShot.cs), so a clip
    // of them renders the same in nr.
    public sealed partial class ReplayPlugin
    {
        private string unitFilter = "aircraft";
        private readonly ViewerUi.UnitPanel unitListPanel = new ViewerUi.UnitPanel();
        private double unitListPanelRefresh;
        // Unit ids, not units: a backward jump reloads the mission and makes new unit objects.
        private string twoShotTarget, twoShotSecond;
        private Unit rideUnit;              // the unit a ride-along is being placed for (free camera on)
        private bool ridePlacing, ridePausedByUs;
        private JObject rideShot;           // the locked ride-along: target, offset, look_dir, roll, fov
        private string rideRoll = "unit";

        private const string RideId = "ride";

        // Keys of this file (ViewerKeys). Returns true when the unit list has the keyboard.
        private bool CamerasKeys()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (unitListPanel.Open) { UnitListKeys(shift); return true; }
            if (Input.GetKeyDown(KeyCode.B)) ViewerUnitList(shift);
            if (studio.Open) return false;   // the studio's free camera is for its own keys
            if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0)) ViewerRide();
            if (Input.GetKeyDown(KeyCode.R))
            {
                if (shift) ViewerRideRoll();
                else ViewerRideLock();
            }
            return false;
        }

        // A new recording: nothing picked yet.
        private void CamerasReset()
        {
            UnitListClose();
            unitFilter = "aircraft";
            twoShotTarget = twoShotSecond = null;
            rideUnit = null; ridePlacing = false; ridePausedByUs = false; rideShot = null; rideRoll = "unit";
            if (viewerPreset == RideId) viewerPreset = "cinematic_chase";
        }

        // ---------------- shots for a camera style ----------------

        // The director's shots for a style (ApplyPreset, clips): ViewerPresets plus the ride-along and the
        // two-shot's units picked in the viewer.
        private JArray PresetShots(string id, ViewerEvent moment, double from, double to)
        {
            if (id == RideId)
            {
                if (rideShot != null)
                {
                    var ride = (JObject)rideShot.DeepClone();
                    ride["t"] = Math.Round(from, 3);
                    return new JArray(ride);
                }
                id = "chase";
            }
            var shots = ViewerPresets.Build(id, moment, from, to, viewerDemo?.Events ?? new List<ViewerEvent>());
            if (id == "two_shot" && shots.Count > 0 && shots[0] is JObject shot)
            {
                // A hand-picked time: the unit that was on screen when 9 was pressed (a moment keeps its shooter).
                if (moment == null && twoShotTarget != null) shot["target"] = "unit:" + twoShotTarget;
                if (twoShotSecond != null && "unit:" + twoShotSecond != shot["target"]?.ToString()) shot["second"] = "unit:" + twoShotSecond;
            }
            return shots;
        }

        private static bool Live(Unit unit) => unit != null && !unit.disabled;

        // Called before a camera style is applied from the viewer (keys 1 - 9, 0).
        private void PresetChosen(string id)
        {
            var unit = director?.CurrentUnit;
            if (id == "two_shot" && unit != null) twoShotTarget = unit.persistentID.ToString();
        }

        // A unit followed with [ ] or the list keeps the two-shot and the ride-along on it.
        private void TargetChosen(Unit unit)
        {
            if (unit == null) return;
            if (viewerPreset == "two_shot") twoShotTarget = unit.persistentID.ToString();
            if (viewerPreset == RideId && rideShot != null) rideShot["target"] = "unit:" + unit.persistentID;
        }

        private static string FilterWord(string filter, int count)
        {
            switch (filter)
            {
                case "ground": return count == 1 ? "ground vehicle" : "ground vehicles";
                case "sea": return count == 1 ? "ship" : "ships";
                case "all": return count == 1 ? "unit" : "units";
                default: return "aircraft";
            }
        }

        // ---------------- unit list ----------------

        // Player names by unit id from the recording's annotations (Units.Player), for the unit list and name:.
        private void LearnPlayers(NuclearReplay.Demo.DemoReader demo)
        {
            Units.ClearRecordedPlayers();
            int annotations = 0;
            foreach (var record in demo.Records)
            {
                if (record.Kind != NuclearReplay.Demo.DemoFormat.KindAnnotation) continue;
                annotations++;
                try { Units.LearnPlayers(Newtonsoft.Json.Linq.JToken.Parse(record.Text)); } catch (Exception) { }
            }
            Logger.LogInfo($"Units: {Units.RecordedPlayerCount} player-flown units named in {annotations} annotations; recording player's side {Units.RecordedHome ?? "unknown"}");
        }

        internal void ViewerUnitList(bool pickSecond)
        {
            if (!viewer || director == null || server == null || recorder != null) return;
            if (unitListPanel.Open && unitListPanel.PickSecond == pickSecond) { UnitListClose(); return; }
            unitListPanel.Open = true;
            unitListPanel.PickSecond = pickSecond;
            unitListPanel.Query = "";
            unitListPanel.Filter = unitFilter;
            unitListPanel.Selected = 0;
            UnitListRefresh(false);
            // Start on the unit on screen (or the current second unit), so Up / Down move from there.
            string start = pickSecond && twoShotSecond != null ? twoShotSecond : unitListPanel.CurrentId;
            int at = start != null ? unitListPanel.Rows.FindIndex(r => r.Id == start) : -1;
            if (at >= 0) { unitListPanel.Selected = at; unitListPanel.Version++; }
            ViewerUi.KeyboardTaken = true;
            ViewerCursor(true);
        }

        internal void UnitListClose()
        {
            unitListPanel.Open = false;
            ViewerUi.KeyboardTaken = false;
        }

        private void UnitListRefresh(bool keepSelection)
        {
            if (director == null) return;
            var cam = CameraStateManager.i;
            Vector3 from = cam != null ? cam.transform.position : Vector3.zero;
            var keep = keepSelection && unitListPanel.Selected >= 0 && unitListPanel.Selected < unitListPanel.Rows.Count ? unitListPanel.Rows[unitListPanel.Selected].Unit : null;
            unitListPanel.Rows = UnitList.View(director.UnitEntries(unitListPanel.ShowDestroyed, from), unitListPanel.Filter, unitListPanel.Query, unitListPanel.ShowDestroyed);
            int at = keep != null ? unitListPanel.Rows.FindIndex(r => r.Unit == keep) : -1;
            unitListPanel.Selected = at >= 0 ? at : Mathf.Clamp(unitListPanel.Selected, 0, Math.Max(0, unitListPanel.Rows.Count - 1));
            unitListPanel.CurrentId = director.CurrentUnit != null ? director.CurrentUnit.persistentID.ToString() : null;
            unitListPanel.SecondId = twoShotSecond;
            unitListPanel.Version++;
            unitListPanelRefresh = Time.realtimeSinceStartupAsDouble + 0.5;
        }

        // Every frame while the list is open (ViewerUpdate).
        private void UnitListTick()
        {
            if (!unitListPanel.Open) return;
            if (!viewer || server == null || recorder != null) { UnitListClose(); return; }
            if (Time.realtimeSinceStartupAsDouble >= unitListPanelRefresh) UnitListRefresh(true);
        }

        private void UnitListKeys(bool shift)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { UnitListClose(); return; }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { UnitListPick(unitListPanel.Selected); return; }
            if (Input.GetKeyDown(KeyCode.Tab)) { UnitListFilter(UnitList.NextFilter(unitListPanel.Filter, shift ? -1 : 1)); return; }
            int move = (Input.GetKeyDown(KeyCode.DownArrow) ? 1 : 0) - (Input.GetKeyDown(KeyCode.UpArrow) ? 1 : 0)
                + (Input.GetKeyDown(KeyCode.PageDown) ? ViewerUi.UnitRows : 0) - (Input.GetKeyDown(KeyCode.PageUp) ? ViewerUi.UnitRows : 0);
            if (ViewerUi.PointerOverViewerUi) move -= Mathf.RoundToInt(Input.mouseScrollDelta.y) * 3;
            if (move != 0 && unitListPanel.Rows.Count > 0)
            {
                unitListPanel.Selected = Mathf.Clamp(unitListPanel.Selected + move, 0, unitListPanel.Rows.Count - 1);
                unitListPanel.Version++;
            }
            string typed = Input.inputString;
            if (string.IsNullOrEmpty(typed)) return;
            string query = unitListPanel.Query;
            foreach (char c in typed)
            {
                if (c == '\b') { if (query.Length > 0) query = query.Substring(0, query.Length - 1); }
                else if (c == '\n' || c == '\r' || c == '\t' || c == 27) { }
                else if (!char.IsControl(c) && query.Length < 40) query += c;
            }
            UnitListSearch(query);
        }

        internal void UnitListSearch(string query)
        {
            if (query == unitListPanel.Query) return;
            unitListPanel.Query = query ?? "";
            unitListPanel.Selected = 0;
            UnitListRefresh(false);
        }

        internal void UnitListFilter(string filter)
        {
            if (Array.IndexOf(UnitList.Filters, filter) < 0) return;
            unitListPanel.Filter = filter;
            unitListPanel.Selected = 0;
            if (!unitListPanel.PickSecond) unitFilter = filter;
            UnitListRefresh(false);
        }

        internal void UnitListToggleDestroyed()
        {
            unitListPanel.ShowDestroyed = !unitListPanel.ShowDestroyed;
            UnitListRefresh(true);
        }

        // Targets the unit of a row (or makes it the two-shot's second unit).
        internal void UnitListPick(int row)
        {
            if (row < 0 || row >= unitListPanel.Rows.Count) return;
            var entry = unitListPanel.Rows[row];
            var unit = entry.Unit as Unit;
            UnitListClose();
            if (unit == null || server == null || director == null) return;
            double now = server.Clock - server.StartTime;
            if (unitListPanel.PickSecond)
            {
                twoShotSecond = entry.Id;
                ui.Toast($"Two-shot second unit: {entry.Label}. 9 frames it with the unit on screen.", 3f);
                if (viewerPreset == "two_shot" && !freeCamOn) ApplyPreset(viewerPreset, now);
                return;
            }
            StudioLeave();
            if (freeCamOn && !ridePlacing) SetFreeCam(false);
            string filter = UnitList.FilterOf(entry.Kind);
            unitFilter = filter;
            rideUnit = unit;
            if (ridePlacing) { ui.Toast($"Ride-along for {entry.Label}: fly the camera next to it and press R.", 4f); return; }
            director.FollowUnit(unit, now);
            TargetChosen(unit);
            ui.Toast($"Following {entry.Label}{(entry.Alive ? "" : " (destroyed)")}. [ ] step through {FilterWord(filter, 2)}.", 3f);
        }

        // ---------------- ride-along ----------------

        // Key 0: plays the locked ride-along, or starts placing one on the unit on screen.
        internal void ViewerRide()
        {
            if (!viewer || server == null || director == null || recorder != null) return;
            if (rideShot != null && !ridePlacing)
            {
                var unit = director.CurrentUnit;
                // A different unit on screen than the locked one: the same offset on that unit.
                if (unit != null && !freeCamOn && viewerPreset != RideId && rideShot["target"]?.ToString() != "unit:" + unit.persistentID && (unit is Aircraft) == RideOnAircraft)
                    rideShot["target"] = "unit:" + unit.persistentID;
                ViewerPreset(RideId);
                ui.Toast("Ride-along. R unlocks it to place it again, Shift+R changes the roll.", 3f);
                return;
            }
            RideStartPlacing(director.CurrentUnit ?? rideUnit, keepPose: false);
        }

        private bool RideOnAircraft = true;

        private void RideStartPlacing(Unit unit, bool keepPose)
        {
            if (!Live(unit)) { ui.Toast("Pick a unit first ([ ] or B), then press 0 to place a ride-along next to it.", 4f); return; }
            rideUnit = unit;
            ridePlacing = true;
            if (!viewerPaused) { ViewerTogglePause(); ridePausedByUs = true; }
            StudioLeave();
            if (freeFly == null) freeFly = new FreeFlyCamera();
            if (!keepPose)
            {
                // Start at the default ride-along spot: off the right side, a little behind and above, looking at it.
                float size = Units.Size(unit);
                Vector3 pos = unit.transform.position.ToGlobalPosition().AsVector3();
                Vector3 cam = pos + unit.transform.rotation * (new Vector3(0.9f, 0.16f, -0.4f) * size);
                var look = Quaternion.LookRotation(pos - cam, Vector3.up).eulerAngles;
                if (freeCamOn) SetFreeCam(false);
                freeFly.Position = cam;
                freeFly.Yaw = look.y;
                freeFly.Pitch = look.x > 180f ? look.x - 360f : look.x;
                freeFly.KeepPose = true;
            }
            if (!freeCamOn) SetFreeCam(true);
            ui.Toast($"Ride-along for {UnitLabel(unit)}: the replay is paused. Fly the camera to where it should ride (WASD, Q/E, right mouse), then press R to lock it.", 6f);
        }

        // R: lock the ride-along (free camera), or unlock it (ride-along on screen).
        internal void ViewerRideLock()
        {
            if (!viewer || server == null || director == null || recorder != null) return;
            if (freeCamOn && freeFly != null)
            {
                var unit = ridePlacing && Live(rideUnit) ? rideUnit : NearestUnit(freeFly.Position, 3000f);
                if (unit == null) { ui.Toast("No unit near the camera to ride with. Press 0 to place a ride-along on the unit on screen.", 4f); return; }
                RideLock(unit);
                return;
            }
            if (viewerPreset == RideId && rideShot != null)
            {
                var unit = director.CurrentUnit ?? rideUnit;
                RideStartPlacing(unit, keepPose: true);   // the free camera starts at the ride-along's pose
                return;
            }
            ui.Toast("R locks a ride-along: press 0, or fly the free camera (F) next to a unit, then press R.", 4f);
        }

        private void RideLock(Unit unit)
        {
            var camRot = Quaternion.Euler(freeFly.Pitch, freeFly.Yaw, 0f);
            Vector3 unitPos = unit.transform.position.ToGlobalPosition().AsVector3();
            CameraFraming.RideFromPose(N(freeFly.Position), Q(camRot), N(unitPos), Q(unit.transform.rotation), out var offset, out var lookDir);
            float fov = freeFly.Fov > 0 ? freeFly.Fov : CameraStateManager.i != null ? CameraStateManager.i.mainCamera.fieldOfView : 50f;
            rideShot = new JObject
            {
                ["t"] = 0, ["camera"] = RideId, ["target"] = "unit:" + unit.persistentID,
                ["offset"] = Vec(offset), ["look_dir"] = Vec(lookDir), ["roll"] = rideRoll, ["fov"] = Math.Round(fov, 1)
            };
            RideOnAircraft = unit is Aircraft;
            ridePlacing = false;
            rideUnit = unit;
            viewerPreset = RideId;
            SetFreeCam(false);   // applies the ride-along (ApplyPreset)
            if (ridePausedByUs && viewerPaused) ViewerTogglePause();
            ridePausedByUs = false;
            Logger.LogInfo("Viewer ride-along: " + rideShot.ToString(Newtonsoft.Json.Formatting.None));
            ui.Toast(string.Format(CultureInfo.InvariantCulture, "Ride-along locked on {0}: {1:F0} m right, {2:F0} m up, {3:F0} m forward. R unlocks, Shift+R roll ({4}).",
                UnitLabel(unit), offset.X, offset.Y, offset.Z, rideRoll), 5f);
        }

        // Shift+R: the horizon banks with the unit, or stays level.
        internal void ViewerRideRoll()
        {
            if (!viewer) return;
            rideRoll = rideRoll == "unit" ? "level" : "unit";
            if (rideShot != null) rideShot["roll"] = rideRoll;
            if (viewerPreset == RideId && !freeCamOn && server != null) ApplyPreset(viewerPreset, server.Clock - server.StartTime);
            ui.Toast(rideRoll == "unit" ? "Ride-along roll: the horizon banks with the unit." : "Ride-along roll: the horizon stays level.", 3f);
        }

        private Unit NearestUnit(Vector3 globalPos, float within)
        {
            Unit best = null;
            float bestD = within * within;
            foreach (var u in UnitRegistry.allUnits)
            {
                if (!Live(u) || u.transform == null) continue;
                var kind = Units.Kind(u);
                if (kind == UnitKind.Building || kind == UnitKind.Other || kind == UnitKind.Missile) continue;
                float d = (u.transform.position.ToGlobalPosition().AsVector3() - globalPos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = u; }
            }
            return best;
        }

        private static System.Numerics.Vector3 N(Vector3 v) => new System.Numerics.Vector3(v.x, v.y, v.z);
        private static System.Numerics.Quaternion Q(Quaternion q) => new System.Numerics.Quaternion(q.x, q.y, q.z, q.w);
        private static JArray Vec(System.Numerics.Vector3 v) => new JArray(Math.Round(v.X, 2), Math.Round(v.Y, 2), Math.Round(v.Z, 3));

        // ---------------- viewer commands (test hook) and status ----------------

        private string CamerasCommand(string cmd, string arg)
        {
            switch (cmd)
            {
                case "units": ViewerUnitList(false); return $"unit list open, {unitListPanel.Rows.Count} rows ({unitListPanel.Filter})";
                case "units-second": ViewerUnitList(true); return $"unit list (second unit) open, {unitListPanel.Rows.Count} rows";
                case "units-close": UnitListClose(); return "unit list closed";
                case "units-search": UnitListSearch(arg); return $"{unitListPanel.Rows.Count} rows for \"{arg}\": " + string.Join(", ", unitListPanel.Rows.Take(5).Select(r => r.Label));
                case "units-filter": UnitListFilter(arg); return $"filter {unitListPanel.Filter}, {unitListPanel.Rows.Count} rows";
                case "units-destroyed": if ((arg != "off") != unitListPanel.ShowDestroyed) UnitListToggleDestroyed(); return $"destroyed shown {unitListPanel.ShowDestroyed}, {unitListPanel.Rows.Count} rows";
                case "units-pick":
                    {
                        int row = int.TryParse(arg, out var n) ? n : unitListPanel.Rows.FindIndex(r => r.Id == arg);
                        if (row < 0 || row >= unitListPanel.Rows.Count) return "no row " + arg;
                        string label = unitListPanel.Rows[row].Label;
                        UnitListPick(row);
                        return "picked " + label;
                    }
                case "filter":
                    if (Array.IndexOf(UnitList.Filters, arg) < 0) return "no filter " + arg;
                    unitFilter = arg; return "filter " + unitFilter;
                case "second-clear": twoShotSecond = null; return "second cleared";
                case "ride": ViewerRide(); return ridePlacing ? "placing a ride-along for " + (rideUnit != null ? UnitLabel(rideUnit) : "?") : "ride-along " + rideShot?.ToString(Newtonsoft.Json.Formatting.None);
                case "ride-lock": ViewerRideLock(); return rideShot != null && !ridePlacing ? "locked " + rideShot.ToString(Newtonsoft.Json.Formatting.None) : ridePlacing ? "placing" : "not locked";
                case "ride-roll": ViewerRideRoll(); return "roll " + rideRoll;
                default: return null;
            }
        }

        private JObject CamerasStatus() => new JObject
        {
            ["unit_filter"] = unitFilter,
            ["unit_list"] = unitListPanel.Open ? new JObject
            {
                ["second"] = unitListPanel.PickSecond, ["filter"] = unitListPanel.Filter, ["query"] = unitListPanel.Query, ["destroyed"] = unitListPanel.ShowDestroyed,
                ["selected"] = unitListPanel.Selected, ["rows"] = new JArray(unitListPanel.Rows.Take(20).Select(r => r.Id + " " + r.Label + " | " + r.Type + " | " + r.Side + (r.Alive ? "" : " | destroyed")))
            } : null,
            ["two_shot_target"] = twoShotTarget,
            ["two_shot_second"] = twoShotSecond,
            ["ride_placing"] = ridePlacing,
            ["ride"] = rideShot != null ? (JObject)rideShot.DeepClone() : null
        };
    }
}
