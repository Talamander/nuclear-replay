using NuclearReplay.Core.Steam;

namespace NuclearReplay.Core.Game;

/// <summary>Where Nuclear Option is installed and how it runs.</summary>
public sealed record GameInstall
{
    public required string GameDir { get; init; }
    /// <summary>Steam library folder that contains steamapps/common/&lt;game&gt;, when known.</summary>
    public string? LibraryDir { get; init; }
    public string? SteamRoot { get; init; }
    /// <summary>How the folder was found: manual, steam_library, guessed.</summary>
    public required string Source { get; init; }
    public string? BuildId { get; init; }
    /// <summary>True on Linux: the Windows game runs through Proton.</summary>
    public bool UsesProton { get; init; }
    /// <summary>Proton prefix folder (steamapps/compatdata/2168680), when known.</summary>
    public string? CompatDataDir { get; init; }

    public string ExePath => Path.Combine(GameDir, GameInfo.ExeName);
}

public static class GameInfo
{
    public const string AppId = "2168680";
    public const string ExeName = "NuclearOption.exe";
    public const string DefaultInstallDir = "Nuclear Option";

    public static bool LooksLikeGameDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, ExeName)) && Directory.Exists(Path.Combine(dir, "NuclearOption_Data"));
}

/// <summary>Finds the game: manual folder first, then Steam's libraries, then common library locations.</summary>
public sealed class GameLocator
{
    private readonly SteamLocator steam;
    public GameLocator(SteamLocator? steam = null) => this.steam = steam ?? new SteamLocator();

    public GameInstall? Locate(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.GameDir))
            return GameInfo.LooksLikeGameDir(settings.GameDir) ? Describe(settings.GameDir!, "manual", null) : null;

        var roots = steam.FindSteamRoots(settings.SteamDir);
        var libraries = new List<(string Path, bool ListsApp, string? Root)>();
        foreach (var root in roots)
        foreach (var library in steam.ReadLibraries(root))
            libraries.Add((library.Path, library.AppIds.Contains(GameInfo.AppId), root));
        // Libraries that say they contain the game first.
        foreach (var (path, _, root) in libraries.OrderByDescending(l => l.ListsApp))
        {
            var dir = FromLibrary(path);
            if (dir != null) return Describe(dir, "steam_library", root);
        }
        foreach (var guess in steam.GuessExtraLibraries())
        {
            var dir = FromLibrary(guess);
            if (dir != null) return Describe(dir, "guessed", roots.FirstOrDefault());
        }
        return null;
    }

    /// <summary>steamapps/common/&lt;installdir from appmanifest&gt; inside a library, when it holds the game.</summary>
    public static string? FromLibrary(string library)
    {
        var steamapps = Path.Combine(library, "steamapps");
        var manifest = Path.Combine(steamapps, $"appmanifest_{GameInfo.AppId}.acf");
        var installDir = GameInfo.DefaultInstallDir;
        if (File.Exists(manifest))
        {
            try { installDir = Vdf.ParseFile(manifest)["AppState"]?.GetString("installdir") ?? installDir; }
            catch (Exception) { }
        }
        var dir = Path.Combine(steamapps, "common", installDir);
        return GameInfo.LooksLikeGameDir(dir) ? dir : null;
    }

    public static GameInstall Describe(string gameDir, string source, string? steamRoot)
    {
        gameDir = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar);
        // <library>/steamapps/common/<game>
        string? library = null;
        var common = Directory.GetParent(gameDir);
        if (common != null && common.Name.Equals("common", StringComparison.OrdinalIgnoreCase)
            && common.Parent != null && common.Parent.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            library = common.Parent.Parent?.FullName;
        string? buildId = null;
        if (library != null)
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{GameInfo.AppId}.acf");
            try { if (File.Exists(manifest)) buildId = Vdf.ParseFile(manifest)["AppState"]?.GetString("buildid"); } catch (Exception) { }
        }
        bool proton = !OperatingSystem.IsWindows();
        string? compat = null;
        if (proton && library != null)
        {
            var candidate = Path.Combine(library, "steamapps", "compatdata", GameInfo.AppId);
            if (Directory.Exists(candidate)) compat = candidate;
        }
        return new GameInstall
        {
            GameDir = gameDir, LibraryDir = library, SteamRoot = steamRoot, Source = source, BuildId = buildId,
            UsesProton = proton, CompatDataDir = compat,
        };
    }
}
