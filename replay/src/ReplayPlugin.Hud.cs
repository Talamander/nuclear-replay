using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Target HUD over replay cameras (design: 2026-10-30-hud-overlay/DESIGN.md): the game's own HUD markers on the
    // units of the fight, target boxes, and lines from missiles to their targets. HudModel.cs decides what is drawn
    // (Unity-free, tested), HudOverlay.cs draws it.
    //
    // The settings on screen each frame:
    //   free camera on: the viewer's (F4 panel);
    //   otherwise the current shot's "target_hud" over the shot list's root "target_hud";
    //   a shot list that never mentions it: the viewer's in the viewer, off in a render job (old lists render as
    //   before).
    // The focus (whose fight the story mode tells): the settings' focus selector, else the shot's target (a missile
    // camera's focus is the missile's shooter), else in the free camera the Camera Studio unit or the last unit
    // followed. Friend and foe: relative to the recording player's side for the whole replay.
    // Shot-list JSON to the plain values HudSettings reads, and back.
    internal static class HudJson
    {
        public static object Plain(JToken t)
        {
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.String: return (string)t;
                case JTokenType.Integer: return (long)t;
                case JTokenType.Float: return (double)t;
                case JTokenType.Object:
                    var d = new Dictionary<string, object>();
                    foreach (var p in ((JObject)t).Properties()) d[p.Name] = Plain(p.Value);
                    return d;
                default: return null;
            }
        }

        public static JObject ToJson(HudSettings s)
        {
            var o = new JObject();
            foreach (var kv in s.ToShot()) o[kv.Key] = JToken.FromObject(kv.Value);
            return o;
        }
    }

    public sealed partial class ReplayPlugin
    {
        private readonly HudSettings hudViewer = new HudSettings();
        private HudOverlay hudOverlay;
        private HudSettings hudShot;
        private JToken hudShotToken, hudRootToken;
        private string hudSource = "none";
        private bool hudPanel;
        private int hudRow;
        private string hudFocusSelector;
        private Unit hudFocusResolved;
        private float hudFocusAt = -1;
        private ConfigEntry<string> hudModeCfg, hudSideCfg, hudHomeCfg, hudUnitsCfg;
        private ConfigEntry<bool> hudLabelsCfg, hudArrowsCfg;
        private ConfigEntry<float> hudSizeCfg;

        private static readonly string[] HudRows = { "Mode", "Side", "Home side", "Labels", "Size", "Arrows" };

        // Called from ViewerAwake.
        private void HudAwake()
        {
            hudModeCfg = Config.Bind("TargetHud", "Mode", "off", "Target HUD over replay cameras: off, story, missiles or all (F4 panel, Shift+F4 cycles).");
            hudSideCfg = Config.Bind("TargetHud", "Side", "both", "Only this side's units, its missiles and their targets; both shows everyone.");
            hudHomeCfg = Config.Bind("TargetHud", "Home", "", "The side drawn friendly; empty: the recording player's side.");
            hudLabelsCfg = Config.Bind("TargetHud", "Labels", true, "Labels on the units of the fight.");
            hudArrowsCfg = Config.Bind("TargetHud", "Arrows", true, "Story mode: arrows at the screen edge towards incoming missiles and the primary target.");
            hudSizeCfg = Config.Bind("TargetHud", "Size", 1f, "Marker and text size, 0.5 to 2.5.");
            hudUnitsCfg = Config.Bind("TargetHud", "Units", "metric", "Distances: metric or imperial.");
            HudSettings.Apply(hudViewer, new Dictionary<string, object>
            {
                ["mode"] = hudModeCfg.Value, ["side"] = hudSideCfg.Value, ["labels"] = hudLabelsCfg.Value, ["arrows"] = hudArrowsCfg.Value,
                ["size"] = (double)hudSizeCfg.Value, ["units"] = hudUnitsCfg.Value
            });
            if (Array.IndexOf(HudSettings.Modes, hudModeCfg.Value) < 0 || hudModeCfg.Value == "off") hudViewer.Mode = "off";
            hudViewer.Home = string.IsNullOrEmpty(hudHomeCfg.Value) ? null : hudHomeCfg.Value;
            HudTargets.Log = Logger;
        }

        // Its own Harmony patch, so a game update that renames the RPC handler only loses the target lists.
        private void HudPatch(Harmony harmony)
        {
            try { harmony.PatchAll(typeof(HudTargetsPatch)); Logger.LogInfo("Target HUD: listening for target lists (Unit.RpcSetStationTargets)"); }
            catch (Exception e) { Logger.LogWarning("Target HUD: could not patch Unit.RpcSetStationTargets; target lists come from the game state only: " + e.Message); }
        }

        private void HudSave()
        {
            if (hudModeCfg == null) return;
            hudModeCfg.Value = hudViewer.Mode;
            hudSideCfg.Value = hudViewer.Side;
            hudHomeCfg.Value = hudViewer.Home ?? "";
            hudLabelsCfg.Value = hudViewer.Labels;
            hudArrowsCfg.Value = hudViewer.Arrows;
            hudSizeCfg.Value = (float)hudViewer.Size;
            hudUnitsCfg.Value = hudViewer.Units;
        }

        // ---------------- every frame ----------------

        // LateUpdate, after the director has picked this frame's shot and target and the camera has moved. The
        // overlay itself reads positions later, in Canvas.willRenderCanvases.
        private void HudStep(bool live)
        {
            if (hudOverlay == null) hudOverlay = new HudOverlay(Logger) { CameraSource = HudCamera };
            HudSettings use = null;
            if (viewer && freeCamOn) { use = hudViewer; hudSource = "viewer"; }
            else
            {
                var token = director?.CurrentHud;
                var root = director?.RootHud;
                if (!ReferenceEquals(token, hudShotToken) || !ReferenceEquals(root, hudRootToken))
                {
                    hudShotToken = token; hudRootToken = root;
                    hudShot = HudSettings.FromShot(HudJson.Plain(token), HudJson.Plain(root));
                }
                if (hudShot != null) { use = hudShot; hudSource = "shot"; }
                else if (viewer) { use = hudViewer; hudSource = "viewer"; }
                else hudSource = "none";
            }
            hudOverlay.Settings = use;
            hudOverlay.Active = live && use != null && use.On;
            if (!hudOverlay.Active) return;
            hudOverlay.Focus = HudFocus(use);
            hudOverlay.Home = HudHome();
            hudOverlay.Hook();
        }

        private Unit HudFocus(HudSettings use)
        {
            if (!string.IsNullOrEmpty(use.Focus) && director != null)
            {
                // A focus selector ("unit:221", "name:Talamander", "host" ...), resolved twice a second.
                if (use.Focus != hudFocusSelector || Time.unscaledTime - hudFocusAt > 0.5f || hudFocusResolved == null)
                {
                    hudFocusSelector = use.Focus; hudFocusAt = Time.unscaledTime;
                    try { hudFocusResolved = director.Resolve(use.Focus); } catch (Exception) { hudFocusResolved = null; }
                }
                if (hudFocusResolved != null) return hudFocusResolved;
            }
            if (viewer && freeCamOn && studio.Open) { var su = StudioUnit(); if (su != null) return su; }
            return director?.CurrentUnit;
        }

        // The side drawn friendly: the recording player's, else the host aircraft's (the overlay falls back to the
        // focus unit's side when neither is known).
        private string HudHome()
        {
            if (!string.IsNullOrEmpty(Units.RecordedHome)) return Units.RecordedHome;
            try { var host = Director.HostAircraft(); if (host != null) return Units.Side(host); } catch (Exception) { }
            return null;
        }

        private static Camera HudCamera()
        {
            var cams = CameraStateManager.i;
            return cams != null && cams.mainCamera != null ? CineCamera.RenderCamera(cams.mainCamera) : null;
        }

        // Seek, restart, leaving the replay.
        private void HudTeardown()
        {
            hudShot = null; hudShotToken = null; hudRootToken = null;
            hudSource = "none";
            hudFocusResolved = null; hudFocusSelector = null;
            HudTargets.Clear();
            if (hudOverlay != null) { hudOverlay.Active = false; hudOverlay.Unhook(); hudOverlay.Reset(); }
        }

        internal Canvas HudCanvas => hudOverlay?.Canvas;

        // ---------------- viewer keys and panel ----------------

        // ViewerKeys calls this every frame. F4: the panel; Shift+F4: the next mode. While the panel is open, Tab /
        // Shift+Tab pick a row, Page Up / Page Down change it, Home resets it.
        private void HudKeys()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.F4))
            {
                if (shift) HudCycleMode(1);
                else ViewerToggleHudPanel();
            }
            if (!hudPanel) return;
            if (Input.GetKeyDown(KeyCode.Tab)) HudSelect(shift ? -1 : 1);
            if (Input.GetKeyDown(KeyCode.PageUp)) HudAdjust(1);
            if (Input.GetKeyDown(KeyCode.PageDown)) HudAdjust(-1);
            if (Input.GetKeyDown(KeyCode.Home)) HudReset();
        }

        internal void ViewerToggleHudPanel()
        {
            if (!viewer) return;
            hudPanel = !hudPanel;
            if (!hudPanel) return;
            lensPanel = false; lightingOpen = false;   // these panels share Tab, Page Up, Page Down and Home
            if (!hudViewer.On) { hudViewer.Mode = "story"; HudChanged("Target HUD: Story"); }
        }

        internal void HudCycleMode(int step)
        {
            hudViewer.Mode = HudSettings.NextMode(hudViewer.Mode, step);
            HudChanged("Target HUD: " + HudSettings.ModeLabel(hudViewer.Mode));
        }

        internal void HudSelect(int step)
        {
            int n = HudRows.Length;
            hudRow = ((hudRow + step) % n + n) % n;
        }

        // The sides in this replay, for the Side and Home rows.
        private List<string> HudFactions()
        {
            var sides = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var u in UnitRegistry.allUnits) { if (u == null) continue; var s = Units.Side(u); if (!string.IsNullOrEmpty(s)) sides.Add(s); } } catch (Exception) { }
            if (!string.IsNullOrEmpty(Units.RecordedHome)) sides.Add(Units.RecordedHome);
            return sides.ToList();
        }

        private static string Step(List<string> ring, string current, int step)
        {
            int i = ring.FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
            if (i < 0) i = 0;
            return ring[((i + step) % ring.Count + ring.Count) % ring.Count];
        }

        internal void HudAdjust(int step)
        {
            switch (hudRow)
            {
                case 0: HudCycleMode(step); return;
                case 1:
                    var sides = new List<string> { "both" }; sides.AddRange(HudFactions());
                    hudViewer.Side = Step(sides, hudViewer.Side, step);
                    HudChanged("Target HUD side: " + (hudViewer.Side == "both" ? "both sides" : hudViewer.Side)); return;
                case 2:
                    var homes = new List<string> { "" }; homes.AddRange(HudFactions());
                    string h = Step(homes, hudViewer.Home ?? "", step);
                    hudViewer.Home = h == "" ? null : h;
                    HudChanged("Target HUD friendly side: " + (hudViewer.Home ?? "the recording player's (" + (HudHome() ?? "unknown") + ")")); return;
                case 3: hudViewer.Labels = !hudViewer.Labels; HudChanged("Target HUD labels " + (hudViewer.Labels ? "on" : "off")); return;
                case 4:
                    hudViewer.Size = HudSettings.Clamp(Math.Round((hudViewer.Size + 0.1 * step) * 10) / 10, HudSettings.MinSize, HudSettings.MaxSize);
                    HudChanged("Target HUD size " + HudModel.Num(hudViewer.Size)); return;
                case 5: hudViewer.Arrows = !hudViewer.Arrows; HudChanged("Target HUD arrows " + (hudViewer.Arrows ? "on" : "off")); return;
            }
        }

        internal void HudReset()
        {
            var d = new HudSettings();
            switch (hudRow)
            {
                case 0: hudViewer.Mode = "story"; break;
                case 1: hudViewer.Side = d.Side; break;
                case 2: hudViewer.Home = null; break;
                case 3: hudViewer.Labels = d.Labels; break;
                case 4: hudViewer.Size = d.Size; break;
                case 5: hudViewer.Arrows = d.Arrows; break;
            }
            HudChanged("Target HUD " + HudRows[hudRow].ToLowerInvariant() + " reset");
        }

        private void HudChanged(string message)
        {
            if (message != null) ui?.Toast(message, 2f);
            HudSave();
        }

        // Panel text (null when closed).
        private string HudPanelText()
        {
            if (!hudPanel) return null;
            var s = hudViewer;
            string[] values =
            {
                HudSettings.ModeLabel(s.Mode),
                s.Side == "both" ? "both sides" : s.Side,
                s.Home ?? "recording player's (" + (HudHome() ?? "unknown") + ")",
                s.Labels ? "on" : "off",
                HudModel.Num(s.Size),
                s.Arrows ? "on" + (s.Mode == "story" ? "" : " (story only)") : "off"
            };
            var lines = new List<string>();
            for (int i = 0; i < HudRows.Length; i++)
                lines.Add((i == hudRow ? "<color=#ffd24a>> " : "   ") + HudRows[i] + ": <b>" + values[i] + "</b>" + (i == hudRow ? "</color>" : ""));
            string note = hudSource == "shot" ? "This shot sets its own target HUD; the panel is for the free camera and shots without one."
                : "Story: the followed unit's fight. Missiles: every missile and its target. All: every unit in range.";
            var f = hudOverlay?.LastFrame;
            if (f != null && hudOverlay.Active) note = $"{f.Marks.Count} marked, {f.Lines.Count} lines" + (f.Focus != null ? ", focus " + HudModel.Name(f.Focus) : "") + ". " + note;
            return string.Join("\n", lines) + "\n<size=14>" + note + "</size>";
        }

        // A viewer clip (C) records with the panel's target HUD, as the list's default for every shot.
        private void HudClipRecord(JObject record)
        {
            if (hudViewer.On) record[HudSettings.Field] = HudJson.ToJson(hudViewer);
        }

        // ---------------- test hooks and status ----------------

        // Viewer command "targethud": "targethud" (state), "targethud off|story|missiles|all",
        // "targethud side <faction|both>", "targethud home <faction|auto>", "targethud labels on|off",
        // "targethud arrows on|off", "targethud size <x>", "targethud units metric|imperial",
        // "targethud focus <selector|none>", "targethud panel on|off", "targethud row <n>", "targethud adjust <+1|-1>".
        private string HudCommand(string arg)
        {
            var parts = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string what = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            string value = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
            string lower = value.ToLowerInvariant();
            if (Array.IndexOf(HudSettings.Modes, what) >= 0) { hudViewer.Mode = what; HudChanged(null); what = ""; }
            switch (what)
            {
                case "": break;
                case "side": hudViewer.Side = lower == "both" || lower == "" ? "both" : value; HudChanged(null); break;
                case "home": hudViewer.Home = lower == "auto" || lower == "" ? null : value; HudChanged(null); break;
                case "labels": hudViewer.Labels = lower != "off"; HudChanged(null); break;
                case "arrows": hudViewer.Arrows = lower != "off"; HudChanged(null); break;
                case "size":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double size)) return "size needs a number";
                    hudViewer.Size = HudSettings.Clamp(size, HudSettings.MinSize, HudSettings.MaxSize); HudChanged(null); break;
                case "units":
                    if (Array.IndexOf(HudSettings.UnitSystems, lower) < 0) return "units: metric or imperial";
                    hudViewer.Units = lower; HudChanged(null); break;
                case "focus": hudViewer.Focus = lower == "none" || lower == "" ? null : value; HudChanged(null); break;
                case "panel": if ((lower != "off") != hudPanel) ViewerToggleHudPanel(); break;
                case "row":
                    if (!int.TryParse(value, out int row) || row < 0 || row >= HudRows.Length) return "row 0-" + (HudRows.Length - 1);
                    hudRow = row; break;
                case "adjust": HudAdjust(lower.StartsWith("-") ? -1 : 1); break;
                case "cycle": HudCycleMode(1); break;
                default: return "unknown targethud command";
            }
            return "targethud " + hudViewer.Summary() + (hudPanel ? " (panel open, row " + HudRows[hudRow] + ")" : "");
        }

        private JObject HudStatus()
        {
            var frame = hudOverlay?.LastFrame;
            var o = new JObject
            {
                ["panel"] = hudPanel, ["row"] = HudRows[hudRow], ["source"] = hudSource, ["viewer"] = HudJson.ToJson(hudViewer),
                ["active"] = hudOverlay != null && hudOverlay.Active, ["error"] = hudOverlay?.LastError,
                ["home"] = HudHome(), ["rpc_messages"] = HudTargets.Messages, ["text"] = HudPanelText()
            };
            if (frame != null)
            {
                o["units"] = hudOverlay.LastUnits;
                o["marks"] = frame.Marks.Count;
                o["lines"] = frame.Lines.Count;
                o["hidden"] = frame.Hidden;
                o["focus"] = frame.Focus?.Id;
                o["labels"] = new JArray(frame.Marks.Where(m => m.Label != null).Select(m => m.Label));
            }
            return o;
        }

        // Render jobs: a log line each replay second with what the overlay drew, for checking a headless render.
        private double hudNextLog;
        private void HudLog(double replayTime)
        {
            var frame = hudOverlay?.LastFrame;
            if (frame == null || hudOverlay == null || !hudOverlay.Active) return;
            if (replayTime < hudNextLog - 2) hudNextLog = 0;   // a new range started earlier in the replay
            if (replayTime < hudNextLog) return;
            hudNextLog = replayTime + 1.0;
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "Target HUD {0:F1}s ({1}, {2}, focus {3}, home {4}): {5} of {6} units marked, {7} lines [{8}], {9} hidden, {10} RPCs; {11}",
                replayTime, hudSource, hudOverlay.Settings?.Mode, frame.Focus != null ? frame.Focus.Id + " " + HudModel.Name(frame.Focus) : "none", frame.Home ?? "none",
                frame.Marks.Count, hudOverlay.LastUnits, frame.Lines.Count, string.Join(", ", frame.Lines.Take(6)), frame.Hidden, HudTargets.Messages,
                string.Join("; ", frame.Marks.Take(8))));
            // Where the labels went (screen pixels, y up), to check placement against the rendered frame.
            Logger.LogInfo("Target HUD labels: " + string.Join("; ", frame.Marks.Where(m => m.Label != null).Select(m =>
                string.Format(CultureInfo.InvariantCulture, "{0} {1:F0},{2:F0} {3} \"{4}\"", m.Unit.Id, m.Unit.X, m.Unit.Y, m.Arrow ? "arrow" : m.LabelAt.ToString(), m.Label))));
        }
    }
}
