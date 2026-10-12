using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay
{
    public sealed partial class Plugin
    {
        public enum CaptureScope { Engagement, World }
        private ConfigEntry<CaptureScope> captureScope;
        private ConfigEntry<float> worldHz, projectileHz;
        private ConfigEntry<int> motionLimit;
        private ConfigEntry<float> unchangedRefresh;
        // Last written pose per non-missile entity: state key and the real time of its last full snapshot.
        private readonly Dictionary<string, (string key, double written)> lastMotion = new Dictionary<string, (string, double)>();
        private readonly Dictionary<string, Unit> registry = new Dictionary<string, Unit>();
        private readonly HashSet<string> announced = new HashSet<string>();
        private bool registrySeeded;
        private double nextWorld, nextProjectile, nextEnvironment;
        private int worldCursor, projectileCursor;
        private string previousLoadout, previousObjectives;

        private void ConfigureObservation()
        {
            captureScope = Config.Bind("Capture", "Scope", CaptureScope.World, "World records locally registered units. Engagement records referenced units and all locally registered missiles. Neither guarantees complete host world coverage.");
            worldHz = Config.Bind("Capture", "WorldHz", 10f, "Motion samples per real second for non-missile units; clamped to 1–30.");
            projectileHz = Config.Bind("Capture", "ProjectileHz", 20f, "Motion samples per real second for missiles; clamped to 1–60. Client samples reflect local smoothing, not exact host motion.");
            motionLimit = Config.Bind("Capture", "MaxMotionPerBatch", 512, "Maximum entity snapshots in each motion batch; round-robin overflow is reported. Clamped to 1–4096.");
            unchangedRefresh = Config.Bind("Capture", "UnchangedRefreshSeconds", 5f, "A non-missile unit whose pose, velocity and state match its last full snapshot is listed by ID in unchanged_entity_ids instead, until this many real seconds pass. 0 writes every snapshot in full.");
        }

        private void ResetObservation()
        {
            registry.Clear(); announced.Clear(); lastMotion.Clear(); registrySeeded = false;
            previousLoadout = previousObjectives = null;
            nextWorld = nextProjectile = nextEnvironment = 0;
            worldCursor = projectileCursor = 0;
        }

        private bool InScope(Unit unit) => unit != null &&
            (captureScope.Value == CaptureScope.World || unit is Missile || IsWatched(unit));

        internal void Registered(Unit unit, string source)
        {
            if (unit == null || currentMission == null) return;
            string id = EntityId(unit);
            registry[id] = unit;
            if (unit is Missile missile) ObserveProjectile(missile, source);
            if (!InScope(unit) || announced.Contains(id)) return;
            var entity = Entity(unit);
            watched.Add(id);
            Emit("entity_observed", new JObject { ["entity"] = entity, ["source"] = source,
                ["time_basis"] = "local_observation", ["spawn_time_known"] = false,
                ["note"] = "First local observation can be a late join or registry bootstrap; it is not a spawn timestamp." });
            announced.Add(id);
        }

        internal void ObserveProjectile(Missile missile, string source)
        {
            if (missile == null || currentMission == null) return;
            string id = EntityId(missile);
            if (Ledger.Observation(id) != null) return;
            watched.Add(id);
            string eventId = Emit("projectile_observed", new JObject
            {
                ["projectile_id"] = id, ["game_projectile_id"] = missile.persistentID.Id,
                ["projectile"] = Motion(missile), ["source"] = source,
                ["time_basis"] = missile.IsServer ? "server_local_observation" : "client_local_observation",
                ["launch_real_time_s"] = JValue.CreateNull(), ["launch_game_time_s"] = JValue.CreateNull(),
                ["launch_event_id"] = JValue.CreateNull(), ["assigned_target"] = JValue.CreateNull(),
                ["slant_range_m"] = JValue.CreateNull(), ["horizontal_range_m"] = JValue.CreateNull(),
                ["observed_game_owner_id"] = missile.ownerID.Id, ["observed_game_target_id"] = missile.targetID.Id,
                ["reported_start_position_m"] = Vector(missile.startPosition.AsVector3()),
                ["reported_start_rotation_xyzw"] = Rotation(missile.startRotation),
                ["reported_start_velocity_mps"] = Vector(missile.startingVelocity),
                ["reported_start_offset_from_owner_m"] = Vector(missile.startOffsetFromOwner),
                ["launch_context"] = "unknown_without_spawn_hook",
                ["assignment_evidence"] = "current_syncvar_only; may differ from original assignment",
                ["note"] = "Replicated start fields describe initialization, including saved missiles. No launch clock or target-at-launch position is transmitted."
            });
            Ledger.Observe(id, eventId, missile.persistentID.Id);
        }

        internal JObject ProjectileLinks(Missile missile)
        {
            string id = EntityId(missile);
            return new JObject { ["projectile_id"] = id, ["launch_event_id"] = Ledger.Find(id)?.EventId,
                ["projectile_observation_event_id"] = Ledger.Observation(id),
                ["time_basis"] = missile.IsServer ? "server_local_observation" : "client_rpc_or_syncvar_processing",
                ["source_event_time_s"] = JValue.CreateNull(), ["timing_uncertainty_s"] = JValue.CreateNull() };
        }

        internal void LaunchRequest(Aircraft aircraft, byte stationIndex, Unit target, GlobalPosition aimpoint, string source)
        {
            if (aircraft == null || !InScope(aircraft)) return;
            Emit("missile_launch_request_observed", new JObject { ["aircraft"] = Entity(aircraft),
                ["station_index"] = stationIndex, ["requested_target"] = Entity(target), ["aimpoint_m"] = Vector(aimpoint.AsVector3()),
                ["source"] = source, ["time_basis"] = source == "Aircraft.CmdLaunchMissile" ? "local_request_invocation" : "replicated_launch_animation_receipt",
                ["projectile_id"] = JValue.CreateNull(), ["launch_event_id"] = JValue.CreateNull(),
                ["launch_real_time_s"] = JValue.CreateNull(), ["timing_uncertainty_s"] = JValue.CreateNull(),
                ["note"] = "A request or launch animation has no projectile ID and may precede a delayed rail launch. No correlation is inferred." });
        }

        private static JArray Rotation(Quaternion value) => new JArray(value.x, value.y, value.z, value.w);

        private JObject Motion(Unit unit)
        {
            var body = unit.rb;
            var data = new JObject { ["entity_id"] = EntityId(unit), ["game_persistent_id"] = unit.persistentID.Id,
                ["definition_key"] = unit.definition?.jsonKey, ["runtime_class"] = unit.GetType().FullName,
                ["position_m"] = Vector(unit.transform.position - Datum.originPosition),
                ["rotation_xyzw"] = Rotation(unit.transform.rotation),
                ["rigidbody_position_m"] = body != null ? Vector(body.position - Datum.originPosition) : null,
                ["rigidbody_rotation_xyzw"] = body != null ? Rotation(body.rotation) : null,
                ["velocity_mps"] = body != null ? Vector(body.velocity) : null,
                ["angular_velocity_rad_s"] = body != null ? Vector(body.angularVelocity) : null,
                ["disabled"] = unit.disabled, ["damage_state"] = unit.unitState.ToString(),
                ["is_server"] = unit.IsServer, ["local_simulation"] = unit.LocalSim,
                ["sample_real_time_s"] = RealSeconds, ["sample_game_time_s"] = Time.timeAsDouble,
                ["motion_source"] = unit.LocalSim ? "local_simulation" : "client_representation_interpolated_or_extrapolated",
                ["knowledge"] = "simulation; not proof of player awareness" };
            if (unit is Missile missile)
            {
                missile.ownerID.TryGetUnit(out var owner);
                missile.targetID.TryGetUnit(out var target);
                data["observed_game_owner_id"] = missile.ownerID.Id;
                data["observed_game_target_id"] = missile.targetID.Id;
                data["observed_owner_entity_id"] = EntityId(owner);
                data["observed_target_entity_id"] = EntityId(target);
                data["projectile_observation_event_id"] = Ledger.Observation(EntityId(unit));
            }
            if (unit is Aircraft aircraft)
            {
                data["gear_deployed"] = aircraft.gearDeployed;
                data["gear_state"] = aircraft.gearState.ToString();
                var inputs = aircraft.GetInputs();
                data["control_inputs"] = inputs == null ? null : new JObject {
                    ["pitch"] = inputs.pitch, ["roll"] = inputs.roll, ["yaw"] = inputs.yaw,
                    ["throttle"] = inputs.throttle, ["brake"] = inputs.brake, ["custom_axis_1"] = inputs.customAxis1 };
                data["control_inputs_note"] = "Observed inputs, possibly network-compressed; not control-surface transforms.";
            }
            return data;
        }

        private JObject PlayerAwareness()
        {
            if (player == null) return null;
            var warning = player.GetMissileWarningSystem();
            var incoming = new JArray();
            if (warning != null)
                foreach (var missile in warning.knownMissiles)
                    if (missile != null) incoming.Add(new JObject { ["projectile_id"] = EntityId(missile),
                        ["game_projectile_id"] = missile.persistentID.Id });
            return new JObject { ["incoming_warning_projectiles"] = warning != null ? incoming : null,
                ["source"] = "MissileWarning.knownMissiles", ["player_noticed_warning"] = JValue.CreateNull(),
                ["countermeasure_trigger"] = player.countermeasureTrigger,
                ["countermeasure_station_index"] = player.countermeasureManager != null ? (JToken)player.countermeasureManager.activeIndex : JValue.CreateNull(),
                ["note"] = "Warning-system state is available to HUD; it does not prove the player noticed it. Countermeasure trigger is not deployment proof." };
        }

        private void SampleMotion(bool missiles)
        {
            var timer = Stopwatch.StartNew();
            // This is a maintained registry, not a Unity world search. Include referenced units that have not registered yet.
            var units = registry.Values.Where(u => InScope(u) && (u is Missile) == missiles).ToArray();
            int limit = Math.Max(1, Math.Min(4096, motionLimit.Value));
            int count = Math.Min(limit, units.Length);
            int cursor = missiles ? projectileCursor : worldCursor;
            var snapshots = new JArray();
            var unchanged = new JArray();
            double refresh = missiles || float.IsNaN(unchangedRefresh.Value) ? 0 : Math.Max(0, Math.Min(3600, unchangedRefresh.Value));
            for (int i = 0; i < count; i++)
            {
                Unit unit = units[(cursor + i) % units.Length];
                Safe("motion_entity", () =>
                {
                    Registered(unit, "capture_scope_entry");
                    var motion = Motion(unit);
                    if (refresh > 0)
                    {
                        string id = (string)motion["entity_id"], key = MotionKey(motion);
                        if (lastMotion.TryGetValue(id, out var last) && last.key == key && RealSeconds - last.written < refresh)
                        { unchanged.Add(id); return; }
                        lastMotion[id] = (key, RealSeconds);
                    }
                    snapshots.Add(motion);
                });
            }
            if (units.Length != 0) cursor = (cursor + count) % units.Length;
            if (missiles) projectileCursor = cursor; else worldCursor = cursor;
            var record = Record("world_motion", new JObject { ["entities"] = snapshots,
                ["unchanged_entity_ids"] = unchanged, ["unchanged_sample_real_time_s"] = RealSeconds,
                ["population"] = missiles ? "missiles" : "other_units", ["eligible_entities"] = units.Length,
                ["omitted_due_to_limit"] = units.Length - count, ["failed_entity_samples"] = count - snapshots.Count - unchanged.Count,
                ["requested_hz"] = Rate(missiles ? projectileHz.Value : worldHz.Value, missiles ? 60 : 30),
                ["capture_read_ms"] = timer.Elapsed.TotalMilliseconds,
                ["datum_origin_position_m"] = Vector(Datum.originPosition),
                ["source_event_time_s"] = JValue.CreateNull(), ["time_basis"] = "local_sample" }, true);
            writer.Enqueue("states", record.ToString(Formatting.None));
        }

        // Every snapshot field except the sample clocks.
        private static string MotionKey(JObject motion)
        {
            var copy = (JObject)motion.DeepClone();
            copy.Remove("sample_real_time_s"); copy.Remove("sample_game_time_s");
            return copy.ToString(Formatting.None);
        }

        private static float Rate(float value, int maximum) => float.IsNaN(value) || float.IsInfinity(value) ? 1 : Math.Max(1, Math.Min(maximum, value));

        private void UpdateObservation()
        {
            if (currentMission == null || !MissionManager.IsRunning) return;
            if (!registrySeeded)
            {
                registrySeeded = true;
                // One bootstrap per mission catches pre-existing units without repeated discovery scans.
                foreach (var unit in UnitRegistry.allUnits.ToArray()) Safe("registry_bootstrap", () => Registered(unit, "registry_bootstrap"));
                Emit("registry_bootstrapped", new JObject { ["local_registry_count"] = registry.Count,
                    ["coverage"] = "locally_registered_units_only; completeness_not_established" });
            }
            if (player != null) Registered(player, "player_reference");
            foreach (var target in selectionReferences) if (target != null) Registered(target, "selection_reference");
            double now = RealSeconds;
            if (now >= nextWorld) { nextWorld = now + 1.0 / Rate(worldHz.Value, 30); Safe("world_motion", () => SampleMotion(false)); }
            if (now >= nextProjectile) { nextProjectile = now + 1.0 / Rate(projectileHz.Value, 60); Safe("projectile_motion", () => SampleMotion(true)); }
            if (now >= nextEnvironment) { nextEnvironment = now + 1; Safe("mission_context", SampleMissionContext); }
        }

        private void SampleMissionContext()
        {
            var level = NetworkSceneSingleton<LevelInfo>.i;
            var data = new JObject { ["map_key_type"] = currentMission.MapKey.Type.ToString(), ["map_key_path"] = currentMission.MapKey.Path,
                ["datum_origin_position_m"] = Vector(Datum.originPosition), ["knowledge"] = "simulation_environment",
                ["time_of_day_game_value"] = level != null ? (JToken)level.timeOfDay : JValue.CreateNull(),
                ["conditions_game_value"] = level != null ? (JToken)level.conditions : JValue.CreateNull(),
                ["cloud_height_m"] = level != null ? (JToken)level.cloudHeight : JValue.CreateNull(),
                ["wind_velocity_mps"] = level != null ? Vector(level.windVelocity) : null,
                ["wind_turbulence_game_value"] = level != null ? (JToken)level.windTurbulence : JValue.CreateNull(),
                ["moon_phase_game_value"] = level != null && level.moonPhase.HasValue ? (JToken)level.moonPhase.Value : JValue.CreateNull(),
                ["initial_state_complete"] = false };
            writer.Enqueue("states", Record("mission_environment", data, true).ToString(Formatting.None));
            var objectives = new JArray();
            if (currentMission.RuntimeObjectives != null)
                foreach (var objective in currentMission.RuntimeObjectives.AllObjectives)
                    objectives.Add(new JObject { ["key"] = objective.SavedObjective.UniqueName,
                        ["status"] = objective.Status.ToString(), ["runtime_type"] = objective.GetType().FullName });
            string serialized = currentMission.RuntimeObjectives == null ? null : objectives.ToString(Formatting.None);
            if (serialized != previousObjectives)
            {
                previousObjectives = serialized;
                Emit("mission_objectives_observed", new JObject { ["objectives"] = objectives,
                    ["available"] = serialized != null, ["time_basis"] = "polled_change_at_1_hz",
                    ["knowledge"] = "local_mission_state; not proof of player visibility or exact completion time" });
            }
            if (player == null) { previousLoadout = null; return; }
            var stations = new JArray();
            foreach (var station in player.weaponStations)
                if (station != null) stations.Add(new JObject { ["station_number"] = station.Number,
                    ["weapon_name"] = station.WeaponInfo?.weaponName, ["ammo"] = station.Ammo,
                    ["ammo_loaded"] = station.GetAmmoLoaded(), ["ammo_total"] = station.GetAmmoTotal() });
            var loadout = new JObject { ["player_entity_id"] = EntityId(player), ["stations"] = stations };
            string key = loadout.ToString(Formatting.None);
            if (key != previousLoadout)
            {
                previousLoadout = key;
                loadout["time_basis"] = "polled_change_at_1_hz";
                loadout["note"] = "Ammunition changes do not establish a shot or a launch.";
                Emit("player_loadout_observed", loadout);
            }
        }
    }
}
