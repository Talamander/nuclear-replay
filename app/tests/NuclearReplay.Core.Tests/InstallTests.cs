using System.IO.Compression;
using NuclearReplay.Core.Game;

namespace NuclearReplay.Core.Tests;

public class InstallTests
{
    private static string BepInExZip(TempDir dir, string? wrapper = null, bool evil = false)
    {
        var path = dir.Combine("BepInEx_win_x64_5.4.23.5.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            var entry = zip.CreateEntry((wrapper != null ? wrapper + "/" : "") + name);
            using var w = new StreamWriter(entry.Open());
            w.Write(content);
        }
        Add("winhttp.dll", "doorstop");
        Add("doorstop_config.ini", "[General]\nenabled = true");
        Add(".doorstop_version", "4.3.0");
        Add("changelog.txt", "changes");
        Add("BepInEx/core/BepInEx.dll", "bepinex core");
        Add("BepInEx/core/0Harmony.dll", "harmony");
        Add("BepInEx/config/BepInEx.cfg", "[Logging]\nfrom zip");
        if (evil) Add("../../escape.txt", "nope");
        return path;
    }

    private static string Plugins(TempDir dir, string version)
    {
        var source = dir.Combine("plugin-" + version);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "NuclearReplay.dll"), "recorder " + version);
        return source;
    }

    [Fact]
    public void InstallsLoaderAndPluginsIntoFreshGame()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var result = new ModInstaller(() => false).Install(new InstallOptions
        {
            GameDir = game, PluginSourceDir = Plugins(dir, "1"), BepInExZip = BepInExZip(dir), BackupRoot = dir.Combine("backups"),
        });
        Assert.True(result.Ok, result.Error);
        Assert.True(ModInstaller.BepInExInstalled(game));
        Assert.Equal("recorder 1", File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "NuclearReplay", "NuclearReplay.dll")));
        Assert.Single(Directory.GetFiles(Path.Combine(game, "BepInEx", "plugins", "NuclearReplay")));
        Assert.Null(result.BackupDir); // nothing replaced
        Assert.Empty(Directory.EnumerateFiles(game, "*.next", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(game, "*.nc-next", SearchOption.AllDirectories));
    }

    [Fact]
    public void MovesTheOldNuclearContextPluginFolderIntoTheBackup()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var legacy = ModInstaller.LegacyPluginDir(game);
        dir.Write(Path.Combine(legacy, "NuclearContext.dll"), "old recorder");
        dir.Write(Path.Combine(legacy, "NuclearContext.Replay.dll"), "old replay");
        dir.Write(Path.Combine(legacy, "meta.json"), "{}");
        var recordings = dir.Write(Path.Combine(game, "NuclearContext", "demos", "a.ncdemo"), "demo");

        var result = new ModInstaller(() => false).Install(new InstallOptions
        {
            GameDir = game, PluginSourceDir = Plugins(dir, "1"), BepInExZip = BepInExZip(dir), BackupRoot = dir.Combine("backups"),
        });

        Assert.True(result.Ok, result.Error);
        Assert.False(Directory.Exists(legacy));
        var moved = Path.Combine(result.BackupDir!, "plugins-legacy", "NuclearContext");
        Assert.Equal("old recorder", File.ReadAllText(Path.Combine(moved, "NuclearContext.dll")));
        Assert.Equal("old replay", File.ReadAllText(Path.Combine(moved, "NuclearContext.Replay.dll")));
        Assert.True(File.Exists(Path.Combine(moved, "meta.json")));
        Assert.Equal("demo", File.ReadAllText(recordings)); // recordings stay where they are
        Assert.Equal("moved", Assert.Single(result.Steps, s => s.Detail == ModInstaller.LegacyStepDetail).Action);
        Assert.Contains(ModInstaller.Describe(result), l => l.StartsWith("Moved the old Nuclear Context plugins"));
        Assert.Equal("recorder 1", File.ReadAllText(Path.Combine(ModInstaller.PluginDir(game), "NuclearReplay.dll")));
    }

    [Fact]
    public void UpdateBacksUpPreviousPluginAndKeepsExistingLoader()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var installer = new ModInstaller(() => false);
        var zip = BepInExZip(dir);
        Assert.True(installer.Install(new InstallOptions { GameDir = game, PluginSourceDir = Plugins(dir, "1"), BepInExZip = zip, BackupRoot = dir.Combine("b") }).Ok);
        File.WriteAllText(Path.Combine(game, "BepInEx", "config", "BepInEx.cfg"), "user edited");

        var update = installer.Install(new InstallOptions { GameDir = game, PluginSourceDir = Plugins(dir, "2"), BepInExZip = zip, BackupRoot = dir.Combine("b") });
        Assert.True(update.Ok);
        Assert.Contains(update.Steps, s => s.Action == "kept" && s.Path.EndsWith("BepInEx"));
        Assert.Equal("recorder 2", File.ReadAllText(Path.Combine(ModInstaller.PluginDir(game), "NuclearReplay.dll")));
        Assert.NotNull(update.BackupDir);
        Assert.Equal("recorder 1", File.ReadAllText(Path.Combine(update.BackupDir!, "plugins", "NuclearReplay.dll")));
        Assert.Equal("user edited", File.ReadAllText(Path.Combine(game, "BepInEx", "config", "BepInEx.cfg")));
        // Backups never land inside BepInEx/plugins (BepInEx would load them).
        Assert.Single(Directory.EnumerateFiles(Path.Combine(game, "BepInEx", "plugins"), "*.dll", SearchOption.AllDirectories));

        var again = installer.Install(new InstallOptions { GameDir = game, PluginSourceDir = Plugins(dir, "2"), BackupRoot = dir.Combine("b") });
        Assert.All(again.Steps.Where(s => s.Path.EndsWith(".dll")), s => Assert.Equal("unchanged", s.Action));
    }

    [Fact]
    public void ExistingBepInExConfigIsNotOverwrittenOnLoaderInstall()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        dir.Write(Path.Combine(game, "BepInEx", "config", "BepInEx.cfg"), "mine");
        var result = new ModInstaller(() => false).Install(new InstallOptions
            { GameDir = game, PluginSourceDir = Plugins(dir, "1"), BepInExZip = BepInExZip(dir, wrapper: "BepInEx_x64"), BackupRoot = dir.Combine("b") });
        Assert.True(result.Ok, result.Error);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(game, "BepInEx", "config", "BepInEx.cfg")));
        Assert.True(File.Exists(Path.Combine(game, "winhttp.dll"))); // wrapper folder stripped
    }

    [Fact]
    public void RefusesWhileGameRunsOrWithoutFiles()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var running = new ModInstaller(() => true).Install(new InstallOptions { GameDir = game, PluginSourceDir = Plugins(dir, "1"), BepInExZip = BepInExZip(dir), BackupRoot = dir.Combine("b") });
        Assert.False(running.Ok);
        Assert.Contains("running", running.Error);
        Assert.False(File.Exists(Path.Combine(game, "winhttp.dll")));

        var noZip = new ModInstaller(() => false).Install(new InstallOptions { GameDir = game, PluginSourceDir = Plugins(dir, "1"), BepInExZip = dir.Combine("missing.zip"), BackupRoot = dir.Combine("b") });
        Assert.False(noZip.Ok);
        Assert.NotNull(noZip.Fix);

        var notGame = new ModInstaller(() => false).Install(new InstallOptions { GameDir = dir.Path, PluginSourceDir = Plugins(dir, "1"), BackupRoot = dir.Combine("b") });
        Assert.False(notGame.Ok);
    }

    [Fact]
    public void RejectsZipWithPathTraversal()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var result = new ModInstaller(() => false).Install(new InstallOptions
            { GameDir = game, PluginSourceDir = Plugins(dir, "1"), BepInExZip = BepInExZip(dir, evil: true), BackupRoot = dir.Combine("b") });
        Assert.False(result.Ok);
        Assert.False(File.Exists(dir.Combine("escape.txt")));
    }

    [Fact]
    public void ProtonPrefixGetsWinhttpOverride()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        var compat = dir.Combine("compat");
        var userReg = dir.Write("compat/pfx/user.reg", "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\DllOverrides] 1790391448\n#time=1dd4d62c427cb46\n\"d3d11\"=\"native\"\n\n[Software\\\\Other] 1\n\"winhttp\"=\"builtin\"\n");
        var result = new ModInstaller(() => false).Install(new InstallOptions
            { GameDir = game, PluginSourceDir = Plugins(dir, "1"), BepInExZip = BepInExZip(dir), BackupRoot = dir.Combine("b"), CompatDataDir = compat });
        Assert.True(result.Ok, result.Error);
        var text = File.ReadAllText(userReg);
        Assert.True(ProtonPrefix.HasWinhttpOverride(text));
        Assert.Contains("#time=1dd4d62c427cb46\n\"winhttp\"=\"native,builtin\"\n\"d3d11\"=\"native\"", text);
        Assert.True(File.Exists(Path.Combine(result.BackupDir!, "user.reg")));
    }

    [Fact]
    public void ProtonOverrideEditsAreIdempotent()
    {
        var none = "WINE REGISTRY Version 2\n";
        Assert.False(ProtonPrefix.HasWinhttpOverride(none));
        var added = ProtonPrefix.WithWinhttpOverride(none, 123);
        Assert.True(ProtonPrefix.HasWinhttpOverride(added));
        Assert.Contains("[Software\\\\Wine\\\\DllOverrides] 123", added);
        Assert.Equal(added, ProtonPrefix.WithWinhttpOverride(added, 456));

        var disabled = "[Software\\\\Wine\\\\DllOverrides] 1\n\"winhttp\"=\"\"\n";
        Assert.False(ProtonPrefix.HasWinhttpOverride(disabled));
        var fixedText = ProtonPrefix.WithWinhttpOverride(disabled, 1);
        Assert.True(ProtonPrefix.HasWinhttpOverride(fixedText));
        Assert.Single(fixedText.Split('\n'), l => l.StartsWith("\"winhttp\""));
    }

    [Fact]
    public void ReadsBepInPluginVersionFromRealPluginWhenPresent()
    {
        // The repository's own build output, when it exists (scripts/build.sh).
        var dll = DemoReadingTests.FindRepoFile("dist/NuclearReplay.dll");
        if (dll == null) return;
        var info = PluginInfoReader.Read(dll);
        Assert.NotNull(info);
        // dist/ may hold a build from before the GUID change; every generation's GUID is ours.
        Assert.True(NuclearReplay.Shared.PluginIds.IsRecorder(info!.Guid), info.Guid);
        // Since the rename one DLL holds both plugins.
        if (info.Guid == NuclearReplay.Shared.PluginIds.Recorder)
            Assert.Contains(PluginInfoReader.ReadAll(dll), p => p.Guid == NuclearReplay.Shared.PluginIds.Player);
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", info.Version);
    }

    [Fact]
    public void PluginInfoReaderIgnoresNonAssemblies()
    {
        using var dir = new TempDir();
        Assert.Null(PluginInfoReader.Read(dir.Write("x.dll", "not a dll")));
        Assert.Null(PluginInfoReader.Read(dir.Combine("missing.dll")));
        Assert.Null(PluginInfoReader.Read(typeof(InstallTests).Assembly.Location)); // managed, but no [BepInPlugin]
    }
}
