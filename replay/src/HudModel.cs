#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NuclearReplay.Playback
{
    // Target HUD over replay cameras (design: 2026-10-30-hud-overlay/DESIGN.md): which units get a marker, which a
    // label, which lines show who is targeting whom, and the settings behind it (the viewer's F4 panel, shot lists'
    // "target_hud"). Unity-free, so NuclearReplay.Core.Tests runs it; HudOverlay.cs gathers the units and draws the
    // result with the game's own HUD sprites, colours and font.
    //
    // What a replay client knows (replicated to every client, so the recording has it):
    //   a missile's target, shooter and seeker state (Missile._targetID, ownerID, seekerMode SyncVars);
    //   each unit's designated targets (Unit.RpcSetStationTargets: an aircraft's target list, a SAM's or ship's
    //   turret targets), the "lock" of this game;
    //   every unit's side, type, position and whether it is destroyed.
    // What it does not have: one pilot's radar picture, stale tracks and RWR warnings (kept on the pilot's machine).
    // So the overlay is omniscient: true positions, every lock.
    //
    // Modes:
    //   off       nothing
    //   story     the focus unit's fight in full (its targets, its missiles and their targets, missiles at it and
    //             their shooters), with lines; everyone else within 15 km as quiet small marks, no labels
    //   missiles  every missile in flight, its target and its line; launchers marked while their missile flies
    //   all       every unit within range, all missile lines, no designation lines
    // Focus: the shot's target; a missile camera's focus is the missile's shooter, so the whole engagement shows.
    // Colours: friend or foe relative to one home side for the whole replay (the recording player's side), so a cut
    // to an enemy jet does not flip the colours.
    internal enum HudSide { Friendly, Hostile, Neutral }

    // Why a unit is in the cast, weakest first (label priority runs the other way).
    internal enum HudRole { Context = 0, Shooter = 1, Target = 2, Primary = 3, FocusMissile = 4, Threat = 5, Focus = 6 }

    // How a marker is drawn: the unit's full HUD icon, a small icon, or the game's minimized pip.
    internal enum HudLook { Full, Small, Pip }

    internal enum HudLabelSide { Right, Left, Above, Below }

    internal enum HudLineKind { Missile, Designation }

    // One unit as the overlay sees it this frame.
    internal sealed class HudUnit
    {
        public string Id;
        public UnitKind Kind;
        public string Side;           // faction name, null for none
        public string Code;           // short type code ("T/A-30", "AGR-18")
        public string Type;           // type name ("StratoLance R9 Launcher")
        public string Player;         // player name, null for AI
        public double X, Y;           // screen pixels from the bottom left
        public double Depth;          // metres in front of the camera (negative behind)
        public double Distance;       // metres from the camera
        public double PX, PY, PZ;     // world position, metres
        public double ScreenSize;     // the unit's size on screen, pixels
        public double Age = 99;       // seconds since the overlay first saw it
        public double DeadFor = -1;   // seconds since it was destroyed; -1 while alive
        public string TargetId;       // missile: the unit it guides on
        public string OwnerId;        // missile: who fired it
        public string Seeker;         // missile: "active" (own seeker locked), "search", "passive"; null when unknown
        public List<string> Targets;  // its designated targets, primary first
        public double TargetsAge = 99;// seconds since its primary target changed
        public bool WasCast;          // in last frame's cast (a destroyed unit keeps its place for the kill flash)
        public object Unit;           // the game's unit (opaque here)

        public bool Alive => DeadFor < 0;
        public bool InFront => Depth > 0.5;
        public double DistanceTo(HudUnit o) => Math.Sqrt((PX - o.PX) * (PX - o.PX) + (PY - o.PY) * (PY - o.PY) + (PZ - o.PZ) * (PZ - o.PZ));
    }

    internal sealed class HudMark
    {
        public HudUnit Unit;
        public HudSide Side;
        public HudRole Role;
        public HudLook Look;
        public double Size = 1;       // multiplier on the marker size (the game's shrink with distance)
        public double Alpha = 1;
        public double Flash;          // 0..1: share of the game's Warning colour mixed in (new contacts, kills)
        public bool Box;              // the game's target box around it (designated targets)
        public bool Arrow;            // off screen: an arrow on the safe-area edge points at it
        public string Label;          // null: no label
        public HudLabelSide LabelAt;  // where the label sits: right of the marker, else left, above or below (to clear others)
        public int Salvo;             // missiles grouped under this one's label
        public override string ToString() => $"{Unit.Id} {Role} {Side} {Look}{(Box ? " box" : "")}{(Arrow ? " arrow" : "")}{(Label != null ? " \"" + Label.Replace("\n", " / ") + "\"" : "")}";
    }

    internal sealed class HudLine
    {
        public HudUnit From, To;
        public HudLineKind Kind;
        public HudSide Side;          // side of the unit at the From end
        public bool Solid;            // a missile whose own seeker has the target; else dashed
        public double Alpha = 1;
        public override string ToString() => $"{Kind} {From.Id}->{To.Id}{(Solid ? " solid" : "")}";
    }

    internal sealed class HudFrame
    {
        public readonly List<HudMark> Marks = new List<HudMark>();
        public readonly List<HudLine> Lines = new List<HudLine>();
        public HudUnit Focus;
        public string Home;           // the side drawn friendly
        public int Hidden;            // units in range left out (subject rule, budget)
        public HudMark Find(string id) => Marks.FirstOrDefault(m => m.Unit.Id == id);
    }

    // The frame the overlay draws into.
    internal struct HudView
    {
        public double Width, Height;  // pixels
        public double Scale => Math.Min(Width, Height) / 1080.0;
        public bool Vertical => Height > Width;
        public HudView(double w, double h) { Width = w; Height = h; }

        // Labels and arrows stay inside this box: 4 % margins in 16:9; in 9:16 x 6-90 % and y 12-75 % from the top
        // (clear of YouTube Shorts' title, buttons and caption band). Markers may sit anywhere.
        public void Safe(out double x0, out double y0, out double x1, out double y1)
        {
            if (Vertical) { x0 = 0.06 * Width; x1 = 0.90 * Width; y0 = (1 - 0.75) * Height; y1 = (1 - 0.12) * Height; }
            else { x0 = 0.04 * Width; x1 = 0.96 * Width; y0 = 0.04 * Height; y1 = 0.96 * Height; }
        }
    }

    internal sealed class HudSettings
    {
        public static readonly string[] Modes = { "off", "story", "missiles", "all" };
        public static readonly string[] ModeLabels = { "Off", "Story", "Missiles", "All" };
        public static readonly string[] UnitSystems = { "metric", "imperial" };

        public string Mode = "off";
        public string Focus;          // a target selector; null: the shot's target (a missile: its shooter)
        public string Side = "both";  // "both" or a faction name: only that side's units, its missiles and their targets
        public string Home;           // the faction drawn friendly; null: the recording player's
        public bool Labels = true;
        public bool Arrows = true;    // story only
        public double Size = 1;       // 0.5 to 2.5
        public string Units = "metric";

        public const double MinSize = 0.5, MaxSize = 2.5;
        public bool On => Mode != "off";

        public HudSettings Clone() => (HudSettings)MemberwiseClone();

        public static string ModeLabel(string mode)
        {
            int i = Array.IndexOf(Modes, mode);
            return i >= 0 ? ModeLabels[i] : mode;
        }

        public static string NextMode(string mode, int step, bool skipOff = false)
        {
            int n = Modes.Length, i = Array.IndexOf(Modes, mode);
            if (i < 0) i = 0;
            do i = ((i + step) % n + n) % n; while (skipOff && Modes[i] == "off");
            return Modes[i];
        }

        public string Summary()
        {
            if (!On) return "off";
            var parts = new List<string> { ModeLabel(Mode) };
            if (!string.IsNullOrEmpty(Focus)) parts.Add("focus " + Focus);
            if (Side != "both") parts.Add("side " + Side);
            if (!string.IsNullOrEmpty(Home)) parts.Add("home " + Home);
            if (!Labels) parts.Add("no labels");
            if (!Arrows) parts.Add("no arrows");
            if (Math.Abs(Size - 1) > 0.001) parts.Add("size " + HudModel.Num(Size));
            if (Units != "metric") parts.Add(Units);
            return string.Join(", ", parts);
        }

        // ---- shot lists: "target_hud": "story" | "off" | true | false | { "mode": ..., ... } ----
        // Values arrive as plain objects (bool, string, long, double, IDictionary<string, object>): the plugin turns
        // the shot list's JSON into these (HudShotJson), so this file needs no JSON library and the tests can run it.

        public const string Field = "target_hud";
        public static readonly string[] Fields = { "mode", "focus", "side", "home", "labels", "arrows", "size", "units" };

        // The settings a shot asks for: the root "target_hud" (the default for every shot), then the shot's own keys.
        // Null when neither mentions the overlay, so the shot renders exactly as before.
        public static HudSettings FromShot(object shot, object root)
        {
            if (shot == null && root == null) return null;
            var s = new HudSettings();
            if (root != null) Apply(s, root);
            if (shot != null) Apply(s, shot);
            return s;
        }

        public static void Apply(HudSettings s, object token)
        {
            if (token == null) return;
            if (token is bool b) { s.Mode = b ? (s.On ? s.Mode : "story") : "off"; return; }
            if (token is string str)
            {
                string v = str.Trim().ToLowerInvariant();
                if (Array.IndexOf(Modes, v) >= 0) s.Mode = v;
                return;
            }
            if (!(token is IDictionary<string, object> o)) return;
            string mode = Str(o, "mode")?.Trim().ToLowerInvariant();
            s.Mode = mode != null && Array.IndexOf(Modes, mode) >= 0 ? mode : s.On ? s.Mode : "story";
            if (o.ContainsKey("focus")) s.Focus = Str(o, "focus");
            string side = Str(o, "side");
            if (side != null) s.Side = side.Trim().Equals("both", StringComparison.OrdinalIgnoreCase) ? "both" : side.Trim();
            if (Str(o, "home") != null) s.Home = Str(o, "home").Trim();
            if (o.TryGetValue("labels", out var labels) && labels is bool lb) s.Labels = lb;
            if (o.TryGetValue("arrows", out var arrows) && arrows is bool ab) s.Arrows = ab;
            if (o.TryGetValue("size", out var size) && Number(size, out double sz)) s.Size = Clamp(sz, MinSize, MaxSize);
            string units = Str(o, "units")?.Trim().ToLowerInvariant();
            if (units != null && Array.IndexOf(UnitSystems, units) >= 0) s.Units = units;
        }

        // The settings as a shot-list object (only what differs from the defaults, mode always).
        public IDictionary<string, object> ToShot()
        {
            var o = new Dictionary<string, object> { ["mode"] = Mode };
            if (!string.IsNullOrEmpty(Focus)) o["focus"] = Focus;
            if (Side != "both") o["side"] = Side;
            if (!string.IsNullOrEmpty(Home)) o["home"] = Home;
            if (!Labels) o["labels"] = false;
            if (!Arrows) o["arrows"] = false;
            if (Math.Abs(Size - 1) > 0.001) o["size"] = Math.Round(Size, 2);
            if (Units != "metric") o["units"] = Units;
            return o;
        }

        private static string Str(IDictionary<string, object> o, string key) => o.TryGetValue(key, out var v) ? v as string : null;

        private static bool Number(object v, out double d)
        {
            switch (v)
            {
                case double x: d = x; return true;
                case float f: d = f; return true;
                case long l: d = l; return true;
                case int i: d = i; return true;
                case decimal m: d = (double)m; return true;
                default: d = 0; return false;
            }
        }

        internal static double Clamp(double v, double lo, double hi) => double.IsNaN(v) ? lo : v < lo ? lo : v > hi ? hi : v;
    }

    internal static class HudModel
    {
        // Sizes at a 1080-pixel short side, times the size setting (DESIGN.md "Size").
        public const double MarkerPx = 26, TextPx = 24, LinePx = 2, ArrowPx = 30;
        public const double ContextRange = 15000, FadeFrom = 10000;   // non-cast markers fade 10-15 km, gone beyond
        public const double FriendlySmallRange = 4000;                 // story: friendlies beyond this are not drawn
        public const double GroundFullRange = 8000;                    // all: ground and sea full size within this
        public const double SubjectShare = 0.08;                       // on screen taller than this share: no marker
        public const double SalvoPx = 40;
        public const double NoDistanceUnder = 300;                     // a missile label drops its distance this close
        public const double FlashTime = 1, KillFlash = 0.5, KillFade = 1;

        public static HudFrame Build(HudSettings s, IList<HudUnit> units, HudUnit focus, HudView view, string home)
        {
            var frame = new HudFrame { Focus = focus, Home = home };
            if (s == null || !s.On || units == null) return frame;
            var byId = new Dictionary<string, HudUnit>();
            foreach (var u in units) if (u?.Id != null) byId[u.Id] = u;
            HudUnit Get(string id) => id != null && byId.TryGetValue(id, out var u) ? u : null;
            // A missile camera's focus is the missile's shooter, so the whole engagement shows.
            if (focus != null && focus.Kind == UnitKind.Missile && Get(focus.OwnerId) != null) focus = Get(focus.OwnerId);
            frame.Focus = focus;
            // What the focus unit is after: its designated targets and its missiles' targets (these get the box).
            var focusTargets = new HashSet<string>();
            if (focus != null)
            {
                if (focus.Targets != null) foreach (var t in focus.Targets) focusTargets.Add(t);
                foreach (var m in units) if (m.Kind == UnitKind.Missile && m.Alive && m.OwnerId == focus.Id && m.TargetId != null) focusTargets.Add(m.TargetId);
            }

            // ---- the cast ----
            var roles = new Dictionary<HudUnit, HudRole>();
            void Give(HudUnit u, HudRole r) { if (u == null) return; if (!roles.TryGetValue(u, out var have) || r > have) roles[u] = r; }
            if (s.Mode == "story" && focus != null)
            {
                Give(focus, HudRole.Focus);
                if (focus.Targets != null)
                    for (int i = 0; i < focus.Targets.Count; i++) { var t = Get(focus.Targets[i]); if (t != null && t.Alive) Give(t, i == 0 ? HudRole.Primary : HudRole.Target); }
                foreach (var m in units)
                {
                    if (m.Kind != UnitKind.Missile || !m.Alive) continue;
                    if (m.OwnerId == focus.Id) { Give(m, HudRole.FocusMissile); Give(Get(m.TargetId), HudRole.Target); }
                    if (m.TargetId == focus.Id) { Give(m, HudRole.Threat); Give(Get(m.OwnerId), HudRole.Shooter); }
                }
            }
            else if (s.Mode == "missiles" || s.Mode == "all")
            {
                foreach (var m in units)
                {
                    if (m.Kind != UnitKind.Missile || !m.Alive) continue;
                    bool atFocus = focus != null && m.TargetId == focus.Id, byFocus = focus != null && m.OwnerId == focus.Id;
                    Give(m, atFocus ? HudRole.Threat : byFocus ? HudRole.FocusMissile : HudRole.Target);
                    var t = Get(m.TargetId);
                    if (t != null && t.Alive) Give(t, HudRole.Target);
                    Give(Get(m.OwnerId), HudRole.Shooter);
                }
                if (focus != null) Give(focus, HudRole.Focus);
            }
            // Destroyed units stay in the cast through their kill flash and fade if they were in it last frame
            // (the missile that hit them is gone, so nothing else keeps them there).
            if (s.Mode != "off")
                foreach (var u in units)
                    if (!u.Alive && u.WasCast && u.DeadFor <= KillFlash + KillFade && !roles.ContainsKey(u)) Give(u, HudRole.Target);

            // ---- side filter: that side's units, missiles it fired and their targets ----
            HashSet<HudUnit> sideKeep = null;
            if (!string.IsNullOrEmpty(s.Side) && s.Side != "both")
            {
                sideKeep = new HashSet<HudUnit>();
                foreach (var u in units)
                {
                    if (!SameSide(u.Side, s.Side)) continue;
                    sideKeep.Add(u);
                    if (u.Kind == UnitKind.Missile) { var t = Get(u.TargetId); if (t != null) sideKeep.Add(t); }
                }
            }

            // ---- markers ----
            var candidates = new List<HudMark>();
            foreach (var u in units)
            {
                if (u == null || u.Kind == UnitKind.Other) continue;
                if (!u.Alive && u.DeadFor > KillFlash + KillFade) continue;
                if (sideKeep != null && !sideKeep.Contains(u)) continue;
                bool cast = roles.TryGetValue(u, out var role);
                if (!cast)
                {
                    if (s.Mode == "missiles") continue;
                    if (u.Kind == UnitKind.Building) continue;                 // buildings only when they are a target
                    if (u.Kind == UnitKind.Missile && s.Mode != "all") continue;
                    if (u.Distance > ContextRange) continue;
                }
                var side = SideOf(u, home);
                var m = new HudMark { Unit = u, Role = cast ? role : HudRole.Context, Side = side };
                m.Look = HudLook.Full;
                if (!cast)
                {
                    m.Alpha = Fade(u.Distance);
                    if (s.Mode == "story")
                    {
                        if (side == HudSide.Friendly) { if (u.Distance > FriendlySmallRange) continue; m.Look = HudLook.Small; }
                        else m.Look = HudLook.Pip;
                    }
                    else if (s.Mode == "all" && (u.Kind == UnitKind.Ground || u.Kind == UnitKind.Sea) && u.Distance > GroundFullRange) m.Look = HudLook.Pip;
                }
                m.Size = DistanceScale(u.Distance);
                m.Box = cast && (role == HudRole.Primary || role == HudRole.Target) && focusTargets.Contains(u.Id);
                if (u.Kind == UnitKind.Missile) m.Flash = u.Age < FlashTime ? 1 - u.Age / FlashTime : 0;
                else if (m.Box && focus.TargetsAge < FlashTime && role == HudRole.Primary) m.Flash = 1 - focus.TargetsAge / FlashTime;
                if (!u.Alive)
                {
                    // Kill: flash for half a second, then fade out over a second.
                    m.Flash = u.DeadFor < KillFlash ? 1 : 0;
                    m.Alpha *= u.DeadFor < KillFlash ? 1 : Math.Max(0, 1 - (u.DeadFor - KillFlash) / KillFade);
                    m.Box = false;
                }
                bool onScreen = u.InFront && u.X >= 0 && u.X <= view.Width && u.Y >= 0 && u.Y <= view.Height;
                if (!onScreen)
                {
                    // Off screen: only the story's incoming missiles and primary target get an arrow (at most 2, below).
                    if (s.Mode == "story" && s.Arrows && u.Alive && (role == HudRole.Threat || role == HudRole.Primary) && cast) m.Arrow = true;
                    else continue;
                }
                // Subject rule: a unit that fills enough of the frame is plain to see; no marker or label on it.
                if (onScreen && u.ScreenSize > SubjectShare * view.Height) { frame.Hidden++; continue; }
                candidates.Add(m);
            }

            // Arrows: the nearest incoming missile(s) first, then the primary target; at most 2.
            var arrows = candidates.Where(c => c.Arrow).OrderByDescending(c => c.Role).ThenBy(c => focus != null ? c.Unit.DistanceTo(focus) : c.Unit.Distance).ToList();
            foreach (var a in arrows.Skip(2)) { candidates.Remove(a); frame.Hidden++; }

            candidates.Sort((a, b) => Priority(b, focus).CompareTo(Priority(a, focus)));
            frame.Marks.AddRange(candidates);

            if (s.Labels) PlaceLabels(s, frame, focus, view, byId);
            AddLines(s, frame, focus, byId, home);
            return frame;
        }

        public static bool SameSide(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        public static HudSide SideOf(HudUnit u, string home)
        {
            if (string.IsNullOrEmpty(u.Side) || string.IsNullOrEmpty(home)) return HudSide.Neutral;
            return SameSide(u.Side, home) ? HudSide.Friendly : HudSide.Hostile;
        }

        // The game's own shrink with distance (HUDUnitMarker.UpdateMaximized): full size to 12.5 km, 0.45 at 37.5 km.
        public static double DistanceScale(double metres)
        {
            double t = metres * 4E-05 - 0.5;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return 1 + (0.45 - 1) * t;
        }

        // Non-cast markers: full to 10 km, gone at 15 km.
        public static double Fade(double metres) => metres <= FadeFrom ? 1 : metres >= ContextRange ? 0 : 1 - (metres - FadeFrom) / (ContextRange - FadeFrom);

        // Label priority: missiles at the focus unit, the focus unit's missiles, its primary target, other targets,
        // shooters, then the nearest others.
        public static double Priority(HudMark m, HudUnit focus)
        {
            double p;
            switch (m.Role)
            {
                case HudRole.Threat: p = 6000; break;
                case HudRole.FocusMissile: p = 5000; break;
                case HudRole.Primary: p = 4000; break;
                case HudRole.Target: p = 3000; break;
                case HudRole.Shooter: p = 2000; break;
                case HudRole.Focus: p = 1500; break;
                default: p = 0; break;
            }
            if (m.Look != HudLook.Full) p -= 500;
            return p - Math.Min(999, m.Unit.Distance / 100.0);
        }

        private static void PlaceLabels(HudSettings s, HudFrame frame, HudUnit focus, HudView view, Dictionary<string, HudUnit> byId)
        {
            int budget = view.Vertical ? 4 : 8;
            double scale = view.Scale * s.Size;
            double h = TextPx * 1.25 * scale, gap = (MarkerPx * 0.6 + 6) * scale;
            view.Safe(out double sx0, out double sy0, out double sx1, out double sy1);
            var placed = new List<(double x0, double y0, double x1, double y1)>();
            // Other markers are obstacles too: a label never covers another unit's icon or box.
            var markers = new List<(HudMark m, (double x0, double y0, double x1, double y1) r)>();
            foreach (var o in frame.Marks)
            {
                if (o.Arrow || !o.Unit.InFront) continue;
                double half = MarkerPx * o.Size * scale * (o.Look == HudLook.Full ? (o.Box ? 0.75 : 0.5) : 0.3);
                markers.Add((o, (o.Unit.X - half, o.Unit.Y - half, o.Unit.X + half, o.Unit.Y + half)));
            }

            // Salvos: three or more missiles of one shooter within 40 px share the lead missile's label.
            var grouped = new HashSet<HudMark>();
            foreach (var m in frame.Marks)
            {
                if (m.Unit.Kind != UnitKind.Missile || grouped.Contains(m) || m.Arrow || m.Unit.OwnerId == null) continue;
                var near = frame.Marks.Where(o => o != m && !grouped.Contains(o) && !o.Arrow && o.Unit.Kind == UnitKind.Missile && o.Unit.OwnerId == m.Unit.OwnerId
                    && Math.Abs(o.Unit.X - m.Unit.X) < SalvoPx * scale && Math.Abs(o.Unit.Y - m.Unit.Y) < SalvoPx * scale).ToList();
                if (near.Count + 1 < 3) continue;
                m.Salvo = near.Count + 1;
                foreach (var o in near) grouped.Add(o);
            }

            int count = 0;
            foreach (var m in frame.Marks)
            {
                if (count >= budget) break;
                if (grouped.Contains(m) || !m.Unit.Alive) continue;
                if (m.Look != HudLook.Full) continue;                         // quiet context: never labelled
                if (m.Role == HudRole.Context && s.Mode != "all") continue;
                string text = LabelText(m, focus, byId, s.Units, view.Vertical);
                if (string.IsNullOrEmpty(text)) continue;
                double w = LabelWidth(text) * scale;
                int lines = text.Split('\n').Length;
                double lh = h * lines;
                double ax = m.Unit.X, ay = m.Unit.Y;
                if (m.Arrow) { m.Label = text; count++; continue; }           // arrows carry their label inward
                // Right of the marker, else left of it, else no label. A label must stay in the safe area and clear
                // every other label and every other marker.
                var right = (ax + gap, ay - lh / 2, ax + gap + w, ay + lh / 2);
                var left = (ax - gap - w, ay - lh / 2, ax - gap, ay + lh / 2);
                bool Free((double x0, double y0, double x1, double y1) r) => Inside(r, sx0, sy0, sx1, sy1)
                    && !placed.Any(p => Overlap(p, r)) && !markers.Any(o => o.m != m && !(m.Salvo >= 3 && grouped.Contains(o.m)) && Overlap(o.r, r));
                // Screen y runs up: above is the larger y.
                var above = (ax - w / 2, ay + gap, ax + w / 2, ay + gap + lh);
                var below = (ax - w / 2, ay - gap - lh, ax + w / 2, ay - gap);
                var spots = new[] { (right, HudLabelSide.Right), (left, HudLabelSide.Left), (above, HudLabelSide.Above), (below, HudLabelSide.Below) };
                foreach (var (r, side) in spots)
                {
                    if (!Free(r)) continue;
                    placed.Add(r); m.Label = text; m.LabelAt = side; count++;
                    break;
                }
            }
        }

        // Rough label width in pixels at scale 1 (the HUD font is about 0.55 em per character).
        public static double LabelWidth(string text) => text.Split('\n').Max(l => l.Length) * TextPx * 0.55 + 4;

        private static bool Inside((double x0, double y0, double x1, double y1) r, double sx0, double sy0, double sx1, double sy1) =>
            r.x0 >= sx0 && r.x1 <= sx1 && r.y0 >= sy0 && r.y1 <= sy1;

        private static bool Overlap((double x0, double y0, double x1, double y1) a, (double x0, double y0, double x1, double y1) b) =>
            a.x0 < b.x1 && b.x0 < a.x1 && a.y0 < b.y1 && b.y0 < a.y1;

        // Label text: short, at most two items.
        //   human aircraft: the player's name; AI aircraft: the type code ("T/A-30")
        //   ground, sea, building: the type name ("StratoLance R9 Launcher")
        //   missile: its code and the distance to its target ("AGR-18 · 2.4 km"), no distance under 300 m;
        //            a salvo: "AGR-18 ×10"
        //   the focus unit's primary target: its name and the range from the focus unit
        public static string LabelText(HudMark m, HudUnit focus, Dictionary<string, HudUnit> byId, string units, bool vertical)
        {
            var u = m.Unit;
            string name = Name(u);
            if (u.Kind == UnitKind.Missile)
            {
                string code = !string.IsNullOrEmpty(u.Code) ? u.Code.Trim() : "Missile";
                if (m.Salvo >= 3) return code + " ×" + m.Salvo;
                if (u.TargetId != null && byId != null && byId.TryGetValue(u.TargetId, out var t))
                {
                    double d = u.DistanceTo(t);
                    if (d >= NoDistanceUnder) return code + " · " + Distance(d, units);
                }
                return code;
            }
            if (m.Arrow || (m.Role == HudRole.Primary && focus != null && focus != u))
            {
                double d = focus != null && focus != u ? u.DistanceTo(focus) : u.Distance;
                return name + " · " + Distance(d, units);
            }
            return name;
        }

        public static string Name(HudUnit u)
        {
            if (!string.IsNullOrEmpty(u.Player)) return u.Player;
            if (u.Kind == UnitKind.Aircraft || u.Kind == UnitKind.Rotorcraft)
                return !string.IsNullOrEmpty(u.Code) ? u.Code.Trim() : u.Type?.Trim();
            return !string.IsNullOrEmpty(u.Type) ? u.Type.Trim() : u.Code?.Trim();
        }

        // The game's distance reading (UnitConverter.DistanceReading) with a space: "850 m", "4.2 km", "12 km";
        // imperial "900 yd", "2.3 nm".
        public static string Distance(double metres, string units)
        {
            var ic = CultureInfo.InvariantCulture;
            if (units == "imperial")
            {
                double yd = metres * 1.09361;
                return yd < 1000 ? yd.ToString("F0", ic) + " yd" : (metres * 0.000539957).ToString("F1", ic) + " nm";
            }
            if (metres > 10000) return (metres * 0.001).ToString("F0", ic) + " km";
            if (metres > 1000) return (metres * 0.001).ToString("F1", ic) + " km";
            return metres.ToString("F0", ic) + " m";
        }

        // Lines: each cast missile to its target, solid once its own seeker has it, dashed before. In story, a fainter
        // dashed line from the focus unit to its primary target while none of its missiles flies at that target.
        private static void AddLines(HudSettings s, HudFrame frame, HudUnit focus, Dictionary<string, HudUnit> byId, string home)
        {
            var flying = new HashSet<(string, string)>();
            foreach (var m in frame.Marks)
            {
                var u = m.Unit;
                if (u.Kind != UnitKind.Missile || !u.Alive || u.TargetId == null) continue;
                if (!byId.TryGetValue(u.TargetId, out var t) || !t.Alive) continue;
                frame.Lines.Add(new HudLine { From = u, To = t, Kind = HudLineKind.Missile, Side = m.Side, Solid = u.Seeker == "active" });
                if (u.OwnerId != null) flying.Add((u.OwnerId, t.Id));
            }
            if (s.Mode != "story" || focus == null || focus.Targets == null || focus.Targets.Count == 0) return;
            if (!byId.TryGetValue(focus.Targets[0], out var primary) || !primary.Alive || flying.Contains((focus.Id, primary.Id))) return;
            frame.Lines.Add(new HudLine { From = focus, To = primary, Kind = HudLineKind.Designation, Side = SideOf(focus, home), Alpha = 0.6 });
        }

        public static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
