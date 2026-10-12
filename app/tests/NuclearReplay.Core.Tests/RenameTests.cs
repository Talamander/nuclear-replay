using NuclearReplay.Core.Game;
using NuclearReplay.Shared;

namespace NuclearReplay.Core.Tests;

/// <summary>
/// The rename from Nuclear Context to Nuclear Replay: nr's settings carry over once, old folders are read but
/// never changed, and the ffmpeg an old install downloaded is found.
/// </summary>
public class RenameTests
{
    [Fact]
    public void SettingsAreCopiedOnceFromTheOldFolders()
    {
        using var dir = new TempDir();
        var legacy = new AppPaths(dir.Combine("old", "config"), dir.Combine("old", "data"));
        var current = new AppPaths(dir.Combine("new", "config"), dir.Combine("new", "data"));
        dir.Write(Path.Combine(legacy.ConfigDir, "settings.json"), """{"game_dir":"G:\\NO"}""");

        Assert.Equal(1, AppPaths.CopyLegacyState(legacy, current));
        Assert.Equal("""{"game_dir":"G:\\NO"}""", File.ReadAllText(current.SettingsFile));
        Assert.True(File.Exists(legacy.SettingsFile)); // the old files stay

        // Once the new settings exist, nothing is copied again.
        File.WriteAllText(current.SettingsFile, "{}");
        Assert.Equal(0, AppPaths.CopyLegacyState(legacy, current));
        Assert.Equal("{}", File.ReadAllText(current.SettingsFile));
    }

    [Fact]
    public void NoOldSettingsNoCopy()
    {
        using var dir = new TempDir();
        var legacy = new AppPaths(dir.Combine("old", "config"), dir.Combine("old", "data"));
        var current = new AppPaths(dir.Combine("new", "config"), dir.Combine("new", "data"));
        dir.Write(Path.Combine(legacy.DataDir, "renders.json"), "[]");
        Assert.Equal(0, AppPaths.CopyLegacyState(legacy, current));
        Assert.False(Directory.Exists(current.DataDir));
    }

    [Fact]
    public void FfmpegFromAnOldInstallIsFoundAndTheNewFolderWins()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        Assert.Null(FfmpegPackage.FindExe(game));
        var old = dir.Write(Path.Combine(game, "NuclearContext", "tools", "ffmpeg", "ffmpeg.exe"), "old");
        Assert.Equal(old, FfmpegPackage.FindExe(game));
        Assert.Equal((old, "shared"), StatusService.ResolveFfmpegSource(new Settings(), game));
        var current = dir.Write(FfmpegPackage.ExePath(game), "new");
        Assert.Equal(current, FfmpegPackage.FindExe(game));
        // Downloads always go to the new folder.
        Assert.StartsWith(Path.Combine(game, "NuclearReplay"), FfmpegPackage.InstallDir(game));
    }

    [Fact]
    public void OldRecordingFolderIsReadOnlyWithoutAChosenFolder()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var install = new GameInstall { GameDir = game, Source = "manual" };
        var primary = Demos.DemoLibrary.ResolveDemoDir(new Settings(), install);
        Assert.Equal(Path.Combine(game, "NuclearReplay", "demos"), primary);
        Assert.Empty(Demos.DemoLibrary.ExtraDemoDirs(new Settings(), install, primary));

        var legacy = Path.Combine(game, "NuclearContext", "demos");
        Directory.CreateDirectory(legacy);
        Assert.Equal(new[] { legacy }, Demos.DemoLibrary.ExtraDemoDirs(new Settings(), install, primary));
        Assert.Empty(Demos.DemoLibrary.ExtraDemoDirs(new Settings { DemoDir = dir.Combine("mine") }, install, dir.Combine("mine")));
        Assert.Empty(Demos.DemoLibrary.ExtraDemoDirs(new Settings(), install, legacy)); // the recorder still writes there
    }
}
