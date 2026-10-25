using NuclearReplay.Core.Demos;

namespace NuclearReplay.Core.Shots;

public sealed record CameraStyle(string Id, string Label, string Description);

public sealed record ResolutionPreset(string Id, string Label, int Width, int Height);

/// <summary>Ready-made camera styles and output sizes for people who do not write shot lists.</summary>
public static class ShotPresets
{
    /// <summary>
    /// Camera styles for the GUI and <c>nr render --style</c>. Every style only uses cameras the shot list
    /// validator accepts (orbit, chase, cockpit, tv, action), so a generated list can always be saved, edited and re-used.
    /// </summary>
    public static readonly CameraStyle[] CameraStyles =
    {
        new("cinematic_chase", "Cinematic chase", "A smooth low-side chase on the shooter, then rides the missile to the hit."),
        new("victim_cut", "Victim cut", "Rides the missile, then cuts to the target going down at impact."),
        new("missile_follow", "Missile follow", "Stays on the missile from launch to impact, then holds on the hit."),
        new("chase", "Chase", "Behind the aircraft, steady."),
        new("orbit", "Orbit", "Circles the aircraft (the victim on a kill)."),
        new("flyby", "Flyby", "A spotter camera watching the action pass by."),
        new("cockpit", "Cockpit", "From the pilot's seat."),
        new("action", "Action cam", "Behind the aircraft on a springy arm: swings out in turns, drops back under thrust, leads into the turn."),
    };

    /// <summary>Style ids as a sentence, for error messages ("chase, orbit or cockpit").</summary>
    public static string StyleList => string.Join(", ", CameraStyles[..^1].Select(c => c.Id)) + " or " + CameraStyles[^1].Id;

    public static readonly ResolutionPreset[] Resolutions =
    {
        new("1080p", "1080p widescreen (1920x1080)", 1920, 1080),
        new("vertical", "Vertical for Shorts/TikTok (1080x1920)", 1080, 1920),
        new("4k", "4K (3840x2160)", 3840, 2160),
    };

    public static ResolutionPreset? Resolution(string id) =>
        Resolutions.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    public static CameraStyle? Style(string id) =>
        CameraStyles.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Clip range around a moment: [t - pre, t + post], clamped to the recording.</summary>
    public static (double From, double To) RangeAround(double t, double pre, double post, double duration)
    {
        double from = Math.Max(0, t - pre);
        double to = t + post;
        if (duration > 0) to = Math.Min(duration, to);
        if (to <= from) to = from + Math.Max(1, post);
        return (Math.Round(from, 2), Math.Round(to, 2));
    }

    /// <summary>
    /// Shot list for one clip in a camera style. <paramref name="moment"/> may be null for a hand-picked time.
    /// <paramref name="events"/> (all events of the recording) lets the missile styles pair a launch with its kill:
    /// kill annotations carry no missile id, and only kills from recorders since 0.5.1 carry the impact time.
    /// </summary>
    public static ShotList Build(string styleId, DemoEvent? moment, double from, double to, IReadOnlyList<DemoEvent>? events = null)
    {
        var shots = new List<Shot>();
        string subject = moment?.Subject?.ShotTarget ?? "host";
        string free = moment == null ? "auto" : subject;
        double eventT = moment?.T ?? from;
        switch (styleId.ToLowerInvariant())
        {
            case "chase":
                shots.Add(new Shot { T = from, Camera = "chase", Target = free });
                break;
            case "orbit":
                shots.Add(new Shot { T = from, Camera = "orbit", Target = moment?.Event == "kill" ? moment.Victim?.ShotTarget ?? free : free, Fov = 50 });
                break;
            case "flyby":
                shots.Add(new Shot { T = from, Camera = "tv", Target = moment?.Event == "kill" ? moment.Victim?.ShotTarget ?? subject : subject });
                break;
            case "cockpit":
                shots.Add(new Shot { T = from, Camera = "cockpit", Target = subject });
                break;
            case "action":
                shots.Add(new Shot { T = from, Camera = "action", Target = free });
                break;
            case "missile_follow":
            case "victim_cut":
                MissileShots(shots, styleId.Equals("victim_cut", StringComparison.OrdinalIgnoreCase), moment, from, to, events);
                break;
            default: // cinematic_chase
                if (moment?.Event == "launch")
                {
                    shots.Add(new Shot { T = from, Camera = "chase_cine", Target = subject, Fov = 50 });
                    var missile = moment.Missile is { } id ? $"unit:{id}" : "latest_missile";
                    shots.Add(new Shot { T = Math.Max(from, eventT + 0.4), Camera = "chase", Target = missile, Fov = 60, Hold = 3 });
                }
                else if (moment?.Event == "kill")
                {
                    var victim = moment.Victim?.ShotTarget;
                    shots.Add(new Shot { T = from, Camera = "chase_cine", Target = subject, Fov = 50 });
                    // With a recorded hit the kill time is the impact: cut to the victim just before it. Older
                    // recordings only have the kill feed, which comes about 2.5 s after the impact.
                    if (victim != null && victim != subject)
                        shots.Add(new Shot { T = Math.Max(from, eventT - KillCutLead(moment)), Camera = "orbit", Target = victim, Fov = 45, Hold = Math.Max(3, to - eventT) });
                }
                else
                {
                    shots.Add(new Shot { T = from, Camera = "chase_cine", Target = free, Fov = 50 });
                }
                break;
        }
        // Shots must be in time order and inside the clip; drop any that a short clip pushed past its end.
        shots = shots.Where((x, i) => i == 0 || x.T < to).OrderBy(x => x.T).ToList();
        return new ShotList { Shots = shots };
    }

    /// <summary>
    /// Seconds before a kill's time to cut to the victim: just ahead of the impact when the kill carries its hit
    /// time, about the usual hit-to-kill-feed delay when it only has the kill feed (recordings before 0.5.1).
    /// </summary>
    public static double KillCutLead(DemoEvent kill) => kill.HitAgo != null ? 0.1 : 2.5;

    /// <summary>Launch and kill of the same missile, as far as the annotations tell.</summary>
    public sealed record MissilePair(DemoEvent? Launch, DemoEvent? Kill);

    /// <summary>
    /// Pairs a moment with the other half of its missile shot: for a launch, the kill of its target by the same
    /// shooter within 90 s; for a kill, the latest launch at the victim by the killer before it.
    /// </summary>
    public static MissilePair Pair(DemoEvent moment, IReadOnlyList<DemoEvent>? events)
    {
        if (moment.Event == "launch")
        {
            var targetId = moment.Target?.Id;
            var shooterId = moment.Shooter?.Id;
            var kill = targetId == null || events == null ? null : events.FirstOrDefault(e => e.Event == "kill" && e.T >= moment.T && e.T <= moment.T + 90
                && e.Victim?.Id == targetId && (e.Killer?.Id == null || shooterId == null || e.Killer.Id == shooterId));
            return new MissilePair(moment, kill);
        }
        if (moment.Event == "kill")
        {
            var victimId = moment.Victim?.Id;
            var killerId = moment.Killer?.Id;
            var launch = victimId == null || events == null ? null : events.LastOrDefault(e => e.Event == "launch" && e.T <= moment.T && e.T >= moment.T - 90
                && e.Target?.Id == victimId && (killerId == null || e.Shooter?.Id == killerId) && e.Missile != null);
            return new MissilePair(launch, moment);
        }
        return new MissilePair(null, null);
    }

    private static void MissileShots(List<Shot> shots, bool cutToVictim, DemoEvent? moment, double from, double to, IReadOnlyList<DemoEvent>? events)
    {
        if (moment == null)
        {
            shots.Add(new Shot { T = from, Camera = "chase", Target = "latest_missile", Fov = 60, Hold = 3 });
            return;
        }
        var (launch, kill) = Pair(moment, events);
        var shooter = (launch?.Shooter ?? kill?.Killer ?? moment.Subject)?.ShotTarget ?? "host";
        var missile = launch?.Missile is { } id ? $"unit:{id}" : "latest_missile";
        var victim = (kill?.Victim ?? launch?.Target)?.ShotTarget;

        // Shooter until the missile is off the rail, then the missile itself. The kill is logged when the victim
        // dies, which can be seconds after the hit, so the hit itself comes from the director: when the missile
        // is gone, victim_cut cuts to the victim at once ("then"), and missile_follow holds the view on the
        // explosion (the game's chase camera would swing to the sky) until the end of the clip.
        var victimCut = cutToVictim && kill != null && victim != null;
        var missileShot = new Shot
        {
            Camera = "chase", Target = missile, Fov = 60,
            Hold = victimCut ? 0 : Math.Max(3, to - from),
            Then = victimCut ? new Shot { Camera = "orbit", Target = victim!, Fov = 50, Hold = Math.Max(3, to - from) } : null,
        };
        if (launch != null && launch.T + 0.4 > from + 0.2)
        {
            shots.Add(new Shot { T = from, Camera = "chase", Target = shooter, Fov = 50 });
            shots.Add(missileShot with { T = launch.T + 0.4 });
        }
        else if (launch != null || moment.Event == "launch")
            shots.Add(missileShot with { T = from });
        else
            shots.Add(new Shot { T = from, Camera = "chase", Target = shooter, Fov = 50 });

        // At the kill (the impact when the recording has the hit time, else the kill feed): the victim going down,
        // not the missile's explosion. After a "then" cut this repeats the shot on screen and the director keeps
        // it; it matters when the missile was not followed to the hit.
        if (cutToVictim && kill != null && victim != null && kill.T > from)
            shots.Add(new Shot { T = Math.Max(from, kill.T - 0.1), Camera = "orbit", Target = victim, Fov = 50, Hold = Math.Max(3, to - kill.T) });
    }
}
