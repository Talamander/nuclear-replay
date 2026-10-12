using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NuclearReplay.Playback
{
    // nr's camera styles (app/src/NuclearReplay.Core/Shots/ShotPresets.cs) for the in-game viewer.
    // Same shots, same pairing of a launch with its kill; keep the two in step when a style changes.
    internal static class ViewerPresets
    {
        public static readonly string[] Ids = { "cinematic_chase", "victim_cut", "missile_follow", "chase", "orbit", "flyby", "cockpit" };
        public static readonly string[] Labels = { "Cinematic chase", "Victim cut", "Missile follow", "Chase", "Orbit", "Flyby", "Cockpit" };

        public static string Label(string id)
        {
            int i = Array.IndexOf(Ids, id);
            return i >= 0 ? Labels[i] : id;
        }

        private static JObject Shot(double t, string camera, string target, double? fov = null, double? hold = null, JObject then = null)
        {
            var o = new JObject { ["t"] = Math.Round(t, 3), ["camera"] = camera, ["target"] = target };
            if (fov.HasValue) o["fov"] = fov.Value;
            if (hold.HasValue) o["hold"] = hold.Value;
            if (then != null) o["then"] = then;
            return o;
        }

        // Shot list for a clip [from, to] around a moment (null: a hand-picked time) in one style.
        public static JArray Build(string style, ViewerEvent moment, double from, double to, IList<ViewerEvent> events)
        {
            var shots = new List<JObject>();
            string subject = ViewerEvent.ShotTarget(moment?.Subject) ?? "host";
            string free = moment == null ? "auto" : subject;
            double eventT = moment?.T ?? from;
            string victimOrFree = moment?.Event == "kill" ? ViewerEvent.ShotTarget(moment.Victim) ?? free : free;
            switch ((style ?? "").ToLowerInvariant())
            {
                case "chase":
                    shots.Add(Shot(from, "chase", free));
                    break;
                case "orbit":
                    shots.Add(Shot(from, "orbit", victimOrFree, 50));
                    break;
                case "flyby":
                    shots.Add(Shot(from, "tv", moment?.Event == "kill" ? ViewerEvent.ShotTarget(moment.Victim) ?? subject : subject));
                    break;
                case "cockpit":
                    shots.Add(Shot(from, "cockpit", subject));
                    break;
                case "missile_follow":
                case "victim_cut":
                    MissileShots(shots, style == "victim_cut", moment, from, to, events);
                    break;
                default:
                    if (moment?.Event == "launch")
                    {
                        shots.Add(Shot(from, "chase", subject, 50));
                        string missile = moment.Missile is long id ? "unit:" + id : "latest_missile";
                        shots.Add(Shot(Math.Max(from, eventT + 0.4), "chase", missile, 60, 3));
                    }
                    else if (moment?.Event == "kill")
                    {
                        string victim = ViewerEvent.ShotTarget(moment.Victim);
                        shots.Add(Shot(from, "chase", subject, 50));
                        if (victim != null && victim != subject)
                            shots.Add(Shot(Math.Max(from, eventT - KillCutLead(moment)), "orbit", victim, 45, Math.Max(3, to - eventT)));
                    }
                    else shots.Add(Shot(from, "chase", free, 55));
                    break;
            }
            var ordered = shots.Where((x, i) => i == 0 || (double)x["t"] < to).OrderBy(x => (double)x["t"]);
            return new JArray(ordered);
        }

        // Seconds before a kill's time to cut to the victim (ShotPresets.KillCutLead): just ahead of the impact
        // when the kill has its hit time, about the hit-to-kill-feed delay when it only has the kill feed.
        public static double KillCutLead(ViewerEvent kill) => kill.HitAgo != null ? 0.1 : 2.5;

        // Launch and kill of the same missile, as far as the annotations tell (ShotPresets.Pair).
        public static void Pair(ViewerEvent moment, IList<ViewerEvent> events, out ViewerEvent launch, out ViewerEvent kill)
        {
            launch = null; kill = null;
            if (moment == null || events == null) return;
            if (moment.Event == "launch")
            {
                launch = moment;
                long? targetId = ViewerEvent.Id(moment.Target), shooterId = ViewerEvent.Id(moment.Shooter);
                if (targetId == null) return;
                kill = events.FirstOrDefault(e => e.Event == "kill" && e.T >= moment.T && e.T <= moment.T + 90 && ViewerEvent.Id(e.Victim) == targetId
                    && (ViewerEvent.Id(e.Killer) == null || shooterId == null || ViewerEvent.Id(e.Killer) == shooterId));
            }
            else if (moment.Event == "kill")
            {
                kill = moment;
                long? victimId = ViewerEvent.Id(moment.Victim), killerId = ViewerEvent.Id(moment.Killer);
                if (victimId == null) return;
                launch = events.LastOrDefault(e => e.Event == "launch" && e.T <= moment.T && e.T >= moment.T - 90 && ViewerEvent.Id(e.Target) == victimId
                    && (killerId == null || ViewerEvent.Id(e.Shooter) == killerId) && e.Missile != null);
            }
        }

        private static void MissileShots(List<JObject> shots, bool cutToVictim, ViewerEvent moment, double from, double to, IList<ViewerEvent> events)
        {
            if (moment == null)
            {
                shots.Add(Shot(from, "chase", "latest_missile", 60, 3));
                return;
            }
            Pair(moment, events, out var launch, out var kill);
            string shooter = ViewerEvent.ShotTarget(launch?.Shooter ?? kill?.Killer ?? moment.Subject) ?? "host";
            string missile = launch?.Missile is long id ? "unit:" + id : "latest_missile";
            string victim = ViewerEvent.ShotTarget(kill?.Victim ?? launch?.Target);
            bool victimCut = cutToVictim && kill != null && victim != null;
            JObject MissileShot(double t) => Shot(t, "chase", missile, 60, victimCut ? 0 : Math.Max(3, to - from),
                victimCut ? new JObject { ["camera"] = "orbit", ["target"] = victim, ["fov"] = 50, ["hold"] = Math.Max(3, to - from) } : null);
            if (launch != null && launch.T + 0.4 > from + 0.2)
            {
                shots.Add(Shot(from, "chase", shooter, 50));
                shots.Add(MissileShot(launch.T + 0.4));
            }
            else if (launch != null || moment.Event == "launch") shots.Add(MissileShot(from));
            else shots.Add(Shot(from, "chase", shooter, 50));
            if (cutToVictim && kill != null && victim != null && kill.T > from)
                shots.Add(Shot(Math.Max(from, kill.T - 0.1), "orbit", victim, 50, Math.Max(3, to - kill.T)));
        }
    }
}
