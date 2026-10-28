using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NuclearReplay.Core.Shots;

/// <summary>One camera instruction of a shot list (DEMO.md "Shot lists").</summary>
public sealed record Shot
{
    /// <summary>Replay time in seconds since the demo started.</summary>
    public double T { get; init; }
    public string Camera { get; init; } = "orbit";
    public string Target { get; init; } = "host";
    /// <summary>Field of view in degrees.</summary>
    public double? Fov { get; init; }
    /// <summary>Seconds the camera stays on a destroyed target before cutting to the nearest live aircraft (director default 3).</summary>
    public double? Hold { get; init; }
    /// <summary>Where the director goes once the target is gone and the hold is over, instead of the nearest
    /// aircraft (for example the victim at the moment a missile hits). Its own T is ignored.</summary>
    public Shot? Then { get; init; }
    /// <summary>two_shot only: the second unit to frame with the target (a target selector). Null: the unit the
    /// target is fighting, picked by the director.</summary>
    public string? Second { get; init; }

    internal JsonObject ToJsonObject(bool withTime = true)
    {
        var o = new JsonObject();
        if (withTime) o["t"] = Math.Round(T, 3);
        o["camera"] = Camera;
        o["target"] = Target;
        if (Fov.HasValue) o["fov"] = Fov.Value;
        if (Hold.HasValue) o["hold"] = Hold.Value;
        if (Second != null) o["second"] = Second;
        if (Then != null) o["then"] = Then.ToJsonObject(withTime: false);
        return o;
    }
}

public sealed class ShotList
{
    public List<Shot> Shots { get; init; } = new();

    public string ToJson() => JsonSerializer.Serialize(new JsonObject
    {
        ["shots"] = new JsonArray(Shots.OrderBy(s => s.T).Select(s => (JsonNode)s.ToJsonObject()).ToArray()),
    }, new JsonSerializerOptions { WriteIndented = true });
}

public sealed record ShotIssue(string Severity, string Path, string Message)
{
    [JsonIgnore] public bool IsError => Severity == "error";
}

public sealed class ShotValidationResult
{
    public bool Valid => Issues.All(i => !i.IsError);
    public List<ShotIssue> Issues { get; } = new();
    public ShotList? ShotList { get; set; }
}

/// <summary>Checks a shot list against what the replay director understands (replay/src/Director.cs).</summary>
public static class ShotValidator
{
    public static readonly string[] Cameras = { "orbit", "chase", "chase_cine", "action", "cockpit", "tv", "free", "relative", "ride", "two_shot" };
    public static readonly string[] FixedTargets = { "host", "auto", "latest_missile", "latest_host_missile" };
    private static readonly Regex UnitTarget = new(@"^unit:[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly string[] KnownShotFields = { "t", "camera", "target", "fov", "hold", "then" };

    // Options of the ride and two_shot cameras (replay/src/CineRide.cs, CineTwoShot.cs; light shake from CinePath.cs).
    private static readonly string[] ShakeFields = { "handheld", "rumble", "buffet", "shake_speed" };
    private static readonly string[] RideFields = { "offset", "look_dir", "look", "look_at", "roll", "lag", "rot_smooth", "look_smooth", "min_agl" };
    private static readonly string[] TwoShotFields = { "second", "side", "distance", "distance_max", "azimuth", "elevation", "fov_min", "fov_max", "margin",
        "swing_smooth", "max_swing", "second_hold", "pos_smooth", "look_smooth", "zoom_smooth", "min_agl" };

    /// <summary>Fields a camera reads besides t, camera, target, fov, hold and then.</summary>
    public static IReadOnlyList<string> CameraFields(string camera) => camera switch
    {
        "ride" => RideFields.Concat(ShakeFields).ToArray(),
        "two_shot" => TwoShotFields.Concat(ShakeFields).ToArray(),
        _ => Array.Empty<string>(),
    };

    public static bool IsValidTarget(string target) =>
        FixedTargets.Contains(target) || UnitTarget.IsMatch(target)
        || (target.StartsWith("name:", StringComparison.Ordinal) && target.Length > 5)
        || (target.StartsWith("type:", StringComparison.Ordinal) && target.Trim().Length > 5);

    private const string TargetHelp = "Use host, auto, latest_missile, latest_host_missile, unit:<id>, name:<text> or type:<text>.";

    public static ShotValidationResult ValidateFile(string path, double? demoDuration = null)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var r = new ShotValidationResult();
            r.Issues.Add(new ShotIssue("error", "$", "Cannot read file: " + e.Message));
            return r;
        }
        return Validate(text, demoDuration);
    }

    public static ShotValidationResult Validate(string json, double? demoDuration = null)
    {
        var result = new ShotValidationResult();
        void Error(string path, string message) => result.Issues.Add(new ShotIssue("error", path, message));
        void Warn(string path, string message) => result.Issues.Add(new ShotIssue("warning", path, message));

        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e)
        {
            Error("$", $"Not valid JSON: {e.Message}");
            return result;
        }
        if (root is not JsonObject obj) { Error("$", "The shot list must be a JSON object: {\"shots\": [...]}."); return result; }
        foreach (var key in obj.Select(p => p.Key).Where(k => k != "shots" && k != "$schema"))
            Warn($"$.{key}", $"Unknown field \"{key}\" is ignored.");
        if (obj["shots"] is not JsonArray shots) { Error("$.shots", "Missing \"shots\" array."); return result; }
        if (shots.Count == 0) Warn("$.shots", "No shots; the director will use orbit/auto.");

        var list = new ShotList();
        double? previous = null;
        for (int i = 0; i < shots.Count; i++)
        {
            var p = $"$.shots[{i}]";
            if (shots[i] is not JsonObject s) { Error(p, "Each shot must be an object."); continue; }
            foreach (var key in s.Select(x => x.Key).Where(k => !KnownShotFields.Contains(k) && !CameraFields(CameraOf(s)).Contains(k)))
                Warn($"{p}.{key}", UnknownField(key, "by the director"));

            double t = 0;
            if (s["t"] is null) Error($"{p}.t", "Missing \"t\" (seconds since the demo started).");
            else if (!TryNumber(s["t"], out t)) Error($"{p}.t", "\"t\" must be a number of seconds.");
            else if (t < 0) Error($"{p}.t", "\"t\" cannot be negative.");
            else if (demoDuration.HasValue && t > demoDuration.Value)
                Warn($"{p}.t", $"t={t.ToString(CultureInfo.InvariantCulture)} is after the end of the recording ({demoDuration.Value:F1}s).");
            if (previous.HasValue && t < previous.Value) Warn($"{p}.t", "Shots are not in time order; the director sorts them.");
            previous = t;

            var shot = Body(s, p, Error, Warn) with { T = t };
            if (s["then"] is not null)
            {
                if (s["then"] is not JsonObject then) Error($"{p}.then", "\"then\" must be a shot object (camera, target, fov, hold).");
                else
                {
                    foreach (var key in then.Select(x => x.Key).Where(k => (!KnownShotFields.Contains(k) && !CameraFields(CameraOf(then)).Contains(k)) || k == "then"))
                        Warn($"{p}.then.{key}", key == "then" ? "Field \"then\" is ignored inside \"then\"." : UnknownField(key, "inside \"then\""));
                    shot = shot with { Then = Body(then, $"{p}.then", Error, Warn) };
                }
            }
            list.Shots.Add(shot);
        }
        result.ShotList = list;
        return result;
    }

    private static string CameraOf(JsonObject s) => TryString(s["camera"], out var c) ? c : "orbit";

    // A field no camera reads, or one that belongs to another camera.
    private static string UnknownField(string key, string where)
    {
        var users = Cameras.Where(c => CameraFields(c).Contains(key)).ToList();
        return users.Count > 0
            ? $"Field \"{key}\" is only read by the {string.Join(" and ", users)} camera; it is ignored here."
            : $"Unknown field \"{key}\" is ignored {where}.";
    }

    // Camera, target, fov and hold of one shot object (also used for a shot's "then").
    private static Shot Body(JsonObject s, string p, Action<string, string> Error, Action<string, string> Warn)
    {
        string camera = "orbit";
        if (s["camera"] is null) Warn($"{p}.camera", "Missing \"camera\"; the director uses orbit.");
        else if (!TryString(s["camera"], out camera)) Error($"{p}.camera", "\"camera\" must be a string.");
        else if (!Cameras.Contains(camera)) Error($"{p}.camera", $"Unknown camera \"{camera}\". Use one of: {string.Join(", ", Cameras)}.");

        string target = "host";
        if (s["target"] is null) Warn($"{p}.target", "Missing \"target\"; the director uses host.");
        else if (!TryString(s["target"], out target)) Error($"{p}.target", "\"target\" must be a string.");
        else if (!IsValidTarget(target))
            Error($"{p}.target", $"Unknown target \"{target}\". {TargetHelp}");

        double? fov = null;
        if (s["fov"] is not null)
        {
            if (!TryNumber(s["fov"], out var f)) Error($"{p}.fov", "\"fov\" must be a number of degrees.");
            else if (f <= 0 || f >= 180) Error($"{p}.fov", "\"fov\" must be between 0 and 180 degrees.");
            else
            {
                if (f < 10 || f > 120) Warn($"{p}.fov", $"fov {f} is unusual; 30-90 looks natural.");
                fov = f;
            }
        }
        double? hold = null;
        if (s["hold"] is not null)
        {
            if (!TryNumber(s["hold"], out var h)) Error($"{p}.hold", "\"hold\" must be a number of seconds.");
            else if (h < 0) Error($"{p}.hold", "\"hold\" cannot be negative.");
            else hold = h;
        }
        if (camera == "ride" || camera == "two_shot") CameraOptions(s, camera, p, Error, Warn);
        string? second = camera == "two_shot" && TryString(s["second"], out var sec) && IsValidTarget(sec) ? sec : null;
        return new Shot { Camera = camera, Target = target, Fov = fov, Hold = hold, Second = second };
    }

    // Types and ranges of the ride and two_shot options. The director reads them from the shot as written.
    private static void CameraOptions(JsonObject s, string camera, string p, Action<string, string> Error, Action<string, string> Warn)
    {
        foreach (var key in new[] { "offset", "look_dir", "look" })
            if (s[key] is not null && !IsVector(s[key]))
                Error($"{p}.{key}", $"\"{key}\" must be three numbers [x, y, z] in the unit's frame (x right, y up, z forward).");
        if (s["look_dir"] is JsonArray d && IsVector(d) && d.Sum(x => Math.Abs(x!.GetValue<double>())) < 1e-6)
            Error($"{p}.look_dir", "\"look_dir\" cannot be [0, 0, 0].");
        int looks = new[] { "look_dir", "look", "look_at" }.Count(k => s[k] is not null);
        if (looks > 1) Warn($"{p}", "Give only one of look_dir, look and look_at; the camera uses look_dir, then look, then look_at.");
        foreach (var key in new[] { "look_at", "second" })
        {
            if (s[key] is null) continue;
            if (!TryString(s[key], out var sel)) Error($"{p}.{key}", $"\"{key}\" must be a target string.");
            else if (!IsValidTarget(sel)) Error($"{p}.{key}", $"Unknown target \"{sel}\". {TargetHelp}");
        }
        if (s["roll"] is JsonNode roll)
        {
            if (TryNumber(roll, out var r)) { if (r < 0 || r > 1) Error($"{p}.roll", "A numeric \"roll\" must be between 0 (level) and 1 (unit)."); }
            else if (!TryString(roll, out var rs) || (rs != "unit" && rs != "level"))
                Error($"{p}.roll", "\"roll\" must be \"unit\", \"level\" or a number from 0 to 1.");
        }
        if (s["side"] is JsonNode side && (!TryString(side, out var sd) || (sd != "left" && sd != "right" && sd != "auto")))
            Error($"{p}.side", "\"side\" must be \"left\", \"right\" or \"auto\".");
        var numbers = CameraFields(camera).Where(k => k is not ("offset" or "look_dir" or "look" or "look_at" or "roll" or "second" or "side"));
        foreach (var key in numbers)
        {
            if (s[key] is null) continue;
            if (!TryNumber(s[key], out var v)) Error($"{p}.{key}", $"\"{key}\" must be a number.");
            else if (v < 0 && key is not ("azimuth" or "elevation")) Error($"{p}.{key}", $"\"{key}\" cannot be negative.");
            else if (key is "fov_min" or "fov_max" && (v <= 0 || v >= 180)) Error($"{p}.{key}", $"\"{key}\" must be between 0 and 180 degrees.");
        }
        if (TryNumber(s["fov_min"], out var lo) && TryNumber(s["fov_max"], out var hi) && lo > hi)
            Error($"{p}.fov_min", "\"fov_min\" is larger than \"fov_max\".");
        if (TryNumber(s["distance"], out var dist) && TryNumber(s["distance_max"], out var dmax) && dmax < dist)
            Warn($"{p}.distance_max", "\"distance_max\" is shorter than \"distance\"; the camera uses distance.");
        if (s["second"] is JsonNode sec && TryString(sec, out var secSel) && TryString(s["target"], out var tgt) && secSel == tgt)
            Warn($"{p}.second", "\"second\" is the same as \"target\"; the camera picks the unit the target is fighting instead.");
    }

    private static bool IsVector(JsonNode? node) =>
        node is JsonArray a && a.Count == 3 && a.All(x => TryNumber(x, out _));

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.GetValueKind() != JsonValueKind.Number) return false;
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
