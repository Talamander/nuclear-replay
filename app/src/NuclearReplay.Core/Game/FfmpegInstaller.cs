using NuclearReplay.Core.Clips;
using NuclearReplay.Shared;

namespace NuclearReplay.Core.Game;

/// <summary>Download progress: bytes received of <see cref="Total"/>, then the verify and unpack stages.</summary>
public readonly record struct FfmpegProgress(string Stage, long Received, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;

    public string Text => Stage switch
    {
        "download" => $"Downloading ffmpeg: {Received / 1e6:0} of {Total / 1e6:0} MB ({Fraction * 100:0}%)",
        "verify" => "Checking the download",
        "unpack" => "Unpacking ffmpeg.exe",
        _ => Stage,
    };
}

/// <summary>Where ffmpeg comes from for one game: the setting, the shared copy in the game, or a bundled copy.</summary>
public sealed record FfmpegState
{
    /// <summary>The ffmpeg nr and the viewer use; null when there is none.</summary>
    public string? Path { get; init; }
    /// <summary>"setting", "shared", "bundled" or null.</summary>
    public string? Source { get; init; }
    public bool Present => Path != null && File.Exists(Path);
    /// <summary>&lt;game&gt;/NuclearReplay/tools/ffmpeg/ffmpeg.exe; null without a game.</summary>
    public string? SharedPath { get; init; }
    public bool SharedPresent { get; init; }
    /// <summary>The shared copy has the size of the pinned build.</summary>
    public bool SharedMatchesPin { get; init; }
}

/// <summary>
/// Downloads the pinned ffmpeg build (see shared/Ffmpeg/FfmpegPackage.cs) into &lt;game&gt;/NuclearReplay/tools/ffmpeg,
/// where nr and the in-game replay viewer both look for it. The zip is checked against the pinned size and
/// SHA-256 before anything is unpacked; only ffmpeg.exe and its license are kept. A failed or cancelled download leaves
/// no partial file behind.
/// </summary>
public sealed class FfmpegInstaller
{
    /// <summary>A read that stalls this long fails the download (the HttpClient timeout covers only the headers).</summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private readonly HttpClient http;
    private readonly FfmpegPackage package;

    internal FfmpegInstaller(HttpClient http, FfmpegPackage package)
    {
        this.http = http;
        this.package = package;
    }

    public string Version => package.Version;
    public string Url => package.Url;
    public string Host => package.Host;
    public long Size => package.Size;
    public string Sha256 => package.Sha256;
    /// <summary>"258 MB".</summary>
    public string SizeText => package.SizeText;
    public string Publisher => package.Publisher;
    public string SourceUrl => package.SourceUrl;

    public static string SharedPath(string gameDir) => FfmpegPackage.ExePath(gameDir);

    public FfmpegState State(Settings settings, string? gameDir)
    {
        var (path, source) = StatusService.ResolveFfmpegSource(settings, gameDir);
        var shared = gameDir != null ? FfmpegPackage.ExePath(gameDir) : null;
        return new FfmpegState
        {
            Path = path,
            Source = source,
            SharedPath = shared,
            SharedPresent = shared != null && File.Exists(shared),
            SharedMatchesPin = gameDir != null && package.IsInstalled(gameDir),
        };
    }

    /// <summary>
    /// Downloads, verifies and unpacks ffmpeg into the game. Returns the path of ffmpeg.exe. Does nothing when the pinned
    /// build is already there, unless <paramref name="force"/>. Throws <see cref="NcError"/> (ffmpeg_download_failed,
    /// ffmpeg_hash_mismatch, ffmpeg_install_failed) or <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public async Task<string> InstallAsync(string gameDir, IProgress<FfmpegProgress>? progress = null, bool force = false,
        CancellationToken cancellationToken = default)
    {
        var exe = FfmpegPackage.ExePath(gameDir);
        if (!force && package.IsInstalled(gameDir)) return exe;
        if (!Directory.Exists(gameDir))
            throw new NcError("game_not_found", $"\"{gameDir}\" does not exist.", "Choose the Nuclear Option folder first.");

        FfmpegPackage.CleanPartials(gameDir);
        string part;
        try
        {
            Directory.CreateDirectory(FfmpegPackage.InstallDir(gameDir));
            part = FfmpegPackage.PartPath(gameDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new NcError("ffmpeg_install_failed", $"Could not create {FfmpegPackage.InstallDir(gameDir)}: {e.Message}",
                "Check that the game folder is writable, then try again.");
        }

        try
        {
            await DownloadAsync(part, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new FfmpegProgress("verify", package.Size, package.Size));
            var problem = await Task.Run(() => package.Verify(part), cancellationToken);
            if (problem != null)
                throw new NcError("ffmpeg_hash_mismatch", problem + " Nothing was installed.",
                    "Try again. If this keeps happening, choose your own ffmpeg.exe: nr settings set ffmpeg_path <path>.");
            progress?.Report(new FfmpegProgress("unpack", package.Size, package.Size));
            await Task.Run(() => package.Extract(part, FfmpegPackage.InstallDir(gameDir)), cancellationToken);
            Log.Info($"Installed ffmpeg {package.Version} to {exe}");
            return exe;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new NcError("ffmpeg_install_failed", "ffmpeg could not be unpacked: " + e.Message,
                "Close the game and any running render, then try again.");
        }
        finally
        {
            FfmpegPackage.TryDelete(part);
        }
    }

    private async Task DownloadAsync(string part, IProgress<FfmpegProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{package.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buffer = new byte[1 << 16];
            long total = 0, reported = -1;
            progress?.Report(new FfmpegProgress("download", 0, package.Size));
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stall.CancelAfter(StallTimeout);
                int read;
                try { read = await source.ReadAsync(buffer, stall.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new HttpRequestException($"The download stopped (no data for {StallTimeout.TotalSeconds:0} seconds).");
                }
                if (read == 0) break;
                total += read;
                if (total > package.Size) throw new HttpRequestException("The download is larger than expected.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                // About 200 updates over the whole download.
                if (progress != null && total - reported >= Math.Max(1, package.Size / 200))
                {
                    reported = total;
                    progress.Report(new FfmpegProgress("download", total, package.Size));
                }
            }
            progress?.Report(new FfmpegProgress("download", total, package.Size));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new NcError("ffmpeg_download_failed", $"ffmpeg could not be downloaded from {package.Host}: {e.Message}",
                "Check your internet connection and try again, or choose your own ffmpeg.exe: nr settings set ffmpeg_path <path>.");
        }
    }
}
