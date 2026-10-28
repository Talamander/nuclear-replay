#nullable disable
using System;
using System.Numerics;

namespace NuclearReplay.Playback
{
    // Framing maths of the ride and two_shot cine cameras (CineRide.cs, CineTwoShot.cs). Unity-free (System.Numerics),
    // so the tests run it as is. Axes as in Unity: x right, y up, z forward.
    internal static class CameraFraming
    {
        public static readonly Vector3 Forward = new Vector3(0, 0, 1);
        public static readonly Vector3 Up = new Vector3(0, 1, 0);

        // ---------- ride ----------

        // A camera pose seen from a unit: the offset (metres) and the look direction, both in the unit's own frame.
        // The viewer's R key turns the free camera's pose into a ride shot with these.
        public static void RideFromPose(Vector3 camPos, Quaternion camRot, Vector3 unitPos, Quaternion unitRot, out Vector3 offset, out Vector3 lookDir)
        {
            var inv = Quaternion.Inverse(Quaternion.Normalize(unitRot));
            offset = Vector3.Transform(camPos - unitPos, inv);
            lookDir = Vector3.Normalize(Vector3.Transform(Vector3.Transform(Forward, Quaternion.Normalize(camRot)), inv));
        }

        // Where the ride camera sits for a unit pose.
        public static Vector3 RidePosition(Vector3 unitPos, Quaternion unitRot, Vector3 offset) => unitPos + Vector3.Transform(offset, unitRot);

        // How much of the unit's roll the ride camera keeps: "unit" (1, the default) or "level" (0, horizon kept
        // level), or a number from 0 to 1 in between.
        public static float RollAmount(string roll, float fallback = 1f)
        {
            if (string.IsNullOrEmpty(roll)) return fallback;
            switch (roll.Trim().ToLowerInvariant())
            {
                case "unit": return 1f;
                case "level": return 0f;
            }
            return float.TryParse(roll, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? Math.Max(0f, Math.Min(1f, v)) : fallback;
        }

        // ---------- two_shot ----------

        internal struct TwoShotFrame
        {
            public Vector3 Camera;    // camera position
            public Vector3 Look;      // point the camera looks at
            public float Fov;         // vertical field of view, degrees
            public float Distance;    // camera distance from the subject actually used
            public bool Fits;         // both units fit inside fov_max
        }

        // Two-shot framing. The camera sits `distance` m from the subject `a`, on the subject's side of the line to the
        // second unit `b`, swung `azimuth` degrees off that line to `side` (+1 right, -1 left, seen from a looking at b)
        // and raised `elevation` times the distance. It looks between the two and picks the narrowest lens that holds
        // both (with `margin` of the frame to spare around their radii). When even fov_max is not enough the camera
        // backs off, up to distanceMax. `axis` is the direction from a to b (the caller smooths it); a zero axis
        // means there is no second unit, and `fallback` (the subject's heading) stands in for it.
        public static TwoShotFrame TwoShot(Vector3 a, Vector3 b, Vector3 axis, Vector3 fallback, float side, float distance, float azimuthDeg,
            float elevation, float radiusA, float radiusB, float aspect, float fovMin, float fovMax, float distanceMax, float margin)
        {
            bool haveB = axis.LengthSquared() > 1e-6f;
            Vector3 f = haveB ? Vector3.Normalize(axis) : (fallback.LengthSquared() > 1e-6f ? Vector3.Normalize(fallback) : Forward);
            Vector3 flat = new Vector3(f.X, 0, f.Z);
            if (flat.LengthSquared() < 1e-6f) flat = fallback.LengthSquared() > 1e-6f ? new Vector3(fallback.X, 0, fallback.Z) : Forward;
            if (flat.LengthSquared() < 1e-6f) flat = Forward;
            Vector3 right = Vector3.Normalize(Vector3.Cross(Up, Vector3.Normalize(flat)));
            float az = azimuthDeg * (float)Math.PI / 180f;
            Vector3 dir = Vector3.Normalize(-f * (float)Math.Cos(az) + right * (side >= 0 ? 1f : -1f) * (float)Math.Sin(az));
            aspect = aspect > 0.1f ? aspect : 16f / 9f;
            fovMin = Math.Max(1f, fovMin);
            fovMax = Math.Max(fovMin, Math.Min(170f, fovMax));
            distance = Math.Max(1f, distance);
            distanceMax = Math.Max(distance, distanceMax);

            var frame = new TwoShotFrame();
            for (int i = 0; i < 40; i++)
            {
                Vector3 cam = a + dir * distance + Up * (distance * elevation);
                Vector3 look;
                float need = NeededFov(cam, a, radiusA, haveB ? b : (Vector3?)null, radiusB, aspect, margin, out look);
                frame.Camera = cam;
                frame.Look = look;
                frame.Distance = distance;
                frame.Fits = need <= fovMax;
                frame.Fov = Math.Max(fovMin, Math.Min(fovMax, need));
                if (frame.Fits || distance >= distanceMax) break;
                distance = Math.Min(distanceMax, distance * 1.15f);
            }
            return frame;
        }

        // The vertical field of view that holds a (and b) seen from cam, looking between them; also that look point.
        public static float NeededFov(Vector3 cam, Vector3 a, float radiusA, Vector3? b, float radiusB, float aspect, float margin, out Vector3 look)
        {
            Vector3 ua = Dir(a - cam);
            if (b == null)
            {
                look = a;
                float d = Math.Max(0.1f, (a - cam).Length());
                return Fov(radiusA / d * (1f + margin) * 2f);
            }
            Vector3 ub = Dir(b.Value - cam);
            Vector3 l = ua + ub;
            l = l.LengthSquared() > 1e-6f ? Vector3.Normalize(l) : ua;
            // Look at the point on the bisector at the subject's distance, so smoothing in the caller moves a point.
            look = cam + l * (a - cam).Length();
            Vector3 right = Vector3.Cross(Up, l);
            right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : new Vector3(1, 0, 0);
            Vector3 up = Vector3.Normalize(Vector3.Cross(l, right));
            float tanV = 0f;
            for (int i = 0; i < 2; i++)
            {
                Vector3 v = (i == 0 ? a : b.Value) - cam;
                float r = i == 0 ? radiusA : radiusB;
                float z = Vector3.Dot(v, l);
                if (z < 0.5f) return 179f;  // behind the camera: no lens holds both
                float x = (Math.Abs(Vector3.Dot(v, right)) + r) / z;
                float y = (Math.Abs(Vector3.Dot(v, up)) + r) / z;
                tanV = Math.Max(tanV, Math.Max(y, x / aspect));
            }
            return Fov(tanV * (1f + margin) * 2f);
        }

        // Vertical field of view in degrees for a frame height of `tanTimesTwo` = 2 tan(fov/2).
        private static float Fov(float tanTimesTwo) => 2f * (float)Math.Atan(tanTimesTwo / 2f) * 180f / (float)Math.PI;

        private static Vector3 Dir(Vector3 v) => v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Forward;

        // Turns `from` toward `to` (unit vectors) with time constant `smooth` seconds and at most `maxDegPerSec`, so a
        // two-shot axis never whips around when the units pass close to each other.
        public static Vector3 TurnToward(Vector3 from, Vector3 to, float dt, float smooth, float maxDegPerSec)
        {
            if (from.LengthSquared() < 1e-6f) return to;
            if (to.LengthSquared() < 1e-6f || dt <= 0) return from;
            from = Vector3.Normalize(from); to = Vector3.Normalize(to);
            float angle = (float)Math.Acos(Math.Max(-1f, Math.Min(1f, Vector3.Dot(from, to))));
            if (angle < 1e-5f) return to;
            float k = smooth > 0 ? 1f - (float)Math.Exp(-dt / smooth) : 1f;
            float step = Math.Min(angle * k, maxDegPerSec * (float)Math.PI / 180f * dt);
            Vector3 axis = Vector3.Cross(from, to);
            if (axis.LengthSquared() < 1e-10f)
            {
                // Opposite directions: turn about any axis across them, preferring up.
                axis = Vector3.Cross(from, Up);
                if (axis.LengthSquared() < 1e-10f) axis = Vector3.Cross(from, new Vector3(1, 0, 0));
            }
            return Vector3.Normalize(Vector3.Transform(from, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), step)));
        }

        // Auto side for a two-shot: the side away from the subject's sideways motion across the line to the second
        // unit, so the subject moves away from the camera rather than into it. Chosen once per shot (no line crossing).
        public static float AutoSide(Vector3 axis, Vector3 subjectVelocity)
        {
            Vector3 flat = new Vector3(axis.X, 0, axis.Z);
            if (flat.LengthSquared() < 1e-6f) return 1f;
            Vector3 right = Vector3.Normalize(Vector3.Cross(Up, Vector3.Normalize(flat)));
            float lateral = Vector3.Dot(subjectVelocity, right);
            return lateral > 0 ? -1f : 1f;
        }
    }
}
