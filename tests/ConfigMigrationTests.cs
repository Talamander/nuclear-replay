// Plugin GUID changes: the first start after an update copies BepInEx/config/<old GUID>.cfg (the newest old name that
// exists: talamander.nuclearcontext*, then local.nuclearoption.context*) to <new GUID>.cfg and reloads the ConfigFile
// before the first Bind. Runs the real BepInEx 5 ConfigFile (managed only, no Unity).
using System;
using System.IO;
using BepInEx.Configuration;
using NuclearReplay.Shared;

int checks = 0;
void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); checks++; }

var root = Path.Combine(Path.GetTempPath(), "nc-config-migration-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
try
{
    var cases = new System.Collections.Generic.List<(string[] Legacies, string Legacy, string Current)>();
    foreach (var legacy in PluginIds.LegacyRecorders) cases.Add((PluginIds.LegacyRecorders, legacy, PluginIds.Recorder));
    foreach (var legacy in PluginIds.LegacyPlayers) cases.Add((PluginIds.LegacyPlayers, legacy, PluginIds.Player));
    foreach (var (legacies, legacy, current) in cases)
    {
        var dir = Path.Combine(root, legacy);
        Directory.CreateDirectory(dir);
        var oldPath = Path.Combine(dir, legacy + ".cfg");
        var oldText = "## Settings file was created by plugin Nuclear Context v0.4.1\n## Plugin GUID: " + legacy +
                      "\n\n[Viewer]\n\nFfmpegPath = C:\\Tools\\ffmpeg.exe\nQuality = 23\n";
        File.WriteAllText(oldPath, oldText);

        // What BaseUnityPlugin does before Awake: open <GUID>.cfg (missing) without saving.
        var newPath = Path.Combine(dir, current + ".cfg");
        var config = new ConfigFile(newPath, false);
        // What MigrateConfig does at the top of Awake.
        Check(PluginIds.CopyLegacyConfig(dir, legacies, current) == legacy + ".cfg", legacy + ": copied");
        config.Reload();
        var quality = config.Bind("Viewer", "Quality", 19, "test");
        var ffmpeg = config.Bind("Viewer", "FfmpegPath", "", "test");
        var fresh = config.Bind("Viewer", "Encoder", "auto", "test");
        Check(quality.Value == 23, current + ": int kept (" + quality.Value + ")");
        Check(ffmpeg.Value == @"C:\Tools\ffmpeg.exe", current + ": path kept (" + ffmpeg.Value + ")");
        Check(fresh.Value == "auto", current + ": new key gets its default");
        Check(File.ReadAllText(newPath).Contains("Quality = 23"), current + ": saved file keeps the value");
        Check(File.ReadAllText(oldPath) == oldText, current + ": old file untouched");

        // Next start: the new file exists, nothing is copied again and the player's later change stays.
        quality.Value = 25;
        Check(PluginIds.CopyLegacyConfig(dir, legacies, current) == null, current + ": second start copies nothing");
        var again = new ConfigFile(newPath, false).Bind("Viewer", "Quality", 19, "test");
        Check(again.Value == 25, current + ": later change kept");
        Check(Directory.GetFiles(dir).Length == 2, current + ": no temporary files");
    }
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"Config migration: {checks} checks passed against BepInEx 5 ConfigFile (managed only; no game started).");
