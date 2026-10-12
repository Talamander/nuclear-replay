using System;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace NuclearReplay.Playback
{
    // Short ffmpeg runs shared by render jobs and viewer clips: the encoder probe and the audio mux.
    internal static class FfmpegRunner
    {
        public const int TimedOut = -999;

        // Runs ffmpeg to completion and returns the last line it printed. code is TimedOut when it ran past
        // timeoutMs (it is killed then), and -1 when it could not be started.
        public static string Run(string ffmpeg, string args, int timeoutMs, out int code)
        {
            code = -1;
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(ffmpeg, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                }))
                {
                    if (p == null) return "ffmpeg did not start: " + ffmpeg;
                    var err = new StringBuilder();
                    p.ErrorDataReceived += (_, d) => { if (d.Data != null) lock (err) err.AppendLine(d.Data); };
                    p.OutputDataReceived += (_, d) => { };
                    p.BeginErrorReadLine(); p.BeginOutputReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } code = TimedOut; return "timeout"; }
                    p.WaitForExit();
                    code = p.ExitCode;
                    lock (err) return err.ToString().Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
                }
            }
            catch (Exception e) { return e.Message; }
        }

        // Checks whether an H.264 encoder works on this machine by encoding a short black clip. When it does
        // not, reason says why.
        public static bool EncoderWorks(string ffmpeg, string codec, int timeoutMs, out string reason)
        {
            string args = codec == "h264_vulkan"
                ? "-hide_banner -loglevel error -init_hw_device vulkan=vk -filter_hw_device vk -f lavfi -i color=black:s=256x256:d=0.2 -vf format=nv12,hwupload -c:v h264_vulkan -f null -"
                : "-hide_banner -loglevel error -f lavfi -i color=black:s=256x256:d=0.2 -c:v " + codec + " -f null -";
            string error = Run(ffmpeg, args, timeoutMs, out int code);
            reason = code == 0 ? null : code == TimedOut ? "probe timed out" : $"exit {code}: {error}";
            return code == 0;
        }
    }
}
