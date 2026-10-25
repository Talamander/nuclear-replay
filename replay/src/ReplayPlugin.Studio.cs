using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Camera Studio (P in the viewer): lay down a camera path with the free camera and play or clip it.
    //
    //   P        open / close the studio (opens the free camera)
    //   K        add a key at the free camera's pose and the replay's time (after the selected key)
    //   U        move the selected key to the free camera's pose (its time stays)
    //   X / Del  delete the selected key
    //   [ / ]    select the previous / next key and put the free camera on it
    //   , / .    move the selected key (and every later key) 0.5 s earlier / later
    //   T L J M  timing (even / keys), aim (keys / target / path), shake, space (world / target)
    //   Enter    play the path from just before its first key (Enter again stops)
    //   C        clip the path
    //
    // The model is CameraStudio.cs; the path plays as a "path" cine shot (CinePath.cs). Every change is saved to
    // <recording>.path.json, which holds the shot (so -ncShots and nr can use the file as a shot list) and the
    // studio's own keys, and is loaded again the next time the studio opens on that recording.
    public sealed partial class ReplayPlugin
    {
        private readonly CameraStudio studio = new CameraStudio();
        private bool studioClip;            // the clip running now was started from the studio
        private string studioLoadedFor;     // the recording whose path file has been read

        internal bool StudioOpen => studio.Open;

        internal void ViewerToggleStudio()
        {
            if (!viewer || server == null || recorder != null) return;
            if (studio.Open) { StudioClose(); return; }
            string path = viewerDemo?.Path;
            if (path != null && studioLoadedFor != path && studio.Count == 0) StudioLoad(path);
            studioLoadedFor = path;
            if (studio.Count == 0 || studio.UnitId == null)
            {
                var unit = director?.CurrentUnit;
                studio.UnitId = unit != null ? unit.persistentID.ToString() : null;
                studio.UnitName = unit != null ? UnitLabel(unit) : null;
                studio.Version++;
            }
            studio.Open = true;
            studio.Playing = false;
            SetFreeCam(true);
            ui.Toast(studio.Count == 0
                ? "Camera Studio: fly the free camera and press K to add a key. Enter plays the path, P closes."
                : $"Camera Studio: {studio.Count} keys loaded. [ ] select, U update, K add, Enter plays, P closes.", 5f);
        }

        private void StudioClose()
        {
            studio.Open = false;
            if (studio.Playing) StudioStop();
            ui.Toast("Camera Studio closed. P opens it again; the path is kept.", 3f);
        }

        // Called by the camera actions that leave the studio's camera (presets, aircraft, F).
        private void StudioLeave()
        {
            if (!studio.Open && !studio.Playing) return;
            studio.Open = false;
            studio.Playing = false;
            studioClip = false;
        }

        // Studio keys; ViewerKeys calls this while the studio is open.
        private void StudioKeys()
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) StudioPlayStop();
            if (studio.Playing) return;
            if (Input.GetKeyDown(KeyCode.K)) StudioAdd();
            if (Input.GetKeyDown(KeyCode.U)) StudioUpdate();
            if (Input.GetKeyDown(KeyCode.X) || Input.GetKeyDown(KeyCode.Delete)) StudioDelete();
            if (Input.GetKeyDown(KeyCode.LeftBracket)) StudioSelect(-1);
            if (Input.GetKeyDown(KeyCode.RightBracket)) StudioSelect(1);
            if (Input.GetKeyDown(KeyCode.Comma)) StudioRetime(-0.5);
            if (Input.GetKeyDown(KeyCode.Period)) StudioRetime(0.5);
            if (Input.GetKeyDown(KeyCode.T)) { studio.ToggleTiming(); StudioChanged("Timing: " + (studio.KeyTiming ? "reach every key at its time" : "even speed")); }
            if (Input.GetKeyDown(KeyCode.L)) { studio.NextAim(); StudioChanged("Aim: " + StudioAimText()); }
            if (Input.GetKeyDown(KeyCode.J)) { studio.NextShake(); StudioChanged("Shake: " + studio.ShakeName); }
            if (Input.GetKeyDown(KeyCode.M)) StudioToggleSpace();
            if (Input.GetKeyDown(KeyCode.C)) StudioClipPath();
        }

        private string StudioAimText() => studio.Aim == "target" ? "look at " + (studio.UnitName ?? "the aircraft")
            : studio.Aim == "path" ? "look along the path" : "the keys' own view";

        internal void StudioAdd()
        {
            if (!StudioCanEdit()) return;
            var key = StudioPose();
            if (key == null) return;
            int at = studio.Add(key, server.Clock - server.StartTime);
            StudioChanged($"Key {at + 1} added at {CameraStudio.Clock(key.Rt)}" + (studio.Count == 1 ? ". Move on in the replay (or not) and add the next." : ""));
        }

        internal void StudioUpdate()
        {
            if (!StudioCanEdit()) return;
            if (studio.Selected < 0) { ui.Toast("No key selected. [ ] select a key.", 3f); return; }
            bool wasTarget = studio.TargetSpace;
            var key = StudioPose();
            if (key == null || !studio.Update(key)) return;
            StudioChanged($"Key {studio.Selected + 1} updated" + (wasTarget && !studio.TargetSpace ? "; the aircraft is gone, so the path is back in world space" : ""));
        }

        internal void StudioDelete()
        {
            if (studio.Playing || studio.Selected < 0) return;
            int number = studio.Selected + 1;
            if (studio.Delete()) StudioChanged($"Key {number} deleted");
        }

        private void StudioSelect(int step)
        {
            var key = studio.Select(step);
            if (key == null) return;
            var cam = CameraStateManager.i;
            if (!freeCamOn) SetFreeCam(true);
            if (freeFly != null && cam != null)
            {
                freeFly.Position = new Vector3(key.World.X, key.World.Y, key.World.Z);
                freeFly.Yaw = key.Yaw;
                freeFly.Pitch = key.Pitch;
                freeFly.SetFov(cam, key.Fov);
            }
            studio.Version++;
            ui.Toast($"Key {studio.Selected + 1} of {studio.Count} at {CameraStudio.Clock(key.Rt)}", 2f);
        }

        private void StudioRetime(double seconds)
        {
            if (studio.Retime(seconds)) StudioChanged($"Key {studio.Selected + 1} at {CameraStudio.Clock(studio.Keys[studio.Selected].Rt)}");
        }

        private void StudioToggleSpace()
        {
            if (studio.ToggleSpace()) StudioChanged(studio.TargetSpace ? "Space: moves with " + (studio.UnitName ?? "the aircraft") : "Space: world (fixed in place)");
            else ui.Toast(studio.UnitId == null ? "Target space needs an aircraft: follow one before opening the studio."
                : "Target space needs the aircraft alive at every key; update the keys made without it (U).", 4f);
        }

        private bool StudioCanEdit()
        {
            if (!studio.Open || studio.Playing || server == null) return false;
            if (startSeeking) { ui.Toast("Wait for the jump to finish.", 2f); return false; }
            if (!freeCamOn) { SetFreeCam(true); ui.Toast("Fly the free camera to the spot, then press the key again.", 3f); return false; }
            return true;
        }

        // The free camera's pose now, with its pose relative to the studio's aircraft when it is alive.
        private CameraStudio.Key StudioPose()
        {
            var cam = CameraStateManager.i;
            if (cam == null || freeFly == null) return null;
            var p = freeFly.Position;
            var key = new CameraStudio.Key
            {
                World = new System.Numerics.Vector3(p.x, p.y, p.z), Yaw = freeFly.Yaw, Pitch = freeFly.Pitch, Fov = freeFly.CurrentFov(cam)
            };
            var unit = StudioUnit();
            if (unit != null)
            {
                // The aircraft's level heading, as the path shot's target space reads it (CinePath.StepPath).
                Vector3 at = unit.transform.position.ToGlobalPosition().AsVector3();
                Vector3 v = unit.rb != null ? unit.rb.velocity : Vector3.zero;
                Vector3 forward = new Vector3(v.x, 0, v.z).sqrMagnitude > 4f ? v : unit.transform.forward;
                float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                key.HasRel = true;
                key.Rel = CameraStudio.ToRelative(key.World, new System.Numerics.Vector3(at.x, at.y, at.z), heading);
                key.RelYaw = CameraStudio.WrapYaw(freeFly.Yaw - heading);
            }
            return key;
        }

        private Unit StudioUnit()
        {
            if (studio.UnitId == null) return null;
            foreach (var u in UnitRegistry.allUnits)
                if (u != null && !u.disabled && u.transform != null && u.persistentID.ToString() == studio.UnitId) return u;
            return null;
        }

        private void StudioChanged(string message)
        {
            if (message != null) ui.Toast(message, 2.5f);
            StudioSave();
        }

        // ---------------- play, stop and clip ----------------

        internal void StudioPlay()
        {
            if (!viewer || server == null || recorder != null) return;
            if (studio.Count == 0) { ui.Toast("No keys yet. Fly the free camera and press K.", 3f); return; }
            studio.Playing = true;
            if (viewerPaused) ViewerTogglePause();
            SetFreeCam(false);   // ApplyPreset plays the path shot while studio.Playing
            ViewerSeek(Math.Max(0, studio.ShotTime));
            ui.Toast("Playing the camera path. Enter stops.", 2.5f);
        }

        internal void StudioStop()
        {
            studio.Playing = false;
            studioClip = false;
            if (freeFly != null) freeFly.KeepPose = true;
            if (server != null) SetFreeCam(true);
        }

        internal void StudioPlayStop()
        {
            if (studio.Playing) StudioStop(); else StudioPlay();
        }

        internal void StudioClipPath()
        {
            if (studio.Count == 0 || server == null || recorder != null) return;
            clipIn = Math.Max(0, studio.ShotTime);
            clipOut = studio.End + 0.5;
            studio.Playing = true;
            studioClip = true;
            SetFreeCam(false);
            ViewerRecordClip();
        }

        // Every frame: the studio goes back to the free camera once the path (or its clip) is done.
        private void StudioTick()
        {
            if (!studio.Playing || server == null || recorder != null || startSeeking || viewerRestarting) return;
            double now = server.Clock - server.StartTime;
            if (!studioClip && now <= studio.End + 1) return;
            if (studio.Open) StudioStop();
            else { studio.Playing = false; studioClip = false; }
        }

        // ---------------- the path shot and its file ----------------

        private JObject StudioShot()
        {
            studio.ShakeAmounts(out float handheld, out float rumble, out float buffet);
            var keys = new JArray();
            foreach (var k in studio.ShotKeys())
                keys.Add(new JObject
                {
                    ["t"] = k.T, ["pos"] = new JArray(R(k.Pos.X), R(k.Pos.Y), R(k.Pos.Z)),
                    ["yaw"] = R(k.Yaw), ["pitch"] = R(k.Pitch), ["roll"] = 0, ["fov"] = R(k.Fov)
                });
            var shot = new JObject
            {
                ["t"] = Math.Round(studio.ShotTime, 3), ["camera"] = "path", ["target"] = studio.ShotTarget,
                // Long enough that the director never cuts away while the path runs, even when its aircraft dies.
                ["hold"] = Math.Round(studio.End - studio.ShotTime + 30, 3),
                ["space"] = studio.TargetSpace ? "target" : "world", ["timing"] = studio.KeyTiming ? "keys" : "even",
                ["aim"] = studio.Aim, ["ease"] = studio.Ease, ["keys"] = keys
            };
            if (handheld > 0) shot["handheld"] = handheld;
            if (rumble > 0) shot["rumble"] = rumble;
            if (buffet > 0) shot["buffet"] = buffet;
            return shot;
        }

        private static double R(float v) => Math.Round(v, 3);

        private void StudioSave()
        {
            if (viewerDemo?.Path == null) return;
            string path = ViewerText.StudioPathFile(viewerDemo.Path);
            try
            {
                if (studio.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
                var keys = new JArray();
                foreach (var k in studio.Keys)
                {
                    var o = new JObject
                    {
                        ["rt"] = Math.Round(k.Rt, 3), ["world"] = new JArray(R(k.World.X), R(k.World.Y), R(k.World.Z)),
                        ["yaw"] = R(k.Yaw), ["pitch"] = R(k.Pitch), ["fov"] = R(k.Fov)
                    };
                    if (k.HasRel) { o["rel"] = new JArray(R(k.Rel.X), R(k.Rel.Y), R(k.Rel.Z)); o["rel_yaw"] = R(k.RelYaw); }
                    keys.Add(o);
                }
                var root = new JObject
                {
                    ["version"] = 1, ["source"] = "camera studio", ["demo"] = viewerDemo.Path,
                    ["shots"] = new JArray(StudioShot()),
                    ["studio"] = new JObject
                    {
                        ["keys"] = keys, ["timing"] = studio.KeyTiming ? "keys" : "even", ["aim"] = studio.Aim,
                        ["space"] = studio.TargetSpace ? "target" : "world", ["shake"] = studio.ShakeName, ["ease"] = studio.Ease,
                        ["unit_id"] = studio.UnitId, ["unit_name"] = studio.UnitName
                    }
                };
                File.WriteAllText(path + ".tmp", root.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e) { Logger.LogWarning("Camera Studio: could not save " + path + ": " + e.Message); }
        }

        private void StudioLoad(string demoPath)
        {
            string path = ViewerText.StudioPathFile(demoPath);
            if (!File.Exists(path)) return;
            try
            {
                var s = JObject.Parse(File.ReadAllText(path))["studio"] as JObject;
                if (s == null) return;
                studio.Clear();
                foreach (var item in s["keys"] as JArray ?? new JArray())
                {
                    var key = new CameraStudio.Key
                    {
                        Rt = (double?)item["rt"] ?? 0, World = N3(item["world"]),
                        Yaw = (float?)item["yaw"] ?? 0, Pitch = (float?)item["pitch"] ?? 0, Fov = (float?)item["fov"] ?? 50
                    };
                    if (item["rel"] is JArray) { key.HasRel = true; key.Rel = N3(item["rel"]); key.RelYaw = (float?)item["rel_yaw"] ?? 0; }
                    studio.Keys.Add(key);
                }
                studio.Keys.Sort((a, b) => a.Rt.CompareTo(b.Rt));
                studio.KeyTiming = (string)s["timing"] == "keys";
                studio.Aim = Array.IndexOf(CameraStudio.Aims, (string)s["aim"]) >= 0 ? (string)s["aim"] : "keys";
                studio.ShakeIndex = Math.Max(0, Array.IndexOf(CameraStudio.ShakeNames, (string)s["shake"]));
                studio.Ease = (float?)s["ease"] ?? 0.5f;
                studio.UnitId = (string)s["unit_id"];
                studio.UnitName = (string)s["unit_name"];
                studio.TargetSpace = (string)s["space"] == "target" && studio.AllRelative && studio.UnitId != null;
                studio.Selected = studio.Count > 0 ? 0 : -1;
                studio.Version++;
                Logger.LogInfo($"Camera Studio: loaded {studio.Count} keys from {path}");
            }
            catch (Exception e) { Logger.LogWarning("Camera Studio: could not read " + path + ": " + e.Message); }
        }

        private static System.Numerics.Vector3 N3(JToken t) =>
            t is JArray a && a.Count == 3 ? new System.Numerics.Vector3((float)a[0], (float)a[1], (float)a[2]) : System.Numerics.Vector3.Zero;

        // A new recording starts with an empty studio.
        private void StudioReset()
        {
            studio.Clear();
            studio.Open = false;
            studio.UnitId = studio.UnitName = null;
            studio.TargetSpace = false;
            studioClip = false;
            studioLoadedFor = null;
        }

        // ---------------- test commands and status ----------------

        // studio on|off|key|update|delete|select <n>|play|stop|timing|aim|shake|space|retime <seconds>|clip|clear
        private string StudioCommand(string arg)
        {
            var parts = arg.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "on";
            string value = parts.Length > 1 ? parts[1].Trim() : "";
            switch (sub)
            {
                case "on": if (!studio.Open) ViewerToggleStudio(); break;
                case "off": if (studio.Open) StudioClose(); break;
                case "key": StudioAdd(); break;
                case "update": StudioUpdate(); break;
                case "delete": StudioDelete(); break;
                case "select":
                    {
                        int n = int.TryParse(value, out var v) ? v : 1;
                        studio.Selected = -1;
                        StudioSelect(1);
                        if (n > 1) StudioSelect(n - 1);
                        break;
                    }
                case "play": if (!studio.Playing) StudioPlay(); break;
                case "stop": if (studio.Playing) StudioStop(); break;
                case "timing": studio.ToggleTiming(); StudioChanged(null); break;
                case "aim": studio.NextAim(); StudioChanged(null); break;
                case "shake": studio.NextShake(); StudioChanged(null); break;
                case "space": StudioToggleSpace(); break;
                case "retime": StudioRetime(Num(value, 0)); break;
                case "clip": StudioClipPath(); break;
                case "clear": studio.Clear(); StudioChanged(null); break;
                default: return "unknown studio command " + sub;
            }
            return string.Format(CultureInfo.InvariantCulture, "studio {0}, {1} keys, selected {2}, {3}", studio.Open ? "open" : "closed", studio.Count, studio.Selected + 1,
                studio.Playing ? "playing" : "stopped");
        }

        private JObject StudioStatus() => new JObject
        {
            ["open"] = studio.Open, ["playing"] = studio.Playing, ["keys"] = studio.Count, ["selected"] = studio.Selected,
            ["start"] = Math.Round(studio.Start, 3), ["end"] = Math.Round(studio.End, 3), ["shot_time"] = Math.Round(studio.ShotTime, 3),
            ["aim"] = studio.Aim, ["timing"] = studio.KeyTiming ? "keys" : "even", ["space"] = studio.TargetSpace ? "target" : "world",
            ["shake"] = studio.ShakeName, ["unit"] = studio.UnitId, ["file"] = viewerDemo?.Path != null ? ViewerText.StudioPathFile(viewerDemo.Path) : null
        };
    }
}
