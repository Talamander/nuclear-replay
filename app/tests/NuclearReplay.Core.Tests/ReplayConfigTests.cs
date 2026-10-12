using NuclearReplay.Core.Game;

namespace NuclearReplay.Core.Tests;

/// <summary>The installer fills the replay plugin's [Viewer] settings so "Clip this" works in a game started from Steam.</summary>
public class ReplayConfigTests
{
    private static string Plugins(TempDir dir)
    {
        var source = dir.Combine("plugin");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "NuclearReplay.dll"), "recorder");
        return source;
    }

    // A game that already has BepInEx, so the tests need no loader zip.
    private static string GameWithBepInEx(TempDir dir)
    {
        var game = dir.FakeGame();
        dir.Write(Path.Combine(game, "BepInEx", "core", "BepInEx.dll"), "core");
        dir.Write(Path.Combine(game, "winhttp.dll"), "doorstop");
        dir.Write(Path.Combine(game, "doorstop_config.ini"), "[General]");
        return game;
    }

    private static InstallResult Install(TempDir dir, string game, ViewerDefaults? viewer) =>
        new ModInstaller(() => false).Install(new InstallOptions
            { GameDir = game, PluginSourceDir = Plugins(dir), BackupRoot = dir.Combine("backups"), Viewer = viewer });

    private static (ViewerDefaults Viewer, string Ffmpeg, string Clips) Defaults(TempDir dir, bool proton = false)
    {
        var ffmpeg = dir.Write("app/bundled/ffmpeg/ffmpeg.exe", "ffmpeg");
        var clips = dir.Combine("Videos", "Nuclear Replay");
        return (new ViewerDefaults(ffmpeg, clips, new PathMapper(proton)), ffmpeg, clips);
    }

    [Fact]
    public void FreshInstallCreatesTheReplayConfigWithFfmpegAndClipsFolder()
    {
        using var dir = new TempDir();
        var game = GameWithBepInEx(dir);
        var (viewer, ffmpeg, clips) = Defaults(dir);

        var result = Install(dir, game, viewer);

        Assert.True(result.Ok, result.Error);
        var cfg = Path.Combine(game, "BepInEx", "config", "talamander.nuclearreplay.player.cfg");
        Assert.Equal(cfg, ReplayConfig.FilePath(game));
        var lines = File.ReadAllLines(cfg);
        Assert.Contains("[Viewer]", lines);
        Assert.Contains("FfmpegPath = " + Path.GetFullPath(ffmpeg), lines);
        Assert.Contains("ClipsDirectory = " + Path.GetFullPath(clips), lines);
        Assert.Contains(result.Steps, s => s.Action == "installed" && s.Path == cfg);
        Assert.Contains("Told the in-game replay viewer where the video encoder (ffmpeg) is and where to save clips, so \"Clip this\" works when you start the game from Steam.",
            ModInstaller.Describe(result));
        Assert.Null(result.BackupDir); // nothing replaced
    }

    [Fact]
    public void ExistingConfigKeepsUserValuesAndFillsEmptyOnes()
    {
        using var dir = new TempDir();
        var game = GameWithBepInEx(dir);
        var (viewer, ffmpeg, _) = Defaults(dir);
        var original = string.Join("\r\n",
            "## Settings file was created by plugin Nuclear Replay Replay v0.5.0",
            "## Plugin GUID: talamander.nuclearreplay.player",
            "",
            "[Replay]",
            "",
            "AutoStartPath = ",
            "",
            "[Viewer]",
            "",
            "## Where \"Clip this\" saves videos.",
            "# Setting type: String",
            "# Default value: ",
            "ClipsDirectory = D:\\My Clips",
            "",
            "## ffmpeg used for viewer clips.",
            "# Setting type: String",
            "# Default value: ",
            "FfmpegPath = ",
            "",
            "Quality = 23",
            "",
            "");
        var cfg = dir.Write(Path.Combine(game, "BepInEx", "config", ReplayConfig.FileName), original);

        var result = Install(dir, game, viewer);

        Assert.True(result.Ok, result.Error);
        var text = File.ReadAllText(cfg);
        Assert.Equal(original.Replace("FfmpegPath = \r\n", "FfmpegPath = " + Path.GetFullPath(ffmpeg) + "\r\n"), text);
        Assert.Contains("ClipsDirectory = D:\\My Clips\r\n", text); // the player's own folder stays
        Assert.Contains("AutoStartPath = \r\n", text);              // other sections untouched
        var step = Assert.Single(result.Steps, s => s.Path == cfg);
        Assert.Equal("updated", step.Action);
        Assert.Equal(ReplayConfig.StepDetail + ": FfmpegPath", step.Detail);
        Assert.Contains("Told the in-game replay viewer where the video encoder (ffmpeg) is, so \"Clip this\" works when you start the game from Steam.",
            ModInstaller.Describe(result));
        Assert.Equal(original, File.ReadAllText(Path.Combine(result.BackupDir!, "config", ReplayConfig.FileName)));
    }

    [Fact]
    public void ConfigWithBothValuesSetIsLeftAlone()
    {
        using var dir = new TempDir();
        var game = GameWithBepInEx(dir);
        var (viewer, _, _) = Defaults(dir);
        var original = "[Viewer]\nClipsDirectory = D:\\Clips\nFfmpegPath = C:\\Tools\\ffmpeg.exe\n";
        var cfg = dir.Write(Path.Combine(game, "BepInEx", "config", ReplayConfig.FileName), original);

        var result = Install(dir, game, viewer);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(original, File.ReadAllText(cfg));
        Assert.Equal("kept", Assert.Single(result.Steps, s => s.Path == cfg).Action);
        Assert.Contains(ModInstaller.Describe(result), l => l.Contains("left as is") && l.Contains("replay viewer"));
        Assert.Null(result.BackupDir);
    }

    [Fact]
    public void AMissingFfmpegIsLeftOutButTheClipsFolderIsWritten()
    {
        using var dir = new TempDir();
        var game = GameWithBepInEx(dir);

        var result = Install(dir, game, new ViewerDefaults(dir.Combine("nowhere", "ffmpeg.exe"), dir.Combine("clips"), new PathMapper(false)));

        Assert.True(result.Ok, result.Error);
        var lines = File.ReadAllLines(ReplayConfig.FilePath(game));
        Assert.DoesNotContain(lines, l => l.StartsWith("FfmpegPath"));
        Assert.Contains("ClipsDirectory = " + dir.Combine("clips"), lines);
    }

    [Fact]
    public void TheSharedFfmpegIsWrittenBeforeItIsDownloaded()
    {
        using var dir = new TempDir();
        var game = GameWithBepInEx(dir);
        var settings = new Settings { OutputDir = dir.Combine("clips") };

        var viewer = ViewerDefaults.For(settings, new GameInstall { GameDir = game, Source = "manual" });
        var result = Install(dir, game, viewer);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(FfmpegInstaller.SharedPath(game), viewer.FfmpegPath);
        Assert.Contains("FfmpegPath = " + FfmpegInstaller.SharedPath(game), File.ReadAllLines(ReplayConfig.FilePath(game)));
        Assert.Contains(ModInstaller.Describe(result), l => l.Contains("where the video encoder (ffmpeg) is"));
    }

    [Fact]
    public void NothingToWriteSkipsTheStepQuietly()
    {
        using var dir = new TempDir();
        var game = GameWithBepInEx(dir);

        var none = Install(dir, game, new ViewerDefaults(null, null, new PathMapper(false)));

        Assert.True(none.Ok);
        Assert.False(File.Exists(ReplayConfig.FilePath(game)));
        Assert.DoesNotContain(none.Steps, s => s.Path.EndsWith(".cfg"));
        Assert.DoesNotContain(ModInstaller.Describe(none), l => l.Contains("replay viewer"));
    }

    [Fact]
    public void ProtonGetsWinePaths()
    {
        using var dir = new TempDir();
        var game = GameWithBepInEx(dir);
        var (viewer, ffmpeg, clips) = Defaults(dir, proton: true);

        Assert.True(Install(dir, game, viewer).Ok);

        var lines = File.ReadAllLines(ReplayConfig.FilePath(game));
        var expectedFfmpeg = PathMapper.LinuxToWine(Path.GetFullPath(ffmpeg));
        Assert.Contains("FfmpegPath = " + expectedFfmpeg, lines);
        Assert.Contains("ClipsDirectory = " + PathMapper.LinuxToWine(Path.GetFullPath(clips)), lines);
        if (!OperatingSystem.IsWindows())
        {
            Assert.StartsWith("Z:\\", expectedFfmpeg);
            Assert.EndsWith("\\app\\bundled\\ffmpeg\\ffmpeg.exe", expectedFfmpeg);
        }
    }

    [Fact]
    public void FillAddsMissingKeysInsideTheSectionOrAppendsTheSection()
    {
        var values = new List<KeyValuePair<string, string>> { new("FfmpegPath", "Z:\\a b\\ffmpeg.exe"), new("ClipsDirectory", "\\\\nas\\clips") };

        var filled = new List<string>();
        var text = ReplayConfig.Fill("[Viewer]\nQuality = 19\n\n[Zeta]\nX = 1\n", "Viewer", values, filled);
        // Drive paths are written as is (BepInEx keeps them verbatim); other paths are escaped the way BepInEx unescapes them.
        Assert.Equal("[Viewer]\nQuality = 19\nFfmpegPath = Z:\\a b\\ffmpeg.exe\nClipsDirectory = \\\\\\\\nas\\\\clips\n\n[Zeta]\nX = 1\n", text);
        Assert.Equal(new[] { "FfmpegPath", "ClipsDirectory" }, filled);

        filled.Clear();
        text = ReplayConfig.Fill("[Replay]\nAutoStartPath = \n", "Viewer", values.Take(1).ToList(), filled);
        Assert.Equal("[Replay]\nAutoStartPath = \n\n[Viewer]\n\nFfmpegPath = Z:\\a b\\ffmpeg.exe\n", text);

        filled.Clear();
        // An empty key in another section is not ours.
        text = ReplayConfig.Fill("[Other]\nFfmpegPath = \n[Viewer]\n# FfmpegPath = commented\nFfmpegPath = C:\\mine.exe\n", "Viewer", values.Take(1).ToList(), filled);
        Assert.Empty(filled);
        Assert.Equal("[Other]\nFfmpegPath = \n[Viewer]\n# FfmpegPath = commented\nFfmpegPath = C:\\mine.exe\n", text);
    }

    [Fact]
    public void OnlyTheOldConfigIsCopiedToTheNewNameThenFilled()
    {
        // An install from before the GUID change: the player's settings live in local.nuclearoption.context.replay.cfg.
        using var dir = new TempDir();
        var game = GameWithBepInEx(dir);
        var (viewer, ffmpeg, _) = Defaults(dir);
        var original = "[Replay]\nPauseKey = Space\n\n[Viewer]\nClipsDirectory = D:\\My Clips\nFfmpegPath = \nQuality = 23\n";
        var legacy = dir.Write(Path.Combine(game, "BepInEx", "config", "local.nuclearoption.context.replay.cfg"), original);

        var result = Install(dir, game, viewer);

        Assert.True(result.Ok, result.Error);
        var cfg = ReplayConfig.FilePath(game);
        Assert.Equal(original.Replace("FfmpegPath = \n", "FfmpegPath = " + Path.GetFullPath(ffmpeg) + "\n"), File.ReadAllText(cfg));
        Assert.Equal(original, File.ReadAllText(legacy)); // the old file stays as it was
        var step = Assert.Single(result.Steps, s => s.Path == cfg);
        Assert.Equal("installed", step.Action);
        Assert.Equal(ReplayConfig.StepDetail + ": FfmpegPath; other settings copied from local.nuclearoption.context.replay.cfg", step.Detail);
        Assert.Contains(ModInstaller.Describe(result), l => l.StartsWith("Told the in-game replay viewer where the video encoder"));
        Assert.Null(result.BackupDir);
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(cfg)!).Length); // no temporary file left behind
    }

    [Fact]
    public void OldConfigWithEverythingSetIsStillCopied()
    {
        using var dir = new TempDir();
        var game = GameWithBepInEx(dir);
        var (viewer, _, _) = Defaults(dir);
        var original = "[Viewer]\nClipsDirectory = D:\\Clips\nFfmpegPath = C:\\Tools\\ffmpeg.exe\n";
        dir.Write(Path.Combine(game, "BepInEx", "config", "talamander.nuclearcontext.replay.cfg"), original);

        var result = Install(dir, game, viewer);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(original, File.ReadAllText(ReplayConfig.FilePath(game)));
        Assert.Equal("kept", Assert.Single(result.Steps, s => s.Path == ReplayConfig.FilePath(game)).Action);
    }

    [Fact]
    public void NewConfigWinsOverTheOldOne()
    {
        using var dir = new TempDir();
        var game = GameWithBepInEx(dir);
        var (viewer, ffmpeg, clips) = Defaults(dir);
        var oldText = "[Viewer]\nClipsDirectory = D:\\Old\nFfmpegPath = C:\\Old\\ffmpeg.exe\n";
        var legacy = dir.Write(Path.Combine(game, "BepInEx", "config", "talamander.nuclearcontext.replay.cfg"), oldText);
        var cfg = dir.Write(Path.Combine(game, "BepInEx", "config", ReplayConfig.FileName), "[Replay]\nAutoStartPath = \n");

        Assert.True(Install(dir, game, viewer).Ok);

        var lines = File.ReadAllLines(cfg);
        Assert.Contains("FfmpegPath = " + Path.GetFullPath(ffmpeg), lines);
        Assert.Contains("ClipsDirectory = " + Path.GetFullPath(clips), lines);
        Assert.Equal(oldText, File.ReadAllText(legacy));
    }

    [Fact]
    public void KeysMatchTheReplayPluginSource()
    {
        // The installer writes the file the plugin's BepInEx config reads: <GUID>.cfg, [Viewer] FfmpegPath / ClipsDirectory.
        var source = DemoReadingTests.FindRepoFile("replay/src/ReplayPlugin.cs");
        var viewer = DemoReadingTests.FindRepoFile("replay/src/ReplayPlugin.Viewer.cs");
        if (source == null || viewer == null) return;
        Assert.Equal("talamander.nuclearreplay.player", ReplayConfig.PluginGuid);
        Assert.Contains("public const string Id = PluginIds.Player;", File.ReadAllText(source));
        var text = File.ReadAllText(viewer);
        Assert.Contains($"Config.Bind(\"{ReplayConfig.ViewerSection}\", \"{ReplayConfig.FfmpegKey}\", \"\"", text);
        Assert.Contains($"Config.Bind(\"{ReplayConfig.ViewerSection}\", \"{ReplayConfig.ClipsKey}\", \"\"", text);
    }
}
