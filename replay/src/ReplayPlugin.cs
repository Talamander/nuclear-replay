using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Mirage;
using Mirage.SocketLayer;
using NuclearReplay.Demo;
using NuclearReplay.Shared;
using NuclearOption.Networking;
using NuclearOption.NetworkTransforms;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Plays a .ncdemo network demo inside the real game. The game runs as an ordinary multiplayer
    // client of a stand-in host that sends the recorded server messages, so aircraft, missiles,
    // damage, detonations and effects are rendered by the game's own code and assets.
    //
    // Start: launch the game with  -ncReplay <path.ncdemo>  [-ncShots <shots.json>]
    //        [-ncReplayFrames <dir> [fps]] [-ncCaptureRange <from> <to>] [-ncCaptureRanges a:b,c:d,...] [-ncReplayShots <dir> <seconds>]
    //        [-ncNoHud] [-ncReplayQuit] [-ncClockLog <file>]
    //        Speed options (all opt-in; without them the export behaves exactly as before):
    //        [-ncFrameFormat png|raw]      raw: one RGBA stream per range (range_NN/frames.rgba, top row first)
    //                                      via end-of-frame GPU readback, instead of one PNG per frame
    //        [-ncFastForward <speed> [stepFps]]  skip-ahead speed (default 4). With stepFps the skip runs on a
    //                                      fixed replay step (speed/stepFps s per frame) as fast as frames render
    //        [-ncUncapped]                 vsync off and no frame-rate cap while exporting
    //        [-ncPerfLog]                  log frames/s and replay rate every 5 s of wall time
    //        [-ncPreroll <s>]              replay seconds played at 1x before each range (default 1)
    //        [-ncStart <s>]                interactive replay: skip with the fixed-step fast-forward to <s> minus
    //                                      the preroll, then play at 1x with the normal keys (ignored for frame export)
    //        Not rendering while skipping was tried and rejected: the game state then differs at the range
    //        (other missiles, other camera target), so the cameras keep drawing.
    //        Ranges may carry their own capture rate: -ncCaptureRanges 10:20,30:40@60
    //        Every finished range writes <frames>/range_NN.done (JSON: fps, frames, format, size).
    //        Engine mattes (raw format only): when any shot has "mattes": ["aircraft"], each range also writes
    //        range_NN/matte.gray (one 8-bit alpha plane per frame, same frame count; black outside matte shots).
    //        Render jobs (docs/PRODUCT-CONTRACT.md §2): -ncRenderJob <job.json> renders every range of the job in
    //        one run, piping raw frames into an ffmpeg process started here, writes progress.json and quits.
    //        Live preview and thumbnails (docs/PRODUCT-CONTRACT.md section 4): -ncPreview <job.json> streams small
    //        JPEG frames to nr over 127.0.0.1 and takes camera and range commands back (PreviewHost.cs).
    // or set [Replay] AutoStartPath in the config.
    // The recorder loads first, as it did when the two plugins were separate DLLs (its GUID sorted first then).
    [BepInPlugin(Id, "Nuclear Replay Player", BuildVersion.Core)]
    [BepInDependency(PluginIds.Recorder, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed partial class ReplayPlugin : BaseUnityPlugin
    {
        public const string Id = PluginIds.Player;
        public const string Version = BuildVersion.Full;
        internal static ReplayPlugin Instance;

        private ConfigEntry<string> autoStart;
        private ConfigEntry<bool> overlay, hideHud;
        private ConfigEntry<KeyCode> pauseKey, fasterKey, slowerKey, targetKey, cameraKey, overlayKey, hudKey;
        private Harmony harmony;
        private string pendingPath, shotListPath, frameDirectory, screenshotDirectory, clockLogPath;
        private StreamWriter clockLog;
        private double nextClockLog;
        private int frameRate = 60;
        private double captureFrom, captureTo = double.MaxValue;
        // -ncCaptureRanges: several sorted ranges exported in one run, each into <frames>/range_NN.
        // Entry: {from, to, fps (0 = the -ncReplayFrames rate)}.
        private readonly List<double[]> ranges = new List<double[]>();
        private int baseFrameRate = 60;
        private string frameFormat = "png";
        private float fastForwardSpeed = 4f;
        private int fastForwardStepFps;
        private bool uncapped, perfLog;
        private double preroll = 1;
        // -ncStart: interactive start time. startSeeking is true until the skip has reached startAt - preroll.
        private double startAt = -1;
        private bool startSeeking;
        private int savedVSync = -1, savedTargetFrameRate;
        // -ncAudioRanges: real-time playback of sorted ranges for game-audio capture. Skips (muted, fixed steps)
        // to each range's start minus preroll, plays it at 1x, then skips to the next; quits after the last.
        private readonly List<double[]> audioRanges = new List<double[]>();
        private int audioIndex;
        private bool audioLive;
        private double audioTail = 0.5;

        private double nextPerfLog, perfReplayStart;
        private int perfFrameStart;
        private RawRangeWriter rawWriter, matteWriter;
        private EngineMatte matte;
        private RenderTexture matteResolve;
        private int matteRendered;   // frames of the current range with a real matte render
        private RenderTexture grabTexture;
        private bool grabPending;
        private int rangeIndex;
        private string frameRoot;
        private bool capturing, fastForwarding;
        private double nextHudScan;
        private bool hudHidden;
        private readonly HashSet<Canvas> hiddenCanvases = new HashSet<Canvas>();
        private CockpitView cockpitView;
        private double screenshotEvery;
        private bool quitAtEnd;
        private LoopbackSocketFactory factory;
        private ReplayServer server;
        private Director director;
        private string status = "idle";
        private double lastDelta;
        private int frameNumber;
        private double nextScreenshot;
        private double finishedAt = -1;
        private GUIStyle style;
        // -ncRenderJob state.
        private RenderJob job;
        private string jobPath;
        private bool jobRangesDone, quitRequested, fastForwardGiven;
        private int jobFrameErrors;
        // -ncPreview state.
        private PreviewHost preview;
        private string previewPath;
        private double restartAfter = -1;

        internal bool Redirecting => factory != null && server == null;
        internal bool Running => server != null;

        // BepInEx opened Config under the new GUID. Before the first Bind, take over the settings saved under the newest
        // old GUID (once; the old files stay) and reload, so an update keeps the player's keys and viewer settings.
        private void MigrateConfig()
        {
            try
            {
                var from = PluginIds.CopyLegacyConfig(Path.GetDirectoryName(Config.ConfigFilePath), PluginIds.LegacyPlayers, Id);
                if (from == null) return;
                Config.Reload();
                Logger.LogInfo("Copied settings from " + from + " to " + Path.GetFileName(Config.ConfigFilePath) + ".");
            }
            catch (Exception e) { Logger.LogWarning("Could not copy settings from an older config file: " + e.Message); }
        }

        private void Awake()
        {
            Instance = this;
            cockpitView = new CockpitView(Logger);
            MigrateConfig();
            // An old install (Nuclear Context) loaded next to this one: the recorder stays off; the player keeps working
            // but says so in the log, in a toast and in the Replays menu.
            string legacy = NuclearReplay.Plugin.LegacyPluginLoaded();
            if (legacy != null) Logger.LogError(PluginIds.LegacyWarning(legacy) + " Nuclear Replay does not record until then.");
            autoStart = Config.Bind("Replay", "AutoStartPath", "", "Demo to replay automatically once the main menu loads. Empty disables. Command-line -ncReplay overrides.");
            overlay = Config.Bind("Replay", "ShowOverlay", true, "Show replay status text in the top-left corner.");
            pauseKey = Config.Bind("Replay", "PauseKey", KeyCode.Pause, "Pause or resume replay. Only active during replay.");
            fasterKey = Config.Bind("Replay", "FasterKey", KeyCode.PageUp, "Double replay speed (max 8x). Only active during replay.");
            slowerKey = Config.Bind("Replay", "SlowerKey", KeyCode.PageDown, "Halve replay speed (min 1/8x). Only active during replay.");
            targetKey = Config.Bind("Replay", "NextTargetKey", KeyCode.Home, "Follow the next aircraft. Only active during replay.");
            cameraKey = Config.Bind("Replay", "NextCameraKey", KeyCode.End, "Cycle orbit, chase, cockpit, TV and free cameras. Only active during replay.");
            overlayKey = Config.Bind("Replay", "OverlayKey", KeyCode.Insert, "Toggle the replay status overlay. Only active during replay.");
            hideHud = Config.Bind("Replay", "HideGameHud", false, "Hide the game's own HUD and kill feed during replay for clean footage. Command-line -ncNoHud forces it on.");
            hudKey = Config.Bind("Replay", "HudKey", KeyCode.Delete, "Toggle the game's HUD during replay. Only active during replay.");
            hudHidden = hideHud.Value;
            ParseCommandLine();
            if (jobPath != null) LoadJob();
            else if (previewPath != null) LoadPreview();
            if (jobPath == null && pendingPath == null && !string.IsNullOrWhiteSpace(autoStart.Value)) pendingPath = HostPath(autoStart.Value);
            // Nuclear Option's first scene load destroys the BepInEx manager object (and this plugin with it)
            // unless the object is hidden, the same as BepInEx's HideManagerGameObject option. The Replays menu
            // lives in every session now, so the plugin always stays alive. The patches only act while a replay runs.
            if ((gameObject.hideFlags & HideFlags.DontSave) == 0)
            {
                gameObject.hideFlags |= HideFlags.HideAndDontSave;
                Logger.LogInfo("Kept the plugin object alive across scene loads.");
            }
            harmony = new Harmony(Id);
            harmony.PatchAll(typeof(ReplayPatches));
            ViewerAwake();
            if (legacy != null) ui.LegacyWarning = "Nuclear Context (old version) is installed too. Delete BepInEx/plugins/NuclearContext and restart. Recording is off.";
            if (pendingPath == null) { Logger.LogInfo("Replay idle (no -ncReplay argument); Replays menu ready."); return; }
            if (frameFormat == "raw") StartCoroutine(EndOfFrameGrab());
            if (preview != null) StartCoroutine(preview.GrabLoop());
            Logger.LogInfo("Replay requested: " + pendingPath);
        }

        private void ParseCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string Next(int offset = 1) => i + offset < args.Length ? args[i + offset] : null;
                switch (args[i])
                {
                    case "-ncReplay": pendingPath = HostPath(Next()); break;
                    case "-ncShots": shotListPath = HostPath(Next()); break;
                    case "-ncReplayQuit": quitAtEnd = true; break;
                    case "-ncNoHud": hudHidden = true; break;
                    case "-ncClockLog": clockLogPath = HostPath(Next()); break;
                    case "-ncCaptureRange":
                        double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out captureFrom);
                        if (!double.TryParse(Next(2), NumberStyles.Float, CultureInfo.InvariantCulture, out captureTo)) captureTo = double.MaxValue;
                        break;
                    case "-ncCaptureRanges":
                        foreach (var item in (Next() ?? "").Split(','))
                        {
                            var at = item.Split('@');
                            var ends = at[0].Split(':');
                            int rangeFps = 0;
                            if (at.Length == 2) int.TryParse(at[1], out rangeFps);
                            if (ends.Length == 2
                                && double.TryParse(ends[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                                && double.TryParse(ends[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                                ranges.Add(new double[] { a, b, Math.Max(0, rangeFps) });
                        }
                        ranges.Sort((x, y) => x[0].CompareTo(y[0]));
                        break;
                    case "-ncReplayFrames":
                        frameDirectory = HostPath(Next());
                        if (int.TryParse(Next(2), out var fps)) frameRate = Math.Max(1, fps);
                        baseFrameRate = frameRate;
                        break;
                    case "-ncFrameFormat": frameFormat = (Next() ?? "png").ToLowerInvariant() == "raw" ? "raw" : "png"; break;
                    case "-ncRenderJob": jobPath = HostPath(Next()); break;
                    case "-ncPreview": previewPath = HostPath(Next()); break;
                    case "-ncFastForward":
                        fastForwardGiven = true;
                        if (float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ff)) fastForwardSpeed = Mathf.Clamp(ff, 1f, 64f);
                        if (int.TryParse(Next(2), out var step)) fastForwardStepFps = Math.Max(0, step);
                        break;
                    case "-ncUncapped": uncapped = true; break;
                    case "-ncPreroll":
                        double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out preroll);
                        preroll = Math.Max(0.1, preroll);
                        break;
                    case "-ncPerfLog": perfLog = true; break;
                    case "-ncAudioRanges":
                        foreach (var item in (Next() ?? "").Split(','))
                        {
                            var ends = item.Split('@')[0].Split(':');
                            if (ends.Length == 2
                                && double.TryParse(ends[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                                && double.TryParse(ends[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > a)
                                audioRanges.Add(new[] { a, b });
                        }
                        audioRanges.Sort((x, y) => x[0].CompareTo(y[0]));
                        break;
                    case "-ncAudioTail":
                        double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out audioTail);
                        break;
                    case "-ncStart":
                        if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var start) && start > 0)
                            startAt = start;
                        break;
                    case "-ncReplayShots":
                        screenshotDirectory = HostPath(Next());
                        double.TryParse(Next(2), NumberStyles.Float, CultureInfo.InvariantCulture, out screenshotEvery);
                        if (screenshotEvery <= 0) screenshotEvery = 10;
                        break;
                }
            }
            if (ranges.Count > 0 && frameDirectory != null)
            {
                frameRoot = frameDirectory;
                SelectRange(0);
            }
            if (audioRanges.Count > 0 && (frameDirectory != null || jobPath != null || previewPath != null || startAt > 0))
            {
                Logger.LogWarning("-ncAudioRanges is ignored with frame export, a render job, a preview or -ncStart.");
                audioRanges.Clear();
            }
            if (audioRanges.Count > 0)
            {
                if (!fastForwardGiven) { fastForwardSpeed = 8f; fastForwardStepFps = 60; }
                Logger.LogInfo($"Audio ranges: {audioRanges.Count} (skip x{fastForwardSpeed} step {fastForwardStepFps}, preroll {preroll.ToString(CultureInfo.InvariantCulture)} s, tail {audioTail.ToString(CultureInfo.InvariantCulture)} s)");
            }
            if (startAt > 0 && (frameDirectory != null || jobPath != null || previewPath != null))
            {
                Logger.LogWarning("-ncStart is for interactive replays and is ignored with frame export, a render job or a preview.");
                startAt = -1;
            }
            if (startAt > 0)
            {
                startSeeking = true;
                // Same skip as render ranges: fixed 8/60 s replay steps unless -ncFastForward says otherwise.
                if (!fastForwardGiven) { fastForwardSpeed = 8f; fastForwardStepFps = 60; }
                Logger.LogInfo($"Replay start at {startAt.ToString("F2", CultureInfo.InvariantCulture)} s "
                    + $"(skip x{fastForwardSpeed} step {fastForwardStepFps}, preroll {preroll.ToString(CultureInfo.InvariantCulture)} s)");
            }
        }

        // -ncStart: skips to startAt - preroll exactly like the skip before a render range (fixed replay
        // steps, frame cap lifted), then hands over at 1x. Runs once; after that the keys work as usual.
        private void StartSeek(double replayNow)
        {
            if (!startSeeking) return;
            bool reached = replayNow >= startAt - preroll || server.Finished;
            if (!reached)
            {
                if (!fastForwarding)
                {
                    fastForwarding = true;
                    savedVSync = QualitySettings.vSyncCount;
                    savedTargetFrameRate = Application.targetFrameRate;
                }
                server.Paused = false;
                server.Speed = fastForwardSpeed;
                if (fastForwardStepFps > 0) Time.captureFramerate = fastForwardStepFps;
                if (Time.frameCount % 30 == 0)
                {
                    if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
                    if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
                }
                return;
            }
            startSeeking = false;
            fastForwarding = false;
            server.Speed = 1f;
            Time.captureFramerate = 0;
            if (savedVSync >= 0)
            {
                QualitySettings.vSyncCount = savedVSync;
                Application.targetFrameRate = savedTargetFrameRate;
            }
            if (viewer) ViewerSeekReached(replayNow);
            Logger.LogInfo($"Replay start reached at {replayNow.ToString("F2", CultureInfo.InvariantCulture)} s "
                + $"(requested {startAt.ToString("F2", CultureInfo.InvariantCulture)} s), playing at 1x");
        }

        // -ncAudioRanges: muted fixed-step skip before each range, 1x (unmuted) from start - preroll to end + tail.
        private void AudioRangeStep(double replayNow)
        {
            bool live = server.ScenesLoaded > 0 && !server.WaitingForScene && !NetworkManagerNuclearOption.IsLoadingScene;
            if (audioIndex >= audioRanges.Count || server.Finished)
            {
                if (!quitRequested) { Logger.LogInfo($"Audio ranges done at {replayNow.ToString("F2", CultureInfo.InvariantCulture)} s"); quitRequested = true; Application.Quit(); }
                return;
            }
            var r = audioRanges[audioIndex];
            if (replayNow > r[1] + audioTail)
            {
                Logger.LogInfo($"Audio range {audioIndex} ({r[0].ToString(CultureInfo.InvariantCulture)}-{r[1].ToString(CultureInfo.InvariantCulture)}) done at {replayNow.ToString("F2", CultureInfo.InvariantCulture)} s");
                clockLog?.WriteLine("# end " + audioIndex);
                audioIndex++;
                audioLive = false;
                return;
            }
            bool ahead = !live || replayNow < r[0] - preroll;
            if (ahead)
            {
                if (!fastForwarding)
                {
                    fastForwarding = true;
                    savedVSync = QualitySettings.vSyncCount;
                    savedTargetFrameRate = Application.targetFrameRate;
                }
                server.Paused = false;
                server.Speed = fastForwardSpeed;
                if (fastForwardStepFps > 0) Time.captureFramerate = fastForwardStepFps;
                AudioListener.volume = 0f;
                if (Time.frameCount % 30 == 0)
                {
                    if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
                    if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
                }
                return;
            }
            if (!audioLive)
            {
                audioLive = true;
                fastForwarding = false;
                server.Speed = 1f;
                Time.captureFramerate = 0;
                AudioListener.volume = 1f;
                if (savedVSync >= 0)
                {
                    QualitySettings.vSyncCount = savedVSync;
                    Application.targetFrameRate = savedTargetFrameRate;
                }
                clockLog?.WriteLine("# start " + audioIndex);
                Logger.LogInfo($"Audio range {audioIndex} live at {replayNow.ToString("F2", CultureInfo.InvariantCulture)} s, playing at 1x");
            }
        }

        private void SelectRange(int index)
        {
            rangeIndex = index;
            captureFrom = ranges[index][0];
            captureTo = ranges[index][1];
            frameRate = ranges[index].Length > 2 && ranges[index][2] > 0 ? (int)ranges[index][2] : baseFrameRate;
            // A render job writes no frame files; frameDirectory only marks the export as active.
            frameDirectory = job != null ? Path.GetDirectoryName(Path.GetFullPath(job.Ranges[index].Out)) : Path.Combine(frameRoot, $"range_{index:D2}");
            frameNumber = 0;
        }

        // -ncRenderJob: the job replaces the frame-export options (raw frames, fixed-step skip, no HUD, own quit).
        private void LoadJob()
        {
            try
            {
                job = RenderJob.Load(jobPath, Logger, HostPath);
            }
            catch (Exception e)
            {
                Logger.LogError("Render job " + jobPath + " rejected: " + e.Message);
                pendingPath = null;
                quitRequested = true;
                return;
            }
            pendingPath = job.Demo;
            shotListPath = job.Shots;
            hudHidden = !job.Hud;
            frameFormat = "raw";
            uncapped = true;
            quitAtEnd = false;
            if (!fastForwardGiven) { fastForwardSpeed = 8f; fastForwardStepFps = 60; }
            ranges.Clear();
            foreach (var r in job.Ranges) ranges.Add(new double[] { r.From, r.To, r.Fps });
            frameRoot = null;
            SelectRange(0);
            Logger.LogInfo($"Render job {jobPath}: {job.Ranges.Count} ranges ({job.Describe()}), {job.Width}x{job.Height}, encoder {job.EncoderSetting}, progress {job.ProgressPath}");
            job.Progress("loading", job.Ranges[0].Index, 0, job.ExpectedFrames(0));
        }

        // -ncPreview: the preview job replaces the interactive options (no overlay, no keys, muted, small window).
        private void LoadPreview()
        {
            try
            {
                preview = PreviewHost.Load(previewPath, Logger, HostPath);
            }
            catch (Exception e)
            {
                Logger.LogError("Preview job " + previewPath + " rejected: " + e.Message);
                preview = null;
                pendingPath = null;
                quitRequested = true;
                return;
            }
            pendingPath = preview.Demo;
            shotListPath = preview.Shots;
            hudHidden = !preview.Hud;
            quitAtEnd = false;
            Logger.LogInfo("Preview job " + previewPath + ": " + preview.Describe());
            preview.Start();
        }

        // Preview and viewer "go back": the replay only runs forward, so leave the match and load it again from
        // the menu.
        private void SoftRestart()
        {
            Logger.LogInfo("Restarting the replay");
            preview?.OnRestarting();
            try { director?.Shutdown(); } catch (Exception e) { Logger.LogWarning("Preview restart: director: " + e.Message); }
            try { server?.Close(); } catch (Exception e) { Logger.LogWarning("Preview restart: server: " + e.Message); }
            server = null;
            director = null;
            cockpitView.Leave();
            finishedAt = -1;
            capturing = false;
            hiddenCanvases.Clear();
            Time.timeScale = 1f;
            Time.captureFramerate = 0;
            try { NetworkManagerNuclearOption.i?.Stop(true); }
            catch (Exception e) { Logger.LogWarning("Preview restart: stop: " + e.Message); }
            pendingPath = preview != null ? preview.Demo : viewerDemo?.Path;
            if (fastForwarding && savedVSync >= 0) { QualitySettings.vSyncCount = savedVSync; Application.targetFrameRate = savedTargetFrameRate; }
            fastForwarding = false;
            // Give the game time to leave the mission and start loading the menu before TryStart looks again.
            restartAfter = Time.realtimeSinceStartupAsDouble + 5;
        }

        private void JobUpdate(double replayTime, bool live)
        {
            if (job == null || job.Finished) return;
            if (!jobRangesDone)
            {
                int index = job.Ranges[rangeIndex].Index, expected = job.ExpectedFrames(rangeIndex);
                if (!live) job.Progress("loading", index, 0, expected);
                else if (capturing) job.Progress("rendering", index, Math.Min(frameNumber, expected), expected);
                else job.Progress("seeking", index, 0, expected);
                if (server != null && finishedAt >= 0 && Time.unscaledTimeAsDouble - finishedAt > 2)
                {
                    FinishRange();
                    job.Fail($"the demo ends at {(server.EndTime - server.StartTime).ToString("F1", CultureInfo.InvariantCulture)} s, "
                        + $"before range {index} ({captureFrom.ToString(CultureInfo.InvariantCulture)}-{captureTo.ToString(CultureInfo.InvariantCulture)}) was complete");
                    quitRequested = true;
                }
                return;
            }
            int last = job.Ranges.Count - 1;
            job.Progress("encoding", job.Ranges[last].Index, job.ExpectedFrames(last), job.ExpectedFrames(last));
            if (job.EncodersDone(out var error))
            {
                if (error != null) job.Fail(error); else job.Done();
                Logger.LogInfo("Render job finished: " + job.State);
                quitRequested = true;
            }
        }

        // Under Proton the game sees Linux paths through drive Z:.
        private static string HostPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (path.StartsWith("/") && Path.DirectorySeparatorChar == '\\') return "Z:" + path.Replace('/', '\\');
            return path;
        }

        private void Update()
        {
            if (preview != null)
            {
                preview.Tick(server != null);
                if (preview.QuitRequested) quitRequested = true;
                if (preview.RestartRequested && server != null && !quitRequested) { preview.RestartRequested = false; SoftRestart(); }
            }
            if (quitRequested) { Application.Quit(); return; }
            try { ViewerTick(); } catch (Exception e) { Logger.LogError("Viewer: " + e); }
            if (job != null && server == null && !job.Finished)
            {
                job.Tick();
                if (Time.realtimeSinceStartup > 900) { job.Fail("the game did not reach the replay within 900 s"); quitRequested = true; return; }
            }
            if (pendingPath != null && server == null) { TryStart(); return; }
            if (server == null) return;
            if (preview != null) return;   // no keyboard control while nr drives the preview
            if (ViewerKeys()) return;
            // While -ncStart skips ahead, pause and speed belong to the skip.
            if (!startSeeking)
            {
                if (Input.GetKeyDown(pauseKey.Value)) server.Paused = !server.Paused;
                if (Input.GetKeyDown(fasterKey.Value)) server.Speed = Mathf.Min(8f, server.Speed * 2f);
                if (Input.GetKeyDown(slowerKey.Value)) server.Speed = Mathf.Max(0.125f, server.Speed / 2f);
            }
            if (Input.GetKeyDown(targetKey.Value)) director.CycleTarget();
            if (Input.GetKeyDown(cameraKey.Value)) director.CycleCamera();
            if (Input.GetKeyDown(overlayKey.Value)) overlay.Value = !overlay.Value;
            if (Input.GetKeyDown(hudKey.Value)) { hudHidden = !hudHidden; nextHudScan = 0; }
        }

        // Runs after game Update and before SendTransformBatcher.LateUpdate flushes the client.
        private void LateUpdate()
        {
            if (server == null) return;
            CineCamera.CaptureFrame = -1;
            try
            {
                server.Receive();
                double replayNow = server.Clock - server.StartTime;
                if (startSeeking) StartSeek(replayNow);
                if (audioRanges.Count > 0) AudioRangeStep(replayNow);
                if (preview != null)
                {
                    if (preview.ShotsChanged && director != null) { preview.ShotsChanged = false; director.ReplaceShots(preview.LatestShots, replayNow); }
                    preview.Drive(server, replayNow);
                }
                // Frame export skips ahead at 4x until shortly before the requested range.
                if (frameDirectory != null && captureFrom > 1 && !capturing)
                {
                    bool ahead = replayNow < captureFrom - preroll;
                    if (ahead != fastForwarding) { fastForwarding = ahead; server.Speed = ahead ? fastForwardSpeed : 1f; }
                    // Fixed-step skip: replay time no longer depends on wall time, so frames run as fast as they render.
                    // In the preroll the step is already the export rate, so the first exported frame is a full 1/fps
                    // step like every other one (it used to be one fast-forward step, 1/60 s).
                    if (fastForwardStepFps > 0) Time.captureFramerate = ahead ? fastForwardStepFps : frameRate;
                }
                if (frameDirectory != null && uncapped && Time.frameCount % 30 == 0)
                {
                    if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
                    if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
                }
                double before = server.Clock;
                double delta = Time.captureDeltaTime > 0 ? Time.captureDeltaTime : Time.unscaledDeltaTime;
                server.Advance(delta);
                lastDelta = server.Clock - before;
                server.Flush();
                bool live = server.ScenesLoaded > 0 && !server.WaitingForScene && !NetworkManagerNuclearOption.IsLoadingScene;
                Time.timeScale = server.Paused ? 0f : server.Speed;
                double replayTime = server.Clock - server.StartTime;
                if (!freeCamOn) director.Update(replayTime, live);
                cockpitView.Step(live);
                if (live) Capture(replayTime);
                preview?.AfterFrame(replayTime, live);
                JobUpdate(replayTime, live);
                if (viewer) ViewerLateUpdate(replayTime, live);
                if (matte != null)
                {
                    matte.Want = live && capturing && Array.IndexOf(director.ActiveMattes, "aircraft") >= 0;
                    matte.SetTarget(director.MatteUnit);
                }
                if (live) ApplyHud();
                if (live) LogClock(replayTime);
                if (perfLog) LogPerf(replayTime);
                status = $"REPLAY {replayTime:F1}/{server.EndTime - server.StartTime:F1}s  x{server.Speed:0.###}{(server.Paused ? " PAUSED" : "")}"
                    + $"{(server.WaitingForScene ? " loading" : "")}{(startSeeking ? $"  skipping to {startAt - preroll:F0}s" : "")}  rec {server.Index}/{server.Count}  {director.Status}";
                if (server.Finished && finishedAt < 0)
                {
                    finishedAt = Time.unscaledTimeAsDouble;
                    Logger.LogInfo($"Replay finished: {server.MessagesSent} messages, {server.BytesSent} bytes, {server.PromotedToReliable} promoted to reliable");
                }
                if (finishedAt >= 0) status += "  END";
                if (quitAtEnd && finishedAt >= 0 && Time.unscaledTimeAsDouble - finishedAt > 3) Application.Quit();
                if (quitAtEnd && frameDirectory != null && replayTime > captureTo + 1) { FinishRange(); Application.Quit(); }
            }
            catch (Exception e)
            {
                Logger.LogError("Replay frame failed: " + e);
                status = "replay error: " + e.Message;
                if (job != null && ++jobFrameErrors > 100) { job.Fail("replay frames keep failing: " + e.Message); quitRequested = true; }
                if (preview != null && ++jobFrameErrors > 100) { preview.Fail("replay frames keep failing: " + e.Message); quitRequested = true; }
            }
        }

        // Writes "replayTime utc" pairs so an audio capture of a real-time replay can be aligned to replay time.
        private void LogClock(double replayTime)
        {
            if (clockLogPath == null || Time.unscaledTimeAsDouble < nextClockLog) return;
            nextClockLog = Time.unscaledTimeAsDouble + 0.25;
            if (clockLog == null) clockLog = new StreamWriter(clockLogPath, false) { AutoFlush = true };
            clockLog.WriteLine(replayTime.ToString("F4", CultureInfo.InvariantCulture) + " " + DateTime.UtcNow.ToString("o"));
        }

        private void TryStart()
        {
            if (MainMenu.State != MainMenu.LoadingState.Loaded) return;
            var manager = NetworkManagerNuclearOption.i;
            if (manager == null || manager.Server.Active || manager.Client.Active) return;
            if (restartAfter >= 0 && (Time.realtimeSinceStartupAsDouble < restartAfter || NetworkManagerNuclearOption.IsLoadingScene)) return;
            restartAfter = -1;
            var path = pendingPath;
            pendingPath = null;
            try
            {
                var demo = DemoReader.Load(path);
                Logger.LogInfo($"Replay loaded {demo.Records.Count} records{(demo.Truncated ? " (truncated tail ignored)" : "")}; header {demo.HeaderJson}");
                // A preview restart reuses the factory component; CreatePair makes fresh sockets.
                factory = manager.gameObject.GetComponent<LoopbackSocketFactory>() ?? manager.gameObject.AddComponent<LoopbackSocketFactory>();
                factory.CreatePair();
                director = new Director(Logger, shotListPath ?? DefaultShotList(path));
                LearnPlayers(demo);
                // Cine diagnostics (DEMO.md) are for script frame exports; nr render jobs leave no traces behind.
                if (job == null && (frameDirectory != null || frameRoot != null)) CineCamera.TraceDir = Path.Combine(ModFolders.DataDir(BepInEx.Paths.GameRootPath), "traces");
                // Render jobs write only the clip; engine mattes stay a render-clips.sh feature.
                if (director.AnyMattes && frameFormat == "raw" && frameDirectory != null && job == null) { matte?.Dispose(); matte = new EngineMatte(Logger); }
                if (job != null && (Screen.width != job.Width || Screen.height != job.Height))
                {
                    Logger.LogInfo($"Render job: screen {Screen.width}x{Screen.height}, requesting {job.Width}x{job.Height}");
                    Screen.SetResolution(job.Width, job.Height, FullScreenMode.Windowed);
                }
                if (preview != null)
                {
                    // Render at the preview size (thumbnails at twice their size, then scaled down).
                    int pw = preview.IsPreview ? preview.Width : preview.Width * 2, ph = preview.IsPreview ? preview.Height : preview.Height * 2;
                    if (Screen.width != pw || Screen.height != ph) Screen.SetResolution(pw, ph, FullScreenMode.Windowed);
                    if (preview.LatestShots != null) director.ReplaceShots(preview.LatestShots, 0);
                    preview.ShotsChanged = false;
                    preview.OnServerStarted();
                }
                manager.StartClient(new ConnectOptions(NuclearOption.Networking.SocketType.UDP, "nuclear-replay-replay", 7777));
                var clientConfig = manager.Client.PeerConfig;
                var config = new Config
                {
                    MaxConnections = 1, TimeoutDuration = 600f, KeepAliveInterval = 2f,
                    ConnectAttemptInterval = 0.25f, MaxConnectAttempts = 40, MaxReliableFragments = 100,
                    MaxReliablePacketsInSendBufferPerConnection = 3000, key = clientConfig?.key
                };
                server = new ReplayServer(Logger, demo.Records, factory.ServerSocket, config);
                server.Annotation += text => Logger.LogInfo("Demo annotation: " + text);
                if (viewer) ViewerStarted();
                if (frameDirectory != null) Directory.CreateDirectory(frameDirectory);
                if (screenshotDirectory != null) Directory.CreateDirectory(screenshotDirectory);
            }
            catch (Exception e)
            {
                Logger.LogError("Replay start failed: " + e);
                status = "replay start failed: " + e.Message;
                if (viewer) { ui?.Toast("Could not open the replay: " + e.Message, 8f); StopViewerReplay(reopenMenu: true); }
                if (job != null) { job.Fail("replay start failed: " + e.Message); quitRequested = true; }
                if (preview != null) { preview.Fail("replay start failed: " + e.Message); quitRequested = true; }
            }
        }

        private static string DefaultShotList(string demoPath)
        {
            var candidate = Path.ChangeExtension(demoPath, ".shots.json");
            return File.Exists(candidate) ? candidate : null;
        }

        private void Capture(double replayTime)
        {
            if (frameDirectory != null)
            {
                bool inRange = !server.Finished && replayTime >= captureFrom && replayTime <= captureTo;
                if (inRange != capturing)
                {
                    // A fixed capture frame rate makes every exported frame exactly 1/fps of replay time.
                    capturing = inRange;
                    Time.captureFramerate = inRange ? frameRate : 0;
                    Logger.LogInfo((inRange ? "Frame export started at " : "Frame export stopped at ") + replayTime.ToString("F2") + "s, " + frameNumber + " frames"
                        + $" ({frameRate} fps, {frameFormat}, wall {DateTime.UtcNow:HH:mm:ss.fff})");
                    if (inRange && frameFormat == "raw")
                        rawWriter = job != null ? job.CreateWriter(rangeIndex, Logger) : new RawRangeWriter(Path.Combine(frameDirectory, "frames.rgba"), Logger);
                    if (inRange && frameFormat == "raw" && matte != null)
                        { matteWriter = new RawRangeWriter(Path.Combine(frameDirectory, "matte.gray"), Logger); matteRendered = 0; }
                    if (!inRange)
                    {
                        FinishRange();
                        if ((frameRoot != null || job != null) && rangeIndex + 1 < ranges.Count)
                        {
                            SelectRange(rangeIndex + 1);
                            if (job == null) Directory.CreateDirectory(frameDirectory);
                        }
                        else if (job != null) jobRangesDone = true;
                    }
                }
                if (capturing && frameFormat == "raw") { grabPending = true; frameNumber++; }
                else if (capturing) ScreenCapture.CaptureScreenshot(Path.Combine(frameDirectory, $"frame_{frameNumber++:D6}.png"));
                if (capturing) { CineCamera.CaptureFrame = frameNumber - 1; CineCamera.ReplayTime = replayTime; }
            }
            if (screenshotDirectory != null && replayTime >= nextScreenshot)
            {
                nextScreenshot = replayTime + screenshotEvery;
                ScreenCapture.CaptureScreenshot(Path.Combine(screenshotDirectory, $"replay_{replayTime:000000.0}s.png"));
            }
        }

        // Raw export: copy the finished frame into a render texture at end of frame and read it back
        // asynchronously; a writer thread appends the pixels to the range's frames.rgba.
        private IEnumerator EndOfFrameGrab()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                if (!grabPending) continue;
                grabPending = false;
                var writer = rawWriter;
                if (writer == null) continue;
                try
                {
                    if (grabTexture == null || grabTexture.width != Screen.width || grabTexture.height != Screen.height)
                    {
                        if (grabTexture != null) grabTexture.Release();
                        grabTexture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
                        grabTexture.Create();
                    }
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(grabTexture);
                    writer.Width = grabTexture.width; writer.Height = grabTexture.height;
                    writer.Pending++;
                    UnityEngine.Rendering.AsyncGPUReadback.Request(grabTexture, 0, TextureFormat.RGBA32, request =>
                    {
                        writer.Pending--;
                        if (request.hasError) { writer.Errors++; return; }
                        writer.Add(request.GetData<byte>());
                    });
                    if (matteWriter != null) GrabMatte(matteWriter);
                }
                catch (Exception e)
                {
                    Logger.LogError("Raw frame grab failed: " + e);
                }
            }
        }

        // Engine matte: resolve the matte camera's MSAA texture and append its alpha as one 8-bit plane.
        // Frames without a matte render (other shots, no target) get an empty plane so counts stay equal.
        private void GrabMatte(RawRangeWriter writer)
        {
            int w = grabTexture.width, h = grabTexture.height;
            writer.Width = w; writer.Height = h;
            if (matte == null || matte.RenderedFrame != Time.frameCount || matte.Texture == null)
            {
                var empty = new Unity.Collections.NativeArray<byte>(w * h, Unity.Collections.Allocator.Temp);
                writer.Add(empty);
                empty.Dispose();
                return;
            }
            if (matteResolve == null || matteResolve.width != w || matteResolve.height != h)
            {
                if (matteResolve != null) matteResolve.Release();
                matteResolve = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                matteResolve.Create();
            }
            Graphics.Blit(matte.Texture, matteResolve);
            matteRendered++;
            writer.Pending++;
            UnityEngine.Rendering.AsyncGPUReadback.Request(matteResolve, 0, TextureFormat.RGBA32, request =>
            {
                writer.Pending--;
                if (request.hasError) { writer.Errors++; return; }
                var rgba = request.GetData<byte>();
                var alpha = new Unity.Collections.NativeArray<byte>(rgba.Length / 4, Unity.Collections.Allocator.Temp);
                for (int i = 0; i < alpha.Length; i++) alpha[i] = rgba[i * 4 + 3];
                writer.Add(alpha);
                alpha.Dispose();
            });
        }

        // Closes the current range: waits for outstanding readbacks, flushes the writer and leaves a
        // range_NN.done marker so an encoder can start on this range while the next one renders.
        private void FinishRange()
        {
            if (frameRoot == null && frameDirectory == null) return;
            string dir = frameDirectory;
            int frames = frameNumber;
            int w = Screen.width, h = Screen.height;
            int errors = 0;
            if (rawWriter != null)
            {
                UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();
                var writer = rawWriter;
                rawWriter = null;
                writer.Close();
                frames = writer.Frames; errors = writer.Errors;
                if (writer.Width > 0) { w = writer.Width; h = writer.Height; }
                if (job != null)
                {
                    Logger.LogInfo($"Render job: range {job.Ranges[rangeIndex].Index} captured {frames} frames ({errors} errors)");
                    if (job.Finished) return;
                    double demoEnd = server != null ? server.EndTime - server.StartTime : double.MaxValue;
                    if (server != null && server.Finished && captureTo > demoEnd + 0.5 / frameRate)
                    {
                        var inv = CultureInfo.InvariantCulture;
                        job.Fail($"the demo ends at {demoEnd.ToString("F1", inv)} s, before range {job.Ranges[rangeIndex].Index} "
                            + $"({captureFrom.ToString(inv)}-{captureTo.ToString(inv)}) was complete");
                        quitRequested = true;
                        return;
                    }
                    job.RangeFinished(rangeIndex, frames, errors);
                    return;
                }
            }
            if (job != null) return;
            bool hadMatte = false;
            if (matteWriter != null)
            {
                var mw = matteWriter;
                matteWriter = null;
                mw.Close();
                hadMatte = matteRendered > 0;
                Logger.LogInfo($"Engine matte: {mw.Frames} frames ({matteRendered} with the aircraft rendered), {mw.Errors} errors");
                if (!hadMatte && dir != null)
                {
                    // No matte shot in this range: drop the all-black plane file instead of encoding it.
                    try { File.Delete(Path.Combine(dir, "matte.gray")); } catch (Exception) { }
                }
                matteRendered = 0;
            }
            if (frameRoot == null || dir == null) return;
            try
            {
                string done = dir.TrimEnd('\\', '/') + ".done";
                if (File.Exists(done)) return;
                // Raw frames are RGBA, top row first (checked against PNG export under Proton/DXVK).
                File.WriteAllText(done, "{" + $"\"fps\":{frameRate},\"frames\":{frames},\"format\":\"{frameFormat}\",\"width\":{w},\"height\":{h},"
                    + $"\"errors\":{errors},\"from\":{captureFrom.ToString(CultureInfo.InvariantCulture)},"
                    + $"\"to\":{captureTo.ToString(CultureInfo.InvariantCulture)}" + (hadMatte ? ",\"matte\":\"gray\"" : "") + "}\n");
            }
            catch (Exception e)
            {
                Logger.LogError("Range marker failed: " + e);
            }
        }

        private void LogPerf(double replayTime)
        {
            double now = Time.unscaledTimeAsDouble;
            if (nextPerfLog == 0) { nextPerfLog = now + 5; perfFrameStart = Time.frameCount; perfReplayStart = replayTime; return; }
            if (now < nextPerfLog) return;
            double span = now - (nextPerfLog - 5);
            Logger.LogInfo($"Perf {DateTime.UtcNow:HH:mm:ss}: {(Time.frameCount - perfFrameStart) / span:F1} frames/s, replay x{(replayTime - perfReplayStart) / span:F2}, "
                + $"at {replayTime:F1}s, {(capturing ? "capturing" : fastForwarding ? "skipping" : "preroll")}, vsync {QualitySettings.vSyncCount}, target {Application.targetFrameRate}");
            nextPerfLog = now + 5; perfFrameStart = Time.frameCount; perfReplayStart = replayTime;
        }

        // Hides or restores the game's own UI canvases. Rescans each second for canvases created later.

        private void ApplyHud()
        {
            if (Time.unscaledTimeAsDouble < nextHudScan) return;
            nextHudScan = Time.unscaledTimeAsDouble + 1;
            if (hudHidden)
            {
                foreach (var canvas in FindObjectsOfType<Canvas>())
                    if (canvas.isRootCanvas && canvas.enabled && canvas != ui?.Canvas && !cockpitView.Keeps(canvas)) { canvas.enabled = false; hiddenCanvases.Add(canvas); }
            }
            else if (hiddenCanvases.Count > 0)
            {
                foreach (var canvas in hiddenCanvases) if (canvas != null) canvas.enabled = true;
                hiddenCanvases.Clear();
            }
        }

        // The replay clock delta for this frame; the client's transform interpolation uses it too.
        internal float TransformDelta(float original) => server == null ? original : (float)lastDelta;
        internal double ServerTime => server.ServerNetworkTime;

        internal void RedirectNetwork(NetworkManagerNuclearOption manager)
        {
            manager.Client.SocketFactory = factory;
            if (manager.Client.PeerConfig != null) manager.Client.PeerConfig.TimeoutDuration = 600f;
            Logger.LogInfo("Replay client socket redirected to in-memory loopback");
        }

        private void OnGUI()
        {
            if (server == null && !status.StartsWith("replay")) return;
            if (viewer || !overlay.Value || job != null || preview != null || Time.captureFramerate > 0 && frameDirectory != null) return;
            if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };
            GUI.Label(new Rect(12, 8, Screen.width - 24, 28), status, style);
        }

        private void OnApplicationQuit()
        {
            FinishRange();
            // The plugin itself quits only after a job is done or failed. Any other quit (the player closed the
            // game) fails the job, so its encoders are stopped and nr reads why it ended.
            if (job != null && !job.Finished) job.Fail("the game closed before the job finished");
            // A viewer clip cannot be finished once the game is gone; cancel it so its ffmpeg does not
            // outlive the game and no half-written file is left.
            if (recorder != null) { recorder.Cancel(); recorder = null; clipFinishing = false; }
            ffmpegDownload?.Shutdown();
            clockLog?.Dispose();
            clockLog = null;
        }

        private void OnDestroy()
        {
            if (preview != null) Logger.LogWarning("Replay plugin destroyed while a preview runs:\n" + Environment.StackTrace);
            server?.Close();
            preview?.Close();
        }
    }

    internal static class ReplayPatches
    {
        // Viewer: keeps the game's flight HUD (the bottom info bar) off while the viewer hides it; the game's
        // camera states switch it back on as they change.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FlightHud), nameof(FlightHud.EnableCanvas))]
        private static void FlightHudPrefix(ref bool enable)
        {
            var replay = ReplayPlugin.Instance;
            if (enable && replay != null && replay.ViewerHidesFlightHud) enable = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NetworkManagerNuclearOption), "ConfigureNetwork")]
        private static void ConfigureNetworkPostfix(NetworkManagerNuclearOption __instance)
        {
            var replay = ReplayPlugin.Instance;
            if (replay != null && replay.Redirecting) replay.RedirectNetwork(__instance);
        }

        // Mission time, objectives and timers read NetworkTime.Time; report recorded host time.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NetworkTime), nameof(NetworkTime.Time), MethodType.Getter)]
        private static bool NetworkTimePrefix(ref double __result)
        {
            var replay = ReplayPlugin.Instance;
            if (replay == null || !replay.Running) return true;
            __result = replay.ServerTime;
            return false;
        }

        // Transform interpolation advances with the replay clock, so pause and speed stay consistent.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SmoothNetworkTime), nameof(SmoothNetworkTime.Update))]
        private static void SmoothTimePrefix(ref float deltaTime)
        {
            var replay = ReplayPlugin.Instance;
            if (replay != null && replay.Running) deltaTime = replay.TransformDelta(deltaTime);
        }
    }
}
