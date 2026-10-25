#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace NuclearReplay.Playback
{
    // Camera Studio's model: the keys of one camera path being laid down in the viewer, and the path's settings.
    // Unity-free, so the tests run it; ReplayPlugin.Studio.cs feeds it free-camera poses and turns it into a "path"
    // shot (CinePath.cs) and a <recording>.path.json file.
    //
    // A key is a free-camera pose (global position, yaw, pitch, lens) at a replay time. When the studio follows
    // an aircraft, a key also keeps its pose relative to that aircraft (metres right, up and forward in the
    // aircraft's level heading frame, and yaw relative to its heading), so the same keys can play in world space
    // (city b-roll, a crane shot past a jet) or in target space (a move around a jet in flight).
    internal sealed class CameraStudio
    {
        internal sealed class Key
        {
            public double Rt;          // replay time, seconds from the start
            public Vector3 World;      // global position
            public float Yaw, Pitch, Fov;
            public bool HasRel;        // Rel and RelYaw are set (the followed aircraft was alive)
            public Vector3 Rel;
            public float RelYaw;

            public Key Clone() => (Key)MemberwiseClone();
        }

        // A key as the path shot needs it: t after the shot's t, position and yaw in the shot's space.
        internal struct ShotKey
        {
            public double T;
            public Vector3 Pos;
            public float Yaw, Pitch, Fov;
        }

        public static readonly string[] Aims = { "keys", "target", "path" };
        public static readonly string[] ShakeNames = { "off", "handheld", "heavy handheld", "rumble", "buffet" };
        public const double Gap = 3;       // seconds between keys laid down while the replay stands still
        public const double Lead = 1.5;    // the shot starts this long before the first key, so the cut settles
        public const double MinStep = 0.1; // keys are never closer in time than this

        public readonly List<Key> Keys = new List<Key>();
        public int Selected = -1;
        public bool KeyTiming;             // false: even speed; true: reach every key at its time
        public string Aim = "keys";
        public bool TargetSpace;
        public int ShakeIndex;
        public float Ease = 0.5f;
        public string UnitId, UnitName;    // the aircraft followed when the studio opened (null: none)
        public bool Open = false, Playing;
        public int Version;                // changes whenever the path changes (gizmos rebuild then)

        public int Count => Keys.Count;
        public double Start => Keys.Count > 0 ? Keys[0].Rt : 0;
        public double End => Keys.Count > 0 ? Keys[Keys.Count - 1].Rt : 0;
        public double ShotTime => Start - Lead;
        public bool AllRelative => Keys.Count > 0 && Keys.TrueForAll(k => k.HasRel);
        public string ShakeName => ShakeNames[ShakeIndex];

        public void Clear()
        {
            Keys.Clear();
            Selected = -1;
            Playing = false;
            Version++;
        }

        // Adds a key after the selected one (at the end when none is selected) and selects it. Its time: the
        // replay time now when the replay has moved on since the key before it, otherwise Gap seconds after it.
        // Between two keys: now when it falls between them, otherwise halfway.
        public int Add(Key key, double now)
        {
            int at = Selected >= 0 && Selected < Keys.Count ? Selected + 1 : Keys.Count;
            if (Keys.Count == 0) key.Rt = Math.Max(0, now);
            else if (at >= Keys.Count)
            {
                double last = Keys[Keys.Count - 1].Rt;
                key.Rt = now >= last + 0.25 ? now : last + Gap;
            }
            else
            {
                double a = Keys[at - 1].Rt, b = Keys[at].Rt;
                key.Rt = now > a + MinStep && now < b - MinStep ? now : (a + b) / 2;
            }
            Keys.Insert(at, key);
            Selected = at;
            Version++;
            return at;
        }

        // Replaces the selected key's pose and keeps its time.
        public bool Update(Key pose)
        {
            if (Selected < 0 || Selected >= Keys.Count) return false;
            pose.Rt = Keys[Selected].Rt;
            Keys[Selected] = pose;
            if (!AllRelative) TargetSpace = false;
            Version++;
            return true;
        }

        public bool Delete()
        {
            if (Selected < 0 || Selected >= Keys.Count) return false;
            Keys.RemoveAt(Selected);
            Selected = Keys.Count == 0 ? -1 : Math.Min(Selected, Keys.Count - 1);
            Version++;
            return true;
        }

        // Moves the selection one key on (or back), wrapping around. Returns the selected key.
        public Key Select(int step)
        {
            if (Keys.Count == 0) { Selected = -1; return null; }
            Selected = Selected < 0 ? (step >= 0 ? 0 : Keys.Count - 1) : ((Selected + step) % Keys.Count + Keys.Count) % Keys.Count;
            return Keys[Selected];
        }

        // Moves the selected key in time by `seconds`, and every later key with it. A key never moves to or
        // before the key ahead of it, or before the start of the replay.
        public bool Retime(double seconds)
        {
            if (Selected < 0 || Selected >= Keys.Count) return false;
            double floor = Selected > 0 ? Keys[Selected - 1].Rt + MinStep : 0;
            double shift = Math.Max(floor - Keys[Selected].Rt, seconds);
            if (Math.Abs(shift) < 1e-9) return false;
            for (int i = Selected; i < Keys.Count; i++) Keys[i].Rt += shift;
            Version++;
            return true;
        }

        public void ToggleTiming() { KeyTiming = !KeyTiming; Version++; }
        public void NextAim() { Aim = Aims[(Array.IndexOf(Aims, Aim) + 1) % Aims.Length]; Version++; }
        public void NextShake() { ShakeIndex = (ShakeIndex + 1) % ShakeNames.Length; Version++; }

        // World and target space; target space only when every key has its pose relative to the aircraft.
        public bool ToggleSpace()
        {
            if (!TargetSpace && (!AllRelative || UnitId == null)) return false;
            TargetSpace = !TargetSpace;
            Version++;
            return true;
        }

        // Shake option amounts for the shot (handheld, rumble, buffet).
        public void ShakeAmounts(out float handheld, out float rumble, out float buffet)
        {
            handheld = rumble = buffet = 0f;
            switch (ShakeIndex)
            {
                case 1: handheld = 1f; break;
                case 2: handheld = 2.2f; break;
                case 3: rumble = 1f; handheld = 0.3f; break;
                case 4: buffet = 1f; break;
            }
        }

        // The shot's target: the aircraft when the path is relative to it or looks at it, else none.
        public string ShotTarget => UnitId != null && (TargetSpace || Aim == "target") ? "unit:" + UnitId : "none";

        public List<ShotKey> ShotKeys()
        {
            var list = new List<ShotKey>();
            double t0 = ShotTime;
            foreach (var k in Keys)
                list.Add(new ShotKey
                {
                    T = Math.Round(k.Rt - t0, 3),
                    Pos = TargetSpace ? k.Rel : k.World,
                    Yaw = TargetSpace ? k.RelYaw : k.Yaw,
                    Pitch = k.Pitch,
                    Fov = k.Fov,
                });
            return list;
        }

        // The path through the keys' world positions (for the gizmos and the panel's length).
        public CameraPath WorldPath()
        {
            if (Keys.Count == 0) return null;
            var list = new List<CameraPath.Key>();
            foreach (var k in Keys)
                list.Add(new CameraPath.Key { T = k.Rt, Pos = k.World, Rot = CameraPath.Euler(k.Pitch, k.Yaw, 0), Fov = k.Fov });
            return new CameraPath(list, KeyTiming, Ease);
        }

        // Pose relative to an aircraft at `unitPos` heading `headingYaw` degrees (level frame: x right, y up, z
        // forward), for Key.Rel and Key.RelYaw.
        public static Vector3 ToRelative(Vector3 world, Vector3 unitPos, float headingYaw) =>
            Vector3.Transform(world - unitPos, Quaternion.Conjugate(CameraPath.Euler(0, headingYaw, 0)));

        public static float WrapYaw(float yaw)
        {
            yaw %= 360f;
            if (yaw > 180f) yaw -= 360f;
            if (yaw <= -180f) yaw += 360f;
            return yaw;
        }

        // Panel text.
        public string Describe()
        {
            var ic = CultureInfo.InvariantCulture;
            var lines = new List<string>();
            if (Keys.Count == 0) lines.Add("Camera path: no keys yet. Fly the free camera and press K.");
            else
            {
                var path = WorldPath();
                lines.Add(string.Format(ic, "Camera path: {0} key{1}, {2} to {3} ({4:0.0} s, {5:0} m)", Keys.Count, Keys.Count == 1 ? "" : "s",
                    Clock(Start), Clock(End), End - Start, Metres(path)));
                if (Selected >= 0)
                {
                    var k = Keys[Selected];
                    lines.Add(string.Format(ic, "Key {0} at {1} (+{2:0.0} s), lens {3:0} deg", Selected + 1, Clock(k.Rt), k.Rt - Start, k.Fov));
                }
            }
            lines.Add(string.Format(ic, "Timing {0} (T)   Aim {1} (L)   Shake {2} (J)", KeyTiming ? "keys" : "even", Aim, ShakeName));
            lines.Add(UnitId == null ? "Space world (no aircraft followed)"
                : string.Format(ic, "Space {0} (M)   Aircraft {1}", TargetSpace ? "target" : "world", UnitName ?? UnitId));
            return string.Join("\n", lines);
        }

        // Distance the camera travels along a path, in metres.
        public static double Metres(CameraPath path)
        {
            double metres = 0;
            var line = path.Polyline;
            for (int i = 1; i < line.Count; i++) metres += Vector3.Distance(line[i], line[i - 1]);
            return metres;
        }

        public static string Clock(double seconds)
        {
            seconds = Math.Max(0, seconds);
            int m = (int)(seconds / 60);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00.0}", m, seconds - m * 60);
        }
    }
}
