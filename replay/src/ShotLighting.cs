#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NuclearReplay.Playback
{
    // Lighting settings of one shot (or of the viewer's Lighting panel): the values Lighting.cs puts on the scene in
    // place of the recorded ones. A null value means "as recorded". Unity-free and JSON-free, so the tests run it as
    // is; Lighting.cs reads and writes the JSON keys through Set and Pairs.
    //
    // Shot keys (or the shot list's root "lighting" object as a default for every shot; the shot wins key by key):
    //   time_of_day     hours, 0-24 (6.5 dawn, 17.3 golden hour, 17.8 dusk on most maps)
    //   sun_azimuth     compass degrees toward the sun (0 = +z, 90 = +x), or "camera": the sun in front of the camera,
    //                   `sun_camera_off` degrees (default 15) right of the view, locked at the shot's first frame
    //   sun_camera_off  degrees, for sun_azimuth "camera"
    //   fog             multiplier on the game's haze (fog density): 0.5 half, 2 double
    //   weather         the game's weather amount, 0 clear to 1 storm (cloud cover, overcast light)
    //   cloud_height    metres above sea level of the cloud layer
    internal sealed class ShotLighting
    {
        public float? TimeOfDay, SunAzimuth, Fog, Weather, CloudHeight;
        public bool SunCamera;
        public float SunCameraOff = 15f;

        internal static readonly string[] Keys = { "time_of_day", "sun_azimuth", "sun_camera_off", "fog", "weather", "cloud_height" };

        public bool Any => TimeOfDay.HasValue || SunAzimuth.HasValue || SunCamera || Fog.HasValue || Weather.HasValue || CloudHeight.HasValue;

        public bool Same(ShotLighting o) => o != null && TimeOfDay == o.TimeOfDay && SunAzimuth == o.SunAzimuth && Fog == o.Fog
            && Weather == o.Weather && CloudHeight == o.CloudHeight && SunCamera == o.SunCamera && SunCameraOff == o.SunCameraOff;

        public ShotLighting Clone() => (ShotLighting)MemberwiseClone();

        // Reads one key. `number` is the value when it is a JSON number, `text` when it is a string. Returns false
        // (and leaves the setting alone) for an unknown key or a value of the wrong kind.
        public bool Set(string key, double? number, string text)
        {
            switch (key)
            {
                case "time_of_day":
                    if (!number.HasValue) return false;
                    TimeOfDay = BacklightMath.Repeat((float)number.Value, 24f);
                    return true;
                case "sun_azimuth":
                    if (text != null && text.Equals("camera", StringComparison.OrdinalIgnoreCase)) { SunCamera = true; SunAzimuth = null; return true; }
                    if (!number.HasValue) return false;
                    SunCamera = false;
                    SunAzimuth = BacklightMath.Repeat((float)number.Value, 360f);
                    return true;
                case "sun_camera_off":
                    if (!number.HasValue) return false;
                    SunCameraOff = (float)number.Value;
                    return true;
                case "fog":
                    if (!number.HasValue) return false;
                    Fog = Math.Max(0f, (float)number.Value);
                    return true;
                case "weather":
                    if (!number.HasValue) return false;
                    Weather = Math.Clamp((float)number.Value, 0f, 1f);
                    return true;
                case "cloud_height":
                    if (!number.HasValue) return false;
                    CloudHeight = (float)number.Value;
                    return true;
            }
            return false;
        }

        // The keys this setting carries, for writing it into a shot or a saved path.
        public IEnumerable<KeyValuePair<string, object>> Pairs()
        {
            if (TimeOfDay.HasValue) yield return Pair("time_of_day", Math.Round(TimeOfDay.Value, 3));
            if (SunCamera)
            {
                yield return Pair("sun_azimuth", "camera");
                if (SunCameraOff != 15f) yield return Pair("sun_camera_off", Math.Round(SunCameraOff, 2));
            }
            else if (SunAzimuth.HasValue) yield return Pair("sun_azimuth", Math.Round(SunAzimuth.Value, 2));
            if (Fog.HasValue) yield return Pair("fog", Math.Round(Fog.Value, 3));
            if (Weather.HasValue) yield return Pair("weather", Math.Round(Weather.Value, 3));
            if (CloudHeight.HasValue) yield return Pair("cloud_height", Math.Round(CloudHeight.Value, 1));
        }

        private static KeyValuePair<string, object> Pair(string k, object v) => new KeyValuePair<string, object>(k, v);

        // `over` wins key by key; null when neither carries anything.
        public static ShotLighting Merge(ShotLighting over, ShotLighting under)
        {
            if (over == null || !over.Any) return under != null && under.Any ? under.Clone() : null;
            if (under == null || !under.Any) return over.Clone();
            var m = under.Clone();
            if (over.TimeOfDay.HasValue) m.TimeOfDay = over.TimeOfDay;
            if (over.SunCamera || over.SunAzimuth.HasValue) { m.SunCamera = over.SunCamera; m.SunAzimuth = over.SunAzimuth; m.SunCameraOff = over.SunCameraOff; }
            if (over.Fog.HasValue) m.Fog = over.Fog;
            if (over.Weather.HasValue) m.Weather = over.Weather;
            if (over.CloudHeight.HasValue) m.CloudHeight = over.CloudHeight;
            return m;
        }

        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "time_of_day {0} sun_azimuth {1} fog {2} weather {3} cloud_height {4}",
            TimeOfDay.HasValue ? TimeOfDay.Value.ToString("F2", CultureInfo.InvariantCulture) : "recorded",
            SunCamera ? "camera+" + SunCameraOff.ToString("F0", CultureInfo.InvariantCulture) : SunAzimuth.HasValue ? SunAzimuth.Value.ToString("F1", CultureInfo.InvariantCulture) : "recorded",
            Fog.HasValue ? "x" + Fog.Value.ToString("F2", CultureInfo.InvariantCulture) : "recorded",
            Weather.HasValue ? Weather.Value.ToString("F2", CultureInfo.InvariantCulture) : "recorded",
            CloudHeight.HasValue ? CloudHeight.Value.ToString("F0", CultureInfo.InvariantCulture) + " m" : "recorded");
    }

    // The viewer's Lighting panel (F2): its presets and text. Unity-free so the tests check the words and the steps.
    internal static class LightingPanel
    {
        // Time presets, in the order the panel's time key steps through them. Measured on the Confrontation map
        // (docs/LIGHTING.md): the sun is about 5 deg up at 6.5 h, 7 deg at 17.3 h, 2 deg at 17.8 h and off from about
        // 17.9 h, so golden hour is 17.0-17.6 h and dusk is just before 17.9 h.
        internal static readonly string[] TimeNames = { "As recorded", "Dawn", "Golden hour", "Dusk", "Night" };
        internal static readonly float?[] TimeHours = { null, 6.5f, 17.3f, 17.8f, 22f };

        internal static readonly string[] HazeNames = { "As recorded", "Clear air", "Light haze", "Hazy", "Thick haze" };
        internal static readonly float?[] HazeValues = { null, 0.25f, 0.5f, 2f, 4f };

        internal static readonly string[] WeatherNames = { "As recorded", "Clear", "Cloudy", "Overcast", "Storm" };
        internal static readonly float?[] WeatherValues = { null, 0f, 0.35f, 0.7f, 0.95f };

        public const float TimeStep = 0.25f;   // hours per PageUp / PageDown

        // Index of the preset that matches `value` exactly, else -1 (a hand-set value).
        internal static int Index(float?[] values, float? value)
        {
            for (int i = 0; i < values.Length; i++) if (values[i] == value) return i;
            return -1;
        }

        // The preset after the current one; a hand-set value goes to the first preset after "as recorded".
        internal static float? Next(float?[] values, float? value)
        {
            int i = Index(values, value);
            return i < 0 ? values[1] : values[(i + 1) % values.Length];
        }

        internal static float? StepTime(float? hours, float recorded, int steps) =>
            BacklightMath.Repeat((hours ?? recorded) + steps * TimeStep, 24f);

        internal static string Clock(float hours)
        {
            int minutes = (int)Math.Round(BacklightMath.Repeat(hours, 24f) * 60f) % (24 * 60);
            return (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        internal static string Name(string[] names, float?[] values, float? value, string handSet)
        {
            int i = Index(values, value);
            return i >= 0 ? names[i] : handSet;
        }

        // The panel's body text. `recordedHours` is what the recording says now (NaN when no level is loaded).
        internal static string Describe(ShotLighting viewer, float recordedHours, bool shotListLighting)
        {
            var s = viewer ?? new ShotLighting();
            string rec = float.IsNaN(recordedHours) ? "" : "  (recorded " + Clock(recordedHours) + ")";
            string time = s.TimeOfDay.HasValue
                ? Name(TimeNames, TimeHours, s.TimeOfDay, "Set by hand") + " " + Clock(s.TimeOfDay.Value) + rec
                : "As recorded" + rec;
            string haze = s.Fog.HasValue
                ? Name(HazeNames, HazeValues, s.Fog, "Set by hand") + " (x" + s.Fog.Value.ToString("0.##", CultureInfo.InvariantCulture) + ")"
                : "As recorded";
            string weather = s.Weather.HasValue
                ? Name(WeatherNames, WeatherValues, s.Weather, "Set by hand") + " (" + s.Weather.Value.ToString("0.##", CultureInfo.InvariantCulture) + ")"
                : "As recorded";
            string sun = s.SunCamera ? "in front of the camera" : s.SunAzimuth.HasValue
                ? "turned to " + s.SunAzimuth.Value.ToString("0", CultureInfo.InvariantCulture) + " deg" : "the map's own path";
            string text = "Time  <b>" + time + "</b>\nHaze  <b>" + haze + "</b>     Weather  <b>" + weather + "</b>\nSun  " + sun;
            if (shotListLighting) text += "\nThe shot list sets lighting too; its keys win over the panel.";
            return text;
        }

        // The toast for a change, e.g. "Lighting: golden hour (17:18)".
        internal static string TimeToast(float? hours) => hours.HasValue
            ? "Lighting: " + Name(TimeNames, TimeHours, hours, "time").ToLowerInvariant() + " (" + Clock(hours.Value) + ")"
            : "Lighting: time as recorded";

        internal static string HazeToast(float? fog) => fog.HasValue
            ? "Lighting: " + Name(HazeNames, HazeValues, fog, "haze").ToLowerInvariant() + " (x" + fog.Value.ToString("0.##", CultureInfo.InvariantCulture) + ")"
            : "Lighting: haze as recorded";

        internal static string WeatherToast(float? weather) => weather.HasValue
            ? "Lighting: weather " + Name(WeatherNames, WeatherValues, weather, "set by hand").ToLowerInvariant()
            : "Lighting: weather as recorded";
    }
}
