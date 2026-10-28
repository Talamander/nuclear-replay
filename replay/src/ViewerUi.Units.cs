using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearReplay.Playback
{
    // The unit list (B in the viewer, ReplayPlugin.Cameras.cs): every unit of the recording, searchable by callsign
    // or player name, type and side, players first and then by type. A click or Enter targets the unit (or, opened
    // with Shift+B, makes it the two-shot's second unit). Above the bottom bar, on the left, like Camera Studio.
    internal sealed partial class ViewerUi
    {
        // What the list shows; ReplayPlugin fills it.
        internal sealed class UnitPanel
        {
            public bool Open, PickSecond, ShowDestroyed;
            public string Query = "", Filter = "aircraft", CurrentId, SecondId;
            public int Selected, Version;
            public List<UnitEntry> Rows = new List<UnitEntry>();
        }

        // True while the unit list takes typing: the free camera ignores WASD / Q / E.
        public static bool KeyboardTaken;
        public const int UnitRows = 14;

        private RectTransform unitsPanel;
        private TextMeshProUGUI unitsTitle, unitsSearch, unitsCount, unitsDestroyedLabel;
        private readonly List<Button> unitRowButtons = new List<Button>();
        private readonly List<TextMeshProUGUI> unitRowTexts = new List<TextMeshProUGUI>();
        private readonly List<Button> unitFilterButtons = new List<Button>();
        private Button rideButton, unitsButton;
        private TextMeshProUGUI rideLabel, unitsButtonLabel;
        private int unitsVersion = -1, unitsFirst;

        // The presets row's extra buttons: 0 ride-along and the unit list.
        private void BuildCameraButtons(RectTransform presetsRow)
        {
            rideButton = MakeButton(presetsRow, "0 " + ViewerPresets.Label("ride"), ViewerPresets.Label("ride").Length * 9f + 44, () => host.ViewerRide(), out rideLabel, 40, 16);
            Gap(presetsRow, 10);
            unitsButton = MakeButton(presetsRow, "Units (B)", 96, () => host.ViewerUnitList(false), out unitsButtonLabel, 40, 16);
        }

        private void BuildUnits()
        {
            unitsPanel = Rect("Units", overlay, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            unitsPanel.pivot = Vector2.zero;
            unitsPanel.sizeDelta = new Vector2(820, 0);
            unitsPanel.anchoredPosition = new Vector2(16, 196);
            Fill(unitsPanel, new Color(panelColor.r, panelColor.g, panelColor.b, 0.95f), true);
            var layout = unitsPanel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 12, 12);
            layout.spacing = 6;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            unitsPanel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            unitsTitle = Text(unitsPanel, "<b>UNITS</b>", 20, TextAlignmentOptions.TopLeft, accent);
            unitsTitle.gameObject.AddComponent<LayoutElement>().preferredHeight = 26;
            var filters = Row(unitsPanel, "Filters", 6);
            for (int i = 0; i < UnitList.Filters.Length; i++)
            {
                string f = UnitList.Filters[i];
                unitFilterButtons.Add(MakeButton(filters, UnitList.FilterLabels[i], 96, () => host.UnitListFilter(f), 34, 15));
            }
            Gap(filters, 10);
            MakeButton(filters, "Destroyed: off", 150, () => host.UnitListToggleDestroyed(), out unitsDestroyedLabel, 34, 15);
            var spacer = Rect("Spacer", filters, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            MakeButton(filters, "Close (Esc)", 110, () => host.UnitListClose(), 34, 15);

            unitsSearch = Text(unitsPanel, "", 18, TextAlignmentOptions.Left);
            unitsSearch.gameObject.AddComponent<LayoutElement>().preferredHeight = 28;
            for (int i = 0; i < UnitRows; i++)
            {
                int slot = i;
                var b = MakeButton(unitsPanel, "", 780, () => host.UnitListPick(unitsFirst + slot), out var text, 30, 16);
                text.alignment = TextAlignmentOptions.Left;
                text.richText = true;
                unitRowButtons.Add(b);
                unitRowTexts.Add(text);
            }
            unitsCount = Text(unitsPanel, "", 15, TextAlignmentOptions.Left, new Color(textColor.r, textColor.g, textColor.b, 0.7f));
            unitsCount.gameObject.AddComponent<LayoutElement>().preferredHeight = 22;
            unitsPanel.gameObject.SetActive(false);
        }

        private void UpdateUnits(State s)
        {
            if (rideButton != null)
            {
                bool rideOn = !s.FreeCam && s.Preset == "ride";
                rideLabel.text = (rideOn ? "<b>" : "") + "0 " + ViewerPresets.Label("ride") + (s.RideLocked ? "" : "...") + (rideOn ? "</b>" : "");
                rideButton.image.color = rideOn ? accent : Color.white;
            }
            var p = s.Units;
            bool open = p != null && p.Open;
            if (unitsButton != null)
            {
                unitsButtonLabel.text = open ? "<b>Units (B)</b>" : "Units (B)";
                unitsButton.image.color = open ? accent : Color.white;
            }
            if (unitsPanel.gameObject.activeSelf != open) unitsPanel.gameObject.SetActive(open);
            if (!open) { unitsVersion = -1; return; }
            float y = bottom.offsetMax.y + 10;
            if (Mathf.Abs(unitsPanel.anchoredPosition.y - y) > 0.5f) unitsPanel.anchoredPosition = new Vector2(16, y);
            PointerOverViewerUi |= RectTransformUtility.RectangleContainsScreenPoint(unitsPanel, Input.mousePosition, null);
            if (p.Version == unitsVersion) return;
            unitsVersion = p.Version;

            unitsTitle.text = p.PickSecond ? "<b>UNITS: PICK THE TWO-SHOT'S SECOND UNIT</b>" : "<b>UNITS</b>";
            for (int i = 0; i < unitFilterButtons.Count; i++)
                unitFilterButtons[i].image.color = UnitList.Filters[i] == p.Filter ? accent : Color.white;
            unitsDestroyedLabel.text = p.ShowDestroyed ? "Destroyed: shown" : "Destroyed: hidden";
            unitsSearch.text = "Search: <b>" + Escape(p.Query) + "</b><color=#FFB840>_</color>"
                + (p.Query.Length == 0 ? "   <alpha=#88>type a callsign, player, type or side" : "");

            // The visible window follows the selection.
            if (p.Selected < unitsFirst) unitsFirst = p.Selected;
            if (p.Selected >= unitsFirst + UnitRows) unitsFirst = p.Selected - UnitRows + 1;
            unitsFirst = Mathf.Clamp(unitsFirst, 0, Mathf.Max(0, p.Rows.Count - UnitRows));
            for (int i = 0; i < UnitRows; i++)
            {
                int row = unitsFirst + i;
                bool has = row < p.Rows.Count;
                if (unitRowButtons[i].gameObject.activeSelf != has) unitRowButtons[i].gameObject.SetActive(has);
                if (!has) continue;
                var u = p.Rows[row];
                string name = Escape(u.Label);
                if (!string.IsNullOrEmpty(u.Player)) name = "<b>" + name + "</b>";
                else name += " <alpha=#88>#" + u.Id + (u.Alive ? "<alpha=#FF>" : "");   // AI units share names; the id tells them apart
                string note = u.Id == p.CurrentId ? "  <color=#FFB840>on screen</color>" : u.Id == p.SecondId ? "  <color=#FFB840>second</color>" : "";
                string distance = u.Distance > 0 ? (u.Distance >= 1000 ? (u.Distance / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km" : u.Distance.ToString("0", CultureInfo.InvariantCulture) + " m") : "";
                unitRowTexts[i].text = (u.Alive ? "" : "<alpha=#88>") + name + note
                    + "<pos=44%>" + Escape(u.Type ?? UnitList.KindWord(u.Kind)) + "<pos=72%>" + Escape(u.Side ?? "-") + "<pos=88%>" + (u.Alive ? distance : "destroyed");
                unitRowButtons[i].image.color = row == p.Selected ? RowSelected : Color.white;
            }
            unitsCount.text = p.Rows.Count == 0 ? "No units match." :
                $"{p.Rows.Count} units. Up / Down and Enter or a click {(p.PickSecond ? "picks the second unit" : "targets the unit")}, Tab changes the filter, Escape closes.";
        }

        private static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : "<noparse>" + s.Replace("</noparse>", "") + "</noparse>";
    }
}
