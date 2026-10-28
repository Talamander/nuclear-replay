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

    internal JsonObject ToJsonObject(bool withTime = true)
    {
        var o = new JsonObject();
        if (withTime) o["t"] = Math.Round(T, 3);
        o["camera"] = Camera;
        o["target"] = Target;
        if (Fov.HasValue) o["fov"] = Fov.Value;
        if (Hold.HasValue) o["hold"] = Hold.Value;
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
    public static readonly string[] Cameras = { "orbit", "chase", "chase_cine", "action", "cockpit", "tv", "free", "relative" };
    public static readonly string[] FixedTargets = { "host", "auto", "latest_missile", "latest_host_missile" };
    private static readonly Regex UnitTarget = new(@"^unit:[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly string[] KnownShotFields = { "t", "camera", "target", "fov", "hold", "then" };

    public static bool IsValidTarget(string target) =>
        FixedTargets.Contains(target) || UnitTarget.IsMatch(target) || (target.StartsWith("name:", StringComparison.Ordinal) && target.Length > 5);

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
        foreach (var key in obj.Select(p => p.Key).Where(k => k != "shots" && k != "$schema" && k != "lighting"))
            Warn($"$.{key}", $"Unknown field \"{key}\" is ignored.");
        if (obj.ContainsKey("lighting")) ShotLightingFields.Root(obj["lighting"], Error, Warn);
        if (obj["shots"] is not JsonArray shots) { Error("$.shots", "Missing \"shots\" array."); return result; }
        if (shots.Count == 0) Warn("$.shots", "No shots; the director will use orbit/auto.");

        var list = new ShotList();
        double? previous = null;
        for (int i = 0; i < shots.Count; i++)
        {
            var p = $"$.shots[{i}]";
            if (shots[i] is not JsonObject s) { Error(p, "Each shot must be an object."); continue; }
            foreach (var key in s.Select(x => x.Key).Where(k => !KnownShotFields.Contains(k) && !ShotLightingFields.IsField(k)))
                Warn($"{p}.{key}", $"Unknown field \"{key}\" is ignored by the director.");

            double t = 0;
            if (s["t"] is null) Error($"{p}.t", "Missing \"t\" (seconds since the demo started).");
            else if (!TryNumber(s["t"], out t)) Error($"{p}.t", "\"t\" must be a number of seconds.");
            else if (t < 0) Error($"{p}.t", "\"t\" cannot be negative.");
            else if (demoDuration.HasValue && t > demoDuration.Value)
                Warn($"{p}.t", $"t={t.ToString(CultureInfo.InvariantCulture)} is after the end of the recording ({demoDuration.Value:F1}s).");
            if (previous.HasValue && t < previous.Value) Warn($"{p}.t", "Shots are not in time order; the director sorts them.");
            previous = t;

            var shot = Body(s, p, Error, Warn) with { T = t };
            ShotLightingFields.Shot(s, p, s["camera"] is null ? null : shot.Camera, Error, Warn);
            if (s["then"] is not null)
            {
                if (s["then"] is not JsonObject then) Error($"{p}.then", "\"then\" must be a shot object (camera, target, fov, hold).");
                else
                {
                    foreach (var key in then.Select(x => x.Key).Where(k => !KnownShotFields.Contains(k) || k == "then"))
                        Warn($"{p}.then.{key}", $"Field \"{key}\" is ignored inside \"then\".");
                    shot = shot with { Then = Body(then, $"{p}.then", Error, Warn) };
                }
            }
            list.Shots.Add(shot);
        }
        result.ShotList = list;
        return result;
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
            Error($"{p}.target", $"Unknown target \"{target}\". Use host, auto, latest_missile, latest_host_missile, unit:<id> or name:<text>.");

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
        return new Shot { Camera = camera, Target = target, Fov = fov, Hold = hold };
    }

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
