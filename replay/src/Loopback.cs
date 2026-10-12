using System;
using System.Collections.Generic;
using Mirage.SocketLayer;

namespace NuclearReplay.Playback
{
    // In-memory datagram pair. The game's client peer and the replay server peer talk through it,
    // so replay never opens a network port and is not limited by a UDP MTU.
    internal sealed class LoopbackSocket : ISocket
    {
        private readonly Queue<byte[]> inbox = new Queue<byte[]>();
        private readonly LoopbackHandle remote;
        private readonly string name;
        internal LoopbackSocket Other;
        private bool closed;
        public long PacketsSent { get; private set; }
        public long BytesSent { get; private set; }

        public LoopbackSocket(string name, string remoteName)
        {
            this.name = name;
            remote = new LoopbackHandle(remoteName);
        }

        public void Bind(IBindEndPoint endPoint) { closed = false; }
        public IConnectionHandle Connect(IConnectEndPoint endPoint) { closed = false; return remote; }
        public void Close() { closed = true; inbox.Clear(); }
        public void SetTickEvents(int maxPacketSize, OnData onData, OnDisconnect onDisconnect) { }
        public void Tick() { }
        public void Flush() { }
        public bool Poll() => !closed && inbox.Count > 0;

        public int Receive(Span<byte> outBuffer, out IConnectionHandle handle)
        {
            var packet = inbox.Dequeue();
            packet.AsSpan().CopyTo(outBuffer);
            handle = remote;
            return packet.Length;
        }

        public void Send(IConnectionHandle handle, ReadOnlySpan<byte> packet)
        {
            if (closed || Other == null || Other.closed) return;
            PacketsSent++;
            BytesSent += packet.Length;
            Other.inbox.Enqueue(packet.ToArray());
        }

        public override string ToString() => name;
    }

    internal sealed class LoopbackHandle : IConnectionHandle
    {
        private readonly string name;
        public LoopbackHandle(string name) { this.name = name; }
        public bool IsStateful => false;
        public ISocketLayerConnection SocketLayerConnection { get; set; }
        public bool SupportsGracefulDisconnect => false;
        public void Disconnect(string gracefulDisconnectReason) { }
        public IConnectionHandle CreateCopy() => this;
        public override int GetHashCode() => base.GetHashCode();
        public override bool Equals(object obj) => ReferenceEquals(this, obj);
        public override string ToString() => name;
    }

    internal sealed class LoopbackEndPoint : IBindEndPoint, IConnectEndPoint
    {
        public override string ToString() => "nuclear-replay-replay";
    }

    internal sealed class LoopbackSocketFactory : SocketFactory
    {
        public const int PacketSize = 16384;
        internal LoopbackSocket ServerSocket { get; private set; }
        internal LoopbackSocket ClientSocket { get; private set; }

        public void CreatePair()
        {
            ServerSocket = new LoopbackSocket("replay-server", "replay-client");
            ClientSocket = new LoopbackSocket("replay-client", "replay-server");
            ServerSocket.Other = ClientSocket;
            ClientSocket.Other = ServerSocket;
        }

        public override int MaxPacketSize => PacketSize;
        public override bool IsSupported => true;
        public override ISocket CreateServerSocket() => ServerSocket;
        public override IBindEndPoint GetBindEndPoint() => new LoopbackEndPoint();
        public override ISocket CreateClientSocket() => ClientSocket;
        public override IConnectEndPoint GetConnectEndPoint(string address = null, ushort? port = null) => new LoopbackEndPoint();
    }
}
