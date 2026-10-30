using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NuclearReplay.Playback
{
    // The Target HUD panel (F4, ReplayPlugin.Hud.cs): bottom right, above the bottom bar, like the Lens panel. One
    // line per setting with the selected one marked, and buttons for the same things the keys do.
    // The "Target HUD: <mode>" button at the end of the camera row shows the viewer's mode: a click steps to the next
    // mode, a right click to the previous one (the same as Shift+F4 and the panel's Mode row; the mode is saved in
    // [TargetHud] Mode). Hovering it shows a hint. It is part of the overlay, so it hides with the rest of the UI (H)
    // and while a viewer clip records.
    internal sealed partial class ViewerUi
    {
        private RectTransform hudPanelRect;
        private TextMeshProUGUI hudPanelText;
        private Button hudButton;
        private TextMeshProUGUI hudButtonLabel;
        private RectTransform hudHint;
        private string hudButtonMode;
        internal bool HudHoverTest;   // test hook: show the hint as if hovered (headless runs have no pointer)

        internal string HudButtonText => hudButton != null && hudButton.gameObject.activeInHierarchy ? hudButtonLabel.text : null;
        internal bool HudHintShown => hudHint != null && hudHint.gameObject.activeInHierarchy;

        // The button on screen in pixels from the bottom left (x, y, width, height), or null when hidden (tests).
        internal float[] HudButtonRect()
        {
            if (hudButton == null || !hudButton.gameObject.activeInHierarchy) return null;
            var corners = new Vector3[4];
            ((RectTransform)hudButton.transform).GetWorldCorners(corners);
            return new[] { corners[0].x, corners[0].y, corners[2].x - corners[0].x, corners[2].y - corners[0].y };
        }

        private void BuildHudButton(RectTransform presetsRow)
        {
            Gap(presetsRow, 10);
            hudButton = MakeButton(presetsRow, HudSettings.ButtonLabel("off"), 184, () => host.HudCycleMode(1), out hudButtonLabel, 40, 16);
            hudButton.gameObject.name = "Button Target HUD";
            hudButton.gameObject.AddComponent<HudButtonInput>().Ui = this;
            hudHint = Rect("Hint", hudButton.transform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), Vector2.zero, Vector2.zero);
            hudHint.pivot = new Vector2(0.5f, 0);
            hudHint.sizeDelta = new Vector2(500, 34);
            hudHint.anchoredPosition = new Vector2(0, 8);
            Fill(hudHint, new Color(0, 0, 0, 0.85f));
            Text(hudHint, HudSettings.ButtonHint, 16, TextAlignmentOptions.Center);
            hudHint.gameObject.SetActive(false);
        }

        // Right click: the previous mode (the Button component takes the left click).
        private sealed class HudButtonInput : MonoBehaviour, IPointerClickHandler
        {
            public ViewerUi Ui;
            public void OnPointerClick(PointerEventData e)
            {
                if (e.button != PointerEventData.InputButton.Right) return;
                try { Ui.host.HudCycleMode(-1); } catch (Exception x) { Debug.LogError(x); }
            }
        }

        // Test hook (viewer command "targethud click [right]"): a pointer click sent through the event system to the
        // button, as a mouse click would arrive.
        internal string ClickHudButton(bool right)
        {
            if (hudButton == null || !hudButton.gameObject.activeInHierarchy) return "Target HUD button not on screen";
            var data = new PointerEventData(EventSystem.current) { button = right ? PointerEventData.InputButton.Right : PointerEventData.InputButton.Left };
            data.position = RectTransformUtility.WorldToScreenPoint(null, hudButton.transform.position);
            ExecuteEvents.Execute(hudButton.gameObject, data, ExecuteEvents.pointerClickHandler);
            return "clicked " + (right ? "right" : "left");
        }

        private void UpdateHudButton(State s)
        {
            if (hudButtonMode != s.HudMode)
            {
                hudButtonMode = s.HudMode;
                bool on = s.HudMode != null && s.HudMode != "off";
                string label = HudSettings.ButtonLabel(s.HudMode);
                hudButtonLabel.text = on ? "<b>" + label + "</b>" : label;
                hudButton.image.color = on ? accent : Color.white;
            }
            bool hover = HudHoverTest || RectTransformUtility.RectangleContainsScreenPoint((RectTransform)hudButton.transform, Input.mousePosition, null);
            if (hudHint.gameObject.activeSelf != hover) hudHint.gameObject.SetActive(hover);
        }

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
            UpdateHudButton(s);
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
