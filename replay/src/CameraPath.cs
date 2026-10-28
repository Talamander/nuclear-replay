using System;
using System.Collections.Generic;
using System.Numerics;

namespace NuclearReplay.Playback
{
    // Camera path maths for the "path" cine camera and the viewer's Camera Studio. Unity-free (System.Numerics), so the
    // tests run it as is.
    //
    // Position: a centripetal Catmull-Rom spline through the keys. Unlike the uniform form it never loops or overshoots
    // at a tight corner or between keys that are close together.
    // Rotation: squad, a smooth quaternion spline through the keys' rotations.
    // Lens: a monotone cubic through the keys' field of view in log space, so a zoom never overshoots a key.
    // Focus (keyed focus pulls): eased from key to key in dioptres (1 / distance); a key may focus on the target instead.
    //
    // Timing: the path is measured in progress, which is metres travelled plus MetresPerRadian for every radian turned
    // and FovMetres for every unit of log field of view, so a pan or a zoom on the spot still takes time.
    //   even  constant progress from the first key's time to the last key's, eased in and out by `ease` (0 to 1).
    //   keys  reaches every key at its own time, with the speed changing smoothly through the keys (a monotone cubic
    //         through time and progress). `ease` slows the start and the end.
    // Before the first key the camera holds on it, and after the last key it holds on that one.
    internal sealed class CameraPath
    {
        internal sealed class Key
        {
            public double T;
            public Vector3 Pos;
            public Quaternion Rot = Quaternion.Identity;
            public float Fov = 50f;
            public float Focus = -1f;    // focus distance in metres; -1 = on the target (or not keyed)
            public float FStop;          // aperture; 0 = the shot's
        }

        public const float MetresPerRadian = 20f;
        public const float FovMetres = 10f;
        private const int Samples = 48;   // per segment, for the progress table

        private readonly Key[] keys;
        private readonly double[] times;
        private readonly bool keyTiming;
        private readonly double ease;
        private readonly Quaternion[] rots, ctrl;
        private readonly double[] logFov, fovSlope;
        private readonly double[] progress;      // cumulative progress at each table sample
        private readonly Vector3[] samplePos;
        private readonly double[] keyProgress, timeSlope;

        public int Count => keys.Length;
        public double Start => times[0];
        public double End => times[times.Length - 1];
        public double Length => progress[progress.Length - 1];
        public IReadOnlyList<Vector3> Polyline => samplePos;

        public CameraPath(IList<Key> source, bool keyTiming, double ease)
        {
            if (source == null || source.Count == 0) throw new ArgumentException("a camera path needs at least one key");
            keys = new Key[source.Count];
            source.CopyTo(keys, 0);
            Array.Sort(keys, (a, b) => a.T.CompareTo(b.T));
            this.keyTiming = keyTiming;
            this.ease = Math.Max(0, Math.Min(1, ease));
            int n = keys.Length;

            // Strictly increasing times (keys at the same time are spread by a millisecond).
            times = new double[n];
            for (int i = 0; i < n; i++) times[i] = i == 0 ? keys[0].T : Math.Max(keys[i].T, times[i - 1] + 1e-3);

            // Rotations on one hemisphere, so each segment turns the short way, and squad's inner control points.
            rots = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                var q = Quaternion.Normalize(keys[i].Rot);
                if (i > 0 && Quaternion.Dot(rots[i - 1], q) < 0) q = Negate(q);
                rots[i] = q;
            }
            ctrl = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                if (i == 0 || i == n - 1) { ctrl[i] = rots[i]; continue; }
                var inv = Quaternion.Conjugate(rots[i]);
                var sum = Log(inv * rots[i + 1]) + Log(inv * rots[i - 1]);
                ctrl[i] = Quaternion.Normalize(rots[i] * Exp(Scale(sum, -0.25f)));
            }

            logFov = new double[n];
            for (int i = 0; i < n; i++) logFov[i] = Math.Log(Math.Max(1f, Math.Min(170f, keys[i].Fov)));
            var index = new double[n];
            for (int i = 0; i < n; i++) index[i] = i;
            fovSlope = MonotoneSlopes(index, logFov, 1);

            // Progress table.
            int count = n == 1 ? 1 : (n - 1) * Samples + 1;
            progress = new double[count];
            samplePos = new Vector3[count];
            samplePos[0] = PosAt(0);
            Quaternion prevRot = RotAt(0);
            double prevFov = FovLogAt(0);
            for (int j = 1; j < count; j++)
            {
                double u = (double)j / Samples;
                samplePos[j] = PosAt(u);
                var rot = RotAt(u);
                double fov = FovLogAt(u);
                progress[j] = progress[j - 1] + Vector3.Distance(samplePos[j], samplePos[j - 1])
                    + MetresPerRadian * Angle(prevRot, rot) + FovMetres * Math.Abs(fov - prevFov);
                prevRot = rot; prevFov = fov;
            }
            keyProgress = new double[n];
            for (int i = 0; i < n; i++) keyProgress[i] = progress[Math.Min(count - 1, i * Samples)];
            timeSlope = MonotoneSlopes(times, keyProgress, 1 - this.ease);
        }

        // Pose at time t (the same clock as the keys' times).
        public void Evaluate(double t, out Vector3 pos, out Quaternion rot, out float fov)
        {
            double u = ParamAt(t);
            pos = PosAt(u);
            rot = RotAt(u);
            fov = (float)Math.Exp(FovLogAt(u));
        }

        // True when any key sets its own focus distance or aperture (a keyed focus pull).
        public bool HasLensKeys => Array.Exists(keys, k => k.Focus > 0 || k.FStop > 0);

        // Focus distance and aperture at time t. Keys on the target use `targetDistance`, keys without an aperture
        // use `shotFStop`. Between two keys the focus moves in dioptres, eased in and out at each key, at the same
        // progress as the camera.
        public void Lens(double t, double targetDistance, double shotFStop, out double focus, out double fStop)
        {
            double KeyFocus(int i) => keys[i].Focus > 0 ? keys[i].Focus : targetDistance;
            double KeyStop(int i) => keys[i].FStop > 0 ? keys[i].FStop : shotFStop;
            if (keys.Length == 1) { focus = KeyFocus(0); fStop = KeyStop(0); return; }
            SegmentAt(ParamAt(t), out int a, out double s);
            double e = s * s * (3 - 2 * s);
            focus = LensMath.FocusBlend(KeyFocus(a), KeyFocus(a + 1), e);
            fStop = Math.Exp(Math.Log(KeyStop(a)) + (Math.Log(KeyStop(a + 1)) - Math.Log(KeyStop(a))) * e);
        }

        // Unit direction of travel at time t (zero when the camera does not move there).
        public Vector3 Direction(double t)
        {
            double u = ParamAt(t);
            double du = 0.02;
            Vector3 d = PosAt(Math.Min(keys.Length - 1, u + du)) - PosAt(Math.Max(0, u - du));
            return d.LengthSquared() > 1e-8f ? Vector3.Normalize(d) : Vector3.Zero;
        }

        // Curve parameter (0 at the first key, 1 at the second, ...) at time t.
        public double ParamAt(double t)
        {
            int n = keys.Length;
            if (n == 1 || t <= times[0]) return 0;
            if (t >= times[n - 1]) return n - 1;
            double total = Length;
            if (total < 1e-6)
            {
                // Every key is the same pose: progress is meaningless, so go by time.
                int k = Segment(times, t);
                return k + (t - times[k]) / (times[k + 1] - times[k]);
            }
            double p;
            if (keyTiming)
            {
                int k = Segment(times, t);
                p = Hermite(times[k], times[k + 1], keyProgress[k], keyProgress[k + 1], timeSlope[k], timeSlope[k + 1], t);
            }
            else
            {
                double x = (t - times[0]) / (times[n - 1] - times[0]);
                double smooth = x * x * x * (x * (x * 6 - 15) + 10);   // smootherstep
                p = total * (x + (smooth - x) * ease);
            }
            return ParamAtProgress(p);
        }

        private double ParamAtProgress(double p)
        {
            int lo = 0, hi = progress.Length - 1;
            if (p <= 0) return 0;
            if (p >= progress[hi]) return (double)hi / Samples;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (progress[mid] < p) lo = mid; else hi = mid;
            }
            double span = progress[hi] - progress[lo];
            double f = span > 1e-12 ? (p - progress[lo]) / span : 0;
            return (lo + f) / Samples;
        }

        private void SegmentAt(double u, out int i, out double s)
        {
            int n = keys.Length;
            i = Math.Max(0, Math.Min(n - 2, (int)Math.Floor(u)));
            s = Math.Max(0, Math.Min(1, u - i));
        }

        private Vector3 PosAt(double u)
        {
            int n = keys.Length;
            if (n == 1) return keys[0].Pos;
            SegmentAt(u, out int i, out double s);
            Vector3 p1 = keys[i].Pos, p2 = keys[i + 1].Pos;
            Vector3 p0 = i > 0 ? keys[i - 1].Pos : 2 * p1 - p2;
            Vector3 p3 = i + 2 < n ? keys[i + 2].Pos : 2 * p2 - p1;
            return Centripetal(p0, p1, p2, p3, (float)s);
        }

        private Quaternion RotAt(double u)
        {
            if (keys.Length == 1) return rots[0];
            SegmentAt(u, out int i, out double s);
            float h = (float)s;
            var outer = Quaternion.Slerp(rots[i], rots[i + 1], h);
            var inner = Quaternion.Slerp(ctrl[i], ctrl[i + 1], h);
            return Quaternion.Normalize(Quaternion.Slerp(outer, inner, 2f * h * (1f - h)));
        }

        private double FovLogAt(double u)
        {
            if (keys.Length == 1) return logFov[0];
            SegmentAt(u, out int i, out double s);
            return Hermite(i, i + 1, logFov[i], logFov[i + 1], fovSlope[i], fovSlope[i + 1], i + s);
        }

        // Barry and Goldman's pyramidal form with alpha 0.5 (knot spacing by the square root of the distance).
        internal static Vector3 Centripetal(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float s)
        {
            if (Vector3.DistanceSquared(p1, p2) < 1e-10f) return p1;
            float t0 = 0f;
            float t1 = t0 + Knot(p0, p1);
            float t2 = t1 + Knot(p1, p2);
            float t3 = t2 + Knot(p2, p3);
            float t = t1 + (t2 - t1) * s;
            Vector3 a1 = Mix(p0, p1, t0, t1, t), a2 = Mix(p1, p2, t1, t2, t), a3 = Mix(p2, p3, t2, t3, t);
            Vector3 b1 = Mix(a1, a2, t0, t2, t), b2 = Mix(a2, a3, t1, t3, t);
            return Mix(b1, b2, t1, t2, t);
        }

        private static float Knot(Vector3 a, Vector3 b) => (float)Math.Max(1e-3, Math.Pow(Vector3.DistanceSquared(a, b), 0.25));
        private static Vector3 Mix(Vector3 a, Vector3 b, float ta, float tb, float t) => (tb - t) / (tb - ta) * a + (t - ta) / (tb - ta) * b;

        // Fritsch-Carlson slopes for a monotone cubic through (x, y); end slopes are scaled by `endScale`.
        internal static double[] MonotoneSlopes(double[] x, double[] y, double endScale)
        {
            int n = x.Length;
            var m = new double[n];
            if (n < 2) return m;
            var d = new double[n - 1];
            for (int i = 0; i < n - 1; i++) d[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
            m[0] = d[0] * endScale;
            m[n - 1] = d[n - 2] * endScale;
            for (int i = 1; i < n - 1; i++)
            {
                if (d[i - 1] * d[i] <= 0) { m[i] = 0; continue; }
                double w1 = 2 * (x[i + 1] - x[i]) + (x[i] - x[i - 1]), w2 = (x[i + 1] - x[i]) + 2 * (x[i] - x[i - 1]);
                m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);   // weighted harmonic mean: stays monotone
            }
            return m;
        }

        internal static double Hermite(double x0, double x1, double y0, double y1, double m0, double m1, double x)
        {
            double h = x1 - x0;
            double s = Math.Max(0, Math.Min(1, (x - x0) / h));
            double s2 = s * s, s3 = s2 * s;
            return (2 * s3 - 3 * s2 + 1) * y0 + (s3 - 2 * s2 + s) * h * m0 + (-2 * s3 + 3 * s2) * y1 + (s3 - s2) * h * m1;
        }

        private static int Segment(double[] x, double t)
        {
            int i = Array.BinarySearch(x, t);
            if (i < 0) i = ~i - 1;
            return Math.Max(0, Math.Min(x.Length - 2, i));
        }

        // Angle in radians between two unit quaternions.
        internal static float Angle(Quaternion a, Quaternion b)
        {
            float dot = Math.Min(1f, Math.Abs(Quaternion.Dot(a, b)));
            return 2f * (float)Math.Acos(dot);
        }

        private static Quaternion Negate(Quaternion q) => new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        private static Quaternion Scale(Quaternion q, float k) => new Quaternion(q.X * k, q.Y * k, q.Z * k, q.W * k);

        private static Quaternion Log(Quaternion q)
        {
            var v = new Vector3(q.X, q.Y, q.Z);
            float len = v.Length();
            if (len < 1e-7f) return new Quaternion(0, 0, 0, 0);
            float angle = (float)Math.Atan2(len, q.W);
            v *= angle / len;
            return new Quaternion(v.X, v.Y, v.Z, 0);
        }

        private static Quaternion Exp(Quaternion q)
        {
            var v = new Vector3(q.X, q.Y, q.Z);
            float angle = v.Length();
            if (angle < 1e-7f) return Quaternion.Identity;
            v *= (float)Math.Sin(angle) / angle;
            return new Quaternion(v.X, v.Y, v.Z, (float)Math.Cos(angle));
        }

        // Rotation from yaw, pitch and roll in degrees, in Unity's order (roll about z, then pitch about x, then yaw about
        // y; y up, z forward, left-handed). Matches UnityEngine.Quaternion.Euler(pitch, yaw, roll).
        internal static Quaternion Euler(float pitch, float yaw, float roll)
        {
            const float d = (float)(Math.PI / 180.0);
            float cx = (float)Math.Cos(pitch * d * 0.5f), sx = (float)Math.Sin(pitch * d * 0.5f);
            float cy = (float)Math.Cos(yaw * d * 0.5f), sy = (float)Math.Sin(yaw * d * 0.5f);
            float cz = (float)Math.Cos(roll * d * 0.5f), sz = (float)Math.Sin(roll * d * 0.5f);
            return new Quaternion(
                cy * sx * cz + sy * cx * sz,
                sy * cx * cz - cy * sx * sz,
                cy * cx * sz - sy * sx * cz,
                cy * cx * cz + sy * sx * sz);
        }
    }
}
