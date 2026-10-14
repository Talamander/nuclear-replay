using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using Mirage.Authentication;
using Mirage.RemoteCalls;
using Mirage.Serialization;
using Newtonsoft.Json.Linq;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearReplay.Demo
{
    // Records a network demo while this game is a client of someone else's server.
    //
    // A client receives almost exactly what the host recorder's ghost connection receives: spawns, sync vars,
    // RPCs and transform batches, including the batches for the client's own aircraft (the server relays them
    // to every player). The differences are fixed up while recording, so the replay player can treat the file
    // like a host demo:
    // - Spawn messages lose their local-player and owner flags. The replay client must not take control of the
    //   recorded player's aircraft; it only watches it, like every other unit.
    // - RPCs the server sends only to the recorded player (reserve notices, credit pop-ups, kick reasons, spawn
    //   UI) are dropped, as are ping replies, RPC replies and authority messages.
    // - RPCs the server sends to everyone except the owner (missile launch, turret aim, sling load) never reach
    //   the owner. They are rebuilt from the requests this client sends, so the recorded aircraft's missiles
    //   leave the rails and its turrets turn in replay.
    // - Sync vars the owner itself sends to the server are copied in as if the server had forwarded them.
    //
    // One demo covers one connection, from authentication to disconnect; mission annotations mark each mission.
    internal sealed class ClientDemoRecorder
    {
        private static readonly ushort AuthSuccessId = (ushort)MessagePacker.GetId<AuthSuccessMessage>();
        private static readonly ushort SpawnId = (ushort)MessagePacker.GetId<SpawnMessage>();
        private static readonly ushort RpcId = (ushort)MessagePacker.GetId<RpcMessage>();
        private static readonly HashSet<ushort> DroppedIds = new HashSet<ushort>
        {
            (ushort)MessagePacker.GetId<NetworkPongMessage>(),
            (ushort)MessagePacker.GetId<RpcReply>(),
            (ushort)MessagePacker.GetId<RemoveAuthorityMessage>(),
            (ushort)MessagePacker.GetId<RemoveCharacterMessage>(),
        };

        // RPCs only the recorded player receives. In replay they would act on the viewer's own UI and session.
        private static readonly HashSet<string> DroppedRpcs = new HashSet<string>
        {
            "NuclearOption.Networking.Player.RpcReserveNotice",
            "NuclearOption.Networking.Player.RpcShowSortieBonus",
            "NuclearOption.Networking.Player.KickReason",
            "NuclearOption.Networking.Player.RpcClearSpawnPending",
            "Spawner.RpcOnGivenAircraftControl",
            "MessageManager.TargetCreditMessage",
        };

        // Owner requests whose server-side broadcast (excludeOwner) skips the owner, mapped to that broadcast.
        // The server forwards the request arguments unchanged, so the request payload is the broadcast payload.
        private static readonly Dictionary<string, string> OwnerExcludedRpcs = new Dictionary<string, string>
        {
            ["Aircraft.CmdLaunchMissile"] = "Aircraft.RpcLaunchMissile",
            ["Aircraft.CmdSetTurretVector"] = "Aircraft.RpcSetTurretVector",
            ["Aircraft.CmdSlingLoadAttachment"] = "Aircraft.RpcSlingLoadAttachment",
        };

        private static readonly Type DataHandlerType = AccessTools.Inner(typeof(NetworkClient), "DataHandler");
        private static readonly AccessTools.FieldRef<object, NetworkClient> DataHandlerClient =
            DataHandlerType != null ? AccessTools.FieldRefAccess<NetworkClient>(DataHandlerType, "_client") : null;

        internal static ClientDemoRecorder Instance;

        // Channel of the packet being received on the main thread. Reliable unless an unreliable or notify packet
        // is being unpacked (the reliable fragment path calls the data handler directly).
        [ThreadStatic] private static byte receiveChannel;

        private readonly ManualLogSource log;
        private readonly string outputDirectory;
        private readonly string modVersion;
        private DemoWriter writer;
        private NetworkClient client;
        private string firstMission;
        private object annotatedMission;
        private float nextPlayerScan;
        private long messages, bytes, rewrittenSpawns, droppedMessages, rebuiltRpcs, ownerVars;
        private readonly HashSet<string> playerEvents = new HashSet<string>();
        private readonly HashSet<uint> killedIds = new HashSet<uint>();
        private readonly HashSet<uint> launchedMissiles = new HashSet<uint>();
        private readonly Dictionary<ulong, string> launchWeapons = new Dictionary<ulong, string>();
        private readonly HashSet<string> faults = new HashSet<string>();

        internal static bool Recording => Instance?.writer != null;

        public ClientDemoRecorder(ManualLogSource log, string outputDirectory, string modVersion)
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
                    harmony.Patch(original, prefix == null ? null : new HarmonyMethod(typeof(ClientDemoRecorder), prefix),
                        postfix == null ? null : new HarmonyMethod(typeof(ClientDemoRecorder), postfix),
                        finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ClientDemoRecorder), finalizer));
                    status[label] = true;
                }
                catch (Exception) { status[label] = false; }
            }
            Patch("client_demo_receive", DataHandlerType == null ? null : AccessTools.Method(DataHandlerType, "ReceiveMessage"), prefix: nameof(ReceivePrefix));
            foreach (var name in new[] { "Mirage.SocketLayer.ReliableConnection", "Mirage.SocketLayer.NoReliableConnection" })
            {
                var type = AccessTools.TypeByName(name);
                var shortName = name.Substring(name.LastIndexOf('.') + 1);
                Patch("client_demo_unreliable_" + shortName, type == null ? null : AccessTools.Method(type, "ReceiveUnreliablePacket"),
                    prefix: nameof(UnreliablePrefix), finalizer: nameof(ChannelFinalizer));
                Patch("client_demo_notify_" + shortName, type == null ? null : AccessTools.Method(type, "ReceiveNotifyPacket"),
                    prefix: nameof(NotifyPrefix), finalizer: nameof(ChannelFinalizer));
            }
            Patch("client_demo_owner_rpc", AccessTools.Method(typeof(ServerRpcSender), nameof(ServerRpcSender.Send)), prefix: nameof(ServerRpcPrefix));
            Patch("client_demo_owner_vars", AccessTools.Method(typeof(SyncVarSender), "SendToRemoteOwner"), prefix: nameof(OwnerVarsPrefix));
            Patch("client_demo_kill_message", AccessTools.GetDeclaredMethods(typeof(MessageManager))
                .FirstOrDefault(m => m.Name.StartsWith("UserCode_RpcKillMessage")), prefix: nameof(KillPrefix));
            Patch("client_demo_missile_start", AccessTools.Method(typeof(Missile), "OnStartNetwork", Type.EmptyTypes), postfix: nameof(MissileStartPostfix));
        }

        // ---- Receive path ----

        private static void UnreliablePrefix() => receiveChannel = DemoFormat.ChannelUnreliable;
        private static void NotifyPrefix() => receiveChannel = DemoFormat.ChannelNotify;
        private static Exception ChannelFinalizer(Exception __exception) { receiveChannel = DemoFormat.ChannelReliable; return __exception; }

        private static void ReceivePrefix(object __instance, ArraySegment<byte> message)
        {
            var self = Instance;
            if (self == null || message.Array == null || message.Count < 2) return;
            try
            {
                ushort id = (ushort)(message.Array[message.Offset] | message.Array[message.Offset + 1] << 8);
                if (self.writer == null)
                {
                    // A recording starts with the server's authentication reply, as a host demo does.
                    if (id != AuthSuccessId) return;
                    var owner = DataHandlerClient?.Invoke(__instance);
                    if (!self.CanStart(owner)) return;
                    self.Start(owner);
                    if (self.writer == null) return;
                }
                else if (!ReferenceEquals(DataHandlerClient?.Invoke(__instance), self.client)) return;
                self.Record(id, message);
            }
            catch (Exception e) { self.Fault("receive", e); }
        }

        private void Record(ushort id, ArraySegment<byte> message)
        {
            if (DroppedIds.Contains(id)) { droppedMessages++; return; }
            var data = new byte[message.Count];
            Buffer.BlockCopy(message.Array, message.Offset, data, 0, message.Count);
            if (id == SpawnId) data = RewriteSpawn(data);
            else if (id == RpcId && IsDroppedRpc(data)) { droppedMessages++; return; }
            Write(receiveChannel, data);
        }

        private byte[] RewriteSpawn(byte[] data)
        {
            var spawn = MessagePacker.Unpack<SpawnMessage>(data, null);
            if (!spawn.IsLocalPlayer && !spawn.IsOwner) return data;
            spawn.IsLocalPlayer = false;
            spawn.IsOwner = false;
            rewrittenSpawns++;
            return MessagePacker.Pack(spawn);
        }

        private bool IsDroppedRpc(byte[] data)
        {
            var rpc = MessagePacker.Unpack<RpcMessage>(data, null);
            var world = client?.World;
            if (world == null || !world.TryGetIdentity(rpc.NetId, out var identity) || identity == null) return false;
            var call = identity.RemoteCallCollection.GetAbsolute(rpc.FunctionIndex);
            return call != null && DroppedRpcs.Contains(call.Name);
        }

        // ---- Owner messages the server never sends back ----

        private static void ServerRpcPrefix(NetworkBehaviour behaviour, int relativeIndex, NetworkWriter writer)
        {
            var self = Instance;
            if (self?.writer == null || behaviour == null || writer == null) return;
            try
            {
                if (!ReferenceEquals(behaviour.Client, self.client)) return;
                var calls = behaviour.Identity.RemoteCallCollection;
                int offset = calls.GetIndexOffset(behaviour);
                var request = calls.GetAbsolute(offset + relativeIndex);
                if (request == null || !OwnerExcludedRpcs.TryGetValue(request.Name, out var broadcastName)) return;
                int broadcast = Array.FindIndex(calls.RemoteCalls, c => c != null && c.Name == broadcastName && ReferenceEquals(c.Behaviour, request.Behaviour));
                if (broadcast < 0) { self.Fault("owner_rpc_lookup", new MissingMemberException(broadcastName)); return; }
                var rpc = new RpcMessage { NetId = behaviour.NetId, FunctionIndex = broadcast, Payload = writer.ToArraySegment() };
                self.Write(DemoFormat.ChannelReliable, MessagePacker.Pack(rpc));
                self.rebuiltRpcs++;
            }
            catch (Exception e) { self.Fault("owner_rpc", e); }
        }

        private static void OwnerVarsPrefix(NetworkIdentity identity, NetworkWriter ownerWriter)
        {
            var self = Instance;
            if (self?.writer == null || identity == null || ownerWriter == null) return;
            try
            {
                if (identity.IsServer || !identity.HasAuthority || !ReferenceEquals(identity.Client, self.client)) return;
                var vars = new UpdateVarsMessage { NetId = identity.NetId, Payload = ownerWriter.ToArraySegment() };
                self.Write(DemoFormat.ChannelReliable, MessagePacker.Pack(vars));
                self.ownerVars++;
            }
            catch (Exception e) { self.Fault("owner_vars", e); }
        }

        private void Write(byte channel, byte[] data)
        {
            writer.Message(Time.unscaledTimeAsDouble, NetTime(), channel, data, 0, data.Length);
            messages++;
            bytes += data.Length;
        }

        // ---- Session ----

        private bool CanStart(NetworkClient owner)
        {
            var manager = NetworkManagerNuclearOption.i;
            if (owner == null || manager == null || !ReferenceEquals(manager.Client, owner) || !owner.Active) return false;
            // A replay plays a demo into the game's own client through the replay player's loopback socket.
            if (owner.SocketFactory is Playback.LoopbackSocketFactory) return false;
            // A host's own client sees the host's world; the host recorder covers that.
            return manager.Server == null || !manager.Server.Active;
        }

        private void Start(NetworkClient owner)
        {
            client = owner;
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var path = Path.Combine(outputDirectory, stamp + "-client" + DemoFormat.Extension);
            var header = new JObject
            {
                ["format"] = "nuclear-context-network-demo", ["format_version"] = DemoFormat.Version,
                ["mod_version"] = modVersion, ["game_version"] = Application.version, ["unity_version"] = Application.unityVersion,
                ["created_utc"] = DateTime.UtcNow.ToString("O"),
                ["game_state"] = GameManager.gameState.ToString(), ["server_listening"] = false,
                ["start_unscaled_time"] = Time.unscaledTimeAsDouble, ["start_server_network_time"] = NetTime(),
                ["time_semantics"] = "Record time is recorder Time.unscaledTimeAsDouble at receive; net time is the client's estimate of server NetworkTime.Time at receive.",
                ["observer"] = "Multiplayer client; records the messages its server sent it, with the recorded player's ownership removed.",
                ["recorded_as"] = "client"
            };
            try
            {
                writer = new DemoWriter(path, header.ToString(Newtonsoft.Json.Formatting.None));
                Annotate(new JObject { ["event"] = "client_authenticated" });
                log.LogInfo("Client demo recording: " + path);
            }
            catch (Exception e)
            {
                log.LogError("Client demo start failed: " + e);
                writer = null;
                client = null;
            }
        }

        public void Update()
        {
            if (writer == null) return;
            if (client == null || !client.Active || !ReferenceEquals(NetworkManagerNuclearOption.i?.Client, client))
            {
                Stop("disconnected");
                return;
            }
            if (writer.Fault != null) { Stop("writer_failed"); return; }
            SafeEvent("mission", AnnotateMission);
            if (Time.unscaledTime >= nextPlayerScan)
            {
                nextPlayerScan = Time.unscaledTime + 2f;
                SafeEvent("player", () =>
                {
                    foreach (var p in UnitRegistry.playerLookup.Values)
                        if (p != null) AnnotatePlayer(p);
                });
            }
        }

        private void AnnotateMission()
        {
            var mission = MissionManager.CurrentMission;
            if (mission == null || ReferenceEquals(mission, annotatedMission) || NetworkManagerNuclearOption.IsLoadingScene) return;
            // The client holds the empty placeholder mission until the server's mission arrives.
            if (ReferenceEquals(mission, NuclearOption.SavedMission.Mission.NullMission) || string.IsNullOrEmpty(mission.Name)) return;
            // The server sets NetworkManagerNuclearOption.MapKey; a client only has the synced mission's key.
            var manager = NetworkManagerNuclearOption.i;
            annotatedMission = mission;
            string map = mission.MapKey.Path, mapType = mission.MapKey.Type.ToString();
            if (string.IsNullOrEmpty(map) && manager != null && manager.MapKey.HasValue) { map = manager.MapKey.Value.Path; mapType = manager.MapKey.Value.Type.ToString(); }
            if (string.IsNullOrEmpty(map)) { map = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; mapType = "scene"; }
            if (firstMission == null) firstMission = mission.Name;
            Annotate(new JObject { ["event"] = "mission", ["name"] = mission.Name, ["map"] = map, ["map_key_type"] = mapType, ["multiplayer"] = true });
        }

        private void AnnotatePlayer(Player player)
        {
            var aircraft = player.Aircraft;
            var unit = aircraft != null ? DemoRecorder.UnitRef(aircraft) : null;
            string faction = FactionName(player.HQ) ?? (string)unit?["faction"];
            string key = player.NetId + ":" + (aircraft != null ? aircraft.persistentID.Id.ToString() : "-") + ":" + faction;
            if (!playerEvents.Add(key)) return;
            string name = null;
            try { name = player.GetPlayerName()?.RawSteamName; } catch (Exception) { }
            var o = new JObject { ["event"] = "player", ["name"] = name, ["faction"] = faction };
            if (unit != null) o["unit"] = unit;
            Annotate(o);
        }

        private static string FactionName(FactionHQ hq)
        {
            if (hq == null) return null;
            try { return hq.faction != null ? hq.faction.factionName : null; } catch (Exception) { return null; }
        }

        // Missiles appear on the client when the server spawns them. Ones already flying when this client joined
        // are counted as launches too; the viewer only uses launches to find moments.
        private static void MissileStartPostfix(Missile __instance)
        {
            var self = Instance;
            if (self?.writer == null || __instance == null || !self.launchedMissiles.Add(__instance.persistentID.Id)) return;
            // Joining a match spawns the missiles already in flight before the client starts the mission; those are not launches.
            if (!MissionManager.IsRunning) return;
            self.SafeEvent("launch", () =>
            {
                var owner = __instance.owner;
                UnitRegistry.TryGetUnit(__instance.targetID, out var target);
                string weapon = DemoRecorder.MissileWeapon(__instance);
                if (owner != null && target != null && weapon != null)
                    self.launchWeapons[(ulong)owner.persistentID.Id << 32 | target.persistentID.Id] = weapon;
                self.Annotate(new JObject { ["event"] = "launch", ["shooter"] = DemoRecorder.UnitRef(owner), ["target"] = DemoRecorder.UnitRef(target),
                    ["weapon"] = weapon, ["missile"] = __instance.persistentID.Id });
            });
        }

        // The server's kill feed, as this client receives it. Clients do not see damage credit, so a client kill
        // names the weapon from the killer's latest launch at the victim and carries no hit time.
        private static void KillPrefix(PersistentID killerID, PersistentID killedID)
        {
            var self = Instance;
            if (self?.writer == null || !killedID.IsValid || !self.killedIds.Add(killedID.Id)) return;
            self.SafeEvent("kill", () =>
            {
                string weapon = null;
                if (killerID.IsValid) self.launchWeapons.TryGetValue((ulong)killerID.Id << 32 | killedID.Id, out weapon);
                self.Annotate(new JObject { ["event"] = "kill", ["victim"] = DemoRecorder.UnitRef(killedID), ["killer"] = DemoRecorder.UnitRef(killerID), ["weapon"] = weapon });
            });
        }

        private double NetTime()
        {
            try { return client?.World?.Time?.Time ?? 0; } catch (Exception) { return 0; }
        }

        private void Annotate(JObject annotation) =>
            writer?.Annotation(Time.unscaledTimeAsDouble, NetTime(), annotation.ToString(Newtonsoft.Json.Formatting.None));

        private void SafeEvent(string name, Action action)
        {
            try { action(); }
            catch (Exception e) { Fault("event_" + name, e); }
        }

        // Reports each failing step once per recording; the game's own handling continues either way.
        private void Fault(string where, Exception e)
        {
            if (!faults.Add(where)) return;
            log.LogError("Client demo " + where + " failed: " + e);
            try { Annotate(new JObject { ["event"] = "fault", ["where"] = where, ["error"] = e.ToString() }); } catch { }
        }

        public void Stop(string reason)
        {
            if (writer == null) return;
            try
            {
                Annotate(new JObject { ["event"] = "end", ["reason"] = reason, ["messages"] = messages, ["bytes"] = bytes,
                    ["rewritten_spawns"] = rewrittenSpawns, ["dropped_messages"] = droppedMessages, ["rebuilt_rpcs"] = rebuiltRpcs, ["owner_vars"] = ownerVars });
            }
            catch { }
            bool finished = writer.Close(writer.Fault != null ? 0 : DemoWriter.DefaultCloseWaitMilliseconds);
            var path = writer.Path;
            var summary = $"{messages} messages, {bytes} bytes, {rewrittenSpawns} spawns rewritten, {droppedMessages} dropped, {rebuiltRpcs} owner RPCs rebuilt, {ownerVars} owner sync vars";
            if (writer.Fault != null)
                log.LogError($"Client demo stopped ({reason}): {writer.Fault}. The file replays up to that point: {path}, {summary}");
            else
            {
                // Name the file after its first mission once the writer is done with it, like host demos.
                if (finished && firstMission != null) path = Rename(path, firstMission);
                log.LogInfo($"Client demo finished ({reason}): {path}, {summary}" + (finished ? "" : " (still writing in the background)"));
            }
            writer = null;
            client = null;
            firstMission = null;
            annotatedMission = null;
            messages = bytes = rewrittenSpawns = droppedMessages = rebuiltRpcs = ownerVars = 0;
            playerEvents.Clear(); killedIds.Clear(); launchedMissiles.Clear(); launchWeapons.Clear(); faults.Clear();
        }

        private string Rename(string path, string mission)
        {
            try
            {
                var name = Path.GetFileName(path);
                var safeName = string.Join("_", mission.Split(Path.GetInvalidFileNameChars()));
                var target = Path.Combine(Path.GetDirectoryName(path), name.Substring(0, name.Length - ("-client" + DemoFormat.Extension).Length)
                    + "-" + safeName + "-client" + DemoFormat.Extension);
                if (File.Exists(target)) return path;
                File.Move(path, target);
                return target;
            }
            catch (Exception e)
            {
                log.LogWarning("Client demo rename failed: " + e.Message);
                return path;
            }
        }
    }
}
