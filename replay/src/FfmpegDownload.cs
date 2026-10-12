using System;
using System.Collections;
using System.IO;
using System.Threading;
using BepInEx.Logging;
using NuclearReplay.Shared;
using UnityEngine;
using UnityEngine.Networking;

namespace NuclearReplay.Playback
{
    // "Clip this" without ffmpeg: asks the player, then downloads the pinned build (shared/Ffmpeg/FfmpegPackage.cs) into
    // <game>/NuclearReplay/tools/ffmpeg, the folder nr uses too. Nothing is downloaded before the player says
    // yes. The download is a UnityWebRequest polled from a coroutine; checking the SHA-256 and unpacking run on a
    // background thread, so the game keeps its frame rate. Only a verified zip is unpacked, and the zip is removed after.
    internal sealed class FfmpegDownload
    {
        public enum Phase { Idle, Asking, Downloading, Installing, Failed }

        // A download that receives nothing for this long fails (a dead connection never ends otherwise).
        private const float StallSeconds = 60f;

        private readonly FfmpegPackage package;
        private readonly string gameDir;
        private readonly ManualLogSource log;
        private readonly Action<string> installed;
        private UnityWebRequest request;
        private string part;
        private bool cancel;
        private int generation;

        public Phase State { get; private set; }
        public float Fraction { get; private set; }
        public ulong Received { get; private set; }
        public string Error { get; private set; }
        public FfmpegPackage Package => package;
        public bool Open => State != Phase.Idle;

        // installed: called on the main thread with the path of ffmpeg.exe.
        public FfmpegDownload(FfmpegPackage package, string gameDir, ManualLogSource log, Action<string> installed)
        {
            this.package = package;
            this.gameDir = gameDir;
            this.log = log;
            this.installed = installed;
        }

        public string ExePath => FfmpegPackage.ExePath(gameDir);

        public string Question =>
            $"Clips need ffmpeg, a free video tool, about {package.SizeText} from {package.Host}. Download now?";

        public string ProgressText
        {
            get
            {
                switch (State)
                {
                    case Phase.Asking:
                        return $"ffmpeg {package.Version} (GPL) is published by {package.Publisher}. It is saved to {FfmpegPackage.InstallDir(gameDir)} and checked before use.";
                    case Phase.Downloading:
                        return Received == 0 ? "Connecting to " + package.Host + "..."
                            : $"Downloading: {Received / 1e6:0} of {package.Size / 1e6:0} MB ({Fraction * 100:0}%). You can keep watching the replay.";
                    case Phase.Installing: return "Checking the download and unpacking ffmpeg.exe...";
                    case Phase.Failed: return Error;
                    default: return "";
                }
            }
        }

        public void Ask()
        {
            if (State == Phase.Downloading || State == Phase.Installing) return;
            Error = null;
            State = Phase.Asking;
        }

        public void Dismiss()
        {
            if (State == Phase.Asking || State == Phase.Failed) State = Phase.Idle;
        }

        public void Cancel()
        {
            if (State == Phase.Downloading) cancel = true;
        }

        // Starts the download (after the player said yes). The caller runs the returned coroutine.
        public IEnumerator Run()
        {
            if (State == Phase.Downloading || State == Phase.Installing) yield break;
            int run = ++generation;
            State = Phase.Downloading;
            cancel = false;
            Fraction = 0; Received = 0; Error = null;
            try
            {
                FfmpegPackage.CleanPartials(gameDir);
                Directory.CreateDirectory(FfmpegPackage.InstallDir(gameDir));
                part = FfmpegPackage.PartPath(gameDir);
                request = new UnityWebRequest(package.Url, UnityWebRequest.kHttpVerbGET)
                {
                    downloadHandler = new DownloadHandlerFile(part) { removeFileOnAbort = true },
                    disposeDownloadHandlerOnDispose = true,
                };
                try { request.SetRequestHeader("User-Agent", "NuclearReplay/" + BuildVersion.Core); } catch (Exception) { }
                request.SendWebRequest();
                log.LogInfo($"ffmpeg: downloading {package.Url} to {part}");
            }
            catch (Exception e)
            {
                Fail("Could not start the download: " + e.Message);
                yield break;
            }

            float lastChange = Time.unscaledTime;
            ulong lastBytes = 0;
            while (!request.isDone)
            {
                if (cancel)
                {
                    Stop();
                    log.LogInfo("ffmpeg: download cancelled");
                    Error = "Download cancelled. Nothing was kept.";
                    State = Phase.Failed;
                    yield break;
                }
                Received = request.downloadedBytes;
                Fraction = package.Size > 0 ? Mathf.Clamp01((float)((double)Received / package.Size)) : request.downloadProgress;
                if (Received != lastBytes) { lastBytes = Received; lastChange = Time.unscaledTime; }
                else if (Time.unscaledTime - lastChange > StallSeconds)
                {
                    Stop();
                    Fail($"The download stopped (no data for {StallSeconds:0} seconds). Check your internet connection and try again.");
                    yield break;
                }
                if ((long)Received > package.Size)
                {
                    Stop();
                    Fail("The download is larger than expected, so it was stopped.");
                    yield break;
                }
                yield return null;
            }

            bool ok = request.result == UnityWebRequest.Result.Success;
            string problem = ok ? null
                : request.responseCode >= 400 ? $"{package.Host} answered {request.responseCode}."
                : request.error;
            request.Dispose(); // closes the file
            request = null;
            if (!ok)
            {
                Cleanup();
                Fail("ffmpeg could not be downloaded: " + problem + " Check your internet connection and try again.");
                yield break;
            }

            State = Phase.Installing;
            Fraction = 1;
            string zip = part;
            var thread = new Thread(() =>
            {
                string error = null;
                try { package.InstallFromDownload(zip, gameDir); }   // verifies size + SHA-256, unpacks, removes the zip
                catch (Exception e) { error = e is InvalidDataException ? e.Message : "ffmpeg could not be unpacked: " + e.Message; }
                UnityMainThread.Post(() =>
                {
                    if (run != generation) return;
                    if (error != null)
                    {
                        Fail(error + " Nothing was installed.");
                        return;
                    }
                    State = Phase.Idle;
                    log.LogInfo("ffmpeg: installed " + ExePath);
                    installed(ExePath);
                });
            }) { IsBackground = true, Name = "nc-ffmpeg-install" };
            thread.Start();
        }

        // Game closing: stop the request and remove the partial file.
        public void Shutdown() => Stop();

        private void Stop()
        {
            try { request?.Abort(); request?.Dispose(); } catch (Exception) { }
            request = null;
            Cleanup();
        }

        private void Fail(string message)
        {
            log.LogWarning("ffmpeg: " + message);
            Error = message;
            State = Phase.Failed;
        }

        private void Cleanup()
        {
            if (part != null) FfmpegPackage.TryDelete(part);
        }
    }
}
