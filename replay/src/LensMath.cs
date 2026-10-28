#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NuclearReplay.Playback
{
    // Lens maths and the lens settings of the viewer's Lens panel (F3) and of shot lists. Unity-free, so
    // NuclearReplay.Core.Tests runs it; Lens.cs turns the settings into a URP Volume and a field of view.
    //
    // Focal length is in millimetres on a sensor preset. The frame is the largest crop of the sensor at the
    // screen's aspect, so on Super 35 a 16:9 frame is 24.89 mm wide and 14.0 mm high and a 9:16 frame is 18.66 mm
    // high. Unity's field of view is vertical, so the conversion needs the aspect.
    internal static class LensMath
    {
        internal sealed class Sensor
        {
            public string Id, Name;
            public double Width, Height;   // mm
            public double Coc;             // circle of confusion for the depth of field readout, mm
        }

        public static readonly Sensor[] Sensors =
        {
            new Sensor { Id = "super35", Name = "Super 35", Width = 24.89, Height = 18.66, Coc = 0.025 },
            new Sensor { Id = "full_frame", Name = "Full frame", Width = 36.0, Height = 24.0, Coc = 0.03 },
        };

        public const string DefaultSensor = "super35";
        public const double MinFocal = 8, MaxFocal = 600;       // mm
        public const double MinFov = 1, MaxFov = 120;           // vertical degrees
        public const double MinFocus = 0.3, MaxFocus = 20000;   // metres
        public const double MinFStop = 1, MaxFStop = 32;        // URP's aperture range

        public static readonly double[] FocalSteps = { 8, 10, 12, 14, 16, 18, 21, 24, 28, 32, 35, 40, 50, 65, 75, 85, 100, 120, 135, 150, 180, 200, 250, 300, 400, 500, 600 };
        public static readonly double[] FStops = { 1.4, 2, 2.8, 4, 5.6, 8, 11, 16, 22 };
        public static readonly double[] FocusSteps = { 0.5, 1, 1.5, 2, 3, 4, 5, 7, 10, 15, 20, 30, 40, 50, 75, 100, 150, 200, 300, 500, 750, 1000, 2000, 5000, 10000 };

        public static Sensor FindSensor(string id)
        {
            foreach (var s in Sensors) if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        public static Sensor SensorOrDefault(string id) => FindSensor(id) ?? Sensors[0];

        // Height of the frame on the sensor, mm: the largest crop of the sensor at this aspect (width / height).
        public static double FrameHeight(Sensor sensor, double aspect)
        {
            if (!(aspect > 0)) aspect = 16.0 / 9.0;
            double width = Math.Min(sensor.Width, sensor.Height * aspect);
            return width / aspect;
        }

        // Vertical field of view in degrees for a focal length in mm.
        public static double FovFromFocal(double focalMm, Sensor sensor, double aspect)
        {
            focalMm = Clamp(focalMm, 0.5, 5000);
            double fov = 2 * Math.Atan(FrameHeight(sensor, aspect) / (2 * focalMm)) * 180 / Math.PI;
            return Clamp(fov, MinFov, MaxFov);
        }

        // Focal length in mm for a vertical field of view in degrees.
        public static double FocalFromFov(double fovDeg, Sensor sensor, double aspect)
        {
            fovDeg = Clamp(fovDeg, 0.1, 179);
            return FrameHeight(sensor, aspect) / (2 * Math.Tan(fovDeg * Math.PI / 360));
        }

        // Near and far limits of acceptable sharpness (metres; far is infinity past the hyperfocal distance).
        public static void SharpRange(double focalMm, double fStop, double focusM, double cocMm, out double near, out double far)
        {
            double f = focalMm / 1000, c = cocMm / 1000, s = Math.Max(focusM, f * 1.01);
            double h = f * f / (fStop * c) + f;   // hyperfocal distance
            near = s * (h - f) / (h + s - 2 * f);
            far = s >= h ? double.PositiveInfinity : s * (h - f) / (h - s);
        }

        // The next value up (step > 0) or down in a sorted list of stops; a value between stops goes to the
        // nearest stop in that direction.
        public static double Step(double[] stops, double value, int step)
        {
            if (step > 0) { foreach (var v in stops) if (v > value * 1.0001) return v; return stops[stops.Length - 1]; }
            if (step < 0) { for (int i = stops.Length - 1; i >= 0; i--) if (stops[i] < value * 0.9999) return stops[i]; return stops[0]; }
            return value;
        }

        // Focus pulls blend in dioptres (1 / distance), the way a follow focus ring moves: the far end of the
        // pull does not rush past.
        public static double FocusBlend(double a, double b, double s)
        {
            s = Clamp(s, 0, 1);
            double da = 1 / Math.Max(a, MinFocus), db = 1 / Math.Max(b, MinFocus);
            return 1 / (da + (db - da) * s);
        }

        // URP 14's Bokeh depth of field reads only a focal length (1 to 300 mm) and an aperture (1 to 32): its blur
        // radius, as a fraction of the frame height, is min(1, A * F / (P - F)) * min(0.05, 14 / heightPx), with
        // A = focal / aperture (mm), F = focal (m) and P = focus distance (m). A * F / (P - F) is the true blur circle (mm
        // on the sensor) of a point at infinity, so URP behaves like a 28 mm tall frame with a 14 pixel blur cap.
        // This picks URP's focal length and aperture so a point at infinity blurs by the same share of the frame as
        // through the real lens on the chosen sensor; past URP's cap (or its 300 mm and f/1 limits) it blurs less.
        public static void UrpBokeh(double focalMm, double fStop, double focusM, double frameHeightMm, double heightPx, out double urpFocal, out double urpAperture)
        {
            focusM = Math.Max(focusM, 0.1);
            double f = focalMm / 1000;
            double trueBlur = focalMm / fStop * f / Math.Max(1e-3, focusM - f);              // mm on the sensor
            double share = trueBlur / 2 / frameHeightMm;                                      // radius, share of the frame
            double cap = Math.Min(0.05, 14 / Math.Max(1, heightPx));
            double want = share / cap;                                                        // URP's A * F / (P - F)
            urpFocal = Clamp(focalMm, 1, 300);
            double ff = urpFocal / 1000;
            urpAperture = urpFocal * ff / Math.Max(1e-6, want * Math.Max(1e-3, focusM - ff));
            if (urpAperture < 1)
            {
                // Wider than f/1: a longer URP focal length instead (A * F grows with its square).
                urpAperture = 1;
                urpFocal = Clamp(Math.Sqrt(want * focusM * 1000), urpFocal, 300);
            }
            urpAperture = Clamp(urpAperture, 1, 32);
        }

        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        public static string Mm(double focalMm) => focalMm.ToString(focalMm < 20 ? "0.#" : "0", CultureInfo.InvariantCulture) + " mm";

        public static string Metres(double m)
        {
            var ic = CultureInfo.InvariantCulture;
            if (double.IsInfinity(m)) return "infinity";
            if (m >= 1000) return (m / 1000).ToString(m >= 10000 ? "0" : "0.#", ic) + " km";
            return m.ToString(m < 10 ? "0.#" : "0", ic) + " m";
        }

        public static string FStop(double n) => "f/" + n.ToString(n < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
    }

    // What the lens does: focal length, sensor, depth of field and the four film effects. One of these is the viewer's
    // Lens panel (kept in the plugin's config), another comes from a shot's fields, and Camera Studio keeps a copy
    // with its path.
    internal sealed class LensSettings
    {
        public double FocalMm;                    // 0 = the camera's own lens
        public string Sensor = LensMath.DefaultSensor;
        public bool Dof;
        public double FStop = 2.8;
        public bool FocusTarget = true;           // autofocus on the target; false: FocusM
        public double FocusM = 50;
        public double Vignette, Grain, Chromatic, MotionBlur;   // 0 to 1, 0 = off

        public LensSettings Clone() => (LensSettings)MemberwiseClone();

        // True when the Volume has something to do.
        public bool AnyEffect => Dof || Vignette > 0 || Grain > 0 || Chromatic > 0 || MotionBlur > 0;
        public LensMath.Sensor SensorInfo => LensMath.SensorOrDefault(Sensor);

        public bool SameAs(LensSettings o) => o != null && FocalMm == o.FocalMm && Sensor == o.Sensor && Dof == o.Dof && FStop == o.FStop
            && FocusTarget == o.FocusTarget && FocusM == o.FocusM && Vignette == o.Vignette && Grain == o.Grain && Chromatic == o.Chromatic
            && MotionBlur == o.MotionBlur;

        // ---------------- shot list fields ----------------

        // Shot fields that belong to the lens (the validator in NuclearReplay.Core knows the same list).
        public static readonly string[] ShotFields = { "focal_mm", "sensor", "dof", "vignette", "grain", "chromatic", "motion_blur" };

        // The lens a shot asks for, from its fields as plain values (numbers as double, strings, bools, and the dof
        // object as a dictionary). Null when the shot has no lens field at all: the shot then renders exactly as
        // before the lens pack. Bad values fall back to the defaults (`nr shots validate` reports them).
        public static LensSettings FromShot(IDictionary<string, object> shot)
        {
            if (shot == null) return null;
            bool any = false;
            foreach (var f in ShotFields) if (shot.ContainsKey(f) && shot[f] != null) any = true;
            if (!any) return null;
            var lens = new LensSettings();
            if (Num(shot, "focal_mm") is double mm && mm > 0) lens.FocalMm = LensMath.Clamp(mm, LensMath.MinFocal, LensMath.MaxFocal);
            if (shot.TryGetValue("sensor", out var sensor) && sensor is string sid && LensMath.FindSensor(sid) != null) lens.Sensor = LensMath.FindSensor(sid).Id;
            if (shot.TryGetValue("dof", out var dof))
            {
                if (dof is bool on) lens.Dof = on;
                else if (dof is IDictionary<string, object> d)
                {
                    lens.Dof = !(d.TryGetValue("enabled", out var en) && en is bool b && !b);
                    if (Num(d, "f_stop") is double n && n > 0) lens.FStop = LensMath.Clamp(n, LensMath.MinFStop, LensMath.MaxFStop);
                    if (d.TryGetValue("focus", out var focus))
                    {
                        if (focus is string fs && string.Equals(fs, "target", StringComparison.OrdinalIgnoreCase)) lens.FocusTarget = true;
                        else if (focus is double m && m > 0) { lens.FocusTarget = false; lens.FocusM = LensMath.Clamp(m, LensMath.MinFocus, LensMath.MaxFocus); }
                    }
                }
            }
            lens.Vignette = Amount(shot, "vignette");
            lens.Grain = Amount(shot, "grain");
            lens.Chromatic = Amount(shot, "chromatic");
            lens.MotionBlur = Amount(shot, "motion_blur");
            return lens;
        }

        // The same fields for a shot (Camera Studio's path shot). Only what differs from "off" is written.
        public Dictionary<string, object> ToShot()
        {
            var o = new Dictionary<string, object>();
            if (FocalMm > 0) o["focal_mm"] = Math.Round(FocalMm, 1);
            if (FocalMm > 0 || Sensor != LensMath.DefaultSensor) o["sensor"] = Sensor;
            if (Dof)
                o["dof"] = new Dictionary<string, object> { ["f_stop"] = Math.Round(FStop, 2), ["focus"] = FocusTarget ? (object)"target" : Math.Round(FocusM, 2) };
            if (Vignette > 0) o["vignette"] = Math.Round(Vignette, 3);
            if (Grain > 0) o["grain"] = Math.Round(Grain, 3);
            if (Chromatic > 0) o["chromatic"] = Math.Round(Chromatic, 3);
            if (MotionBlur > 0) o["motion_blur"] = Math.Round(MotionBlur, 3);
            return o;
        }

        private static double? Num(IDictionary<string, object> o, string key)
        {
            if (!o.TryGetValue(key, out var v) || v == null) return null;
            if (v is double d) return double.IsNaN(d) || double.IsInfinity(d) ? (double?)null : d;
            if (v is float f) return f;
            if (v is int i) return i;
            if (v is long l) return l;
            return null;
        }

        private static double Amount(IDictionary<string, object> o, string key)
        {
            if (o.TryGetValue(key, out var v) && v is bool b) return b ? 0.5 : 0;
            return Num(o, key) is double d ? LensMath.Clamp(d, 0, 1) : 0;
        }

        // ---------------- the Lens panel ----------------

        public enum Row { Focal, Sensor, Dof, Focus, FStop, Vignette, Grain, Chromatic, MotionBlur }
        public static readonly int RowCount = Enum.GetValues(typeof(Row)).Length;

        // Changes one setting by one step up (dir > 0) or down; big steps for the amounts with Shift. `currentFocalMm`
        // is the lens on screen now (used when the focal length leaves "the camera's own"), `liveFocusM` the
        // autofocus distance now (used when the focus leaves "on the target"). Returns what changed, for a toast.
        public string Adjust(Row row, int dir, bool big, double currentFocalMm, double liveFocusM)
        {
            switch (row)
            {
                case Row.Focal:
                    {
                        double from = FocalMm > 0 ? FocalMm : LensMath.Clamp(currentFocalMm > 0 ? currentFocalMm : 24, LensMath.MinFocal, LensMath.MaxFocal);
                        double to = LensMath.Step(LensMath.FocalSteps, from, dir);
                        if (big) to = LensMath.Step(LensMath.FocalSteps, LensMath.Step(LensMath.FocalSteps, to, dir), dir);
                        FocalMm = to;
                        return "Focal length " + LensMath.Mm(FocalMm);
                    }
                case Row.Sensor:
                    {
                        int at = Math.Max(0, Array.FindIndex(LensMath.Sensors, s => s.Id == Sensor));
                        Sensor = LensMath.Sensors[((at + dir) % LensMath.Sensors.Length + LensMath.Sensors.Length) % LensMath.Sensors.Length].Id;
                        return "Sensor " + SensorInfo.Name;
                    }
                case Row.Dof:
                    Dof = dir > 0 || (dir < 0 ? false : !Dof);
                    return "Depth of field " + (Dof ? "on" : "off");
                case Row.Focus:
                    {
                        if (FocusTarget)
                        {
                            // Leaving autofocus starts from where it was focused.
                            FocusTarget = false;
                            FocusM = LensMath.Clamp(liveFocusM > 0 ? liveFocusM : FocusM, LensMath.MinFocus, LensMath.MaxFocus);
                            if (dir == 0) return "Focus fixed at " + LensMath.Metres(FocusM);
                        }
                        double to = LensMath.Step(LensMath.FocusSteps, FocusM, dir);
                        if (big) to = LensMath.Step(LensMath.FocusSteps, LensMath.Step(LensMath.FocusSteps, to, dir), dir);
                        FocusM = to;
                        if (!Dof) Dof = true;
                        return "Focus " + LensMath.Metres(FocusM);
                    }
                case Row.FStop:
                    FStop = LensMath.Step(LensMath.FStops, FStop, dir);
                    if (big) FStop = LensMath.Step(LensMath.FStops, FStop, dir);
                    if (!Dof) Dof = true;
                    return "Aperture " + LensMath.FStop(FStop);
                default:
                    {
                        double step = (big ? 0.25 : 0.1) * Math.Sign(dir);
                        double v = Math.Round(LensMath.Clamp(Get(row) + step, 0, 1), 2);
                        Set(row, v);
                        return RowName(row) + " " + Percent(v);
                    }
            }
        }

        // Back to the default for one row (focal length: the camera's own; focus: on the target).
        public string Reset(Row row)
        {
            var d = new LensSettings();
            switch (row)
            {
                case Row.Focal: FocalMm = 0; return "Focal length: the camera's own";
                case Row.Sensor: Sensor = d.Sensor; return "Sensor " + SensorInfo.Name;
                case Row.Dof: Dof = false; return "Depth of field off";
                case Row.Focus: FocusTarget = true; return "Focus on the target";
                case Row.FStop: FStop = d.FStop; return "Aperture " + LensMath.FStop(FStop);
                default: Set(row, 0); return RowName(row) + " off";
            }
        }

        private double Get(Row row) => row == Row.Vignette ? Vignette : row == Row.Grain ? Grain : row == Row.Chromatic ? Chromatic : row == Row.MotionBlur ? MotionBlur : 0;

        private void Set(Row row, double v)
        {
            switch (row)
            {
                case Row.Vignette: Vignette = v; break;
                case Row.Grain: Grain = v; break;
                case Row.Chromatic: Chromatic = v; break;
                case Row.MotionBlur: MotionBlur = v; break;
            }
        }

        public static string RowName(Row row)
        {
            switch (row)
            {
                case Row.Focal: return "Focal length";
                case Row.Sensor: return "Sensor";
                case Row.Dof: return "Depth of field";
                case Row.Focus: return "Focus";
                case Row.FStop: return "Aperture";
                case Row.Vignette: return "Vignette";
                case Row.Grain: return "Film grain";
                case Row.Chromatic: return "Chromatic aberration";
                default: return "Motion blur";
            }
        }

        private static string Percent(double v) => v <= 0 ? "off" : (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

        // The value column of the panel. `currentFocalMm` and `liveFocusM` as for Adjust (0 when unknown).
        public string Value(Row row, double currentFocalMm, double liveFocusM)
        {
            switch (row)
            {
                case Row.Focal: return FocalMm > 0 ? LensMath.Mm(FocalMm) : "camera's own" + (currentFocalMm > 0 ? " (" + LensMath.Mm(currentFocalMm) + ")" : "");
                case Row.Sensor: return SensorInfo.Name;
                case Row.Dof: return Dof ? "on" : "off";
                case Row.Focus: return FocusTarget ? "on the target" + (liveFocusM > 0 ? " (" + LensMath.Metres(liveFocusM) + ")" : "") : LensMath.Metres(FocusM);
                case Row.FStop: return LensMath.FStop(FStop);
                default: return Percent(Get(row));
            }
        }

        // One line: "Super 35, depth of field f/2.8 on the target, vignette 30%".
        public string Summary()
        {
            var parts = new List<string> { SensorInfo.Name };
            if (FocalMm > 0) parts.Insert(0, LensMath.Mm(FocalMm));
            if (Dof) parts.Add("depth of field " + LensMath.FStop(FStop) + (FocusTarget ? " on the target" : " at " + LensMath.Metres(FocusM)));
            for (var row = Row.Vignette; row <= Row.MotionBlur; row++)
                if (Get(row) > 0) parts.Add(RowName(row).ToLowerInvariant() + " " + Percent(Get(row)));
            if (!AnyEffect) parts.Add("no effects");
            return string.Join(", ", parts);
        }

        // Panel text: one line per row, the selected one marked, then what is sharp.
        public string Describe(Row selected, double currentFocalMm, double liveFocusM)
        {
            var lines = new List<string>();
            for (int i = 0; i < RowCount; i++)
            {
                var row = (Row)i;
                string text = RowName(row) + ": " + Value(row, currentFocalMm, liveFocusM);
                lines.Add(row == selected ? "> <b>" + text + "</b>" : "   " + text);
            }
            double focal = FocalMm > 0 ? FocalMm : currentFocalMm;
            double focus = FocusTarget ? liveFocusM : FocusM;
            if (Dof && focal > 0 && focus > 0)
            {
                LensMath.SharpRange(focal, FStop, focus, SensorInfo.Coc, out double near, out double far);
                lines.Add("Sharp from " + LensMath.Metres(near) + " to " + LensMath.Metres(far));
            }
            return string.Join("\n", lines);
        }
    }
}
