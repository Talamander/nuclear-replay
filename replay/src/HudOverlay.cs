using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using HarmonyLib;
using NuclearOption.UIStyleSystem;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearReplay.Playback
{
    // Draws the target HUD (HudModel.cs) over the replay camera with the game's own HUD symbology: each unit's
    // friendly or hostile HUD icon (UnitDefinition.friendlyIcon / hostileIcon), the minimized pips
    // (CombatHUD.minimizedFriendly / minimizedHostile), the HUD unit colours of the active theme
    // (ColorTheme.HudUnitFriendly / Hostile / Neutral / Selected, Warning), the game's target box
    // (GameAssets.targetUnitSprite) and the HUD's own font.
    //
    // It is a screen-space overlay canvas of its own, under the viewer's UI, so frame grabs (render jobs, clips)
    // include it and the viewer's H (hide UI) does not hide it. Positions are read in Canvas.willRenderCanvases,
    // after every LateUpdate: the director has moved the camera and the cine camera has written each unit's
    // smoothed render position into its transform, so markers sit on the units in the frame being drawn.
    internal sealed class HudOverlay
    {
        private readonly ManualLogSource log;
        private GameObject root;
        private Canvas canvas;
        private RectTransform layer;
        private readonly List<Image> icons = new List<Image>();
        private readonly List<Image> boxes = new List<Image>();
        private readonly List<RawImage> lines = new List<RawImage>();
        private readonly List<Text> labels = new List<Text>();
        private readonly List<Image> arrows = new List<Image>();
        private Font font;
        private Texture2D dash;
        private Sprite arrowSprite, squareSprite, diamondSprite, pipSprite;
        private bool hooked;
        private int logged;
        private bool loggedFallback;

        // Set every frame by the plugin: what to draw and from where. Null settings or Active false hides it.
        public HudSettings Settings;
        public Unit Focus;
        public string Home;
        public bool Active;
        public Func<Camera> CameraSource;
        public HudFrame LastFrame { get; private set; }
        public int LastUnits { get; private set; }
        public string LastError { get; private set; }

        public Canvas Canvas => canvas;

        public HudOverlay(ManualLogSource log) { this.log = log; HudIcons.Init(log); }

        public void Hook()
        {
            if (hooked) return;
            Canvas.willRenderCanvases += Draw;
            hooked = true;
        }

        public void Unhook()
        {
            if (hooked) Canvas.willRenderCanvases -= Draw;
            hooked = false;
            Hide();
        }

        public void Hide()
        {
            if (root != null && root.activeSelf) root.SetActive(false);
        }

        // Seek or restart: forget first-seen and death times so nothing flashes for the wrong reason.
        public void Reset()
        {
            firstSeen.Clear(); deathAt.Clear(); wasCast.Clear();
            activeSince = -1;
        }

        private void EnsureRoot()
        {
            if (root != null) return;
            root = new GameObject("NuclearReplay Target HUD");
            UnityEngine.Object.DontDestroyOnLoad(root);
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 29000;     // under the viewer's UI (30000)
            canvas.pixelPerfect = false;
            layer = (RectTransform)new GameObject("Layer", typeof(RectTransform)).transform;
            layer.SetParent(root.transform, false);
            layer.anchorMin = Vector2.zero; layer.anchorMax = Vector2.zero; layer.pivot = Vector2.zero;
            layer.anchoredPosition = Vector2.zero;
            font = FindHudFont();
            dash = new Texture2D(16, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            for (int x = 0; x < 16; x++) dash.SetPixel(x, 0, x < 9 ? Color.white : new Color(1, 1, 1, 0));
            dash.Apply();
            arrowSprite = MakeSprite((u, v) => { float a = Mathf.Abs(u); return a < (1 - v) * 0.95f && v > 0.05f && !(a < (0.55f - v) * 0.9f); });
            squareSprite = MakeSprite((u, v) => { float a = Mathf.Max(Mathf.Abs(u), Mathf.Abs(v * 2 - 1)); return a > 0.72f && a < 0.95f; });
            diamondSprite = MakeSprite((u, v) => { float a = Mathf.Abs(u) + Mathf.Abs(v * 2 - 1); return a > 0.68f && a < 0.95f; });
            pipSprite = MakeSprite((u, v) => Mathf.Abs(u) < 0.5f && Mathf.Abs(v * 2 - 1) < 0.5f);
            log.LogInfo($"Target HUD: overlay canvas made (font {(font != null ? font.name : "none")})");
        }

        // A 32x32 white sprite from a shape test over u in -1..1 (left to right) and v in 0..1 (bottom to top).
        private static Sprite MakeSprite(Func<float, float, bool> inside)
        {
            var tex = new Texture2D(32, 32, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    tex.SetPixel(x, y, inside((x - 15.5f) / 16f, y / 31f) ? Color.white : new Color(1, 1, 1, 0));
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 32, 32), new Vector2(0.5f, 0.5f));
        }

        // The font of the game's HUD target text (CombatHUD.targetInfo), else the built-in one.
        private static Font FindHudFont()
        {
            try
            {
                var hud = SceneSingleton<CombatHUD>.i;
                if (hud != null)
                {
                    var t = Traverse.Create(hud).Field("targetInfo").GetValue<Text>();
                    if (t != null && t.font != null) return t.font;
                }
            }
            catch (Exception) { }
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
            try { return Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { }
            return null;
        }

        private static Sprite MinimizedSprite(HudSide side)
        {
            try
            {
                var hud = SceneSingleton<CombatHUD>.i;
                if (hud != null) return side == HudSide.Friendly ? hud.minimizedFriendly ?? hud.minimizedHostile : hud.minimizedHostile ?? hud.minimizedFriendly;
            }
            catch (Exception) { }
            return null;
        }

        // ---------------- every frame ----------------

        private readonly List<HudUnit> units = new List<HudUnit>();
        private readonly Dictionary<Unit, HudUnit> byUnit = new Dictionary<Unit, HudUnit>();
        private readonly Dictionary<string, float> firstSeen = new Dictionary<string, float>();
        private readonly Dictionary<string, float> deathAt = new Dictionary<string, float>();
        private readonly HashSet<string> wasCast = new HashSet<string>();
        private float activeSince = -1, lastTime;

        private void Draw()
        {
            try
            {
                if (!Active || Settings == null || !Settings.On) { Hide(); LastFrame = null; activeSince = -1; return; }
                var cam = CameraSource?.Invoke();
                if (cam == null) { Hide(); return; }
                EnsureRoot();
                if (!root.activeSelf) root.SetActive(true);
                if (font == null || font.name == "LegacyRuntime" || font.name == "Arial") { var f = FindHudFont(); if (f != null) font = f; }
                float now = Time.time;
                if (now < lastTime - 0.5f) Reset();          // the replay went back (seek)
                lastTime = now;
                if (activeSince < 0) activeSince = now;
                float sw = Screen.width, sh = Screen.height;
                Gather(cam, sw, sh, now);
                // The plugin sets Focus: the settings' focus selector resolved, else the shot's target.
                HudUnit focus = Focus != null && byUnit.TryGetValue(Focus, out var fu) ? fu : null;
                var view = new HudView(sw, sh);
                var frame = HudModel.Build(Settings, units, focus, view, !string.IsNullOrEmpty(Settings.Home) ? Settings.Home : Home ?? focus?.Side, LastFrame);
                wasCast.Clear();
                foreach (var m in frame.Marks) if (m.Role != HudRole.Context) wasCast.Add(m.Unit.Id);
                LastFrame = frame;
                LastUnits = units.Count;
                Render(frame, cam, view, (float)(view.Scale * Settings.Size));
                LastError = null;
            }
            catch (Exception e)
            {
                if (LastError == null) log.LogError("Target HUD draw failed: " + e);
                LastError = e.Message;
                Hide();
            }
        }

        private void Gather(Camera cam, float sw, float sh, float now)
        {
            units.Clear();
            byUnit.Clear();
            Vector3 camPos = cam.transform.position;
            float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            bool starting = now - activeSince < 0.3f;   // what is there when the overlay starts is not "new"
            foreach (var unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.transform == null || unit.definition == null) continue;
                var kind = Units.Kind(unit);
                if (kind == UnitKind.Other) continue;
                Vector3 pos = unit.transform.position;
                Vector3 vp = cam.WorldToViewportPoint(pos);
                string id = unit.persistentID.ToString();
                if (!firstSeen.TryGetValue(id, out float seen)) { seen = starting ? -1000 : now; firstSeen[id] = seen; }
                double deadFor = -1;
                if (unit.disabled)
                {
                    if (!deathAt.TryGetValue(id, out float died)) { died = now - seen < 0.2f || starting ? -1000 : now; deathAt[id] = died; }
                    deadFor = now - died;
                }
                double depth = vp.z;
                double size = Mathf.Max(unit.maxRadius, 1f) * 2.0;
                var h = new HudUnit
                {
                    Id = id,
                    Kind = kind,
                    Side = Units.Side(unit),
                    Code = kind == UnitKind.Missile ? MissileCode(unit.definition) : unit.definition.code,
                    Type = unit.definition.unitName,
                    Player = kind == UnitKind.Aircraft || kind == UnitKind.Rotorcraft ? Units.Player(unit) : null,
                    X = vp.x * sw, Y = vp.y * sh, Depth = depth,
                    Distance = (pos - camPos).magnitude,
                    PX = pos.x, PY = pos.y, PZ = pos.z,
                    ScreenSize = depth > 0.5 ? size / (2 * depth * tanHalf) * sh : 0,
                    Age = now - seen,
                    DeadFor = deadFor,
                    WasCast = wasCast.Contains(id),
                    Unit = unit
                };
                if (unit is Missile m)
                {
                    try
                    {
                        h.TargetId = m.targetID.IsValid ? m.targetID.ToString() : null;
                        h.OwnerId = m.ownerID.IsValid ? m.ownerID.ToString() : null;
                        h.Seeker = m.seekerMode == Missile.SeekerMode.activeLock ? "active" : m.seekerMode == Missile.SeekerMode.activeSearch ? "search" : "passive";
                    }
                    catch (Exception) { }
                }
                else if (kind != UnitKind.Building)
                {
                    h.Targets = HudTargets.For(unit, id, now, out float changed);
                    h.TargetsAge = now - changed;
                }
                units.Add(h);
                byUnit[unit] = h;
            }
        }

        // A missile's short name: its definition's code is often generic ("MSL"), its name starts with the
        // designation ("AGR-18 Lynchpin", "IRM-S1").
        private static string MissileCode(UnitDefinition d)
        {
            string name = d.unitName?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                string first = name.Split(' ')[0];
                foreach (char c in first) if (char.IsDigit(c)) return first;
            }
            return !string.IsNullOrEmpty(d.code) && d.code.Trim().Length > 3 ? d.code.Trim() : name ?? d.code;
        }

        // ---------------- drawing ----------------

        private static Color ThemeColor(HudSide side)
        {
            try
            {
                var theme = ThemeManager.Active?.ColorTheme;
                if (theme != null)
                    return side == HudSide.Friendly ? theme.HudUnitFriendly : side == HudSide.Hostile ? theme.HudUnitHostile : theme.HudUnitNeutral;
            }
            catch (Exception) { }
            return side == HudSide.Friendly ? new Color(0.3f, 1f, 0.4f) : side == HudSide.Hostile ? new Color(1f, 0.25f, 0.2f) : new Color(0.9f, 0.9f, 0.9f);
        }

        private static Color SelectedColor()
        {
            try { var theme = ThemeManager.Active?.ColorTheme; if (theme != null) return theme.HudUnitSelected; } catch (Exception) { }
            return new Color(1f, 0.85f, 0.2f);
        }

        private static Color WarningColor()
        {
            try { var theme = ThemeManager.Active?.ColorTheme; if (theme != null) return theme.Warning; } catch (Exception) { }
            return new Color(1f, 0.6f, 0.1f);
        }

        private static T Take<T>(List<T> pool, int index, Func<T> make) where T : Component
        {
            while (pool.Count <= index) pool.Add(make());
            var c = pool[index];
            if (!c.gameObject.activeSelf) c.gameObject.SetActive(true);
            return c;
        }

        private static void HideFrom<T>(List<T> pool, int count) where T : Component
        {
            for (int i = count; i < pool.Count; i++) if (pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
        }

        private Image NewImage(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(layer, false);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            img.preserveAspect = true;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            return img;
        }

        private RawImage NewLine()
        {
            var go = new GameObject("Line", typeof(RectTransform));
            go.transform.SetParent(layer, false);
            go.transform.SetAsFirstSibling();   // under the markers
            var img = go.AddComponent<RawImage>();
            img.raycastTarget = false;
            img.texture = dash;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0, 0.5f);
            return img;
        }

        private Text NewLabel()
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(layer, false);
            var t = go.AddComponent<Text>();
            t.raycastTarget = false;
            t.font = font;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.lineSpacing = 0.9f;
            var shadow = go.AddComponent<Outline>();
            shadow.effectColor = new Color(0, 0, 0, 0.8f);
            shadow.effectDistance = new Vector2(1.5f, -1.5f);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0, 0.5f);
            rt.sizeDelta = new Vector2(600, 80);
            return t;
        }

        // Marker size factor from the unit's UnitDefinition.iconSize (the game sizes HUD icons by it), kept close to 1.
        private static float IconFactor(HudUnit u)
        {
            var unit = u.Unit as Unit;
            float size = unit != null && unit.definition != null && unit.definition.iconSize > 0 ? unit.definition.iconSize : 1f;
            return Mathf.Clamp(size, 0.7f, 1.5f);
        }

        private static Color WithAlpha(Color c, double a) { c.a *= (float)a; return c; }

        private void Render(HudFrame frame, Camera cam, HudView view, float scale)
        {
            int ni = 0, nb = 0, nl = 0, nt = 0, na = 0;
            float sw = (float)view.Width, sh = (float)view.Height;
            var selected = SelectedColor();
            var warning = WarningColor();
            float blink = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 18f);

            foreach (var line in frame.Lines)
            {
                if (!LineEnds(cam, line.From, line.To, sw, sh, out var a, out var b)) continue;
                var img = Take(lines, nl++, NewLine);
                img.color = WithAlpha(ThemeColor(line.Side), (line.Kind == HudLineKind.Missile ? 0.9 : 0.7) * line.Alpha);
                Vector2 d = b - a;
                float len = d.magnitude;
                var rt = img.rectTransform;
                rt.anchoredPosition = a;
                rt.sizeDelta = new Vector2(len, Mathf.Max(1f, (float)HudModel.LinePx * scale));
                rt.localEulerAngles = new Vector3(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
                img.uvRect = line.Solid ? new Rect(0, 0, 0.5f, 1) : new Rect(0, 0, len / (14f * scale), 1);
            }

            view.Safe(out double sx0, out double sy0, out double sx1, out double sy1);
            foreach (var m in frame.Marks)
            {
                var u = m.Unit;
                var unit = u.Unit as Unit;
                var color = ThemeColor(m.Side);
                if (m.Flash > 0) color = Color.Lerp(color, warning, (float)m.Flash * blink);
                else if (m.Role == HudRole.Threat) color = Color.Lerp(color, warning, 0.35f * blink);
                color = WithAlpha(color, m.Alpha);
                if (m.Arrow)
                {
                    // Off screen or behind: an arrow on the safe-area edge pointing at it, in the threat colour.
                    var dir = EdgeDirection(cam, unit, sw, sh, sx0, sy0, sx1, sy1, out var at);
                    var arrow = Take(arrows, na++, () => NewImage("Arrow"));
                    arrow.sprite = arrowSprite;
                    arrow.color = m.Role == HudRole.Threat ? Color.Lerp(ThemeColor(HudSide.Hostile), warning, 0.5f * blink) : selected;
                    arrow.rectTransform.anchoredPosition = at;
                    arrow.rectTransform.sizeDelta = Vector2.one * (float)HudModel.ArrowPx * scale;
                    arrow.rectTransform.localEulerAngles = new Vector3(0, 0, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f);
                    if (m.Label != null)
                    {
                        var t = Take(labels, nt++, NewLabel);
                        SetLabel(t, m.Label, arrow.color, scale);
                        bool left = at.x > sw * 0.5f;
                        t.alignment = left ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
                        t.rectTransform.pivot = new Vector2(left ? 1 : 0, 0.5f);
                        Vector2 off = -dir * (float)HudModel.ArrowPx * 1.1f * scale;
                        t.rectTransform.anchoredPosition = at + off;
                    }
                    continue;
                }
                var pos = new Vector2((float)u.X, (float)u.Y);
                var icon = Take(icons, ni++, () => NewImage("Icon"));
                Sprite sprite = null;
                float px;
                if (m.Look == HudLook.Pip)
                {
                    sprite = MinimizedSprite(m.Side);
                    px = (float)HudModel.MarkerPx * 0.45f * scale;
                }
                else
                {
                    if (unit != null && unit.definition != null)
                        sprite = m.Side == HudSide.Hostile ? unit.definition.hostileIcon ?? unit.definition.friendlyIcon : unit.definition.friendlyIcon ?? unit.definition.hostileIcon;
                    px = (float)(HudModel.MarkerPx * m.Size) * IconFactor(u) * scale * (m.Look == HudLook.Small ? 0.6f : 1f);
                    if (u.Kind == UnitKind.Missile) px = Mathf.Max(px * 0.75f, 14f * scale);
                }
                Material iconMaterial = null;
                if (sprite == null)
                {
                    sprite = m.Look == HudLook.Pip ? pipSprite : u.Kind == UnitKind.Missile ? diamondSprite : squareSprite;
                    if (!loggedFallback) { loggedFallback = true; log.LogWarning($"Target HUD: no game sprite for {u.Code} ({m.Look}); drawing a plain shape"); }
                }
                else
                {
                    // The game's icons are a symbol on opaque black: draw them with the game's additive marker
                    // material when it has one, else from a copy with the black keyed out (HudIcons.cs).
                    iconMaterial = HudIcons.GameMaterial();
                    if (iconMaterial == null) sprite = HudIcons.Keyed(sprite);
                }
                if (icon.material != (iconMaterial ?? Graphic.defaultGraphicMaterial)) icon.material = iconMaterial;
                icon.sprite = sprite;
                icon.color = color;
                icon.rectTransform.anchoredPosition = pos;
                icon.rectTransform.sizeDelta = Vector2.one * px;
                if (logged < 12 && unit != null)
                {
                    logged++;
                    log.LogInfo(string.Format(CultureInfo.InvariantCulture, "Target HUD icon {0} {1} {2}: sprite {3} {4}x{5}, iconSize {6}, {7:F0} px",
                        u.Code, u.Kind, m.Look, sprite.name, sprite.rect.width, sprite.rect.height, unit.definition.iconSize, px));
                }
                if (m.Box)
                {
                    var box = Take(boxes, nb++, () => NewImage("Box"));
                    Sprite boxSprite = null;
                    try { boxSprite = m.Side == HudSide.Friendly ? GameAssets.i.targetUnitSpriteFriendly : GameAssets.i.targetUnitSprite; } catch (Exception) { }
                    box.sprite = boxSprite ?? squareSprite;
                    box.color = WithAlpha(m.Flash > 0 ? Color.Lerp(selected, warning, (float)m.Flash * blink) : selected, m.Alpha);
                    box.rectTransform.anchoredPosition = pos;
                    box.rectTransform.sizeDelta = Vector2.one * Mathf.Max(px * 1.7f, 40f * scale);
                }
                if (m.Label != null)
                {
                    var t = Take(labels, nt++, NewLabel);
                    SetLabel(t, m.Label, color, scale);
                    float gap = (float)(HudModel.MarkerPx * 0.6 + 6) * scale;
                    switch (m.LabelAt)
                    {
                        case HudLabelSide.Left: t.alignment = TextAnchor.MiddleRight; t.rectTransform.pivot = new Vector2(1, 0.5f); t.rectTransform.anchoredPosition = pos + new Vector2(-gap, 0); break;
                        case HudLabelSide.Above: t.alignment = TextAnchor.LowerCenter; t.rectTransform.pivot = new Vector2(0.5f, 0); t.rectTransform.anchoredPosition = pos + new Vector2(0, gap); break;
                        case HudLabelSide.Below: t.alignment = TextAnchor.UpperCenter; t.rectTransform.pivot = new Vector2(0.5f, 1); t.rectTransform.anchoredPosition = pos + new Vector2(0, -gap); break;
                        default: t.alignment = TextAnchor.MiddleLeft; t.rectTransform.pivot = new Vector2(0, 0.5f); t.rectTransform.anchoredPosition = pos + new Vector2(gap, 0); break;
                    }
                }
            }
            HideFrom(icons, ni); HideFrom(boxes, nb); HideFrom(lines, nl); HideFrom(labels, nt); HideFrom(arrows, na);
        }

        private void SetLabel(Text t, string text, Color color, float scale)
        {
            if (t.font == null || (font != null && t.font != font)) t.font = font;
            t.text = text;
            t.fontSize = Mathf.Max(8, Mathf.RoundToInt((float)HudModel.TextPx * scale));
            t.color = color;
        }

        // Screen ends of a line between two units, cut where it passes behind the camera. False when nothing of it
        // is in front.
        private static bool LineEnds(Camera cam, HudUnit from, HudUnit to, float sw, float sh, out Vector2 a, out Vector2 b)
        {
            a = b = Vector2.zero;
            Vector3 pa = new Vector3((float)from.PX, (float)from.PY, (float)from.PZ), pb = new Vector3((float)to.PX, (float)to.PY, (float)to.PZ);
            Vector3 c = cam.transform.position, f = cam.transform.forward;
            float near = cam.nearClipPlane + 1f;
            float da = Vector3.Dot(pa - c, f), db = Vector3.Dot(pb - c, f);
            if (da < near && db < near) return false;
            if (da < near) pa = Vector3.Lerp(pa, pb, (near - da) / (db - da));
            else if (db < near) pb = Vector3.Lerp(pb, pa, (near - db) / (da - db));
            Vector3 va = cam.WorldToViewportPoint(pa), vb = cam.WorldToViewportPoint(pb);
            a = new Vector2(va.x * sw, va.y * sh); b = new Vector2(vb.x * sw, vb.y * sh);
            return (b - a).sqrMagnitude > 4f;
        }

        // Direction on screen from the middle of the safe area towards an off-screen or behind-camera unit, and where
        // on the safe area's edge the arrow sits.
        private static Vector2 EdgeDirection(Camera cam, Unit unit, float sw, float sh, double x0, double y0, double x1, double y1, out Vector2 at)
        {
            Vector3 local = cam.transform.InverseTransformPoint(unit.transform.position);
            Vector2 dir = new Vector2(local.x, local.y);
            if (local.z > 0)
            {
                Vector3 vp = cam.WorldToViewportPoint(unit.transform.position);
                dir = new Vector2((vp.x - 0.5f) * sw, (vp.y - 0.5f) * sh);
            }
            if (dir.sqrMagnitude < 1e-6f) dir = Vector2.down;
            dir.Normalize();
            Vector2 centre = new Vector2((float)(x0 + x1) / 2, (float)(y0 + y1) / 2);
            float hx = (float)(x1 - x0) / 2 - 20f, hy = (float)(y1 - y0) / 2 - 20f;
            float t = Mathf.Min(Mathf.Abs(dir.x) > 1e-5f ? hx / Mathf.Abs(dir.x) : float.MaxValue, Mathf.Abs(dir.y) > 1e-5f ? hy / Mathf.Abs(dir.y) : float.MaxValue);
            at = centre + dir * t;
            return dir;
        }
    }

    // Each unit's designated targets as the server last sent them (Unit.RpcSetStationTargets, replicated to every
    // client and so in the recording): an aircraft's target list, a SAM's or ship's turret targets. A Harmony postfix
    // on the RPC's handler notes when they change and logs the first few, to prove a replay client gets them.
    internal static class HudTargets
    {
        private static readonly Dictionary<string, float> changed = new Dictionary<string, float>();
        private static readonly Dictionary<string, string> primary = new Dictionary<string, string>();
        internal static int Messages;
        internal static ManualLogSource Log;
        private const int LogFirst = 40;

        public static void Clear() { changed.Clear(); primary.Clear(); }

        // Called by the patch after the game has applied the targets.
        internal static void Received(Unit unit, byte station)
        {
            try
            {
                if (unit == null) return;
                Messages++;
                string id = unit.persistentID.ToString();
                var list = Read(unit);
                string first = list != null && list.Count > 0 ? list[0] : null;
                primary.TryGetValue(id, out string had);
                if (first != had) { primary[id] = first; changed[id] = Time.time; }
                if (Messages <= LogFirst && Log != null)
                    Log.LogInfo(string.Format(CultureInfo.InvariantCulture, "Target HUD RPC {0}: SetStationTargets {1} ({2}) station {3} -> [{4}] at {5:F2}",
                        Messages, id, unit.definition != null ? unit.definition.code : "?", station, list != null ? string.Join(",", list) : "", Time.time));
                else if (Log != null && Messages % 200 == 0) Log.LogInfo($"Target HUD RPC: {Messages} target-list messages so far");
            }
            catch (Exception) { }
        }

        // The unit's targets, primary first, and when its primary target last changed.
        public static List<string> For(Unit unit, string id, float now, out float changedAt)
        {
            if (!changed.TryGetValue(id, out changedAt)) changedAt = -1000;
            return Read(unit);
        }

        private static List<string> Read(Unit unit)
        {
            List<string> list = null;
            try
            {
                if (unit is Aircraft a && a.weaponManager != null)
                {
                    foreach (var t in a.weaponManager.GetTargetList())
                        if (t != null && !t.disabled) (list ?? (list = new List<string>())).Add(t.persistentID.ToString());
                    if (list != null) return list;
                }
                if (unit.weaponStations == null) return null;
                foreach (var ws in unit.weaponStations)
                {
                    if (ws?.Turrets == null) continue;
                    foreach (var turret in ws.Turrets)
                    {
                        var t = turret != null ? turret.GetTarget() : null;
                        if (t == null || t.disabled) continue;
                        string tid = t.persistentID.ToString();
                        if (list == null) list = new List<string>();
                        if (!list.Contains(tid)) list.Add(tid);
                    }
                }
            }
            catch (Exception) { }
            return list;
        }
    }

    // The game's DebugUI toggles its debug panels on Rewired actions; PerfDebug (the performance and network panel,
    // "(toggle F4)") is on F4, which the viewer uses for the Target HUD. While the viewer has the keyboard, a frame
    // in which F1-F4 (the viewer's panel keys) went down toggles no debug panel. Without the viewer the game's keys
    // work as before.
    [HarmonyPatch(typeof(DebugUI), "CheckToggle")]
    internal static class DebugKeysPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref bool __result)
        {
            var p = ReplayPlugin.Instance;
            if (p == null || !p.ViewerOwnsKeys) return true;
            if (!Input.GetKeyDown(KeyCode.F1) && !Input.GetKeyDown(KeyCode.F2) && !Input.GetKeyDown(KeyCode.F3) && !Input.GetKeyDown(KeyCode.F4)) return true;
            __result = false;
            return false;
        }
    }

    // Patched on its own (ReplayPlugin.HudPatch), so a game update that changes the RPC cannot break the other patches.
    [HarmonyPatch(typeof(Unit), "UserCode_RpcSetStationTargets_1363862903")]
    internal static class HudTargetsPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Unit __instance, byte stationIndex) => HudTargets.Received(__instance, stationIndex);
    }
}
