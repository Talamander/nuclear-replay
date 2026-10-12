using NuclearReplay.Core.Game;

namespace NuclearReplay.Core.Tests;

public class VersionAndLogTests
{
    [Fact]
    public void AppVersionComesFromVersionProps()
    {
        var props = DemoReadingTests.FindRepoFile("version.props");
        Assert.NotNull(props);
        var text = File.ReadAllText(props!);
        var start = text.IndexOf("<NcVersion>", StringComparison.Ordinal) + "<NcVersion>".Length;
        var expected = text[start..text.IndexOf("</NcVersion>", StringComparison.Ordinal)];
        Assert.Equal(expected, AppVersion.Current);
    }

    [Theory]
    [InlineData("0.5.0-beta.1", "0.4.1", true)]
    [InlineData("0.5.0", "0.5.0-beta.1", true)]
    [InlineData("0.5.0-beta.2", "0.5.0-beta.1", true)]
    [InlineData("0.5.0-beta.10", "0.5.0-beta.2", true)]
    [InlineData("0.5.0-beta.1", "0.5.0-beta.1", false)]
    [InlineData("0.5.0-beta.1", "0.5.0", false)]
    [InlineData("0.4.1", "0.5.0-beta.1", false)]
    [InlineData("0.5.0-rc.1", "0.5.0-beta.3", true)]
    public void VersionComparison(string candidate, string current, bool newer) =>
        Assert.Equal(newer, StatusService.IsNewer(candidate, current));

    [Fact]
    public void LogRollsOverAndTails()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Combine("config"), dir.Combine("data"));
        Log.Init(paths, "test");
        var file = Path.Combine(Log.LogDir(paths), Log.FileName);
        var line = new string('x', 1000);
        for (int i = 0; i < 2600; i++) Log.Info($"{i:D5} {line}");
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(Log.Rolled(file, 1)));
        Assert.True(File.Exists(Log.Rolled(file, 2)));
        Assert.False(File.Exists(Log.Rolled(file, 3)));
        Assert.True(new FileInfo(Log.Rolled(file, 1)).Length <= Log.MaxBytes + 4096);
        var tail = Log.Tail(file, 3)!.Split(Environment.NewLine);
        Assert.Equal(3, tail.Length);
        Assert.Contains(tail, l => l.Contains("02599 "));
        Assert.Contains("[test ", tail[^1]);
    }

    [Fact]
    public void DiagnosticsIncludeStatusAndLogs()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        dir.Write(Path.Combine(game, "BepInEx", "LogOutput.log"), "[Info   :   BepInEx] Loading [Nuclear Replay 0.5.0]\n[Error  : Nuclear Replay Replay] boom\n");
        var paths = new AppPaths(dir.Combine("home", "config"), dir.Combine("home", "data"));
        var env = new NcEnvironment(paths, status: new StatusService(gameRunning: () => false, steamRunning: () => true));
        var settings = env.LoadSettings();
        settings.GameDir = game;
        env.SettingsStore.Save(settings);
        Directory.CreateDirectory(Log.LogDir(paths));
        File.WriteAllText(Path.Combine(Log.LogDir(paths), Log.FileName), "2026-10-05 app line one\n");

        var (path, text) = Diagnostics.Save(env);
        Assert.True(File.Exists(path));
        Assert.StartsWith(Diagnostics.DiagnosticsDir(paths), path);
        Assert.Contains(AppVersion.Current, text);
        Assert.Contains("\"checks\"", text);
        Assert.Contains("boom", text);
        Assert.Contains("app line one", text);
    }
}
