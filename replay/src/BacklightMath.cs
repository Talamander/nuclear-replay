using System;
using System.Numerics;

namespace NuclearReplay.Playback
{
    // Pure geometry for sun-aware camera placement (CineBacklight.cs, Lighting.cs and the viewer's "sun behind
    // subject"). Unity-free (System.Numerics), so the tests run it as is. Azimuth is compass style about +y:
    // 0 = +z, 90 = +x; elevation is degrees above the horizontal.
    internal static class BacklightMath
    {
        private const float Deg = 180f / MathF.PI;

        internal static float Repeat(float v, float length) => v - MathF.Floor(v / length) * length;

        // Signed shortest difference b - a in degrees (-180 to 180).
        internal static float DeltaAngle(float a, float b)
        {
            float d = Repeat(b - a, 360f);
            return d > 180f ? d - 360f : d;
        }

        internal static float Azimuth(Vector3 v) => Repeat(MathF.Atan2(v.X, v.Z) * Deg, 360f);
        internal static float Elevation(Vector3 v) => MathF.Atan2(v.Y, MathF.Sqrt(v.X * v.X + v.Z * v.Z)) * Deg;

        internal static Vector3 FromAngles(float azimuth, float elevation)
        {
            float ar = azimuth / Deg, er = elevation / Deg;
            return new Vector3(MathF.Sin(ar) * MathF.Cos(er), MathF.Sin(er), MathF.Cos(ar) * MathF.Cos(er));
        }

        // The camera's view direction (subject seen from the camera) for a backlit shot: the direction toward the
        // sun moved `off` degrees sideways (so the sun shows `off` degrees right of the subject, or left when
        // `left`) and `pitch` degrees down (sun higher in frame). Looking along azimuth a, screen right is a + 90,
        // so the sun appears right of the subject when the view azimuth is smaller than the sun's.
        internal static Vector3 ViewDir(Vector3 towardSun, float off, bool left, float pitch)
        {
            float az = Azimuth(towardSun), el = Elevation(towardSun);
            float cos = MathF.Max(0.2f, MathF.Cos(el / Deg));   // sideways angle at elevation el
            az += (left ? 1f : -1f) * off / cos;
            return FromAngles(az, Math.Clamp(el - pitch, -89f, 89f));
        }

        // Free camera pose for "sun behind subject": `distance` m back from the subject along the backlit view
        // direction, never below `minHeight` m above `groundY`. Returns the position, yaw (compass degrees) and pitch
        // (degrees, positive looks up) that put the subject in the middle of the frame.
        internal static Vector3 BehindSubject(Vector3 subject, Vector3 towardSun, float off, bool left, float distance,
            float groundY, float minHeight, out float yaw, out float pitch)
        {
            Vector3 d = ViewDir(towardSun, off, left, 0f);
            Vector3 p = subject - d * distance;
            if (p.Y < groundY + minHeight) p.Y = groundY + minHeight;
            Vector3 look = subject - p;
            yaw = Azimuth(look);
            pitch = Elevation(look);
            return p;
        }

        // Ground camera on the sun line: march out from the subject (horizontal distance t, the view line falls
        // tan(elevation) per metre) and return the first t where the line meets the ground plus `height`. Rough
        // terrain made a fixed-point search (distance = rise / tan(elevation), re-read the ground, repeat) swing
        // between hill and valley and miss the line; the march always lands on it, and the first hit also keeps
        // the line of sight to the subject clear of terrain.
        // `groundAt(t)` is the ground height at distance t. Returns false (and `max`) when the line never comes
        // down to the ground within `max`, e.g. with the sun on the horizon.
        internal static bool GroundOnLine(Func<float, float> groundAt, float subjectY, float elevation, float height,
            float min, float max, out float t)
        {
            t = max;
            if (elevation <= 0.5f) return false;
            float slope = MathF.Tan(elevation / Deg);
            float Gap(float d) => subjectY - d * slope - (groundAt(d) + height);   // > 0: line above the ground
            if (Gap(min) <= 0f) { t = min; return true; }
            float step = MathF.Max(5f, (max - min) / 800f), a = min;
            for (float b = min + step; ; b += step)
            {
                if (b > max) b = max;
                if (Gap(b) <= 0f)
                {
                    for (int i = 0; i < 12; i++) { float m = 0.5f * (a + b); if (Gap(m) > 0f) a = m; else b = m; }
                    t = b;
                    return true;
                }
                if (b >= max) return false;
                a = b;
            }
        }
    }
}
