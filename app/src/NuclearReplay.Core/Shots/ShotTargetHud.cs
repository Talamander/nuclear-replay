using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NuclearReplay.Core.Shots;

/// <summary>Checks the "target_hud" field of a shot list (at the root, the default for every shot) or of a shot
/// (replay/src/HudModel.cs, HudSettings.FromShot): the game's targeting HUD drawn over the replay camera.
/// A string is the mode; true is "story"; false is "off"; an object takes mode, focus, side, home, labels, arrows,
/// size and units.</summary>
public static class ShotTargetHud
{
    public const string Field = "target_hud";
    public static readonly string[] Modes = { "off", "story", "missiles", "all" };
    public static readonly string[] Fields = { "mode", "focus", "side", "home", "labels", "arrows", "size", "units" };
    public static readonly string[] Factions = { "Boscali", "Primeva" };
    public static readonly string[] UnitSystems = { "metric", "imperial" };
    public const double MinSize = 0.5, MaxSize = 2.5;

    public static void Validate(JsonNode? node, string p, Func<string, bool> isValidTarget, Action<string, string> error, Action<string, string> warn)
    {
        if (node is null) return;
        if (node is JsonValue v)
        {
            if (v.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return;
            if (v.TryGetValue<string>(out var s))
            {
                if (!Modes.Contains(s.Trim().ToLowerInvariant())) error(p, $"\"target_hud\" mode must be one of: {string.Join(", ", Modes)}.");
                return;
            }
        }
        if (node is not JsonObject o)
        {
            error(p, "\"target_hud\" must be a mode (\"story\", \"missiles\", \"all\", \"off\"), true, false or an object {\"mode\": \"story\", ...}.");
            return;
        }
        foreach (var key in o.Select(x => x.Key).Where(k => !Fields.Contains(k)))
            warn($"{p}.{key}", $"Unknown field \"{key}\" is ignored (target_hud takes {string.Join(", ", Fields)}).");
        string mode = "story";
        if (o["mode"] is not null)
        {
            if (!TryString(o["mode"], out var m) || !Modes.Contains(m.Trim().ToLowerInvariant()))
                error($"{p}.mode", $"\"mode\" must be one of: {string.Join(", ", Modes)}.");
            else mode = m.Trim().ToLowerInvariant();
        }
        if (o["focus"] is not null && (!TryString(o["focus"], out var focus) || !isValidTarget(focus)))
            error($"{p}.focus", "\"focus\" must be a target, like the shot's \"target\" (\"unit:221\", \"name:Talamander\", \"host\" ...).");
        if (o["side"] is not null)
        {
            if (!TryString(o["side"], out var side) || (!side.Equals("both", StringComparison.OrdinalIgnoreCase) && !IsFaction(side)))
                error($"{p}.side", $"\"side\" must be \"both\" or a faction: {string.Join(", ", Factions)}.");
        }
        if (o["home"] is not null && (!TryString(o["home"], out var home) || !IsFaction(home)))
            error($"{p}.home", $"\"home\" must be a faction: {string.Join(", ", Factions)}.");
        foreach (var key in new[] { "labels", "arrows" })
            if (o[key] is not null && (o[key] is not JsonValue b || b.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)))
                error($"{p}.{key}", $"\"{key}\" must be true or false.");
        if (o["arrows"] is not null && mode != "story")
            warn($"{p}.arrows", "\"arrows\" only shows in the story mode.");
        if (o["size"] is not null)
        {
            if (!TryNumber(o["size"], out var size)) error($"{p}.size", "\"size\" must be a number.");
            else if (size < MinSize || size > MaxSize) error($"{p}.size", $"\"size\" must be between {MinSize.ToString(CultureInfo.InvariantCulture)} and {MaxSize.ToString(CultureInfo.InvariantCulture)}.");
        }
        if (o["units"] is not null && (!TryString(o["units"], out var units) || !UnitSystems.Contains(units)))
            error($"{p}.units", "\"units\" must be \"metric\" or \"imperial\".");
    }

    /// <summary>The mode a target_hud value asks for (null when it is not a valid one).</summary>
    public static string? ModeOf(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.GetValueKind() == JsonValueKind.True) return "story";
            if (v.GetValueKind() == JsonValueKind.False) return "off";
            if (v.TryGetValue<string>(out var s) && Modes.Contains(s.Trim().ToLowerInvariant())) return s.Trim().ToLowerInvariant();
            return null;
        }
        if (node is JsonObject o) return o["mode"] is null ? "story" : TryString(o["mode"], out var m) && Modes.Contains(m.Trim().ToLowerInvariant()) ? m.Trim().ToLowerInvariant() : null;
        return null;
    }

    private static bool IsFaction(string s) => Factions.Any(f => f.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool TryString(JsonNode? node, out string value)
    {
        value = "";
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) { value = s; return true; }
        return false;
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<double>(out value)) return true;
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        return false;
    }
}
