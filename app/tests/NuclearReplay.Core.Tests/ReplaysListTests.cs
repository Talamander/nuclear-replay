using NuclearReplay.Core.Demos;
using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

// The Replays list: selection rules (replay/src/ListSelection.cs), the list cache (ReplayIndex.cs), the fast
// recording reader (DemoQuickScan.cs) and the list's text (ViewerText.cs).
public class ReplaysListTests
{
    private static ListSelection Rows(int n)
    {
        var s = new ListSelection();
        s.SetKeys(Enumerable.Range(0, n).Select(i => "r" + i));
        return s;
    }

    private static int[] Ticked(ListSelection s) => s.CheckedKeys().Select(s.IndexOf).ToArray();

    [Fact]
    public void PlainClickMovesTheHighlightAndTicksNothing()
    {
        var s = Rows(10);
        s.Click(3, shift: false, ctrl: false);
        Assert.Equal(3, s.CurrentIndex);
        Assert.Equal("r3", s.Anchor);
        Assert.Equal(0, s.CheckedCount);
        Assert.Equal(ListSelection.Ticks.None, s.State);
    }

    [Fact]
    public void CtrlClickTogglesOneRowAndKeepsTheOthers()
    {
        var s = Rows(10);
        s.Click(2, false, ctrl: true);
        s.Click(5, false, ctrl: true);
        Assert.Equal(new[] { 2, 5 }, Ticked(s));
        s.Click(2, false, ctrl: true);
        Assert.Equal(new[] { 5 }, Ticked(s));
        Assert.Equal(2, s.CurrentIndex);
    }

    [Fact]
    public void ShiftClickTicksTheRangeFromTheLastClickEitherWay()
    {
        var s = Rows(10);
        s.Click(2, false, false);
        s.Click(6, shift: true, ctrl: false);
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, Ticked(s));
        Assert.Equal(6, s.CurrentIndex);
        Assert.Equal("r2", s.Anchor);   // the anchor stays, so a second Shift+click re-ranges from it

        var up = Rows(10);
        up.Click(7, false, false);
        up.Click(4, shift: true, ctrl: false);
        Assert.Equal(new[] { 4, 5, 6, 7 }, Ticked(up));
    }

    [Fact]
    public void ShiftClickAddsToTicksAlreadyThere()
    {
        var s = Rows(12);
        s.Click(10, false, ctrl: true);
        s.Click(1, false, false);
        s.Click(3, shift: true, ctrl: false);
        Assert.Equal(new[] { 1, 2, 3, 10 }, Ticked(s));
    }

    [Fact]
    public void ShiftClickWithNothingClickedTicksJustThatRow()
    {
        var s = Rows(5);
        s.Click(3, shift: true, ctrl: false);
        Assert.Equal(new[] { 3 }, Ticked(s));
    }

    [Fact]
    public void CheckboxTogglesAndShiftCheckboxSetsTheRangeToTheNewState()
    {
        var s = Rows(10);
        s.ToggleBox(1, false);
        s.ToggleBox(6, shift: true);           // ticks 1..6
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, Ticked(s));
        s.ToggleBox(4, false);                 // untick one in the middle
        Assert.Equal(new[] { 1, 2, 3, 5, 6 }, Ticked(s));
        Assert.Equal(-1, s.CurrentIndex);      // checkboxes do not move the highlight
        s.ToggleBox(2, shift: true);           // row 2 is ticked, so Shift unticks 2..4 (from the anchor at 4)
        Assert.Equal(new[] { 1, 5, 6 }, Ticked(s));
    }

    [Fact]
    public void SelectAllClearAndToggleAll()
    {
        var s = Rows(4);
        s.ToggleAll();
        Assert.Equal(ListSelection.Ticks.All, s.State);
        Assert.Equal(4, s.CheckedCount);
        s.ToggleBox(0, false);
        Assert.Equal(ListSelection.Ticks.Some, s.State);
        s.ToggleAll();                         // some ticked: ticks the rest
        Assert.Equal(ListSelection.Ticks.All, s.State);
        s.ToggleAll();                         // all ticked: clears
        Assert.Equal(ListSelection.Ticks.None, s.State);
        s.SelectAll();
        s.Clear();
        Assert.Equal(0, s.CheckedCount);
    }

    [Fact]
    public void NewListKeepsTicksOfRecordingsStillThereAndDropsTheRest()
    {
        var s = Rows(6);
        s.Click(1, false, ctrl: true);
        s.Click(3, false, ctrl: true);
        s.Click(4, false, ctrl: true);         // current and anchor: r4
        // r3 and r4 deleted, a new recording on top: indexes move, keys stay.
        s.SetKeys(new[] { "new", "r0", "r1", "r2", "R5" });
        Assert.Equal(new[] { "r1" }, s.CheckedKeys());
        Assert.Equal(2, s.IndexOf("r1"));
        Assert.Null(s.Current);
        Assert.Null(s.Anchor);
        Assert.Equal(4, s.IndexOf("r5"));      // paths compare without case (Windows)
    }

    [Fact]
    public void VersionChangesOnEveryEdit()
    {
        var s = Rows(3);
        int v = s.Version;
        s.Click(1, false, false); Assert.NotEqual(v, v = s.Version);
        s.ToggleBox(1, false); Assert.NotEqual(v, v = s.Version);
        s.SetKeys(new[] { "r0" }); Assert.NotEqual(v, s.Version);
    }

    [Fact]
    public void ClicksOutsideTheListDoNothing()
    {
        var s = Rows(3);
        s.Click(5, true, false);
        s.ToggleBox(-1, false);
        Assert.Equal(0, s.CheckedCount);
        Assert.Null(s.Current);
    }

    private static ReplayIndex.Entry Entry(string path, long size = 1000, long ticks = 638_000_000_000_000_000) => new()
    {
        Path = path, Size = size, ModifiedTicks = ticks, CreatedTicks = ticks - 10,
        Mission = "Escalation", Map = "Heartland", Duration = 1234.5, Players = new List<string> { "Talamander", "Cheeze" },
        Kills = 7, Launches = 19,
    };

    [Fact]
    public void ListCacheRoundTrips()
    {
        var index = new ReplayIndex();
        index.Put(Entry(@"Z:\games\NuclearReplay\demos\a.ncdemo"));
        index.Put(new ReplayIndex.Entry { Path = @"Z:\b.ncdemo", Size = 5, ModifiedTicks = 9, Error = "damaged header", Truncated = true });
        var back = ReplayIndex.Parse(index.Serialize());
        Assert.Equal(2, back.Count);
        var a = back.Find(@"z:\GAMES\NuclearReplay\demos\a.ncdemo", 1000, 638_000_000_000_000_000);
        Assert.NotNull(a);
        Assert.Equal("Escalation", a.Mission);
        Assert.Equal("Heartland", a.Map);
        Assert.Equal(1234.5, a.Duration);
        Assert.Equal(new[] { "Talamander", "Cheeze" }, a.Players);
        Assert.Equal(7, a.Kills);
        Assert.Equal(19, a.Launches);
        Assert.Equal(638_000_000_000_000_000 - 10, a.CreatedTicks);
        var b = back.Find(@"Z:\b.ncdemo", 5, 9);
        Assert.Equal("damaged header", b.Error);
        Assert.True(b.Truncated);
        Assert.Null(b.CreatedTicks);
        Assert.Null(b.Mission);
        Assert.Empty(b.Players);
    }

    [Fact]
    public void ListCacheEntryIsStaleOnceTheFileChanges()
    {
        var index = new ReplayIndex();
        index.Put(Entry("a.ncdemo", size: 1000, ticks: 50));
        Assert.NotNull(index.Find("a.ncdemo", 1000, 50));
        Assert.Null(index.Find("a.ncdemo", 1001, 50));    // grew (a match still recording, a copy finishing)
        Assert.Null(index.Find("a.ncdemo", 1000, 51));    // rewritten
        Assert.Null(index.Find("b.ncdemo", 1000, 50));
        Assert.Null(index.Find(null, 1000, 50));
    }

    [Fact]
    public void ListCacheKeepsAwkwardNames()
    {
        var e = Entry("a\tb\\c.ncdemo");
        e.Mission = "Line\none\ttab \\ back \u001f unit";
        e.Players = new List<string> { "a\u001fb", "", "tab\there" };
        var index = new ReplayIndex();
        index.Put(e);
        var back = ReplayIndex.Parse(index.Serialize()).Find("a\tb\\c.ncdemo", e.Size, e.ModifiedTicks);
        Assert.NotNull(back);
        Assert.Equal(e.Mission, back.Mission);
        Assert.Equal(new[] { "a\u001fb", null, "tab\there" }, back.Players);
    }

    [Fact]
    public void ListCacheIgnoresOtherVersionsAndDamagedLines()
    {
        Assert.Equal(0, ReplayIndex.Parse("").Count);
        Assert.Equal(0, ReplayIndex.Parse(null).Count);
        Assert.Equal(0, ReplayIndex.Parse("nuclear-replay list cache 2\na\t1\t2\t-\t0\t0\t0\t0\t\t\t\t\n").Count);
        var good = new ReplayIndex();
        good.Put(Entry("good.ncdemo"));
        string text = good.Serialize().Replace("\n", "\r\n")
            + "short\tline\n"
            + "bad\tnot-a-number\t2\t-\t0\t0\t0\t0\t\t\t\t\n"
            + "\t1\t2\t-\t0\t0\t0\t0\t\t\t\t\n";     // no path
        var back = ReplayIndex.Parse(text);
        Assert.Equal(1, back.Count);
        Assert.NotNull(back.Find("good.ncdemo", 1000, 638_000_000_000_000_000));
    }

    [Fact]
    public void ListCachePrunesAndSavesThroughATemporaryFile()
    {
        using var dir = new TempDir();
        var index = new ReplayIndex();
        index.Put(Entry("keep.ncdemo"));
        index.Put(Entry("gone.ncdemo"));
        Assert.True(index.Dirty);
        Assert.Equal(1, index.Prune(p => p != "gone.ncdemo"));
        string file = dir.Combine("NuclearReplay", ReplayIndex.FileName);
        Assert.Null(index.Save(file));
        Assert.False(index.Dirty);
        Assert.Null(index.Save(file));      // over an existing file
        Assert.False(File.Exists(file + ".tmp"));
        var back = ReplayIndex.Load(file);
        Assert.Equal(new[] { "keep.ncdemo" }, back.Paths);
        Assert.Equal(0, ReplayIndex.Load(dir.Combine("missing.txt")).Count);
        File.WriteAllText(dir.Combine("junk.txt"), "\0\0garbage");
        Assert.Equal(0, ReplayIndex.Load(dir.Combine("junk.txt")).Count);
        Assert.True(index.Remove("keep.ncdemo"));
        Assert.False(index.Remove("keep.ncdemo"));
    }

    private const string Header = """{"format":"nuclear-context-network-demo","format_version":1,"created_utc":"2026-10-04T02:19:32Z","mission_name":"Escalation"}""";

    private static DemoFile.Builder Match(int messages = 200, int payload = 300)
    {
        var b = new DemoFile.Builder(Header).Annotation(100.0, """{"event":"mission","name":"Escalation","map":"Heartland"}""");
        for (int i = 0; i < messages; i++)
        {
            b.Message(100.5 + i, Enumerable.Repeat((byte)i, payload + i % 7).ToArray());
            if (i % 40 == 0) b.Annotation(100.6 + i, "{\"event\":\"kill\",\"n\":" + i + ",\"pad\":\"" + new string('x', 500 + i) + "\"}");
        }
        return b.Annotation(400.0, """{"event":"end"}""");
    }

    [Theory]
    [InlineData(1 << 20)]
    [InlineData(4096)]
    [InlineData(200)]      // smaller than most records: every annotation spills past the buffer
    [InlineData(1)]        // clamped to the smallest buffer
    public void QuickScanMatchesTheFullReader(int chunk)
    {
        var bytes = Match().ToArray();
        var full = DemoFile.Scan(new MemoryStream(bytes));
        var quick = DemoQuickScan.Read(new MemoryStream(bytes), chunk);
        Assert.Contains("\"Escalation\"", quick.HeaderJson);
        Assert.Equal(full.MessageCount + full.AnnotationCount, quick.Records);
        Assert.Equal(full.Annotations.Select(a => (a.Time, a.Json)), quick.Annotations.Select(a => (a.Key, a.Value)));
        Assert.Equal(100.0, quick.First);
        Assert.Equal(400.0, quick.Last);
        Assert.False(quick.Truncated);
    }

    [Theory]
    [InlineData(5)]        // part of a record header
    [InlineData(30)]       // part of a payload
    public void QuickScanStopsAtACutOffRecord(int extra)
    {
        var whole = Match(messages: 20).ToArray();
        // A record header with a payload that runs past the end, then only `extra` bytes of it.
        var tail = new byte[22];
        tail[0] = 1;
        BitConverter.GetBytes(999.0).CopyTo(tail, 1);
        BitConverter.GetBytes(100).CopyTo(tail, 18);
        var bytes = whole.Concat(extra < 22 ? tail.Take(extra) : tail.Concat(new byte[extra - 22 + 1])).ToArray();
        var quick = DemoQuickScan.Read(new MemoryStream(bytes), 64);
        var full = DemoFile.Scan(new MemoryStream(bytes));
        Assert.True(quick.Truncated);
        Assert.Equal(full.Truncated, quick.Truncated);
        Assert.Equal(full.MessageCount + full.AnnotationCount, quick.Records);
        Assert.Equal(400.0, quick.Last);
    }

    [Fact]
    public void QuickScanRefusesOtherFiles()
    {
        Assert.Throws<InvalidDataException>(() => DemoQuickScan.Read(new MemoryStream("not a demo at all"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => DemoQuickScan.Read(new MemoryStream(new byte[3])));
        var bytes = Match(messages: 1).ToArray();
        Assert.Throws<InvalidDataException>(() => DemoQuickScan.Read(new MemoryStream(bytes.Take(20).ToArray())));   // header cut off
    }

    [Fact]
    public void QuickScanReadsAFileOnDisk()
    {
        using var dir = new TempDir();
        string path = dir.Write("20261030T034243Z-03. Point Blank.ncdemo", Match(messages: 50).ToArray());
        var quick = DemoQuickScan.Read(path);
        Assert.Equal(300.0, quick.Last.GetValueOrDefault() - quick.First.GetValueOrDefault(), 6);
        Assert.Equal(1 + 2 + 1, quick.Annotations.Count);
    }

    [Fact]
    public void ListTextForTicksAndBulkDelete()
    {
        Assert.Equal("", ViewerText.SelectionSummary(0, 0));
        Assert.Equal("3 selected, 1.2 GB", ViewerText.SelectionSummary(3, 1_300_000_000));
        Assert.Equal("Delete", ViewerText.DeleteLabel(0));
        Assert.Equal("Delete 12", ViewerText.DeleteLabel(12));
        Assert.Equal("Delete 3 recordings (1.2 GB) for good? Clips made from them stay.", ViewerText.BulkDeleteQuestion(3, 1_300_000_000));
        Assert.Equal("Delete 1 recording (2 MB) for good? Clips made from them stay.", ViewerText.BulkDeleteQuestion(1, 2_000_000));
        Assert.Equal("4 recordings, 1.2 GB on disk", ViewerText.LibrarySummary(4, 1_300_000_000, 0));
        Assert.Equal("300 recordings, 40 GB on disk, reading details (250 to go)", ViewerText.LibrarySummary(300, 43_000_000_000, 250));
    }

    [Fact]
    public void BulkDeleteResultNamesWhatFailedAndWhy()
    {
        Assert.Equal("Deleted 5 recordings (12 MB).", ViewerText.BulkDeleteResult(5, 12L << 20, new List<KeyValuePair<string, string>>()));
        Assert.Equal("Deleted 1 recording (1 KB).", ViewerText.BulkDeleteResult(1, 10, null));
        var failed = new List<KeyValuePair<string, string>> { new("Escalation", "the file is in use") };
        Assert.Equal("Deleted 2 recordings (3 MB). 1 could not be deleted: Escalation (the file is in use). They stay ticked.",
            ViewerText.BulkDeleteResult(2, 3_000_000, failed));
        var many = Enumerable.Range(1, 5).Select(i => new KeyValuePair<string, string>("M" + i, "the file is read-only or not yours")).ToList();
        Assert.Equal("Nothing was deleted. 5 could not be deleted: M1 (the file is read-only or not yours), M2 (the file is read-only or not yours), "
            + "M3 (the file is read-only or not yours) and 2 more. They stay ticked.", ViewerText.BulkDeleteResult(0, 0, many));
    }

    [Theory]
    [InlineData("20261030T034243Z-03. Point Blank.ncdemo", "03. Point Blank", "2026-10-30T03:42:43")]
    [InlineData("20261024T040505Z-HAW - Annihilation 2.0-client.ncdemo", "HAW - Annihilation 2.0-client", "2026-10-24T04:05:05")]
    [InlineData("my match.ncdemo", "my match", null)]                  // renamed by hand: no stamp
    [InlineData("20261399T999999Z-bad.ncdemo", "20261399T999999Z-bad", null)]
    [InlineData("short.ncdemo", "short", null)]
    public void FileNameGivesTitleAndDateBeforeTheRecordingIsRead(string file, string title, string? stamp)
    {
        Assert.Equal(title, ViewerText.TitleFromFileName(file));
        var got = ViewerText.StampFromFileName(file);
        if (stamp == null) Assert.Null(got);
        else
        {
            Assert.Equal(DateTime.Parse(stamp, System.Globalization.CultureInfo.InvariantCulture), got);
            Assert.Equal(DateTimeKind.Utc, got!.Value.Kind);
        }
    }
}
