using System.Text.Json.Nodes;
using NuclearReplay.Cli;
using NuclearReplay.Core.Clips;
using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Game;
using NuclearReplay.Core.Render;
using NuclearReplay.Core.Shots;

namespace NuclearReplay.Core.Tests;

/// <summary>Clip planning in nr: camera styles, the command line, install and failure text.</summary>
public class ClipTests : IDisposable
{
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private static DemoEvent E(double t, string json) => DemoEvent.Parse(t, json)!;

    private static readonly List<DemoEvent> Events = new()
    {
        E(40, """{"event":"launch","shooter":{"id":169,"name":"Revoker","player":"Talamander"},"target":{"id":201,"name":"Ifrit"},"weapon":"AAM-29","missile":4021}"""),
        E(45, """{"event":"kill","victim":{"id":201,"name":"Ifrit"},"killer":{"id":169,"name":"Revoker","player":"Talamander"},"weapon":"AAM-29"}"""),
        E(70, """{"event":"kill","victim":{"id":305,"name":"APC"},"killer":{"id":169,"name":"Revoker","player":"Talamander"},"weapon":"Gun"}"""),
    };

    [Fact]
    public void KillPairsWithTheLaunchThatCausedIt()
    {
        var (launch, kill) = ShotPresets.Pair(Events[1], Events);
        Assert.Same(Events[0], launch);
        Assert.Same(Events[1], kill);
        Assert.Same(Events[1], ShotPresets.Pair(Events[0], Events).Kill);
        Assert.Null(ShotPresets.Pair(Events[2], Events).Launch); // gun kill: no missile
    }

    [Fact]
    public void VictimCutRidesTheMissileThenCutsToTheVictim()
    {
        var shots = ShotPresets.Build("victim_cut", Events[1], 35, 51, Events).Shots;
        Assert.Equal(("chase", "unit:169"), (shots[0].Camera, shots[0].Target));
        Assert.Equal(35, shots[0].T);
        Assert.Equal(("chase", "unit:4021"), (shots[1].Camera, shots[1].Target));
        Assert.Equal(40.4, shots[1].T, 3);
        Assert.Equal(("orbit", "unit:201"), (shots[2].Camera, shots[2].Target));
        Assert.Equal(44.9, shots[2].T, 3);
        Assert.True(ShotValidator.Validate(new ShotList { Shots = shots }.ToJson()).Valid);
    }

    [Fact]
    public void MissileFollowStaysOnTheMissile()
    {
        var shots = ShotPresets.Build("missile_follow", Events[0], 35, 51, Events).Shots;
        Assert.Contains(shots, s => s.Target == "unit:4021");
        Assert.DoesNotContain(shots, s => s.Camera == "orbit");
    }

    [Fact]
    public void EveryStyleBuildsValidShotsWithoutEvents()
    {
        foreach (var style in ShotPresets.CameraStyles)
        {
            var list = ShotPresets.Build(style.Id, null, 10, 20);
            Assert.True(ShotValidator.Validate(list.ToJson()).Valid, style.Id);
            Assert.All(list.Shots, s => Assert.InRange(s.T, 10, 20));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandLinesRoundTrip(bool windows)
    {
        var args = new List<string> { "render", "20261005T210314Z-05. Furball.ncdemo", "--name", "it's \"good\"", "--out", @"C:\Users\Pilot\Videos\Nuclear Replay", "--at", "61.5", "--shots", @"D:\" };
        var line = ClipCommand.Line(args, windows);
        Assert.StartsWith("nr render ", line);
        Assert.Equal(args, ClipCommand.Split(line));
    }

    [Fact]
    public void RenderArgsOnlyWriteWhatDiffersFromTheDefaults()
    {
        var plain = ClipCommand.RenderArgs(new ClipSpec { Demo = "/x/demos/a.ncdemo", EventIndex = 3, Style = "cinematic_chase" }, "/x/demos");
        Assert.Equal(new[] { "render", "a.ncdemo", "--event", "3", "--style", "cinematic_chase" }, plain);
        var full = ClipCommand.RenderArgs(new ClipSpec
        {
            Demo = "/elsewhere/b.ncdemo", At = 61.25, Pre = 2, Post = 9, Style = "orbit", Size = "vertical", Fps = 30, Hud = true,
            Encoder = "x264", Quality = 23, Name = "Clip",
        }, "/x/demos");
        Assert.Equal(new[] { "render", "/elsewhere/b.ncdemo", "--at", "61.25", "--pre", "2", "--post", "9", "--style", "orbit", "--size", "vertical",
            "--fps", "30", "--hud", "--encoder", "x264", "--quality", "23", "--name", "Clip" }, full);
    }

    [Fact]
    public void InstallSummaryIsPlainLanguage()
    {
        var result = new InstallResult
        {
            Ok = true, BackupDir = "/b",
            Steps = new()
            {
                new InstallStep("installed", "/g/winhttp.dll", "BepInEx"),
                new InstallStep("installed", "/g/BepInEx/core/BepInEx.dll", "BepInEx"),
                new InstallStep("updated", "/g/BepInEx/plugins/NuclearReplay/NuclearReplay.dll", "0.4.0 -> 0.4.1"),
                new InstallStep("unchanged", "/g/BepInEx/plugins/NuclearReplay/Example.dll", "0.5.0"),
                new InstallStep("moved", "/g/BepInEx/plugins/NuclearContext", ModInstaller.LegacyStepDetail),
                new InstallStep("skipped", "/c/pfx/user.reg", "Proton prefix not created yet"),
            },
        };
        var lines = ModInstaller.Describe(result);
        Assert.Equal("Installed the BepInEx 5 mod loader (2 files) into the game folder.", lines[0]);
        Assert.Contains("Updated NuclearReplay.dll (0.4.0 -> 0.4.1).", lines);
        Assert.Contains("Example.dll is already up to date (0.5.0).", lines);
        Assert.Contains(lines, l => l.StartsWith("Moved the old Nuclear Context plugins (BepInEx"));
        Assert.Contains(lines, l => l.StartsWith("The game's Proton prefix does not exist yet."));
        Assert.Equal("Saved the files it replaced in /b.", lines[^1]);
    }

    [Theory]
    [InlineData("The game did not start. Is Steam running?", "Start Steam")]
    [InlineData("The game closed before the clip was finished.", "Leave the game running")]
    [InlineData("ffmpeg exited with code 1", "x264")]
    [InlineData("Something odd", "nr diagnostics")]
    public void RenderFailuresComeWithAFix(string error, string fix) => Assert.Contains(fix, RenderFailure.Advice(error));

    [Fact]
    public void RenderFailureFixNamesTheModLog()
    {
        var game = new GameInstall { GameDir = "/g", Source = "manual" };
        var fix = RenderFailure.Fix(game, "/jobs/1", "lost");
        Assert.Contains(Path.Combine("/g", "BepInEx", "LogOutput.log"), fix);
        Assert.Contains("/jobs/1", fix);
    }
}
