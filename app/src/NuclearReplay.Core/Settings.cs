using System.Text.Json;

namespace NuclearReplay.Core;

/// <summary>User settings. Every path is optional; empty means "find it automatically".</summary>
public sealed class Settings
{
    public string? GameDir { get; set; }
    public string? SteamDir { get; set; }
    public string? DemoDir { get; set; }
    public string? OutputDir { get; set; }
    public string? FfmpegPath { get; set; }
    public bool SetupComplete { get; set; }
    public string DefaultCamera { get; set; } = "cinematic_chase";
    public string DefaultResolution { get; set; } = "1080p";
    public int DefaultFps { get; set; } = 60;
    public double DefaultPreSeconds { get; set; } = 5;
    public double DefaultPostSeconds { get; set; } = 6;
    public bool ShowHud { get; set; }
    public string Encoder { get; set; } = "auto";
    public int Quality { get; set; } = 19;
    /// <summary>
    /// Make moment pictures by itself when a match without them is opened. Off by default: each run starts Nuclear
    /// Option in the background for a few minutes.
    /// </summary>
    public bool AutoThumbnails { get; set; }

    public string EffectiveOutputDir => string.IsNullOrWhiteSpace(OutputDir) ? AppPaths.DefaultOutputDir() : OutputDir!;
}

public sealed class SettingsStore
{
    private readonly AppPaths paths;
    public SettingsStore(AppPaths paths) => this.paths = paths;

    public Settings Load()
    {
        try
        {
            if (File.Exists(paths.SettingsFile))
                return Json.Deserialize<Settings>(File.ReadAllText(paths.SettingsFile)) ?? new Settings();
        }
        catch (JsonException)
        {
            // A damaged settings file must not stop nr; keep a copy for support and start fresh.
            try { File.Copy(paths.SettingsFile, paths.SettingsFile + ".broken", overwrite: true); } catch (IOException) { }
        }
        return new Settings();
    }

    public void Save(Settings settings) => Json.WriteFileAtomic(paths.SettingsFile, Json.Serialize(settings));
}
