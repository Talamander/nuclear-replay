using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReadmeShots
{
    // Test-only plugin for the README media. Never shipped (see ReadmeBurst.csproj).
    //
    // Start the game with -readmeBurst <commands file>. Each line appended to that file is one command:
    //   burst <out dir> <seconds> <fps> <width>
    // records the finished screen (game, viewer overlay and all) for <seconds> of real time at about <fps>, scaled
    // to <width> pixels wide on the GPU, as raw RGBA frames: <out dir>/frame-00000.rgba ... plus index.txt
    // ("<frame> <seconds since start> <width> <height>" per line) and done.txt (with "flip 0|1": 1 when the rows
    // come bottom-up). Frames are read back with AsyncGPUReadback, so the game keeps its frame rate.
    // The plugin writes <commands file>.burst-status ("idle", "recording N", "writing N", "done N").
    [BepInPlugin("talamander.nuclearreplay.readmeshots", "Nuclear Replay README shots (test only)", "0.1.0")]
    public sealed class ReadmeBurst : BaseUnityPlugin
    {
        private string commandsPath;
        private int commandsDone;
        private float nextPoll;
        private bool busy;

        private void Awake()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-readmeBurst") commandsPath = WinPath(args[i + 1]);
            if (commandsPath == null) { Logger.LogInfo("README burst: no -readmeBurst argument; idle."); return; }
            gameObject.hideFlags |= HideFlags.HideAndDontSave;
            Logger.LogInfo("README burst: watching " + commandsPath);
            Status("idle");
        }

        private static string WinPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (path.StartsWith("/") && Path.DirectorySeparatorChar == '\\') return "Z:" + path.Replace('/', '\\');
            return path;
        }

        private void Status(string text)
        {
            try { File.WriteAllText(commandsPath + ".burst-status", text); } catch (Exception) { }
        }

        private void Update()
        {
            if (commandsPath == null || busy || Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.2f;
            string[] lines;
            try
            {
                if (!File.Exists(commandsPath)) return;
                using (var s = new FileStream(commandsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(s)) lines = r.ReadToEnd().Split('\n');
            }
            catch (Exception) { return; }
            if (commandsDone >= lines.Length - 1) return;
            string line = lines[commandsDone++].Trim();
            var p = line.Split(' ');
            if (p.Length >= 5 && p[0] == "burst")
            {
                busy = true;
                StartCoroutine(Burst(WinPath(p[1]), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[4])));
            }
        }

        private IEnumerator Burst(string dir, float seconds, float fps, int width)
        {
            Directory.CreateDirectory(dir);
            int w = Math.Min(width, Screen.width), h = (int)Math.Round((double)Screen.height * w / Screen.width) & ~1;
            var full = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            full.Create();
            var small = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Bilinear };
            small.Create();
            var queue = new Queue<KeyValuePair<int, byte[]>>();
            var index = new List<string>();
            bool finished = false;
            int written = 0;
            var writer = new Thread(() =>
            {
                while (true)
                {
                    KeyValuePair<int, byte[]> item;
                    lock (queue)
                    {
                        while (queue.Count == 0 && !finished) Monitor.Wait(queue);
                        if (queue.Count == 0) return;
                        item = queue.Dequeue();
                    }
                    File.WriteAllBytes(Path.Combine(dir, $"frame-{item.Key:D5}.rgba"), item.Value);
                    Interlocked.Increment(ref written);
                }
            }) { IsBackground = true };
            writer.Start();

            var end = new WaitForEndOfFrame();
            float start = Time.realtimeSinceStartup, next = start;
            int n = 0, pending = 0, errors = 0;
            Logger.LogInfo($"README burst: {seconds} s at {fps} fps, {w}x{h}, to {dir}");
            while (Time.realtimeSinceStartup - start < seconds)
            {
                yield return end;
                float now = Time.realtimeSinceStartup;
                if (now < next) continue;
                next += 1f / fps;
                if (next < now) next = now + 1f / fps;   // fell behind: do not try to catch up
                ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
                Graphics.Blit(full, small);
                int frame = n++;
                float t = now - start;
                index.Add($"{frame} {t.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)} {w} {h}");
                pending++;
                AsyncGPUReadback.Request(small, 0, TextureFormat.RGBA32, request =>
                {
                    pending--;
                    if (request.hasError) { errors++; return; }
                    var bytes = request.GetData<byte>().ToArray();
                    lock (queue) { queue.Enqueue(new KeyValuePair<int, byte[]>(frame, bytes)); Monitor.Pulse(queue); }
                });
                Status("recording " + n);
            }
            AsyncGPUReadback.WaitAllRequests();
            while (pending > 0) yield return null;
            lock (queue) { finished = true; Monitor.Pulse(queue); }
            while (writer.IsAlive) { Status("writing " + written + " of " + n); yield return null; }
            File.WriteAllText(Path.Combine(dir, "index.txt"), string.Join("\n", index) + "\n");
            File.WriteAllText(Path.Combine(dir, "done.txt"),
                $"frames {n}\nerrors {errors}\nsize {w}x{h}\nflip {(SystemInfo.graphicsUVStartsAtTop ? 0 : 1)}\n");
            full.Release(); small.Release();
            Logger.LogInfo($"README burst: {n} frames, {errors} errors, in {dir}");
            Status("done " + n);
            busy = false;
        }
    }
}
