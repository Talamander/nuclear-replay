using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
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
    // fast-forward runs the same fixed steps as before (SeekMath), so the game simulates exactly what it did in 0.5;
    // what changed:
    //   - Drawing: while a viewer jump fast-forwards, only about 12 frames a second are drawn
    //     (OnDemandRendering.renderFrameInterval). The game loop, physics and network messages run every frame as
    //     before; the frames in between are not rendered. Game sound is muted for the jump.
    //   - Backward jumps: the recording is not read from disk again (the loaded records are kept while the viewer
    //     has the replay open), and the reload starts as soon as the main menu scene is up instead of after a fixed
    //     5 s wait.
    //   - The label shows how far the jump is; Space stops it where it is.
    // Render jobs, frame exports, previews, audio ranges and -ncStart without the viewer never take this path, so
    // renders are unchanged.
    public sealed partial class ReplayPlugin
    {
        private bool seekTurboAllowed = true;      // test command "seekturbo off" measures the old way
        private bool seekTurboOn;
        private int seekSavedInterval = 1;
        private float seekSavedVolume = 1f;
        // Jump bookkeeping for the log line and the progress label.
        private Stopwatch seekWatch;
        private double seekFrom, seekTo, seekFastFrom = -1, seekMenuAt = -1, seekLiveAt = -1;
        private long seekDemoMs = -1;
        private bool seekRestart, seekDemoCached;
        private int seekFrames, seekDrawn, seekGc0, seekGc2;
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
        private static bool profInstalled, profOn;
        private bool seekProfile;
        private sealed class SeekProfStart { }
        private sealed class SeekProfEnd { }

        // A jump starts (ViewerSeek, opening a replay at a time).
        private void SeekBegin(double from, double to, bool restart)
        {
            seekWatch = Stopwatch.StartNew();
            seekFrom = from; seekTo = to; seekRestart = restart;
            seekFastFrom = -1; seekMenuAt = -1; seekLiveAt = -1; seekDemoMs = -1; seekDemoCached = false;
            seekFrames = 0; seekDrawn = 0;
            seekGc0 = GC.CollectionCount(0); seekGc2 = GC.CollectionCount(2);
            Array.Clear(profTicks, 0, profTicks.Length); profFixedRuns = 0;
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
                + $"FixedUpdate runs {profFixedRuns} ({(profFixedRuns / (double)seekFrames).ToString("F2", c)} a frame)";
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
                    seekSavedInterval = Math.Max(1, OnDemandRendering.renderFrameInterval);
                    seekSavedVolume = AudioListener.volume;
                    AudioListener.volume = 0f;
                }
                else
                {
                    OnDemandRendering.renderFrameInterval = seekSavedInterval;
                    AudioListener.volume = seekSavedVolume;
                }
            }
            catch (Exception e) { Logger.LogWarning("Jump: " + e.Message); }
        }

        // The jump reached its target (ViewerSeekReached) or was stopped there.
        private void SeekEnd(double replayNow)
        {
            SeekTurbo(false);
            if (seekWatch == null) return;
            double wall = seekWatch.Elapsed.TotalSeconds;
            double fast = seekLiveAt >= 0 ? wall - seekLiveAt : 0;
            double covered = seekFastFrom >= 0 ? replayNow - seekFastFrom : 0;
            var c = CultureInfo.InvariantCulture;
            string reload = seekRestart
                ? $"reload {(seekLiveAt >= 0 ? seekLiveAt : wall).ToString("F1", c)} s (menu {(seekMenuAt >= 0 ? seekMenuAt.ToString("F1", c) : "?")} s, recording {(seekDemoCached ? "cached" : seekDemoMs + " ms")}), "
                : "";
            Logger.LogInfo($"Jump {seekFrom.ToString("F1", c)} -> {seekTo.ToString("F1", c)} s took {wall.ToString("F1", c)} s: {reload}"
                + $"fast-forward {covered.ToString("F1", c)} replay s in {fast.ToString("F1", c)} s ({SeekMath.Rate(covered, fast).ToString("F1", c)}x), "
                + $"{seekFrames} frames ({(fast > 0 ? seekFrames / fast : 0).ToString("F0", c)}/s), {seekDrawn} drawn, "
                + $"GC {GC.CollectionCount(0) - seekGc0} gen0 / {GC.CollectionCount(2) - seekGc2} gen2, turbo {(seekTurboAllowed ? "on" : "off")}, "
                + $"fixedDeltaTime {Time.fixedDeltaTime.ToString("0.####", c)}, maximumDeltaTime {Time.maximumDeltaTime.ToString("0.###", c)}"
                + SeekProfileText(fast));
            seekWatch = null; profOn = false;
        }

        // Leaving the replay, or a restart: drawing and sound come back.
        private void SeekAbort(bool keepStats)
        {
            SeekTurbo(false);
            profOn = false;
            if (!keepStats) seekWatch = null;
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
            if (!startSeeking || server == null) return;
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

        private void ForgetCachedDemo() { cachedDemo = null; cachedDemoKey = null; }

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
                    if (u is Missile m) { o["target"] = m.targetID.ToString(); o["owner"] = m.ownerID.ToString(); o["seeker"] = m.seekerMode.ToString(); }
                    units.Add(o);
                }
                var frame = hudOverlay?.LastFrame;
                var doc = new JObject
                {
                    ["time"] = replayTime, ["frame"] = Time.frameCount, ["units"] = units,
                    ["following"] = director?.CurrentUnit != null ? director.CurrentUnit.persistentID.ToString() : null,
                    ["hud_marks"] = frame?.Marks.Count, ["hud_lines"] = frame?.Lines.Count,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, doc.ToString());
            }
            catch (Exception e) { Logger.LogWarning("Unit dump: " + e); }
        }
    }
}
