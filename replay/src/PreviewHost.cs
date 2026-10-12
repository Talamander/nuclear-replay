using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuclearReplay.Preview;
using NuclearOption.Networking;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace NuclearReplay.Playback
{
    // -ncPreview <job.json>: live preview and thumbnails for nr (docs/PRODUCT-CONTRACT.md section 4).
    //
    // mode "preview": connects to nr on 127.0.0.1:<port>, skips ahead to `from`, plays to `to` and
    //   streams small JPEG frames over the socket (PreviewWire). nr sends commands back: play, pause,
    //   shots (swap the camera live), range / seek (forward skips ahead in place; backward restarts the
    //   replay), restart, stream (frame rate) and quit. The game quits when nr goes away.
    // mode "thumbs": visits each requested replay time in order, writes one JPEG per item, writes
    //   progress.json like a render job and quits.
    //
    // Both modes keep the window small and, on Windows, off-screen; the game is muted.
    internal sealed class PreviewHost
    {
        internal sealed class Thumb
        {
            public string Id;
            public double T;
            public string Out;
            public string State = "pending";   // pending, done, skipped
        }

        private enum Phase { Starting, Loading, Seeking, Preroll, Ready, Playing, Paused, Ended, Restarting, Done, Error }

        private sealed class Grab
        {
            public Thumb Thumb;
            public double T;
            public string State;
        }

        public string Mode = "preview", Demo, Shots, ProgressPath;
        public int Width = 854, Height = 480, Fps = 20, Quality = 70;
        public bool Hud, Mute = true, OffScreen = true, Autoplay = true, SoftRestartAllowed = true;
        public bool? VFlip;
        public int Port;
        public string Token;
        public double From, To, Preroll = 1;
        public readonly List<Thumb> Thumbs = new List<Thumb>();

        // Flags the plugin reads each frame.
        public JArray LatestShots;
        public bool ShotsChanged, RestartRequested, QuitRequested;
        public bool IsPreview => Mode == "preview";
        public string Describe() => IsPreview
            ? $"preview {From:F1}-{To:F1}s at {Width}x{Height} {Fps} fps, port {Port}"
            : $"thumbs x{Thumbs.Count} at {Width}x{Height}";

        private readonly ManualLogSource log;
        private Phase phase = Phase.Starting;
        private bool wantPaused;
        private double now, end = -1, nextStatus, nextGrab, nextProgress, nextOffScreen;
        private int offScreenTries;
        private string lastState, error;
        private long seq;
        private volatile Grab pendingGrab;
        private volatile bool grabBusy;
        private RenderTexture full, small;
        private int fastForwardSavedVSync = -1, fastForwardSavedTarget;
        private bool fastForwarding;
        private volatile Thumb thumbInFlight;
        private int thumbsDone, thumbsSkipped;
        // Frame cost, averaged: main-thread time to capture, scale and queue the readback, and worker-thread
        // time to flip and encode the JPEG. Reported in status messages as grab_ms and encode_ms.
        private double grabMsAverage = -1, encodeMsAverage = -1, gameFrameMs = -1;
        private readonly System.Diagnostics.Stopwatch costClock = System.Diagnostics.Stopwatch.StartNew();

        // Socket state. One reader thread fills `commands`; one writer thread sends queued messages and the
        // newest frame (older unsent frames are dropped so a slow reader never stalls the game).
        private TcpClient client;
        private NetworkStream stream;
        private readonly object sync = new object();
        private readonly Queue<JObject> commands = new Queue<JObject>();
        private readonly Queue<string> outMessages = new Queue<string>();
        private string frameHeader;
        private byte[] framePayload;
        private volatile bool closed, lost, connected;
        private string lostReason;

        private PreviewHost(ManualLogSource log) { this.log = log; }

        public static PreviewHost Load(string path, ManualLogSource log, Func<string, string> hostPath)
        {
            var host = new PreviewHost(log);
            var root = JObject.Parse(File.ReadAllText(path));
            host.ProgressPath = hostPath((string)root["progress"]) ?? Path.ChangeExtension(path, ".progress.json");
            try { host.Parse(root, hostPath); }
            catch (Exception e) { host.Fail(e.Message); throw; }
            return host;
        }

        private void Parse(JObject root, Func<string, string> hostPath)
        {
            int version = (int?)root["version"] ?? 1;
            if (version != 1) throw new InvalidDataException("unsupported preview job version " + version);
            Mode = ((string)root["mode"] ?? "preview").ToLowerInvariant();
            if (Mode != "preview" && Mode != "thumbs") throw new InvalidDataException("mode must be preview or thumbs");
            Demo = hostPath((string)root["demo"]);
            if (Demo == null || !File.Exists(Demo)) throw new FileNotFoundException("demo not found: " + Demo);
            Shots = hostPath((string)root["shots"]);
            if (Shots != null && !File.Exists(Shots)) throw new FileNotFoundException("shot list not found: " + Shots);
            Width = (int?)root["width"] ?? (IsPreview ? 854 : 320);
            Height = (int?)root["height"] ?? (IsPreview ? 480 : 180);
            if (Width < 16 || Height < 16 || Width > 3840 || Height > 2160) throw new InvalidDataException($"bad size {Width}x{Height}");
            Fps = Mathf.Clamp((int?)root["fps"] ?? 20, 1, 30);
            Quality = Mathf.Clamp((int?)root["quality"] ?? 70, 10, 95);
            Hud = (bool?)root["hud"] ?? false;
            Mute = (bool?)root["mute"] ?? true;
            OffScreen = ((string)root["window"] ?? "offscreen") == "offscreen";
            if (root["vflip"] != null && root["vflip"].Type == JTokenType.Boolean) VFlip = (bool)root["vflip"];
            Preroll = Math.Max(0.2, (double?)root["preroll"] ?? 1);
            if (IsPreview)
            {
                var p = root["preview"] as JObject ?? throw new InvalidDataException("preview job has no preview section");
                Port = (int?)p["port"] ?? 0;
                Token = (string)p["token"];
                From = (double?)p["from"] ?? 0;
                To = (double?)p["to"] ?? -1;
                Autoplay = (bool?)p["autoplay"] ?? true;
                SoftRestartAllowed = (bool?)p["softRestart"] ?? true;
                if (Port < 1 || Port > 65535) throw new InvalidDataException("preview.port must be 1-65535");
                if (string.IsNullOrEmpty(Token)) throw new InvalidDataException("preview.token is missing");
                if (From < 0 || To <= From) throw new InvalidDataException("preview needs 0 <= from < to");
            }
            else
            {
                if (!(root["thumbs"] is JArray list) || list.Count == 0) throw new InvalidDataException("thumbs job has no thumbs");
                foreach (var item in list)
                {
                    var t = new Thumb { Id = (string)item["id"], T = (double?)item["t"] ?? -1, Out = hostPath((string)item["out"]) };
                    if (t.T < 0) throw new InvalidDataException($"thumb {t.Id}: t must be >= 0");
                    if (string.IsNullOrWhiteSpace(t.Out)) throw new InvalidDataException($"thumb {t.Id}: out is missing");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(t.Out)));
                    Thumbs.Add(t);
                }
                Thumbs.Sort((a, b) => a.T.CompareTo(b.T));
            }
        }

        // Called once from Awake after Load.
        public void Start()
        {
            Application.runInBackground = true;
            WriteProgress("loading", true);
            if (!IsPreview) return;
            var thread = new Thread(Connect) { IsBackground = true, Name = "nc-preview-socket" };
            thread.Start();
        }

        // ---- socket -------------------------------------------------------------------------------------

        private void Connect()
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!closed)
            {
                try
                {
                    client = new TcpClient();
                    client.NoDelay = true;
                    client.Connect("127.0.0.1", Port);
                    stream = client.GetStream();
                    break;
                }
                catch (Exception e)
                {
                    try { client?.Close(); } catch (Exception) { }
                    client = null;
                    if (DateTime.UtcNow > deadline) { MarkLost("could not reach nr on port " + Port + ": " + e.Message); return; }
                    Thread.Sleep(500);
                }
            }
            if (closed) return;
            var hello = new JObject
            {
                ["type"] = "hello", ["token"] = Token, ["mode"] = Mode, ["version"] = BuildVersion.Full,
                ["from"] = From, ["to"] = To, ["width"] = Width, ["height"] = Height
            };
            try { PreviewWire.Write(stream, PreviewWire.Message, hello.ToString(Formatting.None)); }
            catch (Exception e) { MarkLost("hello failed: " + e.Message); return; }
            connected = true;
            log.LogInfo("Preview: connected to nr on 127.0.0.1:" + Port);
            new Thread(WriteLoop) { IsBackground = true, Name = "nc-preview-writer" }.Start();
            ReadLoop();
        }

        private void ReadLoop()
        {
            try
            {
                while (!closed)
                {
                    if (!PreviewWire.TryRead(stream, out var kind, out var header, out _)) { MarkLost("nr closed the preview"); return; }
                    if (kind != PreviewWire.Message) continue;
                    JObject command;
                    try { command = JObject.Parse(header); }
                    catch (Exception) { log.LogWarning("Preview: ignored a message that is not JSON"); continue; }
                    lock (sync) commands.Enqueue(command);
                }
            }
            catch (Exception e) { MarkLost("preview connection failed: " + e.Message); }
        }

        private void WriteLoop()
        {
            try
            {
                while (!closed)
                {
                    string message = null, header = null;
                    byte[] payload = null;
                    lock (sync)
                    {
                        while (!closed && outMessages.Count == 0 && frameHeader == null) Monitor.Wait(sync, 500);
                        if (closed) return;
                        if (outMessages.Count > 0) message = outMessages.Dequeue();
                        else { header = frameHeader; payload = framePayload; frameHeader = null; framePayload = null; }
                    }
                    if (message != null) PreviewWire.Write(stream, PreviewWire.Message, message);
                    else if (header != null) PreviewWire.Write(stream, PreviewWire.Frame, header, payload);
                }
            }
            catch (Exception e) { MarkLost("preview send failed: " + e.Message); }
        }

        private void MarkLost(string reason)
        {
            if (closed || lost) return;
            lostReason = reason;
            lost = true;
            log.LogWarning("Preview: " + reason + "; the game will quit");
            lock (sync) Monitor.PulseAll(sync);
        }

        private void Send(JObject message)
        {
            if (!connected) return;
            lock (sync)
            {
                if (outMessages.Count > 200) outMessages.Dequeue();
                outMessages.Enqueue(message.ToString(Formatting.None));
                Monitor.PulseAll(sync);
            }
        }

        private void SendFrame(string header, byte[] jpeg)
        {
            if (!connected) return;
            lock (sync)
            {
                frameHeader = header;
                framePayload = jpeg;
                Monitor.PulseAll(sync);
            }
        }

        public void Close()
        {
            if (!closed && connected && !lost) log.LogInfo("Preview: closing the connection to nr");
            closed = true;
            lock (sync) Monitor.PulseAll(sync);
            try { client?.Close(); } catch (Exception) { }
        }

        // ---- commands -----------------------------------------------------------------------------------

        // Main thread, every Update. hasServer says whether a replay is running right now.
        public void Tick(bool hasServer)
        {
            if (Mute && AudioListener.volume != 0f) AudioListener.volume = 0f;
            gameFrameMs = Average(gameFrameMs, Time.unscaledDeltaTime * 1000.0);
            double wall = Time.realtimeSinceStartupAsDouble;
            if (OffScreen && offScreenTries < 30 && wall >= nextOffScreen)
            {
                nextOffScreen = wall + 2;
                offScreenTries++;
                WindowMover.MoveOffScreen(log, offScreenTries == 1);
            }
            if (!hasServer && (phase == Phase.Starting || phase == Phase.Loading) && wall > 900)
            {
                Fail("the game did not reach the replay within 900 s");
                QuitRequested = true;
            }
            if (lost && IsPreview) QuitRequested = true;
            if (!hasServer && phase != Phase.Restarting) SendStatusIfDue(false, wall);
            List<JObject> batch;
            lock (sync) { if (commands.Count == 0) return; batch = commands.ToList(); commands.Clear(); }
            foreach (var command in batch) Apply(command, hasServer);
        }

        private void Apply(JObject command, bool hasServer)
        {
            string type = ((string)command["type"] ?? "").ToLowerInvariant();
            log.LogInfo("Preview command: " + command.ToString(Formatting.None));
            switch (type)
            {
                case "play":
                    wantPaused = false;
                    if (phase == Phase.Ended)
                    {
                        // Past the end: extend nothing, start the range again.
                        if (now < To - 0.05) phase = Phase.Playing;
                        else RequestRestart("play after the end");
                    }
                    else if (phase == Phase.Ready || phase == Phase.Paused) phase = Phase.Playing;
                    break;
                case "pause":
                    wantPaused = true;
                    if (phase == Phase.Playing) phase = Phase.Paused;
                    break;
                case "shots":
                    if (command["shots"] is JArray shots) { LatestShots = shots; ShotsChanged = true; }
                    else Reply("error", "shots needs a shots array");
                    break;
                case "seek":
                {
                    double t = (double?)command["t"] ?? -1;
                    if (t < 0) { Reply("error", "seek needs t >= 0"); break; }
                    double length = To - From;
                    SetRange(t, t >= To ? t + length : To, hasServer);
                    break;
                }
                case "range":
                {
                    double from = (double?)command["from"] ?? From, to = (double?)command["to"] ?? To;
                    if (from < 0 || to <= from) { Reply("error", "range needs 0 <= from < to"); break; }
                    SetRange(from, to, hasServer);
                    break;
                }
                case "restart":
                    if (command["from"] != null) { double f = (double)command["from"]; if (f >= 0 && f < To) From = f; }
                    if (command["to"] != null) { double t2 = (double)command["to"]; if (t2 > From) To = t2; }
                    RequestRestart("restart command");
                    break;
                case "stream":
                    Fps = Mathf.Clamp((int?)command["fps"] ?? Fps, 1, 30);
                    break;
                case "quit":
                    QuitRequested = true;
                    break;
                default:
                    Reply("error", "unknown command " + type);
                    break;
            }
            nextStatus = 0;
        }

        // Forward moves skip ahead in the running replay; moving the start back before the replay clock
        // needs a restart because the replay only runs forward.
        private void SetRange(double from, double to, bool hasServer)
        {
            bool backward = hasServer && phase >= Phase.Seeking && from < now - 0.25;
            From = from;
            To = to;
            if (!hasServer || phase < Phase.Seeking) return;
            if (backward) { RequestRestart("range moved back"); return; }
            if (from > now + 0.05) { phase = Phase.Seeking; return; }
            if (phase == Phase.Ended && now < To) phase = wantPaused ? Phase.Paused : Phase.Playing;
        }

        private void RequestRestart(string why)
        {
            if (!SoftRestartAllowed)
            {
                Reply("restart_needed", "this preview cannot go back in time; start a new preview (" + why + ")");
                return;
            }
            log.LogInfo("Preview: restart requested (" + why + ")");
            RestartRequested = true;
        }

        private void Reply(string type, string message) => Send(new JObject { ["type"] = type, ["message"] = message });

        public void OnRestarting()
        {
            phase = Phase.Restarting;
            end = -1;
            SendStatus("restarting", Time.realtimeSinceStartupAsDouble, "Going back: reloading the match");
        }

        public void OnServerStarted()
        {
            phase = Phase.Loading;
            nextStatus = 0;
        }

        // ---- replay pacing ------------------------------------------------------------------------------

        // Main thread, LateUpdate, before the replay advances: decides speed and pause for this frame.
        public void Drive(ReplayServer server, double replayNow)
        {
            now = replayNow;
            end = server.EndTime - server.StartTime;
            if (phase == Phase.Starting || phase == Phase.Loading || phase == Phase.Restarting) phase = Phase.Seeking;
            if (!IsPreview) { DriveThumbs(server); return; }
            switch (phase)
            {
                case Phase.Seeking:
                    if (now < From - Preroll && !server.Finished) { FastForward(server); return; }
                    EndFastForward(server);
                    phase = Phase.Preroll;
                    goto case Phase.Preroll;
                case Phase.Preroll:
                    server.Paused = false;
                    server.Speed = 1f;
                    if (now >= From || server.Finished)
                        phase = server.Finished ? Phase.Ended : !Autoplay || wantPaused ? Phase.Ready : Phase.Playing;
                    break;
                case Phase.Playing:
                    server.Paused = false;
                    server.Speed = 1f;
                    if (now >= To || server.Finished) { phase = Phase.Ended; server.Paused = true; }
                    break;
                case Phase.Ready:
                case Phase.Paused:
                case Phase.Ended:
                case Phase.Done:
                case Phase.Error:
                    server.Paused = true;
                    break;
            }
        }

        private void DriveThumbs(ReplayServer server)
        {
            if (phase == Phase.Done || phase == Phase.Error) { server.Paused = true; return; }
            if (thumbInFlight != null) { server.Paused = true; return; }
            var item = Thumbs.FirstOrDefault(t => t.State == "pending");
            if (item == null) { FinishThumbs(); server.Paused = true; return; }
            if (server.Finished && now < item.T - 0.5)
            {
                foreach (var t in Thumbs.Where(t => t.State == "pending" && t.T > now)) { t.State = "skipped"; thumbsSkipped++; }
                log.LogWarning($"Preview: the demo ends at {end:F1} s; skipped {thumbsSkipped} thumbnails after it");
                return;
            }
            if (now < item.T - Preroll && !server.Finished) { phase = Phase.Seeking; FastForward(server); return; }
            EndFastForward(server);
            phase = Phase.Preroll;
            server.Paused = false;
            server.Speed = 1f;
        }

        private void FastForward(ReplayServer server)
        {
            if (!fastForwarding)
            {
                fastForwarding = true;
                fastForwardSavedVSync = QualitySettings.vSyncCount;
                fastForwardSavedTarget = Application.targetFrameRate;
            }
            server.Paused = false;
            server.Speed = 8f;
            Time.captureFramerate = 60;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
        }

        private void EndFastForward(ReplayServer server)
        {
            if (!fastForwarding) return;
            fastForwarding = false;
            server.Speed = 1f;
            Time.captureFramerate = 0;
            if (fastForwardSavedVSync >= 0) { QualitySettings.vSyncCount = fastForwardSavedVSync; Application.targetFrameRate = fastForwardSavedTarget; }
        }

        // Main thread, LateUpdate, after the replay advanced and the director moved the camera.
        public void AfterFrame(double replayTime, bool live)
        {
            now = replayTime;
            double wall = Time.realtimeSinceStartupAsDouble;
            if (!IsPreview)
            {
                if (live && thumbInFlight == null && phase == Phase.Preroll)
                {
                    var item = Thumbs.FirstOrDefault(t => t.State == "pending");
                    if (item != null && replayTime >= item.T && !grabBusy && pendingGrab == null)
                    {
                        thumbInFlight = item;
                        pendingGrab = new Grab { Thumb = item, T = replayTime };
                    }
                }
                if (wall >= nextProgress) WriteProgress(live ? (phase == Phase.Seeking ? "seeking" : "rendering") : "loading", false);
                return;
            }
            SendStatusIfDue(live, wall);
            if (!live || grabBusy || pendingGrab != null || !connected) return;
            double interval = phase == Phase.Playing || phase == Phase.Preroll ? 1.0 / Fps : 0.5;
            if (wall < nextGrab) return;
            // Step from the scheduled time, not from now, so frame-boundary overshoot does not lower the rate
            // (at 60 fps a 20 fps stream otherwise drops to about 17); after a stall, start again from now.
            nextGrab = wall - nextGrab < interval ? nextGrab + interval : wall + interval;
            pendingGrab = new Grab { T = replayTime, State = StateName(true) };
        }

        private string StateName(bool live)
        {
            switch (phase)
            {
                case Phase.Starting: return "starting";
                case Phase.Loading: return "loading";
                case Phase.Restarting: return "restarting";
                case Phase.Seeking:
                case Phase.Preroll: return live ? "seeking" : "loading";
                case Phase.Ready: return "ready";
                case Phase.Playing: return "playing";
                case Phase.Paused: return "paused";
                case Phase.Ended: return "ended";
                case Phase.Error: return "error";
                default: return "done";
            }
        }

        private void SendStatusIfDue(bool live, double wall)
        {
            string state = StateName(live);
            if (state == lastState && wall < nextStatus) return;
            SendStatus(state, wall, null);
        }

        private void SendStatus(string state, double wall, string message)
        {
            lastState = state;
            nextStatus = wall + 0.5;
            var status = new JObject
            {
                ["type"] = "status", ["state"] = state, ["t"] = Math.Round(now, 3), ["from"] = From, ["to"] = To,
                ["paused"] = wantPaused, ["fps"] = Fps
            };
            if (end >= 0) status["end"] = Math.Round(end, 3);
            if (grabMsAverage >= 0) status["grab_ms"] = Math.Round(grabMsAverage, 2);
            if (encodeMsAverage >= 0) status["encode_ms"] = Math.Round(encodeMsAverage, 2);
            if (gameFrameMs > 0) status["game_fps"] = Math.Round(1000.0 / gameFrameMs, 1);
            if (state == "seeking" && From - Preroll > 0) status["fraction"] = Math.Round(Mathf.Clamp01((float)(now / (From - Preroll))), 3);
            if (message != null) status["message"] = message;
            if (error != null) status["error"] = error;
            Send(status);
        }

        // ---- frames -------------------------------------------------------------------------------------

        // Coroutine started by the plugin: grabs the finished frame, scales it on the GPU, reads it back
        // asynchronously and encodes the JPEG on a worker thread.
        public IEnumerator GrabLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                var grab = pendingGrab;
                if (grab == null) continue;
                pendingGrab = null;
                try
                {
                    int sw = Screen.width, sh = Screen.height;
                    if (full == null || full.width != sw || full.height != sh)
                    {
                        if (full != null) full.Release();
                        full = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32);
                        full.Create();
                    }
                    // Keep the screen's aspect ratio inside the requested box.
                    int w = Width, h = Height;
                    if (sw > 0 && sh > 0)
                    {
                        double scale = Math.Min((double)Width / sw, (double)Height / sh);
                        w = Math.Max(2, (int)Math.Round(sw * scale) & ~1);
                        h = Math.Max(2, (int)Math.Round(sh * scale) & ~1);
                    }
                    if (small == null || small.width != w || small.height != h)
                    {
                        if (small != null) small.Release();
                        small = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                        small.Create();
                    }
                    double grabStart = costClock.Elapsed.TotalMilliseconds;
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
                    Graphics.Blit(full, small);
                    grabBusy = true;
                    UnityEngine.Rendering.AsyncGPUReadback.Request(small, 0, TextureFormat.RGBA32, request =>
                    {
                        if (request.hasError) { grabBusy = false; GrabFailed(grab, "GPU readback failed"); return; }
                        var pixels = request.GetData<byte>().ToArray();
                        ThreadPool.QueueUserWorkItem(_ => Encode(grab, pixels, w, h));
                    });
                    grabMsAverage = Average(grabMsAverage, costClock.Elapsed.TotalMilliseconds - grabStart);
                }
                catch (Exception e)
                {
                    grabBusy = false;
                    log.LogError("Preview frame grab failed: " + e);
                    GrabFailed(grab, e.Message);
                }
            }
        }

        private void GrabFailed(Grab grab, string why)
        {
            if (grab.Thumb == null) return;
            grab.Thumb.State = "skipped";
            thumbsSkipped++;
            thumbInFlight = null;
            log.LogWarning("Preview: thumbnail " + grab.Thumb.Id + " failed: " + why);
        }

        private void Encode(Grab grab, byte[] pixels, int w, int h)
        {
            try
            {
                // EncodeArrayToJPG expects Unity texture order (bottom row first). The readback is top row
                // first where graphicsUVStartsAtTop (D3D, DXVK under Proton); the job's vflip overrides this.
                double encodeStart = costClock.Elapsed.TotalMilliseconds;
                bool flip = VFlip ?? SystemInfo.graphicsUVStartsAtTop;
                if (flip) FlipRows(pixels, w * 4, h);
                var jpeg = ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, Quality);
                encodeMsAverage = Average(encodeMsAverage, costClock.Elapsed.TotalMilliseconds - encodeStart);
                if (grab.Thumb != null)
                {
                    string part = grab.Thumb.Out + ".part";
                    File.WriteAllBytes(part, jpeg);
                    if (File.Exists(grab.Thumb.Out)) File.Delete(grab.Thumb.Out);
                    File.Move(part, grab.Thumb.Out);
                    grab.Thumb.State = "done";
                    Interlocked.Increment(ref thumbsDone);
                    thumbInFlight = null;
                    return;
                }
                var header = "{\"type\":\"frame\",\"t\":" + grab.T.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"seq\":" + Interlocked.Increment(ref seq) + ",\"w\":" + w + ",\"h\":" + h + ",\"state\":" + PreviewWire.Quote(grab.State) + "}";
                SendFrame(header, jpeg);
            }
            catch (Exception e)
            {
                log.LogError("Preview frame encode failed: " + e);
                GrabFailed(grab, e.Message);
            }
            finally { grabBusy = false; }
        }

        private static double Average(double average, double sample) => average < 0 ? sample : average * 0.9 + sample * 0.1;

        private static void FlipRows(byte[] pixels, int stride, int rows)
        {
            var line = new byte[stride];
            for (int top = 0, bottom = rows - 1; top < bottom; top++, bottom--)
            {
                Buffer.BlockCopy(pixels, top * stride, line, 0, stride);
                Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
                Buffer.BlockCopy(line, 0, pixels, bottom * stride, stride);
            }
        }

        // ---- progress -----------------------------------------------------------------------------------

        private void FinishThumbs()
        {
            if (phase == Phase.Done) return;
            phase = Phase.Done;
            if (thumbsDone == 0) { Fail($"no thumbnails were written ({thumbsSkipped} skipped)"); QuitRequested = true; return; }
            WriteProgress("done", true);
            log.LogInfo($"Preview: wrote {thumbsDone} thumbnails, skipped {thumbsSkipped}");
            QuitRequested = true;
        }

        public void Fail(string message)
        {
            if (phase == Phase.Error) return;
            phase = Phase.Error;
            error = message;
            log.LogError("Preview failed: " + message);
            WriteProgress("error", true);
            SendStatus("error", Time.realtimeSinceStartupAsDouble, message);
        }

        // Same file format as render-job progress (state, range, ranges, frame, frames, error), so nr's
        // ProgressWatcher reads it; frame/frames count thumbnails. "skipped" counts thumbnails past the end.
        private void WriteProgress(string state, bool force)
        {
            if (ProgressPath == null) return;
            double wall = Time.realtimeSinceStartupAsDouble;
            if (!force && wall < nextProgress) return;
            nextProgress = wall + 0.5;
            var o = new JObject
            {
                ["state"] = state, ["range"] = 0, ["ranges"] = 1, ["frame"] = thumbsDone,
                ["frames"] = IsPreview ? 0 : Thumbs.Count, ["skipped"] = thumbsSkipped, ["error"] = error,
                ["mode"] = Mode
            };
            string tmp = ProgressPath + ".tmp";
            try
            {
                File.WriteAllText(tmp, o.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
                if (File.Exists(ProgressPath))
                {
                    try { File.Replace(tmp, ProgressPath, null); }
                    catch (Exception) { File.Delete(ProgressPath); File.Move(tmp, ProgressPath); }
                }
                else File.Move(tmp, ProgressPath);
            }
            catch (Exception e) { log.LogWarning("Preview: progress write failed: " + e.Message); }
        }
    }

    // Moves the game window off-screen without activating it (Windows API; Wine provides it under
    // Proton). Best effort: a Wayland compositor may ignore window positions.
    internal static class WindowMover
    {
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private const uint NoSize = 0x0001, NoZOrder = 0x0004, NoActivate = 0x0010;

        public static void MoveOffScreen(ManualLogSource log, bool first)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer) return;
            try
            {
                uint self;
                using (var game = Process.GetCurrentProcess()) self = (uint)game.Id;
                int moved = 0;
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var pid);
                    if (pid == self && IsWindowVisible(window) && SetWindowPos(window, IntPtr.Zero, -32000, -32000, 0, 0, NoSize | NoZOrder | NoActivate)) moved++;
                    return true;
                }, IntPtr.Zero);
                if (first) log.LogInfo($"Preview: moved {moved} game window(s) off-screen");
            }
            catch (Exception e) { if (first) log.LogWarning("Preview: could not move the window off-screen: " + e.Message); }
        }
    }
}
