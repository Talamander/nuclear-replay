using System.Runtime.Versioning;

namespace NuclearReplay.Core.Steam;

public sealed record SteamLibrary(string Path, IReadOnlyList<string> AppIds, string Source);

/// <summary>Finds the Steam client folder and its game libraries.</summary>
public sealed class SteamLocator
{
    private readonly Func<string, bool> directoryExists;
    private readonly Func<string, bool> fileExists;
    private readonly Func<string, string> readFile;
    private readonly string home;

    public SteamLocator() : this(Directory.Exists, File.Exists, File.ReadAllText,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }

    public SteamLocator(Func<string, bool> directoryExists, Func<string, bool> fileExists, Func<string, string> readFile, string home)
    {
        this.directoryExists = directoryExists;
        this.fileExists = fileExists;
        this.readFile = readFile;
        this.home = home;
    }

    /// <summary>Candidate Steam client folders in priority order. Only existing folders are returned.</summary>
    public IReadOnlyList<string> FindSteamRoots(string? manual = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(manual)) candidates.Add(manual!);
        if (OperatingSystem.IsWindows())
        {
            candidates.AddRange(WindowsRegistrySteamPaths());
            var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(x86)) candidates.Add(System.IO.Path.Combine(x86, "Steam"));
            candidates.Add(@"C:\Program Files (x86)\Steam");
            candidates.Add(@"C:\Program Files\Steam");
        }
        else
        {
            candidates.AddRange(LinuxSteamRootCandidates(home));
        }
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            string full;
            try { full = ResolveLink(candidate); } catch (Exception) { continue; }
            if (!directoryExists(full)) continue;
            if (!fileExists(System.IO.Path.Combine(full, "steamapps", "libraryfolders.vdf"))
                && !fileExists(System.IO.Path.Combine(full, "config", "libraryfolders.vdf"))
                && !directoryExists(System.IO.Path.Combine(full, "steamapps"))) continue;
            if (!result.Contains(full, PathComparer)) result.Add(full);
        }
        return result;
    }

    public static IEnumerable<string> LinuxSteamRootCandidates(string home) => new[]
    {
        System.IO.Path.Combine(home, ".local", "share", "Steam"),
        System.IO.Path.Combine(home, ".steam", "steam"),
        System.IO.Path.Combine(home, ".steam", "root"),
        System.IO.Path.Combine(home, ".steam", "debian-installation"),
        System.IO.Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
        System.IO.Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
    };

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> WindowsRegistrySteamPaths()
    {
        var found = new List<string>();
        try
        {
            using var user = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (user?.GetValue("SteamPath") is string p1) found.Add(p1.Replace('/', '\\'));
            using var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                                ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (machine?.GetValue("InstallPath") is string p2) found.Add(p2);
        }
        catch (Exception) { /* registry access denied or missing: fall back to defaults */ }
        return found;
    }

    /// <summary>Libraries listed by the Steam client (steamapps/libraryfolders.vdf, then config/libraryfolders.vdf).</summary>
    public IReadOnlyList<SteamLibrary> ReadLibraries(string steamRoot)
    {
        var libraries = new List<SteamLibrary>();
        void Add(string path, IReadOnlyList<string> apps, string source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var existing = libraries.FindIndex(l => PathComparer.Equals(l.Path, path));
            if (existing >= 0)
            {
                if (apps.Count > 0 && libraries[existing].AppIds.Count == 0) libraries[existing] = new SteamLibrary(path, apps, source);
                return;
            }
            libraries.Add(new SteamLibrary(path, apps, source));
        }
        Add(steamRoot, Array.Empty<string>(), "steam_root");
        foreach (var file in new[]
                 {
                     System.IO.Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                     System.IO.Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
                 })
        {
            if (!fileExists(file)) continue;
            VdfNode root;
            try { root = Vdf.Parse(readFile(file)); } catch (Exception) { continue; }
            foreach (var (path, apps) in ParseLibraryFolders(root)) Add(path, apps, file);
        }
        // Older clients list extra libraries in config.vdf as BaseInstallFolder_N.
        var config = System.IO.Path.Combine(steamRoot, "config", "config.vdf");
        if (fileExists(config))
        {
            try
            {
                var steam = Vdf.Parse(readFile(config)).Path("InstallConfigStore", "Software", "Valve", "Steam");
                foreach (var child in steam?.Children ?? new List<VdfNode>())
                    if (child.Key.StartsWith("BaseInstallFolder_", StringComparison.OrdinalIgnoreCase) && child.Value != null)
                        Add(child.Value, Array.Empty<string>(), config);
            }
            catch (Exception) { }
        }
        return libraries;
    }

    /// <summary>Parses libraryfolders.vdf. Handles the current format (numbered sections with path/apps) and the old one ("1" "D:\\Games").</summary>
    public static IEnumerable<(string Path, IReadOnlyList<string> Apps)> ParseLibraryFolders(VdfNode root)
    {
        var top = root["libraryfolders"] ?? root["LibraryFolders"] ?? root;
        foreach (var child in top.Children)
        {
            if (!int.TryParse(child.Key, out _)) continue;
            if (child.Value != null) { yield return (child.Value, Array.Empty<string>()); continue; }
            var path = child.GetString("path");
            if (path == null) continue;
            var apps = child["apps"]?.Children.Select(a => a.Key).ToList() ?? new List<string>();
            yield return (path, apps);
        }
    }

    /// <summary>Folders where people commonly put a second Steam library that Steam may not list (lost config, other distro, external drive).</summary>
    public IEnumerable<string> GuessExtraLibraries()
    {
        var guesses = new List<string>();
        IEnumerable<string> SafeDirs(string dir)
        {
            try { return directoryExists(dir) ? Directory.EnumerateDirectories(dir) : Enumerable.Empty<string>(); }
            catch (Exception) { return Enumerable.Empty<string>(); }
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            foreach (var name in new[] { "SteamLibrary", "Steam", @"Program Files (x86)\Steam", @"Games\Steam", @"Games\SteamLibrary" })
                guesses.Add(System.IO.Path.Combine(drive.RootDirectory.FullName, name));
        }
        else
        {
            var mountRoots = new List<string> { "/mnt", "/media", System.IO.Path.Combine("/run/media", Environment.UserName), System.IO.Path.Combine("/media", Environment.UserName) };
            foreach (var root in mountRoots)
            foreach (var mount in SafeDirs(root))
            {
                guesses.Add(System.IO.Path.Combine(mount, "SteamLibrary"));
                guesses.Add(System.IO.Path.Combine(mount, "steamlibrary"));
                guesses.Add(System.IO.Path.Combine(mount, "Steam"));
            }
            guesses.Add(System.IO.Path.Combine(home, "Games", "SteamLibrary"));
            guesses.Add(System.IO.Path.Combine(home, "SteamLibrary"));
        }
        return guesses.Where(g => directoryExists(System.IO.Path.Combine(g, "steamapps")));
    }

    private static string ResolveLink(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && info.LinkTarget != null)
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target != null) return target.FullName;
        }
        return info.FullName.TrimEnd(System.IO.Path.DirectorySeparatorChar);
    }

    public static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
