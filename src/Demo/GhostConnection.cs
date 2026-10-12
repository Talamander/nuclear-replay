using System;
using System.Collections.Generic;
using Mirage.SocketLayer;

namespace NuclearReplay.Demo
{
    // A server-side connection with no socket behind it. Everything the server sends to it is
    // copied into the demo file, so the demo contains what a remote multiplayer client would receive.
    internal sealed class GhostConnection : IConnection
    {
        private readonly Action<byte, byte[], int, int> record;
        private readonly List<Action> pendingDelivery = new List<Action>();

        public GhostConnection(Action<byte, byte[], int, int> record) { this.record = record; }

        public IConnectionHandle Handle { get; } = new GhostHandle();
        public ConnectionState State { get; private set; } = ConnectionState.Connected;
        public long Messages { get; private set; }
        public long Bytes { get; private set; }

        public void Disconnect() => State = ConnectionState.Disconnected;
        public void Disconnect(DisconnectReason reason) => State = ConnectionState.Disconnected;

        public void SendReliable(byte[] message, int offset, int length) => Record(DemoFormat.ChannelReliable, message, offset, length);
        public void SendUnreliable(byte[] packet, int offset, int length) => Record(DemoFormat.ChannelUnreliable, packet, offset, length);

        public INotifyToken SendNotify(byte[] packet, int offset, int length)
        {
            Record(DemoFormat.ChannelNotify, packet, offset, length);
            var token = new GhostNotifyToken();
            pendingDelivery.Add(token.FireDelivered);
            return token;
        }

        public void SendNotify(byte[] packet, int offset, int length, INotifyCallBack callBacks)
        {
            Record(DemoFormat.ChannelNotify, packet, offset, length);
            if (callBacks != null) pendingDelivery.Add(callBacks.OnDelivered);
        }

        public void FlushBatch() { }

        // Notify senders expect an acknowledgement later, never inside their own send call.
        public void Tick()
        {
            if (pendingDelivery.Count == 0) return;
            var callbacks = pendingDelivery.ToArray();
            pendingDelivery.Clear();
            foreach (var callback in callbacks) callback();
        }

        private void Record(byte channel, byte[] data, int offset, int length)
        {
            if (State != ConnectionState.Connected) return;
            Messages++;
            Bytes += length;
            record(channel, data, offset, length);
        }

        public override string ToString() => "NuclearReplay demo ghost";

        private sealed class GhostHandle : IConnectionHandle
        {
            public bool IsStateful => false;
            public ISocketLayerConnection SocketLayerConnection { get; set; }
            public bool SupportsGracefulDisconnect => false;
            public void Disconnect(string gracefulDisconnectReason) { }
            public IConnectionHandle CreateCopy() => this;
            public override int GetHashCode() => base.GetHashCode();
            public override bool Equals(object obj) => ReferenceEquals(this, obj);
            public override string ToString() => "ghost";
        }

        private sealed class GhostNotifyToken : INotifyToken
        {
            public event Action Delivered;
            public event Action Lost;
            public void FireDelivered() => Delivered?.Invoke();
            internal void Unused() => Lost?.Invoke();
        }
    }
}
