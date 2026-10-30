#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NuclearReplay.Playback
{
    // Reads what the Replays list needs from a recording (the header, the event annotations, the first and last
    // record times) in one sequential pass over big buffers. Message payloads are skipped in memory, so a 330 MB
    // match with 1.8 million records costs one sequential read and no allocation per record (the old reader made a
    // small array and a seek for every record, which on Mono under Proton meant a buffer refill each time).
    //
    // Same file rules as shared/Demo/DemoFile.cs (DemoFormat) and the nr reader; the constants are repeated here so
    // the tests can link this file without the plugin. A truncated final record (crash, power loss) ends the scan
    // with Truncated set, as the replay reader does.
    internal static class DemoQuickScan
    {
        internal static readonly byte[] MagicPrefix = { (byte)'N', (byte)'C', (byte)'D', (byte)'E', (byte)'M', (byte)'O', 0 };
        internal const int RecordHeaderBytes = 1 + 8 + 8 + 1 + 4;
        internal const byte KindAnnotation = 2;
        internal const int MaxHeaderBytes = 16 << 20;

        internal sealed class Result
        {
            public string HeaderJson;
            public double? First, Last;
            public bool Truncated;
            public long Records;
            public readonly List<KeyValuePair<double, string>> Annotations = new List<KeyValuePair<double, string>>();
        }

        public static Result Read(string path, int chunkBytes = 1 << 20)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
                return Read(stream, chunkBytes);
        }

        public static Result Read(Stream stream, int chunkBytes = 1 << 20)
        {
            var result = new Result();
            var start = new byte[MagicPrefix.Length + 1 + 4];
            if (ReadFully(stream, start, 0, start.Length) < start.Length) throw new InvalidDataException("not a Nuclear Replay demo");
            for (int i = 0; i < MagicPrefix.Length; i++)
                if (start[i] != MagicPrefix[i]) throw new InvalidDataException("not a Nuclear Replay demo");
            int headerLength = BitConverter.ToInt32(start, MagicPrefix.Length + 1);
            if (headerLength < 0 || headerLength > MaxHeaderBytes) throw new InvalidDataException("damaged header");
            var header = new byte[headerLength];
            if (ReadFully(stream, header, 0, headerLength) < headerLength) throw new InvalidDataException("damaged header");
            result.HeaderJson = Encoding.UTF8.GetString(header);

            long length = stream.Length;
            long position = start.Length + (long)headerLength;   // file offset of the next record
            var buffer = new byte[Math.Max(chunkBytes, RecordHeaderBytes * 4)];
            long bufferStart = position;                          // file offset of buffer[0]
            int bufferLength = 0;

            while (position < length)
            {
                // The record header must be in the buffer; refill from it when it is not.
                if (position + RecordHeaderBytes > bufferStart + bufferLength)
                {
                    if (stream.Position != position) stream.Seek(position, SeekOrigin.Begin);
                    bufferStart = position;
                    bufferLength = ReadFully(stream, buffer, 0, buffer.Length);
                    if (bufferLength < RecordHeaderBytes) { result.Truncated = true; break; }
                }
                int at = (int)(position - bufferStart);
                byte kind = buffer[at];
                double time = BitConverter.ToDouble(buffer, at + 1);
                int payload = BitConverter.ToInt32(buffer, at + 18);
                long payloadStart = position + RecordHeaderBytes;
                if (payload < 0 || payloadStart + payload > length) { result.Truncated = true; break; }
                if (kind == KindAnnotation)
                {
                    string text;
                    if (payloadStart + payload <= bufferStart + bufferLength)
                        text = Encoding.UTF8.GetString(buffer, (int)(payloadStart - bufferStart), payload);
                    else
                    {
                        // Spills past the buffer: read it on its own; the next record refills the buffer.
                        var bytes = new byte[payload];
                        stream.Seek(payloadStart, SeekOrigin.Begin);
                        if (ReadFully(stream, bytes, 0, payload) < payload) { result.Truncated = true; break; }
                        bufferLength = 0;
                        text = Encoding.UTF8.GetString(bytes);
                    }
                    result.Annotations.Add(new KeyValuePair<double, string>(time, text));
                }
                if (result.First == null) result.First = time;
                result.Last = time;
                result.Records++;
                position = payloadStart + payload;
            }
            return result;
        }

        private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }
}
