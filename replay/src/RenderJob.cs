using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuclearReplay.Shared;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // -ncRenderJob <job.json>: renders every range of a job in one game run (docs/PRODUCT-CONTRACT.md §2).
    // Frames go from the async GPU readback straight into an ffmpeg process this class starts, through its
    // stdin, so no shell, FIFO or gamescope is needed; this works on native Windows and under Proton.
    // Each clip is written as <out>.part and renamed when ffmpeg has finished it.
    internal sealed class RenderJob
    {
        internal sealed class Range
        {
            public int Index;          // position in the job file (progress "range")
            public double From, To;
            public int Fps;
            public string Out;
            public int Frames => Math.Max(1, (int)Math.Round((To - From) * Fps));
        }

        private sealed class Encoder
        {
            public Range Range;
            public Process Process;
            public string Part;
            public readonly Queue<string> Stderr = new Queue<string>();
            public Thread Waiter;
            public volatile bool Finished;
            public volatile string Error;
        }

        public string Demo, Shots, Ffmpeg, EncoderSetting, ProgressPath;
        public int Width, Height, Quality;
        public bool Hud;
        public bool? VFlip;
        public readonly List<Range> Ranges = new List<Range>();

        private readonly ManualLogSource log;
        private readonly List<Encoder> encoders = new List<Encoder>();
        private Thread probe;
        private string codec;                    // h264_nvenc, h264_vulkan or libx264 once known
        private string state = "loading", error;
        private int range, frame, frames;
        private double nextWrite;
        public bool Finished => state == "done" || state == "error";
        public string State => state;

        private RenderJob(ManualLogSource log) { this.log = log; }

        // Loads and checks the job. A job that cannot be read throws; the caller reports it.
        public static RenderJob Load(string path, ManualLogSource log, Func<string, string> hostPath)
        {
            var job = new RenderJob(log);
            var root = JObject.Parse(File.ReadAllText(path));
            // The progress path comes first so every later problem can be reported through it.
            job.ProgressPath = hostPath((string)root["progress"]) ?? Path.ChangeExtension(path, ".progress.json");
            try { job.Parse(root, hostPath); }
            catch (Exception e) { job.Fail(e.Message); throw; }
            return job;
        }

        private void Parse(JObject root, Func<string, string> hostPath)
        {
            int version = (int?)root["version"] ?? 1;
            if (version != 1) throw new InvalidDataException("unsupported job version " + version);
            Demo = hostPath((string)root["demo"]);
            if (Demo == null || !File.Exists(Demo)) throw new FileNotFoundException("demo not found: " + Demo);
            Shots = hostPath((string)root["shots"]);
            if (Shots != null && !File.Exists(Shots)) throw new FileNotFoundException("shot list not found: " + Shots);
            Width = (int?)root["width"] ?? 1920;
            Height = (int?)root["height"] ?? 1080;
            if (Width < 16 || Height < 16 || Width % 2 != 0 || Height % 2 != 0) throw new InvalidDataException($"bad size {Width}x{Height} (even numbers required)");
            Hud = (bool?)root["hud"] ?? false;
            // No ffmpeg in the job: the copy downloaded into the game, else ffmpeg on PATH.
            Ffmpeg = hostPath((string)root["ffmpeg"]) ?? SharedFfmpeg() ?? "ffmpeg";
            if ((Ffmpeg.Contains("\\") || Ffmpeg.Contains("/")) && !File.Exists(Ffmpeg)) throw new FileNotFoundException("ffmpeg not found: " + Ffmpeg);
            EncoderSetting = ((string)root["encoder"] ?? "auto").ToLowerInvariant();
            if (EncoderSetting != "auto" && EncoderSetting != "nvenc" && EncoderSetting != "vulkan" && EncoderSetting != "x264") throw new InvalidDataException("encoder must be auto, nvenc, vulkan or x264");
            Quality = Mathf.Clamp((int?)root["quality"] ?? 19, 0, 51);
            if (root["vflip"] != null && root["vflip"].Type == JTokenType.Boolean) VFlip = (bool)root["vflip"];
            if (!(root["ranges"] is JArray list) || list.Count == 0) throw new InvalidDataException("job has no ranges");
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i] as JObject ?? throw new InvalidDataException($"range {i} is not an object");
                var item = new Range
                {
                    Index = i, From = (double?)r["from"] ?? -1, To = (double?)r["to"] ?? -1,
                    Fps = (int?)r["fps"] ?? 30, Out = hostPath((string)r["out"])
                };
                if (item.From < 0 || item.To <= item.From) throw new InvalidDataException($"range {i}: need 0 <= from < to");
                if (item.Fps < 1 || item.Fps > 240) throw new InvalidDataException($"range {i}: fps must be 1-240");
                if (string.IsNullOrWhiteSpace(item.Out)) throw new InvalidDataException($"range {i}: out is missing");
                Ranges.Add(item);
            }
            Ranges.Sort((a, b) => a.From.CompareTo(b.From));
            // The game renders one range at a time on one forward pass of the replay.
            for (int i = 1; i < Ranges.Count; i++)
                if (Ranges[i].From < Ranges[i - 1].To)
                    throw new InvalidDataException($"ranges {Ranges[i - 1].Index} and {Ranges[i].Index} overlap; render overlapping clips as one range and cut them afterwards");
            foreach (var r in Ranges) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(r.Out)));
            // Decide the encoder in the background while the game boots.
            probe = new Thread(ProbeEncoder) { IsBackground = true, Name = "nc-ffmpeg-probe" };
            probe.Start();
        }

        private static string SharedFfmpeg()
        {
            try
            {
                return FfmpegPackage.FindExe(BepInEx.Paths.GameRootPath);
            }
            catch (Exception) { return null; }
        }

        private void ProbeEncoder()
        {
            if (EncoderSetting == "x264") { codec = "libx264"; return; }
            if (EncoderSetting == "nvenc") { codec = "h264_nvenc"; return; }
            if (EncoderSetting == "vulkan") { codec = "h264_vulkan"; return; }
            // auto: NVENC, then Vulkan (the GPU path that works under Proton), then x264.
            codec = "libx264";
            foreach (var candidate in new[] { "h264_nvenc", "h264_vulkan" })
            {
                if (FfmpegRunner.EncoderWorks(Ffmpeg, candidate, 30000, out string reason)) { codec = candidate; break; }
                log.LogInfo($"Render job: {candidate} unavailable ({reason})");
            }
            log.LogInfo("Render job: encoder " + codec);
        }

        private Process Start(string arguments)
        {
            var info = new ProcessStartInfo(Ffmpeg, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            return Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start: " + Ffmpeg);
        }

        private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

        // A frame writer for range i whose target is a new ffmpeg process, started on the writer thread
        // when the first frame arrives (its size is the grabbed screen size; ffmpeg scales to the job size).
        public RawRangeWriter CreateWriter(int i, ManualLogSource logger)
        {
            var r = Ranges[i];
            return new RawRangeWriter(writer => OpenEncoder(r, writer.Width, writer.Height), Path.GetFileName(r.Out) + " encoder", logger);
        }

        private Stream OpenEncoder(Range r, int w, int h)
        {
            if (probe != null && !probe.Join(60000)) codec = "libx264";
            var filters = new List<string>();
            bool flip = VFlip ?? !SystemInfo.graphicsUVStartsAtTop;
            if (flip) filters.Add("vflip");
            if (w != Width || h != Height) filters.Add($"scale={Width}:{Height}:flags=lanczos");
            string hw = "";
            string venc;
            if (codec == "h264_vulkan")
            {
                // Frames go to the GPU as NV12; the encoder takes Vulkan frames, so no output -pix_fmt.
                hw = "-init_hw_device vulkan=vk -filter_hw_device vk ";
                filters.Add("format=nv12");
                filters.Add("hwupload");
                venc = $"-c:v h264_vulkan -rc_mode cqp -qp {Quality + 1} -tune hq -usage record -content rendered";
            }
            else venc = (codec == "h264_nvenc"
                ? $"-c:v h264_nvenc -preset p5 -tune hq -rc vbr -cq {Quality} -b:v 0"
                : $"-c:v libx264 -preset veryfast -crf {Quality}") + " -pix_fmt yuv420p";
            var e = new Encoder { Range = r, Part = r.Out + ".part" };
            string args = $"-hide_banner -loglevel error -y {hw}-f rawvideo -pix_fmt rgba -s {w}x{h} -r {r.Fps} -i pipe:0 "
                + (filters.Count > 0 ? "-vf " + string.Join(",", filters) + " " : "")
                + $"{venc} -g {r.Fps * 2} -movflags +faststart -f mp4 {Quote(e.Part)}";
            log.LogInfo($"Render job: range {r.Index} encoder: {Ffmpeg} {args}");
            e.Process = Start(args);
            e.Process.ErrorDataReceived += (_, d) => { if (d.Data == null) return; lock (e.Stderr) { e.Stderr.Enqueue(d.Data); while (e.Stderr.Count > 20) e.Stderr.Dequeue(); } };
            e.Process.OutputDataReceived += (_, d) => { };
            e.Process.BeginErrorReadLine();
            e.Process.BeginOutputReadLine();
            lock (encoders) encoders.Add(e);
            return e.Process.StandardInput.BaseStream;
        }

        // Called after the range's writer has closed (stdin closed): waits for ffmpeg off the main thread,
        // then renames <out>.part to <out>.
        public void RangeFinished(int i, int framesWritten, int errors)
        {
            var r = Ranges[i];
            Encoder e;
            lock (encoders) e = encoders.FirstOrDefault(x => x.Range == r);
            if (e == null)
            {
                Fail($"range {r.Index} ({r.From}-{r.To}): no frames were captured");
                return;
            }
            try { e.Process.StandardInput.Close(); } catch (Exception) { }
            e.Waiter = new Thread(() =>
            {
                try
                {
                    if (!e.Process.WaitForExit(600000)) { try { e.Process.Kill(); } catch (Exception) { } e.Error = "ffmpeg did not finish within 600 s"; return; }
                    e.Process.WaitForExit();
                    if (e.Process.ExitCode != 0) { e.Error = $"ffmpeg exited with code {e.Process.ExitCode}: {Tail(e)}"; return; }
                    if (errors > 0) { e.Error = $"{errors} frame write errors: {Tail(e)}"; return; }
                    if (File.Exists(r.Out)) File.Delete(r.Out);
                    File.Move(e.Part, r.Out);
                    log.LogInfo($"Render job: wrote {r.Out} ({framesWritten} frames, {codec})");
                }
                catch (Exception ex) { e.Error = "finishing clip failed: " + ex.Message; }
                finally { e.Finished = true; }
            }) { IsBackground = true, Name = "nc-ffmpeg-wait" };
            e.Waiter.Start();
        }

        private static string Tail(Encoder e)
        {
            lock (e.Stderr) return string.Join(" | ", e.Stderr).Trim();
        }

        // True once every started encoder has finished; error holds the first failure.
        public bool EncodersDone(out string firstError)
        {
            firstError = null;
            lock (encoders)
            {
                foreach (var e in encoders)
                {
                    if (!e.Finished) return false;
                    if (firstError == null && e.Error != null) firstError = $"range {e.Range.Index}: {e.Error}";
                }
            }
            return true;
        }

        public void Progress(string newState, int rangeIdx, int frameNow, int frameCount)
        {
            if (Finished) return;
            bool changed = newState != state || rangeIdx != range;
            state = newState; range = rangeIdx; frame = frameNow; frames = frameCount;
            Tick(changed);
        }

        public void Done()
        {
            if (Finished) return;
            state = "done"; frame = frames;
            Tick(true);
        }

        public void Fail(string message)
        {
            if (Finished) return;
            state = "error"; error = message;
            log.LogError("Render job failed: " + message);
            lock (encoders)
                foreach (var e in encoders)
                {
                    if (!e.Finished) { try { if (!e.Process.HasExited) e.Process.Kill(); } catch (Exception) { } }
                    try { if (File.Exists(e.Part)) File.Delete(e.Part); } catch (Exception) { }
                }
            Tick(true);
        }

        // Rewrites the progress file at least twice a second and on every state change, atomically
        // (write a temporary file, then replace).
        public void Tick(bool force = false)
        {
            if (ProgressPath == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (!force && now < nextWrite) return;
            nextWrite = now + 0.5;
            var o = new JObject
            {
                ["state"] = state, ["range"] = range, ["ranges"] = Ranges.Count, ["frame"] = frame, ["frames"] = frames,
                ["error"] = error, ["encoder"] = codec
            };
            string text = o.ToString(Formatting.None);
            string tmp = ProgressPath + ".tmp";
            try
            {
                File.WriteAllText(tmp, text + "\n", new UTF8Encoding(false));
                if (File.Exists(ProgressPath))
                {
                    try { File.Replace(tmp, ProgressPath, null); }
                    catch (Exception) { File.Delete(ProgressPath); File.Move(tmp, ProgressPath); }
                }
                else File.Move(tmp, ProgressPath);
            }
            catch (Exception e) { log.LogWarning("Render job: progress write failed: " + e.Message); }
        }

        public int ExpectedFrames(int i) => Ranges[i].Frames;
        public string Describe() => string.Join(", ", Ranges.Select(r => $"{r.From.ToString(CultureInfo.InvariantCulture)}:{r.To.ToString(CultureInfo.InvariantCulture)}@{r.Fps}"));
    }
}
