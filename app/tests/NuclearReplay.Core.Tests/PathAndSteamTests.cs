using NuclearReplay.Core.Game;
using NuclearReplay.Core.Steam;

namespace NuclearReplay.Core.Tests;

public class PathMapperTests
{
    [Theory]
    [InlineData("/home/me/Videos/Nuclear Replay/clip 1.mp4", @"Z:\home\me\Videos\Nuclear Replay\clip 1.mp4")]
    [InlineData("/mnt/games/SteamLibrary/steamapps/common/Nuclear Option/NuclearReplay/demos/20261004T021932Z-Escalation.ncdemo",
        @"Z:\mnt\games\SteamLibrary\steamapps\common\Nuclear Option\NuclearReplay\demos\20261004T021932Z-Escalation.ncdemo")]
    [InlineData("/", @"Z:\")]
    [InlineData(@"C:\already\windows", @"C:\already\windows")]
    [InlineData("relative/path", "relative/path")]
    public void LinuxToWine(string linux, string wine) => Assert.Equal(wine, PathMapper.LinuxToWine(linux));

    [Fact]
    public void WineToLinuxUsesDriveMap()
    {
        var drives = new Dictionary<char, string> { ['z'] = "/", ['s'] = "/mnt/games/SteamLibrary", ['c'] = "/pfx/drive_c/" };
        Assert.Equal("/mnt/games/SteamLibrary/steamapps/common/Nuclear Option/NuclearReplay/demos",
            PathMapper.WineToLinux(@"S:\steamapps\common\Nuclear Option\NuclearReplay\demos", drives));
        Assert.Equal("/home/me/x.json", PathMapper.WineToLinux(@"Z:\home\me\x.json", drives));
        Assert.Equal("/home/me/x.json", PathMapper.WineToLinux(@"z:/home/me/x.json", drives));
        Assert.Equal("/pfx/drive_c/users", PathMapper.WineToLinux(@"C:\users", drives));
        Assert.Equal("/mnt/games/SteamLibrary", PathMapper.WineToLinux(@"S:\", drives));
        Assert.Null(PathMapper.WineToLinux(@"Q:\nowhere", drives));
        Assert.Null(PathMapper.WineToLinux("/not/a/drive", drives));
    }

    [Fact]
    public void RoundTripThroughZ()
    {
        var mapper = new PathMapper(proton: true);
        var host = "/tmp/a b/c.ncdemo";
        Assert.Equal(host, mapper.ToHostPath(mapper.ToGamePath(host)));
    }

    [Fact]
    public void WindowsMapperIsIdentity()
    {
        var mapper = new PathMapper(proton: false);
        Assert.Equal(@"S:\x", mapper.ToHostPath(@"S:\x"));
        Assert.Equal(Path.GetFullPath("x.json"), mapper.ToGamePath("x.json"));
    }

    [Fact]
    public void ReadsDosDeviceLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        var dosdevices = dir.Combine("compat", "pfx", "dosdevices");
        Directory.CreateDirectory(dosdevices);
        Directory.CreateDirectory(dir.Combine("compat", "pfx", "drive_c"));
        File.CreateSymbolicLink(Path.Combine(dosdevices, "c:"), "../drive_c");
        File.CreateSymbolicLink(Path.Combine(dosdevices, "s:"), "/mnt/games/SteamLibrary");
        File.CreateSymbolicLink(Path.Combine(dosdevices, "d::"), "/dev/sda"); // raw device entries are ignored
        var map = PathMapper.ReadDosDevices(dir.Combine("compat"));
        Assert.Equal("/mnt/games/SteamLibrary", map['s']);
        Assert.Equal(dir.Combine("compat", "pfx", "drive_c"), map['c']);
        Assert.Equal("/", map['z']);
        Assert.False(map.ContainsKey('d'));
    }
}

public class VdfTests
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"2919812231714707095"
        		"apps"
        		{
        			"228980"		"493115473"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"apps"
        		{
        			"2168680"		"2418833410"
        			"1250410"		"1"
        		}
        	}
        }
        """;

    [Fact]
    public void ParsesCurrentLibraryFolders()
    {
        var libs = SteamLocator.ParseLibraryFolders(Vdf.Parse(LibraryFolders)).ToList();
        Assert.Equal(2, libs.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", libs[0].Path);
        Assert.Equal(@"D:\SteamLibrary", libs[1].Path);
        Assert.Contains("2168680", libs[1].Apps);
        Assert.DoesNotContain("2168680", libs[0].Apps);
    }

    [Fact]
    public void ParsesOldLibraryFoldersFormat()
    {
        const string old = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1561832478"
            	"ContentStatsID"		"-158337411110787451"
            	"1"		"D:\\Games\\Steam"
            	"2"		"/mnt/ssd/SteamLibrary"
            }
            """;
        var libs = SteamLocator.ParseLibraryFolders(Vdf.Parse(old)).Select(l => l.Path).ToList();
        Assert.Equal(new[] { @"D:\Games\Steam", "/mnt/ssd/SteamLibrary" }, libs);
    }

    [Fact]
    public void ToleratesCommentsConditionalsAndUnquotedTokens()
    {
        var root = Vdf.Parse("""
            // comment
            AppState
            {
                appid 2168680 // trailing
                "installdir" "Nuclear Option" [$WIN32]
                "nested" { "k" "v" }
            }
            """);
        var state = root["appstate"]!; // keys are case-insensitive
        Assert.Equal("2168680", state.GetString("appid"));
        Assert.Equal("Nuclear Option", state.GetString("installdir"));
        Assert.Equal("v", state.Path("nested", "k")!.Value);
    }

    [Fact]
    public void ReadsLaunchOptionsFromLocalConfig()
    {
        var root = Vdf.Parse("""
            "UserLocalConfigStore"
            {
            	"Software"
            	{
            		"Valve"
            		{
            			"Steam"
            			{
            				"apps"
            				{
            					"2168680"
            					{
            						"LastPlayed"		"1791166306"
            						"LaunchOptions"		"WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"
            					}
            				}
            			}
            		}
            	}
            }
            """);
        Assert.Equal("WINEDLLOVERRIDES=\"winhttp=n,b\" %command%", SteamLaunchOptions.FromLocalConfig(root, "2168680"));
        Assert.Null(SteamLaunchOptions.FromLocalConfig(root, "1"));
    }

    [Fact]
    public void LocatesGameThroughLibrariesAndManifest()
    {
        using var dir = new TempDir();
        var steamRoot = dir.Combine("Steam");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        var library = dir.Combine("Games");
        dir.Write("Steam/steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
              "0" { "path" "{{steamRoot.Replace("\\", "\\\\")}}" "apps" { "1" "1" } }
              "1" { "path" "{{library.Replace("\\", "\\\\")}}" "apps" { "2168680" "1" } }
            }
            """);
        dir.Write("Games/steamapps/appmanifest_2168680.acf", "\"AppState\" { \"appid\" \"2168680\" \"installdir\" \"NO Custom\" \"buildid\" \"24724372\" }");
        var game = dir.Combine("Games", "steamapps", "common", "NO Custom");
        Directory.CreateDirectory(Path.Combine(game, "NuclearOption_Data"));
        File.WriteAllText(Path.Combine(game, "NuclearOption.exe"), "");

        var locator = new SteamLocator();
        var libs = locator.ReadLibraries(steamRoot);
        Assert.Contains(libs, l => l.Path == library && l.AppIds.Contains("2168680"));

        var found = new GameLocator(locator).Locate(new Settings { SteamDir = steamRoot });
        Assert.NotNull(found);
        Assert.Equal(game, found!.GameDir);
        Assert.Equal("steam_library", found.Source);
        Assert.Equal("24724372", found.BuildId);
        Assert.Equal(library, found.LibraryDir);
    }

    [Fact]
    public void ManualGameDirMustContainTheGame()
    {
        using var dir = new TempDir();
        Assert.Null(new GameLocator().Locate(new Settings { GameDir = dir.Path }));
        var game = dir.FakeGame();
        var found = new GameLocator().Locate(new Settings { GameDir = game });
        Assert.Equal("manual", found!.Source);
    }

    [Fact]
    public void ProtonCheckTrustsAGameThatLoadedTheMod()
    {
        if (OperatingSystem.IsWindows()) return; // Proton only exists on Linux
        using var tmp = new TempDir();
        var game = tmp.FakeGame();
        var home = tmp.Combine("home");
        Directory.CreateDirectory(home);
        var status = new StatusService(new SteamLocator(Directory.Exists, File.Exists, File.ReadAllText, home), () => false, () => true);
        var settings = new Settings { GameDir = game };
        Assert.Equal(CheckStatus.Warning, status.Check(settings).Checks.Single(c => c.Id == "proton_loader").Status);

        // A wrapper or launcher set the override: the mod log proves BepInEx loads.
        tmp.Write(Path.Combine(game, "BepInEx", "LogOutput.log"), "[Info   :   BepInEx] Loading [Nuclear Replay 0.4.1]\n");
        var proton = status.Check(settings).Checks.Single(c => c.Id == "proton_loader");
        Assert.Equal(CheckStatus.Ok, proton.Status);
        Assert.Null(proton.Fix);
    }
}
