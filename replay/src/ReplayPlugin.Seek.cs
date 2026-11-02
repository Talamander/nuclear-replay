using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using NuclearReplay.Demo;
using NuclearOption.Networking;
using NuclearOption.SceneLoading;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace NuclearReplay.Playback
{
    // Faster viewer timeline jumps (0.6). The replay still only runs forward: a forward jump fast-forwards from where
    // the replay is, a backward jump reloads the mission and fast-forwards from the start (docs/JUMP-POINTS.md). The
    // replay clock still advances 8/60 s a game frame as in 0.5 (SeekMath); what changed while a viewer jump
    // fast-forwards ("turbo"):
    //   - Physics runs at a 1/20 s step instead of the game's 1/60 s (SeekMath.JumpPhysicsStep). Physics was about
    //     two thirds of a jump's time. The recorded network messages still set every unit's position, state, targets
    //     and kills, and the game's own step is back before the replay reaches the target.
    //   - Only about 12 frames a second are drawn (OnDemandRendering.renderFrameInterval); the frames in between run
    //     the game loop but are not rendered. Game sound is muted for the jump.
    //   - Backward jumps: the recording is not read from disk again (the loaded records are kept while the viewer
    //     has the replay open), and the reload starts as soon as the main menu scene is up instead of after a fixed
    //     5 s wait.
    //   - The label shows how far the jump is; Space stops it where it is.
    // Render jobs, frame exports, previews, audio ranges and -ncStart without the viewer never take this path, and a
    // clip recording's own jump keeps the 0.5 behaviour, so renders and clips are unchanged. Test command
    // "seekturbo off" gives the 0.5 behaviour for comparisons.
    public sealed partial class ReplayPlugin
    {
        private bool seekTurboAllowed = true;      // test command "seekturbo off" measures the old way
        private bool seekTurboOn;
        private bool seekCovered, seekStopRequested;
        private readonly PendingViewerSeek pendingViewerSeek = new PendingViewerSeek();
        private double seekDeferredSeconds;
        private int seekSavedInterval = 1;
        private float seekFixedDt = SeekMath.JumpPhysicsStep, seekSavedFixedDt = -1;   // test command "seekfixed <dt>|0"
        private float seekSavedVolume = 1f;
        // Jump bookkeeping for the log line and the progress label.
        private Stopwatch seekWatch;
        private double seekFrom, seekTo, seekFastFrom = -1, seekMenuAt = -1, seekLiveAt = -1;
        private long seekDemoMs = -1;
        private bool seekRestart, seekDemoCached;
        private int seekFrames, seekDrawn, seekGc0, seekGc2;
        private float seekStepUsed = -1;
        // Backward jumps reuse the recording already in memory.
        private DemoReader cachedDemo;
        private string cachedDemoKey;
        private double restartIssuedAt = -1, menuSeenAt = -1;
        // Test hook: "probe <t> <path>" pauses at replay time t, writes the units to <path>.json and a screenshot.
        private double probeAt = -1;
        private string probePath;
        // Test hook: "seekprofile on" times the game loop's phases during jumps (research; never on for players).
        private static readonly string[] profPhases = { "Initialization", "EarlyUpdate", "FixedUpdate", "PreUpdate", "Update", "PreLateUpdate", "PostLateUpdate" };
        private static readonly long[] profTicks = new long[7], profStart = new long[7];
        private static int profFixedRuns;
        private static readonly Dictionary<string, long> profFixedChild = new Dictionary<string, long>();
        private static long profChildStart;
        private static bool profInstalled, profOn;
        private bool seekProfile;
        private sealed class SeekProfStart { }
        private sealed class SeekProfEnd { }
        // Jump points (docs/JUMP-POINTS.md). Enabled by default; [Viewer] JumpPoints and seekmode can disable them.
        private enum JumpMode { FastForward, Auto, Always }
        private ConfigEntry<bool> jumpPointsCfg;
        private JumpMode jumpMode;
        private bool jumpModeSet;
        private double jumpRunIn = SeekMath.JumpRunIn, jumpTarget = -1, jumpSceneAt = -1, jumpSentAt = -1;
        private double seekRate, seekReloadSeconds, jumpPointSeconds;
        private string jumpFailReason, jumpForceFail;
        private int jumpFailures;
        private JumpPlan jumpPlan;
        private bool seekByJumpPoint;
        private int jumpPlaceFrames, jumpPlaced;
        private JumpIndex jumpIndex;
        private List<DemoRecord> jumpIndexRecords;
        private volatile bool jumpIndexCancel;
        private const int MaxJumpFailures = 2, JumpBurstFrames = 6;

        private JumpMode CurrentJumpMode => jumpModeSet ? jumpMode : jumpPointsCfg.Value ? JumpMode.Auto : JumpMode.FastForward;

        private bool JumpWanted(double now, double target, bool keepClip)
        {
            var mode = CurrentJumpMode;
            if (mode == JumpMode.FastForward || keepClip || recorder != null || target <= jumpRunIn + 5) return false;
            if (jumpIndex == null || jumpFailures >= MaxJumpFailures) return false;
            return mode == JumpMode.Always || SeekMath.UseJumpPoint(now, target, seekRate > 0 ? seekRate : SeekMath.DefaultFastForwardRate,
                seekReloadSeconds > 0 ? seekReloadSeconds : SeekMath.DefaultReloadSeconds, jumpPointSeconds > 0 ? jumpPointSeconds : SeekMath.DefaultJumpPointSeconds);
        }

        // A recording opened. Runs off the main thread; until it is done, jumps fast-forward.
        private void JumpIndexStart(List<DemoRecord> records)
        {
            if (records == null || CurrentJumpMode == JumpMode.FastForward || ReferenceEquals(records, jumpIndexRecords)) return;
            JumpIndexForget();
            string mismatch = JumpPlan.WireMismatch();
            if (mismatch != null) { Logger.LogWarning("Jump points off: the game's message ids changed (" + mismatch + ")"); return; }
            jumpIndexRecords = records;
            jumpIndexCancel = false;
            var thread = new Thread(() =>
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    var index = JumpIndex.Build(records, () => jumpIndexCancel);
                    if (index == null || !ReferenceEquals(records, jumpIndexRecords)) return;
                    jumpIndex = index;
                    Logger.LogInfo($"Jump index: {records.Count} records in {watch.ElapsedMilliseconds} ms, {index.SegmentCount} missions, {index.ObjectCount} objects, {index.MessageCount} messages, "
                        + $"{index.TransformBatches} transform batches, {index.CheckpointCount} checkpoints, {index.BadRecords} unreadable");
                }
                catch (Exception e) { Logger.LogWarning("Jump index failed, jumps will fast-forward: " + e); }
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "NuclearReplay jump index" };
            thread.Start();
        }

        private void JumpIndexForget()
        {
            jumpIndexCancel = true;
            jumpIndex = null;
            jumpIndexRecords = null;
            jumpFailures = 0;
        }

        // TryStart: a jump point restart replaces the fast-forward from zero with a burst up to the cut.
        private void JumpAttach(DemoReader demo)
        {
            jumpPlan = null;
            JumpPlan.BurstActive = false;
            JumpIndexStart(demo.Records);
            if (jumpTarget < 0) return;
            double target = jumpTarget;
            jumpTarget = -1;
            try
            {
                jumpPlan = jumpIndex != null ? JumpPlan.Create(Logger, jumpIndex, demo.Records, server.StartTime + target - jumpRunIn) : null;
                if (jumpPlan == null) { Logger.LogWarning("Jump point: no plan, fast-forwarding"); return; }
                jumpPlan.FailBuild = jumpForceFail == "build";
                jumpPlan.FailSend = jumpForceFail == "send";
                jumpPlan.StallSend = jumpForceFail == "timeout";
                jumpPlan.CorruptBurst = jumpForceFail == "corrupt";
                seekByJumpPoint = true;
                jumpSceneAt = jumpSentAt = -1;
                jumpPlaceFrames = 0;
                server.JumpSceneReady += () => { if (seekWatch != null) jumpSceneAt = seekWatch.Elapsed.TotalSeconds; };
                server.JumpFailed += reason => jumpFailReason = reason;
                server.JumpBurstSent += () =>
                {
                    if (seekWatch != null) jumpSentAt = seekWatch.Elapsed.TotalSeconds;
                    seekFastFrom = -1; seekLiveAt = -1;
                    Logger.LogInfo($"Jump point: scene ready at {jumpSceneAt:F2} s, burst sent at {jumpSentAt:F2} s, replay at {server.Clock - server.StartTime:F1} s");
                };
                server.StartJump(jumpPlan);
            }
            catch (Exception e) { Logger.LogError("Jump point failed: " + e); jumpPlan = null; }
        }

        // Every frame of a jump. After the burst it runs for some frames, because the client takes a moment to handle
        // all of it.
        private void JumpTick()
        {
            if (jumpFailReason != null) { JumpFallback(jumpFailReason); return; }
            if (jumpPlan == null || server == null || server.Jumping || jumpSentAt < 0) return;
            try
            {
                var world = NetworkManagerNuclearOption.i.Client.World;
                jumpPlaced = jumpPlan.PlaceStoppedVehicles(world);
                // The client has handled the burst by now.
                if (++jumpPlaceFrames == JumpBurstFrames)
                {
                    JumpRadarPatches.RestoreRecordedState();
                    JumpPlan.BurstActive = false;
                }
                if (jumpPlaceFrames < 30) return;
                int spawned = world.SpawnedIdentities.Count;
                Logger.LogInfo($"Jump point: {jumpPlaced} stopped vehicles placed, {spawned} of {jumpPlan.ExpectedObjects} objects");
                if (!SeekMath.EnoughObjects(jumpForceFail == "count" ? 0 : spawned, jumpPlan.ExpectedObjects))
                {
                    JumpFallback($"{spawned} objects after the burst, expected {jumpPlan.ExpectedObjects}");
                    return;
                }
                jumpPlan = null;
            }
            catch (Exception e) { JumpFallback(e.ToString()); }
        }

        // The jump point cannot finish: reload and fast-forward from the start, the 0.6 way.
        private void JumpFallback(string reason)
        {
            Logger.LogWarning("Jump point failed, fast-forwarding instead: " + reason);
            JumpPlan.BurstActive = false;
            jumpFailReason = null;
            jumpPlan = null;
            jumpFailures++;
            jumpForceFail = null;
            double target = viewerSeekTarget >= 0 ? viewerSeekTarget : seekTo;
            ViewerReload(target, server != null ? server.Clock - server.StartTime : 0);
        }

        // A jump starts (ViewerSeek, opening a replay at a time).
        private void SeekBegin(double from, double to, bool restart)
        {
            seekDeferredSeconds = 0;
            seekStopRequested = false;
            if (viewer && recorder == null) SeekCover(true);
            seekWatch = Stopwatch.StartNew();
            seekFrom = from; seekTo = to; seekRestart = restart;
            seekFastFrom = -1; seekMenuAt = -1; seekLiveAt = -1; seekDemoMs = -1; seekDemoCached = false;
            seekFrames = 0; seekDrawn = 0; seekStepUsed = -1; seekByJumpPoint = false;
            seekGc0 = GC.CollectionCount(0); seekGc2 = GC.CollectionCount(2);
            Array.Clear(profTicks, 0, profTicks.Length); profFixedRuns = 0; profFixedChild.Clear();
        }

        // Wraps each top-level phase of Unity's player loop in two timing systems (only after "seekprofile on").
        private static void InstallSeekProfiler()
        {
            if (profInstalled) return;
            profInstalled = true;
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            for (int i = 0; i < subs.Length; i++)
            {
                int k = Array.IndexOf(profPhases, subs[i].type?.Name);
                if (k < 0) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList ?? new PlayerLoopSystem[0]);
                if (k == 2)
                {
                    // FixedUpdate: time each child system too.
                    var wrapped = new List<PlayerLoopSystem>();
                    foreach (var child in list)
                    {
                        string name = child.type?.Name ?? "?";
                        wrapped.Add(new PlayerLoopSystem { type = typeof(SeekProfStart), updateDelegate = () => profChildStart = Stopwatch.GetTimestamp() });
                        wrapped.Add(child);
                        wrapped.Add(new PlayerLoopSystem
                        {
                            type = typeof(SeekProfEnd),
                            updateDelegate = () =>
                            {
                                if (!profOn) return;
                                profFixedChild.TryGetValue(name, out long t);
                                profFixedChild[name] = t + Stopwatch.GetTimestamp() - profChildStart;
                            },
                        });
                    }
                    list = wrapped;
                }
                list.Insert(0, new PlayerLoopSystem { type = typeof(SeekProfStart), updateDelegate = () => profStart[k] = Stopwatch.GetTimestamp() });
                list.Add(new PlayerLoopSystem
                {
                    type = typeof(SeekProfEnd),
                    updateDelegate = () => { if (!profOn) return; profTicks[k] += Stopwatch.GetTimestamp() - profStart[k]; if (k == 2) profFixedRuns++; },
                });
                subs[i].subSystemList = list.ToArray();
            }
            root.subSystemList = subs;
            PlayerLoop.SetPlayerLoop(root);
        }

        private string SeekProfileText(double fastSeconds)
        {
            if (!seekProfile || seekFrames == 0) return "";
            var c = CultureInfo.InvariantCulture;
            double sum = 0;
            var parts = new List<string>();
            for (int k = 0; k < profPhases.Length; k++)
            {
                double sec = profTicks[k] / (double)Stopwatch.Frequency;
                sum += sec;
                parts.Add(profPhases[k] + " " + sec.ToString("F1", c));
            }
            return $"; profile s: {string.Join(", ", parts)}, other (drawing, present, waiting) {(fastSeconds - sum).ToString("F1", c)}; "
                + $"FixedUpdate runs {profFixedRuns} ({(profFixedRuns / (double)seekFrames).ToString("F2", c)} a frame): "
                + string.Join(", ", profFixedChild.OrderByDescending(kv => kv.Value).Take(4)
                    .Select(kv => kv.Key + " " + (kv.Value / (double)Stopwatch.Frequency).ToString("F1", c)));
        }

        // Every frame of a viewer jump's fast-forward (StartSeek, before the target is reached).
        private void SeekTick(double replayNow)
        {
            bool live = server != null && server.ScenesLoaded > 0 && !server.WaitingForScene && !NetworkManagerNuclearOption.IsLoadingScene;
            profOn = seekProfile && seekWatch != null && live;
            if (seekWatch != null && live)
            {
                if (seekLiveAt < 0) { seekLiveAt = seekWatch.Elapsed.TotalSeconds; seekFastFrom = replayNow; }
                seekFrames++;
                if (OnDemandRendering.willCurrentFrameRender) seekDrawn++;
            }
            // A clip recording's own jump to its start keeps drawing every frame, as before 0.6, so recorded clips
            // come out exactly as they did (ReplayPlugin.cs notes why frame export never skips drawing).
            if (!live || !seekTurboAllowed || recorder != null) { SeekTurbo(false); return; }
            SeekCover(true);
            SeekTurbo(true);
            // fpsNow counts every game frame (drawn or not), measured over the last second.
            int interval = SeekMath.RenderInterval(fpsNow);
            if (OnDemandRendering.renderFrameInterval != interval) OnDemandRendering.renderFrameInterval = interval;
        }

        private void SeekTurbo(bool on)
        {
            if (on == seekTurboOn) return;
            seekTurboOn = on;
            try
            {
                if (on)
                {
                    float step = SeekMath.PhysicsStep(Time.fixedDeltaTime, seekFixedDt);
                    if (step != Time.fixedDeltaTime) { seekSavedFixedDt = Time.fixedDeltaTime; Time.fixedDeltaTime = step; }
                    seekStepUsed = step;
                }
                else
                {
                    if (seekSavedFixedDt > 0) { Time.fixedDeltaTime = seekSavedFixedDt; seekSavedFixedDt = -1; }
                }
            }
            catch (Exception e) { Logger.LogWarning("Jump: " + e.Message); }
        }

        // Keep sound and drawing covered through scene reloads as well as the burst and run-in.
        // Physics still restores before unloading a scene. Render jobs and clip seeks never enter this scope.
        private void SeekCover(bool on)
        {
            if (on == seekCovered) return;
            seekCovered = on;
            if (on)
            {
                seekSavedInterval = Math.Max(1, OnDemandRendering.renderFrameInterval);
                seekSavedVolume = AudioListener.volume;
                AudioListener.volume = 0f;
                OnDemandRendering.renderFrameInterval = SeekMath.RenderInterval(Math.Max(60, fpsNow));
            }
            else
            {
                OnDemandRendering.renderFrameInterval = seekSavedInterval;
                AudioListener.volume = seekSavedVolume;
            }
        }

        // The jump reached its target (ViewerSeekReached) or was stopped there.
        private void SeekEnd(double replayNow)
        {
            SeekTurbo(false);
            SeekCover(false);
            seekStopRequested = false;
            if (seekWatch == null) return;
            double elapsed = seekWatch.Elapsed.TotalSeconds;
            double wall = elapsed + seekDeferredSeconds;
            double fast = seekLiveAt >= 0 ? elapsed - seekLiveAt : 0;
            double covered = seekFastFrom >= 0 ? replayNow - seekFastFrom : 0;
            var c = CultureInfo.InvariantCulture;
            string reload = seekRestart
                ? $"reload {(seekLiveAt >= 0 ? seekLiveAt : wall).ToString("F1", c)} s (menu {(seekMenuAt >= 0 ? seekMenuAt.ToString("F1", c) : "?")} s, recording {(seekDemoCached ? "cached" : seekDemoMs + " ms")}), "
                : "";
            if (seekDeferredSeconds > 0) reload = $"queued {seekDeferredSeconds.ToString("F1", c)} s for the previous load, " + reload;
            if (seekRestart && !seekByJumpPoint && seekLiveAt > 0) seekReloadSeconds = SeekMath.Smooth(seekReloadSeconds, seekLiveAt);
            if (covered >= 5 && fast > 0) seekRate = SeekMath.Smooth(seekRate, SeekMath.Rate(covered, fast));
            if (seekByJumpPoint) jumpPointSeconds = SeekMath.Smooth(jumpPointSeconds, wall);
            Logger.LogInfo($"Jump {seekFrom.ToString("F1", c)} -> {seekTo.ToString("F1", c)} s took {wall.ToString("F1", c)} s, by {(seekByJumpPoint ? "jump point" : "fast-forward")}: {reload}"
                + $"fast-forward {covered.ToString("F1", c)} replay s in {fast.ToString("F1", c)} s ({SeekMath.Rate(covered, fast).ToString("F1", c)}x), "
                + $"{seekFrames} frames ({(fast > 0 ? seekFrames / fast : 0).ToString("F0", c)}/s), {seekDrawn} drawn, "
                + $"GC {GC.CollectionCount(0) - seekGc0} gen0 / {GC.CollectionCount(2) - seekGc2} gen2, turbo {(seekTurboAllowed ? "on" : "off")}, "
                + $"physics step {(seekStepUsed > 0 ? seekStepUsed : Time.fixedDeltaTime).ToString("0.####", c)} (game {Time.fixedDeltaTime.ToString("0.####", c)}), maximumDeltaTime {Time.maximumDeltaTime.ToString("0.###", c)}"
                + SeekProfileText(fast));
            seekWatch = null; profOn = false;
        }

        // Leaving the replay, or a restart: drawing and sound come back.
        private void SeekAbort(bool keepStats)
        {
            pendingViewerSeek.Clear();
            SeekTurbo(false);
            profOn = false;
            jumpPlan = null;
            jumpFailReason = null;
            JumpPlan.BurstActive = false;
            if (!keepStats)
            {
                SeekCover(false);
                seekWatch = null;
                seekStopRequested = false;
                jumpTarget = -1;
                jumpForceFail = null;
            }
        }

        // The jump's progress label (BuildState).
        private string SeekLabel()
        {
            if (viewerSeekTarget < 0) return "Loading the replay...";
            // Until the replay runs again after a reload (or the first load), there is no progress to show.
            bool running = server != null && viewerLive;
            double now = server != null ? server.Clock - server.StartTime : 0;
            double from = seekFastFrom >= 0 ? seekFastFrom : seekRestart ? 0 : seekFrom;
            return SeekMath.Label(ViewerCatalog.Clock(viewerSeekTarget), !running && (viewerRestarting || server == null),
                SeekMath.Fraction(from, now, viewerSeekTarget), running);
        }

        // Space while a jump fast-forwards: stop here.
        private void ViewerStopJump()
        {
            pendingViewerSeek.Clear();
            if (!startSeeking) return;
            // Never expose a half-built world. Stop at the first complete state after the burst drains.
            seekStopRequested = true;
            if (server == null || !SeekMath.CanStopJump(viewerLive, server.Jumping, jumpPlan != null)) return;
            double now = server.Clock - server.StartTime;
            Logger.LogInfo($"Viewer: jump stopped at {now:F1} s");
            startAt = now;
            viewerSeekTarget = now;
        }

        // TryStart: the recording to play. A viewer reload of the same unchanged file reuses the records in memory.
        private DemoReader LoadDemo(string path)
        {
            string key = null;
            try { var info = new FileInfo(path); key = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks; }
            catch (Exception) { }
            if (seekMenuAt < 0 && seekWatch != null) seekMenuAt = seekWatch.Elapsed.TotalSeconds;
            if (viewer && key != null && key == cachedDemoKey && cachedDemo != null)
            {
                seekDemoCached = true;
                Logger.LogInfo("Replay reuses the recording already loaded");
                return cachedDemo;
            }
            var watch = Stopwatch.StartNew();
            var demo = DemoReader.Load(path);
            seekDemoMs = watch.ElapsedMilliseconds;
            Logger.LogInfo($"Replay read {path} in {seekDemoMs} ms");
            cachedDemo = viewer ? demo : null;
            cachedDemoKey = viewer ? key : null;
            return demo;
        }

        private void ForgetCachedDemo() { cachedDemo = null; cachedDemoKey = null; JumpIndexForget(); }

        // TryStart after a restart: the old wait was a fixed 5 s. A viewer jump goes on as soon as the game is back in
        // its main menu scene and done loading (it unloads the mission and collects garbage first); 5 s stays the cap.
        private bool RestartWaitOver()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= restartAfter) return true;
            if (!viewer || !viewerRestarting || restartIssuedAt < 0 || now < restartIssuedAt + 0.5) return false;
            bool menu = SceneManager.GetActiveScene().path == MapLoader.MainMenu && !NetworkManagerNuclearOption.IsLoadingScene
                && MainMenu.State == MainMenu.LoadingState.Loaded;
            if (!menu) { menuSeenAt = -1; return false; }
            if (menuSeenAt < 0) menuSeenAt = now;
            return now >= menuSeenAt + 0.25;
        }

        // ---------------- test hooks (-ncViewerCommands) ----------------

        private string SeekCommand(string cmd, string arg)
        {
            switch (cmd)
            {
                case "seekturbo":
                    seekTurboAllowed = arg != "off";
                    if (!seekTurboAllowed) SeekTurbo(false);
                    return "seek turbo " + (seekTurboAllowed ? "on" : "off");
                case "stopjump": ViewerStopJump(); return "stop jump";
                case "seekmode":
                    jumpMode = arg == "snap" ? JumpMode.Always : arg == "auto" ? JumpMode.Auto : JumpMode.FastForward;
                    jumpModeSet = true;
                    JumpIndexStart(server != null ? cachedDemo?.Records : null);
                    return "seek mode " + jumpMode;
                case "seekfail": jumpForceFail = arg; return "seek fail " + arg;
                case "seekrunin": jumpRunIn = Math.Max(1, Num(arg, 30)); return "seek run-in " + jumpRunIn;
                case "seekprofile":
                    seekProfile = arg != "off";
                    if (seekProfile) InstallSeekProfiler();
                    return "seek profile " + (seekProfile ? "on" : "off");
                // Research only: a different fast-forward step for the following jumps (speed/fps replay s a frame).
                case "seekstep":
                    {
                        var p = arg.Split(' ');
                        fastForwardSpeed = (float)Math.Max(1, Math.Min(64, Num(p[0], SeekMath.FastForwardSpeed)));
                        fastForwardStepFps = (int)Math.Max(10, Num(p.Length > 1 ? p[1] : null, SeekMath.FastForwardStepFps));
                        return $"seek step {fastForwardSpeed}/{fastForwardStepFps}";
                    }
                case "seekfixed":
                    seekFixedDt = (float)Math.Max(0, Math.Min(0.1, Num(arg, SeekMath.JumpPhysicsStep)));
                    return "seek fixed step " + seekFixedDt;
                case "probe":
                    {
                        var parts = arg.Split(new[] { ' ' }, 2);
                        probeAt = Num(parts[0], -1);
                        probePath = parts.Length > 1 ? HostPath(parts[1].Trim()) : null;
                        return probePath != null ? $"probe at {probeAt} -> {probePath}" : "probe needs <t> <path>";
                    }
                case "dumpunits": DumpUnits(HostPath(arg), server != null ? server.Clock - server.StartTime : -1); return "units " + arg;
                default: return null;
            }
        }

        // ViewerLateUpdate: once the replay plays past the probe time, pause and write what is there.
        private void ProbeStep(double replayTime, bool live)
        {
            if (probePath == null || !live || startSeeking || replayTime < probeAt) return;
            string path = probePath;
            probePath = null;
            if (!viewerPaused) ViewerTogglePause();
            server.Paused = true;
            DumpUnits(path + ".json", replayTime);
            try { ScreenCapture.CaptureScreenshot(path + ".png"); } catch (Exception e) { Logger.LogWarning("Probe screenshot: " + e.Message); }
            try { File.WriteAllText(path + ".json.done", replayTime.ToString("R", CultureInfo.InvariantCulture)); } catch (Exception) { }
        }

        // Every unit the client knows, for comparing a jump with a straight play to the same time.
        private static JObject ProbeCamera()
        {
            var camera = HudCamera();
            if (camera == null) return null;
            var p = camera.transform.position.ToGlobalPosition().AsVector3();
            var q = camera.transform.rotation;
            return new JObject
            {
                ["p"] = new JArray(p.x, p.y, p.z), ["q"] = new JArray(q.x, q.y, q.z, q.w), ["fov"] = camera.fieldOfView,
            };
        }

        private void DumpUnits(string path, double replayTime)
        {
            try
            {
                var units = new JArray();
                foreach (var u in UnitRegistry.allUnits.ToList())
                {
                    if (u == null) continue;
                    var p = u.transform.position.ToGlobalPosition().AsVector3();
                    var v = u.rb != null ? u.rb.velocity : Vector3.zero;
                    var o = new JObject
                    {
                        ["id"] = u.persistentID.ToString(), ["name"] = u.unitName, ["kind"] = Units.Kind(u).ToString(),
                        ["disabled"] = u.disabled, ["state"] = u.unitState.ToString(),
                        ["p"] = new JArray(Math.Round(p.x, 1), Math.Round(p.y, 1), Math.Round(p.z, 1)),
                        ["v"] = new JArray(Math.Round(v.x, 1), Math.Round(v.y, 1), Math.Round(v.z, 1)),
                    };
                    o["parts_off"] = u.damageables?.Count(d => d.Removed) ?? 0;
                    o["parts"] = u.damageables == null ? new JArray() : new JArray(u.damageables.Select((d, i) => new JObject
                    {
                        ["index"] = i, ["removed"] = d.Removed,
                        ["health"] = d.Damageable is UnitPart part ? (JToken)Math.Round(part.hitPoints, 4) : null,
                    }));
                    if (u.weaponStations != null) o["ammo"] = new JArray(u.weaponStations.Select(w => w.Ammo).ToArray());
                    o["radar"] = u.radar != null && u.radar.activated;
                    if (u is Aircraft air)
                    {
                        o["fuel"] = Math.Round(air.fuelLevel, 3);
                        o["active_station"] = air.weaponStations?.IndexOf(air.weaponManager.currentWeaponStation) ?? -1;
                    }
                    o["hq"] = u.NetworkHQ != null ? u.NetworkHQ.faction?.factionName : null;
                    if (u is Missile m) { o["target"] = m.targetID.ToString(); o["owner"] = m.ownerID.ToString(); o["seeker"] = m.seekerMode.ToString(); }
                    units.Add(o);
                }
                var frame = hudOverlay?.LastFrame;
                var doc = new JObject
                {
                    ["time"] = replayTime, ["frame"] = Time.frameCount, ["units"] = units,
                    ["following"] = director?.CurrentUnit != null ? director.CurrentUnit.persistentID.ToString() : null,
                    ["hud_marks"] = frame?.Marks.Count, ["hud_lines"] = frame?.Lines.Count,
                    ["hud_mark_state"] = frame == null ? new JArray() : new JArray(frame.Marks.OrderBy(m => m.Unit.Id).Select(m => new JObject
                    {
                        ["id"] = m.Unit.Id, ["role"] = m.Role.ToString(), ["side"] = m.Side.ToString(),
                        ["look"] = m.Look.ToString(), ["box"] = m.Box, ["arrow"] = m.Arrow,
                    })),
                    ["hud_line_state"] = frame == null ? new JArray() : new JArray(frame.Lines.OrderBy(l => l.From.Id).ThenBy(l => l.To.Id).Select(l => new JObject
                    {
                        ["from"] = l.From.Id, ["to"] = l.To.Id, ["kind"] = l.Kind.ToString(), ["solid"] = l.Solid,
                    })),
                    ["camera"] = ProbeCamera(),
                    ["factions"] = new JArray(FactionRegistry.GetAllHQs().OrderBy(h => h.faction.factionName).Select(h => new JObject
                    {
                        ["name"] = h.faction.factionName, ["score"] = h.factionScore,
                        ["tracked"] = new JArray(h.trackingDatabase.Keys.Select(id => id.ToString()).OrderBy(id => id)),
                    })),
                    ["airbases"] = new JArray(FindObjectsOfType<Airbase>().OrderBy(b => b.name).Select(b => new JObject
                    {
                        ["name"] = b.name, ["owner"] = b.CurrentHQ?.faction?.factionName,
                    })),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, doc.ToString());
            }
            catch (Exception e) { Logger.LogWarning("Unit dump: " + e); }
        }
    }
}
