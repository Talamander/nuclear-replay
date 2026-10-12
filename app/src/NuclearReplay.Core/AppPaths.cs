namespace NuclearReplay.Core;

/// <summary>Per-user locations for settings, job files and caches.</summary>
/// <remarks>
/// Windows: %APPDATA%\Nuclear Replay (settings) and %LOCALAPPDATA%\Nuclear Replay (data).
/// Linux: $XDG_CONFIG_HOME/nuclear-replay and $XDG_DATA_HOME/nuclear-replay.
/// NC_HOME overrides both (tests, portable installs, agents that want an isolated state).
/// Before the rename these were "Nuclear Context" and nuclear-context; <see cref="Default"/> copies the settings
/// from there once (<see cref="CopyLegacyState"/>).
/// </remarks>
public sealed class AppPaths
{
    public string ConfigDir { get; }
    public string DataDir { get; }

    public AppPaths(string configDir, string dataDir)
    {
        ConfigDir = configDir;
        DataDir = dataDir;
    }

    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public string JobsDir => Path.Combine(DataDir, "jobs");
    public string BackupsDir => Path.Combine(DataDir, "backups");
    public string CacheDir => Path.Combine(DataDir, "cache");

    /// <summary>Folder next to the executable holding the bundled BepInEx zip, plugin DLLs and ffmpeg (see app/bundled/README.md).</summary>
    public static string BundledDir =>
        Environment.GetEnvironmentVariable("NC_BUNDLED_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(AppContext.BaseDirectory, "bundled");

    public static AppPaths Default()
    {
        var home = Environment.GetEnvironmentVariable("NC_HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return new AppPaths(Path.Combine(home, "config"), Path.Combine(home, "data"));
        var current = ForName("Nuclear Replay", "nuclear-replay");
        // Before the rename this was Nuclear Context. Its settings carry over once.
        try { CopyLegacyState(ForName("Nuclear Context", "nuclear-context"), current); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return current;
    }

    /// <summary>The per-user folders for an app called <paramref name="windowsName"/> (Windows) or <paramref name="xdgName"/> (Linux).</summary>
    private static AppPaths ForName(string windowsName, string xdgName)
    {
        if (OperatingSystem.IsWindows())
        {
            return new AppPaths(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), windowsName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), windowsName));
        }
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return new AppPaths(
            Path.Combine(string.IsNullOrWhiteSpace(config) ? Path.Combine(userHome, ".config") : config, xdgName),
            Path.Combine(string.IsNullOrWhiteSpace(data) ? Path.Combine(userHome, ".local", "share") : data, xdgName));
    }

    /// <summary>
    /// First start after the rename: when <paramref name="current"/> has no settings yet and <paramref name="legacy"/>
    /// (the Nuclear Context folders) has, copies settings.json across.
    /// The old folders are never changed. Returns the number of files copied.
    /// </summary>
    public static int CopyLegacyState(AppPaths legacy, AppPaths current)
    {
        if (File.Exists(current.SettingsFile) || !File.Exists(legacy.SettingsFile)) return 0;
        Directory.CreateDirectory(Path.GetDirectoryName(current.SettingsFile)!);
        File.Copy(legacy.SettingsFile, current.SettingsFile);
        return 1;
    }

    public static string DefaultOutputDir()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos))
            videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
        return Path.Combine(videos, "Nuclear Replay");
    }
}
