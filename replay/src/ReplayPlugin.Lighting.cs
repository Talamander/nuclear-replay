using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // The viewer's Lighting panel (F2). Its keys work only while the panel is open:
    //   Y              time of day: as recorded, dawn, golden hour, dusk, night
    //   PageUp / Down  time of day 15 minutes later / earlier
    //   N              haze: as recorded, clear air, light, hazy, thick
    //   V              weather: as recorded, clear, cloudy, overcast, storm
    //   Z              sun behind subject: the free camera (and the selected Camera Studio key) moves to the
    //                  backlit side of the followed aircraft, looking at it with the sun just beside it
    //   Home           everything as recorded
    //
    // The panel's settings are the viewer layer of LightingOverride (Lighting.cs). They stay through seeks, cuts and
    // camera changes until the replay closes, apply to clips (C) because a clip records what the viewer shows, and
    // are saved with the Camera Studio path (<recording>.path.json), so a path renders with its light.
    public sealed partial class ReplayPlugin
    {
        private bool lightingOpen;

        internal bool LightingOpen => lightingOpen;

        // The panel's settings (never null; empty means as recorded).
        private ShotLighting ViewerLight => LightingOverride.Viewer?.Clone() ?? new ShotLighting();

        internal void ViewerToggleLighting()
        {
            if (!viewer || server == null) return;
            lightingOpen = !lightingOpen;
            if (lightingOpen) ui.Toast("Lighting: Y time, PgUp/PgDn 15 min, N haze, V weather, Z sun behind subject, Home as recorded. F2 closes.", 5f);
        }

        private void LightingKeys()
        {
            if (Input.GetKeyDown(KeyCode.Y)) LightingTimePreset();
            if (Input.GetKeyDown(KeyCode.PageUp)) LightingTimeStep(1);
            if (Input.GetKeyDown(KeyCode.PageDown)) LightingTimeStep(-1);
            if (Input.GetKeyDown(KeyCode.N)) LightingHaze();
            if (Input.GetKeyDown(KeyCode.V)) LightingWeather();
            if (Input.GetKeyDown(KeyCode.Z)) LightingSunBehind();
            if (Input.GetKeyDown(KeyCode.Home)) LightingAsRecorded();
        }

        internal void LightingTimePreset()
        {
            var s = ViewerLight;
            s.TimeOfDay = LightingPanel.Next(LightingPanel.TimeHours, s.TimeOfDay);
            LightingSet(s, LightingPanel.TimeToast(s.TimeOfDay));
        }

        internal void LightingTimeStep(int steps)
        {
            var s = ViewerLight;
            s.TimeOfDay = LightingPanel.StepTime(s.TimeOfDay, LightingRecordedTime(), steps);
            LightingSet(s, LightingPanel.TimeToast(s.TimeOfDay));
        }

        internal void LightingHaze()
        {
            var s = ViewerLight;
            s.Fog = LightingPanel.Next(LightingPanel.HazeValues, s.Fog);
            LightingSet(s, LightingPanel.HazeToast(s.Fog));
        }

        internal void LightingWeather()
        {
            var s = ViewerLight;
            s.Weather = LightingPanel.Next(LightingPanel.WeatherValues, s.Weather);
            LightingSet(s, LightingPanel.WeatherToast(s.Weather));
        }

        internal void LightingAsRecorded()
        {
            LightingSet(null, "Lighting: as recorded");
        }

        private static float LightingRecordedTime()
        {
            float t = LightingOverride.RecordedTime();
            return float.IsNaN(t) ? 12f : t;
        }

        private void LightingSet(ShotLighting s, string toast)
        {
            LightingOverride.SetViewer(s, "viewer");
            if (toast != null) ui.Toast(toast, 2.5f);
            // The studio's path carries the light it was made with.
            if (studio.Count > 0) StudioSave();
        }

        // Sun behind subject: the free camera goes to the far side of the subject from the sun, at about the same
        // distance as now (30-400 m), looking at the subject with the sun 15 degrees to its right.
        internal void LightingSunBehind()
        {
            if (!viewer || server == null || recorder != null) return;
            var cams = CameraStateManager.i;
            if (cams == null) return;
            var unit = (studio.Open ? StudioUnit() : null) ?? director?.CurrentUnit ?? NearestAircraft(cams.transform.position);
            if (unit == null || unit.transform == null) { ui.Toast("No aircraft to put the sun behind. Follow one first ([ ] or a preset).", 3f); return; }
            if (studio.Playing) { ui.Toast("Stop the path first (Enter).", 2.5f); return; }
            Vector3 subject = unit.transform.position.ToGlobalPosition().AsVector3();
            Vector3 camPos = cams.transform.position.ToGlobalPosition().AsVector3();
            float distance = Mathf.Clamp((camPos - subject).magnitude, 30f, 400f);
            Vector3 towardSun = LightingOverride.TowardSun();
            var p = BacklightMath.BehindSubject(LightingOverride.N(subject), LightingOverride.N(towardSun), 15f, false, distance,
                CineCamera.GroundY(subject), 5f, out float yaw, out float pitch);
            // As StudioSelect: the free camera first (entering it resets its pose), then the pose.
            if (!freeCamOn) SetFreeCam(true);
            if (freeFly == null) return;
            freeFly.Position = LightingOverride.U(p);
            freeFly.Yaw = yaw;
            freeFly.Pitch = -pitch;     // Unity's x angle: positive looks down
            float elevation = LightingOverride.Elevation(towardSun);
            string msg = $"Sun behind {UnitLabel(unit)}" + (elevation < 0 ? " (the sun is down; pick a time with Y)" : elevation > 45 ? " (the sun is high; a low sun backlights better: Y)" : "");
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "Lighting: sun behind {0}: camera {1:F0} m back, yaw {2:F1}, pitch {3:F1}; {4}",
                UnitLabel(unit), distance, yaw, pitch, LightingOverride.Status()));
            if (studio.Open && studio.Selected >= 0) { StudioUpdate(); return; }
            ui.Toast(msg, 3f);
        }

        private static Unit NearestAircraft(Vector3 local)
        {
            Unit best = null;
            float bestD = float.MaxValue;
            foreach (var u in UnitRegistry.allUnits)
            {
                if (!(u is Aircraft) || u.disabled || u.transform == null) continue;
                float d = (u.transform.position - local).sqrMagnitude;
                if (d < bestD) { bestD = d; best = u; }
            }
            return best;
        }

        // The panel closes and the light goes back to the recording when the replay closes.
        private void LightingReset()
        {
            lightingOpen = false;
            LightingOverride.Reset("viewer closed");
        }

        // A shot (the saved camera path's) with the panel's lighting keys, so `nr render` of the path file renders
        // with the same light.
        private JObject LightingShot(JObject shot)
        {
            LightingOverride.Write(LightingOverride.Viewer, shot);
            return shot;
        }

        private JObject LightingStatus()
        {
            var o = new JObject
            {
                ["open"] = lightingOpen,
                ["viewer"] = LightingOverride.ToJson(LightingOverride.Viewer),
                ["effective"] = LightingOverride.ToJson(LightingOverride.Effective),
                ["shot_list"] = LightingOverride.ShotListLighting,
                ["describe"] = LightingOverride.Status()
            };
            float t = LightingOverride.RecordedTime();
            if (!float.IsNaN(t)) o["recorded_time"] = Math.Round(t, 3);
            return o;
        }

        // Test commands: light on|off|time <h|preset|recorded>|haze <x|recorded>|weather <0-1|recorded>|cloud <m|recorded>
        // |azimuth <deg|camera|recorded>|behind|recorded|next-time|next-haze|next-weather
        private string LightingCommand(string arg)
        {
            if (!viewer || server == null) return "light: no replay open";
            var parts = arg.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "on";
            string value = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "";
            bool recorded = value == "recorded" || value == "as recorded";
            float? Num() => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (float?)null;
            var s = ViewerLight;
            switch (sub)
            {
                case "on": if (!lightingOpen) ViewerToggleLighting(); break;
                case "off": lightingOpen = false; break;
                case "next-time": LightingTimePreset(); break;
                case "next-haze": LightingHaze(); break;
                case "next-weather": LightingWeather(); break;
                case "behind": LightingSunBehind(); break;
                case "recorded": LightingAsRecorded(); break;
                case "time":
                {
                    int preset = Array.FindIndex(LightingPanel.TimeNames, n => n.Equals(value, StringComparison.OrdinalIgnoreCase));
                    s.TimeOfDay = recorded ? null : preset >= 0 ? LightingPanel.TimeHours[preset] : Num().HasValue ? BacklightMath.Repeat(Num().Value, 24f) : s.TimeOfDay;
                    LightingSet(s, LightingPanel.TimeToast(s.TimeOfDay));
                    break;
                }
                case "haze": s.Fog = recorded ? null : Num().HasValue ? Math.Max(0f, Num().Value) : s.Fog; LightingSet(s, LightingPanel.HazeToast(s.Fog)); break;
                case "weather": s.Weather = recorded ? null : Num().HasValue ? Mathf.Clamp01(Num().Value) : s.Weather; LightingSet(s, LightingPanel.WeatherToast(s.Weather)); break;
                case "cloud": s.CloudHeight = recorded ? null : Num() ?? s.CloudHeight; LightingSet(s, null); break;
                case "azimuth":
                    if (recorded) { s.SunAzimuth = null; s.SunCamera = false; }
                    else if (value == "camera") { s.SunCamera = true; s.SunAzimuth = null; }
                    else if (Num().HasValue) { s.SunCamera = false; s.SunAzimuth = BacklightMath.Repeat(Num().Value, 360f); }
                    LightingSet(s, null);
                    break;
                default: return "light: unknown " + sub;
            }
            return "light " + LightingOverride.Status();
        }
    }
}
