using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Game;
using NuclearReplay.Shared;

namespace NuclearReplay.Core.Tests;

/// <summary>
/// The plugins' GUIDs changed twice: local.nuclearoption.context* (up to 0.5.0-beta.1), talamander.nuclearcontext* (Nuclear
/// Context betas) and now talamander.nuclearreplay.recorder / .player, both in NuclearReplay.dll. Existing installs, their
/// BepInEx configs and demos must keep working.
/// </summary>
public class PluginIdTests
{
    [Fact]
    public void GuidsAreTheNewOnesAndEveryGenerationIsOurs()
    {
        Assert.Equal("talamander.nuclearreplay.recorder", PluginIds.Recorder);
        Assert.Equal("talamander.nuclearreplay.player", PluginIds.Player);
        foreach (var g in new[] { "talamander.nuclearreplay.recorder", "talamander.nuclearcontext", "local.nuclearoption.context" })
            Assert.True(PluginIds.IsRecorder(g), g);
        foreach (var g in new[] { "talamander.nuclearreplay.player", "talamander.nuclearcontext.replay", "local.nuclearoption.context.replay" })
            Assert.True(PluginIds.IsPlayer(g), g);
        Assert.False(PluginIds.IsRecorder(PluginIds.Player));
        Assert.False(PluginIds.IsPlayer("local.nuclearoption.context.playback"));
        Assert.Equal(new[] { "talamander.nuclearcontext", "local.nuclearoption.context", "talamander.nuclearcontext.replay", "local.nuclearoption.context.replay" },
            PluginIds.AllLegacy().ToArray());
    }

    [Fact]
    public void CopyLegacyConfigCopiesOnceAndKeepsTheOldFile()
    {
        using var dir = new TempDir();
        var config = dir.Combine("BepInEx", "config");
        var legacy = dir.Write(Path.Combine(config, "local.nuclearoption.context.cfg"), "[Demo]\nEnabled = false\n");
        var current = Path.Combine(config, "talamander.nuclearreplay.recorder.cfg");

        Assert.Equal("local.nuclearoption.context.cfg", PluginIds.CopyLegacyConfig(config, PluginIds.LegacyRecorders, PluginIds.Recorder));
        Assert.Equal("[Demo]\nEnabled = false\n", File.ReadAllText(current));
        Assert.Equal("[Demo]\nEnabled = false\n", File.ReadAllText(legacy));

        // The new file now exists and wins, also after the player changes it.
        File.WriteAllText(current, "[Demo]\nEnabled = true\n");
        Assert.Null(PluginIds.CopyLegacyConfig(config, PluginIds.LegacyRecorders, PluginIds.Recorder));
        Assert.Equal("[Demo]\nEnabled = true\n", File.ReadAllText(current));
        Assert.Equal(2, Directory.GetFiles(config).Length);
    }

    [Fact]
    public void CopyLegacyConfigTakesTheNewestOldName()
    {
        using var dir = new TempDir();
        var config = dir.Combine("BepInEx", "config");
        var oldest = dir.Write(Path.Combine(config, "local.nuclearoption.context.replay.cfg"), "oldest");
        var context = dir.Write(Path.Combine(config, "talamander.nuclearcontext.replay.cfg"), "nuclear context");

        Assert.Equal("talamander.nuclearcontext.replay.cfg", PluginIds.CopyLegacyConfig(config, PluginIds.LegacyPlayers, PluginIds.Player));
        Assert.Equal("nuclear context", File.ReadAllText(Path.Combine(config, "talamander.nuclearreplay.player.cfg")));
        Assert.Equal("oldest", File.ReadAllText(oldest));
        Assert.Equal("nuclear context", File.ReadAllText(context));
    }

    [Fact]
    public void CopyLegacyConfigDoesNothingWithoutTheOldFile()
    {
        using var dir = new TempDir();
        var config = dir.Combine("config");
        Assert.Null(PluginIds.CopyLegacyConfig(config, PluginIds.LegacyPlayers, PluginIds.Player));
        Assert.False(Directory.Exists(config));
    }

    [Fact]
    public void ExistingConfigPrefersTheNewName()
    {
        using var dir = new TempDir();
        var config = dir.Combine("config");
        Directory.CreateDirectory(config);
        var current = Path.Combine(config, "talamander.nuclearreplay.player.cfg");
        var oldest = Path.Combine(config, "local.nuclearoption.context.replay.cfg");
        var context = Path.Combine(config, "talamander.nuclearcontext.replay.cfg");

        Assert.Equal(current, PluginIds.ExistingConfig(config, PluginIds.Player, PluginIds.LegacyPlayers)); // none
        File.WriteAllText(oldest, "");
        Assert.Equal(oldest, PluginIds.ExistingConfig(config, PluginIds.Player, PluginIds.LegacyPlayers));  // only the oldest
        File.WriteAllText(context, "");
        Assert.Equal(context, PluginIds.ExistingConfig(config, PluginIds.Player, PluginIds.LegacyPlayers)); // newest old one
        File.WriteAllText(current, "");
        Assert.Equal(current, PluginIds.ExistingConfig(config, PluginIds.Player, PluginIds.LegacyPlayers)); // new one
    }

    [Fact]
    public void RecorderDemoDirIsReadFromTheNewConfigFirst()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var config = Path.Combine(game, "BepInEx", "config");
        dir.Write(Path.Combine(config, "local.nuclearoption.context.cfg"), "[Demo]\nOutputDirectory = S:\\oldest\n");
        Assert.Equal(@"S:\oldest", DemoLibrary.ReadRecorderDemoDir(game));
        dir.Write(Path.Combine(config, "talamander.nuclearcontext.cfg"), "[Demo]\nOutputDirectory = S:\\old\n");
        Assert.Equal(@"S:\old", DemoLibrary.ReadRecorderDemoDir(game));
        dir.Write(Path.Combine(config, "talamander.nuclearreplay.recorder.cfg"), "[Demo]\nOutputDirectory = S:\\new\n");
        Assert.Equal(@"S:\new", DemoLibrary.ReadRecorderDemoDir(game));
    }

    [Fact]
    public void LegacyDefaultFoldersMoveToTheNewFolderAndOthersStay()
    {
        const string game = @"Z:\games\Nuclear Option";
        Assert.Equal(Path.Combine(game, "NuclearReplay"), ModFolders.ReplaceLegacyDefault(@"Z:\games\Nuclear Option\NuclearContext", game));
        Assert.Equal(Path.Combine(game, "NuclearReplay", "demos"), ModFolders.ReplaceLegacyDefault(@"z:/games/nuclear option/NuclearContext/demos/", game, "demos"));
        Assert.Null(ModFolders.ReplaceLegacyDefault(@"D:\my recordings", game, "demos"));
        Assert.Null(ModFolders.ReplaceLegacyDefault(@"Z:\games\Nuclear Option\NuclearContext", game, "demos"));
        Assert.Null(ModFolders.ReplaceLegacyDefault("", game));
    }

    [Fact]
    public void StatusSeesBothPluginsInOneDll()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var plugins = ModInstaller.PluginDir(game);
        FakePlugin.WriteMany(Path.Combine(plugins, "NuclearReplay.dll"), "0.5.0", PluginIds.Player, PluginIds.Recorder);

        var all = PluginInfoReader.ReadAll(Path.Combine(plugins, "NuclearReplay.dll"));
        Assert.Equal(2, all.Count);
        Assert.Equal(PluginIds.Recorder, PluginInfoReader.Read(Path.Combine(plugins, "NuclearReplay.dll"))!.Guid); // the recorder, wherever it is
        var checks = new StatusService(gameRunning: () => false, steamRunning: () => true).Check(new Settings { GameDir = game }).Checks;
        Assert.Contains("0.5.0 is installed", checks.Single(c => c.Id == "replay_plugin").Message);
        Assert.Contains("0.5.0 is installed", checks.Single(c => c.Id == "plugin").Message);
    }

    [Fact]
    public void StatusReportsAMissingPlayer()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        FakePlugin.Write(Path.Combine(ModInstaller.PluginDir(game), "NuclearReplay.dll"), "0.5.0", PluginIds.Recorder);
        var checks = new StatusService(gameRunning: () => false, steamRunning: () => true).Check(new Settings { GameDir = game }).Checks;
        Assert.Equal(CheckStatus.Error, checks.Single(c => c.Id == "replay_plugin").Status);
    }

    [Fact]
    public void ANommInstallIsRecognizedAndKeptOnUpdate()
    {
        // NOMM extracts the plugins zip into BepInEx/plugins/<NOMNOM id>/ and adds meta.json.
        using var dir = new TempDir();
        var game = dir.FakeGame();
        dir.Write(Path.Combine(game, "BepInEx", "core", "BepInEx.dll"), "bepinex");
        dir.Write(Path.Combine(game, "winhttp.dll"), "doorstop");
        dir.Write(Path.Combine(game, "doorstop_config.ini"), "[General]\nenabled = true");
        var plugins = ModInstaller.PluginDir(game);
        FakePlugin.Write(Path.Combine(plugins, "NuclearReplay.dll"), "0.5.0");
        var meta = dir.Write(Path.Combine(plugins, "meta.json"), """{"id":"NuclearReplay","artifact":{"version":"0.5.0"}}""");
        var checks = new StatusService(gameRunning: () => false, steamRunning: () => true).Check(new Settings { GameDir = game }).Checks;
        Assert.Contains("0.5.0 is installed", checks.Single(c => c.Id == "plugin").Message);

        var source = dir.Combine("source");
        FakePlugin.Write(Path.Combine(source, "NuclearReplay.dll"), "0.5.1");
        var result = new ModInstaller(() => false).Install(new InstallOptions
        {
            GameDir = game, PluginSourceDir = source, BackupRoot = dir.Combine("backups"),
        });
        Assert.True(result.Ok, result.Error);
        Assert.Equal("0.5.1", result.InstalledVersion);
        Assert.True(File.Exists(meta));
    }

    [Fact]
    public void AModTurnedOffInNommIsReported()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var disabled = ModInstaller.DisabledPluginDir(game);
        FakePlugin.Write(Path.Combine(disabled, "NuclearReplay.dll"), "0.5.0", PluginIds.Recorder);
        var check = new StatusService(gameRunning: () => false, steamRunning: () => true).Check(new Settings { GameDir = game })
            .Checks.Single(c => c.Id == "plugin");
        Assert.Equal(CheckStatus.Error, check.Status);
        Assert.Contains("turned off in a mod manager", check.Message);
        Assert.Contains("NOMM", check.Fix);
    }

    [Theory]
    [InlineData("src/Plugin.cs", "PluginIds.Recorder")]
    [InlineData("replay/src/ReplayPlugin.cs", "PluginIds.Player")]
    public void PluginsCopyTheOldConfigBeforeTheFirstBind(string file, string id)
    {
        var path = DemoReadingTests.FindRepoFile(file);
        if (path == null) return;
        var source = File.ReadAllText(path);
        Assert.Contains($"public const string Id = {id};", source);
        var awake = source.IndexOf("private void Awake()", StringComparison.Ordinal);
        var migrate = source.IndexOf("MigrateConfig();", awake, StringComparison.Ordinal);
        var bind = source.IndexOf("Config.Bind(", awake, StringComparison.Ordinal);
        Assert.True(awake > 0 && migrate > awake && migrate < bind, "MigrateConfig must run first in Awake");
        Assert.Contains("Config.Reload();", source);
    }

    [Fact]
    public void TheRecorderStaysOffNextToAnOldInstallAndThePlayerLoadsAfterIt()
    {
        var recorder = DemoReadingTests.FindRepoFile("src/Plugin.cs");
        var player = DemoReadingTests.FindRepoFile("replay/src/ReplayPlugin.cs");
        if (recorder == null || player == null) return;
        var r = File.ReadAllText(recorder);
        var awake = r.IndexOf("private void Awake()", StringComparison.Ordinal);
        var check = r.IndexOf("LegacyPluginLoaded() is string legacy", awake, StringComparison.Ordinal);
        var hooks = r.IndexOf("new Harmony(Id)", awake, StringComparison.Ordinal);
        Assert.True(check > awake && check < hooks, "the old-install check must come before any hook");
        Assert.Contains("[BepInDependency(PluginIds.Recorder, BepInDependency.DependencyFlags.SoftDependency)]", File.ReadAllText(player));
    }
}
