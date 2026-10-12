using System.IO.Compression;
using System.Text.Json.Nodes;
using NuclearReplay.Cli;
using NuclearReplay.Core.Clips;
using NuclearReplay.Core.Game;
using NuclearReplay.Shared;

namespace NuclearReplay.Core.Tests;

/// <summary>A small zip shaped like the real ffmpeg download, served by <see cref="FakeHttp"/>. Tests never download ffmpeg.</summary>
internal sealed class FakeFfmpeg
{
    public const string Url = "https://example.invalid/ffmpeg-test-full_build.zip";
    public byte[] Zip { get; }
    public byte[] Exe { get; }
    public FfmpegPackage Package { get; }

    public FakeFfmpeg(CompressionLevel level = CompressionLevel.Optimal, int exeSize = 300_000)
    {
        // Compressible but not trivial, so the deflate path does real work.
        Exe = new byte[exeSize];
        for (int i = 0; i < Exe.Length; i++) Exe[i] = (byte)(i * 31 % 251 ^ i / 1024);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "ffmpeg-test-full_build/README.txt", "readme"u8.ToArray(), level);
            Add(zip, "ffmpeg-test-full_build/bin/ffprobe.exe", "probe"u8.ToArray(), level);
            Add(zip, "ffmpeg-test-full_build/bin/ffmpeg.exe", Exe, level);
            Add(zip, "ffmpeg-test-full_build/LICENSE", "GPL v3 text"u8.ToArray(), level);
        }
        Zip = stream.ToArray();
        Package = new FfmpegPackage
        {
            Version = "test",
            Url = Url,
            Size = Zip.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Zip)).ToLowerInvariant(),
            ExeEntry = "ffmpeg-test-full_build/bin/ffmpeg.exe",
            ExeSize = Exe.Length,
            LicenseEntry = "ffmpeg-test-full_build/LICENSE",
            Publisher = "test",
            SourceUrl = "https://example.invalid/source",
        };
    }

    private static void Add(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        using var entry = zip.CreateEntry(name, level).Open();
        entry.Write(data);
    }

    public FakeHttp Serve(FakeHttp? http = null)
    {
        http ??= new FakeHttp();
        http.Files[Url] = Zip;
        return http;
    }
}

public class FfmpegTests
{
    private static FfmpegInstaller Installer(FakeFfmpeg fake, FakeHttp http) =>
        new NcEnvironment(httpHandler: http) { FfmpegPackage = fake.Package }.CreateFfmpegInstaller();

    private static string[] Leftovers(string game) =>
        Directory.Exists(FfmpegPackage.InstallDir(game))
            ? Directory.GetFiles(FfmpegPackage.InstallDir(game)).Select(f => Path.GetFileName(f)).Where(n => n != "ffmpeg.exe" && n != "LICENSE.txt").ToArray()
            : Array.Empty<string>();

    [Fact]
    public void ThePinIsCompleteAndPermanent()
    {
        var pin = FfmpegPackage.Pinned;
        // A versioned GitHub release (kept), not an autobuild (pruned).
        Assert.StartsWith("https://github.com/GyanD/codexffmpeg/releases/download/" + pin.Version + "/", pin.Url);
        Assert.Matches("^[0-9a-f]{64}$", pin.Sha256);
        Assert.True(pin.Size > 100_000_000);
        Assert.True(pin.ExeSize > 100_000_000);
        Assert.EndsWith("/bin/ffmpeg.exe", pin.ExeEntry);
        Assert.Equal("github.com", pin.Host);
        Assert.Equal("258 MB", pin.SizeText);
    }

    [Fact]
    public void SharedPathIsInTheGamesNuclearReplayFolder()
    {
        var game = Path.Combine("games", "Nuclear Option");
        Assert.Equal(Path.Combine(game, "NuclearReplay", "tools", "ffmpeg", "ffmpeg.exe"), FfmpegPackage.ExePath(game));
        Assert.Equal(FfmpegPackage.ExePath(game), FfmpegInstaller.SharedPath(game));
    }

    [Theory]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.NoCompression)]
    public async Task DownloadsVerifiesAndUnpacksOnlyFfmpegAndItsLicense(CompressionLevel level)
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var fake = new FakeFfmpeg(level);
        var http = fake.Serve();
        var reports = new List<FfmpegProgress>();

        var path = await Installer(fake, http).InstallAsync(game, new Progress<FfmpegProgress>(reports.Add));

        Assert.Equal(FfmpegPackage.ExePath(game), path);
        Assert.Equal(fake.Exe, File.ReadAllBytes(path));
        Assert.Equal("GPL v3 text", File.ReadAllText(FfmpegPackage.LicensePath(game)));
        Assert.Equal(new[] { "LICENSE.txt", "ffmpeg.exe" }, Directory.GetFiles(FfmpegPackage.InstallDir(game)).Select(Path.GetFileName).Order());
        Assert.True(fake.Package.IsInstalled(game));

        // A second install sees the pinned build and does not download again.
        await Installer(fake, http).InstallAsync(game);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task ABadHashIsRejectedAndNothingIsLeftBehind()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var fake = new FakeFfmpeg();
        var http = fake.Serve();
        var tampered = (byte[])fake.Zip.Clone();
        tampered[100] ^= 0xFF; // same size, different bytes
        http.Files[FakeFfmpeg.Url] = tampered;

        var e = await Assert.ThrowsAsync<NcError>(() => Installer(fake, http).InstallAsync(game));

        Assert.Equal("ffmpeg_hash_mismatch", e.Code);
        Assert.Contains("SHA-256", e.Message);
        Assert.False(File.Exists(FfmpegPackage.ExePath(game)));
        Assert.Empty(Leftovers(game));
    }

    [Fact]
    public async Task AShortDownloadIsRejected()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var fake = new FakeFfmpeg();
        var http = fake.Serve();
        http.Files[FakeFfmpeg.Url] = fake.Zip[..^10];

        var e = await Assert.ThrowsAsync<NcError>(() => Installer(fake, http).InstallAsync(game));

        Assert.Equal("ffmpeg_hash_mismatch", e.Code);
        Assert.Contains("wrong size", e.Message);
        Assert.Empty(Leftovers(game));
    }

    [Fact]
    public async Task OfflineAndMissingFilesSayWhatToDo()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var fake = new FakeFfmpeg();

        var offline = await Assert.ThrowsAsync<NcError>(() => Installer(fake, new FakeHttp { Offline = true }).InstallAsync(game));
        var notFound = await Assert.ThrowsAsync<NcError>(() => Installer(fake, new FakeHttp()).InstallAsync(game));

        Assert.Equal("ffmpeg_download_failed", offline.Code);
        Assert.Contains("example.invalid", offline.Message);
        Assert.Contains("try again", offline.Fix);
        Assert.Equal("ffmpeg_download_failed", notFound.Code);
        Assert.Contains("404", notFound.Message);
        Assert.Empty(Leftovers(game));
    }

    [Fact]
    public async Task CancelRemovesThePartialDownload()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var fake = new FakeFfmpeg(CompressionLevel.NoCompression, exeSize: 2_000_000);
        var http = fake.Serve();
        using var cancel = new CancellationTokenSource();
        bool sawPartial = false;
        var progress = new SyncProgress(p =>
        {
            if (p.Stage == "download" && p.Received > 0)
            {
                sawPartial = Directory.GetFiles(FfmpegPackage.InstallDir(game), "*.part").Length == 1;
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer(fake, http).InstallAsync(game, progress, cancellationToken: cancel.Token));

        Assert.True(sawPartial);
        Assert.Empty(Leftovers(game));
        Assert.False(File.Exists(FfmpegPackage.ExePath(game)));
    }

    [Fact]
    public void PartialsLeftByAClosedGameAreCleanedUp()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var stale = dir.Write(Path.Combine(FfmpegPackage.InstallDir(game), "download-1234abcd.zip.part"), "half");

        FfmpegPackage.CleanPartials(game);

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void ExtractRejectsAZipWithoutFfmpeg()
    {
        using var dir = new TempDir();
        var zip = dir.Combine("other.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(archive.CreateEntry("readme.txt").Open())) w.Write("nothing");
        var target = dir.Combine("out");

        var e = Assert.Throws<InvalidDataException>(() => new FakeFfmpeg().Package.Extract(zip, target));

        Assert.Contains("bin/ffmpeg.exe", e.Message);
        Assert.False(File.Exists(Path.Combine(target, "ffmpeg.exe")));
    }

    [Fact]
    public void ExtractRejectsANonZip()
    {
        using var dir = new TempDir();
        var file = dir.Write("not.zip", new string('x', 100));
        var e = Assert.Throws<InvalidDataException>(() => new FakeFfmpeg().Package.Extract(file, dir.Combine("out")));
        Assert.Contains("not a zip", e.Message);
    }

    [Fact]
    public void ResolveOrderIsSettingThenSharedThenBundled()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var settings = new Settings();

        Assert.Equal((null, null), Bundled(StatusService.ResolveFfmpegSource(settings, game)));
        var shared = dir.Write(Path.Combine(FfmpegPackage.InstallDir(game), "ffmpeg.exe"), "x");
        Assert.Equal((shared, "shared"), StatusService.ResolveFfmpegSource(settings, game));
        settings.FfmpegPath = dir.Combine("mine", "ffmpeg.exe");
        Assert.Equal((settings.FfmpegPath, "setting"), StatusService.ResolveFfmpegSource(settings, game));

        // The test host may have a bundled copy next to it; ignore that case.
        static (string?, string?) Bundled((string? Path, string? Source) r) => r.Source == "bundled" ? (null, null) : r;
    }

    [Fact]
    public void StatusCheckPointsAtTheDownload()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var status = new StatusService(gameRunning: () => false, steamRunning: () => true);
        var report = status.Check(new Settings { GameDir = game });
        var check = Assert.Single(report.Checks, c => c.Id == "ffmpeg");
        if (check.Status == CheckStatus.Ok) return; // a bundled copy next to the test host
        Assert.Equal("download_ffmpeg", check.Action);
        Assert.Contains("nr ffmpeg install", check.Fix);
        Assert.Contains("258 MB", check.Fix);
    }

    private sealed class SyncProgress(Action<FfmpegProgress> report) : IProgress<FfmpegProgress>
    {
        public void Report(FfmpegProgress value) => report(value);
    }
}

/// <summary>nr ffmpeg status|install and the ffmpeg step of nr install, against a fake download.</summary>
public class FfmpegCliTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeFfmpeg fake = new();
    private readonly FakeHttp http;
    private readonly NcEnvironment env;
    private readonly string game;

    public FfmpegCliTests()
    {
        game = dir.FakeGame();
        http = fake.Serve();
        env = new NcEnvironment(new AppPaths(dir.Combine("home", "config"), dir.Combine("home", "data")),
            new GameLauncher(() => false, () => true, _ => throw new InvalidOperationException("tests never start the game")),
            httpHandler: http)
        {
            GameRunning = () => false,
            FfmpegPackage = fake.Package,
        };
    }

    public void Dispose() => dir.Dispose();

    private (int Code, JsonObject Json, List<JsonObject> Progress) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var code = Program.Run(args.Append("--json").ToArray(), env, stdout, new StringWriter());
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var progress = lines.Where(l => l.StartsWith("{\"type\":\"progress\"")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        var result = JsonNode.Parse(string.Join('\n', lines.Where(l => !l.StartsWith("{\"type\":\"progress\""))))!.AsObject();
        return (code, result, progress);
    }

    [Fact]
    public void StatusThenInstall()
    {
        var (code, before, _) = Run("ffmpeg", "status", "--game-dir", game);
        Assert.Equal(0, code);
        Assert.Equal(FfmpegPackage.ExePath(game), before["data"]!["shared_path"]!.GetValue<string>());
        Assert.False(before["data"]!["shared_present"]!.GetValue<bool>());
        Assert.Equal(FakeFfmpeg.Url, before["data"]!["pinned"]!["url"]!.GetValue<string>());

        var (installCode, installed, progress) = Run("ffmpeg", "install", "--game-dir", game);
        Assert.Equal(0, installCode);
        Assert.True(installed["ok"]!.GetValue<bool>());
        Assert.Equal("shared", installed["data"]!["source"]!.GetValue<string>());
        Assert.True(installed["data"]!["matches_pin"]!.GetValue<bool>());
        Assert.Contains(progress, p => p["stage"]!.GetValue<string>() == "download");
        Assert.Contains(progress, p => p["stage"]!.GetValue<string>() == "verify");
        Assert.True(File.Exists(FfmpegPackage.ExePath(game)));
    }

    [Fact]
    public void InstallFailureIsAJsonError()
    {
        http.Offline = true;
        var (code, json, _) = Run("ffmpeg", "install", "--game-dir", game);
        Assert.Equal(1, code);
        Assert.Equal("ffmpeg_download_failed", json["error"]!["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(json["error"]!["fix"]!.GetValue<string>()));
    }

    private (string Plugins, string Zip) ModFiles()
    {
        var plugins = dir.Combine("plugins");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "NuclearReplay.dll"), "r");
        var zip = dir.Combine("bepinex.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var name in new[] { "winhttp.dll", "doorstop_config.ini", "BepInEx/core/BepInEx.dll" })
                using (var w = new StreamWriter(archive.CreateEntry(name).Open())) w.Write(name);
        return (plugins, zip);
    }

    [Fact]
    public void InstallDownloadsFfmpegAndPointsTheViewerAtIt()
    {
        var (plugins, zip) = ModFiles();
        var (code, json, _) = Run("install", "--game-dir", game, "--from", plugins, "--bepinex", zip, "--no-proton-fix");

        Assert.Equal(0, code);
        Assert.Equal("installed", json["data"]!["ffmpeg"]!["action"]!.GetValue<string>());
        Assert.True(File.Exists(FfmpegPackage.ExePath(game)));
        AssertViewerPointsAtSharedFfmpeg();
    }

    [Fact]
    public void InstallStillSucceedsWhenTheDownloadFails()
    {
        http.Offline = true;
        var (plugins, zip) = ModFiles();
        var (code, json, _) = Run("install", "--game-dir", game, "--from", plugins, "--bepinex", zip, "--no-proton-fix");

        Assert.Equal(0, code);
        Assert.Equal("failed", json["data"]!["ffmpeg"]!["action"]!.GetValue<string>());
        Assert.Equal("ffmpeg_download_failed", json["data"]!["ffmpeg"]!["error"]!["code"]!.GetValue<string>());
        Assert.Contains(json["data"]!["summary"]!.AsArray(), l => l!.GetValue<string>().Contains("nr ffmpeg install"));
        Assert.True(File.Exists(Path.Combine(ModInstaller.PluginDir(game), "NuclearReplay.dll")));
        // The viewer still learns the shared location, so a later download needs no reinstall.
        AssertViewerPointsAtSharedFfmpeg();
    }

    // Under Proton (Linux) the config holds the Z:\ form of the path.
    private void AssertViewerPointsAtSharedFfmpeg() =>
        Assert.Contains(File.ReadAllLines(ReplayConfig.FilePath(game)),
            l => l.StartsWith("FfmpegPath = ") && l.Replace('\\', '/').EndsWith(game.Replace('\\', '/') + "/NuclearReplay/tools/ffmpeg/ffmpeg.exe"));

    [Fact]
    public void NoFfmpegSkipsTheDownload()
    {
        var (plugins, zip) = ModFiles();
        var (code, json, _) = Run("install", "--game-dir", game, "--from", plugins, "--bepinex", zip, "--no-proton-fix", "--no-ffmpeg");

        Assert.Equal(0, code);
        Assert.Equal("skipped", json["data"]!["ffmpeg"]!["action"]!.GetValue<string>());
        Assert.Empty(http.Requests);
    }
}
