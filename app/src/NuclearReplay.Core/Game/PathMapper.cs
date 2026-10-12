namespace NuclearReplay.Core.Game;

/// <summary>Converts between paths this app sees and paths the game process can open.</summary>
/// <remarks>
/// On Windows both are the same. Under Proton the game sees the Linux root as drive Z:, so
/// "/home/me/a b/x.json" becomes "Z:\home\me\a b\x.json". Other Wine drives (Steam maps the game's
/// library to S: when it launches the game) are resolved back through the prefix's dosdevices links.
/// </remarks>
public sealed class PathMapper
{
    private readonly bool proton;
    private readonly IReadOnlyDictionary<char, string> drives;

    public PathMapper(bool proton, IReadOnlyDictionary<char, string>? drives = null)
    {
        this.proton = proton;
        this.drives = drives ?? new Dictionary<char, string> { ['z'] = "/" };
    }

    public static PathMapper For(GameInstall? install)
    {
        if (install == null || !install.UsesProton) return new PathMapper(false);
        return new PathMapper(true, ReadDosDevices(install.CompatDataDir));
    }

    public bool Proton => proton;

    /// <summary>Path that the game process can open for a file this app can open.</summary>
    public string ToGamePath(string hostPath)
    {
        if (!proton) return Path.GetFullPath(hostPath);
        return LinuxToWine(Path.GetFullPath(hostPath));
    }

    /// <summary>Path this app can open for a path the game wrote (for example a demo folder in the plugin's config).</summary>
    public string ToHostPath(string gamePath)
    {
        if (!proton) return gamePath;
        return WineToLinux(gamePath, drives) ?? gamePath;
    }

    /// <summary>"/x/y" to "Z:\x\y". Paths that are not absolute Linux paths are returned unchanged.</summary>
    public static string LinuxToWine(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/') return path;
        return "Z:" + path.Replace('/', '\\');
    }

    /// <summary>"S:\a\b" to "&lt;target of drive s&gt;/a/b". Returns null for unknown drives or non-drive paths.</summary>
    public static string? WineToLinux(string path, IReadOnlyDictionary<char, string> drives)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsLetter(path[0])) return null;
        if (!drives.TryGetValue(char.ToLowerInvariant(path[0]), out var root)) return null;
        var rest = path.Substring(2).Replace('\\', '/').TrimStart('/');
        if (root == "/") return "/" + rest;
        return rest.Length == 0 ? root : root.TrimEnd('/') + "/" + rest;
    }

    /// <summary>Drive letters of a Proton prefix: compatdata/&lt;app&gt;/pfx/dosdevices/&lt;x&gt;: symlinks.</summary>
    public static IReadOnlyDictionary<char, string> ReadDosDevices(string? compatDataDir)
    {
        var map = new Dictionary<char, string> { ['z'] = "/" };
        if (compatDataDir == null) return map;
        var dosdevices = Path.Combine(compatDataDir, "pfx", "dosdevices");
        if (!Directory.Exists(dosdevices)) return map;
        try
        {
            foreach (var entry in new DirectoryInfo(dosdevices).EnumerateFileSystemInfos())
            {
                var name = entry.Name;
                if (name.Length != 2 || name[1] != ':' || !char.IsLetter(name[0]) || entry.LinkTarget == null) continue;
                var target = entry.LinkTarget;
                if (!Path.IsPathRooted(target)) target = Path.GetFullPath(Path.Combine(dosdevices, target));
                map[char.ToLowerInvariant(name[0])] = target;
            }
        }
        catch (Exception) { }
        return map;
    }
}
