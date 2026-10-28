using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Backlight placement for cine cameras (opt-in with "backlight": true). The camera is put on the far side of
    // the subject from the sun, so the camera looks toward the sun with the subject in between: silhouettes and
    // rim light. The sun's path is fixed per map, so backlight places the camera rather than moving the sun. The
    // sun is read from the game's sun light (LevelInfo.sun) after any lighting override of the same shot
    // (Lighting.cs), so time_of_day / sun_azimuth and backlight combine.
    //   backlight         true to enable
    //   backlight_off     angle in degrees between the sun and the subject as seen from the camera (default 15), so
    //                     the sun sits toward the frame edge instead of dead centre (a 15 deg offset is inside a
    //                     30 deg lens, outside a 15 deg one)
    //   backlight_side    "right" (default) or "left": which side of the subject the sun appears on
    //   backlight_pitch   degrees; positive puts the sun higher in frame than the subject (camera lower)
    //   backlight_at      track_from: seconds ahead at which the subject should cross the sun line (default 2)
    //   backlight_height  chase_cine: metres added straight up to the sun-locked offset
    //   backlight_min / backlight_max  metres, bounds of the ground search along the sun line (default 60 / 8000)
    // Per mode:
    //   track_from  with "from": "backlight" (or no "from"): a fixed vantage `distance` m (default 1200) from where
    //               the subject will be after `backlight_at` s. With `agl`, the camera stands that high above the
    //               ground instead and is moved along the sun line until the subject is at the sun's elevation (the
    //               ground silhouette shot).
    //   flyby       the spot is `offset` m from the pass point toward the anti-sun side (agl works as above);
    //               `side` is ignored.
    //   chase_cine  sun-locked chase: the offset is `distance` m on the anti-sun side, whatever the heading;
    //               `drift`/`azimuth` still turn it about the vertical; `look_smooth` defaults to 0.05 s (the camera
    //               is often beside the jet, where look lag shows).
    //   keyframe    relative keys use a sun frame instead of the heading frame: +z (forward) points toward the
    //               sun, so a key at [0, 0, -100] is 100 m on the anti-sun side looking into the sun.
    // Not for mount, action or path (use sun_azimuth "camera" there instead).
    internal sealed partial class CineCamera
    {
        private bool backlight;

        private void BacklightBegin()
        {
            backlight = B("backlight", false) && (mode == "chase_cine" || mode == "flyby" || mode == "track_from" || mode == "keyframe");
            if (!backlight) return;
            // A sun-locked chase often sits beside the jet, so look lag shows as sideways drift (a 0.25 s look lag
            // put the jet out of frame in a turn at 35 m); keep the look tight unless the shot sets look_smooth.
            if (mode == "chase_cine" && raw["look_smooth"] == null) lookSmooth = 0.05f;
            Vector3 s = LightingOverride.TowardSun();
            log.LogInfo(string.Format(CultureInfo.InvariantCulture, "Cine backlight: sun azimuth {0:F1} elevation {1:F1}, off {2:F0} deg {3}, pitch {4:F0}",
                LightingOverride.Azimuth(s), LightingOverride.Elevation(s), F("backlight_off", 15f), Side(), F("backlight_pitch", 0f)));
        }

        private string Side() => ((string)raw["backlight_side"] ?? "right").ToLowerInvariant();

        // The camera's view direction: toward the sun, `backlight_off` sideways and `backlight_pitch` down.
        private Vector3 BacklightDir() => LightingOverride.U(BacklightMath.ViewDir(LightingOverride.N(LightingOverride.TowardSun()),
            F("backlight_off", 15f), Side() == "left", F("backlight_pitch", 0f)));

        // Camera spot for a subject at `subject` (global). Free-air: `dist` m back along the view line. With agl (or
        // when the free-air spot is under the terrain): on the ground, slid along the view azimuth until the subject
        // sits at the view elevation.
        private Vector3 BacklightSpot(Vector3 subject, float dist, float? agl)
        {
            Vector3 d = BacklightDir();
            Vector3 p = subject - d * dist;
            if (!agl.HasValue && p.y >= GroundY(p) + minAgl) return p;
            float h = agl ?? minAgl;
            Vector3 flat = new Vector3(d.x, 0, d.z);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            float el = LightingOverride.Elevation(d);
            float lo = F("backlight_min", 60f), hi = F("backlight_max", 8000f);
            // The first point out along the sun line that meets the ground (+ h). If the line never comes down
            // (sun on the horizon, or the subject too high for backlight_max), use `dist` and accept a steeper view.
            bool onLine = BacklightMath.GroundOnLine(t => GroundY(subject - flat * t), subject.y, el, h, lo, hi, out float hd);
            if (!onLine) hd = Mathf.Clamp(dist, lo, hi);
            p = subject - flat * hd;
            p.y = GroundY(p) + h;
            Vector3 view = subject - p;
            log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "Cine backlight ground spot {0} m out, {1:F1} m above ground; view elevation {2:F1} (wanted {3:F1}){4}",
                hd.ToString("F0", CultureInfo.InvariantCulture), h, LightingOverride.Elevation(view), el,
                onLine ? "" : "; the sun line does not reach the ground within backlight_max, used distance"));
            return p;
        }

        // track_from "from": "backlight" (or no "from" at all).
        private bool BacklightTrackFrom()
        {
            if (!backlight) return false;
            var from = raw["from"];
            if (from != null && !(from.Type == JTokenType.String && (string)from == "backlight")) return false;
            Vector3 subject = targetPos + targetVel * F("backlight_at", 2f);
            anchor = BacklightSpot(subject, F("distance", 1200f), raw["agl"] != null ? F("agl", 2f) : (float?)null);
            anchor.y = Mathf.Max(anchor.y, GroundY(anchor) + Mathf.Min(minAgl, raw["agl"] != null ? F("agl", 2f) : minAgl));
            return true;
        }

        private bool BacklightFlyby(Vector3 pass)
        {
            if (!backlight) return false;
            anchor = BacklightSpot(pass, F("offset", 80f), raw["agl"] != null ? F("agl", 2f) : (float?)null);
            return true;
        }

        private Vector3 BacklightChaseOffset()
        {
            Vector3 o = -BacklightDir() * F("distance", 35f);
            return Quaternion.AngleAxis(driftDeg, Vector3.up) * o + Vector3.up * F("backlight_height", 0f);
        }

        // Sun frame for keyframe: forward toward the (offset) sun, flattened, horizon level.
        private void BacklightFrame(out Vector3 fwd, out Vector3 right, out Vector3 up)
        {
            fwd = Flat(BacklightDir(), 1f);
            right = Vector3.Cross(Vector3.up, fwd).normalized;
            up = Vector3.up;
        }
    }
}
