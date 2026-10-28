using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearReplay.Playback
{
    // The Lens panel (F3, ReplayPlugin.Lens.cs): bottom right, above the bottom bar. One line per setting with the
    // selected one marked, what is sharp, and buttons for the same things the keys do.
    internal sealed partial class ViewerUi
    {
        private RectTransform lensPanel;
        private TextMeshProUGUI lensText;

        private void BuildLens()
        {
            lensPanel = Rect("Lens", overlay, new Vector2(1, 0), new Vector2(1, 0), Vector2.zero, Vector2.zero);
            lensPanel.pivot = new Vector2(1, 0);
            lensPanel.sizeDelta = new Vector2(470, 0);
            lensPanel.anchoredPosition = new Vector2(-16, 196);
            Fill(lensPanel, panelColor, true);
            var layout = lensPanel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 12, 12);
            layout.spacing = 8;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            lensPanel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Text(lensPanel, "<b>LENS</b>", 20, TextAlignmentOptions.TopLeft, accent);
            lensText = Text(lensPanel, "", 17, TextAlignmentOptions.TopLeft);
            lensText.enableWordWrapping = true;
            lensText.overflowMode = TextOverflowModes.Overflow;
            var rows = Row(lensPanel, "Rows", 6);
            MakeButton(rows, "Up", 58, () => host.LensSelect(-1), 38, 15);
            MakeButton(rows, "Down (Tab)", 104, () => host.LensSelect(1), 38, 15);
            MakeButton(rows, "- (PgDn)", 86, () => host.LensAdjust(-1, false), 38, 15);
            MakeButton(rows, "+ (PgUp)", 86, () => host.LensAdjust(1, false), 38, 15);
            var more = Row(lensPanel, "More", 6);
            MakeButton(more, "Reset (Home)", 124, () => host.LensReset(), 38, 15);
            MakeButton(more, "All off", 84, () => host.LensAllOff(), 38, 15);
            MakeButton(more, "Close (F3)", 104, () => host.ViewerToggleLens(), 38, 15);
            lensPanel.gameObject.SetActive(false);
        }

        private void UpdateLens(State s)
        {
            bool open = s.LensOpen && s.LensText != null;
            if (lensPanel.gameObject.activeSelf != open) lensPanel.gameObject.SetActive(open);
            if (!open) return;
            float y = bottom.offsetMax.y + 10;
            if (Mathf.Abs(lensPanel.anchoredPosition.y - y) > 0.5f) lensPanel.anchoredPosition = new Vector2(-16, y);
            if (lensText.text != s.LensText) lensText.text = s.LensText;
            PointerOverViewerUi |= RectTransformUtility.RectangleContainsScreenPoint(lensPanel, Input.mousePosition, null);
        }
    }
}
