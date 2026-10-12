#nullable disable
using System.Collections.Generic;
using System.IO;

namespace NuclearReplay.Shared
{
    // The plugins' BepInEx GUIDs. Compiled into the mod (NuclearReplay.dll holds both plugins) and nr
    // (NuclearReplay.Core), so all of them agree.
    //
    // Earlier names, newest first:
    //   Nuclear Context 0.5.0-beta.1 and later  talamander.nuclearcontext        talamander.nuclearcontext.replay
    //   up to 0.5.0-beta.1                      local.nuclearoption.context      local.nuclearoption.context.replay
    // BepInEx names a plugin's config file after its GUID (BepInEx/config/<GUID>.cfg), so each plugin seeds its new file
    // once, before its first Config.Bind, from the newest old file that exists (CopyLegacyConfig). Old files are never
    // deleted. Readers that only look at files (nr) accept every name (ExistingConfig).
    internal static class PluginIds
    {
        public const string Recorder = "talamander.nuclearreplay.recorder";
        public const string Player = "talamander.nuclearreplay.player";

        // Old GUIDs, newest first.
        public static readonly string[] LegacyRecorders = { "talamander.nuclearcontext", "local.nuclearoption.context" };
        public static readonly string[] LegacyPlayers = { "talamander.nuclearcontext.replay", "local.nuclearoption.context.replay" };

        // Every GUID an older install of the mod (Nuclear Context) can load under. When one of them is loaded next to
        // this mod, both would hook the game; the recorder then stays off and the player warns.
        public static IEnumerable<string> AllLegacy()
        {
            foreach (var guid in LegacyRecorders) yield return guid;
            foreach (var guid in LegacyPlayers) yield return guid;
        }

        // What both plugins log (and the player shows) when an old install is loaded too.
        public static string LegacyWarning(string legacyGuid) =>
            "Nuclear Context, the old name of this mod, is also installed (plugin " + legacyGuid + "). "
            + "Delete the folder BepInEx/plugins/NuclearContext and restart the game.";

        public static bool IsRecorder(string guid) => guid == Recorder || System.Array.IndexOf(LegacyRecorders, guid) >= 0;
        public static bool IsPlayer(string guid) => guid == Player || System.Array.IndexOf(LegacyPlayers, guid) >= 0;

        public static string ConfigFileName(string guid) => guid + ".cfg";

        // The config file the plugin with this GUID reads, or the one it will copy on its next start: <guid>.cfg when it
        // exists, otherwise the newest <legacy GUID>.cfg that exists, otherwise <guid>.cfg.
        public static string ExistingConfig(string configDir, string guid, IReadOnlyList<string> legacyGuids)
        {
            var current = Path.Combine(configDir, ConfigFileName(guid));
            if (File.Exists(current)) return current;
            return NewestLegacyConfig(configDir, legacyGuids) ?? current;
        }

        // The newest old config file that exists, or null.
        public static string NewestLegacyConfig(string configDir, IReadOnlyList<string> legacyGuids)
        {
            foreach (var legacyGuid in legacyGuids)
            {
                var legacy = Path.Combine(configDir, ConfigFileName(legacyGuid));
                if (File.Exists(legacy)) return legacy;
            }
            return null;
        }

        // Copies the newest old config file that exists to <guid>.cfg when the new one does not exist yet. The old files
        // stay (an older version of the plugin still finds its settings). Returns the file name it copied from, or null
        // when it copied nothing; the plugin then reloads its ConfigFile so the following Config.Bind calls see the old
        // values.
        public static string CopyLegacyConfig(string configDir, IReadOnlyList<string> legacyGuids, string guid)
        {
            var current = Path.Combine(configDir, ConfigFileName(guid));
            if (File.Exists(current)) return null;
            var legacy = NewestLegacyConfig(configDir, legacyGuids);
            if (legacy == null) return null;
            var temp = current + ".copying";
            File.Copy(legacy, temp, true);
            try
            {
                File.Move(temp, current); // fails when the new file appeared meanwhile; that file wins
            }
            catch (IOException) when (File.Exists(current))
            {
                File.Delete(temp);
                return null;
            }
            return Path.GetFileName(legacy);
        }
    }
}
