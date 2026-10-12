using System;
using System.Collections.Concurrent;
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
    // "Clip this" in the viewer: records the replay in real time while it plays at 1x, at the screen's own
    // resolution, and encodes it with ffmpeg as it goes.
    //
    // Frames: each rendered frame that crosses one or more 1/fps steps of replay time is copied into a render
    // texture at end of frame and read back with AsyncGPUReadback (never a blocking ReadPixels); the
    // readback is written once per step it covers, so the file has a constant frame rate even when the game
    // runs slower or faster than the clip rate. A writer thread pipes the RGBA frames into ffmpeg's stdin.
    //
    // Encoder (auto): h264_nvenc, then h264_vulkan, then libx264. Under Proton the Windows ffmpeg cannot use
    // NVENC (it needs nvEncodeAPI64.dll, which Wine/Proton do not provide; dxvk-nvapi covers NVAPI only), but
    // h264_vulkan encodes on the GPU through winevulkan and the driver's Vulkan Video support.
    //
    // Audio: an OnAudioFilterRead tap on the AudioListener copies the game's final mix into a WAV file for the
    // same span; after the video is encoded, ffmpeg muxes it in (AAC). With no audio output device (muted
    // test sessions) Unity mixes nothing and the clip has no sound track.
    //
    // Output: <clips>/<mission> <from>-<to>.mp4 and a record in <game>/NuclearReplay/viewer-clips/<id>/
    // (job.json in the render-job format plus progress.json). The desktop app imported these into its Renders list;
    // nothing reads them now, but tools can.
    internal sealed class ClipRecorder
    {
        // ---------- encoder probe (once per session, in the background) ----------
        private static string probedCodec;
        private static Thread probeThread;
        private static string probeFfmpeg;

        public static string ProbedCodec => probedCodec;

        public static void Probe(string ffmpeg, string setting, ManualLogSource log)
        {
            if (probeThread != null && probeFfmpeg == ffmpeg) return;
            probeFfmpeg = ffmpeg;
            probedCodec = null;
            probeThread = new Thread(() =>
            {
                string chosen = "libx264";
                var order = setting == "x264" ? new string[0] : setting == "nvenc" ? new[] { "h264_nvenc" } : setting == "vulkan" ? new[] { "h264_vulkan" }
                    : new[] { "h264_nvenc", "h264_vulkan" };
                foreach (var codec in order)
                {
                    if (FfmpegRunner.EncoderWorks(ffmpeg, codec, 25000, out string reason)) { chosen = codec; break; }
                    log.LogInfo($"Viewer clips: {codec} unavailable ({reason})");
                }
                probedCodec = chosen;
                log.LogInfo("Viewer clips: encoder " + chosen);
            }) { IsBackground = true, Name = "nc-clip-probe" };
            probeThread.Start();
        }

        // ---------- one clip ----------
        public readonly double From, To;
        public readonly int Fps;
        public readonly int TotalFrames;
        public string OutPath, PartPath, JobDir, Codec, Error;
        public int Width, Height;
        public int Produced;            // output frames handed to readback
        public bool Started, Captured, Done;
        public string State = "waiting";
        public double FpsBefore, FpsDuring;
        public bool AudioWritten;

        private readonly ManualLogSource log;
        private readonly string ffmpeg;
        private readonly int quality;
        private RawRangeWriter writer;
        private Process process;
        private readonly Queue<string> stderr = new Queue<string>();
        private RenderTexture texture;
        private int grabCount;           // output frames the pending end-of-frame grab stands for
        private AudioTap tap;
        private string wavPath;
        private double wallStart, frameStart;
        private int readbackErrors;
        private readonly JObject jobRecord;
        private readonly Func<string, string> hostPath;

        public ClipRecorder(ManualLogSource log, string ffmpeg, int quality, double from, double to, int fps, string clipsDir, string recordRoot,
            string stem, JObject jobRecord, Func<string, string> hostPath)
        {
            this.log = log;
            this.ffmpeg = ffmpeg;
            this.quality = quality;
            this.jobRecord = jobRecord;
            this.hostPath = hostPath;
            From = from; To = to; Fps = fps;
            TotalFrames = Math.Max(1, (int)Math.Round((to - from) * fps));
            Directory.CreateDirectory(clipsDir);
            OutPath = ClipNames.UniquePath(clipsDir, ClipNames.Stem(stem, from, to));
            PartPath = OutPath + ".part.mp4";
            JobDir = Path.Combine(recordRoot, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(JobDir);
            wavPath = Path.Combine(JobDir, "audio.wav");
            Codec = probedCodec ?? "libx264";
            WriteRecord();
        }

        // Called every LateUpdate while the clip records, with the replay time of the frame about to render.
        // Returns true when the end-of-frame grab should run this frame.
        public bool OnFrame(double replayTime)
        {
            if (Done || Captured) return false;
            if (!Started)
            {
                if (replayTime < From) return false;
                Started = true;
                State = "recording";
                wallStart = Time.realtimeSinceStartupAsDouble;
                frameStart = Time.frameCount;
                Width = Screen.width & ~1; Height = Screen.height & ~1;
                writer = new RawRangeWriter(w => OpenEncoder(w.Width, w.Height), Path.GetFileName(OutPath) + " encoder", log);
                tap = AudioTap.Attach(wavPath, log);
                log.LogInfo($"Viewer clip: recording {From:F2}-{To:F2} s at {Width}x{Height} {Fps} fps with {Codec} into {OutPath}");
                WriteRecord();
            }
            int due = Math.Min(TotalFrames, (int)Math.Floor((replayTime - From) * Fps + 1e-6) + 1);
            if (due <= Produced) return false;
            grabCount = due - Produced;
            Produced = due;
            if (Produced >= TotalFrames)
            {
                Captured = true;
                double wall = Time.realtimeSinceStartupAsDouble - wallStart;
                FpsDuring = wall > 0 ? (Time.frameCount - frameStart) / wall : 0;
                tap?.Stop();
            }
            return true;
        }

        // End of frame: copy the finished frame (viewer UI already hidden) and read it back asynchronously.
        public void Grab()
        {
            int count = grabCount;
            grabCount = 0;
            if (count <= 0 || writer == null) return;
            if (texture == null || texture.width != Screen.width || texture.height != Screen.height)
            {
                if (texture != null) texture.Release();
                texture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
                texture.Create();
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(texture);
            writer.Width = texture.width; writer.Height = texture.height;
            writer.Pending++;
            var w = writer;
            UnityEngine.Rendering.AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32, request =>
            {
                w.Pending--;
                if (request.hasError) { readbackErrors++; w.Errors++; return; }
                var data = request.GetData<byte>();
                for (int i = 0; i < count; i++) w.Add(data);
            });
        }

        private Stream OpenEncoder(int w, int h)
        {
            var filters = new List<string>();
            if (!SystemInfo.graphicsUVStartsAtTop) filters.Add("vflip");
            int ew = w & ~1, eh = h & ~1;
            if (ew != w || eh != h) filters.Add($"crop={ew}:{eh}:0:0");
            string venc;
            string hw = "";
            switch (Codec)
            {
                case "h264_nvenc":
                    venc = $"-c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq {quality} -b:v 0 -pix_fmt yuv420p";
                    break;
                case "h264_vulkan":
                    hw = "-init_hw_device vulkan=vk -filter_hw_device vk ";
                    filters.Add("format=nv12");
                    filters.Add("hwupload");
                    venc = $"-c:v h264_vulkan -rc_mode cqp -qp {quality + 1} -tune hq -usage record -content rendered";
                    break;
                default:
                    // Real time on the CPU: fast presets only; above 1440p drop to superfast.
                    venc = $"-c:v libx264 -preset {(w * h > 2560 * 1440 ? "superfast" : "veryfast")} -crf {quality} -pix_fmt yuv420p";
                    break;
            }
            string args = $"-hide_banner -loglevel error -y {hw}-f rawvideo -pix_fmt rgba -s {w}x{h} -r {Fps} -i pipe:0 "
                + (filters.Count > 0 ? "-vf " + string.Join(",", filters) + " " : "")
                + $"{venc} -g {Fps * 2} -movflags +faststart -f mp4 \"{PartPath}\"";
            log.LogInfo($"Viewer clip encoder: {ffmpeg} {args}");
            process = Process.Start(new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }) ?? throw new InvalidOperationException("ffmpeg did not start: " + ffmpeg);
            process.ErrorDataReceived += (_, d) => { if (d.Data == null) return; lock (stderr) { stderr.Enqueue(d.Data); while (stderr.Count > 20) stderr.Dequeue(); } };
            process.OutputDataReceived += (_, d) => { };
            process.BeginErrorReadLine(); process.BeginOutputReadLine();
            return process.StandardInput.BaseStream;
        }

        // After the last frame: flush readbacks, close the encoder and finish the file off the main thread.
        public void Finish(Action<ClipRecorder> finished)
        {
            State = "encoding";
            WriteRecord();
            UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();
            ReleaseTexture();
            var w = writer;
            writer = null;
            new Thread(() =>
            {
                try
                {
                    // Ends the sound track here, also for a clip cut short by the end of the replay.
                    tap?.Close();
                    w?.Close();
                    if (process == null) throw new InvalidOperationException("no frames were captured");
                    try { process.StandardInput.Close(); } catch (Exception) { }
                    if (!process.WaitForExit(600000)) { try { process.Kill(); } catch (Exception) { } throw new TimeoutException("ffmpeg did not finish within 600 s"); }
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {Tail()}");
                    if (w != null && w.Errors > 0) throw new InvalidOperationException($"{w.Errors} frame write errors: {Tail()}");
                    long audioBytes = tap?.DataBytes ?? 0;
                    if (audioBytes > 0 && HasSignal())
                    {
                        string muxed = OutPath + ".mux.mp4";
                        string err = FfmpegRunner.Run(ffmpeg, $"-hide_banner -loglevel error -y -i \"{PartPath}\" -i \"{wavPath}\" -map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart \"{muxed}\"", 300000, out int code);
                        if (code == 0) { File.Delete(PartPath); File.Move(muxed, PartPath); AudioWritten = true; }
                        else log.LogWarning("Viewer clip: adding the sound failed, keeping the clip without it: " + err);
                    }
                    else log.LogInfo("Viewer clip: no game audio was mixed (no audio output device in this session), clip has no sound track");
                    if (File.Exists(OutPath)) File.Delete(OutPath);
                    File.Move(PartPath, OutPath);
                    try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch (Exception) { }
                    State = "done";
                    log.LogInfo($"Viewer clip: wrote {OutPath} ({TotalFrames} frames, {Codec}, audio {(AudioWritten ? "yes" : "no")}, game fps {FpsBefore:F1} before, {FpsDuring:F1} while recording)");
                }
                catch (Exception e)
                {
                    Error = e.Message;
                    State = "error";
                    log.LogError("Viewer clip failed: " + e);
                    try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch (Exception) { }
                    try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch (Exception) { }
                }
                try { process?.Dispose(); } catch (Exception) { }
                Done = true;
                WriteRecord();
                finished?.Invoke(this);
            }) { IsBackground = true, Name = "nc-clip-finish" }.Start();
        }

        // Cancels a clip that is still recording; no file is kept.
        public void Cancel()
        {
            tap?.Stop();
            tap?.Close();
            Error = "canceled";
            State = "error";
            Done = true;
            try { writer?.Close(); } catch (Exception) { }
            writer = null;
            UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();
            ReleaseTexture();
            try { if (process != null && !process.HasExited) process.Kill(); } catch (Exception) { }
            try { process?.Dispose(); } catch (Exception) { }
            try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch (Exception) { }
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch (Exception) { }
            WriteRecord();
        }

        private void ReleaseTexture()
        {
            if (texture == null) return;
            texture.Release();
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        // True when the WAV holds more than silence (the mixer runs silent when there is no output device).
        private bool HasSignal() => tap != null && tap.Peak > 1e-4f;

        private string Tail() { lock (stderr) return string.Join(" | ", stderr).Trim(); }

        private void WriteRecord()
        {
            try
            {
                var job = (JObject)jobRecord.DeepClone();
                job["width"] = Width > 0 ? Width : Screen.width;
                job["height"] = Height > 0 ? Height : Screen.height;
                job["encoder"] = Codec;
                job["quality"] = quality;
                job["progress"] = Path.Combine(JobDir, "progress.json");
                job["ranges"] = new JArray(new JObject { ["from"] = From, ["to"] = To, ["fps"] = Fps, ["out"] = OutPath });
                WriteAtomic(Path.Combine(JobDir, "job.json"), job.ToString(Formatting.Indented));
                var progress = new JObject
                {
                    ["state"] = State, ["range"] = 0, ["ranges"] = 1, ["frame"] = Math.Min(Produced, TotalFrames), ["frames"] = TotalFrames,
                    ["error"] = Error, ["encoder"] = Codec, ["fps_before"] = Math.Round(FpsBefore, 1), ["fps_during"] = Math.Round(FpsDuring, 1),
                    ["audio"] = AudioWritten
                };
                WriteAtomic(Path.Combine(JobDir, "progress.json"), progress.ToString(Formatting.None));
            }
            catch (Exception e) { log.LogWarning("Viewer clip: record write failed: " + e.Message); }
        }

        private static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text + "\n", new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }

    // Copies the game's final audio mix (everything the AudioListener hears) into a 16-bit WAV file.
    internal sealed class AudioTap : MonoBehaviour
    {
        private readonly BlockingCollection<float[]> queue = new BlockingCollection<float[]>(4096);
        private volatile bool recording;
        private Thread thread;
        private FileStream file;
        private int channels, rate;
        private ManualLogSource log;
        public long DataBytes;
        public float Peak;

        public static AudioTap Attach(string path, ManualLogSource log)
        {
            try
            {
                var listener = FindObjectOfType<AudioListener>();
                if (listener == null) { log.LogInfo("Viewer clip: no AudioListener, recording without sound"); return null; }
                var tap = listener.gameObject.AddComponent<AudioTap>();
                tap.log = log;
                tap.rate = AudioSettings.outputSampleRate;
                tap.file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                tap.file.Write(new byte[44], 0, 44);
                tap.thread = new Thread(tap.Run) { IsBackground = true, Name = "nc-clip-audio" };
                tap.thread.Start();
                tap.recording = true;
                return tap;
            }
            catch (Exception e) { log.LogWarning("Viewer clip: audio tap failed: " + e.Message); return null; }
        }

        private void OnAudioFilterRead(float[] data, int count)
        {
            if (!recording) return;
            channels = count;
            var copy = new float[data.Length];
            Array.Copy(data, copy, data.Length);
            try { queue.TryAdd(copy); }
            catch (InvalidOperationException) { }   // Close completed the queue after the check above
        }

        private void Run()
        {
            var bytes = new byte[0];
            try
            {
                foreach (var block in queue.GetConsumingEnumerable())
                {
                    if (bytes.Length != block.Length * 2) bytes = new byte[block.Length * 2];
                    for (int i = 0; i < block.Length; i++)
                    {
                        float v = block[i];
                        float a = v < 0 ? -v : v;
                        if (a > Peak) Peak = a;
                        short s = (short)Mathf.Clamp(v * 32767f, -32768f, 32767f);
                        bytes[i * 2] = (byte)s; bytes[i * 2 + 1] = (byte)(s >> 8);
                    }
                    file.Write(bytes, 0, bytes.Length);
                    DataBytes += bytes.Length;
                }
            }
            catch (Exception e)
            {
                recording = false;
                log?.LogWarning("Viewer clip: audio write failed, the rest of the clip has no sound: " + e.Message);
                foreach (var _ in queue.GetConsumingEnumerable()) { }   // keep draining until Close
            }
        }

        public void Stop() => recording = false;

        public void Close()
        {
            recording = false;
            var t = Interlocked.Exchange(ref thread, null);   // Cancel and the finish thread may both close
            if (t == null) return;
            queue.CompleteAdding();
            t.Join(10000);
            try
            {
                int ch = channels > 0 ? channels : 2;
                file.Seek(0, SeekOrigin.Begin);
                var w = new BinaryWriter(file);
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write((int)(36 + DataBytes)); w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate);
                w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write((int)DataBytes);
                w.Flush();
                file.Dispose();
            }
            catch (Exception e) { log?.LogWarning("Viewer clip: WAV close failed: " + e.Message); }
            try { UnityMainThread.Post(() => { if (this != null) Destroy(this); }); } catch (Exception) { }
        }
    }

    // Runs small actions on the game thread (the viewer calls Drain every frame).
    internal static class UnityMainThread
    {
        private static readonly ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();
        public static void Post(Action a) => actions.Enqueue(a);
        public static void Drain()
        {
            while (actions.TryDequeue(out var a)) { try { a(); } catch (Exception e) { UnityEngine.Debug.LogException(e); } }
        }
    }
}
