using System;

namespace NuclearReplay.Playback
{
    // Procedural camera shake that any cine camera can layer on top of its pose. Unity-free, so the tests run it.
    //
    // Three layers, each scaled by its amount (0 = off, 1 = the normal strength):
    //   handheld  slow drift and sway of a camera held by hand: about 0.45 Hz with finer detail on top.
    //   rumble    fast, small buzz of a camera fixed to a vehicle or near an engine: about 15 Hz.
    //   buffet    wind on a long lens or an open door: about 4.5 Hz, coming and going in gusts.
    // `speed` scales every frequency. The noise is a pure function of time and seed, so a clip renders the same
    // every time and a seek lands on the same shake.
    internal static class CameraShake
    {
        // Pitch, yaw and roll offsets in degrees at time t (seconds).
        public static void Angles(double t, int seed, float handheld, float rumble, float buffet, float speed,
            out float pitch, out float yaw, out float roll)
        {
            pitch = yaw = roll = 0f;
            if (speed <= 0f) speed = 1f;
            double ts = t * speed;
            if (handheld > 0f)
            {
                pitch += handheld * 0.50f * Fbm(ts * 0.45, seed, 0, 3);
                yaw += handheld * 0.60f * Fbm(ts * 0.40, seed, 1, 3);
                roll += handheld * 0.35f * Fbm(ts * 0.30, seed, 2, 3);
            }
            if (rumble > 0f)
            {
                pitch += rumble * 0.08f * Fbm(ts * 15.0, seed, 3, 2);
                yaw += rumble * 0.06f * Fbm(ts * 13.0, seed, 4, 2);
                roll += rumble * 0.04f * Fbm(ts * 17.0, seed, 5, 2);
            }
            if (buffet > 0f)
            {
                // Gusts: a slow envelope between 0.15 and 1.
                float gust = 0.15f + 0.85f * Smooth01(0.5f + 0.9f * Noise(ts * 0.25, seed, 6));
                pitch += buffet * 0.60f * gust * Fbm(ts * 4.5, seed, 7, 2);
                yaw += buffet * 0.45f * gust * Fbm(ts * 3.8, seed, 8, 2);
                roll += buffet * 0.30f * gust * Fbm(ts * 5.2, seed, 9, 2);
            }
        }

        // Largest offset any layer combination can give, in degrees, for the given amounts. Used by the tests.
        public static float Bound(float handheld, float rumble, float buffet) =>
            Math.Abs(handheld) * 0.60f + Math.Abs(rumble) * 0.08f + Math.Abs(buffet) * 0.60f;

        // Fractal noise: octaves at double frequency and half amplitude, normalised to about -1..1.
        private static float Fbm(double x, int seed, int lane, int octaves)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Noise(x, seed, lane * 7 + i);
                norm += amp;
                amp *= 0.5f;
                x *= 2.0;
            }
            return sum / norm;
        }

        // 1D gradient noise in -1..1, smooth (quintic fade), deterministic per seed and lane.
        private static float Noise(double x, int seed, int lane)
        {
            double fl = Math.Floor(x);
            long i = (long)fl;
            float f = (float)(x - fl);
            float g0 = Gradient(i, seed, lane), g1 = Gradient(i + 1, seed, lane);
            float a = g0 * f, b = g1 * (f - 1f);
            float u = f * f * f * (f * (f * 6f - 15f) + 10f);
            return 2f * (a + (b - a) * u);   // the raw range is -0.5..0.5
        }

        private static float Gradient(long i, int seed, int lane)
        {
            unchecked
            {
                ulong h = (ulong)i * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)seed * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(uint)lane * 0x165667B19E3779F9UL;
                h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL;
                h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL;
                h ^= h >> 33;
                return (float)((h >> 40) / (double)(1UL << 24)) * 2f - 1f;
            }
        }

        private static float Smooth01(float x)
        {
            x = Math.Max(0f, Math.Min(1f, x));
            return x * x * (3f - 2f * x);
        }
    }
}
