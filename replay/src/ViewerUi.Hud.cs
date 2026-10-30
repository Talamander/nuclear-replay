using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearReplay.Playback
{
    // The Target HUD panel (F4, ReplayPlugin.Hud.cs): bottom right, above the bottom bar, like the Lens panel. One
    // line per setting with the selected one marked, and buttons for the same things the keys do.
    internal sealed partial class ViewerUi
    {
        private RectTransform hudPanelRect;
        private TextMeshProUGUI hudPanelText;

        private void BuildHud()
        {
            hudPanelRect = Rect("TargetHud", overlay, new Vector2(1, 0), new Vector2(1, 0), Vector2.zero, Vector2.zero);
            hudPanelRect.pivot = new Vector2(1, 0);
            hudPanelRect.sizeDelta = new Vector2(470, 0);
            hudPanelRect.anchoredPosition = new Vector2(-16, 196);
            Fill(hudPanelRect, panelColor, true);
            var layout = hudPanelRect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 12, 12);
            layout.spacing = 8;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            hudPanelRect.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Text(hudPanelRect, "<b>TARGET HUD</b>", 20, TextAlignmentOptions.TopLeft, accent);
            hudPanelText = Text(hudPanelRect, "", 17, TextAlignmentOptions.TopLeft);
            hudPanelText.enableWordWrapping = true;
            hudPanelText.overflowMode = TextOverflowModes.Overflow;
            var rows = Row(hudPanelRect, "Rows", 6);
            MakeButton(rows, "Up", 58, () => host.HudSelect(-1), 38, 15);
            MakeButton(rows, "Down (Tab)", 104, () => host.HudSelect(1), 38, 15);
            MakeButton(rows, "- (PgDn)", 86, () => host.HudAdjust(-1), 38, 15);
            MakeButton(rows, "+ (PgUp)", 86, () => host.HudAdjust(1), 38, 15);
            var more = Row(hudPanelRect, "More", 6);
            MakeButton(more, "Reset (Home)", 124, () => host.HudReset(), 38, 15);
            MakeButton(more, "Mode (Shift+F4)", 150, () => host.HudCycleMode(1), 38, 15);
            MakeButton(more, "Close (F4)", 104, () => host.ViewerToggleHudPanel(), 38, 15);
            hudPanelRect.gameObject.SetActive(false);
        }

        private void UpdateHud(State s)
        {
            bool open = s.HudOpen && s.HudText != null;
            if (hudPanelRect.gameObject.activeSelf != open) hudPanelRect.gameObject.SetActive(open);
            if (!open) return;
            float y = bottom.offsetMax.y + 10;
            if (Mathf.Abs(hudPanelRect.anchoredPosition.y - y) > 0.5f) hudPanelRect.anchoredPosition = new Vector2(-16, y);
            if (hudPanelText.text != s.HudText) hudPanelText.text = s.HudText;
            PointerOverViewerUi |= RectTransformUtility.RectangleContainsScreenPoint(hudPanelRect, Input.mousePosition, null);
        }
    }
}
