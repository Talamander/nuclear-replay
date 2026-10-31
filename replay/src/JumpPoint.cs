using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using Mirage.RemoteCalls;
using Mirage.Serialization;
using NuclearOption.Networking;
using NuclearOption.NetworkTransforms;
using NuclearOption.SceneLoading;
using NuclearReplay.Demo;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // How the jump point burst treats one client RPC (docs/JUMP-POINTS.md, "RPC rules").
    internal enum JumpRpcClass { Skip, All, Latest }

    internal static class JumpRpcRules
    {
        // Lasting state that adds up: every call counts, in recorded order.
        private static readonly HashSet<string> All = new HashSet<string>
        {
            "Unit.RpcDamage", "Aircraft.RpcDamage", "Unit.RpcBreakPart", "PilotDismounted.RpcTakeDamage",
            "Aircraft.RpcLaunchMissile", "Unit.RpcRearm", "Airbase.RpcRegisterUsage", "FactionHQ.RpcExclusionZone",
            "Building.RpcSetAirbase",
        };

        // Lasting state where the last call wins: group name -> leading payload bytes that belong to the key
        // (a station or part index). Calls in one group replace each other.
        private static readonly Dictionary<string, (string Group, int KeyBytes)> Latest = new Dictionary<string, (string, int)>
        {
            ["Aircraft.RpcToggleRadar"] = ("radar", 0),
            ["Aircraft.RpcCountermeasures"] = ("cm", 0),
            ["Aircraft.RpcSetActiveStation"] = ("active", 0),
            ["Aircraft.RpcSetTurretVector"] = ("turretvec", 1),
            ["Aircraft.RpcSetStationTurretTarget"] = ("turrettgt", 2),
            ["Aircraft.RpcSlingLoadAttachment"] = ("sling", 0),
            ["Aircraft.RpcRefuel"] = ("refuel", 0),
            ["Aircraft.RpcJettisonCanopy"] = ("canopy", 0),
            ["Aircraft.RpcEscapeCapsule"] = ("capsule", 0),
            ["Unit.RpcSetStationTargets"] = ("targets", 1),
            ["Unit.RpcSyncAmmoCount"] = ("ammo", 1),
            ["Unit.RpcSingleRemoteFire"] = ("ammo", 1),
            ["Unit.RpcAssignRadar"] = ("assignradar", 0),
            ["Unit.RpcUpdateRearmerCapacity"] = ("rearmer", 0),
            ["Unit.RpcFuelTankStatus"] = ("fueltank", 1),
            ["GroundVehicle.RpcDeployFireControl"] = ("firecontrol", 0),
            ["Building.RpcToggleRepairStatus"] = ("repair", 0),
            ["Hangar.RpcOpenHangar"] = ("hangar", 0),
            ["Hangar.RpcCloseHangar"] = ("hangar", 0),
            ["Hangar.RpcHangarAvailable"] = ("hangaravail", 0),
            ["Missile.RpcUnfoldFins"] = ("fins", 0),
            // The whole payload is the key, so each tracked unit keeps one call per faction.
            ["FactionHQ.RpcUpdateTrackingInfo"] = ("tracking", int.MaxValue),
        };

        public static JumpRpcClass Classify(string name, out string group, out int keyBytes)
        {
            group = null; keyBytes = 0;
            if (All.Contains(name)) return JumpRpcClass.All;
            if (Latest.TryGetValue(name, out var rule)) { group = rule.Group; keyBytes = rule.KeyBytes; return JumpRpcClass.Latest; }
            return JumpRpcClass.Skip;
        }
    }

    // One jump: the records that rebuild the world at the cut time, and where normal playback goes on.
    //
    // The client must have its scene loaded before the rest of the plan can be made (scene objects and prefabs name
    // the RPCs), so the prelude goes first and BuildBurst runs when the client answers SceneReady.
    internal sealed class JumpPlan
    {
        private static readonly ushort LoadMapId = (ushort)MessagePacker.GetId<LoadMapMessage>();
        private static readonly ushort SpawnId = (ushort)MessagePacker.GetId<SpawnMessage>();
        private static readonly ushort DestroyId = (ushort)MessagePacker.GetId<ObjectDestroyMessage>();
        private static readonly ushort HideId = (ushort)MessagePacker.GetId<ObjectHideMessage>();
        private static readonly ushort VarsId = (ushort)MessagePacker.GetId<UpdateVarsMessage>();
        private static readonly ushort RpcId = (ushort)MessagePacker.GetId<RpcMessage>();
        private static readonly ushort TransformId = (ushort)MessagePacker.GetId(AccessTools.Inner(typeof(SendTransformBatcher), "TransformMessage"));
        private const int MaxTransformBytes = 1000;

        private sealed class Tracked
        {
            public int Segment, SeenSegment;
            public ulong? SceneId;
            public int? PrefabHash;
            public readonly List<int> Kept = new List<int>();
            public readonly Dictionary<string, int> Latest = new Dictionary<string, int>();
        }

        private readonly ManualLogSource log;
        private readonly List<DemoRecord> records;
        private readonly int firstLoad, segment;
        private readonly List<int> loads;
        private Dictionary<uint, (int Record, int BitPosition, int Bits)> lastTransforms;

        public List<DemoRecord> Prelude { get; }
        public int ResumeIndex { get; }
        public double CutTime { get; }
        public double CutNetTime { get; }
        public string Summary { get; private set; } = "";

        private JumpPlan(ManualLogSource log, List<DemoRecord> records, List<int> loads, int segment, int resumeIndex)
        {
            this.log = log;
            this.records = records;
            this.loads = loads;
            this.segment = segment;
            firstLoad = loads[0];
            ResumeIndex = resumeIndex;
            CutTime = records[resumeIndex].Time;
            CutNetTime = records[resumeIndex].NetTime;
            Prelude = new List<DemoRecord>();
            for (int i = 0; i < firstLoad; i++) if (records[i].Kind == DemoFormat.KindMessage) Prelude.Add(records[i]);
            Prelude.Add(records[loads[segment]]);
        }

        // Null when the recording has no map load before the cut.
        public static JumpPlan Create(ManualLogSource log, List<DemoRecord> records, double cutTime)
        {
            var loads = new List<int>();
            int resume = -1;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].Time > cutTime) { resume = i; break; }
                if (records[i].Kind == DemoFormat.KindMessage && records[i].MessageId == LoadMapId) loads.Add(i);
            }
            if (loads.Count == 0 || resume < 0) return null;
            while (records[resume].Kind != DemoFormat.KindMessage) { if (++resume >= records.Count) return null; }
            return new JumpPlan(log, records, loads, loads.Count - 1, resume);
        }

        public List<DemoRecord> BuildBurst()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var names = new RpcNames(log);
            var stats = new SortedDictionary<string, long[]>();
            var tracked = new Dictionary<uint, Tracked>();
            var kept = new List<int>();
            var transforms = new Dictionary<uint, (int Record, int BitPosition, int Bits)>();
            double transformStamp = CutNetTime;
            int seg = 0;
            int objectsSpawned = 0, objectsDestroyed = 0;
            for (int i = firstLoad + 1; i < ResumeIndex; i++)
            {
                var r = records[i];
                if (r.Kind != DemoFormat.KindMessage) continue;
                ushort id = r.MessageId;
                if (id == LoadMapId) { seg++; continue; }
                if (id == SpawnId)
                {
                    var m = MessagePacker.Unpack<SpawnMessage>(r.Payload, null);
                    var o = new Tracked { Segment = seg, SeenSegment = seg, SceneId = m.SceneId, PrefabHash = m.PrefabHash };
                    tracked[m.NetId] = o;
                    o.Kept.Add(i);
                    objectsSpawned++;
                }
                else if (id == DestroyId) { if (tracked.Remove(MessagePacker.Unpack<ObjectDestroyMessage>(r.Payload, null).NetId)) objectsDestroyed++; }
                else if (id == HideId) { if (tracked.Remove(MessagePacker.Unpack<ObjectHideMessage>(r.Payload, null).NetId)) objectsDestroyed++; }
                else if (id == VarsId)
                {
                    var m = MessagePacker.Unpack<UpdateVarsMessage>(r.Payload, null);
                    if (tracked.TryGetValue(m.NetId, out var o)) { o.SeenSegment = seg; o.Kept.Add(i); }
                }
                else if (id == RpcId)
                {
                    var m = MessagePacker.Unpack<RpcMessage>(r.Payload, null);
                    if (!tracked.TryGetValue(m.NetId, out var o)) { Count(stats, "(rpc on a gone object)", 0); continue; }
                    o.SeenSegment = seg;
                    string name = names.Name(o.SceneId, o.PrefabHash, m.FunctionIndex);
                    var kind = JumpRpcRules.Classify(name, out var group, out int keyBytes);
                    Count(stats, name, kind == JumpRpcClass.Skip ? 0 : 1);
                    if (kind == JumpRpcClass.All) o.Kept.Add(i);
                    else if (kind == JumpRpcClass.Latest) o.Latest[LatestKey(group, keyBytes, m.Payload)] = i;
                }
                else if (id == TransformId) { if (seg == segment) ReadTransforms(r, i, tracked, seg, transforms, ref transformStamp); }
                else if (seg == segment || IsConnectionLevel(id)) kept.Add(i);
            }

            foreach (var o in tracked.Values)
            {
                // Objects from an earlier mission stay only if this mission still talks about them (players).
                if (o.Segment != segment && o.SeenSegment != segment) continue;
                kept.AddRange(o.Kept);
                kept.AddRange(o.Latest.Values);
            }
            kept.Sort();
            var burst = new List<DemoRecord>(kept.Count + 64);
            foreach (int i in kept) burst.Add(records[i]);
            long burstBytes = 0;
            foreach (var r in burst) burstBytes += r.Payload.Length;
            int batches = AppendTransforms(burst, tracked, transforms, transformStamp, out int entries);
            lastTransforms = transforms;
            Summary = $"segment {segment}/{loads.Count}, {ResumeIndex} records read in {watch.ElapsedMilliseconds} ms, {tracked.Count} live of {objectsSpawned} spawned ({objectsDestroyed} gone), "
                + $"burst {burst.Count} records {burstBytes} bytes + {entries} transforms in {batches} batches";
            log.LogInfo("Jump plan: " + Summary);
            foreach (var kv in stats.OrderByDescending(k => k.Value[0]))
                log.LogInfo($"Jump plan rpc: {kv.Key} {kv.Value[0]} seen, {kv.Value[1]} before dedupe");
            return burst;
        }

        // A stopped vehicle is kinematic on the client and ignores transform snapshots (GroundVehicleNetworkTransform),
        // so one that drove and parked would stay at its spawn. Places those directly from their last recorded transform.
        public int PlaceStoppedVehicles(NetworkWorld world)
        {
            int placed = 0;
            foreach (var kv in lastTransforms)
            {
                if (!world.TryGetIdentity(kv.Key, out var identity) || identity == null) continue;
                var vehicle = identity.GetComponent<GroundVehicle>();
                if (vehicle == null || vehicle.rb == null || !vehicle.rb.isKinematic) continue;
                using var outer = NetworkReaderPool.GetReader(records[kv.Value.Record].Payload, null);
                outer.ReadUInt16();
                outer.ReadDouble();
                var data = outer.ReadBytesAndSizeSegment();
                using var reader = NetworkReaderPool.GetReader(data, null);
                reader.MoveBitPosition(kv.Value.BitPosition);
                reader.ReadPackedUInt32();
                var position = reader.ReadGlobalPosition().ToLocalPosition();
                var rotation = reader.Read<Quaternion>();
                vehicle.transform.SetPositionAndRotation(position, rotation);
                vehicle.rb.position = position;
                vehicle.rb.rotation = rotation;
                placed++;
            }
            return placed;
        }

        private static void Count(SortedDictionary<string, long[]> stats, string key, int keptCount)
        {
            if (!stats.TryGetValue(key, out var v)) stats[key] = v = new long[2];
            v[0]++; v[1] += keptCount;
        }

        private static string LatestKey(string group, int keyBytes, ArraySegment<byte> payload)
        {
            int n = Math.Min(keyBytes, payload.Count);
            if (n <= 0) return group;
            var key = new StringBuilder(group, group.Length + n * 2 + 1).Append(':');
            for (int i = 0; i < n; i++) key.Append(payload.Array[payload.Offset + i].ToString("x2"));
            return key.ToString();
        }

        private static bool IsConnectionLevel(ushort id)
            => MessagePacker.MessageTypes.TryGetValue(id, out var type) && type.FullName.Contains("StringStore");

        // A transform batch: packed id, double timestamp, then a sized byte block of entries. Each entry is a 16-bit
        // bit length, then that many bits, which start with the object's packed net id.
        private static void ReadTransforms(DemoRecord r, int index, Dictionary<uint, Tracked> tracked, int seg,
            Dictionary<uint, (int, int, int)> latest, ref double stamp)
        {
            using var outer = NetworkReaderPool.GetReader(r.Payload, null);
            outer.ReadUInt16();
            stamp = outer.ReadDouble();
            var data = outer.ReadBytesAndSizeSegment();
            using var reader = NetworkReaderPool.GetReader(data, null);
            while (reader.CanReadBits(16))
            {
                int bits = (int)reader.Read(16);
                int start = reader.BitPosition;
                uint netId = reader.ReadPackedUInt32();
                latest[netId] = (index, start, bits);
                if (tracked.TryGetValue(netId, out var o)) o.SeenSegment = seg;
                reader.MoveBitPosition(start + bits);
            }
        }

        private int AppendTransforms(List<DemoRecord> burst, Dictionary<uint, Tracked> tracked,
            Dictionary<uint, (int Record, int BitPosition, int Bits)> latest, double stamp, out int entries)
        {
            entries = 0;
            int batches = 0;
            using var body = NetworkWriterPool.GetWriter();
            using var message = NetworkWriterPool.GetWriter();
            void Flush()
            {
                if (body.ByteLength == 0) return;
                message.Reset();
                message.WriteUInt16(TransformId);
                message.WriteDouble(stamp);
                message.WriteBytesAndSizeSegment(body.ToArraySegment());
                burst.Add(new DemoRecord { Kind = DemoFormat.KindMessage, Time = CutTime, NetTime = CutNetTime, Channel = DemoFormat.ChannelReliable, Payload = message.ToArray() });
                body.Reset();
                batches++;
            }
            foreach (var kv in latest)
            {
                if (!tracked.TryGetValue(kv.Key, out var o) || o.Segment != segment && o.SeenSegment != segment) continue;
                using var outer = NetworkReaderPool.GetReader(records[kv.Value.Record].Payload, null);
                outer.ReadUInt16();
                outer.ReadDouble();
                var data = outer.ReadBytesAndSizeSegment();
                using var reader = NetworkReaderPool.GetReader(data, null);
                reader.MoveBitPosition(kv.Value.BitPosition);
                if (body.ByteLength + kv.Value.Bits / 8 + 3 > MaxTransformBytes) Flush();
                body.Write((ulong)kv.Value.Bits, 16);
                for (int left = kv.Value.Bits; left > 0;)
                {
                    int n = Math.Min(left, 64);
                    body.Write(reader.Read(n), n);
                    left -= n;
                }
                entries++;
            }
            Flush();
            return batches;
        }

        // Names an RPC from the spawn that made its object: the scene object or prefab holds the same RPC table.
        private sealed class RpcNames
        {
            private readonly ManualLogSource log;
            private readonly Dictionary<ulong, NetworkIdentity> scene = new Dictionary<ulong, NetworkIdentity>();
            private readonly Dictionary<long, string[]> tables = new Dictionary<long, string[]>();

            public RpcNames(ManualLogSource log)
            {
                this.log = log;
                foreach (var identity in Resources.FindObjectsOfTypeAll<NetworkIdentity>())
                    if (identity.gameObject.scene.IsValid() && identity.SceneId != 0) scene[identity.SceneId] = identity;
                log.LogInfo($"Jump plan: {scene.Count} scene objects named");
            }

            public string Name(ulong? sceneId, int? prefabHash, int index)
            {
                long key = sceneId.HasValue ? (long)sceneId.Value : prefabHash ?? 0;
                if (!tables.TryGetValue(key, out var table)) tables[key] = table = Table(sceneId, prefabHash);
                return table != null && index >= 0 && index < table.Length ? table[index] : "(unknown rpc)";
            }

            private string[] Table(ulong? sceneId, int? prefabHash)
            {
                NetworkIdentity identity = null;
                if (sceneId.HasValue && sceneId.Value != 0) scene.TryGetValue(sceneId.Value, out identity);
                else if (prefabHash.HasValue)
                {
                    try { identity = NetworkManagerNuclearOption.i.ClientObjectManager.GetSpawnHandler(prefabHash.Value).Prefab; }
                    catch (Exception e) { log.LogWarning($"Jump plan: prefab {prefabHash}: {e.Message}"); }
                }
                if (identity == null) { log.LogWarning($"Jump plan: no object for scene {sceneId} prefab {prefabHash}"); return null; }
                try { return identity.RemoteCallCollection.RemoteCalls.Select(c => c?.Name).ToArray(); }
                catch (Exception e) { log.LogWarning($"Jump plan: rpc table of {identity.name}: {e.Message}"); return null; }
            }
        }
    }
}
