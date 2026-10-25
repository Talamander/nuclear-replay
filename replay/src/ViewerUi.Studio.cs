using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearReplay.Playback
{
    // Camera Studio's part of the overlay (ReplayPlugin.Studio.cs): the panel above the bottom bar, the path's span
    // and keys on the timeline, and the path itself drawn into the scene (a line through the keys and a numbered
    // dot on each key) while the studio is open and not playing.
    internal sealed partial class ViewerUi
    {
        private static readonly Color PathColor = new Color(0.78f, 0.50f, 1f, 0.40f);
        private static readonly Color PathLine = new Color(0.85f, 0.65f, 1f, 0.85f);
        private const int MaxLinePoints = 200;

        private Button studioButton;
        private TextMeshProUGUI studioLabel, studioText, studioPlayLabel;
        private RectTransform studioPanel, pathBand, pathTicks, gizmos;
        private readonly List<RectTransform> tickPool = new List<RectTransform>();
        private readonly List<RectTransform> segmentPool = new List<RectTransform>();
        private readonly List<RectTransform> dotPool = new List<RectTransform>();
        private readonly List<System.Numerics.Vector3> linePoints = new List<System.Numerics.Vector3>();
        private int lineVersion = -1;
        private CameraStudio lineStudio;

        // Under the timeline's markers: the path's span and a tick per key.
        private void BuildStudioTimeline()
        {
            pathBand = Rect("Camera path", timeline, new Vector2(0, 0), new Vector2(0, 0.3f), Vector2.zero, Vector2.zero);
            Fill(pathBand, PathColor);
            pathTicks = Rect("Camera path keys", timeline, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            pathBand.gameObject.SetActive(false);
        }

        private void BuildStudio()
        {
            // The scene drawing goes first, under every panel.
            gizmos = Rect("Camera path gizmos", overlay, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            gizmos.SetAsFirstSibling();
            gizmos.gameObject.SetActive(false);

            // Bottom left, just above the bottom bar; as tall as its text.
            studioPanel = Rect("Camera Studio", overlay, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            studioPanel.pivot = Vector2.zero;
            studioPanel.sizeDelta = new Vector2(700, 0);
            studioPanel.anchoredPosition = new Vector2(16, 196);
            Fill(studioPanel, panelColor, true);
            var layout = studioPanel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 12, 12);
            layout.spacing = 8;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            studioPanel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Text(studioPanel, "<b>CAMERA STUDIO</b>", 20, TextAlignmentOptions.TopLeft, accent);
            studioText = Text(studioPanel, "", 17, TextAlignmentOptions.TopLeft);
            studioText.enableWordWrapping = true;
            studioText.overflowMode = TextOverflowModes.Overflow;
            var buttons = Row(studioPanel, "Buttons", 6);
            MakeButton(buttons, "Add key (K)", 112, () => host.StudioAdd(), 38, 15);
            MakeButton(buttons, "Update (U)", 104, () => host.StudioUpdate(), 38, 15);
            MakeButton(buttons, "Delete (X)", 100, () => host.StudioDelete(), 38, 15);
            MakeButton(buttons, "Play (Enter)", 120, () => host.StudioPlayStop(), out studioPlayLabel, 38, 15);
            MakeButton(buttons, "Clip (C)", 88, () => host.StudioClipPath(), 38, 15);
            MakeButton(buttons, "Close (P)", 92, () => host.ViewerToggleStudio(), 38, 15);
            studioPanel.gameObject.SetActive(false);
        }

        private void UpdateStudio(State s, double duration, float width)
        {
            var studio = s.Studio;
            bool open = s.StudioOpen && studio != null;
            if (studioPanel.gameObject.activeSelf != open) studioPanel.gameObject.SetActive(open);
            if (open)
            {
                float y = bottom.offsetMax.y + 10;
                if (Mathf.Abs(studioPanel.anchoredPosition.y - y) > 0.5f) studioPanel.anchoredPosition = new Vector2(16, y);
                studioText.text = s.StudioPlaying ? "Playing the path. Enter stops." : studio.Describe();
                studioPlayLabel.text = s.StudioPlaying ? "Stop (Enter)" : "Play (Enter)";
                PointerOverViewerUi |= RectTransformUtility.RectangleContainsScreenPoint(studioPanel, Input.mousePosition, null);
            }

            // Timeline: the span the path shot covers, from its lead-in to the last key, and a tick per key.
            bool band = studio != null && studio.Count > 0 && (open || s.StudioPlaying);
            if (pathBand.gameObject.activeSelf != band) pathBand.gameObject.SetActive(band);
            int ticks = band ? studio.Count : 0;
            while (tickPool.Count < ticks)
            {
                var t = Rect("Key", pathTicks, new Vector2(0, 0), new Vector2(0, 0.45f), Vector2.zero, Vector2.zero);
                t.sizeDelta = new Vector2(3, 0);
                Fill(t, Color.white);
                tickPool.Add(t);
            }
            for (int i = 0; i < tickPool.Count; i++)
            {
                bool on = i < ticks;
                if (tickPool[i].gameObject.activeSelf != on) tickPool[i].gameObject.SetActive(on);
                if (!on) continue;
                tickPool[i].anchoredPosition = new Vector2((float)(studio.Keys[i].Rt / duration * width), 0);
                tickPool[i].GetComponent<Image>().color = i == studio.Selected ? accent : Color.white;
            }
            if (band)
            {
                float a = (float)(System.Math.Max(0, studio.ShotTime) / duration * width), b = (float)(studio.End / duration * width);
                pathBand.sizeDelta = new Vector2(Mathf.Max(2, b - a), 0);
                pathBand.anchoredPosition = new Vector2((a + b) / 2, 0);
            }

            bool draw = open && !s.StudioPlaying && studio.Count > 0;
            if (gizmos.gameObject.activeSelf != draw) gizmos.gameObject.SetActive(draw);
            if (draw) DrawPath(studio);
        }

        // The path in the scene: each point is projected through the camera that renders the view, every frame
        // (the camera moves); the points themselves are rebuilt only when the path changes.
        private void DrawPath(CameraStudio studio)
        {
            var cams = CameraStateManager.i;
            if (cams == null || cams.mainCamera == null) { gizmos.gameObject.SetActive(false); return; }
            var cam = CineCamera.RenderCamera(cams.mainCamera);
            float scale = canvas.scaleFactor > 0 ? canvas.scaleFactor : 1f;

            if (lineVersion != studio.Version || lineStudio != studio)
            {
                lineVersion = studio.Version; lineStudio = studio;
                linePoints.Clear();
                var line = studio.Count > 1 ? studio.WorldPath().Polyline : null;
                if (line != null)
                {
                    int stride = Mathf.Max(1, Mathf.CeilToInt(line.Count / (float)MaxLinePoints));
                    for (int i = 0; i < line.Count; i += stride) linePoints.Add(line[i]);
                    if ((line.Count - 1) % stride != 0) linePoints.Add(line[line.Count - 1]);
                }
            }

            int used = 0;
            bool havePrev = false;
            Vector2 prev = Vector2.zero;
            foreach (var p in linePoints)
            {
                bool ok = Project(cam, p, scale, out var here);
                if (ok && havePrev)
                {
                    Vector2 d = here - prev;
                    float len = d.magnitude;
                    if (len > 0.5f && len < 4000f)
                    {
                        if (used == segmentPool.Count)
                        {
                            var seg = Rect("Segment", gizmos, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                            seg.pivot = new Vector2(0, 0.5f);
                            Fill(seg, PathLine);
                            segmentPool.Add(seg);
                        }
                        var rt = segmentPool[used++];
                        if (!rt.gameObject.activeSelf) rt.gameObject.SetActive(true);
                        rt.anchoredPosition = prev;
                        rt.sizeDelta = new Vector2(len, 2.5f);
                        rt.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
                    }
                }
                prev = here; havePrev = ok;
            }
            for (int i = used; i < segmentPool.Count; i++) if (segmentPool[i].gameObject.activeSelf) segmentPool[i].gameObject.SetActive(false);

            while (dotPool.Count < studio.Count)
            {
                var dot = Rect("Key", gizmos, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                dot.sizeDelta = new Vector2(16, 16);
                Fill(dot, Color.white);
                var label = Text(dot, "", 18, TextAlignmentOptions.Bottom);
                label.rectTransform.offsetMin = new Vector2(-40, 18); label.rectTransform.offsetMax = new Vector2(40, 44);
                label.fontStyle = FontStyles.Bold;
                dotPool.Add(dot);
            }
            for (int i = 0; i < dotPool.Count; i++)
            {
                Vector2 at = Vector2.zero;
                bool on = i < studio.Count && Project(cam, studio.Keys[i].World, scale, out at);
                if (dotPool[i].gameObject.activeSelf != on) dotPool[i].gameObject.SetActive(on);
                if (!on) continue;
                dotPool[i].anchoredPosition = at;
                var color = i == studio.Selected ? accent : Color.white;
                dotPool[i].GetComponent<Image>().color = color;
                var label = dotPool[i].GetComponentInChildren<TextMeshProUGUI>();
                label.text = (i + 1).ToString();
                label.color = color;
            }
        }

        // Canvas position (bottom left origin) of a global position; false when it is behind the camera.
        private static bool Project(Camera cam, System.Numerics.Vector3 global, float scale, out Vector2 canvasPos)
        {
            Vector3 local = new GlobalPosition(new Vector3(global.X, global.Y, global.Z)).ToLocalPosition();
            Vector3 screen = cam.WorldToScreenPoint(local);
            canvasPos = new Vector2(screen.x, screen.y) / scale;
            return screen.z > 0.5f;
        }
    }
}
