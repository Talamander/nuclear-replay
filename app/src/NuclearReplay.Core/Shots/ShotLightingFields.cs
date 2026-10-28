using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NuclearReplay.Core.Shots;

/// <summary>Checks the lighting and backlight fields of a shot list (replay/src/Lighting.cs, ShotLighting.cs and
/// CineBacklight.cs; docs/AGENTS.md "Lighting"). Every field is optional; a list without them renders as recorded.</summary>
public static class ShotLightingFields
{
    /// <summary>Lighting keys, allowed on a shot and in the list's root "lighting" object (defaults for every shot).</summary>
    public static readonly string[] Lighting = { "time_of_day", "sun_azimuth", "sun_camera_off", "fog", "weather", "cloud_height" };

    /// <summary>Backlight keys, allowed on a shot: they place a cine camera on the sun line.</summary>
    public static readonly string[] Backlight =
        { "backlight", "backlight_off", "backlight_side", "backlight_pitch", "backlight_at", "backlight_height", "backlight_min", "backlight_max" };

    /// <summary>Cameras that honor backlight.</summary>
    public static readonly string[] BacklightCameras = { "chase_cine", "flyby", "track_from", "keyframe" };

    public static bool IsField(string key) => Array.IndexOf(Lighting, key) >= 0 || Array.IndexOf(Backlight, key) >= 0;

    /// <summary>The root "lighting" object.</summary>
    public static void Root(JsonNode? node, Action<string, string> error, Action<string, string> warn)
    {
        const string p = "$.lighting";
        if (node is not JsonObject o) { error(p, "\"lighting\" must be an object of lighting keys (time_of_day, sun_azimuth, fog, weather, cloud_height)."); return; }
        foreach (var key in o.Select(x => x.Key).Where(k => Array.IndexOf(Lighting, k) < 0))
            warn($"{p}.{key}", $"Unknown lighting field \"{key}\" is ignored. Use {string.Join(", ", Lighting)}.");
        Values(o, p, error, warn);
    }

    /// <summary>The lighting and backlight keys of one shot.</summary>
    public static void Shot(JsonObject s, string p, string? camera, Action<string, string> error, Action<string, string> warn)
    {
        Values(s, p, error, warn);
        bool on = false;
        if (s["backlight"] is not null)
        {
            if (!TryBool(s["backlight"], out on)) error($"{p}.backlight", "\"backlight\" must be true or false.");
            else if (on && camera != null && Array.IndexOf(BacklightCameras, camera) < 0)
                warn($"{p}.backlight", $"backlight only places {string.Join(", ", BacklightCameras)} cameras; the {camera} camera ignores it. sun_azimuth \"camera\" puts the sun in front of any camera.");
        }
        Number(s, p, "backlight_off", error, warn, v => v < 0 || v > 90 ? "\"backlight_off\" must be degrees between 0 and 90." : null);
        if (s["backlight_side"] is not null)
        {
            if (!TryString(s["backlight_side"], out var side) || (side != "left" && side != "right"))
                error($"{p}.backlight_side", "\"backlight_side\" must be \"left\" or \"right\".");
        }
        Number(s, p, "backlight_pitch", error, warn, v => v < -89 || v > 89 ? "\"backlight_pitch\" must be degrees between -89 and 89." : null);
        Number(s, p, "backlight_at", error, warn, v => v < 0 ? "\"backlight_at\" cannot be negative." : null);
        Number(s, p, "backlight_height", error, warn, _ => null);
        double? min = Number(s, p, "backlight_min", error, warn, v => v <= 0 ? "\"backlight_min\" must be more than 0 metres." : null);
        double? max = Number(s, p, "backlight_max", error, warn, v => v <= 0 ? "\"backlight_max\" must be more than 0 metres." : null);
        if (min.HasValue && max.HasValue && min.Value >= max.Value) error($"{p}.backlight_max", "\"backlight_max\" must be more than \"backlight_min\".");
        if (!on && Backlight.Skip(1).Any(k => s[k] is not null))
            warn($"{p}.backlight", "backlight_* fields do nothing without \"backlight\": true.");
    }

    private static void Values(JsonObject o, string p, Action<string, string> error, Action<string, string> warn)
    {
        Number(o, p, "time_of_day", error, warn, v => v < 0 || v > 24 ? "\"time_of_day\" must be hours between 0 and 24." : null);
        bool camera = false;
        if (o["sun_azimuth"] is not null)
        {
            if (TryString(o["sun_azimuth"], out var text))
            {
                if (!text.Equals("camera", StringComparison.OrdinalIgnoreCase)) error($"{p}.sun_azimuth", "\"sun_azimuth\" must be compass degrees (0-360) or \"camera\".");
                else camera = true;
            }
            else Number(o, p, "sun_azimuth", error, warn, v => v < 0 || v > 360 ? "\"sun_azimuth\" must be compass degrees between 0 and 360." : null);
        }
        Number(o, p, "sun_camera_off", error, warn, v => v < -90 || v > 90 ? "\"sun_camera_off\" must be degrees between -90 and 90." : null);
        if (o["sun_camera_off"] is not null && !camera && o["sun_azimuth"] is not null)
            warn($"{p}.sun_camera_off", "\"sun_camera_off\" only matters with \"sun_azimuth\": \"camera\".");
        double? fog = Number(o, p, "fog", error, warn, v => v < 0 ? "\"fog\" cannot be negative (1 is the recorded haze)." : null);
        if (fog > 10) warn($"{p}.fog", $"fog {Fmt(fog.Value)} is very thick; 0.25-4 covers clear air to heavy haze.");
        Number(o, p, "weather", error, warn, v => v < 0 || v > 1 ? "\"weather\" must be between 0 (clear) and 1 (storm)." : null);
        double? cloud = Number(o, p, "cloud_height", error, warn, v => v < 0 ? "\"cloud_height\" cannot be negative." : null);
        if (cloud > 12000) warn($"{p}.cloud_height", $"cloud_height {Fmt(cloud.Value)} m is above where the game puts clouds (missions use about 500-5000 m).");
    }

    // A number field: an error when it is not a number or `check` returns a message; its value otherwise.
    private static double? Number(JsonObject o, string p, string key, Action<string, string> error, Action<string, string> warn, Func<double, string?> check)
    {
        if (o[key] is null) return null;
        if (!TryNumber(o[key], out var v)) { error($"{p}.{key}", $"\"{key}\" must be a number."); return null; }
        var message = check(v);
        if (message != null) { error($"{p}.{key}", message); return null; }
        return v;
    }

    private static string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out value) && double.IsFinite(value);
    }

    private static bool TryString(JsonNode? node, out string value)
    {
        value = "";
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return false;
        value = v.GetValue<string>();
        return true;
    }

    private static bool TryBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is not JsonValue v) return false;
        var kind = v.GetValueKind();
        if (kind != JsonValueKind.True && kind != JsonValueKind.False) return false;
        value = kind == JsonValueKind.True;
        return true;
    }
}
