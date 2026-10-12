#nullable disable
using System;
using System.IO;
using System.Text;

namespace NuclearReplay.Preview
{
    // Wire format of the live preview channel between the replay plugin and nr
    // (docs/PRODUCT-CONTRACT.md section 4). Compiled into both the plugin (netstandard2.1, Unity's
    // mscorlib) and NuclearReplay.Core, so both sides frame messages with the same code.
    //
    // nr listens on 127.0.0.1; the plugin connects and sends a "hello" message with the token from
    // the preview job. Every message is:
    //
    //   kind            1 byte   1 = JSON message, 2 = frame (JSON header + JPEG payload)
    //   header length   4 bytes  unsigned, big-endian
    //   payload length  4 bytes  unsigned, big-endian (0 for JSON messages)
    //   header          UTF-8 JSON object
    //   payload         bytes
    public static class PreviewWire
    {
        public const byte Message = 1;
        public const byte Frame = 2;
        public const int MaxHeader = 1 << 20;
        public const int MaxPayload = 32 << 20;

        // Writes one message as a single buffer so concurrent writers that hold a lock never interleave.
        public static void Write(Stream stream, byte kind, string header, byte[] payload = null, int payloadCount = -1)
        {
            if (kind != Message && kind != Frame) throw new ArgumentOutOfRangeException(nameof(kind));
            var head = Encoding.UTF8.GetBytes(header ?? "{}");
            int count = payload == null ? 0 : payloadCount < 0 ? payload.Length : payloadCount;
            if (head.Length > MaxHeader) throw new ArgumentException("preview header too large");
            if (count > MaxPayload) throw new ArgumentException("preview payload too large");
            var buffer = new byte[9 + head.Length + count];
            buffer[0] = kind;
            PutUInt32(buffer, 1, (uint)head.Length);
            PutUInt32(buffer, 5, (uint)count);
            Buffer.BlockCopy(head, 0, buffer, 9, head.Length);
            if (count > 0) Buffer.BlockCopy(payload, 0, buffer, 9 + head.Length, count);
            stream.Write(buffer, 0, buffer.Length);
            stream.Flush();
        }

        // Reads one message. Returns false at a clean end of stream (the other side closed between
        // messages); throws InvalidDataException for anything that is not this protocol.
        public static bool TryRead(Stream stream, out byte kind, out string header, out byte[] payload)
        {
            kind = 0; header = null; payload = null;
            var prefix = new byte[9];
            int got = ReadFully(stream, prefix, 0, 9);
            if (got == 0) return false;
            if (got < 9) throw new EndOfStreamException("preview message cut off");
            kind = prefix[0];
            if (kind != Message && kind != Frame) throw new InvalidDataException("unknown preview message kind " + kind);
            uint headLength = GetUInt32(prefix, 1), payloadLength = GetUInt32(prefix, 5);
            if (headLength > MaxHeader || payloadLength > MaxPayload) throw new InvalidDataException("preview message too large");
            var head = new byte[headLength];
            if (ReadFully(stream, head, 0, head.Length) < head.Length) throw new EndOfStreamException("preview message cut off");
            header = Encoding.UTF8.GetString(head);
            payload = new byte[payloadLength];
            if (ReadFully(stream, payload, 0, payload.Length) < payload.Length) throw new EndOfStreamException("preview message cut off");
            return true;
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

        private static void PutUInt32(byte[] b, int at, uint value)
        {
            b[at] = (byte)(value >> 24); b[at + 1] = (byte)(value >> 16); b[at + 2] = (byte)(value >> 8); b[at + 3] = (byte)value;
        }

        private static uint GetUInt32(byte[] b, int at) =>
            ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

        // Minimal JSON string escaping for the plugin, which builds small headers by hand.
        public static string Quote(string text)
        {
            if (text == null) return "null";
            var sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
