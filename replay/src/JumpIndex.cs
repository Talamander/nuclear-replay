#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuclearReplay.Demo;

namespace NuclearReplay.Playback
{
    // How the jump point burst treats one client RPC (docs/JUMP-POINTS.md, "RPC rules").
    internal enum JumpRpcClass { Unknown, Skip, All, Latest }

    internal readonly struct JumpRpcRule
    {
        public readonly JumpRpcClass Class;
        public readonly int Group, KeyBytes;
        public JumpRpcRule(JumpRpcClass rpcClass, int group, int keyBytes) { Class = rpcClass; Group = group; KeyBytes = keyBytes; }
    }

    internal static class JumpRpcRules
    {
        // Calls in one group replace each other.
        private enum Group { None, Radar, Countermeasures, Active, TurretVector, TurretTarget, Sling, Refuel, Canopy, Capsule, Targets, Ammo, AssignRadar, Rearmer, FuelTank, FireControl, Repair, Hangar, HangarAvailable, Fins, Tracking }

        private static readonly Dictionary<string, JumpRpcRule> Rules = BuildRules();

        private static Dictionary<string, JumpRpcRule> BuildRules()
        {
            var rules = new Dictionary<string, JumpRpcRule>();
            // Lasting state that adds up: every call counts, in recorded order.
            foreach (var name in new[]
            {
                "Unit.RpcDamage", "Aircraft.RpcDamage", "Unit.RpcBreakPart", "PilotDismounted.RpcTakeDamage",
                "Aircraft.RpcLaunchMissile", "Unit.RpcRearm", "Airbase.RpcRegisterUsage", "FactionHQ.RpcExclusionZone",
                "Building.RpcSetAirbase", "FactionHQ.RpcGetTrackingStateBatched",
            }) rules[name] = new JumpRpcRule(JumpRpcClass.All, 0, 0);
            // Lasting state where the last call wins. keyBytes is how many leading payload bytes belong to the key
            // (a station or part index).
            void Latest(string name, Group group, int keyBytes) => rules[name] = new JumpRpcRule(JumpRpcClass.Latest, (int)group, keyBytes);
            Latest("Aircraft.RpcToggleRadar", Group.Radar, 0);
            Latest("Aircraft.RpcCountermeasures", Group.Countermeasures, 0);
            Latest("Aircraft.RpcSetActiveStation", Group.Active, 0);
            Latest("Aircraft.RpcSetTurretVector", Group.TurretVector, 1);
            Latest("Aircraft.RpcSetStationTurretTarget", Group.TurretTarget, 2);
            Latest("Aircraft.RpcSlingLoadAttachment", Group.Sling, 0);
            Latest("Aircraft.RpcRefuel", Group.Refuel, 0);
            Latest("Aircraft.RpcJettisonCanopy", Group.Canopy, 0);
            Latest("Aircraft.RpcEscapeCapsule", Group.Capsule, 0);
            Latest("Unit.RpcSetStationTargets", Group.Targets, 1);
            Latest("Unit.RpcSyncAmmoCount", Group.Ammo, 1);
            Latest("Unit.RpcSingleRemoteFire", Group.Ammo, 1);
            Latest("Unit.RpcAssignRadar", Group.AssignRadar, 0);
            Latest("Unit.RpcUpdateRearmerCapacity", Group.Rearmer, 0);
            Latest("Unit.RpcFuelTankStatus", Group.FuelTank, 1);
            Latest("GroundVehicle.RpcDeployFireControl", Group.FireControl, 0);
            Latest("Building.RpcToggleRepairStatus", Group.Repair, 0);
            Latest("Hangar.RpcOpenHangar", Group.Hangar, 0);
            Latest("Hangar.RpcCloseHangar", Group.Hangar, 0);
            Latest("Hangar.RpcHangarAvailable", Group.HangarAvailable, 0);
            Latest("Missile.RpcUnfoldFins", Group.Fins, 0);
            // The whole payload is the key, so each tracked unit keeps one call per faction.
            Latest("FactionHQ.RpcUpdateTrackingInfo", Group.Tracking, int.MaxValue);
            // Explicitly classified transient/UI calls. A newly introduced RPC must fall back instead of silently
            // discarding state that a newer game might require.
            foreach (var name in new[]
            {
                "Aircraft.RpcGetRadarWarning", "FactionHQ.RpcDeclareEndGame", "Missile.RpcDetonate", "Unit.RpcJam",
                "MessageManager.RpcAllHQMessage", "MessageManager.RpcBombFailMessage", "MessageManager.RpcHQMessage",
                "MessageManager.RpcKillMessage", "MessageManager.RpcPilotCaptureMessage", "MessageManager.RpcPlayerJoinFactionMessage",
                "MessageManager.RpcRepairMessage", "MessageManager.RpcWarheadDestroyedMessage", "MessageManager.TargetCreditMessage",
                "MissionMessages.RpcShowMessage", "NuclearOption.Chat.ChatManager.RpcServerMessage",
                "NuclearOption.Chat.ChatManager.RpcTargetServerMessage", "NuclearOption.Chat.ChatManager.TargetReceiveMessage",
                "NuclearOption.Networking.Player.KickReason", "NuclearOption.Networking.Player.RpcClearSpawnPending",
                "NuclearOption.Networking.Player.RpcConvoyDonationMessage", "NuclearOption.Networking.Player.RpcFundsDonationMessage",
                "NuclearOption.Networking.Player.RpcReserveNotice", "NuclearOption.Networking.Player.RpcShowSortieBonus",
                "NuclearOption.Networking.Player.RpcUnitDonationMessage", "Spawner.RpcOnGivenAircraftControl",
            }) rules[name] = new JumpRpcRule(JumpRpcClass.Skip, 0, 0);
            return rules;
        }

        public static JumpRpcRule Rule(string name)
            => name != null && Rules.TryGetValue(name, out var rule) ? rule : default;
    }

    // Reads Mirage's bit stream: bits fill each byte from the lowest bit up.
    internal struct JumpBitReader
    {
        private readonly byte[] data;
        private readonly int end;
        public int Position;

        public JumpBitReader(byte[] data, int bitPosition, int bitEnd) { this.data = data; Position = bitPosition; end = bitEnd; }

        public int BitsLeft => end - Position;

        public ulong Read(int bits)
        {
            if (Position + bits > end) throw new EndOfStreamException("message ended early");
            ulong value = 0;
            int got = 0, position = Position;
            while (got < bits)
            {
                int offset = position & 7, take = Math.Min(8 - offset, bits - got);
                value |= (ulong)(data[position >> 3] >> offset & (1 << take) - 1) << got;
                got += take;
                position += take;
            }
            Position = position;
            return value;
        }

        public bool ReadBool() => Read(1) == 1;

        // Mirage's packed integers.
        public ulong ReadPacked()
        {
            ulong first = Read(8);
            if (first < 241) return first;
            if (first <= 248) return 240 + (first - 241 << 8) + Read(8);
            if (first == 249) { ulong high = Read(8); return 2288 + (high << 8) + Read(8); }
            return Read(8 * ((int)first - 247));
        }

        public void PadToByte() => Position = Position + 7 & ~7;
    }

    internal sealed class JumpBitWriter
    {
        private byte[] data = new byte[256];
        private int position;

        public int ByteLength => position + 7 >> 3;

        public void Reset() { Array.Clear(data, 0, ByteLength); position = 0; }

        public void Write(ulong value, int bits)
        {
            if (position + bits + 8 >> 3 > data.Length) Array.Resize(ref data, data.Length * 2);
            int done = 0;
            while (done < bits)
            {
                int offset = position & 7, take = Math.Min(8 - offset, bits - done);
                data[position >> 3] |= (byte)((value >> done & (1UL << take) - 1) << offset);
                done += take;
                position += take;
            }
        }

        public void WriteBytes(byte[] bytes, int count)
        {
            for (int i = 0; i < count; i++) Write(bytes[i], 8);
        }

        public void WritePacked(ulong value)
        {
            if (value <= 240) Write(value, 8);
            else if (value <= 2287) { Write((value - 240 >> 8) + 241, 8); Write(value - 240 & 0xFF, 8); }
            else if (value <= 67823) { Write(249, 8); Write(value - 2288 >> 8, 8); Write(value - 2288 & 0xFF, 8); }
            else
            {
                int bytes = 3;
                while (bytes < 8 && value >> 8 * bytes != 0) bytes++;
                Write((ulong)(247 + bytes), 8);
                Write(value, 8 * bytes);
            }
        }

        public void PadToByte() => position = position + 7 & ~7;

        public byte[] Data => data;

        public byte[] ToArray()
        {
            var bytes = new byte[ByteLength];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }

    // The messages a jump point must read, without Mirage: its pooled readers do not work off the main thread.
    internal static class JumpWire
    {
        public const string TransformType = "NuclearOption.NetworkTransforms.SendTransformBatcher+TransformMessage";
        public static readonly ushort LoadMapId = Id("NuclearOption.SceneLoading.LoadMapMessage");
        public static readonly ushort SpawnId = Id("Mirage.SpawnMessage");
        public static readonly ushort DestroyId = Id("Mirage.ObjectDestroyMessage");
        public static readonly ushort HideId = Id("Mirage.ObjectHideMessage");
        public static readonly ushort VarsId = Id("Mirage.UpdateVarsMessage");
        public static readonly ushort RpcId = Id("Mirage.RemoteCalls.RpcMessage");
        public static readonly ushort TransformId = Id(TransformType);
        public static readonly ushort StringLengthsId = Id("Mirage.Serialization.BrotliCompression.StringStoreLengthsMessage");
        public static readonly ushort StringsId = Id("Mirage.Serialization.BrotliCompression.StringStoreStringsMessage");

        // Mirage's MessagePacker.GetId: the low 16 bits of the type name's stable hash.
        public static ushort Id(string typeName)
        {
            int hash = 23;
            unchecked { foreach (char c in typeName) hash = hash * 31 + c; }
            return (ushort)(hash & 0xFFFF);
        }

        public static uint ReadNetId(byte[] payload) => (uint)new JumpBitReader(payload, 16, payload.Length * 8).ReadPacked();

        public static void ReadSpawn(byte[] payload, out uint netId, out ulong? sceneId, out int? prefabHash)
        {
            var reader = new JumpBitReader(payload, 16, payload.Length * 8);
            netId = (uint)reader.ReadPacked();
            reader.Read(2);   // IsLocalPlayer, IsOwner
            sceneId = reader.ReadBool() ? reader.ReadPacked() : (ulong?)null;
            prefabHash = reader.ReadBool() ? ZigZag(reader.ReadPacked()) : (int?)null;
        }

        public static int ZigZag(ulong value) => (int)((uint)value >> 1) ^ -(int)((uint)value & 1);

        public static ulong ZigZag(int value) => (uint)(value << 1 ^ value >> 31);

        public static void ReadRpc(byte[] payload, out uint netId, out int function, out int bodyOffset, out int bodyCount)
        {
            var reader = new JumpBitReader(payload, 16, payload.Length * 8);
            netId = (uint)reader.ReadPacked();
            function = ZigZag(reader.ReadPacked());
            ReadBytesAndSize(ref reader, out int bitStart, out bodyCount);
            bodyOffset = bitStart >> 3;
        }

        // Sized bytes: a packed count plus one (0 means null), then byte aligned data.
        private static void ReadBytesAndSize(ref JumpBitReader reader, out int bitStart, out int count)
        {
            ulong packed = reader.ReadPacked();
            count = packed == 0 ? 0 : checked((int)(packed - 1));
            reader.PadToByte();
            bitStart = reader.Position;
            if (count * 8 > reader.BitsLeft) throw new EndOfStreamException("message ended early");
        }

        // A transform batch: id, double timestamp, then sized bytes of entries. Each entry is a 16-bit bit length, then
        // that many bits, which start with the object's packed net id.
        public static void ReadTransformBatch(byte[] payload, out double stamp, out int bitStart, out int bitEnd)
        {
            var reader = new JumpBitReader(payload, 16, payload.Length * 8);
            stamp = BitConverter.Int64BitsToDouble((long)reader.Read(64));
            ReadBytesAndSize(ref reader, out bitStart, out int count);
            bitEnd = bitStart + count * 8;
        }
    }

    // What the viewer needs to know about one object for a jump point.
    internal sealed class JumpObject
    {
        public uint NetId;
        public ulong? SceneId;
        public int? PrefabHash;
        public int SpawnRecord, EndRecord = int.MaxValue, Segment, LastSeenSegment;
        // The table that names this object's RPCs. Map objects have their own hash, so this is not a type.
        public long TableKey => SceneId.HasValue && SceneId.Value != 0 ? (long)SceneId.Value : PrefabHash ?? 0;
    }

    internal readonly struct JumpTransformEntry
    {
        public readonly int Record, BitPosition, Bits;
        public JumpTransformEntry(int record, int bitPosition, int bits) { Record = record; BitPosition = bitPosition; Bits = bits; }
    }

    // Where a jump resumes normal playback.
    internal sealed class JumpCut
    {
        public int Segment, ResumeIndex;
        public double Time, NetTime;
    }

    // One pass over a recording, off the main thread when a replay opens: mission segments, object lifetimes and the
    // messages that can matter to a jump. RPCs stay unclassified here, because naming them needs the loaded scene.
    internal sealed class JumpIndex
    {
        public const double CheckpointSeconds = 60;

        internal enum Kind : byte { LoadMap, Spawn, Vars, Rpc, Mission, Connection }

        internal struct Message
        {
            public int Record, Object, Function;
            public Kind Kind;
        }

        private sealed class Checkpoint
        {
            public int Transform;
            public double Stamp;
            public Dictionary<uint, JumpTransformEntry> Latest;
        }

        private readonly IList<DemoRecord> records;
        private readonly List<int> loads = new List<int>();
        private readonly List<Message> messages = new List<Message>();
        private readonly List<JumpObject> objects = new List<JumpObject>();
        private readonly List<int> transforms = new List<int>();
        private readonly List<Checkpoint> checkpoints = new List<Checkpoint>();
        public int BadRecords { get; private set; }
        public int SegmentCount => loads.Count;
        public int ObjectCount => objects.Count;
        public int MessageCount => messages.Count;
        public int TransformBatches => transforms.Count;
        public int CheckpointCount => checkpoints.Count;

        private JumpIndex(IList<DemoRecord> records) { this.records = records; }

        // Returns null when cancelled.
        public static JumpIndex Build(IList<DemoRecord> records, Func<bool> cancelled = null)
        {
            var index = new JumpIndex(records);
            return index.Scan(cancelled) ? index : null;
        }

        private bool Scan(Func<bool> cancelled)
        {
            var live = new Dictionary<uint, int>();
            var latest = new Dictionary<uint, JumpTransformEntry>();
            int segment = -1;
            double nextCheckpoint = 0, stamp = 0;
            for (int i = 0; i < records.Count; i++)
            {
                if ((i & 0xFFFF) == 0 && cancelled != null && cancelled()) return false;
                var record = records[i];
                if (record.Kind != DemoFormat.KindMessage || record.Payload.Length < 2) continue;
                ushort id = record.MessageId;
                if (id == JumpWire.LoadMapId)
                {
                    loads.Add(i);
                    segment++;
                    latest.Clear();
                    nextCheckpoint = record.Time + CheckpointSeconds;
                    messages.Add(new Message { Record = i, Kind = Kind.LoadMap });
                    continue;
                }
                if (segment < 0) continue;   // The connection's start: every jump sends all of it.
                try
                {
                    if (id == JumpWire.SpawnId)
                    {
                        JumpWire.ReadSpawn(record.Payload, out uint netId, out var sceneId, out var prefabHash);
                        if (live.TryGetValue(netId, out int old)) objects[old].EndRecord = i;
                        live[netId] = objects.Count;
                        messages.Add(new Message { Record = i, Object = objects.Count, Kind = Kind.Spawn });
                        objects.Add(new JumpObject { NetId = netId, SceneId = sceneId, PrefabHash = prefabHash, SpawnRecord = i, Segment = segment, LastSeenSegment = segment });
                    }
                    else if (id == JumpWire.DestroyId || id == JumpWire.HideId)
                    {
                        uint netId = JumpWire.ReadNetId(record.Payload);
                        if (live.TryGetValue(netId, out int o)) { objects[o].EndRecord = i; live.Remove(netId); }
                    }
                    else if (id == JumpWire.VarsId)
                    {
                        if (live.TryGetValue(JumpWire.ReadNetId(record.Payload), out int o))
                        {
                            objects[o].LastSeenSegment = segment;
                            messages.Add(new Message { Record = i, Object = o, Kind = Kind.Vars });
                        }
                    }
                    else if (id == JumpWire.RpcId)
                    {
                        JumpWire.ReadRpc(record.Payload, out uint netId, out int function, out _, out _);
                        if (live.TryGetValue(netId, out int o))
                        {
                            objects[o].LastSeenSegment = segment;
                            messages.Add(new Message { Record = i, Object = o, Function = function, Kind = Kind.Rpc });
                        }
                    }
                    else if (id == JumpWire.TransformId)
                    {
                        JumpWire.ReadTransformBatch(record.Payload, out stamp, out int bit, out int end);
                        ReadEntries(record.Payload, i, bit, end, latest, live);
                        transforms.Add(i);
                        if (record.Time >= nextCheckpoint)
                        {
                            nextCheckpoint = record.Time + CheckpointSeconds;
                            checkpoints.Add(new Checkpoint { Transform = transforms.Count, Stamp = stamp, Latest = new Dictionary<uint, JumpTransformEntry>(latest) });
                        }
                    }
                    else messages.Add(new Message { Record = i, Kind = id == JumpWire.StringLengthsId || id == JumpWire.StringsId ? Kind.Connection : Kind.Mission });
                }
                catch (Exception e) when (e is EndOfStreamException || e is OverflowException) { BadRecords++; }
            }
            return true;
        }

        private void ReadEntries(byte[] payload, int record, int bit, int end, Dictionary<uint, JumpTransformEntry> latest, Dictionary<uint, int> live)
        {
            var reader = new JumpBitReader(payload, bit, end);
            while (reader.BitsLeft >= 16)
            {
                int bits = (int)reader.Read(16), start = reader.Position;
                uint netId = (uint)reader.ReadPacked();
                latest[netId] = new JumpTransformEntry(record, start, bits);
                if (live != null && live.TryGetValue(netId, out int o)) objects[o].LastSeenSegment = loads.Count - 1;
                reader.Position = start + bits;
            }
        }

        // Null when the recording has no map load before the cut, or nothing after it.
        public JumpCut Cut(double cutTime)
        {
            int resume = -1;
            for (int i = 0; i < records.Count; i++)
                if (records[i].Time > cutTime) { resume = i; break; }
            if (resume < 0) return null;
            int segment = -1;
            while (segment + 1 < loads.Count && loads[segment + 1] < resume) segment++;
            if (segment < 0) return null;
            // The next message can be minutes away during a quiet gap. Start at the requested cut,
            // using the same last-message clock mapping as straight playback, without skipping annotations.
            int previous = resume - 1;
            while (records[previous].Kind != DemoFormat.KindMessage) previous--;
            var last = records[previous];
            return new JumpCut { Segment = segment, ResumeIndex = resume, Time = cutTime,
                NetTime = last.NetTime + (cutTime - last.Time) };
        }

        // What the client gets before it loads: the connection's start and this mission's map load.
        public List<DemoRecord> Prelude(JumpCut cut)
        {
            var prelude = new List<DemoRecord>();
            for (int i = 0; i < loads[0]; i++) if (records[i].Kind == DemoFormat.KindMessage) prelude.Add(records[i]);
            prelude.Add(records[loads[cut.Segment]]);
            return prelude;
        }

        // Objects from an earlier mission stay only if this mission or a later one still talks about them (players).
        private bool Alive(JumpObject o, JumpCut cut)
            => o.SpawnRecord < cut.ResumeIndex && o.EndRecord >= cut.ResumeIndex && (o.Segment == cut.Segment || o.LastSeenSegment >= cut.Segment);

        public IEnumerable<JumpObject> LiveObjects(JumpCut cut) => objects.Where(o => Alive(o, cut));

        // Pure and safe off the main thread. tables holds the RPC rules of each TableKey; a missing key leaves that
        // object's RPCs out and counts them in JumpBurst.Unnamed.
        public JumpBurst BuildBurst(JumpCut cut, IReadOnlyDictionary<long, JumpRpcRule[]> tables)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var burst = new JumpBurst();
            var keep = new bool[objects.Count];
            var rules = new JumpRpcRule[objects.Count][];
            var liveIds = new Dictionary<uint, JumpObject>();
            for (int o = 0; o < objects.Count; o++)
            {
                var item = objects[o];
                if (!Alive(item, cut)) continue;
                keep[o] = true;
                liveIds[item.NetId] = item;
                tables.TryGetValue(item.TableKey, out rules[o]);
            }
            var kept = new List<int>();
            var latest = new Dictionary<(int Object, int Group, ulong Key), int>();
            int segment = 0, seen = 0;
            for (int m = 0; m < messages.Count; m++)
            {
                var message = messages[m];
                if (message.Record >= cut.ResumeIndex) break;
                if (message.Kind == Kind.LoadMap) { if (message.Record != loads[0]) segment++; continue; }
                if (message.Kind == Kind.Connection) { kept.Add(message.Record); continue; }
                if (message.Kind == Kind.Mission) { if (segment == cut.Segment) kept.Add(message.Record); continue; }
                seen++;
                if (!keep[message.Object]) continue;
                if (message.Kind != Kind.Rpc) { kept.Add(message.Record); continue; }
                var table = rules[message.Object];
                if (table == null)
                {
                    long key = objects[message.Object].TableKey;
                    burst.Unnamed.TryGetValue(key, out int count);
                    burst.Unnamed[key] = count + 1;
                    continue;
                }
                var rule = message.Function >= 0 && message.Function < table.Length ? table[message.Function] : default;
                if (rule.Class == JumpRpcClass.Unknown)
                    throw new InvalidDataException($"Unclassified RPC {message.Function} on prefab {objects[message.Object].TableKey}; use fast-forward");
                if (rule.Class == JumpRpcClass.All) kept.Add(message.Record);
                else if (rule.Class == JumpRpcClass.Latest) latest[(message.Object, rule.Group, KeyHash(records[message.Record].Payload, rule.KeyBytes))] = message.Record;
            }
            kept.AddRange(latest.Values);
            kept.Sort();
            burst.Records = new List<DemoRecord>(kept.Count + 64);
            foreach (int i in kept) { burst.Records.Add(records[i]); burst.Bytes += records[i].Payload.Length; }
            burst.LastTransforms = LastTransforms(cut, liveIds, out double stamp);
            AppendTransforms(burst, stamp == 0 ? cut.NetTime : stamp, cut);
            burst.Live = liveIds.Count;
            burst.Summary = $"segment {cut.Segment + 1}/{loads.Count}, {seen} messages read in {watch.ElapsedMilliseconds} ms, {liveIds.Count} live of {objects.Count} objects, "
                + $"burst {burst.Records.Count} records {burst.Bytes} bytes + {burst.TransformEntries} transforms in {burst.TransformBatches} batches";
            return burst;
        }

        private static ulong KeyHash(byte[] payload, int keyBytes)
        {
            if (keyBytes <= 0) return 0;
            JumpWire.ReadRpc(payload, out _, out _, out int offset, out int count);
            int n = Math.Min(keyBytes, count);
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < n; i++) hash = (hash ^ payload[offset + i]) * 1099511628211UL;
            return hash;
        }

        // The last transform of every live object in this mission. A stopped vehicle sends no more transforms, so the
        // last one can be far back; checkpoints keep this from reading the whole mission.
        private Dictionary<uint, JumpTransformEntry> LastTransforms(JumpCut cut, Dictionary<uint, JumpObject> live, out double stamp)
        {
            int start = LowerBound(transforms, loads[cut.Segment]);
            var latest = new Dictionary<uint, JumpTransformEntry>();
            stamp = 0;
            for (int c = checkpoints.Count - 1; c >= 0; c--)
            {
                var checkpoint = checkpoints[c];
                int last = transforms[checkpoint.Transform - 1];
                if (last >= cut.ResumeIndex) continue;
                if (last < loads[cut.Segment]) break;
                latest = new Dictionary<uint, JumpTransformEntry>(checkpoint.Latest);
                start = checkpoint.Transform;
                stamp = checkpoint.Stamp;
                break;
            }
            for (int t = start; t < transforms.Count && transforms[t] < cut.ResumeIndex; t++)
            {
                var payload = records[transforms[t]].Payload;
                JumpWire.ReadTransformBatch(payload, out stamp, out int bit, out int end);
                ReadEntries(payload, transforms[t], bit, end, latest, null);
            }
            var result = new Dictionary<uint, JumpTransformEntry>();
            foreach (var pair in latest)
                if (live.TryGetValue(pair.Key, out var o) && pair.Value.Record > o.SpawnRecord) result[pair.Key] = pair.Value;
            return result;
        }

        private static int LowerBound(List<int> sorted, int value)
        {
            int low = 0, high = sorted.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (sorted[middle] < value) low = middle + 1; else high = middle;
            }
            return low;
        }

        // Rebuilds one set of reliable batches holding the last entry of each object.
        private void AppendTransforms(JumpBurst burst, double stamp, JumpCut cut)
        {
            var body = new JumpBitWriter();
            var message = new JumpBitWriter();
            void Flush()
            {
                if (body.ByteLength == 0) return;
                message.Reset();
                message.Write(JumpWire.TransformId, 16);
                message.Write((ulong)BitConverter.DoubleToInt64Bits(stamp), 64);
                message.WritePacked((ulong)body.ByteLength + 1);
                message.PadToByte();
                message.WriteBytes(body.Data, body.ByteLength);
                burst.Records.Add(new DemoRecord { Kind = DemoFormat.KindMessage, Time = cut.Time, NetTime = cut.NetTime, Channel = DemoFormat.ChannelReliable, Payload = message.ToArray() });
                body.Reset();
                burst.TransformBatches++;
            }
            foreach (var pair in burst.LastTransforms.OrderBy(p => p.Key))
            {
                var entry = pair.Value;
                if (body.ByteLength + entry.Bits / 8 + 3 > JumpBurst.MaxTransformBytes) Flush();
                body.Write((ulong)entry.Bits, 16);
                var reader = new JumpBitReader(records[entry.Record].Payload, entry.BitPosition, entry.BitPosition + entry.Bits);
                for (int left = entry.Bits; left > 0;)
                {
                    int n = Math.Min(left, 64);
                    body.Write(reader.Read(n), n);
                    left -= n;
                }
                burst.TransformEntries++;
            }
            Flush();
        }
    }

    // The records that rebuild the world at a cut.
    internal sealed class JumpBurst
    {
        public const int MaxTransformBytes = 1000;
        public List<DemoRecord> Records;
        public Dictionary<uint, JumpTransformEntry> LastTransforms;
        public readonly Dictionary<long, int> Unnamed = new Dictionary<long, int>();
        public int Live, TransformEntries, TransformBatches;
        public long Bytes;
        public string Summary = "";
    }
}
