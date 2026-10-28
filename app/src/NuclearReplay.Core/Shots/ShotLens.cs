using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NuclearReplay.Core.Shots;

/// <summary>Checks the lens fields of a shot (replay/src/LensMath.cs, LensSettings.FromShot): focal_mm, sensor, dof,
/// vignette, grain, chromatic, motion_blur, and a path shot's per-key focal_mm, focus and f_stop.</summary>
public static class ShotLens
{
    public static readonly string[] Fields = { "focal_mm", "sensor", "dof", "vignette", "grain", "chromatic", "motion_blur" };
    public static readonly string[] Sensors = { "super35", "full_frame" };
    public const double MinFocal = 8, MaxFocal = 600, MinFStop = 1, MaxFStop = 32, MinFocus = 0.3, MaxFocus = 20000;
    private static readonly string[] Amounts = { "vignette", "grain", "chromatic", "motion_blur" };
    private static readonly string[] DofFields = { "enabled", "f_stop", "focus" };

    public static void Validate(JsonObject s, string p, Action<string, string> error, Action<string, string> warn)
    {
        if (s["focal_mm"] is not null)
        {
            Focal(s["focal_mm"], $"{p}.focal_mm", error, warn);
            if (s["fov"] is not null) warn($"{p}.fov", "Both \"fov\" and \"focal_mm\" are set; focal_mm wins.");
        }
        if (s["sensor"] is not null)
        {
            if (!TryString(s["sensor"], out var sensor) || !Sensors.Contains(sensor))
                error($"{p}.sensor", $"\"sensor\" must be one of: {string.Join(", ", Sensors)}.");
            else if (s["focal_mm"] is null && !HasKeyFocal(s))
                warn($"{p}.sensor", "\"sensor\" only changes what focal_mm means; this shot has no focal_mm.");
        }
        bool dofOn = false;
        if (s["dof"] is JsonObject dof)
        {
            dofOn = true;
            foreach (var key in dof.Select(x => x.Key).Where(k => !DofFields.Contains(k)))
                warn($"{p}.dof.{key}", $"Unknown field \"{key}\" is ignored (dof takes enabled, f_stop, focus).");
            if (dof["enabled"] is not null)
            {
                if (dof["enabled"] is not JsonValue ev || ev.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                    error($"{p}.dof.enabled", "\"enabled\" must be true or false.");
                else dofOn = ev.GetValueKind() == JsonValueKind.True;
            }
            if (dof["f_stop"] is not null) FStop(dof["f_stop"], $"{p}.dof.f_stop", error, warn);
            if (dof["focus"] is not null) Focus(dof["focus"], $"{p}.dof.focus", error, warn);
        }
        else if (s["dof"] is JsonValue dv && dv.GetValueKind() is JsonValueKind.True or JsonValueKind.False) dofOn = dv.GetValueKind() == JsonValueKind.True;
        else if (s["dof"] is not null) error($"{p}.dof", "\"dof\" must be true, false or an object {\"f_stop\": 2.8, \"focus\": \"target\" or metres}.");

        foreach (var name in Amounts)
        {
            var node = s[name];
            if (node is null) continue;
            if (node is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False) continue;
            if (!TryNumber(node, out var v)) error($"{p}.{name}", $"\"{name}\" must be an amount from 0 (off) to 1, or true (0.5).");
            else if (v < 0 || v > 1) error($"{p}.{name}", $"\"{name}\" must be between 0 and 1.");
        }

        if (s["keys"] is JsonArray keys)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] is not JsonObject k) continue;
                string kp = $"{p}.keys[{i}]";
                if (k["focal_mm"] is not null)
                {
                    Focal(k["focal_mm"], $"{kp}.focal_mm", error, warn);
                    if (k["fov"] is not null) warn($"{kp}.fov", "Both \"fov\" and \"focal_mm\" are set; focal_mm wins.");
                }
                bool focusKey = k["focus"] is not null || k["f_stop"] is not null;
                if (k["focus"] is not null) Focus(k["focus"], $"{kp}.focus", error, warn);
                if (k["f_stop"] is not null) FStop(k["f_stop"], $"{kp}.f_stop", error, warn);
                if (focusKey && !dofOn) warn($"{kp}.focus", "Keyed focus needs depth of field on the shot (\"dof\"); it is ignored without it.");
            }
        }
    }

    private static bool HasKeyFocal(JsonObject s) => s["keys"] is JsonArray keys && keys.OfType<JsonObject>().Any(k => k["focal_mm"] is not null);

    private static void Focal(JsonNode? node, string path, Action<string, string> error, Action<string, string> warn)
    {
        if (!TryNumber(node, out var mm) || mm <= 0) error(path, "\"focal_mm\" must be a focal length in mm, above 0.");
        else if (mm < MinFocal || mm > MaxFocal)
            warn(path, $"focal_mm {mm.ToString(CultureInfo.InvariantCulture)} is outside {MinFocal}-{MaxFocal} mm and is clamped.");
    }

    private static void FStop(JsonNode? node, string path, Action<string, string> error, Action<string, string> warn)
    {
        if (!TryNumber(node, out var n) || n <= 0) error(path, "\"f_stop\" must be an aperture (f-number) above 0, e.g. 2.8.");
        else if (n < MinFStop || n > MaxFStop)
            warn(path, $"f_stop {n.ToString(CultureInfo.InvariantCulture)} is outside f/{MinFStop}-f/{MaxFStop} and is clamped.");
    }

    private static void Focus(JsonNode? node, string path, Action<string, string> error, Action<string, string> warn)
    {
        if (TryString(node, out var s))
        {
            if (!string.Equals(s, "target", StringComparison.OrdinalIgnoreCase)) error(path, "\"focus\" must be \"target\" or a distance in metres.");
            return;
        }
        if (!TryNumber(node, out var m) || m <= 0) error(path, "\"focus\" must be \"target\" or a distance in metres above 0.");
        else if (m < MinFocus || m > MaxFocus)
            warn(path, $"focus {m.ToString(CultureInfo.InvariantCulture)} m is outside {MinFocus}-{MaxFocus} m and is clamped.");
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return false;
        return v.TryGetValue(out value) && double.IsFinite(value);
    }

    private static bool TryString(JsonNode? node, out string value)
    {
        value = "";
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return false;
        value = v.GetValue<string>();
        return true;
    }
}
