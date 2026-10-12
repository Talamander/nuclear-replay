using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NuclearReplay.Core.Game;
using NuclearReplay.Preview;

namespace NuclearReplay.Core.Preview;

/// <summary>What the simulated game is showing when it draws a frame.</summary>
public sealed record SimulatedFrame(double T, int Width, int Height, string Camera, string Target, string State);

/// <summary>
/// A stand-in for Nuclear Option with the replay plugin, speaking the real preview protocol over a real socket.
/// Used by tests and by <c>nr preview --simulate</c> / <c>nr thumbs --simulate</c>, so
/// scripts can be built and checked without starting the game. Its frames are labelled "SIMULATED PREVIEW" and are
/// never game footage.
/// </summary>
public sealed class SimulatedGame
{
    /// <summary>Time multiplier: 1 is real time, 20 runs boot, loading and playback twenty times faster.</summary>
    public double Speed { get; init; } = 1;
    public double BootSeconds { get; init; } = 3;
    public double LoadSeconds { get; init; } = 8;
    /// <summary>Replay seconds per second while skipping ahead (the plugin uses 8x).</summary>
    public double SeekRate { get; init; } = 8;
    /// <summary>Length of the simulated recording.</summary>
    public double DemoLength { get; init; } = 3600;
    /// <summary>Draws a frame; the default is the embedded "SIMULATED PREVIEW" picture.</summary>
    public Func<SimulatedFrame, byte[]>? Draw { get; init; }
    /// <summary>When set, loading fails with this message (error path tests and screenshots).</summary>
    public string? FailWhileLoading { get; init; }
    /// <summary>When true, the simulator never connects (start-up timeout tests).</summary>
    public bool NeverConnect { get; init; }

    private volatile bool running, killed;
    private readonly ConcurrentQueue<JsonObject> inbox = new();
    public bool Running => running;
    public int Launches { get; private set; }
    public List<string> Commands { get; } = new();
    public string? LastJobFile { get; private set; }
    public List<string> LastArguments { get; private set; } = new();

    private static readonly Lazy<byte[]> Placeholder = new(() =>
    {
        using var stream = typeof(SimulatedGame).Assembly.GetManifestResourceStream("NuclearReplay.Core.simulated-preview.jpg")!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    /// <summary>The embedded "SIMULATED PREVIEW" JPEG.</summary>
    public static byte[] PlaceholderImage => Placeholder.Value;

    /// <summary>
    /// A stand-in game folder for the simulator when Nuclear Option is not installed (the launcher checks that the exe
    /// exists). The "exe" is a short text file; nothing ever runs it.
    /// </summary>
    public static GameInstall StubInstall(string dir)
    {
        var install = new GameInstall { GameDir = dir, Source = "simulated" };
        Directory.CreateDirectory(dir);
        if (!File.Exists(install.ExePath)) File.WriteAllText(install.ExePath, "Nuclear Replay simulated game: not a program.");
        return install;
    }

    /// <summary>A launcher that "starts the game" by starting this simulator.</summary>
    public GameLauncher CreateLauncher() => new(() => running, () => true, plan => { Start(plan); return null; }) { Wrapper = null };

    /// <summary>Stops the simulator like closing the game process.</summary>
    public int Kill()
    {
        if (!running) return 0;
        killed = true;
        running = false;
        return 1;
    }

    public void Start(LaunchPlan plan)
    {
        LastArguments = plan.Arguments.ToList();
        int at = plan.Arguments.IndexOf("-ncPreview");
        if (at < 0 || at + 1 >= plan.Arguments.Count) throw new InvalidOperationException("the simulated game only runs previews and thumbnails");
        var jobFile = HostPath(plan.Arguments[at + 1]);
        LastJobFile = jobFile;
        Launches++;
        running = true;
        killed = false;
        new Thread(() =>
        {
            try { Run(jobFile); }
            catch (Exception e) { Log.Warn("Simulated game: " + e.Message); }
            finally { running = false; }
        }) { IsBackground = true, Name = "nc-simulated-game" }.Start();
    }

    private static string HostPath(string gamePath) => new PathMapper(OperatingSystem.IsLinux() && gamePath.Length > 1 && gamePath[1] == ':').ToHostPath(gamePath);

    private bool Sleep(double seconds)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(0, seconds / Speed));
        while (DateTime.UtcNow < end)
        {
            if (killed) return false;
            Thread.Sleep(10);
        }
        return !killed;
    }

    private void Run(string jobFile)
    {
        var job = PreviewJobFile.FromJson(File.ReadAllText(jobFile)) ?? throw new InvalidDataException("unreadable job");
        var progress = HostPath(job.Progress);
        if (!Sleep(BootSeconds)) return;
        if (job.Mode == "thumbs") { RunThumbs(job, progress); return; }
        if (NeverConnect) { while (Sleep(0.5)) { } return; }
        var section = job.Preview!;
        using var client = new TcpClient();
        client.Connect("127.0.0.1", section.Port);
        var stream = client.GetStream();
        var writeLock = new object();
        void Send(JsonObject o) { lock (writeLock) PreviewWire.Write(stream, PreviewWire.Message, o.ToJsonString()); }
        void SendFrame(JsonObject header, byte[] image) { lock (writeLock) PreviewWire.Write(stream, PreviewWire.Frame, header.ToJsonString(), image); }
        Send(new JsonObject { ["type"] = "hello", ["token"] = section.Token, ["mode"] = "preview", ["version"] = "simulated" });
        bool closed = false;
        new Thread(() =>
        {
            try
            {
                while (PreviewWire.TryRead(stream, out _, out var header, out _))
                    if (JsonNode.Parse(header) is JsonObject o) inbox.Enqueue(o);
            }
            catch (Exception) { }
            closed = true;
        }) { IsBackground = true }.Start();

        var shots = ReadShots(job.Shots != null ? HostPath(job.Shots) : null);
        double from = section.From, to = section.To, t = 0;
        int fps = job.Fps;
        string state = "starting";
        bool wantPaused = !section.Autoplay;
        double loadLeft = LoadSeconds;
        long seq = 0;
        double nextStatus = 0, nextFrame = 0, wall = 0;
        const double step = 0.02;
        while (!killed && !closed)
        {
            // Commands.
            while (inbox.TryDequeue(out var c))
            {
                string type = (string?)c["type"] ?? "";
                lock (Commands) Commands.Add(c.ToJsonString());
                switch (type)
                {
                    case "play": wantPaused = false; if (state is "ready" or "paused") state = "playing"; else if (state == "ended") { if (t < to - 0.05) state = "playing"; else { state = "restarting"; loadLeft = LoadSeconds / 2; t = 0; } } break;
                    case "pause": wantPaused = true; if (state == "playing") state = "paused"; break;
                    case "shots": shots = ParseShots(c["shots"] as JsonArray); break;
                    case "stream": fps = Math.Clamp((int?)c["fps"] ?? fps, 1, 30); break;
                    case "quit": killed = true; break;
                    case "range":
                    case "seek":
                    {
                        double newFrom = type == "seek" ? (double?)c["t"] ?? from : (double?)c["from"] ?? from;
                        double newTo = type == "seek" ? (newFrom >= to ? newFrom + (to - from) : to) : (double?)c["to"] ?? to;
                        bool backward = newFrom < t - 0.25 && state is not ("starting" or "loading");
                        from = newFrom; to = newTo;
                        if (backward) { state = "restarting"; loadLeft = LoadSeconds / 2; t = 0; }
                        else if (from > t + 0.05 && state is not ("starting" or "loading" or "restarting")) state = "seeking";
                        else if (state == "ended" && t < to) state = wantPaused ? "paused" : "playing";
                        break;
                    }
                    case "restart":
                        if (c["from"] != null) from = (double)c["from"]!;
                        if (c["to"] != null) to = (double)c["to"]!;
                        state = "restarting"; loadLeft = LoadSeconds / 2; t = 0;
                        break;
                }
            }
            if (killed) break;
            // Simulation step.
            switch (state)
            {
                case "starting":
                    if (wall > 0.5) state = "loading";
                    break;
                case "loading":
                case "restarting":
                    loadLeft -= step;
                    if (loadLeft <= 0)
                    {
                        if (FailWhileLoading != null)
                        {
                            File.WriteAllText(progress, new JsonObject { ["state"] = "error", ["error"] = FailWhileLoading, ["range"] = 0, ["ranges"] = 1, ["frame"] = 0, ["frames"] = 0 }.ToJsonString());
                            Send(new JsonObject { ["type"] = "status", ["state"] = "error", ["error"] = FailWhileLoading, ["t"] = t, ["from"] = from, ["to"] = to });
                            Sleep(0.2);
                            return;
                        }
                        state = "seeking";
                    }
                    break;
                case "seeking":
                    t += SeekRate * step;
                    if (t >= from - 1) { t = Math.Max(t, from - 1); state = "preroll"; }
                    break;
                case "preroll":
                    t += step;
                    if (t >= from) { t = from; state = wantPaused ? "ready" : "playing"; }
                    break;
                case "playing":
                    t += step;
                    if (t >= to || t >= DemoLength) { t = Math.Min(t, to); state = "ended"; }
                    break;
            }
            string wire = state == "preroll" ? "seeking" : state;
            bool live = state is not ("starting" or "loading" or "restarting");
            if (wall >= nextStatus)
            {
                nextStatus = wall + 0.5;
                var s = new JsonObject { ["type"] = "status", ["state"] = wire, ["t"] = Math.Round(t, 3), ["from"] = from, ["to"] = to, ["paused"] = wantPaused, ["fps"] = fps };
                if (live) s["end"] = DemoLength;
                if (wire == "seeking" && from > 1) s["fraction"] = Math.Round(Math.Clamp(t / (from - 1), 0, 1), 3);
                Send(s);
            }
            double interval = state is "playing" or "preroll" ? 1.0 / fps : 0.5;
            if (live && wall >= nextFrame)
            {
                nextFrame = wall + interval;
                var (camera, target) = ShotAt(shots, t);
                var image = Draw?.Invoke(new SimulatedFrame(t, job.Width, job.Height, camera, target, wire)) ?? PlaceholderImage;
                SendFrame(new JsonObject { ["type"] = "frame", ["t"] = Math.Round(t, 3), ["seq"] = ++seq, ["w"] = job.Width, ["h"] = job.Height, ["state"] = wire }, image);
            }
            if (!Sleep(step)) break;
            wall += step;
        }
    }

    private void RunThumbs(PreviewJobFile job, string progress)
    {
        if (!Sleep(LoadSeconds)) return;
        var shots = ReadShots(job.Shots != null ? HostPath(job.Shots) : null);
        int done = 0, skipped = 0, total = job.Thumbs?.Count ?? 0;
        foreach (var item in job.Thumbs ?? new())
        {
            if (killed) return;
            if (item.T > DemoLength) { skipped++; continue; }
            var (camera, target) = ShotAt(shots, item.T);
            var image = Draw?.Invoke(new SimulatedFrame(item.T, job.Width, job.Height, camera, target, "thumb")) ?? PlaceholderImage;
            var path = HostPath(item.Out);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, image);
            done++;
            Json.WriteFileAtomic(progress, new JsonObject { ["state"] = "rendering", ["range"] = 0, ["ranges"] = 1, ["frame"] = done, ["frames"] = total, ["skipped"] = skipped, ["mode"] = "thumbs" }.ToJsonString());
            if (!Sleep(0.3)) return;
        }
        Json.WriteFileAtomic(progress, new JsonObject { ["state"] = done > 0 ? "done" : "error", ["error"] = done > 0 ? null : "no thumbnails were written", ["range"] = 0, ["ranges"] = 1, ["frame"] = done, ["frames"] = total, ["skipped"] = skipped, ["mode"] = "thumbs" }.ToJsonString());
    }

    private static List<(double T, string Camera, string Target)> ReadShots(string? path) =>
        path != null && File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root ? ParseShots(root["shots"] as JsonArray) : new();

    private static List<(double T, string Camera, string Target)> ParseShots(JsonArray? list) =>
        (list ?? new JsonArray()).OfType<JsonObject>()
            .Select(s => ((double?)s["t"] ?? 0, (string?)s["camera"] ?? "orbit", (string?)s["target"] ?? "host"))
            .OrderBy(s => s.Item1).ToList();

    private static (string Camera, string Target) ShotAt(List<(double T, string Camera, string Target)> shots, double t)
    {
        var current = shots.LastOrDefault(s => s.T <= t);
        if (current.Camera == null) current = shots.FirstOrDefault();
        return current.Camera == null ? ("orbit", "auto") : (current.Camera, current.Target);
    }
}
