using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using Mirage.Authentication;
using Mirage.Serialization;
using Mirage.SocketLayer;
using Newtonsoft.Json.Linq;
using NuclearOption.Networking;
using NuclearOption.Networking.Authentication;
using NuclearOption.NetworkTransforms;
using NuclearOption.SavedMission;
using NuclearOption.SavedMission.Objectives;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace NuclearReplay.Demo
{
    // Records a network demo by attaching an invisible spectator connection to the host server.
    // The server then serializes the match for that connection exactly as it would for a remote
    // multiplayer client: spawns, sync vars, RPCs (damage, detonations, effects) and transforms.
    // Replay feeds those bytes into a real game client, so the game itself renders every effect.
    internal sealed class DemoRecorder
    {
        private static readonly FieldInfo ConnectionsField = AccessTools.Field(typeof(NetworkServer), "_connections");
        private static readonly MethodInfo AuthenticationSuccessMethod = AccessTools.Method(typeof(NetworkServer), "AuthenticationSuccess");
        private static readonly MethodInfo OnDisconnectedMethod = AccessTools.Method(typeof(NetworkServer), "OnDisconnected");
        private static readonly MethodInfo SceneReadyMethod = AccessTools.Method(typeof(NetworkManagerNuclearOption), "HandleSceneReadyMessage");
        private static readonly ushort LoadMapId = (ushort)MessagePacker.GetId<LoadMapMessage>();
        private static readonly ushort LoadWaitingId = (ushort)MessagePacker.GetId<LoadWaitingSceneMessage>();

        internal static DemoRecorder Instance;
        internal static int GhostCount => Instance?.player != null ? 1 : 0;

        private readonly ManualLogSource log;
        private readonly string outputDirectory;
        private readonly string modVersion;
        private DemoWriter writer;
        private GhostConnection connection;
        private NetworkPlayer player;
        private NetworkServer server;
        private Mission mission;
        private Mission failedMission;
        private bool menuLoaded;
        private bool sceneReadyPending;
        private int loadMapCount;

        public DemoRecorder(ManualLogSource log, string outputDirectory, string modVersion)
        {
            this.log = log;
            this.outputDirectory = outputDirectory;
            this.modVersion = modVersion;
            Instance = this;
        }

        internal static void InstallPatches(Harmony harmony, Dictionary<string, bool> status)
        {
            void Patch(string label, MethodBase original, string prefix = null, string postfix = null, string finalizer = null)
            {
                try
                {
                    if (original == null) { status[label] = false; return; }
                    harmony.Patch(original, prefix == null ? null : new HarmonyMethod(typeof(DemoRecorder), prefix),
                        postfix == null ? null : new HarmonyMethod(typeof(DemoRecorder), postfix),
                        finalizer: finalizer == null ? null : new HarmonyMethod(typeof(DemoRecorder), finalizer));
                    status[label] = true;
                }
                catch (Exception) { status[label] = false; }
            }
            Patch("demo_singleplayer_transforms", AccessTools.Method(typeof(SendTransformBatcher), "LateUpdate"), prefix: nameof(TransformPrefix));
            Patch("demo_crash_objective_lives", AccessTools.Method(typeof(CrashAircraftObjective), "GetTotalLives"), postfix: nameof(LivesPostfix));
            // Event annotations. Launches, damage scopes and damage credit come through the shared hooks in Hooks.cs.
            Patch("demo_kill_message", AccessTools.Method(typeof(MessageManager), "RpcKillMessage"), prefix: nameof(KillMessagePrefix));
            Patch("demo_player_aircraft", AccessTools.Method(typeof(Player), "SetAircraft"), postfix: nameof(SetAircraftPostfix));
            Patch("demo_player_faction", AccessTools.Method(typeof(FactionHQ), "AddPlayer"), postfix: nameof(AddPlayerPostfix));
            Patch("demo_gun_damage_scope", AccessTools.Method(typeof(DamageEffects), "ArmorPenetrate"), prefix: nameof(ArmorPrefix), finalizer: nameof(DamageWeaponFinalizer));
        }

        // ---- Gameplay event annotations (host only, no scene scans, no per-frame work) ----

        // Weapon name of the damage call in progress on the main thread; read by the damage-credit hook.
        [ThreadStatic] internal static string DamageWeapon;
        // Depth of weapon damage calls in progress (missile impact, missile blast, gun hit). Damage credit inside one
        // is a hit by that weapon; credit outside one (seeker lock, fire, crash) is not.
        [ThreadStatic] internal static int WeaponHitDepth;
        // A kill's hit time is written only when the hit came this many seconds or less before the kill feed.
        internal const double MaxHitAgeSeconds = 20;
        // victim id -> dealer id -> weapon of the dealer's latest damage with a known weapon.
        private readonly Dictionary<uint, Dictionary<uint, string>> damageWeapons = new Dictionary<uint, Dictionary<uint, string>>();
        // victim id -> dealer id -> record time (Time.unscaledTimeAsDouble) of the dealer's latest weapon hit on it.
        private readonly Dictionary<uint, Dictionary<uint, double>> hitTimes = new Dictionary<uint, Dictionary<uint, double>>();
        private readonly HashSet<uint> killedIds = new HashSet<uint>();
        // (shooter id << 32 | target id) -> weapon of the shooter's latest launch at that target. Names the
        // weapon of a kill when no damage call carried one (missiles that detonate after they unregister).
        private readonly Dictionary<ulong, string> launchWeapons = new Dictionary<ulong, string>();
        private readonly HashSet<string> playerEvents = new HashSet<string>();
        private readonly HashSet<string> eventFaults = new HashSet<string>();
        internal static bool Recording => Instance?.writer != null;

        private void SafeEvent(string name, Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                if (!eventFaults.Add(name)) return;
                Fault("event_" + name, e);
            }
        }

        internal static string UnitType(Unit unit) => unit is Missile ? "missile" : unit is Aircraft ? "aircraft" : unit is Ship ? "ship"
            : unit is GroundVehicle ? "vehicle" : unit is Building ? "building" : unit is PilotDismounted ? "pilot"
            : unit is Container ? "container" : unit is Scenery ? "scenery" : "unit";

        private static string FactionName(FactionHQ hq)
        {
            if (hq == null) return null;
            try { return hq.faction != null ? hq.faction.factionName : null; } catch (Exception) { return null; }
        }

        private static string PlayerName(Player player)
        {
            if (player == null) return null;
            try { return player.GetPlayerName()?.RawSteamName; } catch (Exception) { return null; }
        }

        // Unit reference as defined in docs/PRODUCT-CONTRACT.md: id, name, type, faction, player (human units only).
        internal static JObject UnitRef(Unit unit)
        {
            if (unit == null) return null;
            var o = new JObject { ["id"] = unit.persistentID.Id, ["name"] = unit.unitName ?? unit.definition?.unitName, ["type"] = UnitType(unit) };
            var faction = FactionName(unit.NetworkHQ);
            if (faction != null) o["faction"] = faction;
            var name = unit is Aircraft aircraft ? PlayerName(aircraft.Player) : null;
            if (name != null) o["player"] = name;
            return o;
        }

        // Also resolves units that are already gone, through the registry's persistent record.
        private static JObject UnitRef(PersistentID id)
        {
            if (!id.IsValid) return null;
            if (UnitRegistry.TryGetPersistentUnit(id, out var pu) && pu != null)
            {
                if (pu.unit != null) { var live = UnitRef(pu.unit); if (live["player"] == null && PlayerName(pu.player) is string p) live["player"] = p; return live; }
                var o = new JObject { ["id"] = id.Id, ["name"] = pu.unitName ?? pu.definition?.unitName };
                if (pu.definition is AircraftDefinition) o["type"] = "aircraft";
                var faction = FactionName(pu.GetHQ());
                if (faction != null) o["faction"] = faction;
                if (PlayerName(pu.player) is string name) o["player"] = name;
                return o;
            }
            return new JObject { ["id"] = id.Id };
        }

        private void AnnotatePlayer(Player player, Aircraft aircraft)
        {
            if (player == null) return;
            var unit = aircraft != null ? UnitRef(aircraft) : null;
            // Player.HQ can lag the aircraft's faction by a frame; the aircraft's faction is the same team.
            string faction = FactionName(player.HQ) ?? (string)unit?["faction"];
            string key = player.NetId + ":" + (aircraft != null ? aircraft.persistentID.Id.ToString() : "-") + ":" + faction;
            if (!playerEvents.Add(key)) return;
            var o = new JObject { ["event"] = "player", ["name"] = PlayerName(player), ["faction"] = faction };
            if (unit != null) o["unit"] = unit;
            Annotate(o);
        }

        private void AnnotateMissionAndPlayers(NetworkManagerNuclearOption manager)
        {
            string map = mission.MapKey.Path;
            string mapType = mission.MapKey.Type.ToString();
            if (string.IsNullOrEmpty(map) && manager.MapKey.HasValue) { map = manager.MapKey.Value.Path; mapType = manager.MapKey.Value.Type.ToString(); }
            if (string.IsNullOrEmpty(map)) { map = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; mapType = "scene"; }
            Annotate(new JObject { ["event"] = "mission", ["name"] = mission.Name, ["map"] = map, ["map_key_type"] = mapType,
                ["multiplayer"] = server.Listening });
            // Players who joined or spawned before recording started (one pass over the player list).
            foreach (var p in UnitRegistry.playerLookup.Values)
                if (p != null) AnnotatePlayer(p, p.Aircraft);
        }

        internal static void OnLaunch(Missile missile, Unit owner, Unit target)
        {
            var self = Instance;
            if (self?.writer == null || missile == null) return;
            self.SafeEvent("launch", () =>
            {
                string weapon = MissileWeapon(missile);
                if (owner != null && target != null && weapon != null)
                    self.launchWeapons[(ulong)owner.persistentID.Id << 32 | target.persistentID.Id] = weapon;
                self.Annotate(new JObject { ["event"] = "launch", ["shooter"] = UnitRef(owner), ["target"] = UnitRef(target),
                    ["weapon"] = weapon, ["missile"] = missile.persistentID.Id });
            });
        }

        // Called from prefixes on the game's damage code, so it never throws.
        internal static string MissileWeapon(Missile missile)
        {
            if (missile == null) return null;
            try
            {
                var info = missile.GetWeaponInfo();
                return info != null ? info.weaponName : missile.definition?.unitName;
            }
            catch (Exception) { return null; }
        }

        internal static string MissileWeapon(PersistentID missileId)
        {
            if (!Recording || !missileId.IsValid) return null;
            try { return UnitRegistry.TryGetUnit(missileId, out var unit) && unit is Missile missile ? MissileWeapon(missile) : null; }
            catch (Exception) { return null; }
        }

        internal static void OnDamageCredit(Unit victim, PersistentID dealer)
        {
            var self = Instance;
            var weapon = DamageWeapon;
            if (self?.writer == null || victim == null || !dealer.IsValid) return;
            if (weapon == null && WeaponHitDepth <= 0) return;
            uint id = victim.persistentID.Id;
            // The time of the hit, on the record clock: the kill feed comes later, when the victim actually dies.
            if (!self.hitTimes.TryGetValue(id, out var times)) self.hitTimes[id] = times = new Dictionary<uint, double>();
            times[dealer.Id] = Time.unscaledTimeAsDouble;
            if (weapon == null) return;
            if (!self.damageWeapons.TryGetValue(id, out var byDealer)) self.damageWeapons[id] = byDealer = new Dictionary<uint, string>();
            byDealer[dealer.Id] = weapon;
        }

        internal static void EnterWeaponHit() { WeaponHitDepth++; }
        internal static void ExitWeaponHit() { if (WeaponHitDepth > 0) WeaponHitDepth--; }

        // Gun hits: name the dealer's gun whose ballistics match this penetration. Missile scopes set the weapon first.
        private static void ArmorPrefix(float muzzleVelocity, float pierceDamage, PersistentID dealerID, out string __state)
        {
            __state = DamageWeapon;
            EnterWeaponHit();
            if (DamageWeapon != null || !Recording) return;
            try
            {
                if (!UnitRegistry.TryGetUnit(dealerID, out var dealer) || dealer == null) return;
                foreach (var station in dealer.weaponStations)
                {
                    var info = station?.WeaponInfo;
                    if (info != null && Mathf.Approximately(info.muzzleVelocity, muzzleVelocity) && Mathf.Approximately(info.pierceDamage, pierceDamage))
                    { DamageWeapon = info.weaponName; return; }
                }
            }
            catch (Exception) { }
        }

        private static void DamageWeaponFinalizer(string __state) { DamageWeapon = __state; ExitWeaponHit(); }

        // The game's own kill feed message carries the killer the game credited (largest damage share). It comes
        // when the victim dies, often seconds after the hit (fire, breakup, crash), so the kill also carries
        // "hit_ago": seconds from the killer's last weapon hit on the victim to this record.
        private static void KillMessagePrefix(PersistentID killerID, PersistentID killedID)
        {
            var self = Instance;
            if (self?.writer == null || !killedID.IsValid || !self.killedIds.Add(killedID.Id)) return;
            self.SafeEvent("kill", () =>
            {
                string weapon = null;
                double? hitAgo = null;
                if (killerID.IsValid && self.damageWeapons.TryGetValue(killedID.Id, out var byDealer)) byDealer.TryGetValue(killerID.Id, out weapon);
                if (weapon == null && killerID.IsValid) self.launchWeapons.TryGetValue((ulong)killerID.Id << 32 | killedID.Id, out weapon);
                if (killerID.IsValid && self.hitTimes.TryGetValue(killedID.Id, out var times) && times.TryGetValue(killerID.Id, out var hitTime))
                {
                    double ago = Time.unscaledTimeAsDouble - hitTime;
                    if (ago >= 0 && ago <= MaxHitAgeSeconds) hitAgo = Math.Round(ago, 3);
                }
                self.damageWeapons.Remove(killedID.Id);
                self.hitTimes.Remove(killedID.Id);
                var kill = new JObject { ["event"] = "kill", ["victim"] = UnitRef(killedID), ["killer"] = UnitRef(killerID), ["weapon"] = weapon };
                if (hitAgo.HasValue) kill["hit_ago"] = hitAgo.Value;
                self.Annotate(kill);
            });
        }

        private static void SetAircraftPostfix(Player __instance, Aircraft aircraft)
        {
            var self = Instance;
            if (self?.writer == null || __instance == null || !__instance.IsServer) return;
            self.SafeEvent("player", () => self.AnnotatePlayer(__instance, aircraft));
        }

        private static void AddPlayerPostfix(Player player)
        {
            var self = Instance;
            if (self?.writer == null || player == null) return;
            self.SafeEvent("player", () => self.AnnotatePlayer(player, player.Aircraft));
        }

        private static readonly MethodInfo ServerUpdateMethod = AccessTools.Method(typeof(SendTransformBatcher), "ServerUpdate");

        // Transform batches are only built in multiplayer. While the ghost observes a single-player
        // host, build them too; the host's own simulation does not read them.
        private static void TransformPrefix(SendTransformBatcher __instance, NetworkServer ___Server)
        {
            if (GhostCount == 0 || GameManager.gameState == GameState.Multiplayer || NetworkManagerNuclearOption.IsLoadingScene) return;
            if (___Server == null || !___Server.Active) return;
            try { ServerUpdateMethod.Invoke(__instance, null); }
            catch (Exception e)
            {
                // This runs every frame; report a lasting failure once per recording.
                var self = Instance;
                if (self != null && self.eventFaults.Add("transform_server_update")) self.Fault("transform_server_update", e);
            }
        }

        // Mission lives scale with authenticated players; the ghost must not add lives.
        private static void LivesPostfix(ref int __result, ValueWrapperInt ___livesPerPlayer)
        {
            if (GhostCount > 0 && ___livesPerPlayer != null) __result -= ___livesPerPlayer.Value * GhostCount;
        }

        public void Update()
        {
            if (!menuLoaded) { if (MainMenu.State != MainMenu.LoadingState.Loaded) return; menuLoaded = true; }
            var manager = NetworkManagerNuclearOption.i;
            if (manager == null) return;
            var activeServer = manager.Server;
            if (player != null)
            {
                bool serverGone = activeServer == null || !activeServer.Active || !ReferenceEquals(activeServer, server);
                if (serverGone) { Stop("server_stopped", disconnect: false); return; }
                if (!ReferenceEquals(mission, MissionManager.CurrentMission)) { Stop("mission_changed", disconnect: true); return; }
                connection.Tick();
                if (sceneReadyPending && !NetworkManagerNuclearOption.IsLoadingScene && manager.MapKey.HasValue
                    && server.LocalPlayer != null && server.LocalPlayer.SceneIsReady)
                {
                    sceneReadyPending = false;
                    Annotate(new JObject { ["event"] = "ghost_scene_ready", ["load_map_count"] = loadMapCount });
                    SceneReadyMethod.Invoke(manager, new object[] { player, default(SceneReadyMessage) });
                }
                // The writer accepts nothing after a fault: end this mission's demo instead of recording into a void.
                if (writer.Fault != null) { failedMission = mission; Stop("writer_failed", disconnect: true); }
                return;
            }
            if (activeServer == null || !activeServer.Active || NetworkManagerNuclearOption.IsLoadingScene) return;
            if (!manager.MapKey.HasValue || activeServer.LocalPlayer == null || !activeServer.LocalPlayer.SceneIsReady) return;
            if (MissionManager.CurrentMission == null || GameManager.gameState == GameState.Editor) return;
            if (ReferenceEquals(failedMission, MissionManager.CurrentMission)) return;
            Start(manager, activeServer);
        }

        private void Start(NetworkManagerNuclearOption manager, NetworkServer activeServer)
        {
            server = activeServer;
            mission = MissionManager.CurrentMission;
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var safeName = string.Join("_", (mission.Name ?? "mission").Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(outputDirectory, stamp + "-" + safeName + DemoFormat.Extension);
            var header = new JObject
            {
                ["format"] = "nuclear-context-network-demo", ["format_version"] = DemoFormat.Version,
                ["mod_version"] = modVersion, ["game_version"] = Application.version, ["unity_version"] = Application.unityVersion,
                ["created_utc"] = DateTime.UtcNow.ToString("O"), ["mission_name"] = mission.Name,
                ["map_key_type"] = manager.MapKey.Value.Type.ToString(), ["map_key_path"] = manager.MapKey.Value.Path,
                ["game_state"] = GameManager.gameState.ToString(), ["server_listening"] = server.Listening,
                ["mission_running"] = MissionManager.IsRunning,
                ["start_unscaled_time"] = Time.unscaledTimeAsDouble, ["start_server_network_time"] = server.World.Time.Time,
                ["time_semantics"] = "Record time is recorder Time.unscaledTimeAsDouble; net time is server NetworkTime.Time at send.",
                ["observer"] = "Invisible spectator connection added on the host; receives what a remote multiplayer client receives."
            };
            try
            {
                // Inside the try: a file that cannot be created fails this mission once instead of every frame.
                writer = new DemoWriter(path, header.ToString(Newtonsoft.Json.Formatting.None));
                connection = new GhostConnection(OnSend);
                player = new NetworkPlayer(connection, isHost: false, server, null);
                // Single-player hosts do not publish the mission; remote clients need it for objectives and factions.
                if (!server.Listening)
                    manager.NetworkMission.Set(mission, MissionManager.IsRunning ? NetworkMission.State.Running : NetworkMission.State.Loaded);
                var connections = (Dictionary<IConnection, INetworkPlayer>)ConnectionsField.GetValue(server);
                connections[connection] = player;
                var data = NetworkAuthenticatorNuclearOption.AuthData.FromUdp(PlayerType.Spectator, "Nuclear Replay Recorder");
                var result = AuthenticationResult.CreateSuccess(manager.Authenticator, data);
                // Authentication sends the mission and LoadMapMessage; OnSend then schedules the scene-ready reply.
                AuthenticationSuccessMethod.Invoke(server, new object[] { player, result });
                Annotate(new JObject { ["event"] = "ghost_authenticated" });
                SafeEvent("mission", () => AnnotateMissionAndPlayers(manager));
                log.LogInfo("Demo recording: " + path);
            }
            catch (Exception e)
            {
                Fault("ghost_attach", e);
                failedMission = mission;
                Stop("attach_failed", disconnect: true);
            }
        }

        private void OnSend(byte channel, byte[] data, int offset, int length)
        {
            // A stopped recording can leave the ghost attached (shutdown, stopped server); never throw into the server's send.
            if (writer == null) return;
            double netTime = 0;
            try { netTime = server.World.Time.Time; } catch { }
            writer.Message(Time.unscaledTimeAsDouble, netTime, channel, data, offset, length);
            if (length >= 2)
            {
                ushort id = (ushort)(data[offset] | data[offset + 1] << 8);
                if (id == LoadMapId) { loadMapCount++; sceneReadyPending = true; }
                else if (id == LoadWaitingId) sceneReadyPending = false;
            }
        }

        private void Annotate(JObject annotation)
        {
            double netTime = 0;
            try { netTime = server.World.Time.Time; } catch { }
            writer?.Annotation(Time.unscaledTimeAsDouble, netTime, annotation.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void Fault(string where, Exception e)
        {
            log.LogError("Demo " + where + " failed: " + e);
            try { Annotate(new JObject { ["event"] = "fault", ["where"] = where, ["error"] = e.ToString() }); } catch { }
        }

        public void Stop(string reason, bool disconnect)
        {
            if (writer == null) return;
            try { Annotate(new JObject { ["event"] = "end", ["reason"] = reason, ["messages"] = connection.Messages, ["bytes"] = connection.Bytes }); } catch { }
            var finishedPlayer = player;
            player = null;
            if (disconnect && finishedPlayer != null && server != null && server.Active)
            {
                try
                {
                    connection.Disconnect();
                    OnDisconnectedMethod.Invoke(server, new object[] { finishedPlayer });
                }
                catch (Exception e) { log.LogWarning("Demo ghost detach failed: " + e); }
            }
            // A faulted writer may be stuck on a stalled disk: do not wait for it on the game thread.
            bool finished = writer.Close(writer.Fault != null ? 0 : DemoWriter.DefaultCloseWaitMilliseconds);
            var summary = $"{writer.Path}, {connection.Messages} messages, {connection.Bytes} bytes";
            if (writer.Fault != null)
                log.LogError($"Demo stopped ({reason}): {writer.Fault}. The file replays up to that point: {summary}");
            else
                log.LogInfo($"Demo finished ({reason}): {summary}" + (finished ? "" : " (still writing in the background)"));
            writer = null;
            connection = null;
            server = null;
            mission = null;
            sceneReadyPending = false;
            loadMapCount = 0;
            damageWeapons.Clear(); hitTimes.Clear(); launchWeapons.Clear(); killedIds.Clear(); playerEvents.Clear(); eventFaults.Clear();
        }
    }
}
