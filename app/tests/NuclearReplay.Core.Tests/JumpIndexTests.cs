using System.Diagnostics;
using NuclearReplay.Demo;
using NuclearReplay.Playback;
using Xunit.Abstractions;

namespace NuclearReplay.Core.Tests;

// replay/src/JumpIndex.cs: what a jump point sends. Messages are built bit by bit as Mirage writes them.
public class JumpIndexTests(ITestOutputHelper output)
{
    private const long Plane = 5, Base = 6;
    private const int Radar = 0, Damage = 1, Ammo = 2, Cosmetic = 3, Hangar = 4, HangarOther = 5;

    private static readonly Dictionary<long, JumpRpcRule[]> Tables = new()
    {
        [Plane] = new[] { "Aircraft.RpcToggleRadar", "Aircraft.RpcDamage", "Unit.RpcSyncAmmoCount", "Aircraft.RpcSomethingElse" }.Select(JumpRpcRules.Rule).ToArray(),
        [Base] = new[] { "x", "x", "x", "x", "Hangar.RpcOpenHangar", "Hangar.RpcCloseHangar" }.Select(JumpRpcRules.Rule).ToArray(),
    };

    private static DemoRecord Record(double time, JumpBitWriter w) => new()
    {
        Kind = DemoFormat.KindMessage, Time = time, NetTime = time + 100, Channel = DemoFormat.ChannelReliable, Payload = w.ToArray(),
    };

    private static JumpBitWriter Header(ushort id)
    {
        var w = new JumpBitWriter();
        w.Write(id, 16);
        return w;
    }

    private static DemoRecord Load(double time) => Record(time, Header(JumpWire.LoadMapId));
    private static DemoRecord Other(double time) => Record(time, Header(1234));
    private static DemoRecord Strings(double time) => Record(time, Header(JumpWire.StringsId));

    private static DemoRecord Spawn(double time, uint netId, int? hash, ulong? scene = null)
    {
        var w = Header(JumpWire.SpawnId);
        w.WritePacked(netId);
        w.Write(0, 2);
        w.Write(scene.HasValue ? 1UL : 0, 1);
        if (scene.HasValue) w.WritePacked(scene.Value);
        w.Write(hash.HasValue ? 1UL : 0, 1);
        if (hash.HasValue) w.WritePacked(JumpWire.ZigZag(hash.Value));
        return Record(time, w);
    }

    private static DemoRecord Net(double time, ushort id, uint netId)
    {
        var w = Header(id);
        w.WritePacked(netId);
        return Record(time, w);
    }

    private static DemoRecord Rpc(double time, uint netId, int function, params byte[] body)
    {
        var w = Header(JumpWire.RpcId);
        w.WritePacked(netId);
        w.WritePacked(JumpWire.ZigZag(function));
        w.WritePacked((ulong)body.Length + 1);
        w.PadToByte();
        w.WriteBytes(body, body.Length);
        return Record(time, w);
    }

    // One entry per object, odd bit lengths so that entries do not fall on byte boundaries.
    private static DemoRecord Transforms(double time, params (uint NetId, ulong Value, int Bits)[] entries)
    {
        var body = new JumpBitWriter();
        foreach (var (netId, value, bits) in entries)
        {
            var entry = new JumpBitWriter();
            entry.WritePacked(netId);
            int idBits = entry.ByteLength * 8;
            body.Write((ulong)(idBits + bits), 16);
            body.WritePacked(netId);
            body.Write(value, bits);
        }
        var w = Header(JumpWire.TransformId);
        w.Write((ulong)BitConverter.DoubleToInt64Bits(time + 100), 64);
        w.WritePacked((ulong)body.ByteLength + 1);
        w.PadToByte();
        w.WriteBytes(body.Data, body.ByteLength);
        return Record(time, w);
    }

    private static List<(uint NetId, ulong Value)> ReadEntries(DemoRecord record, int valueBits)
    {
        var entries = new List<(uint, ulong)>();
        JumpWire.ReadTransformBatch(record.Payload, out _, out int bit, out int end);
        var reader = new JumpBitReader(record.Payload, bit, end);
        while (reader.BitsLeft >= 16)
        {
            int bits = (int)reader.Read(16), start = reader.Position;
            uint netId = (uint)reader.ReadPacked();
            entries.Add((netId, reader.Read(valueBits)));
            reader.Position = start + bits;
        }
        return entries;
    }

    private static List<DemoRecord> Recording(params DemoRecord[] records) => [.. records];

    private static JumpBurst Burst(List<DemoRecord> records, double cutTime)
    {
        var index = JumpIndex.Build(records);
        return index.BuildBurst(index.Cut(cutTime), Tables);
    }

    private static List<uint> Spawned(JumpBurst burst) => burst.Records.Where(r => r.MessageId == JumpWire.SpawnId).Select(r => JumpWire.ReadNetId(r.Payload)).ToList();

    private static List<int> Calls(JumpBurst burst, uint netId) => burst.Records.Where(r => r.MessageId == JumpWire.RpcId && JumpWire.ReadNetId(r.Payload) == netId)
        .Select(r => { JumpWire.ReadRpc(r.Payload, out _, out int function, out _, out _); return function; }).ToList();

    [Fact]
    public void MessageIdsAreMirageStableHashes()
    {
        Assert.Equal(62842, JumpWire.SpawnId);
        Assert.Equal(4922, JumpWire.DestroyId);
        Assert.Equal(5880, JumpWire.HideId);
        Assert.Equal(19204, JumpWire.VarsId);
        Assert.Equal(42127, JumpWire.RpcId);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(240UL)]
    [InlineData(241UL)]
    [InlineData(2287UL)]
    [InlineData(2288UL)]
    [InlineData(67823UL)]
    [InlineData(67824UL)]
    [InlineData(1UL << 24)]
    [InlineData(uint.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void PackedIntegersRoundTripAtAnyBitOffset(ulong value)
    {
        for (int offset = 0; offset < 8; offset++)
        {
            var w = new JumpBitWriter();
            w.Write(1, offset);
            w.WritePacked(value);
            var reader = new JumpBitReader(w.Data, offset, w.ByteLength * 8);
            Assert.Equal(value, reader.ReadPacked());
        }
    }

    [Fact]
    public void ReadsARecordedRpcHeader()
    {
        // From a real recording: net id 11, function 24, 2 byte body.
        var payload = new byte[] { 0x8f, 0xa4, 0x0b, 0x30, 0x03, 0x09, 0x00 };
        JumpWire.ReadRpc(payload, out uint netId, out int function, out int offset, out int count);
        Assert.Equal((11u, 24, 5, 2), (netId, function, offset, count));
    }

    [Fact]
    public void ADestroyedOrHiddenObjectIsNotInTheBurst()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 10, 5), Spawn(1, 11, 5), Spawn(1, 12, 5),
            Rpc(2, 10, Damage), Net(3, JumpWire.DestroyId, 10), Net(4, JumpWire.HideId, 11), Other(5), Other(6)), 5.5);
        Assert.Equal([12u], Spawned(burst));
        Assert.Equal(1, burst.Live);
        Assert.Empty(Calls(burst, 10));
    }

    [Fact]
    public void AnObjectDestroyedAfterTheCutIsInTheBurst()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 10, 5), Other(2), Other(5), Net(6, JumpWire.DestroyId, 10)), 3);
        Assert.Equal([10u], Spawned(burst));
    }

    [Fact]
    public void ASpawnOverALiveNetIdEndsTheOldObject()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 10, 5), Rpc(2, 10, Damage), Spawn(3, 10, 5), Rpc(4, 10, Radar), Other(5), Other(6)), 5.5);
        Assert.Single(Spawned(burst));
        Assert.Equal([Radar], Calls(burst, 10));
    }

    [Fact]
    public void RpcRulesKeepAllLatestOrNone()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 10, 5),
            Rpc(2, 10, Damage, 1), Rpc(3, 10, Damage, 2),
            Rpc(4, 10, Radar, 1), Rpc(5, 10, Radar, 0),
            Rpc(6, 10, Cosmetic), Other(7), Other(8)), 7.5);
        Assert.Equal([Damage, Damage, Radar], Calls(burst, 10));
        Assert.Equal(new byte[] { 0 }, burst.Records.Last(r => r.MessageId == JumpWire.RpcId).Payload[^1..]);
    }

    [Fact]
    public void LatestKeepsOneCallPerKey()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 10, 5),
            Rpc(2, 10, Ammo, 0, 8), Rpc(3, 10, Ammo, 1, 4), Rpc(4, 10, Ammo, 0, 6), Other(5), Other(6)), 5.5);
        Assert.Equal(2, Calls(burst, 10).Count);
    }

    [Fact]
    public void CallsOfOneGroupReplaceEachOther()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 20, 6), Rpc(2, 20, Hangar), Rpc(3, 20, HangarOther), Other(4), Other(5)), 4.5);
        Assert.Equal([HangarOther], Calls(burst, 20));
    }

    [Fact]
    public void AnUnknownPrefabIsCountedAndItsRpcsLeftOut()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 30, 999), Rpc(2, 30, Damage), Rpc(3, 30, Damage), Other(4), Other(5)), 4.5);
        Assert.Empty(Calls(burst, 30));
        Assert.Equal(2, burst.Unnamed[999]);
    }

    [Fact]
    public void AnRpcOutsideItsTableIsSkipped()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 10, 5), Rpc(2, 10, 200), Rpc(2, 10, -1), Other(3), Other(4)), 3.5);
        Assert.Empty(Calls(burst, 10));
    }

    [Fact]
    public void OnlyTheTargetMissionIsInTheBurst()
    {
        var records = Recording(Other(0), Load(1), Spawn(2, 10, 5), Other(3), Strings(3),
            Load(10), Spawn(11, 20, 5), Other(12), Other(13), Other(14),
            Load(20), Spawn(21, 30, 5), Other(22));
        var burst = Burst(records, 13.5);
        Assert.Equal([20u], Spawned(burst));
        Assert.Equal(1, burst.Records.Count(r => r.MessageId == JumpWire.StringsId));
        Assert.Equal(2, burst.Records.Count(r => r.MessageId == 1234));
        Assert.DoesNotContain(burst.Records, r => r.MessageId == JumpWire.LoadMapId);
        var index = JumpIndex.Build(records);
        var prelude = index.Prelude(index.Cut(13.5));
        Assert.Equal(new double[] { 0, 10 }, prelude.Select(r => r.Time));
    }

    [Fact]
    public void AnObjectFromAnEarlierMissionStaysOnlyIfStillInUse()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 10, 5), Spawn(1, 11, 5), Load(10), Rpc(11, 11, Radar), Other(12), Other(13)), 12.5);
        Assert.Equal([11u], Spawned(burst));
    }

    [Fact]
    public void ACutJustAfterAMapLoadHasAnEmptyBurst()
    {
        var records = Recording(Load(0), Spawn(1, 10, 5), Other(2), Load(10), Other(10.1), Other(10.2));
        var index = JumpIndex.Build(records);
        var cut = index.Cut(10.05);
        Assert.Equal(1, cut.Segment);
        var burst = index.BuildBurst(cut, Tables);
        Assert.Empty(Spawned(burst));
        Assert.Equal(10, index.Prelude(cut)[^1].Time);
    }

    [Fact]
    public void ACutInsideAJoinBurstKeepsWhatCameBeforeIt()
    {
        var records = Recording(Load(0), Spawn(0.5, 10, 5), Spawn(1, 11, 5), Spawn(1, 12, 5), Spawn(2, 13, 5), Other(3));
        var burst = Burst(records, 1);
        Assert.Equal([10u, 11u, 12u], Spawned(burst));
    }

    [Fact]
    public void NoCutBeforeTheFirstMapLoadOrAfterTheLastRecord()
    {
        var index = JumpIndex.Build(Recording(Other(1), Other(2), Load(5), Other(6)));
        Assert.Null(index.Cut(1.5));
        Assert.Null(index.Cut(100));
        Assert.Null(JumpIndex.Build(Recording(Other(1), Other(2))).Cut(1.5));
    }

    [Fact]
    public void RebuiltTransformsDecodeToTheSameEntries()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 10, 5), Spawn(1, 300, 5), Spawn(1, 70000, 5),
            Transforms(2, (10, 1, 13), (300, 2, 13), (70000, 3, 13)),
            Transforms(3, (10, 4, 13)), Other(4), Other(5)), 4.5);
        var entries = burst.Records.Where(r => r.MessageId == JumpWire.TransformId).SelectMany(r => ReadEntries(r, 13)).OrderBy(e => e.NetId);
        Assert.Equal([(10u, 4UL), (300u, 2UL), (70000u, 3UL)], entries);
        Assert.Equal(3, burst.TransformEntries);
    }

    [Fact]
    public void ManyTransformsSplitIntoSmallReliableBatches()
    {
        var records = Recording(Load(0));
        for (uint id = 1; id <= 300; id++) records.Add(Spawn(1, id, 5));
        var entries = Enumerable.Range(1, 300).Select(i => ((uint)i, (ulong)i, 90)).ToArray();
        records.Add(Transforms(2, entries));
        records.Add(Other(3));
        records.Add(Other(4));
        var batches = Burst(records, 3.5).Records.Where(r => r.MessageId == JumpWire.TransformId).ToList();
        Assert.True(batches.Count > 1);
        Assert.All(batches, r => Assert.True(r.Payload.Length < JumpBurst.MaxTransformBytes + 16));
        Assert.All(batches, r => Assert.Equal(DemoFormat.ChannelReliable, r.Channel));
        Assert.Equal(Enumerable.Range(1, 300).Select(i => (uint)i), batches.SelectMany(r => ReadEntries(r, 90)).Select(e => e.NetId));
    }

    [Fact]
    public void CheckpointsGiveTheSameTransformsAsReadingTheWholeMission()
    {
        var records = Recording(Load(0), Spawn(1, 10, 5), Spawn(1, 11, 5), Transforms(2, (11, 7, 13)));
        for (int t = 3; t < 400; t++) records.Add(Transforms(t, (10, (ulong)t, 13)));
        records.Add(Other(401));
        var index = JumpIndex.Build(records);
        Assert.True(index.CheckpointCount > 3);
        foreach (double cut in new[] { 2.5, 59.5, 60.5, 61.5, 200.5, 399.5 })
        {
            var burst = index.BuildBurst(index.Cut(cut), Tables);
            var entries = burst.Records.Where(r => r.MessageId == JumpWire.TransformId).SelectMany(r => ReadEntries(r, 13)).OrderBy(e => e.NetId).ToList();
            Assert.Equal((11u, 7UL), entries[^1]);
            Assert.Equal(cut < 3 ? 1 : 2, entries.Count);
            if (cut > 3) Assert.Equal((10u, (ulong)Math.Floor(cut)), entries[0]);
        }
    }

    [Fact]
    public void ATransformFromBeforeARespawnIsNotKept()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 10, 5), Transforms(2, (10, 1, 13)), Net(3, JumpWire.DestroyId, 10), Spawn(4, 10, 5), Other(5), Other(6)), 5.5);
        Assert.Equal(0, burst.TransformEntries);
    }

    [Fact]
    public void AMissionTransformDoesNotLeakIntoTheNext()
    {
        var burst = Burst(Recording(Load(0), Spawn(1, 10, 5), Transforms(2, (10, 1, 13)), Load(10), Rpc(11, 10, Radar), Other(12), Other(13)), 12.5);
        Assert.Equal(0, burst.TransformEntries);
    }

    [Fact]
    public void ATruncatedMessageIsCountedAndSkipped()
    {
        var bad = Spawn(1, 70000, 5);
        var records = Recording(Load(0), new DemoRecord { Kind = DemoFormat.KindMessage, Time = 1, Payload = bad.Payload[..3] }, Spawn(2, 10, 5), Other(3), Other(4));
        var index = JumpIndex.Build(records);
        Assert.Equal(1, index.BadRecords);
        Assert.Equal([10u], Spawned(index.BuildBurst(index.Cut(3.5), Tables)));
    }

    [Fact]
    public void BuildingStopsWhenCancelled() => Assert.Null(JumpIndex.Build(Recording(Load(0)), () => true));

    // Set NR_JUMP_DEMO to a list of .ncdemo files separated by ':' to read real recordings: checks the wire format
    // holds for every recording that was ever made, and prints the time and memory an index takes.
    [Fact]
    public void RealRecordings()
    {
        string paths = Environment.GetEnvironmentVariable("NR_JUMP_DEMO");
        if (string.IsNullOrEmpty(paths)) return;
        foreach (string path in paths.Split(':'))
        {
            var demo = DemoReader.Load(path);
            long before = GC.GetTotalMemory(true);
            var watch = Stopwatch.StartNew();
            var index = JumpIndex.Build(demo.Records);
            long built = watch.ElapsedMilliseconds;
            long memory = GC.GetTotalMemory(true) - before;
            Assert.Equal(0, index.BadRecords);
            double start = demo.Records[0].Time, end = demo.Records[^1].Time;
            var tables = new Dictionary<long, JumpRpcRule[]>();
            watch.Restart();
            var burst = index.BuildBurst(index.Cut(start + (end - start) * 0.95), tables);
            output.WriteLine($"{Path.GetFileName(path)}: {demo.Records.Count} records, {end - start:F0} s, truncated {demo.Truncated}: index {built} ms {memory >> 20} MB, "
                + $"{index.SegmentCount} missions, {index.ObjectCount} objects, {index.MessageCount} messages, {index.CheckpointCount} checkpoints; burst {watch.ElapsedMilliseconds} ms, {burst.Summary}");
            Assert.True(index.ObjectCount > 0);
            GC.KeepAlive(index);
        }
    }
}
