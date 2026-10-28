using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NuclearReplay.Playback
{
    // The viewer's screens, built with the game's own UI system (uGUI + TextMeshPro) and the main menu's
    // font, button sprite and colours, so they look like part of Nuclear Option:
    //   - a "Replays" button cloned from the main menu's Missions button,
    //   - the Replays panel (recordings with map when recorded, date, length, players and kills),
    //   - the replay overlay: status line, timeline with launch/kill markers and the clip window, transport,
    //     speed, camera presets, unit and free camera buttons, clip controls, and a help panel.
    // All actions go back to ReplayPlugin; the UI only shows state.
    internal sealed partial class ViewerUi
    {
        // Snapshot of what the overlay shows, filled by ReplayPlugin every frame.
        internal sealed class State
        {
            public bool InReplay, Live, Paused, Seeking, Ended, FreeCam, Recording, HelpOpen, GameHud;
            public string SeekText, Following, Preset, Title, Subtitle, RecordText;
            public double Time, Duration, ClipIn = -1, ClipOut = -1;
            public float Speed = 1f;
            public ViewerEvent Focus;
            public List<ViewerEvent> Events = new List<ViewerEvent>();
            public FfmpegDownload Ffmpeg;
            public CameraStudio Studio;
            public bool StudioOpen, StudioPlaying;
            public bool LightingOpen, LightingSet;
            public string LightingText;
        }

        public static bool PointerOverViewerUi;
        public static TMP_FontAsset Font;

        private readonly ReplayPlugin host;
        private readonly ManualLogSource log;
        private GameObject root;
        private Canvas canvas;
        private RectTransform overlay, bottom, help, menu, menuList, toastBox, tooltip;
        private TextMeshProUGUI status, subStatus, seekLabel, toast, tooltipText, menuEmpty, playLabel, speedLabel, unitLabel, freeLabel, clipLabel, recordLabel;
        private readonly List<Button> presetButtons = new List<Button>();
        private Button freeButton;
        private RectTransform timeline, timelineFill, playhead, clipBand, markerLayer;
        private readonly List<KeyValuePair<RectTransform, ViewerEvent>> markers = new List<KeyValuePair<RectTransform, ViewerEvent>>();
        private List<ViewerEvent> markerSource;
        private ViewerEvent markerFocus;
        private double markerDuration;
        private float toastUntil;
        private int menuVersion = -1;
        private GameObject menuButton;
        private MainMenu menuButtonOwner;
        private ViewerDemo selectedDemo;
        private GameObject mapHeader;
        private LayoutElement missionHeader;
        private readonly List<KeyValuePair<Image, ViewerDemo>> rows = new List<KeyValuePair<Image, ViewerDemo>>();
        private TextMeshProUGUI menuHint, deleteLabel, deleteNote;
        private ViewerDemo deleteArmed;
        private float deleteArmedUntil;
        private RectTransform ffmpegPanel, ffmpegBar, ffmpegBarFill;
        private TextMeshProUGUI ffmpegTitle, ffmpegBody, ffmpegDetail, ffmpegPrimaryLabel, ffmpegSecondaryLabel;
        private Button ffmpegPrimary, ffmpegSecondary;

        // Looks borrowed from the main menu (fallbacks until the menu has been seen).
        private static Color buttonColor = new Color(0.10f, 0.13f, 0.16f, 0.92f);
        private static Color buttonHover = new Color(0.22f, 0.30f, 0.36f, 1f);
        private static Color textColor = new Color(0.92f, 0.94f, 0.95f, 1f);      // on dark panels
        private static Color buttonText = new Color(0.92f, 0.94f, 0.95f, 1f);     // on buttons (dark on the game's light buttons)
        private static readonly Color RowColor = new Color(0.10f, 0.13f, 0.16f, 0.92f);
        private static readonly Color RowSelected = new Color(0.55f, 0.40f, 0.12f, 0.95f);
        private static Color accent = new Color(1f, 0.72f, 0.25f, 1f);
        private static Color panelColor = new Color(0.03f, 0.05f, 0.07f, 0.82f);
        private static Sprite buttonSprite;
        private static readonly Color LaunchColor = new Color(1f, 0.78f, 0.25f, 1f);
        private static readonly Color KillColor = new Color(1f, 0.30f, 0.25f, 1f);
        private static readonly Color ClipColor = new Color(0.35f, 0.75f, 1f, 0.35f);

        public bool MenuOpen => menu != null && menu.gameObject.activeSelf;
        public Canvas Canvas => canvas;

        public ViewerUi(ReplayPlugin host, ManualLogSource log)
        {
            this.host = host;
            this.log = log;
        }

        // ---------------- building ----------------

        private void EnsureRoot()
        {
            if (root != null) return;
            if (Font == null) Font = TMP_Settings.defaultFontAsset;
            root = new GameObject("NuclearReplay Viewer UI");
            UnityEngine.Object.DontDestroyOnLoad(root);
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();
            BuildOverlay();
            BuildHelp();
            BuildMenu();
            BuildToast();
            BuildFfmpegPrompt();
            overlay.gameObject.SetActive(false);
            help.gameObject.SetActive(false);
            menu.gameObject.SetActive(false);
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = min; rt.anchorMax = max; rt.offsetMin = offMin; rt.offsetMax = offMax;
            return rt;
        }

        private static Image Fill(RectTransform rt, Color color, bool raycast = false)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        private static TextMeshProUGUI Text(Transform parent, string text, float size, TextAlignmentOptions align, Color? color = null)
        {
            var rt = Rect("Text", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null) t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = color ?? textColor;
            t.raycastTarget = false;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        private static Button MakeButton(Transform parent, string label, float width, Action onClick, out TextMeshProUGUI text, float height = 40, float size = 18)
        {
            var rt = Rect("Button " + label, parent, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.preferredHeight = height; le.minWidth = width * 0.75f;   // shrinks on narrow screens
            var img = Fill(rt, Color.white, true);
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }
            var b = rt.gameObject.AddComponent<Button>();
            var colors = b.colors;
            colors.normalColor = buttonColor; colors.highlightedColor = buttonHover; colors.selectedColor = buttonColor;
            colors.pressedColor = accent; colors.colorMultiplier = 1f; colors.fadeDuration = 0.08f;
            b.colors = colors;
            b.targetGraphic = img;
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            b.onClick.AddListener(() => { try { onClick(); } catch (Exception e) { Debug.LogError(e); } });
            text = Text(rt, label, size, TextAlignmentOptions.Center, buttonText);
            text.rectTransform.offsetMin = new Vector2(6, 0); text.rectTransform.offsetMax = new Vector2(-6, 0);
            return b;
        }

        private static Button MakeButton(Transform parent, string label, float width, Action onClick, float height = 40, float size = 18)
            => MakeButton(parent, label, width, onClick, out _, height, size);

        private static RectTransform Row(Transform parent, string name, float spacing = 6)
        {
            var rt = Rect(name, parent, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing; h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            h.childControlWidth = true; h.childControlHeight = true; h.childAlignment = TextAnchor.MiddleLeft;
            return rt;
        }

        private static void Gap(Transform parent, float width)
        {
            var rt = Rect("Gap", parent, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            rt.gameObject.AddComponent<LayoutElement>().preferredWidth = width;
        }

        private void BuildOverlay()
        {
            overlay = Rect("Overlay", root.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            // Top status bar.
            // Top centre: the game's kill feed lives in the top-left corner.
            var top = Rect("Top", overlay, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-560, -76), new Vector2(560, -12));
            var topBg = Rect("Bg", top, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Fill(topBg, panelColor);
            status = Text(topBg, "", 22, TextAlignmentOptions.TopLeft);
            status.rectTransform.offsetMin = new Vector2(14, 6); status.rectTransform.offsetMax = new Vector2(-14, -6);
            subStatus = Text(topBg, "", 17, TextAlignmentOptions.BottomLeft, new Color(textColor.r, textColor.g, textColor.b, 0.75f));
            subStatus.rectTransform.offsetMin = new Vector2(14, 6); subStatus.rectTransform.offsetMax = new Vector2(-14, -6);

            // Centre: "Jumping to 1:23" while seeking.
            var seek = Rect("Seek", overlay, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-260, -32), new Vector2(260, 32));
            Fill(seek, panelColor);
            seekLabel = Text(seek, "", 26, TextAlignmentOptions.Center, accent);

            // Bottom bar: timeline + controls.
            // At the bottom edge; it moves up while the game's HUD (and its unit panel) is on.
            bottom = Rect("Bottom", overlay, new Vector2(0, 0), new Vector2(1, 0), new Vector2(16, 16), new Vector2(-16, 186));
            Fill(bottom, panelColor, true);

            timeline = Rect("Timeline", bottom, new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -52), new Vector2(-16, -14));
            Fill(timeline, new Color(1, 1, 1, 0.08f), true);
            var click = timeline.gameObject.AddComponent<TimelineInput>();
            click.Ui = this;
            var bar = Rect("Bar", timeline, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, -3), new Vector2(0, 3));
            Fill(bar, new Color(1, 1, 1, 0.25f));
            clipBand = Rect("Clip", timeline, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            Fill(clipBand, ClipColor);
            timelineFill = Rect("Played", timeline, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, -3), new Vector2(0, 3));
            Fill(timelineFill, accent);
            markerLayer = Rect("Markers", timeline, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            BuildStudioTimeline();
            playhead = Rect("Playhead", timeline, new Vector2(0, 0), new Vector2(0, 1), new Vector2(-1.5f, -4), new Vector2(1.5f, 4));
            Fill(playhead, Color.white);

            tooltip = Rect("Tooltip", timeline, new Vector2(0, 1), new Vector2(0, 1), new Vector2(-200, 8), new Vector2(200, 40));
            Fill(tooltip, new Color(0, 0, 0, 0.85f));
            tooltipText = Text(tooltip, "", 16, TextAlignmentOptions.Center);
            tooltip.gameObject.SetActive(false);

            // Two rows of controls under the timeline: playback, unit and clip on top, camera presets below.
            var controls = Row(bottom, "Controls", 6);
            controls.anchorMin = new Vector2(0, 0); controls.anchorMax = new Vector2(1, 0);
            controls.offsetMin = new Vector2(16, 62); controls.offsetMax = new Vector2(-16, 104);
            var presetsRow = Row(bottom, "Presets", 6);
            presetsRow.anchorMin = new Vector2(0, 0); presetsRow.anchorMax = new Vector2(1, 0);
            presetsRow.offsetMin = new Vector2(16, 12); presetsRow.offsetMax = new Vector2(-16, 54);

            MakeButton(controls, "-10 s", 64, () => host.ViewerSkip(-10));
            MakeButton(controls, "Pause", 84, () => host.ViewerTogglePause(), out playLabel);
            MakeButton(controls, "+10 s", 64, () => host.ViewerSkip(10));
            Gap(controls, 10);
            MakeButton(controls, "-", 32, () => host.ViewerSpeed(-1));
            speedLabel = Label(controls, "1x", 60, 18);
            MakeButton(controls, "+", 32, () => host.ViewerSpeed(1));
            Gap(controls, 10);
            MakeButton(controls, "<", 32, () => host.ViewerUnit(-1));
            unitLabel = Label(controls, "", 220, 15);
            MakeButton(controls, ">", 32, () => host.ViewerUnit(1));
            freeButton = MakeButton(controls, "Free cam", 92, () => host.ViewerToggleFreeCam(), out freeLabel, 40, 16);
            studioButton = MakeButton(controls, "Camera path", 116, () => host.ViewerToggleStudio(), out studioLabel, 40, 16);
            lightingButton = MakeButton(controls, "Lighting", 96, () => host.ViewerToggleLighting(), out lightingLabel, 40, 16);
            Gap(controls, 10);
            MakeButton(controls, "In", 44, () => host.ViewerSetIn());
            MakeButton(controls, "Out", 50, () => host.ViewerSetOut());
            MakeButton(controls, "Clip this", 96, () => host.ViewerRecordClip(), out recordLabel, 40, 16);
            clipLabel = Label(controls, "", 150, 15);
            var spacer = Rect("Spacer", controls, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            MakeButton(controls, "Game HUD", 104, () => host.ViewerToggleGameHud(), 40, 16);
            MakeButton(controls, "Hide UI", 82, () => host.ViewerToggleHide(), 40, 16);
            MakeButton(controls, "Help", 60, () => ToggleHelp(), 40, 16);
            MakeButton(controls, "Exit", 56, () => host.ViewerExit(), 40, 16);

            for (int i = 0; i < ViewerPresets.Ids.Length; i++)
            {
                string id = ViewerPresets.Ids[i];
                presetButtons.Add(MakeButton(presetsRow, (i + 1) + " " + ViewerPresets.Labels[i], ViewerPresets.Labels[i].Length * 9f + 44, () => host.ViewerPreset(id), 40, 16));
            }

            BuildStudio();
            BuildLighting();
        }

        // A fixed-width text cell in a control row (speed, unit, clip in/out); it never shrinks to nothing.
        private static TextMeshProUGUI Label(Transform row, string text, float width, float size)
        {
            var box = Rect("Label", row, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            var le = box.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.minWidth = width * 0.8f; le.preferredHeight = 40;
            return Text(box, text, size, TextAlignmentOptions.Center);
        }

        private void BuildHelp()
        {
            // 454 wide at the top right; as tall as its text (a layout group and a size fitter follow the font).
            help = Rect("Help", root.transform, new Vector2(1, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            help.pivot = new Vector2(1, 1);
            help.anchoredPosition = new Vector2(-16, -88);
            help.sizeDelta = new Vector2(454, 0);
            Fill(help, panelColor, true);
            var layout = help.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(18, 18, 14, 14);
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            help.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var t = Text(help, HelpText, 17, TextAlignmentOptions.TopLeft);
            t.enableWordWrapping = true;
            t.overflowMode = TextOverflowModes.Overflow;
        }

        internal const string HelpText =
            "<b><size=22>REPLAY KEYS</size></b>\n\n" +
            "<b>Space</b>  play / pause\n" +
            "<b>Left / Right</b>  back / forward 10 s\n" +
            "<b>Up / Down</b> or <b>+ / -</b>  speed 0.25x to 4x\n" +
            "<b>Click the timeline</b>  jump; a marker jumps to that moment\n" +
            "<b>1 - 8</b>  cameras: cinematic chase, victim cut, missile follow, chase, orbit, flyby, cockpit, action cam\n" +
            "<b>[ / ]</b>  previous / next aircraft\n" +
            "<b>F</b>  free camera: <b>WASD</b> move, <b>Q/E</b> down/up, <b>Shift</b> faster, wheel sets speed, <b>Ctrl</b> + wheel zooms, hold <b>right mouse</b> to look\n" +
            "<b>P</b>  Camera Studio: <b>K</b> add key, <b>U</b> update, <b>X</b> delete, <b>[ ]</b> select, <b>, .</b> retime, <b>T</b> timing, <b>L</b> aim, <b>J</b> shake, <b>M</b> space, <b>Enter</b> play, <b>C</b> clip the path\n" +
            "<b>I / O</b>  clip in / out at the current time\n" +
            "<b>C</b>  clip this: plays the clip at 1x and records it (C again cancels)\n" +
            "<b>G</b>  the game's flight HUD on / off\n" +
            "<b>H</b>  hide all UI\n" +
            "<b>F2</b>  lighting: <b>Y</b> time (dawn, golden hour, dusk, night), <b>PgUp / PgDn</b> 15 min later / earlier, <b>N</b> haze, <b>V</b> weather, <b>Z</b> sun behind the subject, <b>Home</b> as recorded\n" +
            "<b>F1</b>  this help\n" +
            "<b>Backspace</b>  leave the replay";

        private void BuildToast()
        {
            toastBox = Rect("Toast", root.transform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-420, -150), new Vector2(420, -96));
            Fill(toastBox, panelColor);
            toast = Text(toastBox, "", 19, TextAlignmentOptions.Center);
            toast.enableWordWrapping = true;
            toast.overflowMode = TextOverflowModes.Overflow;
            toast.rectTransform.offsetMin = new Vector2(ToastPad, ToastPad); toast.rectTransform.offsetMax = new Vector2(-ToastPad, -ToastPad);
            toastBox.gameObject.SetActive(false);
        }

        // "Clip this" without ffmpeg: asks before downloading, then shows progress and errors (FfmpegDownload).
        // Below the top status bar, so the replay stays visible while the download runs.
        private void BuildFfmpegPrompt()
        {
            ffmpegPanel = Rect("Ffmpeg", root.transform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-460, -420), new Vector2(460, -164));
            Fill(ffmpegPanel, new Color(panelColor.r, panelColor.g, panelColor.b, 0.95f), true);
            var title = Rect("Title", ffmpegPanel, new Vector2(0, 1), new Vector2(1, 1), new Vector2(24, -54), new Vector2(-24, -12));
            ffmpegTitle = Text(title, "", 26, TextAlignmentOptions.Left, accent);
            var body = Rect("Body", ffmpegPanel, new Vector2(0, 1), new Vector2(1, 1), new Vector2(24, -114), new Vector2(-24, -58));
            ffmpegBody = Text(body, "", 20, TextAlignmentOptions.TopLeft);
            ffmpegBody.enableWordWrapping = true;
            var detail = Rect("Detail", ffmpegPanel, new Vector2(0, 1), new Vector2(1, 1), new Vector2(24, -170), new Vector2(-24, -116));
            ffmpegDetail = Text(detail, "", 16, TextAlignmentOptions.TopLeft, new Color(textColor.r, textColor.g, textColor.b, 0.7f));
            ffmpegDetail.enableWordWrapping = true;
            ffmpegBar = Rect("Bar", ffmpegPanel, new Vector2(0, 1), new Vector2(1, 1), new Vector2(24, -186), new Vector2(-24, -178));
            Fill(ffmpegBar, new Color(1, 1, 1, 0.1f));
            ffmpegBarFill = Rect("Fill", ffmpegBar, Vector2.zero, new Vector2(0, 1), Vector2.zero, Vector2.zero);
            Fill(ffmpegBarFill, accent);
            var buttons = Row(ffmpegPanel, "Buttons", 10);
            buttons.anchorMin = new Vector2(0, 0); buttons.anchorMax = new Vector2(1, 0);
            buttons.offsetMin = new Vector2(24, 16); buttons.offsetMax = new Vector2(-24, 62);
            buttons.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleRight;
            ffmpegSecondary = MakeButton(buttons, "No", 150, () => host.FfmpegPromptSecondary(), out ffmpegSecondaryLabel, 44, 20);
            ffmpegPrimary = MakeButton(buttons, "Yes, download", 220, () => host.FfmpegPromptPrimary(), out ffmpegPrimaryLabel, 44, 20);
            ffmpegPanel.gameObject.SetActive(false);
        }

        private void UpdateFfmpegPrompt(FfmpegDownload d)
        {
            bool show = d != null && d.Open;
            if (ffmpegPanel.gameObject.activeSelf != show)
            {
                ffmpegPanel.gameObject.SetActive(show);
                if (show) ffmpegPanel.SetAsLastSibling();
            }
            if (!show) return;
            var phase = d.State;
            bool modal = phase == FfmpegDownload.Phase.Asking || phase == FfmpegDownload.Phase.Failed;
            PointerOverViewerUi |= modal || RectTransformUtility.RectangleContainsScreenPoint(ffmpegPanel, Input.mousePosition, null);
            switch (phase)
            {
                case FfmpegDownload.Phase.Asking:
                    ffmpegTitle.text = "<b>DOWNLOAD FFMPEG?</b>";
                    ffmpegBody.text = d.Question;
                    break;
                case FfmpegDownload.Phase.Downloading:
                    ffmpegTitle.text = "<b>DOWNLOADING FFMPEG</b>";
                    ffmpegBody.text = $"From {d.Package.Host}, about {d.Package.SizeText}. Press C when it is done to record the clip.";
                    break;
                case FfmpegDownload.Phase.Installing:
                    ffmpegTitle.text = "<b>INSTALLING FFMPEG</b>";
                    ffmpegBody.text = "Almost done.";
                    break;
                case FfmpegDownload.Phase.Failed:
                    ffmpegTitle.text = "<b>FFMPEG WAS NOT INSTALLED</b>";
                    ffmpegBody.text = d.Error;
                    break;
            }
            ffmpegDetail.text = phase == FfmpegDownload.Phase.Failed
                ? "Try again later. Or download ffmpeg.exe yourself and put its path in FfmpegPath in BepInEx/config/talamander.nuclearreplay.player.cfg."
                : d.ProgressText;
            bool bar = phase == FfmpegDownload.Phase.Downloading || phase == FfmpegDownload.Phase.Installing;
            if (ffmpegBar.gameObject.activeSelf != bar) ffmpegBar.gameObject.SetActive(bar);
            if (bar) ffmpegBarFill.anchorMax = new Vector2(Mathf.Clamp01(d.Fraction), 1);
            ffmpegPrimary.gameObject.SetActive(modal);
            ffmpegSecondary.gameObject.SetActive(phase != FfmpegDownload.Phase.Installing);
            ffmpegPrimaryLabel.text = phase == FfmpegDownload.Phase.Failed ? "Try again  (Y)" : "Yes, download  (Y)";
            ffmpegSecondaryLabel.text = phase == FfmpegDownload.Phase.Asking ? "No  (N)"
                : phase == FfmpegDownload.Phase.Downloading ? "Cancel" : "Close  (N)";
        }

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
                var cell = Cell(header, label, width, 16, new Color(textColor.r, textColor.g, textColor.b, 0.6f));
                if (label == "Map") mapHeader = cell;
                if (label == "Mission") missionHeader = cell.GetComponent<LayoutElement>();
            }

            var view = Rect("List", menu, Vector2.zero, Vector2.one, new Vector2(28, 84), new Vector2(-28, -108));
            Fill(view, new Color(1, 1, 1, 0.03f), true);
            view.gameObject.AddComponent<RectMask2D>();
            menuList = Rect("Content", view, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            menuList.pivot = new Vector2(0.5f, 1);
            var v = menuList.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 4; v.childForceExpandHeight = false; v.childControlHeight = true; v.childControlWidth = true; v.childForceExpandWidth = true;
            menuList.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = menuList; scroll.horizontal = false; scroll.vertical = true; scroll.viewport = view;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 40;
            menuEmpty = Text(view, "Looking for recordings...", 20, TextAlignmentOptions.Center);

            var buttons = Row(menu, "Buttons", 10);
            buttons.anchorMin = new Vector2(0, 0); buttons.anchorMax = new Vector2(1, 0);
            buttons.offsetMin = new Vector2(28, 20); buttons.offsetMax = new Vector2(-28, 68);
            buttons.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleRight;
            // Delete sits apart on the left and asks first: the first click (or the Delete key) arms it, a second one
            // within a few seconds deletes.
            MakeButton(buttons, "Delete", 160, () => DeleteClicked(), out deleteLabel, 46, 20);
            var note = Rect("DeleteNote", buttons, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            var noteLayout = note.gameObject.AddComponent<LayoutElement>();
            noteLayout.flexibleWidth = 1; noteLayout.preferredHeight = 46;
            deleteNote = Text(note, "", 17, TextAlignmentOptions.Left, KillColor);
            deleteNote.rectTransform.offsetMin = new Vector2(8, 0);
            deleteNote.enableWordWrapping = true;
            MakeButton(buttons, "Refresh", 120, () => host.ViewerRefreshMenu(), 46, 20);
            MakeButton(buttons, "Close", 120, () => ShowMenu(false), 46, 20);
            MakeButton(buttons, "Watch", 160, () => { if (selectedDemo != null) host.ViewerOpen(selectedDemo, -1, -1); }, 46, 22);
        }

        private static readonly (string, float)[] Columns =
        {
            ("Mission", 360), ("Map", 170), ("Date", 190), ("Length", 100), ("Players", 230), ("Kills", 80), ("Launches", 90)
        };

        private static GameObject Cell(Transform row, string text, float width, float size, Color? color = null)
        {
            var rt = Rect("Cell", row, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.minWidth = width; le.preferredHeight = 30;
            var t = Text(rt, text, size, TextAlignmentOptions.Left, color);
            t.rectTransform.offsetMin = new Vector2(8, 0);
            return rt.gameObject;
        }

        private void FillMenu(List<ViewerDemo> demos, bool scanning)
        {
            foreach (Transform child in menuList) UnityEngine.Object.Destroy(child.gameObject);
            rows.Clear();
            menuEmpty.gameObject.SetActive(demos.Count == 0);
            menuEmpty.text = scanning ? "Looking for recordings..." : "No recordings yet. Host a mission with Nuclear Replay installed and it records automatically.";
            // Older recordings and the stock maps record no map name; the column shows only when one row has it.
            bool showMap = demos.Any(x => x.Error == null && x.Map != null);
            mapHeader.SetActive(showMap);
            // Without the Map column the Mission column takes its width, so the table still fills the panel.
            float missionWidth = showMap ? 360 : 530;
            if (menuHint != null && (!scanning || demos.Count > 0)) menuHint.text = ViewerText.LibrarySummary(demos.Count, demos.Sum(x => x.Size));
            missionHeader.preferredWidth = missionHeader.minWidth = missionWidth;
            foreach (var demo in demos)
            {
                var d = demo;
                var row = Row(menuList, "Row", 0);
                row.gameObject.AddComponent<LayoutElement>().preferredHeight = 40;
                var img = Fill(row, d == selectedDemo ? RowSelected : RowColor, true);
                var b = row.gameObject.AddComponent<Button>();
                var colors = b.colors; colors.normalColor = Color.white; colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f); colors.colorMultiplier = 1.5f;
                b.colors = colors; b.targetGraphic = img; b.navigation = new Navigation { mode = Navigation.Mode.None };
                float lastClick = -1;
                b.onClick.AddListener(() =>
                {
                    bool twice = selectedDemo == d && Time.unscaledTime - lastClick < 0.4f;
                    lastClick = Time.unscaledTime;
                    SelectDemo(d);
                    if (twice) host.ViewerOpen(d, -1, -1);
                });
                rows.Add(new KeyValuePair<Image, ViewerDemo>(img, d));
                string date = d.Created.HasValue ? d.Created.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : d.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                if (d.Error != null)
                {
                    Cell(row, d.FileName, 360, 18);
                    Cell(row, "Cannot read: " + d.Error, 800, 16, KillColor);
                    continue;
                }
                Cell(row, d.Title + (d.Truncated ? "  (cut short)" : ""), missionWidth, 18);
                if (showMap) Cell(row, d.Map ?? "-", 170, 17);
                Cell(row, date, 190, 17);
                Cell(row, ViewerCatalog.Clock(d.Duration), 100, 17);
                Cell(row, d.Players.Count == 0 ? "-" : string.Join(", ", d.Players), 230, 16);
                Cell(row, d.Kills.ToString(CultureInfo.InvariantCulture), 80, 17);
                Cell(row, d.Launches.ToString(CultureInfo.InvariantCulture), 90, 17);
            }
            SelectDemo(selectedDemo != null && demos.Contains(selectedDemo) ? selectedDemo : demos.FirstOrDefault(x => x.Error == null));
        }

        // ---------------- main menu ----------------

        // Called every frame while the main menu is up: adds the Replays button once per menu instance and
        // borrows the menu's font, button sprite and colours.
        public void AttachToMainMenu()
        {
            var main = UnityEngine.Object.FindObjectOfType<MainMenu>();
            if (main == null || main == menuButtonOwner && menuButton != null) return;
            menuButtonOwner = main;
            try
            {
                var field = typeof(MainMenu).GetField("missionsButton", BindingFlags.NonPublic | BindingFlags.Instance);
                var missions = field?.GetValue(main) as Button;
                if (missions == null) { log.LogWarning("Replays menu: the main menu's Missions button was not found"); EnsureRoot(); return; }
                BorrowLook(missions);
                EnsureRoot();
                menuButton = UnityEngine.Object.Instantiate(missions.gameObject, missions.transform.parent);
                menuButton.name = "NuclearReplay Replays";
                menuButton.transform.SetSiblingIndex(missions.transform.GetSiblingIndex() + 1);
                var button = menuButton.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(() => ShowMenu(true));
                // Other click hooks on the clone (event triggers) would open the missions screen too.
                foreach (var trigger in menuButton.GetComponentsInChildren<EventTrigger>(true)) UnityEngine.Object.Destroy(trigger);
                // Any other main menu button leaves the Replays screen, as the game's own screens do.
                foreach (var other in missions.transform.parent.GetComponentsInChildren<Button>(true))
                    if (other.gameObject != menuButton) other.onClick.AddListener(() => { if (MenuOpen) ShowMenu(false); });
                string original = missions.GetComponentInChildren<TMP_Text>(true)?.text ?? missions.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text ?? "Missions";
                string label = original.ToUpperInvariant() == original ? "REPLAYS" : "Replays";
                foreach (var t in menuButton.GetComponentsInChildren<TMP_Text>(true)) t.text = label;
                foreach (var t in menuButton.GetComponentsInChildren<UnityEngine.UI.Text>(true)) t.text = label;
                foreach (var l in menuButton.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && m.GetType().Name.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0))
                    UnityEngine.Object.Destroy(l);   // a localisation component would put "Missions" back
                log.LogInfo($"Replays menu: added the Replays button under {missions.transform.parent.name} (font {(Font != null ? Font.name : "default")})");
                if (LegacyWarning != null && !legacyWarned)
                {
                    legacyWarned = true;
                    Toast(LegacyWarning, 20f);
                }
            }
            catch (Exception e) { log.LogWarning("Replays menu: could not add the button: " + e); EnsureRoot(); }
        }

        private static void BorrowLook(Button missions)
        {
            var tmp = missions.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null && tmp.font != null) Font = tmp.font;
            if (Font == null) Font = TMP_Settings.defaultFontAsset;
            var img = missions.targetGraphic as Image ?? missions.GetComponent<Image>();
            if (img != null && img.sprite != null) buttonSprite = img.sprite;
            var c = missions.colors;
            if (img != null)
            {
                Color baseColor = img.color;
                buttonColor = Mul(baseColor, c.normalColor, 0.92f);
                buttonHover = Mul(baseColor, c.highlightedColor, 1f);
                if (buttonColor.a < 0.3f) buttonColor = new Color(0.10f, 0.13f, 0.16f, 0.92f);
                if (buttonHover.a < 0.3f || Approximately(buttonHover, buttonColor)) buttonHover = new Color(0.22f, 0.30f, 0.36f, 1f);
            }
            // Text on buttons: dark on light buttons (the game's look), light on dark ones.
            float lum = 0.299f * buttonColor.r + 0.587f * buttonColor.g + 0.114f * buttonColor.b;
            buttonText = lum > 0.5f ? new Color(0.08f, 0.09f, 0.10f, 1f) : new Color(0.92f, 0.94f, 0.95f, 1f);
            // Best guess: the colour the game itself draws on that button.
            if (tmp != null && tmp.color.a > 0.5f) buttonText = new Color(tmp.color.r, tmp.color.g, tmp.color.b, 1f);
            if (c.pressedColor.a > 0.3f && !Approximately(c.pressedColor, Color.white)) accent = new Color(c.pressedColor.r, c.pressedColor.g, c.pressedColor.b, 1f);
        }

        private static Color Mul(Color a, Color b, float alphaMin) => new Color(a.r * b.r, a.g * b.g, a.b * b.b, Mathf.Max(alphaMin * a.a * b.a, a.a * b.a));
        private static bool Approximately(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.05f;

        public void ShowMenu(bool show)
        {
            EnsureRoot();
            menu.gameObject.SetActive(show);
            Disarm();
            if (show) { host.ViewerRefreshMenu(); menuVersion = -1; }
            host.ViewerCursor(show || overlay.gameObject.activeSelf);
        }

        // Selects a recording and moves the row highlight to it (row clicks and the menu-pick test command).
        public void SelectDemo(ViewerDemo demo)
        {
            if (demo != deleteArmed) Disarm();
            selectedDemo = demo;
            foreach (var r in rows) r.Key.color = r.Value == demo ? RowSelected : RowColor;
        }
        public ViewerDemo SelectedDemo => selectedDemo;

        // Delete button / Delete key: arms on the first press, deletes the selected recording on the second.
        internal void DeleteClicked()
        {
            if (selectedDemo == null) return;
            if (deleteArmed != selectedDemo || Time.unscaledTime > deleteArmedUntil)
            {
                deleteArmed = selectedDemo;
                deleteArmedUntil = Time.unscaledTime + 6f;
                deleteLabel.text = "<b>Yes, delete</b>";
                deleteNote.text = ViewerText.DeleteQuestion(selectedDemo.Error != null ? selectedDemo.FileName : selectedDemo.Title, selectedDemo.Size);
                return;
            }
            var demo = selectedDemo;
            var list = rows.Select(r => r.Value).ToList();
            int at = list.IndexOf(demo);
            Disarm();
            string error = host.ViewerDelete(demo);
            if (error != null) { Toast("Could not delete the recording: " + error + ".", 5f); return; }
            list.Remove(demo);
            // Keep the place in the list: the next recording down, or the last one.
            selectedDemo = list.Count == 0 ? null : list[Math.Max(0, Math.Min(at, list.Count - 1))];
            Toast("Deleted " + (demo.Error != null ? demo.FileName : demo.Title) + ".", 3f);
        }

        private void Disarm()
        {
            deleteArmed = null;
            if (deleteLabel != null) deleteLabel.text = "Delete";
            if (deleteNote != null) deleteNote.text = "";
        }

        // ---------------- per frame ----------------

        // Screen point of a fraction of the timeline (test hook: clicks the bar there).
        internal Vector2 TimelineScreenPoint(double fraction)
        {
            var r = timeline.rect;
            var local = new Vector2(r.xMin + r.width * (float)fraction, r.center.y);
            return RectTransformUtility.WorldToScreenPoint(null, timeline.TransformPoint(local));
        }

        public void Update(State s, bool uiHidden, ViewerCatalog catalog)
        {
            if (root == null && !s.InReplay) return;
            EnsureRoot();
            if (EventSystem.current == null)
            {
                var es = new GameObject("NuclearReplay EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                es.transform.SetParent(root.transform, false);
            }
            PointerOverViewerUi = false;
            UpdateFfmpegPrompt(s.Ffmpeg);
            // The Replays screen closes with Escape, and when the main menu hides its button column (another screen
            // took over without a click on one of its buttons).
            if (MenuOpen && (Input.GetKeyDown(KeyCode.Escape) || menuButton != null && !menuButton.activeInHierarchy)) ShowMenu(false);
            if (MenuOpen)
            {
                if (catalog.Version != menuVersion && !catalog.Scanning || menuVersion == -1 && catalog.Demos.Count > 0)
                {
                    menuVersion = catalog.Version;
                    FillMenu(catalog.Demos, catalog.Scanning);
                }
                if (deleteArmed != null && Time.unscaledTime > deleteArmedUntil) Disarm();
                if (Input.GetKeyDown(KeyCode.Delete)) DeleteClicked();
                PointerOverViewerUi = true;
            }
            bool showOverlay = s.InReplay && !uiHidden && !s.Recording;
            if (overlay.gameObject.activeSelf != showOverlay) overlay.gameObject.SetActive(showOverlay);
            bool showHelp = s.InReplay && s.HelpOpen && !uiHidden && !s.Recording;
            if (help.gameObject.activeSelf != showHelp) help.gameObject.SetActive(showHelp);
            if (toastBox.gameObject.activeSelf && Time.unscaledTime > toastUntil) toastBox.gameObject.SetActive(false);
            if (s.Recording && toastBox.gameObject.activeSelf) toastBox.gameObject.SetActive(false);
            if (!showOverlay) return;

            string speed = s.Speed.ToString(s.Speed < 1f ? "0.##" : "0.#", CultureInfo.InvariantCulture) + "x";
            status.text = $"<b>{s.Title}</b>   {ViewerCatalog.Clock(s.Time)} / {ViewerCatalog.Clock(s.Duration)}   {speed}"
                + (s.Paused ? "   <color=#FFB840>PAUSED</color>" : "") + (s.Ended ? "   END" : "") + (!s.Live && !s.Seeking ? "   loading..." : "");
            subStatus.text = (s.FreeCam ? "Free camera" : "Camera: " + ViewerPresets.Label(s.Preset)) + (s.Following != null && !s.FreeCam ? "   Following " + s.Following : "")
                + (s.Focus != null && ViewerText.MomentInView(s.Time, s.Focus.T) ? "   Moment: " + s.Focus.Summary + " at " + ViewerCatalog.Clock(s.Focus.T) : "") + (s.Subtitle != null ? "   " + s.Subtitle : "");
            seekLabel.transform.parent.gameObject.SetActive(s.Seeking);
            if (s.Seeking) seekLabel.text = s.SeekText;
            // With the game's HUD on, its unit panel (speed, altitude, weapons) takes the bottom of the screen.
            float bottomY = s.GameHud ? 160 : 16;
            if (Mathf.Abs(bottom.offsetMin.y - bottomY) > 0.5f) { bottom.offsetMin = new Vector2(16, bottomY); bottom.offsetMax = new Vector2(-16, bottomY + 170); }
            playLabel.text = s.Paused ? "Play" : "Pause";
            speedLabel.text = speed;
            unitLabel.text = s.Following ?? "-";
            freeLabel.text = s.FreeCam ? "<b>Free cam</b>" : "Free cam";
            freeButton.image.color = s.FreeCam ? accent : Color.white;
            studioLabel.text = s.StudioOpen ? "<b>Camera path</b>" : "Camera path";
            studioButton.image.color = s.StudioOpen ? accent : Color.white;
            for (int i = 0; i < presetButtons.Count; i++)
            {
                var label = presetButtons[i].GetComponentInChildren<TextMeshProUGUI>();
                bool on = !s.FreeCam && ViewerPresets.Ids[i] == s.Preset;
                label.text = (on ? "<b>" : "") + (i + 1) + " " + ViewerPresets.Labels[i] + (on ? "</b>" : "");
                presetButtons[i].image.color = on ? accent : Color.white;
            }
            clipLabel.text = s.ClipIn >= 0 && s.ClipOut > s.ClipIn ? $"{ViewerCatalog.Clock(s.ClipIn)} - {ViewerCatalog.Clock(s.ClipOut)}" : "no clip set";
            recordLabel.text = s.RecordText ?? "Clip this";

            double duration = Math.Max(1, s.Duration);
            float width = timeline.rect.width;
            float x = (float)(Math.Min(1, Math.Max(0, s.Time / duration)) * width);
            playhead.anchoredPosition = new Vector2(x, 0);
            timelineFill.sizeDelta = new Vector2(x, 6);
            timelineFill.anchoredPosition = new Vector2(x / 2, 0);
            if (s.ClipIn >= 0 && s.ClipOut > s.ClipIn)
            {
                clipBand.gameObject.SetActive(true);
                float a = (float)(s.ClipIn / duration * width), b = (float)(s.ClipOut / duration * width);
                clipBand.sizeDelta = new Vector2(Mathf.Max(2, b - a), 0);
                clipBand.anchoredPosition = new Vector2((a + b) / 2, 0);
            }
            else clipBand.gameObject.SetActive(false);
            if (markerSource != s.Events || markerFocus != s.Focus || Math.Abs(markerDuration - duration) > 0.5 || markers.Count > 0 && markers[0].Key.rect.height <= 0)
                BuildMarkers(s.Events, s.Focus, duration);
            PlaceMarkers(duration, width);
            UpdateStudio(s, duration, width);
            UpdateLighting(s);
            PointerOverViewerUi |= RectTransformUtility.RectangleContainsScreenPoint(bottom, Input.mousePosition, null)
                || help.gameObject.activeSelf && RectTransformUtility.RectangleContainsScreenPoint(help, Input.mousePosition, null);
            UpdateTooltip(duration);
        }

        private void BuildMarkers(List<ViewerEvent> events, ViewerEvent focus, double duration)
        {
            foreach (var m in markers) UnityEngine.Object.Destroy(m.Key.gameObject);
            markers.Clear();
            markerSource = events; markerFocus = focus; markerDuration = duration;
            foreach (var e in events.Where(x => x.IsMoment))
            {
                bool kill = e.Event == "kill";
                var rt = Rect(e.Event, markerLayer, new Vector2(0, kill ? 0 : 0.2f), new Vector2(0, kill ? 1 : 0.8f), Vector2.zero, Vector2.zero);
                rt.sizeDelta = new Vector2(e == focus ? 5 : 3, 0);
                Fill(rt, e == focus ? Color.white : kill ? KillColor : LaunchColor);
                markers.Add(new KeyValuePair<RectTransform, ViewerEvent>(rt, e));
            }
            // Kills on top of launches.
            foreach (var m in markers.Where(m => m.Value.Event == "kill")) m.Key.SetAsLastSibling();
        }

        private void PlaceMarkers(double duration, float width)
        {
            foreach (var m in markers) m.Key.anchoredPosition = new Vector2((float)(m.Value.T / duration * width), 0);
        }

        // Time and nearest marker under a screen point on the timeline (marker within 7 px).
        internal bool HitTimeline(Vector2 screen, out double time, out ViewerEvent marker)
        {
            time = 0; marker = null;
            if (timeline == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(timeline, screen, null, out var local)) return false;
            var r = timeline.rect;
            float fx = (local.x - r.xMin) / Mathf.Max(1, r.width);
            if (fx < -0.01f || fx > 1.01f || local.y < r.yMin - 4 || local.y > r.yMax + 4) return false;
            double duration = Math.Max(1, markerDuration);
            time = Math.Max(0, Math.Min(duration, fx * duration));
            float best = 7f;
            foreach (var m in markers)
            {
                float px = Mathf.Abs((float)(m.Value.T / duration * r.width) - (local.x - r.xMin));
                if (px <= best) { best = px; marker = m.Value; }
            }
            return true;
        }

        private void UpdateTooltip(double duration)
        {
            if (HitTimeline(Input.mousePosition, out var t, out var marker) && RectTransformUtility.RectangleContainsScreenPoint(timeline, Input.mousePosition, null))
            {
                tooltip.gameObject.SetActive(true);
                tooltipText.text = marker != null ? ViewerCatalog.Clock(marker.T) + "  " + marker.Summary : ViewerCatalog.Clock(t);
                float x = (float)((marker?.T ?? t) / duration * timeline.rect.width);
                tooltip.anchoredPosition = new Vector2(Mathf.Clamp(x, 200, timeline.rect.width - 200), 0);
            }
            else tooltip.gameObject.SetActive(false);
        }

        internal void TimelineClicked(Vector2 screen)
        {
            if (!HitTimeline(screen, out var t, out var marker)) return;
            if (marker != null) host.ViewerJumpToMoment(marker);
            else host.ViewerSeek(t);
        }

        public void ToggleHelp() => host.ViewerToggleHelp();

        // Set when an old install (Nuclear Context) is loaded too: shown once as a toast on the main menu and in the
        // Replays menu header.
        internal string LegacyWarning;
        private bool legacyWarned;

        private const float ToastPad = 14, ToastTop = 96, ToastMaxWidth = 1100, ToastMinHeight = 26;

        // Shows a message under the top of the screen. The box fits the text: as wide as one line up to
        // ToastMaxWidth (or the screen), then the text wraps and the box grows down.
        public void Toast(string text, float seconds = 4f)
        {
            EnsureRoot();
            toast.text = text;
            float screen = ((RectTransform)root.transform).rect.width;
            if (screen < 400) screen = 1920;   // canvas not laid out yet
            float maxWidth = Mathf.Max(200, Mathf.Min(ToastMaxWidth, screen - 64) - 2 * ToastPad);
            float width = Mathf.Min(toast.GetPreferredValues(text, float.PositiveInfinity, 0).x, maxWidth);
            float height = Mathf.Max(ToastMinHeight, toast.GetPreferredValues(text, width, 0).y);
            float w = Mathf.Ceil(width) + 2 * ToastPad + 2, h = Mathf.Ceil(height) + 2 * ToastPad;
            toastBox.offsetMin = new Vector2(-w / 2, -ToastTop - h);
            toastBox.offsetMax = new Vector2(w / 2, -ToastTop);
            toastBox.gameObject.SetActive(true);
            toastUntil = Time.unscaledTime + seconds;
        }

        public void HideOverlayNow()
        {
            if (overlay != null) overlay.gameObject.SetActive(false);
            if (help != null) help.gameObject.SetActive(false);
            if (toastBox != null) toastBox.gameObject.SetActive(false);
        }

        private sealed class TimelineInput : MonoBehaviour, IPointerClickHandler
        {
            public ViewerUi Ui;
            public void OnPointerClick(PointerEventData e)
            {
                if (e.button == PointerEventData.InputButton.Left) Ui.TimelineClicked(e.position);
            }
        }
    }
}
