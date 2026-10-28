using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearReplay.Playback
{
    // The Lighting panel (F2, ReplayPlugin.Lighting.cs): bottom right, above the bottom bar. Its text comes from
    // LightingPanel.Describe (ShotLighting.cs), which the tests check.
    internal sealed partial class ViewerUi
    {
        private Button lightingButton;
        private TextMeshProUGUI lightingLabel, lightingText;
        private RectTransform lightingPanel;

        private void BuildLighting()
        {
            lightingPanel = Rect("Lighting", overlay, new Vector2(1, 0), new Vector2(1, 0), Vector2.zero, Vector2.zero);
            lightingPanel.pivot = new Vector2(1, 0);
            lightingPanel.sizeDelta = new Vector2(560, 0);
            lightingPanel.anchoredPosition = new Vector2(-16, 196);
            Fill(lightingPanel, panelColor, true);
            var layout = lightingPanel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 12, 12);
            layout.spacing = 8;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            lightingPanel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Text(lightingPanel, "<b>LIGHTING</b>", 20, TextAlignmentOptions.TopLeft, accent);
            lightingText = Text(lightingPanel, "", 17, TextAlignmentOptions.TopLeft);
            lightingText.enableWordWrapping = true;
            lightingText.overflowMode = TextOverflowModes.Overflow;
            var time = Row(lightingPanel, "Time", 6);
            MakeButton(time, "Time (Y)", 96, () => host.LightingTimePreset(), 38, 15);
            MakeButton(time, "-15 min (PgDn)", 140, () => host.LightingTimeStep(-1), 38, 15);
            MakeButton(time, "+15 min (PgUp)", 140, () => host.LightingTimeStep(1), 38, 15);
            MakeButton(time, "Haze (N)", 96, () => host.LightingHaze(), 38, 15);
            var more = Row(lightingPanel, "More", 6);
            MakeButton(more, "Weather (V)", 118, () => host.LightingWeather(), 38, 15);
            MakeButton(more, "Sun behind (Z)", 140, () => host.LightingSunBehind(), 38, 15);
            MakeButton(more, "As recorded (Home)", 170, () => host.LightingAsRecorded(), 38, 15);
            MakeButton(more, "Close (F2)", 96, () => host.ViewerToggleLighting(), 38, 15);
            lightingPanel.gameObject.SetActive(false);
        }

        private void UpdateLighting(State s)
        {
            lightingLabel.text = s.LightingOpen ? "<b>Lighting</b>" : s.LightingSet ? "Lighting *" : "Lighting";
            lightingButton.image.color = s.LightingOpen ? accent : Color.white;
            if (lightingPanel.gameObject.activeSelf != s.LightingOpen) lightingPanel.gameObject.SetActive(s.LightingOpen);
            if (!s.LightingOpen) return;
            float y = bottom.offsetMax.y + 10;
            if (Mathf.Abs(lightingPanel.anchoredPosition.y - y) > 0.5f) lightingPanel.anchoredPosition = new Vector2(-16, y);
            lightingText.text = s.LightingText ?? "";
            PointerOverViewerUi |= RectTransformUtility.RectangleContainsScreenPoint(lightingPanel, Input.mousePosition, null);
        }
    }
}
