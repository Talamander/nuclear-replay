using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using NuclearOption.SavedMission;
using NuclearReplay.Shared;

namespace NuclearReplay
{
    [BepInPlugin(Id, "Nuclear Replay Recorder", BuildVersion.Core)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = PluginIds.Recorder;
        // Full product version (version.props), written into demo headers and logs. BepInPlugin gets the numeric part.
        public const string Version = BuildVersion.Full;
        internal static Plugin Instance;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        internal readonly Correlation Ledger = new Correlation();
        private ConditionalWeakTable<Unit, Identity> identities = new ConditionalWeakTable<Unit, Identity>();
        private readonly HashSet<string> watched = new HashSet<string>();
        private readonly HashSet<string> detonated = new HashSet<string>();
        private readonly HashSet<string> killed = new HashSet<string>();
        private readonly Dictionary<string, JObject> previousTargets = new Dictionary<string, JObject>();
        private readonly List<Unit> selectionReferences = new List<Unit>();
        private readonly Dictionary<string, bool> hookStatus = new Dictionary<string, bool>();
        internal readonly Dictionary<string, string> HookFailures = new Dictionary<string, string>();
        private readonly Dictionary<string, long> errorCounts = new Dictionary<string, long>();
        private BundleWriter writer;
        private Demo.DemoRecorder demo;
        private Harmony harmony;
        private string session, mission, lastPlayer, lastWeapon, output, marker;
        private int entitySequence, eventSequence, stateSequence, missionSequence, markerSequence;
        private Mission currentMission;
        private Aircraft player;
        private ConfigEntry<float> sampleHz;
        private ConfigEntry<KeyCode> syncKey;
        private double nextSample, nextHealth, markerUntil;
        private bool markerRecorded;
        private bool closed;
        private string writerFault;
        private string lastDestroyedPlayer;
        private long reportedDrops;
        private GUIStyle markerStyle;
        private readonly HashSet<string> runtimeErrors = new HashSet<string>();
        private sealed class Identity { public string Id; }
        public double RealSeconds => clock.Elapsed.TotalSeconds;
        // True while the telemetry bundle (BundleWriter and its observers) is on for this session.
        internal bool Telemetry => writer != null;

        private void Awake()
        {
            Instance = this;
            MigrateConfig();
            if (LegacyPluginLoaded() is string legacy)
            {
                // Two recorders would hook the same game and write every recording twice.
                Logger.LogError(PluginIds.LegacyWarning(legacy) + " Recording is off for this session.");
                Instance = null; enabled = false;
                return;
            }
            // A config file written by an older version keeps the bundle on; a fresh install records demos only.
            bool freshConfig = !File.Exists(Config.ConfigFilePath);
            sampleHz = Config.Bind("Capture", "SampleHz", 5f, "Samples per real second, clamped to 1–30. Discrete hooks do not depend on this rate.");
            ConfigureObservation();
            syncKey = Config.Bind("Video", "SyncKey", KeyCode.F8, "Display a unique synchronization marker for two real seconds. Avoid desktop shortcut conflicts.");
            var root = Config.Bind("Capture", "OutputDirectory", ModFolders.DataDir(Paths.GameRootPath), "Bundle output directory. Linux paths under Proton must use a Wine drive mapping.");
            MoveLegacyDefault(root);
            var capacity = Config.Bind("Capture", "QueueCapacity", 4096, "Bounded queue capacity. Full queues drop records and report counts.");
            var byteCapacity = Config.Bind("Capture", "QueuePayloadMiB", 32, "Maximum queued immutable UTF-16 payload, clamped to 1–256 MiB. Excludes object overhead and the active writer item.");
            var recordReplays = Config.Bind("Capture", "RecordDuringReplay", false, "Write a telemetry bundle and demo while the replay player plays a demo. A replay repeats recorded data, so these files are normally redundant.");
            if (!recordReplays.Value && ReplayLaunch())
            {
                Logger.LogInfo("Replay launch detected; telemetry bundle and demo recording are off for this session.");
                Instance = null; enabled = false;
                return;
            }
            var bundleEnabled = Config.Bind("Capture", "TelemetryBundle", !freshConfig,
                "Write the heavy telemetry bundle (events, states, motion samples) beside the demos. Demo recording ([Demo] Enabled) does not need it. "
                + "Off by default for new installs; configs from older versions keep it on. Command line -ncTelemetryBundle on|off overrides it for one session.");
            bool telemetry = BundleOverride() ?? bundleEnabled.Value;
            session = Guid.NewGuid().ToString("N");
            mission = session + ":m0";
            var demoEnabled = Config.Bind("Demo", "Enabled", true, "Record a network demo (.ncdemo) of each hosted mission for in-engine replay. Costs one extra network observer on the host.");
            var demoRoot = Config.Bind("Demo", "OutputDirectory", Path.Combine(root.Value, "demos"), "Demo output directory. Linux paths under Proton must use a Wine drive mapping.");
            MoveLegacyDefault(demoRoot, "demos");
            if (!telemetry && !demoEnabled.Value)
            {
                Logger.LogInfo("Telemetry bundle and demo recording are both off; nothing to record.");
                Instance = null; enabled = false;
                return;
            }
            // Nuclear Option's first scene load destroys the BepInEx manager object (and this plugin with it) unless
            // the object is hidden, as BepInEx's HideManagerGameObject option does. the replay player does the
            // same; doing it here keeps recording alive when the replay plugin is missing.
            if ((gameObject.hideFlags & HideFlags.DontSave) == 0) gameObject.hideFlags |= HideFlags.HideAndDontSave;
            if (telemetry)
            {
                output = Path.Combine(root.Value, DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + session.Substring(0, 8));
                writer = new BundleWriter(output, capacity.Value, Math.Max(1, Math.Min(256, byteCapacity.Value)) * 1024L * 1024L);
            }
            harmony = new Harmony(Id);
            Hooks.Install(harmony, hookStatus, telemetry);
            if (demoEnabled.Value)
            {
                demo = new Demo.DemoRecorder(Logger, demoRoot.Value, Version);
                Demo.DemoRecorder.InstallPatches(harmony, hookStatus);
            }
            foreach (var h in hookStatus.Where(x => !x.Value)) Logger.LogWarning("Missing hook: " + h.Key);
            if (!telemetry)
            {
                Logger.LogInfo("Telemetry bundle off; demo recording " + (demo != null ? "on" : "off") + ".");
                return;
            }
            WriteSession();
            Emit("session_started", new JObject { ["bundle_path"] = output });
            currentMission = MissionManager.CurrentMission;
            if (currentMission != null) BeginMission("plugin_loaded", false);
            Logger.LogInfo("Bundle: " + output + "; synchronization: " + syncKey.Value);
        }

        // -ncTelemetryBundle on|off overrides [Capture] TelemetryBundle for one session.
        private static bool? BundleOverride()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-ncTelemetryBundle")
                {
                    var v = args[i + 1].ToLowerInvariant();
                    if (v == "on" || v == "true" || v == "1") return true;
                    if (v == "off" || v == "false" || v == "0") return false;
                }
            return null;
        }

        // BepInEx opened Config under the new GUID. Before the first Bind, take over the settings saved under the newest
        // old GUID (once; the old files stay) and reload, so an update keeps the player's settings.
        private void MigrateConfig()
        {
            try
            {
                var from = PluginIds.CopyLegacyConfig(Path.GetDirectoryName(Config.ConfigFilePath), PluginIds.LegacyRecorders, Id);
                if (from == null) return;
                Config.Reload();
                Logger.LogInfo("Copied settings from " + from + " to " + Path.GetFileName(Config.ConfigFilePath) + ".");
            }
            catch (Exception e) { Logger.LogWarning("Could not copy settings from an older config file: " + e.Message); }
        }

        // The GUID of an old install of this mod (Nuclear Context) that BepInEx loaded too, or null. Old GUIDs sort before
        // talamander.nuclearreplay.*, so BepInEx has already registered them when this plugin wakes.
        internal static string LegacyPluginLoaded() =>
            PluginIds.AllLegacy().FirstOrDefault(guid => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(guid));

        // A config copied from Nuclear Context holds that version's default folder (<game>/NuclearContext, .../demos).
        // New recordings go to <game>/NuclearReplay instead; a folder the player chose stays as it is. The old folder and
        // its files are never touched.
        private void MoveLegacyDefault(ConfigEntry<string> entry, params string[] parts)
        {
            var moved = ModFolders.ReplaceLegacyDefault(entry.Value, Paths.GameRootPath, parts);
            if (moved == null) return;
            Logger.LogInfo("[" + entry.Definition.Section + "] " + entry.Definition.Key + ": " + entry.Value + " was the Nuclear Context default; now " + moved + ". The old folder stays as it is.");
            entry.Value = moved;
        }

        // Same triggers as the replay player: a -ncReplay argument or a non-empty AutoStartPath.
        private static bool ReplayLaunch()
        {
            if (Environment.GetCommandLineArgs().Any(a => a == "-ncReplay")) return true;
            try
            {
                // The replay plugin may start after this one and copy its old config only then.
                var replayConfig = PluginIds.ExistingConfig(Paths.ConfigPath, PluginIds.Player, PluginIds.LegacyPlayers);
                return File.Exists(replayConfig) && File.ReadLines(replayConfig)
                    .Select(line => line.Split(new[] { '=' }, 2))
                    .Any(pair => pair.Length == 2 && pair[0].Trim() == "AutoStartPath" && pair[1].Trim().Length != 0);
            }
            catch (Exception) { return false; }
        }

        private void WriteSession()
        {
            var units = new JObject
            {
                ["position"] = "metres in Unity global mission coordinates; x/z horizontal, y up; local position minus Datum.originPosition",
                ["altitude_msl_m"] = "global y, metres above Datum.SeaLevel (0,0,0)",
                ["ground_speed_mps"] = "magnitude of Unit.rb.velocity; metres/second in world coordinates, not indicated airspeed",
                ["heading_deg"] = "projected transform forward; atan2(x,z), [0,360); mission +z=0, +x=90; not asserted geographic north",
                ["slant_range_m"] = "3D Euclidean distance from launching-unit origin to assigned-target origin observed on host at spawn invocation",
                ["horizontal_range_m"] = "Euclidean distance in global x/z plane",
                ["game_time_s"] = "Unity Time.timeAsDouble; scaled simulation seconds since application start",
                ["rotation_xyzw"] = "Unity world quaternion, x/y/z/w; floating-origin translation does not change rotation",
                ["velocity_mps"] = "Unit.rb.velocity in Unity world axes, metres/second; client values may be smoothed",
                ["angular_velocity_rad_s"] = "Unit.rb.angularVelocity in Unity world axes, radians/second; client missile code sets this to zero",
                ["last_spotted_time_since_level_load_s"] = "TrackingInfo.lastSpottedTime uses Unity Time.timeSinceLevelLoad; not game_time_s",
                ["real_time_s"] = "Stopwatch elapsed since this plugin session started; monotonic, independent of pause/timeScale",
                ["utc"] = "DateTime.UtcNow at snapshot; wall clock can jump",
                ["damage_amount"] = "game Unit.RecordDamage credit amount, not hitpoints or physical energy"
            };
            units["part_hit_points"] = "UnitPart.hitPoints, game part health units; initialized to 100 per part, may be negative; not aggregate aircraft health percent";
            var metadata = new JObject
            {
                ["schema_version"] = "1.1", ["session_id"] = session,
                ["created_utc"] = DateTime.UtcNow.ToString("O"), ["mod_version"] = Version,
                ["game_version"] = Application.version, ["unity_version"] = Application.unityVersion,
                ["game_assembly_sha256"] = Sha256(Path.Combine(Paths.ManagedPath, "Assembly-CSharp.dll")),
                ["unity_core_sha256"] = Sha256(Path.Combine(Paths.ManagedPath, "UnityEngine.CoreModule.dll")),
                ["unity_particles_sha256"] = Sha256(Path.Combine(Paths.ManagedPath, "UnityEngine.ParticleSystemModule.dll")),
                ["unity_render_pipeline_core_sha256"] = Sha256(Path.Combine(Paths.ManagedPath, "Unity.RenderPipelines.Core.Runtime.dll")),
                ["unity_render_pipeline_universal_sha256"] = Sha256(Path.Combine(Paths.ManagedPath, "Unity.RenderPipelines.Universal.Runtime.dll")),
                ["bepinex_version"] = typeof(BaseUnityPlugin).Assembly.GetName().Version.ToString(),
                ["harmony_version"] = typeof(Harmony).Assembly.GetName().Version.ToString(),
                ["harmony_product_version"] = FileVersionInfo.GetVersionInfo(Path.Combine(Paths.BepInExRootPath, "core", "0Harmony.dll")).ProductVersion,
                ["sample_hz"] = Rate(sampleHz.Value, 30),
                ["capture_scope"] = captureScope.Value.ToString(), ["world_hz"] = Rate(worldHz.Value, 30),
                ["projectile_hz"] = Rate(projectileHz.Value, 60), ["max_motion_per_batch"] = Math.Max(1, Math.Min(4096, motionLimit.Value)),
                ["reconstruction_status"] = "capture_foundation_only; no playback engine; gameplay_unverified",
                ["unavailable_capture_fields"] = new JArray("complete initial simulation state", "remote exact launch clock and assignment",
                    "complete model/control-surface/rotor/turret asset mapping", "complete livery/attachment/damage geometry resolution",
                    "complete effect discovery, shader and particle module state", "audio state", "final rendered camera timing", "client/host clock alignment"),
                ["hooks"] = JObject.FromObject(hookStatus), ["hook_failures"] = JObject.FromObject(HookFailures), ["units"] = units,
                ["recorder"] = new JObject { ["detected"] = JValue.CreateNull(), ["websocket_available_at_inspection"] = JValue.CreateNull(),
                    ["method"] = "visible marker; actual video frame anchors required", ["sync_key"] = syncKey.Value.ToString() },
                ["recording_alignment"] = new JObject { ["status"] = "unknown", ["segments"] = new JArray() },
                ["limitations"] = new JArray("Exact launch context requires this plugin on the host. Clients record initialization, current target, request and detonation observations with unknown launch times.",
                    "Target identity, exact position, damage, seeker changes and destruction are simulation data; player recognition is not established.",
                    "No kill is attributed to a projectile from disappearance, proximity or target selection.",
                    "Gun rounds, nuclear shockwave projectile correlation and individual countermeasure deployments are not captured. Incoming warning state and countermeasure trigger are sampled only.",
                    "writer-status.json contains final or periodically flushed loss/error counts; check clean_shutdown after exit.")
            };
            writer.Enqueue("session", metadata.ToString(Formatting.Indented));
        }

        private static string Sha256(string path)
        {
            using var hash = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        internal void Safe(string operation, Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                errorCounts.TryGetValue(operation, out var count);
                errorCounts[operation] = count + 1;
                if (!runtimeErrors.Add(operation)) return;
                Logger.LogWarning("Recorder operation " + operation + " failed: " + e);
                try { Emit("hook_error", new JObject { ["hook"] = operation, ["error"] = e.ToString() }); } catch { }
            }
        }

        internal string EntityId(Unit unit)
        {
            // Reference equality avoids Unity's overloaded null/equality and instance-ID reuse.
            if (ReferenceEquals(unit, null)) return null;
            return identities.GetValue(unit, _ => new Identity { Id = mission + ":e" + (++entitySequence) }).Id;
        }

        internal JObject Entity(Unit unit)
        {
            if (unit == null) return null;
            Vector3 pos = unit.transform.position - Datum.originPosition;
            Vector3 forward = unit.transform.forward;
            float heading = (Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg + 360f) % 360f;
            var result = new JObject
            {
                ["entity_id"] = EntityId(unit), ["game_persistent_id"] = unit.persistentID.Id,
                ["name"] = unit.unitName, ["type"] = unit.definition != null ? unit.definition.unitName : null,
                ["type_code"] = unit.definition != null ? unit.definition.code : null,
                ["definition_key"] = unit.definition?.jsonKey, ["rotation_xyzw"] = Rotation(unit.transform.rotation),
                ["velocity_mps"] = unit.rb != null ? Vector(unit.rb.velocity) : null,
                ["angular_velocity_rad_s"] = unit.rb != null ? Vector(unit.rb.angularVelocity) : null,
                ["runtime_class"] = unit.GetType().FullName,
                ["local_simulation"] = unit.LocalSim, ["is_server"] = unit.IsServer,
                ["position_source"] = "local_transform_at_observation; remote units may be smoothed",
                ["position_m"] = Vector(pos), ["altitude_msl_m"] = pos.y,
                ["ground_speed_mps"] = unit.rb != null ? (JToken)unit.rb.velocity.magnitude : JValue.CreateNull(),
                ["heading_deg"] = heading, ["damage_state"] = unit.unitState.ToString(), ["disabled"] = unit.disabled,
                ["identity_source"] = "simulation_definition", ["player_recognized_type"] = JValue.CreateNull(),
                ["visibility_source"] = "hidden_simulation_data"
            };
            var parts = new JArray();
            for (int index = 0; index < unit.damageables.Count; index++)
            {
                var part = unit.damageables[index];
                if (!part.Removed && part.Damageable is UnitPart unitPart && unitPart != null)
                    parts.Add(new JObject { ["part_index"] = index, ["hit_points"] = unitPart.hitPoints });
            }
            result["part_hit_points"] = parts;
            if (player != null && player.NetworkHQ != null && player.NetworkHQ.trackingDatabase.TryGetValue(unit.persistentID, out var tracking))
                result["player_tracking"] = new JObject { ["observed_recently"] = tracking.Observed(),
                    ["last_spotted_time_since_level_load_s"] = tracking.lastSpottedTime, ["last_known_position_m"] = Vector(tracking.lastKnownPosition.AsVector3()),
                    ["note"] = "Faction tracking, not proof the player visually identified this type." };
            return result;
        }

        internal static JArray Vector(Vector3 value) => new JArray(value.x, value.y, value.z);

        internal JObject WeaponState()
        {
            var station = player != null && player.weaponManager != null ? player.weaponManager.currentWeaponStation : null;
            if (station == null) return null;
            return new JObject { ["station_number"] = station.Number,
                ["name"] = station.WeaponInfo != null ? station.WeaponInfo.weaponName : null,
                ["type"] = WeaponType(station.WeaponInfo), ["ammo"] = station.Ammo,
                ["ammo_loaded"] = station.GetAmmoLoaded(), ["ammo_total"] = station.GetAmmoTotal() };
        }

        internal static string WeaponType(WeaponInfo info) => info == null ? "unknown" : info.gun ? "gun" :
            info.glideBomb ? "glide_bomb" : info.bomb ? "bomb" : info.missile ? "missile" : "other";

        private JObject Record(string kind, JObject data, bool state = false)
        {
            return new JObject { ["schema_version"] = "1.1", [state ? "state_id" : "event_id"] = session + (state ? ":s" + (++stateSequence) : ":v" + (++eventSequence)),
                ["session_id"] = session, ["mission_id"] = mission, ["kind"] = kind,
                ["real_time_s"] = RealSeconds, ["utc"] = DateTime.UtcNow.ToString("O"),
                ["game_time_s"] = Time.timeAsDouble, ["time_scale"] = Time.timeScale,
                ["video_time_s"] = JValue.CreateNull(), ["evidence"] = "direct_local_simulation_observation", ["data"] = data };
        }

        internal string Emit(string kind, JObject data, double? realTime = null, bool requireAccepted = false)
        {
            if (writer == null) return null;
            var record = Record(kind, data);
            if (realTime.HasValue) record["real_time_s"] = realTime.Value;
            string json = record.ToString(Formatting.None);
            if (requireAccepted) writer.RequireEnqueue("events", json);
            else writer.Enqueue("events", json);
            return (string)record["event_id"];
        }

        internal void BeginMission(string reason, bool force)
        {
            if (!force && ReferenceEquals(currentMission, MissionManager.CurrentMission) && missionSequence > 0) return;
            if (missionSequence > 0)
            {
                Emit("mission_ended", new JObject { ["reason"] = reason, ["outcomes_without_evidence"] = "unknown" });
            }
            mission = session + ":m" + (++missionSequence);
            currentMission = MissionManager.CurrentMission;
            Ledger.Clear(); watched.Clear(); detonated.Clear(); killed.Clear(); ResetObservation();
            identities = new ConditionalWeakTable<Unit, Identity>(); previousTargets.Clear();
            lastPlayer = lastWeapon = lastDestroyedPlayer = null; player = null; selectionReferences.Clear();
            Emit("mission_changed", new JObject { ["name"] = currentMission?.Name, ["reason"] = reason });
        }

        internal void CheckPlayer()
        {
            GameManager.GetLocalAircraft(out var current);
            string id = EntityId(current);
            if (id != lastPlayer)
            {
                string old = lastPlayer;
                player = current; lastPlayer = id; lastWeapon = null;
                foreach (var oldTarget in previousTargets.Values) Emit("target_deselected", new JObject { ["target"] = oldTarget.DeepClone(), ["reason"] = "player_aircraft_changed" });
                previousTargets.Clear();
                if (current != null) watched.Add(id);
                Emit("player_aircraft_changed", new JObject { ["previous_entity_id"] = old, ["player"] = Entity(current),
                    ["respawn"] = JValue.CreateNull(),
                    ["note"] = "Aircraft identity change; respawn cause is not inferred when unknown." });
                if (current != null && lastDestroyedPlayer != null && lastDestroyedPlayer != id)
                {
                    Emit("player_respawn_observed", new JObject { ["previous_destroyed_entity_id"] = lastDestroyedPlayer,
                        ["player"] = Entity(current), ["evidence"] = "new local aircraft after prior local Unit.ReportKilled" });
                    lastDestroyedPlayer = null;
                }
            }
        }

        internal void SelectionChanged(string source)
        {
            CheckPlayer();
            var targets = player != null && player.weaponManager != null ? player.weaponManager.GetTargetList() : null;
            var current = new Dictionary<string, JObject>();
            if (targets != null) foreach (var target in targets)
            {
                if (target == null) continue;
                string id = EntityId(target); watched.Add(id); current[id] = Entity(target);
            }
            foreach (var old in previousTargets.Where(x => !current.ContainsKey(x.Key)).ToArray())
                Emit("target_deselected", new JObject { ["target"] = old.Value.DeepClone(), ["source"] = source });
            foreach (var added in current.Where(x => !previousTargets.ContainsKey(x.Key)))
                Emit("target_selected", new JObject { ["target"] = added.Value.DeepClone(), ["source"] = source,
                    ["relationship"] = "player_selection_only; not missile_assignment" });
            previousTargets.Clear(); foreach (var entry in current) previousTargets.Add(entry.Key, entry.Value);
            selectionReferences.Clear(); if (targets != null) selectionReferences.AddRange(targets);
            var weapon = WeaponState();
            string weaponKey = weapon == null ? null : weapon["station_number"] + ":" + weapon["name"];
            if (weaponKey != lastWeapon)
            {
                lastWeapon = weaponKey;
                Emit("weapon_selected", new JObject { ["player_entity_id"] = lastPlayer, ["weapon"] = weapon, ["source"] = source });
            }
        }

        private void Update()
        {
            if (demo != null) Safe("demo_update", demo.Update);
            if (writer == null) return;
            Safe("frame_observation", () =>
            {
                if (!ReferenceEquals(currentMission, MissionManager.CurrentMission)) BeginMission("frame_observation", false);
                // O(only selected targets). No world scan and no disk operations.
                CheckPlayer();
                var list = player != null && player.weaponManager != null ? player.weaponManager.GetTargetList() : null;
                bool selectionChanged = (list?.Count ?? 0) != selectionReferences.Count;
                if (!selectionChanged && list != null)
                    for (int i = 0; i < list.Count; i++) if (!ReferenceEquals(list[i], selectionReferences[i])) { selectionChanged = true; break; }
                var currentStation = player != null && player.weaponManager != null ? player.weaponManager.currentWeaponStation : null;
                string currentWeaponKey = currentStation == null ? null : currentStation.Number + ":" + currentStation.WeaponInfo?.weaponName;
                if (selectionChanged || currentWeaponKey != lastWeapon) SelectionChanged("frame_observation");
                if (Input.GetKeyDown(syncKey.Value)) StartMarker();
                Safe("observation_update", UpdateObservation);
                if (RealSeconds >= nextSample)
                {
                    nextSample = RealSeconds + 1.0 / Rate(sampleHz.Value, 30);
                    var targets = new JArray();
                    if (list != null) foreach (var target in list) { var snapshot = Entity(target); if (snapshot != null) targets.Add(snapshot); }
                    var record = Record("context", new JObject { ["player"] = Entity(player), ["selected_targets"] = targets,
                        ["selected_weapon"] = WeaponState(), ["player_awareness"] = PlayerAwareness(), ["mission_running"] = MissionManager.IsRunning,
                        ["launch_capture_available"] = player != null && player.IsServer }, true);
                    writer.Enqueue("states", record.ToString(Formatting.None));
                }
                if (RealSeconds >= nextHealth)
                {
                    nextHealth = RealSeconds + 1;
                    writer.Enqueue("states", Record("capture_health", new JObject {
                        ["hook_error_counts"] = JObject.FromObject(errorCounts),
                        ["registered_entities"] = registry.Count, ["dropped_events"] = writer.DroppedEvents,
                        ["dropped_states"] = writer.DroppedStates, ["writer_fault"] = writer.Fault }, true).ToString(Formatting.None));
                    long drops = writer.DroppedEvents + writer.DroppedStates;
                    if (drops != reportedDrops)
                    {
                        Logger.LogWarning("Dropped records: events=" + writer.DroppedEvents + ", states=" + writer.DroppedStates);
                        Emit("records_dropped", new JObject { ["dropped_events"] = writer.DroppedEvents, ["dropped_states"] = writer.DroppedStates });
                        reportedDrops = drops;
                    }
                    if (writer.Fault != null && writerFault != writer.Fault)
                    { writerFault = writer.Fault; Logger.LogError("Telemetry writer stopped: " + writerFault); }
                }
            });
        }

        internal JObject SnapshotLaunch(Unit owner, Unit assigned, Vector3 launchPosition)
        {
            CheckPlayer();
            // CheckPlayer uses the runtime-tested local aircraft lookup. Avoid the
            // aggressively inlined Unit overload when running alongside Harmony mods.
            if (owner == null || (captureScope.Value != CaptureScope.World && !ReferenceEquals(owner, player))) return null;
            double invocationTime = RealSeconds;
            bool localPlayer = ReferenceEquals(owner, player);
            if (localPlayer) SelectionChanged("launch_snapshot");
            var context = new JObject { ["player"] = Entity(owner), ["assigned_target"] = Entity(assigned),
                ["launching_unit"] = Entity(owner), ["is_local_player"] = localPlayer,
                ["selected_targets"] = localPlayer ? new JArray(previousTargets.Values.Select(x => x.DeepClone())) : null,
                ["selected_weapon"] = localPlayer ? WeaponState() : null,
                ["time_basis"] = "host_spawn_invocation", ["source"] = "Spawner.SpawnMissile", ["projectile_launch_position_m"] = Vector(launchPosition - Datum.originPosition),
                ["assignment_evidence"] = "Spawner.SpawnMissile target argument", ["launch_real_time_s"] = invocationTime,
                ["range_basis"] = "owner and assigned-target transforms observed on host at spawn invocation; remote aircraft can be client-authoritative",
                ["launch_utc"] = DateTime.UtcNow.ToString("O"), ["launch_game_time_s"] = Time.timeAsDouble };
            if (assigned != null)
            {
                Vector3 delta = assigned.transform.position - owner.transform.position;
                context["slant_range_m"] = delta.magnitude;
                context["horizontal_range_m"] = Math.Sqrt((double)delta.x * delta.x + (double)delta.z * delta.z);
            }
            else { context["slant_range_m"] = JValue.CreateNull(); context["horizontal_range_m"] = JValue.CreateNull(); }
            return context;
        }

        internal void LaunchObserved(Missile missileObject, JObject snapshot)
        {
            if (missileObject == null || snapshot == null) return;
            Registered(missileObject, "Spawner.SpawnMissile return");
            string projectile = EntityId(missileObject);
            if (Ledger.Find(projectile) != null) return;
            snapshot["projectile_id"] = projectile;
            snapshot["game_projectile_id"] = missileObject.persistentID.Id;
            snapshot["projectile_observation_event_id"] = Ledger.Observation(projectile);
            var info = missileObject.GetWeaponInfo();
            snapshot["weapon"] = new JObject { ["entity"] = Entity(missileObject), ["name"] = info != null ? info.weaponName : missileObject.definition?.unitName,
                ["type"] = WeaponType(info), ["definition_code"] = missileObject.definition?.code };
            string eventId = Emit("weapon_launch", snapshot, (double)snapshot["launch_real_time_s"]);
            Ledger.Add(new Correlation.Launch(projectile, eventId, (string)snapshot["player"]?["entity_id"], (string)snapshot["assigned_target"]?["entity_id"]));
            watched.Add(projectile);
            if (snapshot["assigned_target"] is JObject target) watched.Add((string)target["entity_id"]);
        }

        internal string ProjectileId(PersistentID gameId) => gameId.IsValid ? Ledger.Resolve(gameId.Id) : null;
        internal bool IsWatched(Unit unit) => !ReferenceEquals(unit, null) && watched.Contains(EntityId(unit));

        internal JObject PartDamageBefore(Unit unit, byte index)
        {
            if (!IsWatched(unit)) return null;
            var part = index < unit.damageables.Count ? unit.damageables[index].Damageable as UnitPart : null;
            return new JObject { ["victim_entity_id"] = EntityId(unit), ["part_index"] = index,
                ["hit_points_before"] = part != null ? (JToken)part.hitPoints : JValue.CreateNull() };
        }

        internal void PartDamageAfter(Unit unit, byte index, DamageInfo damage, JObject snapshot)
        {
            if (snapshot == null) return;
            var part = index < unit.damageables.Count ? unit.damageables[index].Damageable as UnitPart : null;
            snapshot["hit_points_after"] = part != null ? (JToken)part.hitPoints : JValue.CreateNull();
            snapshot["victim"] = Entity(unit);
            snapshot["pierce_damage"] = damage.pierceDamage.Decompress(); snapshot["blast_damage"] = damage.blastDamage.Decompress();
            snapshot["fire_damage"] = damage.fireDamage.Decompress(); snapshot["impact_damage"] = damage.impactDamage.Decompress();
            var launch = Ledger.Find(Hooks.DamageProjectile);
            snapshot["projectile_id"] = Hooks.DamageProjectile; snapshot["launch_event_id"] = launch?.EventId;
            snapshot["projectile_observation_event_id"] = Ledger.Observation(Hooks.DamageProjectile);
            snapshot["correlation"] = Hooks.DamageProjectile != null ? "direct_nested_damage_call_for_explicit_projectile" : "projectile_unknown";
            snapshot["note"] = "RPC processing and observed part health; no projectile/dealer ID exists in damage RPC. Projectile link requires explicit outer damage scope.";
            Emit("part_damage_observed", snapshot);
        }

        internal void SeekerChanged(Missile missileObject, PersistentID oldId, PersistentID newId)
        {
            string projectile = EntityId(missileObject);
            if (Ledger.Observation(projectile) == null || oldId == newId) return;
            newId.TryGetUnit(out var target);
            var data = ProjectileLinks(missileObject);
            data["old_game_target_id"] = oldId.Id; data["new_game_target_id"] = newId.Id;
            data["target"] = Entity(target); data["source"] = "Missile.TargetIDChanged";
            data["visibility"] = "hidden_simulation_data";
            Emit("missile_target_changed", data);
        }

        internal void Detonation(Missile missileObject, bool armed, bool armor, bool terrain, Unit relativeUnit, bool useUnit, Vector3 pos, Vector3 normal)
        {
            Registered(missileObject, "detonation_callback");
            string projectile = EntityId(missileObject);
            if (Ledger.Observation(projectile) == null || !detonated.Add(projectile)) return;
            var data = ProjectileLinks(missileObject);
            data["projectile"] = Entity(missileObject); data["armed"] = armed;
            data["hit_armor_flag"] = armor; data["hit_terrain_flag"] = terrain;
            data["relative_coordinate_unit"] = Entity(relativeUnit); data["rpc_use_unit"] = useUnit;
            data["rpc_position"] = Vector(pos); data["rpc_normal"] = Vector(normal);
            data["outcome"] = "detonation_observed; target_hit_unknown";
            data["note"] = "RPC processing time is not host detonation time. The relative unit is a coordinate reference, not hit evidence.";
            Emit("missile_detonated", data);
        }

        internal void Impact(Missile missileObject, IDamageable damageable, Vector3 point)
        {
            var launch = Ledger.Find(EntityId(missileObject)); if (Ledger.Observation(EntityId(missileObject)) == null) return;
            var unit = damageable?.GetUnit();
            Emit("missile_collision", new JObject { ["projectile_id"] = EntityId(missileObject), ["launch_event_id"] = launch?.EventId,
                ["projectile_observation_event_id"] = Ledger.Observation(EntityId(missileObject)),
                ["contact_unit"] = Entity(unit), ["position_m"] = Vector(point - Datum.originPosition),
                ["outcome"] = "collision_with_damageable_observed; applied_damage_unknown", ["source"] = "Missile.PenetrateObject" });
        }

        internal void DamageCredit(Unit victim, PersistentID dealer, float amount)
        {
            dealer.TryGetUnit(out var dealerUnit);
            string projectile = Hooks.DamageProjectile;
            var launch = Ledger.Find(projectile);
            if (!IsWatched(victim) && !(player != null && ReferenceEquals(player, dealerUnit)) && launch == null) return;
            Emit("damage_credit_observed", new JObject { ["victim"] = Entity(victim), ["dealer"] = Entity(dealerUnit),
                ["dealer_game_id"] = dealer.Id, ["damage_amount"] = amount,
                ["projectile_id"] = projectile, ["launch_event_id"] = launch?.EventId,
                ["projectile_observation_event_id"] = Ledger.Observation(projectile),
                ["correlation"] = projectile != null ? "direct_nested_damage_call_for_explicit_projectile" : "shooter_credit_only; projectile_unknown",
                ["source"] = "Unit.RecordDamage", ["visibility"] = "hidden_simulation_data" });
        }

        internal void Destruction(Unit victim)
        {
            if (!IsWatched(victim) || !killed.Add(EntityId(victim))) return;
            if (player != null && ReferenceEquals(player, victim)) lastDestroyedPlayer = EntityId(victim);
            var launch = Ledger.Find(Hooks.DamageProjectile);
            Emit("unit_destruction_reported", new JObject { ["victim"] = Entity(victim), ["source"] = "Unit.ReportKilled",
                ["damage_call_projectile_id"] = launch?.ProjectileId, ["launch_event_id"] = launch?.EventId,
                ["kill_attribution"] = "unknown", ["visibility"] = "hidden_simulation_data",
                ["note"] = "An active damage call is context, not an exclusive kill attribution." });
        }

        internal void Removed(Unit unit)
        {
            string id = EntityId(unit);
            registry.Remove(id); lastMotion.Remove(id);
            if (!IsWatched(unit)) return;
            var launch = Ledger.Find(id);
            Emit("entity_removed", new JObject { ["entity_id"] = id, ["launch_event_id"] = launch?.EventId,
                ["projectile_id"] = unit is Missile ? id : null,
                ["projectile_observation_event_id"] = Ledger.Observation(id), ["outcome"] = "unknown", ["source"] = "Unit.OnDestroy",
                ["note"] = "Removal can be despawn, scene unload, or combat. It does not establish a hit or kill." });
            Ledger.Retire(id, unit.persistentID.Id);
            watched.Remove(id); announced.Remove(id);
        }

        private void StartMarker()
        {
            marker = "NC-" + session.Substring(0, 6).ToUpperInvariant() + "-" + (++markerSequence).ToString("D3");
            markerUntil = RealSeconds + 2; markerRecorded = false;
            Emit("sync_requested", new JObject { ["marker"] = marker });
        }

        private void OnGUI()
        {
            if (marker == null || RealSeconds >= markerUntil) return;
            Safe("marker_render", () =>
            {
                if (markerStyle == null) markerStyle = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.black } };
                GUI.depth = -10000;
                Color saved = GUI.color; GUI.color = Color.yellow;
                GUI.DrawTexture(new Rect(20, 20, Math.Min(Screen.width - 40, 760), 80), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(32, 20, Math.Min(Screen.width - 64, 736), 80), marker + "  VIDEO SYNC", markerStyle);
                GUI.color = saved;
                if (!markerRecorded && Event.current.type == EventType.Repaint)
                {
                    markerRecorded = true;
                    Emit("sync_marker", new JObject { ["marker"] = marker, ["display_seconds"] = 2,
                        ["source"] = "first OnGUI Repaint submission", ["presentation_latency_s"] = JValue.CreateNull(),
                        ["game_frame_interval_s"] = Time.unscaledDeltaTime,
                        ["note"] = "Bind first matching frame in actual recording. Render submission is not monitor presentation." });
                }
            });
        }

        private void OnApplicationQuit() => Close();
        private void OnDestroy() => Close();

        private void Close()
        {
            if (closed || (writer == null && demo == null)) return;
            closed = true;
            Safe("demo_shutdown", () => demo?.Stop("shutdown", disconnect: false));
            if (writer != null)
            {
                Safe("shutdown", () => Emit("session_ended", new JObject { ["dropped_events"] = writer.DroppedEvents, ["dropped_states"] = writer.DroppedStates }));
            }
            harmony?.UnpatchSelf(); writer?.Dispose(); Instance = null;
        }
    }
}
