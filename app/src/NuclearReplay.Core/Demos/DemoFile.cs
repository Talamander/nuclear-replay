using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NuclearReplay.Core.Demos;

/// <summary>
/// Reader for Nuclear Replay network demos (.ncdemo). Same rules as tools/demo_inspect.py and shared/Demo/DemoFile.cs:
/// <code>
///   8 bytes  "NCDEMO\0" + format version byte
///   int32    header length, then UTF-8 JSON header
///   records until end of file: byte kind (1 message, 2 annotation), float64 record time, float64 net time,
///                              byte channel, int32 payload length, payload
/// </code>
/// A truncated final record (crash, power loss, recording still running) ends the read; everything before it counts.
/// Message payloads are skipped by seeking, so a 150 MB match is scanned without loading it.
/// </summary>
public static class DemoFile
{
    public static readonly byte[] Magic = "NCDEMO\0"u8.ToArray();
    public const int RecordHeaderBytes = 22;
    public const byte KindMessage = 1;
    public const byte KindAnnotation = 2;
    public const string Extension = ".ncdemo";

    public sealed class ScanResult
    {
        public required JsonObject Header { get; init; }
        public int FormatVersion { get; init; }
        public long MessageCount { get; set; }
        public long AnnotationCount { get; set; }
        public double? FirstTime { get; set; }
        public double? LastTime { get; set; }
        public double? FirstMessageTime { get; set; }
        public double? LastMessageTime { get; set; }
        public bool Truncated { get; set; }
        /// <summary>Annotations in file order with their raw record time.</summary>
        public List<(double Time, double NetTime, string Json)> Annotations { get; } = new();
        public double Duration => FirstTime.HasValue && LastTime.HasValue ? LastTime.Value - FirstTime.Value : 0;
    }

    public static ScanResult Scan(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Scan(stream);
    }

    public static ScanResult Scan(Stream stream)
    {
        Span<byte> head = stackalloc byte[RecordHeaderBytes];
        var prefix = new byte[12];
        if (ReadFully(stream, prefix) < 12 || !prefix.AsSpan(0, 7).SequenceEqual(Magic))
            throw new InvalidDataException("Not a Nuclear Replay demo file.");
        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
        if (headerLength < 0 || headerLength > 16 << 20) throw new InvalidDataException("Demo header is damaged.");
        var headerBytes = new byte[headerLength];
        if (ReadFully(stream, headerBytes) < headerLength) throw new InvalidDataException("Demo header is incomplete.");
        JsonObject header;
        try { header = JsonNode.Parse(headerBytes) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { throw new InvalidDataException("Demo header is not valid JSON."); }

        var result = new ScanResult { Header = header, FormatVersion = prefix[7] };
        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        long pos = 12 + headerLength;
        while (true)
        {
            int got = ReadFully(stream, head);
            if (got == 0) break;
            if (got < RecordHeaderBytes) { result.Truncated = true; break; }
            byte kind = head[0];
            double time = BinaryPrimitives.ReadDoubleLittleEndian(head[1..]);
            double net = BinaryPrimitives.ReadDoubleLittleEndian(head[9..]);
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(head[18..]);
            if (payloadLength < 0 || pos + RecordHeaderBytes + payloadLength > length) { result.Truncated = true; break; }
            if (kind == KindAnnotation)
            {
                var payload = new byte[payloadLength];
                if (ReadFully(stream, payload) < payloadLength) { result.Truncated = true; break; }
                result.Annotations.Add((time, net, Encoding.UTF8.GetString(payload)));
                result.AnnotationCount++;
            }
            else
            {
                if (stream.CanSeek) stream.Seek(payloadLength, SeekOrigin.Current);
                else if (Skip(stream, payloadLength) < payloadLength) { result.Truncated = true; break; }
                if (kind == KindMessage)
                {
                    result.MessageCount++;
                    result.FirstMessageTime ??= time;
                    result.LastMessageTime = time;
                }
            }
            result.FirstTime ??= time;
            result.LastTime = time;
            pos += RecordHeaderBytes + payloadLength;
        }
        return result;
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static long Skip(Stream stream, int count)
    {
        var buffer = new byte[Math.Min(count, 81920)];
        long skipped = 0;
        while (skipped < count)
        {
            int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count - skipped));
            if (n == 0) break;
            skipped += n;
        }
        return skipped;
    }

    /// <summary>Builds a demo file in memory (tests and tools).</summary>
    public sealed class Builder
    {
        private readonly MemoryStream stream = new();

        public Builder(string headerJson, byte version = 1)
        {
            stream.Write(Magic);
            stream.WriteByte(version);
            var header = Encoding.UTF8.GetBytes(headerJson);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, header.Length);
            stream.Write(len);
            stream.Write(header);
        }

        public Builder Message(double time, byte[] payload, double net = 0, byte channel = 0) => Record(KindMessage, time, net, channel, payload);
        public Builder Annotation(double time, string json, double net = 0) => Record(KindAnnotation, time, net, 0, Encoding.UTF8.GetBytes(json));

        public Builder Record(byte kind, double time, double net, byte channel, byte[] payload)
        {
            Span<byte> head = stackalloc byte[RecordHeaderBytes];
            head[0] = kind;
            BinaryPrimitives.WriteDoubleLittleEndian(head[1..], time);
            BinaryPrimitives.WriteDoubleLittleEndian(head[9..], net);
            head[17] = channel;
            BinaryPrimitives.WriteInt32LittleEndian(head[18..], payload.Length);
            stream.Write(head);
            stream.Write(payload);
            return this;
        }

        /// <summary>Appends raw bytes, for example a partial record to simulate a crash mid-write.</summary>
        public Builder Raw(byte[] bytes) { stream.Write(bytes); return this; }

        public byte[] ToArray() => stream.ToArray();
    }
}
