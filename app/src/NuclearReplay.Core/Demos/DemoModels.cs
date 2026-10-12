using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NuclearReplay.Core.Demos;

/// <summary>A unit reference from an event annotation (docs/PRODUCT-CONTRACT.md §1). Any field except id may be missing.</summary>
public sealed record UnitRef
{
    public long? Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? Faction { get; init; }
    public string? Player { get; init; }

    /// <summary>"Talamander (Revoker)", "Revoker", "unit 169".</summary>
    public string Label
    {
        get
        {
            if (!string.IsNullOrEmpty(Player) && !string.IsNullOrEmpty(Name))
            {
                // Player-flown units are often named "Player [Aircraft]" already.
                if (Name!.StartsWith(Player + " [", StringComparison.Ordinal) && Name.EndsWith(']'))
                    return $"{Player} ({Name[(Player!.Length + 2)..^1]})";
                if (Name.Contains(Player!, StringComparison.Ordinal)) return Name;
                return $"{Player} ({Name})";
            }
            if (!string.IsNullOrEmpty(Player)) return Player!;
            if (!string.IsNullOrEmpty(Name)) return Name!;
            return Id.HasValue ? $"unit {Id}" : "unknown";
        }
    }

    /// <summary>Shot list target for this unit: "unit:&lt;id&gt;" when the id is known.</summary>
    public string? ShotTarget => Id.HasValue ? $"unit:{Id.Value.ToString(CultureInfo.InvariantCulture)}" : null;

    public static UnitRef? From(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<long>(out var bareId)) return new UnitRef { Id = bareId };
        if (node is not JsonObject o) return null;
        return new UnitRef
        {
            Id = TryLong(o["id"]),
            Name = TryString(o["name"]),
            Type = TryString(o["type"]),
            Faction = TryString(o["faction"]),
            Player = TryString(o["player"]),
        };
    }

    internal static long? TryLong(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9) return (long)Math.Round(d);
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
        return null;
    }

    internal static string? TryString(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        return v.ToJsonString();
    }
}

/// <summary>One event annotation. Unknown events and fields are kept in <see cref="Data"/> and otherwise ignored.</summary>
public sealed class DemoEvent
{
    /// <summary>
    /// Replay time in seconds: record time minus the first record's time. Shot lists and render ranges use this clock.
    /// For a kill that carries a hit time (<see cref="HitAgo"/>, recorders from 0.5.1) this is the time of the hit,
    /// not of the kill feed record, so marks, clips and cuts land on the impact.
    /// </summary>
    public double T { get; init; }
    /// <summary>
    /// Kills only: seconds from the killer's last weapon hit on the victim to the kill feed ("hit_ago" in the
    /// annotation). Null for older recordings and for kills without a known hit; <see cref="T"/> is then the kill feed time.
    /// Serialized so the summary cache (DemoLibrary) keeps it.
    /// </summary>
    public double? HitAgo { get; init; }
    /// <summary>Replay time of the annotation record itself (for a kill, the game's kill feed).</summary>
    [JsonIgnore] public double LoggedT => T + (HitAgo ?? 0);
    public required string Event { get; init; }
    /// <summary>The annotation exactly as recorded.</summary>
    public required JsonObject Data { get; init; }

    [JsonIgnore] public UnitRef? Shooter => UnitRef.From(Data["shooter"]);
    [JsonIgnore] public UnitRef? Target => UnitRef.From(Data["target"]);
    [JsonIgnore] public UnitRef? Victim => UnitRef.From(Data["victim"]);
    [JsonIgnore] public UnitRef? Killer => UnitRef.From(Data["killer"]);
    [JsonIgnore] public UnitRef? Unit => UnitRef.From(Data["unit"]);
    [JsonIgnore] public string? Weapon => UnitRef.TryString(Data["weapon"]);
    [JsonIgnore] public long? Missile => UnitRef.TryLong(Data["missile"]);
    [JsonIgnore] public string? Name => UnitRef.TryString(Data["name"]);
    /// <summary>Map name, or null when the game only reported its generic scene name ("GameWorld").</summary>
    [JsonIgnore] public string? Map => UnitRef.TryString(Data["map"]) is { } m && !DemoAnalyzer.IsGenericMap(m) ? m : null;
    [JsonIgnore] public string? Faction => UnitRef.TryString(Data["faction"]);

    /// <summary>Gameplay moments a player can pick from (launches and kills).</summary>
    [JsonIgnore] public bool IsMoment => Event is "launch" or "kill";

    /// <summary>The unit the camera should follow for this moment.</summary>
    [JsonIgnore]
    public UnitRef? Subject => Event switch
    {
        "launch" => Shooter,
        "kill" => Killer ?? Victim,
        "player" => Unit,
        _ => null,
    };

    /// <summary>Plain-English one-liner, e.g. "Talamander (Revoker) fired AIM-120 at Ifrit".</summary>
    public string Summary => Event switch
    {
        "mission" => $"Mission {Name ?? "started"}" + (Map != null ? $" on {Map}" : ""),
        "player" => $"{Name ?? Unit?.Player ?? "A player"} joined" + (Faction != null ? $" {Faction}" : "") + (Unit?.Name != null ? $" in {Unit.Name}" : ""),
        "launch" => $"{Shooter?.Label ?? "Someone"} fired {Weapon ?? "a weapon"}" + (Target != null ? $" at {Target.Label}" : ""),
        "kill" => Killer != null
            ? $"{Killer.Label} destroyed {Victim?.Label ?? "a unit"}" + (Weapon != null ? $" with {Weapon}" : "")
            : $"{Victim?.Label ?? "A unit"} was destroyed",
        "ghost_authenticated" => "Recording started",
        "ghost_scene_ready" => "Map loaded",
        "fault" => "Recorder problem: " + (UnitRef.TryString(Data["where"]) ?? "unknown"),
        "end" => "Recording ended" + (UnitRef.TryString(Data["reason"]) is { } r ? $" ({r.Replace('_', ' ')})" : ""),
        _ => Event,
    };

    public static DemoEvent? Parse(double replayTime, string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var name = UnitRef.TryString(o["event"]);
            if (string.IsNullOrEmpty(name)) return null;
            double? hitAgo = name == "kill" ? ValidHitAgo(o["hit_ago"]) : null;
            if (hitAgo is { } ago) return new DemoEvent { T = Math.Max(0, replayTime - ago), HitAgo = Math.Min(ago, replayTime), Event = name!, Data = o };
            return new DemoEvent { T = replayTime, Event = name!, Data = o };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Longest gap between a hit and its kill feed that readers accept (the recorder writes up to 20 s).</summary>
    public const double MaxHitAgo = 30;

    private static double? ValidHitAgo(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<double>(out var ago)) return null;
        return double.IsFinite(ago) && ago >= 0 && ago <= MaxHitAgo ? ago : null;
    }
}

/// <summary>What nr list and nr events show about a recording.</summary>
public sealed class DemoSummary
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public long SizeBytes { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public DateTimeOffset? CreatedUtc { get; init; }
    public string? Mission { get; init; }
    public string? Map { get; init; }
    public string? GameVersion { get; init; }
    public string? ModVersion { get; init; }
    /// <summary>SinglePlayer or Multiplayer, from the header.</summary>
    public string? GameState { get; init; }
    public int FormatVersion { get; init; }
    public double DurationS { get; init; }
    public long MessageCount { get; init; }
    public bool Truncated { get; init; }
    /// <summary>The recorder wrote its "end" event (the match finished cleanly).</summary>
    public bool Complete { get; init; }
    public string? EndReason { get; init; }
    public List<string> Players { get; init; } = new();
    public int Launches { get; init; }
    public int Kills { get; init; }
    /// <summary>Kills that pair with a missile launch at the victim by the killer (a missile that hit).</summary>
    public int MissileHits { get; init; }
    /// <summary>The recording has launch/kill/player/mission events (recorder with event annotations).</summary>
    public bool HasEvents { get; init; }
    public string? Error { get; init; }

    [JsonIgnore] public string Title => Mission ?? System.IO.Path.GetFileNameWithoutExtension(FileName);
}

public sealed class DemoDetails
{
    public required DemoSummary Summary { get; init; }
    public required JsonObject Header { get; init; }
    public required List<DemoEvent> Events { get; init; }
}

public static class DemoAnalyzer
{
    public static readonly string[] GameplayEvents = { "mission", "player", "launch", "kill" };

    /// <summary>Scene names the game uses for every map; they tell the viewer nothing.</summary>
    public static bool IsGenericMap(string? map) =>
        string.IsNullOrWhiteSpace(map) || map.Equals("GameWorld", StringComparison.OrdinalIgnoreCase);

    public static DemoDetails Read(string path)
    {
        var info = new FileInfo(path);
        var scan = DemoFile.Scan(path);
        return Build(path, info.Length, info.LastWriteTimeUtc, scan);
    }

    public static DemoDetails Build(string path, long size, DateTime modifiedUtc, DemoFile.ScanResult scan)
    {
        double origin = scan.FirstTime ?? 0;
        var events = new List<DemoEvent>();
        foreach (var (time, _, json) in scan.Annotations)
        {
            var e = DemoEvent.Parse(Math.Max(0, time - origin), json);
            if (e != null) events.Add(e);
        }
        // A kill placed at its hit can be earlier than events logged before its kill feed; keep time order (stable).
        events = events.OrderBy(e => e.T).ToList();
        var header = scan.Header;
        string? H(string key) => UnitRef.TryString(header[key]) is { Length: > 0 } s ? s : null;
        var mission = events.FirstOrDefault(e => e.Event == "mission");
        var end = events.LastOrDefault(e => e.Event == "end");
        var players = events.Where(e => e.Event == "player")
            .Select(e => e.Name ?? e.Unit?.Player).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
            .Distinct().ToList();
        DateTimeOffset? created = null;
        if (H("created_utc") is { } c && DateTimeOffset.TryParse(c, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            created = parsed;
        else if (TryParseStamp(System.IO.Path.GetFileName(path), out var stamp))
            created = stamp;
        var mapPath = H("map_key_path");
        var summary = new DemoSummary
        {
            Path = path,
            FileName = System.IO.Path.GetFileName(path),
            SizeBytes = size,
            ModifiedUtc = modifiedUtc,
            CreatedUtc = created,
            Mission = mission?.Name ?? H("mission_name"),
            Map = mission?.Map ?? (mapPath != null && !IsGenericMap(System.IO.Path.GetFileNameWithoutExtension(mapPath)) ? System.IO.Path.GetFileNameWithoutExtension(mapPath) : null),
            GameVersion = H("game_version"),
            ModVersion = H("mod_version"),
            GameState = H("game_state"),
            FormatVersion = scan.FormatVersion,
            DurationS = Math.Round(scan.Duration, 3),
            MessageCount = scan.MessageCount,
            Truncated = scan.Truncated,
            Complete = end != null,
            EndReason = end != null ? UnitRef.TryString(end.Data["reason"]) : null,
            Players = players,
            Launches = events.Count(e => e.Event == "launch"),
            Kills = events.Count(e => e.Event == "kill"),
            MissileHits = events.Count(e => e.Event == "kill" && NuclearReplay.Core.Shots.ShotPresets.Pair(e, events).Launch != null),
            HasEvents = events.Any(e => GameplayEvents.Contains(e.Event)),
        };
        return new DemoDetails { Summary = summary, Header = header, Events = events };
    }

    /// <summary>Recorder file names start with a UTC stamp: 20261004T021932Z-Escalation.ncdemo.</summary>
    public static bool TryParseStamp(string fileName, out DateTimeOffset value)
    {
        value = default;
        if (fileName.Length < 16) return false;
        return DateTimeOffset.TryParseExact(fileName[..16], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }
}
