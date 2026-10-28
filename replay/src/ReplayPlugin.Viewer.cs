using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using NuclearReplay.Shared;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // The in-game replay viewer: the game itself is the player.
    //
    //   Main menu:  a "Replays" button next to Missions opens a list of recordings; picking one loads it.
    //   Direct:     -ncReplay <demo> -ncViewer [-ncAt <s>] [-ncFocus <s>] [-ncClipsDir <dir>]
    //               [-ncFfmpeg <ffmpeg.exe>] [-ncQuality <n>] [-ncEncoder auto|nvenc|vulkan|x264]
    //               -ncViewer without -ncReplay opens the Replays list once the main menu is up.
    //   Test hook:  -ncViewerCommands <file> (or NC_VIEWER_COMMANDS)  runs one command per line as lines are appended (see RunCommand)
    //               and writes <file>.status (JSON) every half second.
    //
    // Seeking: the replay only runs forward. Forward jumps use the fixed-step fast-forward of -ncStart; backward
    // jumps leave the match and load it again (the preview's soft restart), then fast-forward.
    public sealed partial class ReplayPlugin
    {
        private static readonly float[] ViewerSpeeds = { 0.25f, 0.5f, 1f, 2f, 4f };
        private const CursorFlags ViewerCursorFlag = (CursorFlags)0x1000;

        private bool viewer;                 // the running (or loading) replay belongs to the viewer
        private bool viewerMenuOnStart, viewerReopenMenu;
        private ViewerUi ui;
        private ViewerCatalog catalog;
        private ViewerDemo viewerDemo;
        private ViewerEvent viewerFocus;
        private double viewerAt = -1, viewerFocusAt = -1;
        private string viewerPreset = "cinematic_chase";
        private bool viewerGameHud;          // the game's flight HUD (bottom info bar) in the viewer; off by default
        private bool viewerPaused, viewerUiHidden, viewerHelp, viewerHudBefore, viewerLive;
        private float viewerSpeed = 1f;
        private double clipIn = -1, clipOut = -1;
        private double viewerSeekTarget = -1;
        private bool viewerRestarting;
        private FreeFlyCamera freeFly;
        private bool freeCamOn, resumeFreeCam;
        private double flyUntil;
        private Vector3 flyMove;
        private ClipRecorder recorder;
        private bool clipGrab, clipFinishing, clipRestoreUi;
        private double clipStartedWait;
        private ConfigEntry<string> clipsDirectory, ffmpegPath, encoderSetting;
        private FfmpegDownload ffmpegDownload;
        private ConfigEntry<int> clipQuality;
        private string commandsPath;
        private int commandsDone;
        private double nextCommandPoll;
        private double fpsWindowStart;
        private int fpsWindowFrames;
        private readonly Queue<double> fpsHistory = new Queue<double>();
        private double fpsNow;

        // FlightHud.EnableCanvas is forced off while this is true (ReplayPatches).
        internal bool ViewerHidesFlightHud => viewer && server != null && (!viewerGameHud || viewerUiHidden || recorder != null);

        private string GameData(params string[] parts) => Path.Combine(new[] { ModFolders.DataDir(BepInEx.Paths.GameRootPath) }.Concat(parts).ToArray());

        // Called from Awake before the idle check.
        private void ViewerAwake()
        {
            clipsDirectory = Config.Bind("Viewer", "ClipsDirectory", "", "Where \"Clip this\" saves videos. Empty: <game>/NuclearReplay/clips. nr passes its own with -ncClipsDir.");
            ffmpegPath = Config.Bind("Viewer", "FfmpegPath", "", "ffmpeg used for viewer clips. Empty: the copy downloaded into <game>/NuclearReplay/tools/ffmpeg. nr passes its own with -ncFfmpeg; the last one given is kept here.");
            clipQuality = Config.Bind("Viewer", "Quality", 19, "Clip quality (lower is better; CRF/CQ scale, 14-30).");
            encoderSetting = Config.Bind("Viewer", "Encoder", "auto", "auto, nvenc, vulkan or x264.");
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string Next(int offset = 1) => i + offset < args.Length ? args[i + offset] : null;
                switch (args[i])
                {
                    case "-ncViewer": viewer = true; break;
                    case "-ncAt": viewerAt = Num(Next(), -1); break;
                    case "-ncFocus": viewerFocusAt = Num(Next(), -1); break;
                    case "-ncClipsDir": if (Next() != null) clipsDirectory.Value = HostPath(Next()); break;
                    case "-ncFfmpeg": if (Next() != null) ffmpegPath.Value = HostPath(Next()); break;
                    case "-ncQuality": if (int.TryParse(Next(), out var q)) clipQuality.Value = Math.Max(10, Math.Min(40, q)); break;
                    case "-ncEncoder": if (Next() != null) encoderSetting.Value = Next().ToLowerInvariant(); break;
                    case "-ncViewerCommands": commandsPath = HostPath(Next()); break;
                }
            }
            // Test harnesses that launch through nr set the hook by environment instead.
            if (commandsPath == null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NC_VIEWER_COMMANDS")))
                commandsPath = HostPath(Environment.GetEnvironmentVariable("NC_VIEWER_COMMANDS"));
            catalog = new ViewerCatalog(Logger);
            catalog.AddDirectory(GameData("demos"));
            // Recordings an install from before the rename (Nuclear Context) made stay where they are and still show.
            string legacyDemos = ModFolders.LegacyDemos(BepInEx.Paths.GameRootPath);
            if (Directory.Exists(legacyDemos)) catalog.AddDirectory(legacyDemos);
            ui = new ViewerUi(this, Logger);
            ffmpegDownload = new FfmpegDownload(FfmpegPackage.Pinned, BepInEx.Paths.GameRootPath, Logger, FfmpegInstalled);
            FfmpegPackage.CleanPartials(BepInEx.Paths.GameRootPath); // a download the last session did not finish
            string ffmpeg = ViewerFfmpeg();
            if (ffmpeg != null) ClipRecorder.Probe(ffmpeg, encoderSetting.Value, Logger);
            StartCoroutine(ViewerEndOfFrame());
            if (!viewer) return;
            if (jobPath != null || previewPath != null) { viewer = false; Logger.LogWarning("-ncViewer is ignored with a render job or a preview."); return; }
            if (pendingPath == null) { viewerMenuOnStart = true; viewer = false; Logger.LogInfo("Viewer: opening the Replays list at the main menu"); return; }
            try { catalog.AddDirectory(Path.GetDirectoryName(pendingPath)); } catch (Exception) { }
            viewerDemo = catalog.Get(pendingPath);
            if (viewerFocusAt >= 0) viewerFocus = viewerDemo.Moments.OrderBy(e => Math.Abs(e.T - viewerFocusAt)).FirstOrDefault(e => Math.Abs(e.T - viewerFocusAt) < 1.0);
            if (viewerFocus != null) { clipIn = Math.Max(0, viewerFocus.T - 5); clipOut = viewerFocus.T + 6; }
            if (viewerAt < 0 && viewerFocus != null) viewerAt = Math.Max(0, viewerFocus.T - 5);
            BeginViewerSession(viewerAt);
            Logger.LogInfo($"Viewer: {viewerDemo.FileName}, {viewerDemo.Events.Count} events, start {viewerAt.ToString("F1", CultureInfo.InvariantCulture)} s"
                + (viewerFocus != null ? ", focus " + viewerFocus.Summary : ""));
        }

        private static double Num(string s, double fallback) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        // Fresh viewer state for the demo in pendingPath, starting at `at` seconds (-1: the beginning).
        private void BeginViewerSession(double at)
        {
            viewer = true;
            quitAtEnd = false;
            startAt = -1; startSeeking = false;
            preroll = 0;
            fastForwardSpeed = 8f; fastForwardStepFps = 60;
            viewerPaused = false; viewerSpeed = 1f; viewerUiHidden = false; viewerHelp = false; viewerGameHud = false;
            freeCamOn = false;
            if (at > 0.5) { startAt = at; startSeeking = true; viewerSeekTarget = at; }
            else viewerSeekTarget = -1;
        }

        // ---------------- per frame ----------------

        // Start of Update: main-thread work, the main-menu hook, test commands, the overlay.
        private void ViewerTick()
        {
            UnityMainThread.Drain();
            MeasureFps();
            PollCommands();
            bool atMenu = server == null && MainMenu.State == MainMenu.LoadingState.Loaded && !NetworkManagerNuclearOption.IsLoadingScene
                && (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Client.Active);
            if (atMenu && pendingPath == null && !viewerRestarting)
            {
                ui.AttachToMainMenu();
                if (viewerMenuOnStart || viewerReopenMenu) { viewerMenuOnStart = false; viewerReopenMenu = false; ui.ShowMenu(true); }
            }
            StudioTick();
            UnitListTick();
            ui.Update(BuildState(), viewerUiHidden, catalog);
            ApplyUnitPanel();
        }

        // The game's spectator unit panel (GameplayUICanvas/UnitDebug: speed, altitude, target, weapons) sits where the
        // viewer's bottom bar goes. It shows with the game HUD (G) only; a CanvasGroup hides it, so the game can keep
        // toggling the object itself.
        private CanvasGroup unitPanel;
        private double nextUnitPanelScan;
        private void ApplyUnitPanel()
        {
            if (!viewer || server == null) return;
            if (unitPanel == null && Time.unscaledTimeAsDouble >= nextUnitPanelScan)
            {
                nextUnitPanelScan = Time.unscaledTimeAsDouble + 1;
                var canvas = GameObject.Find("GameplayUICanvas");
                var panel = canvas != null ? canvas.transform.Find("UnitDebug") : null;
                if (panel != null) unitPanel = panel.GetComponent<CanvasGroup>() ?? panel.gameObject.AddComponent<CanvasGroup>();
            }
            if (unitPanel == null) return;
            bool show = viewerGameHud && !viewerUiHidden && recorder == null;
            float alpha = show ? 1f : 0f;
            if (unitPanel.alpha != alpha) { unitPanel.alpha = alpha; unitPanel.blocksRaycasts = show; }
        }

        private ViewerUi.State BuildState()
        {
            var s = new ViewerUi.State { InReplay = viewer, Ffmpeg = ffmpegDownload };
            if (!viewer) return s;
            s.Title = viewerDemo?.Title ?? "Replay";
            s.Events = viewerDemo?.Events ?? s.Events;
            s.Duration = server != null ? server.EndTime - server.StartTime : viewerDemo?.Duration ?? 0;
            s.Time = server != null ? server.Clock - server.StartTime : Math.Max(0, viewerSeekTarget);
            s.Live = viewerLive;
            s.Paused = viewerPaused;
            s.Speed = viewerSpeed;
            s.Seeking = startSeeking || server == null;
            s.SeekText = viewerSeekTarget >= 0 ? $"Jumping to {ViewerCatalog.Clock(viewerSeekTarget)}{(viewerRestarting || server == null ? " (reloading the mission)" : "")}..." : "Loading the replay...";
            s.Ended = finishedAt >= 0;
            s.FreeCam = freeCamOn;
            s.Preset = viewerPreset;
            s.Following = director?.CurrentUnit != null ? UnitLabel(director.CurrentUnit) : null;
            s.Focus = viewerFocus;
            s.ClipIn = clipIn; s.ClipOut = clipOut;
            s.Recording = recorder != null && !clipFinishing;
            s.RecordText = recorder != null ? "Recording..." : null;
            s.HelpOpen = viewerHelp;
            s.GameHud = viewerGameHud;
            if (freeCamOn && freeFly != null)
                s.Subtitle = $"speed {freeFly.BaseSpeed:F0} m/s" + (freeFly.Fov > 0 ? $"   lens {freeFly.Fov:F0} deg" : "");
            s.Studio = studio;
            s.StudioOpen = studio.Open;
            s.StudioPlaying = studio.Playing;
            s.Units = unitListPanel;
            s.UnitFilter = unitFilter;
            s.RideLocked = rideShot != null;
            return s;
        }

        private static string UnitLabel(Unit unit)
        {
            try
            {
                string player = Units.Player(unit);
                if (!string.IsNullOrEmpty(player) && (unit.unitName == null || unit.unitName.IndexOf(player, StringComparison.OrdinalIgnoreCase) < 0))
                    return player + " (" + unit.unitName + ")";
            }
            catch (Exception) { }
            return unit.unitName;
        }

        private void MeasureFps()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (fpsWindowStart == 0) { fpsWindowStart = now; fpsWindowFrames = 0; return; }
            fpsWindowFrames++;
            if (now - fpsWindowStart < 1) return;
            fpsNow = fpsWindowFrames / (now - fpsWindowStart);
            fpsWindowStart = now; fpsWindowFrames = 0;
            if (recorder == null && !startSeeking && server != null && !server.Paused)
            {
                fpsHistory.Enqueue(fpsNow);
                while (fpsHistory.Count > 5) fpsHistory.Dequeue();
            }
        }

        // Viewer keys (Update, replay running or loading). Returns true when the viewer owns the keyboard.
        private bool ViewerKeys()
        {
            if (!viewer) return false;
            if (recorder != null)
            {
                if (Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.Escape)) ViewerRecordClip();
                return true;
            }
            if (TypingInGameField()) return true;
            // The ffmpeg question (or its error) takes Y / Enter and N / Escape, and nothing else, until it is answered.
            if (ffmpegDownload.State == FfmpegDownload.Phase.Asking || ffmpegDownload.State == FfmpegDownload.Phase.Failed)
            {
                if (Input.GetKeyDown(KeyCode.Y) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) FfmpegPromptPrimary();
                else if (Input.GetKeyDown(KeyCode.N) || Input.GetKeyDown(KeyCode.Escape)) FfmpegPromptSecondary();
                return true;
            }
            // The unit list (B) takes the keyboard while it is open.
            if (CamerasKeys()) return true;
            // The game's own menu: bring the UI back so it can be seen.
            if (Input.GetKeyDown(KeyCode.Escape) && viewerUiHidden) ViewerToggleHide();
            if (Input.GetKeyDown(KeyCode.G)) ViewerToggleGameHud();
            if (Input.GetKeyDown(KeyCode.Space)) ViewerTogglePause();
            if (Input.GetKeyDown(KeyCode.LeftArrow)) ViewerSkip(-10);
            if (Input.GetKeyDown(KeyCode.RightArrow)) ViewerSkip(10);
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals)) ViewerSpeed(1);
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Minus)) ViewerSpeed(-1);
            for (int i = 0; i < ViewerPresets.Ids.Length; i++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) ViewerPreset(ViewerPresets.Ids[i]);
            if (Input.GetKeyDown(KeyCode.P)) ViewerToggleStudio();
            // Camera Studio takes [ ], C and its own keys while it is open.
            if (studio.Open) StudioKeys();
            else
            {
                if (Input.GetKeyDown(KeyCode.LeftBracket)) ViewerUnit(-1);
                if (Input.GetKeyDown(KeyCode.RightBracket)) ViewerUnit(1);
                if (Input.GetKeyDown(KeyCode.C)) ViewerRecordClip();
            }
            if (Input.GetKeyDown(KeyCode.F)) ViewerToggleFreeCam();
            if (Input.GetKeyDown(KeyCode.I)) ViewerSetIn();
            if (Input.GetKeyDown(KeyCode.O)) ViewerSetOut();
            if (Input.GetKeyDown(KeyCode.H)) ViewerToggleHide();
            if (Input.GetKeyDown(KeyCode.F1)) ViewerToggleHelp();
            if (Input.GetKeyDown(KeyCode.Backspace)) ViewerExit();
            if (freeCamOn && flyUntil > Time.realtimeSinceStartupAsDouble && freeFly != null) freeFly.ScriptMove += flyMove;
            return true;
        }

        private static bool TypingInGameField()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var selected = es != null ? es.currentSelectedGameObject : null;
            return selected != null && (selected.GetComponent<TMPro.TMP_InputField>() != null || selected.GetComponent<UnityEngine.UI.InputField>() != null);
        }

        // LateUpdate, after the director: clip recording and the seek bookkeeping.
        private void ViewerLateUpdate(double replayTime, bool live)
        {
            viewerLive = live;
            if (live && !startSeeking) viewerSeekTarget = -1;
            if (recorder == null || clipFinishing) return;
            if (startSeeking || !live) return;
            if (!recorder.Started && recorder.FpsBefore <= 0) recorder.FpsBefore = fpsHistory.Count > 0 ? fpsHistory.Average() : fpsNow;
            // Real time at 1x while recording.
            server.Paused = false; server.Speed = 1f;
            if (!recorder.Started && replayTime < recorder.From - 3 && Time.realtimeSinceStartupAsDouble - clipStartedWait > 1) { ViewerSeek(recorder.From - 1, keepClip: true); return; }
            if (recorder.OnFrame(replayTime)) clipGrab = true;
            bool endedEarly = recorder.Started && !recorder.Captured && server.Finished;
            if (recorder.Captured || endedEarly) FinishClip();
        }

        private IEnumerator ViewerEndOfFrame()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                if (!clipGrab) continue;
                clipGrab = false;
                try { recorder?.Grab(); }
                catch (Exception e) { Logger.LogError("Viewer clip frame grab failed: " + e); }
            }
        }

        // Called when a fast-forward seek reaches its target.
        private void ViewerSeekReached(double replayNow)
        {
            viewerRestarting = false;
            viewerSeekTarget = -1;
            if (recorder != null) { server.Paused = false; server.Speed = 1f; }
            else { server.Paused = viewerPaused; server.Speed = viewerSpeed; }
            if (resumeFreeCam && freeFly != null) { resumeFreeCam = false; freeFly.KeepPose = true; SetFreeCam(true); }
            else if (!freeCamOn) ApplyPreset(viewerPreset, replayNow);
        }

        // Right after TryStart made the server and director for a viewer replay.
        private void ViewerStarted()
        {
            if (!string.IsNullOrEmpty(viewerDemo?.Path)) catalog.AddDirectory(Path.GetDirectoryName(viewerDemo.Path));
            double now = 0;
            if (!startSeeking) viewerRestarting = false;
            freeCamOn = false;
            if (!startSeeking) resumeFreeCam = false;
            ApplyPreset(viewerPreset, now);
            server.Paused = !startSeeking && viewerPaused;
            server.Speed = startSeeking ? server.Speed : viewerSpeed;
        }

        // ---------------- actions (UI buttons, keys and test commands) ----------------

        internal void ViewerRefreshMenu() => catalog.Refresh();

        // Replays list "Delete": removes a recording for good. Returns null, or why not.
        internal string ViewerDelete(ViewerDemo demo)
        {
            if (demo == null) return "nothing selected";
            if (server != null && viewerDemo != null && string.Equals(viewerDemo.Path, demo.Path, StringComparison.OrdinalIgnoreCase))
                return "it is playing; leave the replay first";
            return catalog.Delete(demo);
        }

        internal void ViewerCursor(bool visible)
        {
            try { CursorManager.SetFlag(ViewerCursorFlag, visible); } catch (Exception) { }
        }

        internal void ViewerOpen(ViewerDemo demo, double at, double focus)
        {
            if (demo == null || demo.Error != null) return;
            Logger.LogInfo("Viewer: opening " + demo.Path);
            if (server != null) StopViewerReplay(reopenMenu: false);
            ui.ShowMenu(false);
            viewerDemo = catalog.Get(demo.Path);
            viewerFocus = focus >= 0 ? viewerDemo.Moments.OrderBy(e => Math.Abs(e.T - focus)).FirstOrDefault() : null;
            clipIn = viewerFocus != null ? Math.Max(0, viewerFocus.T - 5) : -1;
            clipOut = viewerFocus != null ? viewerFocus.T + 6 : -1;
            if (at < 0 && viewerFocus != null) at = Math.Max(0, viewerFocus.T - 5);
            BeginViewerSession(at);
            StudioReset();
            CamerasReset();
            shotListPath = null;
            pendingPath = demo.Path;
        }

        internal void ViewerTogglePause()
        {
            if (!viewer || recorder != null) return;
            viewerPaused = !viewerPaused;
            if (server != null && !startSeeking) server.Paused = viewerPaused;
        }

        internal void ViewerSpeed(int direction)
        {
            if (!viewer || recorder != null) return;
            int at = Array.IndexOf(ViewerSpeeds, viewerSpeed);
            if (at < 0) at = 2;
            at = Math.Max(0, Math.Min(ViewerSpeeds.Length - 1, at + direction));
            SetSpeed(ViewerSpeeds[at]);
        }

        private void SetSpeed(float speed)
        {
            viewerSpeed = Mathf.Clamp(speed, 0.25f, 4f);
            if (server != null && !startSeeking) server.Speed = viewerSpeed;
        }

        internal void ViewerSkip(double seconds)
        {
            if (!viewer || recorder != null) return;
            double now = server != null ? server.Clock - server.StartTime : Math.Max(0, viewerSeekTarget);
            if (startSeeking && viewerSeekTarget >= 0) now = viewerSeekTarget;
            ViewerSeek(now + seconds);
        }

        internal void ViewerSeek(double target) => ViewerSeek(target, keepClip: false);

        private void ViewerSeek(double target, bool keepClip)
        {
            if (!viewer || recorder != null && !keepClip) return;
            double duration = server != null ? server.EndTime - server.StartTime : viewerDemo?.Duration ?? 0;
            target = Math.Max(0, Math.Min(Math.Max(0, duration - 0.5), target));
            viewerSeekTarget = target;
            if (server == null)
            {
                // Still loading: start there instead.
                if (target > 0.5) { startAt = target; startSeeking = true; } else { startAt = -1; startSeeking = false; }
                return;
            }
            double now = server.Clock - server.StartTime;
            Logger.LogInfo($"Viewer: jump from {now:F1} s to {target:F1} s");
            if (target >= now - 0.05)
            {
                if (target - now < 0.3) { viewerSeekTarget = -1; return; }
                startAt = target;
                startSeeking = true;
                return;
            }
            // Backward: the replay only runs forward, so load the match again and fast-forward. A free camera
            // comes back where it was once the jump is done.
            if (freeCamOn) { resumeFreeCam = true; SetFreeCam(false); }
            startAt = target > 0.5 ? target : -1;
            startSeeking = target > 0.5;
            viewerRestarting = true;
            SoftRestart();
        }

        internal void ViewerJumpToMoment(ViewerEvent moment)
        {
            if (!viewer || moment == null || recorder != null) return;
            viewerFocus = moment;
            clipIn = Math.Max(0, moment.T - 5);
            clipOut = moment.T + 6;
            ui.Toast(moment.Summary + " at " + ViewerCatalog.Clock(moment.T), 3f);
            ViewerSeek(Math.Max(0, moment.T - 5));
        }

        internal void ViewerPreset(string id)
        {
            if (!viewer || recorder != null && !freeCamOn) return;
            PresetChosen(id);
            viewerPreset = id;
            StudioLeave();
            if (freeCamOn) SetFreeCam(false);
            if (server != null && director != null) ApplyPreset(id, server.Clock - server.StartTime);
        }

        // Replaces the director's shots with a camera style. Around the focused moment while it is near (the
        // same shots nr renders for that moment); otherwise the style's generic shot from now on.
        private void ApplyPreset(string id, double now)
        {
            if (director == null) return;
            // Camera Studio playback (or its clip) shows the studio's path instead.
            if (studio.Playing && studio.Count > 0) { director.ReplaceShots(new JArray(StudioShot()), now); return; }
            var moment = viewerFocus != null && now <= viewerFocus.T + 8 && now >= viewerFocus.T - 30 ? viewerFocus : null;
            double to = moment != null ? Math.Max(now + 3, moment.T + 6) : now + 36000;
            director.ReplaceShots(PresetShots(id, moment, now, to), now);
        }

        internal void ViewerUnit(int step)
        {
            if (!viewer || director == null || server == null) return;
            StudioLeave();
            if (freeCamOn) SetFreeCam(false);
            var unit = director.Follow(step, server.Clock - server.StartTime, unitFilter, out int position, out int count);
            TargetChosen(unit);
            if (unit != null) ui.Toast($"Following {UnitLabel(unit)}  ({position} of {count} {FilterWord(unitFilter, count)})", 2f);
            else ui.Toast(unitFilter == "aircraft" ? "No aircraft in the air" : $"No {FilterWord(unitFilter, 2)} left; B opens the unit list", 2f);
        }

        internal void ViewerToggleFreeCam()
        {
            if (!viewer || server == null) return;
            StudioLeave();
            SetFreeCam(!freeCamOn);
        }

        private void SetFreeCam(bool on)
        {
            var cameras = CameraStateManager.i;
            if (cameras == null) return;
            if (on == freeCamOn) return;
            if (on)
            {
                if (freeFly == null) freeFly = new FreeFlyCamera();
                director?.Shutdown();
                try { if (cameras.followingUnit != null) cameras.SetFollowingUnit(null); } catch (Exception e) { Logger.LogWarning("Free camera: " + e.Message); }
                cameras.SwitchState(freeFly);
                freeCamOn = true;
                ui.Toast("Free camera: WASD move, Q/E down/up, Shift faster, hold right mouse to look. F leaves.", 4f);
            }
            else
            {
                freeFly?.StopLook();
                freeCamOn = false;
                if (server != null) ApplyPreset(viewerPreset, server.Clock - server.StartTime);
            }
        }

        internal void ViewerSetIn()
        {
            if (!viewer || server == null || recorder != null) return;
            clipIn = server.Clock - server.StartTime;
            if (clipOut <= clipIn) clipOut = clipIn + 11;
            ui.Toast("Clip in " + ViewerCatalog.Clock(clipIn), 2f);
        }

        internal void ViewerSetOut()
        {
            if (!viewer || server == null || recorder != null) return;
            clipOut = server.Clock - server.StartTime;
            if (clipIn < 0 || clipIn >= clipOut) clipIn = Math.Max(0, clipOut - 11);
            ui.Toast("Clip out " + ViewerCatalog.Clock(clipOut), 2f);
        }

        internal void ViewerToggleHide()
        {
            if (!viewer) return;
            viewerUiHidden = !viewerUiHidden;
            if (viewerUiHidden) viewerHudBefore = hudHidden;
            hudHidden = viewerUiHidden || viewerHudBefore;
            nextHudScan = 0;
            ViewerCursor(!viewerUiHidden);
        }

        internal void ViewerToggleGameHud()
        {
            if (!viewer) return;
            viewerGameHud = !viewerGameHud;
            try { FlightHud.EnableCanvas(viewerGameHud); } catch (Exception) { }
            ui.Toast(viewerGameHud ? "Game HUD on" : "Game HUD off", 2f);
        }

        internal void ViewerToggleHelp() { if (viewer) viewerHelp = !viewerHelp; }

        internal void ViewerExit()
        {
            if (!viewer) return;
            StopViewerReplay(reopenMenu: true);
        }

        // Leaves the replay for the main menu (and its Replays list when reopenMenu).
        private void StopViewerReplay(bool reopenMenu)
        {
            Logger.LogInfo("Viewer: leaving the replay");
            // A clip that is still recording is dropped. One that is already encoding no longer needs the
            // replay, so it is left to finish and save in the background.
            if (recorder != null && !clipFinishing) recorder.Cancel();
            recorder = null; clipFinishing = false; clipRestoreUi = false;
            if (freeCamOn) { freeFly?.StopLook(); freeCamOn = false; }
            studio.Open = false; studio.Playing = false; studioClip = false;
            try { director?.Shutdown(); } catch (Exception) { }
            try { server?.Close(); } catch (Exception) { }
            server = null; director = null; finishedAt = -1; capturing = false;
            cockpitView.Leave();
            startSeeking = false; startAt = -1; viewerRestarting = false; viewerSeekTarget = -1;
            if (fastForwarding && savedVSync >= 0) { QualitySettings.vSyncCount = savedVSync; Application.targetFrameRate = savedTargetFrameRate; }
            fastForwarding = false;
            hiddenCanvases.Clear();
            hudHidden = hideHud.Value;
            Time.timeScale = 1f; Time.captureFramerate = 0;
            try { NetworkManagerNuclearOption.i?.Stop(true); } catch (Exception e) { Logger.LogWarning("Viewer exit: stop: " + e.Message); }
            factory = null;   // the next match the player starts uses the game's own network again
            viewer = false; viewerUiHidden = false; viewerHelp = false;
            ViewerCursor(false);
            viewerReopenMenu = reopenMenu;
            restartAfter = Time.realtimeSinceStartupAsDouble + 5;
        }

        // ---------------- clip this ----------------

        internal void ViewerRecordClip()
        {
            if (!viewer || server == null) return;
            if (recorder != null)
            {
                if (clipFinishing) return;
                recorder.Cancel();
                recorder = null;
                RestoreAfterClip();
                ui.Toast("Clip canceled", 3f);
                return;
            }
            if (ffmpegDownload.State == FfmpegDownload.Phase.Downloading || ffmpegDownload.State == FfmpegDownload.Phase.Installing)
            {
                ui.Toast($"ffmpeg is still downloading ({ffmpegDownload.Fraction * 100:0}%). Press C again when it is done.", 4f);
                return;
            }
            string ffmpeg = ViewerFfmpeg();
            if (ffmpeg == null)
            {
                // Ask first; nothing is downloaded until the player says yes.
                Logger.LogInfo("Viewer clip: no ffmpeg (config FfmpegPath \"" + ffmpegPath.Value + "\", none in " + FfmpegPackage.InstallDir(BepInEx.Paths.GameRootPath) + "); asking to download it");
                ffmpegDownload.Ask();
                ViewerCursor(true);
                return;
            }
            double now = server.Clock - server.StartTime;
            double from = clipIn >= 0 && clipOut > clipIn ? clipIn : Math.Max(0, now - 5);
            double to = clipIn >= 0 && clipOut > clipIn ? clipOut : now + 6;
            double duration = server.EndTime - server.StartTime;
            to = Math.Min(to, duration);
            if (to - from < 0.5) { ui.Toast("The clip is too short; set In and Out first (I / O).", 4f); return; }
            if (ClipRecorder.ProbedCodec == null) ClipRecorder.Probe(ffmpeg, encoderSetting.Value, Logger);
            string clips = string.IsNullOrEmpty(clipsDirectory.Value) ? GameData("clips") : clipsDirectory.Value;
            bool path = studio.Playing && studio.Count > 0;
            string style = path ? "path" : freeCamOn ? "free" : viewerPreset;
            var shots = path ? new JArray(StudioShot()) : PresetShots(viewerPreset, viewerFocus, from, to);
            var record = new JObject
            {
                ["version"] = 1, ["source"] = "viewer", ["demo"] = viewerDemo?.Path, ["mission"] = viewerDemo?.Title,
                ["camera_style"] = style, ["hud"] = false, ["ffmpeg"] = ffmpeg,
                ["moment"] = viewerFocus != null ? new JObject { ["t"] = viewerFocus.T, ["event"] = viewerFocus.Event, ["summary"] = viewerFocus.Summary } : null,
                ["shots"] = shots
            };
            try
            {
                recorder = new ClipRecorder(Logger, ffmpeg, clipQuality.Value, from, to, 60, clips, GameData("viewer-clips"),
                    viewerDemo?.Title ?? "clip", record, HostPath);
            }
            catch (Exception e) { Logger.LogError("Viewer clip: " + e); ui.Toast("Could not start the clip: " + e.Message, 6f); recorder = null; return; }
            recorder.FpsBefore = fpsHistory.Count > 0 ? fpsHistory.Average() : fpsNow;
            clipFinishing = false;
            clipRestoreUi = true;
            viewerHudBefore = hudHidden;
            hudHidden = true; nextHudScan = 0;
            ui.HideOverlayNow();
            ViewerCursor(false);
            clipStartedWait = Time.realtimeSinceStartupAsDouble;
            Logger.LogInfo($"Viewer clip: {from:F2}-{to:F2} s, camera {style}, encoder {recorder.Codec}, game fps before {recorder.FpsBefore:F1}");
            // Start one second before In so the camera has settled; jump when In is far away.
            if (now > from - 0.2 || from - now > 3)
                ViewerSeek(Math.Max(0, from - 1), keepClip: true);
            if (!freeCamOn && (now <= from + 0.2)) ApplyPreset(viewerPreset, now);
        }

        // The ffmpeg for clips: [Viewer] FfmpegPath (written by nr install or passed with -ncFfmpeg)
        // when that file exists, else the copy downloaded into <game>/NuclearReplay/tools/ffmpeg, else one an install from
        // before the rename downloaded into <game>/NuclearContext/tools/ffmpeg. Null: none exists.
        private string ViewerFfmpeg()
        {
            string configured = ffmpegPath.Value;
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
            return FfmpegPackage.FindExe(BepInEx.Paths.GameRootPath);
        }

        // Yes / Try again on the ffmpeg prompt.
        internal void FfmpegPromptPrimary()
        {
            if (ffmpegDownload.State != FfmpegDownload.Phase.Asking && ffmpegDownload.State != FfmpegDownload.Phase.Failed) return;
            Logger.LogInfo("ffmpeg: the player chose to download it");
            StartCoroutine(ffmpegDownload.Run());
            ViewerCursor(viewer && !viewerUiHidden);
        }

        // No / Cancel / Close on the ffmpeg prompt.
        internal void FfmpegPromptSecondary()
        {
            switch (ffmpegDownload.State)
            {
                case FfmpegDownload.Phase.Asking:
                    ffmpegDownload.Dismiss();
                    ui.Toast("Not downloaded. Clips need ffmpeg; press C again when you want it.", 4f);
                    break;
                case FfmpegDownload.Phase.Downloading: ffmpegDownload.Cancel(); return; // the prompt shows "cancelled"
                case FfmpegDownload.Phase.Failed: ffmpegDownload.Dismiss(); break;
                default: return;
            }
            ViewerCursor(viewer && !viewerUiHidden);
        }

        private void FfmpegInstalled(string path)
        {
            ClipRecorder.Probe(path, encoderSetting.Value, Logger);
            ui.Toast(viewer ? "ffmpeg is ready. Press C to record the clip." : "ffmpeg is ready. Clip this works in replays now.", 6f);
        }

        private void FinishClip()
        {
            clipFinishing = true;
            var rec = recorder;
            rec.Finish(done => UnityMainThread.Post(() =>
            {
                if (recorder == done) { recorder = null; clipFinishing = false; RestoreAfterClip(); }
                // Encoder and frame rates are in the log line ClipRecorder writes ("Viewer clip: wrote ...").
                ui.Toast(ViewerText.ClipToast(done.State == "done", Path.GetFileName(done.OutPath), done.AudioWritten, done.Error), 8f);
            }));
            // Stop on the last frame until the file is written, so the player sees where the clip ended.
            viewerPaused = true;
            server.Paused = true;
        }

        private void RestoreAfterClip()
        {
            if (!clipRestoreUi) return;
            clipRestoreUi = false;
            hudHidden = viewerHudBefore; nextHudScan = 0;
            if (server != null) { server.Speed = viewerSpeed; server.Paused = viewerPaused; }
            ViewerCursor(!viewerUiHidden);
        }

        // ---------------- test hook ----------------

        private void PollCommands()
        {
            if (commandsPath == null || Time.realtimeSinceStartupAsDouble < nextCommandPoll) return;
            nextCommandPoll = Time.realtimeSinceStartupAsDouble + 0.5;
            WriteStatus();
            string[] lines;
            try
            {
                if (!File.Exists(commandsPath)) return;
                using (var stream = new FileStream(commandsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) lines = reader.ReadToEnd().Split('\n');
            }
            catch (Exception) { return; }
            // The last element is a partial line until the newline arrives.
            for (; commandsDone < lines.Length - 1; commandsDone++)
            {
                string line = lines[commandsDone].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try { Logger.LogInfo("Viewer command: " + line + " -> " + RunCommand(line)); }
                catch (Exception e) { Logger.LogWarning("Viewer command failed: " + line + ": " + e.Message); }
            }
        }

        private string RunCommand(string line)
        {
            var parts = line.Split(new[] { ' ' }, 2);
            string cmd = parts[0].ToLowerInvariant(), arg = parts.Length > 1 ? parts[1].Trim() : "";
            double N(int index = 0, double fallback = 0) => Num(arg.Split(' ').ElementAtOrDefault(index), fallback);
            switch (cmd)
            {
                case "shot":
                    {
                        string path = HostPath(arg);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        ScreenCapture.CaptureScreenshot(path);
                        return "screenshot " + path;
                    }
                case "menu-button":
                    {
                        var button = GameObject.Find("NuclearReplay Replays")?.GetComponent<UnityEngine.UI.Button>();
                        if (button == null) return "no Replays button";
                        button.onClick.Invoke();
                        return "clicked " + button.name + " under " + button.transform.parent.name;
                    }
                case "menu-close": ui.ShowMenu(false); return "ok";
                // Clicks the main menu's own Missions button (the Replays screen should close).
                case "menu-missions":
                    {
                        var main = FindObjectOfType<MainMenu>();
                        var field = typeof(MainMenu).GetField("missionsButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var missions = main != null ? field?.GetValue(main) as UnityEngine.UI.Button : null;
                        if (missions == null) return "no Missions button";
                        missions.onClick.Invoke();
                        return "clicked Missions, menu open " + ui.MenuOpen;
                    }
                case "ffmpeg-yes": FfmpegPromptPrimary(); return ffmpegDownload.State.ToString();
                case "ffmpeg-no": FfmpegPromptSecondary(); return ffmpegDownload.State.ToString();
                case "menu-pick":
                    {
                        var demos = catalog.Demos;
                        int index = int.TryParse(arg, out var row) ? row : demos.FindIndex(d => d.FileName.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (index < 0 || index >= demos.Count) return "no row " + arg + " of " + demos.Count;
                        ui.SelectDemo(demos[index]);
                        return "selected " + demos[index].FileName;
                    }
                // Presses the list's Delete button: the first press asks, a second one deletes.
                case "menu-delete": ui.DeleteClicked(); return ui.SelectedDemo != null ? "selected " + ui.SelectedDemo.FileName : "nothing selected";
                case "menu-watch": if (ui.SelectedDemo == null) return "nothing selected"; ViewerOpen(ui.SelectedDemo, -1, -1); return "opening " + ui.SelectedDemo.FileName;
                case "seek": ViewerSeek(N()); return "seek " + N();
                case "skip": ViewerSkip(N()); return "skip " + N();
                case "pause": if (!viewerPaused) ViewerTogglePause(); return "paused";
                case "play": if (viewerPaused) ViewerTogglePause(); return "playing";
                case "speed": SetSpeed((float)N(0, 1)); return "speed " + viewerSpeed;
                case "faster": ViewerSpeed(1); return "speed " + viewerSpeed;
                case "slower": ViewerSpeed(-1); return "speed " + viewerSpeed;
                case "preset":
                    {
                        string id = int.TryParse(arg, out var n) && n >= 1 && n <= ViewerPresets.Ids.Length ? ViewerPresets.Ids[n - 1] : arg;
                        ViewerPreset(id);
                        return "preset " + id;
                    }
                case "unit":
                    {
                        if (freeCamOn) SetFreeCam(false);
                        int position = 0, count = 0;
                        var unit = director != null && server != null ? director.Follow((int)N(0, 1), server.Clock - server.StartTime, unitFilter, out position, out count) : null;
                        TargetChosen(unit);
                        return unit != null ? $"following {UnitLabel(unit)} ({position} of {count})" : "no aircraft";
                    }
                case "gamehud": ViewerToggleGameHud(); return "game hud " + viewerGameHud;
                case "free": SetFreeCam(arg != "off"); return "free " + freeCamOn;
                case "studio": return StudioCommand(arg);
                case "msgstats":
                    {
                        // Research for docs/JUMP-POINTS.md: message types, counts and bytes in a recording, split into
                        // the join burst (the first 3 seconds after the first message) and the rest.
                        // Usage: msgstats <out.json>[|<recording>]  (default: the open replay, else the newest recording)
                        var paths = arg.Split('|');
                        string outPath = HostPath(paths[0].Trim());
                        string demoPath = paths.Length > 1 ? HostPath(paths[1].Trim()) : viewerDemo?.Path ?? catalog.Demos.FirstOrDefault()?.Path;
                        var reader = NuclearReplay.Demo.DemoReader.Load(demoPath);
                        double join = 3, first = double.NaN;
                        var stats = new SortedDictionary<string, long[]>();
                        foreach (var r in reader.Records)
                        {
                            if (r.Kind != NuclearReplay.Demo.DemoFormat.KindMessage) continue;
                            if (double.IsNaN(first)) first = r.Time;
                            string name = Mirage.Serialization.MessagePacker.MessageTypes.TryGetValue(r.MessageId, out var type) ? type.FullName : "id " + r.MessageId;
                            if (!stats.TryGetValue(name, out var v)) stats[name] = v = new long[4];
                            int slot = r.Time - first <= join ? 0 : 2;
                            v[slot]++; v[slot + 1] += r.Payload.Length;
                        }
                        var o = new JObject { ["demo"] = demoPath, ["join_seconds"] = join, ["duration"] = reader.Records.Count > 0 ? reader.Records[reader.Records.Count - 1].Time - first : 0 };
                        var types = new JObject();
                        foreach (var kv in stats.OrderByDescending(k => k.Value[1] + k.Value[3]))
                            types[kv.Key] = new JObject { ["join_count"] = kv.Value[0], ["join_bytes"] = kv.Value[1], ["rest_count"] = kv.Value[2], ["rest_bytes"] = kv.Value[3] };
                        o["types"] = types;
                        File.WriteAllText(outPath, o.ToString());
                        return "message stats " + outPath;
                    }
                case "uiat":
                    {
                        // Game UI under a screen point (pixels from the bottom left): which objects the overlay covers.
                        var point = new Vector2((float)N(0), (float)N(1));
                        var hits = new List<string>();
                        foreach (var g in FindObjectsOfType<UnityEngine.UI.Graphic>())
                        {
                            if (g.canvas == null || g.canvas.rootCanvas == ui?.Canvas || !g.isActiveAndEnabled) continue;
                            if (!RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, point, g.canvas.rootCanvas.worldCamera)) continue;
                            var path = g.name; var t = g.transform.parent;
                            while (t != null) { path = t.name + "/" + path; t = t.parent; }
                            hits.Add(path);
                        }
                        return string.Join(" | ", hits.Take(20));
                    }
                case "fly":
                    flyMove = new Vector3((float)N(0), (float)N(1), (float)N(2));
                    flyUntil = Time.realtimeSinceStartupAsDouble + N(3, 1);
                    return "fly " + flyMove;
                case "look": if (freeFly != null) freeFly.ScriptLook += new Vector2((float)N(0), (float)N(1)); return "look";
                case "flyspeed": if (freeFly != null) freeFly.BaseSpeed = (float)N(0, 60); return "fly speed " + freeFly?.BaseSpeed;
                case "hide": if (!viewerUiHidden) ViewerToggleHide(); return "hidden";
                case "show": if (viewerUiHidden) ViewerToggleHide(); return "shown";
                case "help": viewerHelp = arg != "off"; return "help " + viewerHelp;
                // Shows a message as the viewer would (test sessions run muted, so a real clip says "without sound").
                case "toast": ui.Toast(arg, 8f); return "toast";
                case "in": clipIn = N(); return "in " + clipIn;
                case "out": clipOut = N(); return "out " + clipOut;
                case "clip": ViewerRecordClip(); return recorder != null ? "recording " + recorder.OutPath : "not recording";
                case "moment":
                    {
                        var moments = viewerDemo?.Moments.ToList() ?? new List<ViewerEvent>();
                        int index = (int)N();
                        if (index < 0 || index >= moments.Count) return "no moment " + index;
                        ViewerJumpToMoment(moments[index]);
                        var m = moments[index];
                        return "moment " + m.Summary + " at " + m.T.ToString("0.000", CultureInfo.InvariantCulture)
                            + (m.HitAgo != null ? " (hit; kill feed " + (m.T + m.HitAgo.Value).ToString("0.000", CultureInfo.InvariantCulture) + ")" : "");
                    }
                case "timeline":
                    {
                        var point = ui.TimelineScreenPoint(N());
                        ui.TimelineClicked(point);
                        return "clicked the timeline at " + point;
                    }
                case "exit": ViewerExit(); return "exit";
                case "quit": quitRequested = true; return "quit";
                default: return CamerasCommand(cmd, arg) ?? "unknown command";
            }
        }

        private void WriteStatus()
        {
            try
            {
                var s = BuildState();
                var o = new JObject
                {
                    ["wall"] = Time.realtimeSinceStartupAsDouble, ["viewer"] = viewer, ["menu_open"] = ui.MenuOpen,
                    ["menu_button"] = GameObject.Find("NuclearReplay Replays") != null, ["demos"] = catalog.Demos.Count,
                    ["main_menu"] = MainMenu.State.ToString(), ["server"] = server != null, ["live"] = viewerLive,
                    ["time"] = Math.Round(s.Time, 2), ["duration"] = Math.Round(s.Duration, 2), ["seeking"] = startSeeking, ["restarting"] = viewerRestarting,
                    ["paused"] = server?.Paused, ["speed"] = server?.Speed, ["viewer_speed"] = viewerSpeed, ["preset"] = viewerPreset,
                    ["free_cam"] = freeCamOn, ["camera_state"] = CameraStateManager.i?.currentState?.GetType().Name,
                    ["following"] = s.Following, ["ui_hidden"] = viewerUiHidden, ["help"] = viewerHelp,
                    ["clip_in"] = clipIn, ["clip_out"] = clipOut, ["fps"] = Math.Round(fpsNow, 1), ["screen"] = Screen.width + "x" + Screen.height,
                    ["encoder"] = ClipRecorder.ProbedCodec, ["studio"] = StudioStatus(), ["cameras"] = CamerasStatus(),
                    ["ffmpeg"] = new JObject
                    {
                        ["path"] = ViewerFfmpeg(), ["download"] = ffmpegDownload.State.ToString(), ["fraction"] = Math.Round(ffmpegDownload.Fraction, 3),
                        ["error"] = ffmpegDownload.Error
                    },
                    ["clip"] = recorder == null ? null : new JObject
                    {
                        ["state"] = recorder.State, ["frames"] = recorder.Produced, ["of"] = recorder.TotalFrames, ["out"] = recorder.OutPath,
                        ["fps_before"] = Math.Round(recorder.FpsBefore, 1), ["fps_during"] = Math.Round(recorder.FpsDuring, 1)
                    }
                };
                string path = commandsPath + ".status";
                File.WriteAllText(path + ".tmp", o.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception) { }
        }
    }
}
