using System.Diagnostics;
using System.Text.Json;
using NuclearReplay.Core.Demos;

namespace NuclearReplay.Core.Tests;

public class DemoReadingTests
{
    private const string Header = """{"format":"nuclear-context-network-demo","format_version":1,"mod_version":"0.5.0","game_version":"0.34.1","created_utc":"2026-10-04T02:19:32.9952580Z","mission_name":"Escalation","map_key_type":"None","map_key_path":"","game_state":"Multiplayer"}""";

    private static byte[] Payload(int n, ushort id = 0xaa65) => new byte[] { (byte)id, (byte)(id >> 8) }.Concat(Enumerable.Repeat((byte)7, n)).ToArray();

    /// <summary>A match: start annotations, messages, contract events, end. Recorder clock starts at 100 s.</summary>
    private static DemoFile.Builder Match() => new DemoFile.Builder(Header)
        .Annotation(100.0, """{"event":"ghost_authenticated"}""")
        .Message(100.1, Payload(10))
        .Annotation(100.4, """{"event":"ghost_scene_ready","load_map_count":1}""")
        .Annotation(101.0, """{"event":"mission","name":"Escalation","map":"Heartland"}""")
        .Annotation(102.0, """{"event":"player","name":"Talamander","faction":"BDF","unit":{"id":169,"name":"Revoker","type":"aircraft","faction":"BDF","player":"Talamander"}}""")
        .Message(110.0, Payload(300), net: 12.5, channel: 1)
        .Annotation(129.5, """{"event":"launch","shooter":{"id":169,"name":"Revoker","player":"Talamander"},"target":{"id":201,"name":"Ifrit"},"weapon":"AAM-29","missile":4021,"extra":"ignored"}""")
        .Annotation(135.25, """{"event":"kill","victim":{"id":201,"name":"Ifrit","faction":"PALA"},"killer":{"id":169,"name":"Revoker","player":"Talamander"},"weapon":"AAM-29"}""")
        .Annotation(136.0, """{"event":"kill","victim":{"id":305},"killer":null,"weapon":null}""")
        .Annotation(137.0, """{"event":"future_event","whatever":[1,2,3]}""")
        .Message(150.0, Payload(50))
        .Annotation(150.5, """{"event":"end","reason":"shutdown","messages":4,"bytes":360}""");

    [Fact]
    public void ScanReadsHeaderDurationAndCounts()
    {
        var scan = DemoFile.Scan(new MemoryStream(Match().ToArray()));
        Assert.Equal("Escalation", scan.Header["mission_name"]!.GetValue<string>());
        Assert.Equal(1, scan.FormatVersion);
        Assert.Equal(3, scan.MessageCount);
        Assert.Equal(9, scan.AnnotationCount);
        Assert.False(scan.Truncated);
        Assert.Equal(50.5, scan.Duration, 6);
    }

    [Fact]
    public void KillWithHitTimeIsPlacedAtTheHit()
    {
        var kill = DemoEvent.Parse(35.25, """{"event":"kill","victim":{"id":201},"killer":{"id":169},"weapon":"AAM-29","hit_ago":2.4}""")!;
        Assert.Equal(32.85, kill.T, 6);
        Assert.Equal(2.4, kill.HitAgo!.Value, 6);
        Assert.Equal(35.25, kill.LoggedT, 6);

        // Recordings before 0.5.1 have no hit time: the kill stays at the kill feed.
        var old = DemoEvent.Parse(35.25, """{"event":"kill","victim":{"id":201},"killer":{"id":169},"weapon":"AAM-29"}""")!;
        Assert.Equal(35.25, old.T, 6);
        Assert.Null(old.HitAgo);
        Assert.Equal(35.25, old.LoggedT, 6);

        // Values a reader cannot trust are ignored; a hit before the recording started clamps to 0.
        foreach (var bad in new[] { "-1", "99", "\"2\"", "null" })
        {
            var e = DemoEvent.Parse(35.25, $$"""{"event":"kill","victim":{"id":201},"hit_ago":{{bad}}}""")!;
            Assert.Equal(35.25, e.T, 6);
            Assert.Null(e.HitAgo);
        }
        var early = DemoEvent.Parse(1.0, """{"event":"kill","victim":{"id":201},"hit_ago":2.5}""")!;
        Assert.Equal(0, early.T, 6);
        Assert.Equal(1.0, early.LoggedT, 6);
        // Only kills carry a hit time.
        Assert.Equal(10, DemoEvent.Parse(10, """{"event":"launch","hit_ago":2}""")!.T, 6);
    }

    [Fact]
    public void KillPlacedAtItsHitSortsBeforeLaterEvents()
    {
        using var dir = new TempDir();
        var path = dir.Write("m.ncdemo", new DemoFile.Builder(Header)
            .Annotation(100.0, """{"event":"mission","name":"Escalation"}""")
            .Annotation(110.0, """{"event":"launch","shooter":{"id":169},"weapon":"AAM-29","missile":4021}""")
            .Annotation(112.0, """{"event":"kill","victim":{"id":201},"killer":{"id":169},"weapon":"AAM-29","hit_ago":3}""")
            .Annotation(120.0, """{"event":"end","reason":"shutdown"}""")
            .ToArray());
        var events = DemoAnalyzer.Read(path).Events;
        Assert.Equal(new[] { "mission", "kill", "launch", "end" }, events.Select(e => e.Event));
        Assert.Equal(9, events[1].T, 6);

        // The hit time survives the summary cache.
        var paths = new AppPaths(dir.Combine("cfg"), dir.Combine("data"));
        new DemoLibrary(paths).Get(path);
        var cached = new DemoLibrary(paths).Get(path).Events.Single(e => e.Event == "kill");
        Assert.Equal(9, cached.T, 6);
        Assert.Equal(3, cached.HitAgo!.Value, 6);
        Assert.Equal(12, cached.LoggedT, 6);
    }

    [Fact]
    public void EventsUseReplayTimeFromFirstRecord()
    {
        using var dir = new TempDir();
        var path = dir.Write("m.ncdemo", Match().ToArray());
        var details = DemoAnalyzer.Read(path);
        var launch = details.Events.Single(e => e.Event == "launch");
        Assert.Equal(29.5, launch.T, 6);
        Assert.Equal("Talamander (Revoker) fired AAM-29 at Ifrit", launch.Summary);
        Assert.Equal("unit:169", launch.Subject!.ShotTarget);
        Assert.Equal(4021, launch.Missile);
        Assert.Equal("ignored", launch.Data["extra"]!.GetValue<string>());

        var kills = details.Events.Where(e => e.Event == "kill").ToList();
        Assert.Equal(35.25, kills[0].T, 6);
        Assert.Equal("Talamander (Revoker) destroyed Ifrit with AAM-29", kills[0].Summary);
        Assert.Equal("unit 305 was destroyed", kills[1].Summary);
        Assert.Null(kills[1].Killer);
        Assert.Equal("unit:305", kills[1].Subject!.ShotTarget);

        // Unknown events are kept but not treated as moments.
        var unknown = details.Events.Single(e => e.Event == "future_event");
        Assert.False(unknown.IsMoment);

        var s = details.Summary;
        Assert.Equal("Escalation", s.Mission);
        Assert.Equal("Heartland", s.Map);
        Assert.Equal(new[] { "Talamander" }, s.Players);
        Assert.Equal(1, s.Launches);
        Assert.Equal(2, s.Kills);
        Assert.True(s.HasEvents);
        Assert.True(s.Complete);
        Assert.Equal("shutdown", s.EndReason);
        Assert.Equal("2026-10-04T02:19:32", s.CreatedUtc!.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void OldDemoWithoutEventsHasNoMoments()
    {
        var bytes = new DemoFile.Builder(Header)
            .Annotation(5, """{"event":"ghost_authenticated"}""")
            .Message(5.1, Payload(4))
            .Message(65.1, Payload(4))
            .ToArray();
        using var dir = new TempDir();
        var details = DemoAnalyzer.Read(dir.Write("old.ncdemo", bytes));
        Assert.False(details.Summary.HasEvents);
        Assert.False(details.Summary.Complete);
        Assert.Null(details.Summary.Map); // header map key is empty in recorder 0.4.x
        Assert.Equal(60.1, details.Summary.DurationS, 3);
    }

    public static IEnumerable<object[]> Truncations()
    {
        // Cut inside the next record header.
        yield return new object[] { new byte[] { 1, 0, 0, 0, 0, 0 } };
        // Complete record header announcing 100 bytes, but only 3 present.
        var head = new byte[22];
        head[0] = 1;
        BitConverter.GetBytes(999.0).CopyTo(head, 1);
        BitConverter.GetBytes(100).CopyTo(head, 18);
        yield return new object[] { head.Concat(new byte[] { 1, 2, 3 }).ToArray() };
        // Annotation header with negative length.
        var negative = new byte[22];
        negative[0] = 2;
        BitConverter.GetBytes(-5).CopyTo(negative, 18);
        yield return new object[] { negative };
    }

    [Theory]
    [MemberData(nameof(Truncations))]
    public void TruncatedTailIsIgnoredAndFlagged(byte[] tail)
    {
        var bytes = Match().Raw(tail).ToArray();
        var scan = DemoFile.Scan(new MemoryStream(bytes));
        Assert.True(scan.Truncated);
        Assert.Equal(3, scan.MessageCount);
        Assert.Equal(50.5, scan.Duration, 6); // the partial record does not extend the duration

        // Same answer through the non-seekable path.
        var unseekable = DemoFile.Scan(new NonSeekableStream(bytes));
        Assert.True(unseekable.Truncated);
        Assert.Equal(3, unseekable.MessageCount);
    }

    [Fact]
    public void RejectsNonDemoFiles()
    {
        Assert.Throws<InvalidDataException>(() => DemoFile.Scan(new MemoryStream("NOTADEMO-and-more-bytes"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => DemoFile.Scan(new MemoryStream(new byte[3])));
    }

    [Fact]
    public void LibraryCachesAndReportsUnreadableFiles()
    {
        using var dir = new TempDir();
        dir.Write("demos/20261001T010203Z-A.ncdemo", Match().ToArray());
        dir.Write("demos/20261002T010203Z-B.ncdemo", "garbage");
        var library = new DemoLibrary(new AppPaths(dir.Combine("cfg"), dir.Combine("data")));
        var list = library.List(dir.Combine("demos"));
        Assert.Equal(2, list.Count);
        Assert.Equal("20261002T010203Z-B.ncdemo", list[0].FileName); // newest first
        Assert.NotNull(list[0].Error);
        Assert.Null(list[1].Error);
        Assert.True(File.Exists(dir.Combine("data", "cache", "demos.json")));

        // A fresh library instance reads the cache file.
        var again = new DemoLibrary(new AppPaths(dir.Combine("cfg"), dir.Combine("data"))).Get(dir.Combine("demos/20261001T010203Z-A.ncdemo"));
        Assert.Equal(1, again.Summary.Launches);
        Assert.Equal("launch", again.Events.Single(e => e.IsMoment && e.Event == "launch").Event);
    }

    [Fact]
    public void ReadsRecorderDemoDirFromPluginConfig()
    {
        using var dir = new TempDir();
        var game = dir.FakeGame();
        dir.Write(Path.Combine(game, "BepInEx", "config", "local.nuclearoption.context.cfg"),
            "[Capture]\nOutputDirectory = S:\\x\n\n[Demo]\n\n## Demo output directory.\n# Setting type: String\nOutputDirectory = S:\\steamapps\\common\\Nuclear Option\\NuclearReplay\\demos\n");
        Assert.Equal(@"S:\steamapps\common\Nuclear Option\NuclearReplay\demos", DemoLibrary.ReadRecorderDemoDir(game));
    }

    /// <summary>Cross-check against tools/demo_inspect.py (the reference reader) when Python is available.</summary>
    [Fact]
    public void MatchesDemoInspectPy()
    {
        var tool = FindRepoFile("tools/demo_inspect.py");
        if (tool == null || !CanRun("python3")) return;
        using var dir = new TempDir();
        var path = dir.Write("x.ncdemo", Match().Raw(new byte[] { 1, 2, 3 }).ToArray());
        var script = "import sys,json; sys.path.insert(0, sys.argv[1]); import demo_inspect as d; h,r,t=d.read(sys.argv[2]); " +
                     "print(json.dumps({'messages':sum(1 for x in r if x[0]==1),'annotations':[round(x[1]-r[0][1],6) for x in r if x[0]==2],'truncated':t}))";
        var info = new ProcessStartInfo("python3") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add(Path.GetDirectoryName(tool)!);
        info.ArgumentList.Add(path);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        using var doc = JsonDocument.Parse(output);
        var scan = DemoFile.Scan(path);
        var details = DemoAnalyzer.Read(path);
        Assert.Equal(doc.RootElement.GetProperty("messages").GetInt64(), scan.MessageCount);
        Assert.Equal(doc.RootElement.GetProperty("truncated").GetBoolean(), scan.Truncated);
        var pyTimes = doc.RootElement.GetProperty("annotations").EnumerateArray().Select(e => e.GetDouble()).ToList();
        Assert.Equal(pyTimes, details.Events.Select(e => Math.Round(e.T, 6)).ToList());
    }

    internal static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static bool CanRun(string program)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(program, "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
        public override long Length => throw new NotSupportedException();
    }
}

public class RealPayloadTests
{
    // Annotation payloads as the 0.5 recorder writes them (orchestrator sample, 2026-10-05).
    [Fact]
    public void PlayerFlownUnitNamesAreNotDoubled()
    {
        var kill = DemoEvent.Parse(12, """{"event":"kill","victim":{"id":138,"name":"Talamander [T/A-30 Compass]","type":"aircraft","faction":"Boscali","player":"Talamander"},"killer":{"id":128,"name":"T/A-30 Compass","type":"aircraft","faction":"Primeva"},"weapon":"MMR-S3"}""")!;
        Assert.Equal("Talamander (T/A-30 Compass)", kill.Victim!.Label);
        Assert.Equal("T/A-30 Compass destroyed Talamander (T/A-30 Compass) with MMR-S3", kill.Summary);
        Assert.Equal("unit:128", kill.Subject!.ShotTarget);
    }

    [Fact]
    public void GenericSceneNameIsNotShownAsMap()
    {
        var mission = DemoEvent.Parse(0, """{"event":"mission","name":"05. Furball","map":"GameWorld","map_key_type":"scene","multiplayer":false}""")!;
        Assert.Null(mission.Map);
        Assert.Equal("Mission 05. Furball", mission.Summary);
        var early = DemoEvent.Parse(1, """{"event":"player","name":"Talamander","faction":null}""")!;
        Assert.Equal("Talamander joined", early.Summary);
    }
}
