using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearReplay.Playback
{
    // The Replays screen: the recordings list (newest first) with a checkbox per row for bulk delete.
    //
    // The list is virtual: only the rows on screen exist (a small pool, re-filled as it scrolls), so a library of
    // hundreds of recordings opens as fast as one of four. Rows show at once from the file (name, date, size); the
    // other columns fill in as ViewerCatalog reads them in the background. Selection rules are in ListSelection.
    internal sealed partial class ViewerUi
    {
        private const float RowHeight = 40, RowGap = 4, RowStride = RowHeight + RowGap;
        private const float MissionWidth = 330, MissionWidthNoMap = 490;
        private static readonly Color RowChecked = new Color(0.17f, 0.27f, 0.34f, 0.96f);
        private static readonly Color BoxColor = new Color(0.85f, 0.88f, 0.90f, 0.85f);
        private static readonly Color BoxEmpty = new Color(0.06f, 0.08f, 0.10f, 1f);

        private static readonly (string, float)[] Columns =
        {
            ("", 44), ("Mission", MissionWidth), ("Map", 160), ("Date", 175), ("Length", 90), ("Size", 90), ("Players", 205), ("Kills", 70), ("Launches", 90)
        };

        private sealed class MenuRow
        {
            public RectTransform Root;
            public Image Back, Fill;
            public TextMeshProUGUI Mission, Map, Date, Length, Size, Players, Kills, Launches, Problem;
            public LayoutElement MissionLayout;
            public GameObject MapCell, ProblemCell;
            public GameObject[] DetailCells;
            public int Index = -1;
        }

        private RectTransform menuList, menuView;
        private TextMeshProUGUI menuEmpty, menuHint, deleteLabel, deleteNote, allLabel;
        private Image headerFill;
        private GameObject mapHeader;
        private LayoutElement missionHeader;
        private ViewerCatalog menuCatalog;
        private List<ViewerDemo> shown = new List<ViewerDemo>();
        private readonly Dictionary<string, ViewerDemo> shownByPath = new Dictionary<string, ViewerDemo>(StringComparer.OrdinalIgnoreCase);
        private readonly ListSelection selection = new ListSelection();
        private readonly List<MenuRow> pool = new List<MenuRow>();
        private int menuVersion = -1, menuDetailVersion = -1, boundFirst = -1, boundSelection = -1, pendingCurrent = -1;
        private bool showMap, menuDirty;
        private List<ViewerDemo> deleteArmed;
        private float deleteArmedUntil;
        private float lastRowClick = -10;
        private int lastRowClickIndex = -1;

        // Timings of the last open, for the log and the status file (test runs compare builds with them).
        private float menuOpenedAt;
        private bool menuRowsTimed, menuDetailsTimed;
        private double menuRowsMs = -1, menuDetailsMs = -1, menuMaxFrameMs, menuMaxWorkMs;
        private int menuFrames;

        public ViewerDemo SelectedDemo => selection.Current != null && shownByPath.TryGetValue(selection.Current, out var d) ? d : null;

        private static bool ShiftHeld => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        private static bool CtrlHeld => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);

        private void BuildMenu()
        {
            // Right of the main menu's button column.
            menu = Rect("Replays", root.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-450, -385), new Vector2(900, 330));
            Fill(menu, new Color(panelColor.r, panelColor.g, panelColor.b, 0.93f), true);
            var title = Rect("Title", menu, new Vector2(0, 1), new Vector2(1, 1), new Vector2(28, -70), new Vector2(-28, -14));
            Text(title, "<b>REPLAYS</b>", 34, TextAlignmentOptions.Left, accent);
            var hint = Rect("Hint", menu, new Vector2(0, 1), new Vector2(1, 1), new Vector2(28, -70), new Vector2(-28, -14));
            if (LegacyWarning != null) Text(hint, "Old Nuclear Context installed: delete BepInEx/plugins/NuclearContext", 17, TextAlignmentOptions.Right, new Color(1f, 0.35f, 0.3f));
            else menuHint = Text(hint, "Recordings made by Nuclear Replay", 17, TextAlignmentOptions.Right, new Color(textColor.r, textColor.g, textColor.b, 0.6f));

            var header = Row(menu, "Header", 0);
            header.anchorMin = new Vector2(0, 1); header.anchorMax = new Vector2(1, 1);
            header.offsetMin = new Vector2(28, -104); header.offsetMax = new Vector2(-28, -74);
            foreach (var (label, width) in Columns)
            {
                if (label == "")
                {
                    // Tick or clear every row.
                    headerFill = CheckBox(header, () => { Disarm(); selection.ToggleAll(); });
                    continue;
                }
                var cell = Cell(header, label, width, 16, new Color(textColor.r, textColor.g, textColor.b, 0.6f));
                if (label == "Map") mapHeader = cell;
                if (label == "Mission") missionHeader = cell.GetComponent<LayoutElement>();
            }

            menuView = Rect("List", menu, Vector2.zero, Vector2.one, new Vector2(28, 84), new Vector2(-28, -108));
            Fill(menuView, new Color(1, 1, 1, 0.03f), true);
            menuView.gameObject.AddComponent<RectMask2D>();
            menuList = Rect("Content", menuView, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            menuList.pivot = new Vector2(0.5f, 1);
            var scroll = menuView.gameObject.AddComponent<ScrollRect>();
            scroll.content = menuList; scroll.horizontal = false; scroll.vertical = true; scroll.viewport = menuView;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 40;
            menuEmpty = Text(menuView, "Looking for recordings...", 20, TextAlignmentOptions.Center);

            var buttons = Row(menu, "Buttons", 10);
            buttons.anchorMin = new Vector2(0, 0); buttons.anchorMax = new Vector2(1, 0);
            buttons.offsetMin = new Vector2(28, 20); buttons.offsetMax = new Vector2(-28, 68);
            buttons.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleRight;
            // Delete sits apart on the left and asks first: the first click (or the Delete key) arms it, a second one
            // within a few seconds deletes. With rows ticked it deletes those, else the highlighted one.
            MakeButton(buttons, "Delete", 160, () => DeleteClicked(), out deleteLabel, 46, 20);
            var note = Rect("DeleteNote", buttons, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            var noteLayout = note.gameObject.AddComponent<LayoutElement>();
            noteLayout.flexibleWidth = 1; noteLayout.preferredHeight = 46;
            deleteNote = Text(note, "", 17, TextAlignmentOptions.Left, KillColor);
            deleteNote.rectTransform.offsetMin = new Vector2(8, 0);
            deleteNote.enableWordWrapping = true;
            MakeButton(buttons, "Select all", 130, () => { Disarm(); if (selection.CheckedCount > 0) selection.Clear(); else selection.SelectAll(); }, out allLabel, 46, 20);
            MakeButton(buttons, "Refresh", 120, () => host.ViewerRefreshMenu(), 46, 20);
            MakeButton(buttons, "Close", 120, () => ShowMenu(false), 46, 20);
            MakeButton(buttons, "Watch", 160, () => { var d = SelectedDemo; if (d != null) host.ViewerOpen(d, -1, -1); }, 46, 22);
        }

        private static GameObject Cell(Transform row, string text, float width, float size, Color? color = null)
        {
            var rt = Rect("Cell", row, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.minWidth = width; le.preferredHeight = 30;
            var t = Text(rt, text, size, TextAlignmentOptions.Left, color);
            t.rectTransform.offsetMin = new Vector2(8, 0);
            return rt.gameObject;
        }

        // A square checkbox cell (44 px wide; the whole cell takes the click). Returns the fill that shows the tick.
        private static Image CheckBox(Transform row, Action onClick)
        {
            var cell = Rect("Check", row, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            var le = cell.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = 44; le.minWidth = 44; le.preferredHeight = 30;
            var hit = Fill(cell, new Color(0, 0, 0, 0), true);
            var b = cell.gameObject.AddComponent<Button>();
            b.targetGraphic = hit; b.transition = Selectable.Transition.None;
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            b.onClick.AddListener(() => { try { onClick(); } catch (Exception e) { Debug.LogError(e); } });
            var frame = Rect("Box", cell, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-11, -11), new Vector2(11, 11));
            Fill(frame, BoxColor);
            var inner = Rect("Inside", frame, Vector2.zero, Vector2.one, new Vector2(2, 2), new Vector2(-2, -2));
            Fill(inner, BoxEmpty);
            var tick = Rect("Tick", frame, Vector2.zero, Vector2.one, new Vector2(4, 4), new Vector2(-4, -4));
            return Fill(tick, new Color(0, 0, 0, 0));
        }

        private MenuRow MakeMenuRow()
        {
            var r = new MenuRow();
            var row = Row(menuList, "Row", 0);
            row.anchorMin = new Vector2(0, 1); row.anchorMax = new Vector2(1, 1); row.pivot = new Vector2(0.5f, 1);
            row.sizeDelta = new Vector2(0, RowHeight);
            r.Root = row;
            r.Back = Fill(row, RowColor, true);
            var b = row.gameObject.AddComponent<Button>();
            var colors = b.colors; colors.normalColor = Color.white; colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f); colors.colorMultiplier = 1.5f;
            b.colors = colors; b.targetGraphic = r.Back; b.navigation = new Navigation { mode = Navigation.Mode.None };
            b.onClick.AddListener(() => RowClicked(r.Index, ShiftHeld, CtrlHeld));
            r.Fill = CheckBox(row, () => BoxClicked(r.Index, ShiftHeld));
            TextMeshProUGUI T(GameObject cell) => cell.GetComponentInChildren<TextMeshProUGUI>();
            var mission = Cell(row, "", MissionWidth, 18);
            r.Mission = T(mission); r.MissionLayout = mission.GetComponent<LayoutElement>();
            r.MapCell = Cell(row, "", 160, 17); r.Map = T(r.MapCell);
            var date = Cell(row, "", 175, 17); r.Date = T(date);
            var length = Cell(row, "", 90, 17); r.Length = T(length);
            var size = Cell(row, "", 90, 17); r.Size = T(size);
            var players = Cell(row, "", 205, 16); r.Players = T(players);
            var kills = Cell(row, "", 70, 17); r.Kills = T(kills);
            var launches = Cell(row, "", 90, 17); r.Launches = T(launches);
            r.DetailCells = new[] { date, length, size, players, kills, launches };
            r.ProblemCell = Cell(row, "", 760, 16, KillColor); r.Problem = T(r.ProblemCell);
            r.ProblemCell.SetActive(false);
            row.gameObject.SetActive(false);
            return r;
        }

        public void ShowMenu(bool show)
        {
            EnsureRoot();
            menu.gameObject.SetActive(show);
            Disarm();
            if (show)
            {
                menuOpenedAt = Time.realtimeSinceStartup;
                menuRowsTimed = menuDetailsTimed = false;
                menuRowsMs = menuDetailsMs = -1; menuMaxFrameMs = menuMaxWorkMs = 0; menuFrames = 0;
                host.ViewerRefreshMenu();
                menuVersion = -1;
            }
            host.ViewerCursor(show || overlay.gameObject.activeSelf);
        }

        // Selects a recording and scrolls to it (the menu-pick test command).
        public void SelectDemo(ViewerDemo demo)
        {
            Disarm();
            int i = demo != null ? selection.IndexOf(demo.Path) : -1;
            selection.SetCurrent(i);
            if (i >= 0) ScrollTo(i);
        }

        // Scrolls so row i is on screen (at the top when it was above, at the bottom when it was below).
        internal void ScrollTo(int i, bool top = false)
        {
            if (menuList == null || menuView == null) return;
            float view = menuView.rect.height, y = menuList.anchoredPosition.y;
            float rowTop = i * RowStride, rowBottom = rowTop + RowHeight;
            if (top || rowTop < y) y = rowTop;
            else if (rowBottom > y + view) y = rowBottom - view;
            float max = Mathf.Max(0, menuList.sizeDelta.y - view);
            menuList.anchoredPosition = new Vector2(menuList.anchoredPosition.x, Mathf.Clamp(y, 0, max));
        }

        // A click on a row: plain selects (twice opens it), Ctrl ticks one, Shift ticks the range from the last click.
        internal void RowClicked(int i, bool shift, bool ctrl)
        {
            if (i < 0 || i >= shown.Count) return;
            bool twice = !shift && !ctrl && lastRowClickIndex == i && selection.CurrentIndex == i && Time.unscaledTime - lastRowClick < 0.4f;
            lastRowClickIndex = i; lastRowClick = Time.unscaledTime;
            Disarm();
            selection.Click(i, shift, ctrl);
            if (twice) host.ViewerOpen(shown[i], -1, -1);
        }

        // A click on a row's checkbox: ticks or unticks it; Shift does the same to the range from the last click.
        internal void BoxClicked(int i, bool shift)
        {
            if (i < 0 || i >= shown.Count) return;
            Disarm();
            selection.ToggleBox(i, shift);
        }

        internal void MenuSelectAll(bool on)
        {
            Disarm();
            if (on) selection.SelectAll(); else selection.Clear();
        }

        private List<ViewerDemo> DeleteTargets() =>
            selection.CheckedCount > 0
                ? selection.CheckedKeys().Select(k => shownByPath.TryGetValue(k, out var d) ? d : null).Where(d => d != null).ToList()
                : SelectedDemo != null ? new List<ViewerDemo> { SelectedDemo } : new List<ViewerDemo>();

        // Delete button / Delete key: arms on the first press (the note says what goes and how big it is), deletes on
        // the second. Ticked rows go together on a background thread; without ticks, the highlighted row goes.
        internal void DeleteClicked()
        {
            if (menuCatalog != null && menuCatalog.Deleting) return;
            var targets = DeleteTargets();
            if (targets.Count == 0) return;
            bool bulk = selection.CheckedCount > 0;
            if (deleteArmed == null || Time.unscaledTime > deleteArmedUntil || !deleteArmed.SequenceEqual(targets))
            {
                deleteArmed = targets;
                deleteArmedUntil = Time.unscaledTime + (bulk ? 10f : 6f);
                deleteLabel.text = "<b>Yes, delete</b>";
                var one = targets[0];
                deleteNote.color = KillColor;
                deleteNote.text = bulk ? ViewerText.BulkDeleteQuestion(targets.Count, targets.Sum(d => d.Size))
                    : ViewerText.DeleteQuestion(one.Error != null ? one.FileName : one.Title, one.Size);
                return;
            }
            Disarm();
            int at = targets.Select(d => selection.IndexOf(d.Path)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
            if (!bulk)
            {
                var demo = targets[0];
                string error = host.ViewerDelete(demo);
                if (error != null) { Toast("Could not delete the recording: " + error + ".", 5f); return; }
                // Keep the place in the list: the next recording down, or the last one.
                pendingCurrent = at; menuVersion = -1;
                Toast("Deleted " + (demo.Error != null ? demo.FileName : demo.Title) + ".", 3f);
                return;
            }
            host.ViewerDeleteMany(targets, (deleted, failed) =>
            {
                pendingCurrent = at; menuVersion = -1;
                var reasons = failed.Select(f => new KeyValuePair<string, string>(f.Key.Error != null ? f.Key.FileName : f.Key.Title, f.Value)).ToList();
                Toast(ViewerText.BulkDeleteResult(deleted.Count, deleted.Sum(d => d.Size), reasons), failed.Count > 0 ? 10f : 4f);
            });
        }

        private void Disarm()
        {
            deleteArmed = null;
            boundSelection = -1;   // the note and labels go back to the selection summary
        }

        // Called every frame while the Replays screen is open.
        private void UpdateMenu(ViewerCatalog catalog)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            menuCatalog = catalog;
            if (menuFrames++ > 0) menuMaxFrameMs = Math.Max(menuMaxFrameMs, Time.unscaledDeltaTime * 1000.0);
            if (catalog.Version != menuVersion)
            {
                menuVersion = catalog.Version;
                SetShown(catalog.Demos, catalog.Scanning);
            }
            if (catalog.DetailVersion != menuDetailVersion || menuDirty)
            {
                menuDetailVersion = catalog.DetailVersion;
                bool map = shown.Any(x => x.Detailed && x.Error == null && x.Map != null);
                if (map != showMap || mapHeader.activeSelf != map)
                {
                    showMap = map;
                    mapHeader.SetActive(map);
                    missionHeader.preferredWidth = missionHeader.minWidth = map ? MissionWidth : MissionWidthNoMap;
                }
                if (menuHint != null && (!catalog.Scanning || shown.Count > 0))
                    menuHint.text = ViewerText.LibrarySummary(shown.Count, shown.Sum(x => x.Size), catalog.Pending);
                menuDirty = true;
            }
            if (deleteArmed != null && (Time.unscaledTime > deleteArmedUntil || deleteArmed.Any(d => !shownByPath.ContainsKey(d.Path)))) Disarm();
            if (selection.Version != boundSelection)
            {
                boundSelection = selection.Version;
                menuDirty = true;
                headerFill.color = selection.State == ListSelection.Ticks.All ? accent
                    : selection.State == ListSelection.Ticks.Some ? new Color(accent.r, accent.g, accent.b, 0.45f) : new Color(0, 0, 0, 0);
                allLabel.text = selection.CheckedCount > 0 ? "Clear" : "Select all";
                if (deleteArmed == null && !catalog.Deleting)
                {
                    deleteLabel.text = ViewerText.DeleteLabel(selection.CheckedCount);
                    deleteNote.color = textColor;
                    deleteNote.text = selection.CheckedCount > 0
                        ? ViewerText.SelectionSummary(selection.CheckedCount, selection.CheckedKeys().Sum(k => shownByPath.TryGetValue(k, out var d) ? d.Size : 0))
                        : "";
                }
            }
            if (catalog.Deleting)
            {
                deleteLabel.text = "Deleting...";
                deleteNote.color = textColor;
                deleteNote.text = $"Deleting {catalog.DeleteDone} of {catalog.DeleteTotal}...";
                boundSelection = -1;
            }

            // Rows on screen: the pool covers the view plus one row; it re-fills when the first visible row changes.
            float view = menuView.rect.height;
            if (view > 1)
            {
                int need = Mathf.CeilToInt(view / RowStride) + 1;
                while (pool.Count < need) { pool.Add(MakeMenuRow()); menuDirty = true; }
            }
            int first = Mathf.Clamp(Mathf.FloorToInt(menuList.anchoredPosition.y / RowStride), 0, Math.Max(0, shown.Count - 1));
            catalog.ReadFocus = first;
            if (first != boundFirst || menuDirty)
            {
                boundFirst = first;
                menuDirty = false;
                for (int k = 0; k < pool.Count; k++) BindRow(pool[k], first + k);
            }

            if (Input.GetKeyDown(KeyCode.Delete)) DeleteClicked();
            if (CtrlHeld && Input.GetKeyDown(KeyCode.A)) MenuSelectAll(true);

            menuMaxWorkMs = Math.Max(menuMaxWorkMs, watch.Elapsed.TotalMilliseconds);
            if (!menuRowsTimed && shown.Count > 0 && pool.Count > 0)
            {
                menuRowsTimed = true;
                menuRowsMs = (Time.realtimeSinceStartup - menuOpenedAt) * 1000.0;
                log.LogInfo($"Replays menu: {shown.Count} rows on screen {menuRowsMs:F0} ms after opening ({catalog.Pending} still to read)");
            }
            if (menuRowsTimed && !menuDetailsTimed && !catalog.Busy && catalog.Pending == 0)
            {
                menuDetailsTimed = true;
                menuDetailsMs = (Time.realtimeSinceStartup - menuOpenedAt) * 1000.0;
                log.LogInfo($"Replays menu: all details {menuDetailsMs:F0} ms after opening; longest frame {menuMaxFrameMs:F1} ms, longest list update {menuMaxWorkMs:F1} ms");
            }
        }

        private void SetShown(List<ViewerDemo> demos, bool scanning)
        {
            shown = demos;
            shownByPath.Clear();
            foreach (var d in demos) shownByPath[d.Path] = d;
            selection.SetKeys(demos.Select(d => d.Path));
            if (pendingCurrent >= 0 && demos.Count > 0)
            {
                selection.SetCurrent(Math.Min(pendingCurrent, demos.Count - 1));
                pendingCurrent = -1;
            }
            else if (selection.Current == null)
                selection.SetCurrent(demos.FindIndex(x => !x.Detailed || x.Error == null));
            menuList.sizeDelta = new Vector2(0, Math.Max(0, demos.Count * RowStride - RowGap));
            menuEmpty.gameObject.SetActive(demos.Count == 0);
            menuEmpty.text = scanning ? "Looking for recordings..." : "No recordings yet. Host a mission with Nuclear Replay installed and it records automatically.";
            menuDirty = true;
        }

        private static void SetText(TextMeshProUGUI t, string s)
        {
            if (t.text != s) t.text = s;
        }

        private static void SetActive(GameObject g, bool on)
        {
            if (g.activeSelf != on) g.SetActive(on);
        }

        private void BindRow(MenuRow r, int i)
        {
            if (i < 0 || i >= shown.Count)
            {
                r.Index = -1;
                SetActive(r.Root.gameObject, false);
                return;
            }
            SetActive(r.Root.gameObject, true);
            r.Index = i;
            r.Root.anchoredPosition = new Vector2(0, -i * RowStride);
            var d = shown[i];
            bool ticked = selection.IsChecked(i);
            r.Back.color = selection.CurrentIndex == i ? RowSelected : ticked ? RowChecked : RowColor;
            r.Fill.color = ticked ? accent : new Color(0, 0, 0, 0);
            bool problem = d.Detailed && d.Error != null;
            float missionWidth = showMap ? MissionWidth : MissionWidthNoMap;
            if (Math.Abs(r.MissionLayout.preferredWidth - missionWidth) > 0.5f) r.MissionLayout.preferredWidth = r.MissionLayout.minWidth = missionWidth;
            SetText(r.Mission, problem ? d.FileName : d.Title + (d.Detailed && d.Truncated ? "  (cut short)" : ""));
            SetActive(r.ProblemCell, problem);
            SetActive(r.MapCell, !problem && showMap);
            foreach (var c in r.DetailCells) SetActive(c, !problem);
            if (problem) { SetText(r.Problem, "Cannot read: " + d.Error); return; }
            const string wait = "...";
            var date = (d.Created ?? d.Modified).ToLocalTime();
            SetText(r.Date, date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            SetText(r.Size, ViewerText.Size(d.Size));
            if (!d.Detailed)
            {
                SetText(r.Map, wait); SetText(r.Length, wait); SetText(r.Players, wait); SetText(r.Kills, wait); SetText(r.Launches, wait);
                return;
            }
            SetText(r.Map, d.Map ?? "-");
            SetText(r.Length, ViewerCatalog.Clock(d.Duration));
            SetText(r.Players, d.Players.Count == 0 ? "-" : string.Join(", ", d.Players));
            SetText(r.Kills, d.Kills.ToString(CultureInfo.InvariantCulture));
            SetText(r.Launches, d.Launches.ToString(CultureInfo.InvariantCulture));
        }

        // The status file's "menu" object (test runs).
        internal JObject MenuStatus()
        {
            var c = menuCatalog;
            return new JObject
            {
                ["open"] = MenuOpen, ["rows"] = shown.Count, ["pending"] = c?.Pending ?? -1, ["busy"] = c?.Busy ?? false,
                ["detailed"] = shown.Count(x => x.Detailed),
                ["checked"] = selection.CheckedCount,
                ["checked_bytes"] = selection.CheckedKeys().Sum(k => shownByPath.TryGetValue(k, out var d) ? d.Size : 0),
                ["current"] = selection.CurrentIndex, ["anchor"] = selection.IndexOf(selection.Anchor),
                ["first_visible"] = boundFirst, ["pool"] = pool.Count, ["armed"] = deleteArmed?.Count ?? 0,
                ["deleting"] = c != null && c.Deleting ? c.DeleteDone + "/" + c.DeleteTotal : null,
                ["note"] = deleteNote?.text, ["delete_label"] = deleteLabel?.text, ["hint"] = menuHint?.text,
                ["rows_ms"] = Math.Round(menuRowsMs, 1), ["details_ms"] = Math.Round(menuDetailsMs, 1),
                ["max_frame_ms"] = Math.Round(menuMaxFrameMs, 1), ["max_work_ms"] = Math.Round(menuMaxWorkMs, 2),
                ["list_ms"] = c != null ? Math.Round(c.ListMs, 1) : -1, ["read_ms"] = c != null ? Math.Round(c.DetailsMs, 1) : -1,
                ["from_cache"] = c?.FromCache ?? 0, ["from_disk"] = c?.FromDisk ?? 0,
                ["checked_rows"] = new JArray(selection.CheckedKeys().Select(k => selection.IndexOf(k))),
            };
        }
    }
}
