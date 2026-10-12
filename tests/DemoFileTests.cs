using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NuclearReplay.Demo;

// In-memory stand-in for the demo file: can stall (Gate) or fail (FailOnWrite), and notes any use after close.
sealed class TestStream : Stream
{
    private readonly MemoryStream inner = new MemoryStream();
    public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(true);
    public int FailOnWrite = -1; // index of the Write call that throws (header writes count)
    public volatile bool Closed, UsedAfterClose;
    private int writes;

    public byte[] Bytes => inner.ToArray();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { if (Closed) UsedAfterClose = true; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Closed) { UsedAfterClose = true; throw new ObjectDisposedException("TestStream"); }
        Gate.Wait();
        if (writes++ == FailOnWrite) throw new IOException("No space left on device");
        inner.Write(buffer, offset, count);
    }

    protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
}

static class DemoFileTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }

    static void WaitFor(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > 5000) throw new Exception("FAIL: timed out waiting for " + message); Thread.Sleep(5); }
    }

    // 8-byte payload: 30-byte records.
    static readonly byte[] Payload = { 1, 0, 2, 3, 4, 5, 6, 7 };
    const int HeaderWrites = 3;

    // A stalled disk must not grow memory without bound or block the game thread. The writer refuses records
    // once the queue is full, then writes out what it already accepted, so the file replays up to that point.
    static void QueueOverflowStopsCleanly()
    {
        var stream = new TestStream();
        var writer = new DemoWriter("overflow.ncdemo", stream, "{}", maxQueuedBytes: 100);
        stream.Gate.Reset();
        var clock = Stopwatch.StartNew();
        int accepted = 0;
        while (writer.Fault == null && accepted < 100)
        {
            writer.Message(accepted, accepted, DemoFormat.ChannelReliable, Payload, 0, Payload.Length);
            if (writer.Fault == null) accepted++;
        }
        writer.Annotation(99, 99, "{\"event\":\"after\"}");
        writer.Message(99, 99, DemoFormat.ChannelReliable, Payload, 0, Payload.Length);
        Check(clock.ElapsedMilliseconds < 1000, "game thread not blocked by a stalled disk");
        Check(accepted == 3, "queue holds 100 bytes = 3 records, accepted " + accepted);
        Check(writer.Fault != null && writer.Fault.Contains("queue full"), "overflow fault reason: " + writer.Fault);
        stream.Gate.Set();
        Check(writer.Close(5000), "writer finishes after the disk recovers");
        Check(stream.Closed && !stream.UsedAfterClose, "stream closed once, by the writer");
        var demo = DemoReader.Load(new MemoryStream(stream.Bytes));
        Check(demo.Records.Count == 3 && !demo.Truncated, "overflowed demo keeps whole accepted records: " + demo.Records.Count);
        Check(demo.Records[2].Time == 2, "records kept in order");
    }

    // A write error after the file opened: one fault with the reason, later records refused, file closed, no throw.
    static void FaultAfterOpen()
    {
        var stream = new TestStream { FailOnWrite = HeaderWrites + 1 };
        var writer = new DemoWriter("fault.ncdemo", stream, "{}", DemoWriter.DefaultMaxQueuedBytes);
        writer.Message(1, 1, DemoFormat.ChannelReliable, Payload, 0, Payload.Length);
        writer.Message(2, 2, DemoFormat.ChannelReliable, Payload, 0, Payload.Length);
        WaitFor(() => writer.Fault != null, "write fault");
        Check(writer.Fault.Contains("write failed") && writer.Fault.Contains("No space left on device"), "fault reason: " + writer.Fault);
        var first = writer.Fault;
        writer.Message(3, 3, DemoFormat.ChannelReliable, Payload, 0, Payload.Length);
        writer.Annotation(3, 3, "{}");
        Check(writer.Fault == first, "first fault reason kept");
        WaitFor(() => stream.Closed, "writer closes the failed stream");
        writer.Dispose();
        writer.Dispose();
        Check(!stream.UsedAfterClose, "no use of the closed stream");
        var demo = DemoReader.Load(new MemoryStream(stream.Bytes));
        Check(demo.Records.Count == 1 && !demo.Truncated, "records before the fault replay: " + demo.Records.Count);
    }

    // Dispose while the writer is stuck on the disk: returns after its wait, never touches the stream itself;
    // the writer finishes and closes the file once the disk moves again.
    static void DisposeWithSlowWriter()
    {
        var stream = new TestStream();
        var writer = new DemoWriter("slow.ncdemo", stream, "{}", DemoWriter.DefaultMaxQueuedBytes);
        stream.Gate.Reset();
        writer.Message(1, 1, DemoFormat.ChannelReliable, Payload, 0, Payload.Length);
        writer.Message(2, 2, DemoFormat.ChannelReliable, Payload, 0, Payload.Length);
        Check(!writer.Close(50), "close reports a busy writer");
        var clock = Stopwatch.StartNew();
        writer.Dispose();
        Check(clock.ElapsedMilliseconds < DemoWriter.DefaultCloseWaitMilliseconds + 2000, "dispose wait is bounded");
        Check(!stream.Closed, "dispose leaves a busy stream to the writer");
        writer.Message(3, 3, DemoFormat.ChannelReliable, Payload, 0, Payload.Length);
        stream.Gate.Set();
        WaitFor(() => stream.Closed, "writer closes the stream after the stall");
        Check(!stream.UsedAfterClose && writer.Fault == null, "no use after close, no fault");
        var demo = DemoReader.Load(new MemoryStream(stream.Bytes));
        Check(demo.Records.Count == 2 && !demo.Truncated, "records accepted before dispose all written: " + demo.Records.Count);
    }

    static int Main()
    {
        QueueOverflowStopsCleanly();
        FaultAfterOpen();
        DisposeWithSlowWriter();

        var directory = Path.Combine(Path.GetTempPath(), "ncdemo-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "a.ncdemo");
        var payload = new byte[] { 9, 9, 0x34, 0x12, 1, 2, 3, 9 };
        using (var writer = new DemoWriter(path, "{\"format\":\"test\"}"))
        {
            writer.Message(1.5, 100.25, DemoFormat.ChannelReliable, payload, 2, 5);
            writer.Annotation(2.0, 101.0, "{\"event\":\"x\"}");
            writer.Message(2.5, 101.5, DemoFormat.ChannelUnreliable, new byte[0], 0, 0);
        }
        var demo = DemoReader.Load(path);
        Check(demo.HeaderJson == "{\"format\":\"test\"}", "header");
        Check(demo.Records.Count == 3, "record count " + demo.Records.Count);
        var first = demo.Records[0];
        Check(first.Kind == DemoFormat.KindMessage && first.Time == 1.5 && first.NetTime == 100.25 && first.Channel == 0, "first record fields");
        Check(first.Payload.Length == 5 && first.MessageId == 0x1234 && first.Payload[4] == 3, "payload slice and id");
        Check(demo.Records[1].Kind == DemoFormat.KindAnnotation && demo.Records[1].Text == "{\"event\":\"x\"}", "annotation");
        Check(demo.Records[2].Payload.Length == 0 && demo.Records[2].Channel == 1, "empty unreliable");
        Check(!demo.Truncated, "not truncated");

        // A crash mid-record leaves a partial tail; everything before it must still load.
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^3]);
        demo = DemoReader.Load(path);
        Check(demo.Truncated && demo.Records.Count == 2, "truncated tail");

        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        bool rejected = false;
        try { DemoReader.Load(path); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "bad magic rejected");
        Directory.Delete(directory, true);
        Console.WriteLine("DemoFileTests passed");
        return 0;
    }
}
