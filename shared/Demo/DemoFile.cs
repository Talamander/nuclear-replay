using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace NuclearReplay.Demo
{
    // Network demo: the exact Mirage messages a host server sent to one observing connection.
    //
    // File layout (little endian):
    //   8 bytes  magic "NCDEMO\0" + format version byte
    //   int32    header length, then UTF-8 JSON header
    //   records  until end of file:
    //     byte    kind (1 = message, 2 = annotation)
    //     float64 recorder Time.unscaledTimeAsDouble when the message was sent
    //     float64 server NetworkTime.Time when the message was sent
    //     byte    channel (0 reliable, 1 unreliable, 2 notify) for messages; 0 for annotations
    //     int32   payload length, then payload (packed Mirage message, or UTF-8 JSON annotation)
    //
    // A truncated final record (crash, power loss) is ignored by the reader; everything before it replays.
    public static class DemoFormat
    {
        public const byte Version = 1;
        public static readonly byte[] Magic = { (byte)'N', (byte)'C', (byte)'D', (byte)'E', (byte)'M', (byte)'O', 0, Version };
        public const byte KindMessage = 1;
        public const byte KindAnnotation = 2;
        public const byte ChannelReliable = 0;
        public const byte ChannelUnreliable = 1;
        public const byte ChannelNotify = 2;
        public const int RecordHeaderBytes = 1 + 8 + 8 + 1 + 4;
        public const string Extension = ".ncdemo";

        public static ushort MessageId(byte[] payload) => payload.Length >= 2 ? (ushort)(payload[0] | payload[1] << 8) : (ushort)0;
    }

    public struct DemoRecord
    {
        public byte Kind;
        public double Time;
        public double NetTime;
        public byte Channel;
        public byte[] Payload;
        public ushort MessageId => DemoFormat.MessageId(Payload);
        public string Text => Encoding.UTF8.GetString(Payload);
    }

    // Writes a demo on a background thread so the game thread never touches the disk.
    //
    // Failure policy: the game thread never blocks on the disk and never gets an exception from the writer.
    // - Queue full (the disk stopped keeping up): the writer refuses all later records, sets Fault, writes out
    //   the whole records already queued and closes the file. The file stays readable up to that point.
    // - Write error: the writer sets Fault, drops the queue and closes the file. A partial final record is
    //   ignored by the reader, as after a crash.
    // The recorder polls Fault and stops the demo. Only the writer thread touches the file after the header,
    // so Close never races a write; if the thread does not finish in time, it closes the file itself later.
    public sealed class DemoWriter : IDisposable
    {
        // About 100 minutes of a typical 10 KiB/s recording waiting for the disk.
        public const long DefaultMaxQueuedBytes = 64L << 20;
        public const int DefaultCloseWaitMilliseconds = 3000;

        private readonly ConcurrentQueue<byte[]> queue = new ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly Thread thread;
        private readonly Stream file;
        private readonly long maxQueuedBytes;
        private volatile bool stopping;
        private volatile bool writeFailed;
        private string fault;
        private long queuedBytes, records;
        public string Path { get; }
        // Null while healthy; the first failure's reason otherwise. Once set, no later record is accepted.
        public string Fault => Volatile.Read(ref fault);
        public long Records => Interlocked.Read(ref records);

        public DemoWriter(string path, string headerJson, long maxQueuedBytes = DefaultMaxQueuedBytes)
            : this(path, OpenFile(path), headerJson, maxQueuedBytes) { }

        // Tests pass their own stream to simulate slow or failing disks.
        internal DemoWriter(string path, Stream stream, string headerJson, long maxQueuedBytes)
        {
            Path = path;
            file = stream;
            this.maxQueuedBytes = maxQueuedBytes;
            try
            {
                var header = Encoding.UTF8.GetBytes(headerJson);
                file.Write(DemoFormat.Magic, 0, DemoFormat.Magic.Length);
                file.Write(BitConverter.GetBytes(header.Length), 0, 4);
                file.Write(header, 0, header.Length);
                file.Flush();
            }
            catch { file.Dispose(); throw; }
            thread = new Thread(Run) { IsBackground = true, Name = "NuclearReplay demo writer" };
            thread.Start();
        }

        private static Stream OpenFile(string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
        }

        // Called on the game thread. One allocation and one copy; no disk access.
        public void Message(double time, double netTime, byte channel, byte[] source, int offset, int length)
        {
            if (!Accepting) return;
            var record = new byte[DemoFormat.RecordHeaderBytes + length];
            WriteHeader(record, DemoFormat.KindMessage, time, netTime, channel, length);
            Buffer.BlockCopy(source, offset, record, DemoFormat.RecordHeaderBytes, length);
            Push(record);
        }

        public void Annotation(double time, double netTime, string json)
        {
            if (!Accepting) return;
            var payload = Encoding.UTF8.GetBytes(json);
            var record = new byte[DemoFormat.RecordHeaderBytes + payload.Length];
            WriteHeader(record, DemoFormat.KindAnnotation, time, netTime, 0, payload.Length);
            Buffer.BlockCopy(payload, 0, record, DemoFormat.RecordHeaderBytes, payload.Length);
            Push(record);
        }

        private bool Accepting => !stopping && Fault == null;

        private static void WriteHeader(byte[] record, byte kind, double time, double netTime, byte channel, int length)
        {
            record[0] = kind;
            Array.Copy(BitConverter.GetBytes(time), 0, record, 1, 8);
            Array.Copy(BitConverter.GetBytes(netTime), 0, record, 9, 8);
            record[17] = channel;
            Array.Copy(BitConverter.GetBytes(length), 0, record, 18, 4);
        }

        private void Push(byte[] record)
        {
            long waiting = Interlocked.Add(ref queuedBytes, record.Length);
            if (waiting > maxQueuedBytes)
            {
                Interlocked.Add(ref queuedBytes, -record.Length);
                SetFault($"write queue full ({waiting - record.Length} bytes waiting for the disk); recording stopped");
                signal.Set();
                return;
            }
            queue.Enqueue(record);
            signal.Set();
        }

        private void SetFault(string reason) => Interlocked.CompareExchange(ref fault, reason, null);

        private void Run()
        {
            var lastFlush = DateTime.UtcNow;
            try
            {
                while (true)
                {
                    signal.WaitOne(250);
                    while (queue.TryDequeue(out var record))
                    {
                        file.Write(record, 0, record.Length);
                        Interlocked.Add(ref queuedBytes, -record.Length);
                        Interlocked.Increment(ref records);
                    }
                    // Bound loss on a crash to about one second of traffic.
                    if ((DateTime.UtcNow - lastFlush).TotalSeconds >= 1) { file.Flush(); lastFlush = DateTime.UtcNow; }
                    // Nothing more can arrive once stopping or faulted; the queue is written out first.
                    if ((stopping || Fault != null) && queue.IsEmpty) break;
                }
                file.Flush();
            }
            catch (Exception e)
            {
                writeFailed = true;
                SetFault("write failed: " + e.GetType().Name + ": " + e.Message);
                while (queue.TryDequeue(out var dropped)) Interlocked.Add(ref queuedBytes, -dropped.Length);
            }
            finally
            {
                try { file.Dispose(); }
                catch (Exception e) { if (!writeFailed) SetFault("close failed: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        // Stops accepting records and waits up to waitMilliseconds for the queue to reach the disk.
        // Returns false if the writer is still busy; it then finishes and closes the file on its own.
        // Never throws.
        public bool Close(int waitMilliseconds)
        {
            stopping = true;
            signal.Set();
            try { return thread.Join(Math.Max(0, waitMilliseconds)); }
            catch (Exception) { return false; }
        }

        public void Dispose() => Close(DefaultCloseWaitMilliseconds);
    }

    public sealed class DemoReader
    {
        public string HeaderJson { get; private set; }
        public List<DemoRecord> Records { get; } = new List<DemoRecord>();
        public bool Truncated { get; private set; }

        public static DemoReader Load(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return Load(stream);
        }

        public static DemoReader Load(Stream stream)
        {
            var reader = new DemoReader();
            var input = new BinaryReader(stream);
            var magic = input.ReadBytes(DemoFormat.Magic.Length);
            for (int i = 0; i < DemoFormat.Magic.Length - 1; i++)
                if (magic.Length != DemoFormat.Magic.Length || magic[i] != DemoFormat.Magic[i]) throw new InvalidDataException("Not a Nuclear Replay demo file.");
            if (magic[DemoFormat.Magic.Length - 1] != DemoFormat.Version) throw new InvalidDataException("Unsupported demo format version " + magic[DemoFormat.Magic.Length - 1]);
            int headerLength = input.ReadInt32();
            reader.HeaderJson = Encoding.UTF8.GetString(input.ReadBytes(headerLength));
            while (true)
            {
                var head = input.ReadBytes(DemoFormat.RecordHeaderBytes);
                if (head.Length == 0) break;
                if (head.Length < DemoFormat.RecordHeaderBytes) { reader.Truncated = true; break; }
                int length = BitConverter.ToInt32(head, 18);
                if (length < 0) { reader.Truncated = true; break; }
                var payload = input.ReadBytes(length);
                if (payload.Length < length) { reader.Truncated = true; break; }
                reader.Records.Add(new DemoRecord { Kind = head[0], Time = BitConverter.ToDouble(head, 1), NetTime = BitConverter.ToDouble(head, 9),
                    Channel = head[17], Payload = payload });
            }
            return reader;
        }
    }
}
