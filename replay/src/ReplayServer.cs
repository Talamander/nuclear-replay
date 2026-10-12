using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Mirage;
using Mirage.Serialization;
using Mirage.SocketLayer;
using NuclearReplay.Demo;
using NuclearOption.Networking;
using NuclearOption.SceneLoading;

namespace NuclearReplay.Playback
{
    // Stands in for the original host. It never simulates anything: it sends the recorded server
    // messages to the real game client in recorded order and at recorded pace.
    internal sealed class ReplayServer : IDataHandler
    {
        private static readonly ushort LoadMapId = (ushort)MessagePacker.GetId<LoadMapMessage>();
        private static readonly ushort LoadWaitingId = (ushort)MessagePacker.GetId<LoadWaitingSceneMessage>();
        private static readonly ushort SceneReadyId = (ushort)MessagePacker.GetId<SceneReadyMessage>();
        private static readonly ushort PingId = (ushort)MessagePacker.GetId<NetworkPingMessage>();

        private readonly ManualLogSource log;
        private readonly List<DemoRecord> records;
        private readonly Peer peer;
        private readonly int maxUnreliable;
        private IConnection client;
        private int index;
        private bool waitingForScene;
        private double lastRecordTime, lastNetTime;
        private readonly HashSet<ushort> unknownClientMessages = new HashSet<ushort>();

        public double Clock { get; private set; }
        public double StartTime { get; }
        public double EndTime { get; }
        public float Speed { get; set; } = 1f;
        public bool Paused { get; set; }
        public bool Finished => index >= records.Count;
        public bool WaitingForScene => waitingForScene;
        public int Index => index;
        public int Count => records.Count;
        public int ScenesLoaded { get; private set; }
        public long MessagesSent { get; private set; }
        public long BytesSent { get; private set; }
        public long PromotedToReliable { get; private set; }
        public event Action<string> Annotation;

        // Recorded server NetworkTime at the current replay clock.
        public double ServerNetworkTime => lastNetTime + (Clock - lastRecordTime);

        public ReplayServer(ManualLogSource log, List<DemoRecord> records, LoopbackSocket socket, Config config)
        {
            this.log = log;
            this.records = records;
            peer = new Peer(socket, LoopbackSocketFactory.PacketSize, this, config);
            maxUnreliable = peer.GetMaxUnreliableMessageSize();
            peer.OnConnected += connection => { client = connection; log.LogInfo("Replay client connected"); };
            peer.OnDisconnected += (connection, reason) => { log.LogWarning("Replay client disconnected: " + reason); client = null; };
            peer.Bind(new LoopbackEndPoint());
            StartTime = records.Count > 0 ? records[0].Time : 0;
            EndTime = records.Count > 0 ? records[records.Count - 1].Time : 0;
            Clock = StartTime;
            lastRecordTime = StartTime;
            lastNetTime = records.Count > 0 ? records[0].NetTime : 0;
        }

        public void Receive() => peer.UpdateReceive();

        public void Advance(double unscaledDelta)
        {
            if (client == null || client.State != ConnectionState.Connected) return;
            if (!Paused && !waitingForScene)
                Clock += Math.Min(unscaledDelta, 0.25) * Speed;
            while (index < records.Count && !waitingForScene && records[index].Time <= Clock)
            {
                var record = records[index];
                if (record.Kind == DemoFormat.KindAnnotation)
                {
                    index++;
                    Annotation?.Invoke(record.Text);
                    continue;
                }
                if (!Send(record)) break;
                index++;
                lastRecordTime = record.Time;
                lastNetTime = record.NetTime;
                var id = record.MessageId;
                if (id == LoadMapId || id == LoadWaitingId)
                {
                    // The original host held spawns until this client finished loading; do the same.
                    waitingForScene = true;
                    Clock = record.Time;
                    log.LogInfo($"Replay waiting for client scene load at {record.Time - StartTime:F2}s");
                }
            }
        }

        public void Flush() => peer.UpdateSent();

        private bool Send(DemoRecord record)
        {
            try
            {
                var payload = record.Payload;
                if (record.Channel == DemoFormat.ChannelReliable || payload.Length > maxUnreliable)
                {
                    if (record.Channel != DemoFormat.ChannelReliable) PromotedToReliable++;
                    client.SendReliable(payload, 0, payload.Length);
                }
                else client.SendUnreliable(payload, 0, payload.Length);
                MessagesSent++;
                BytesSent += payload.Length;
                return true;
            }
            catch (BufferFullException)
            {
                // Retry next frame once the client has acknowledged more reliable packets.
                return false;
            }
            catch (Exception e)
            {
                log.LogError($"Replay send failed at record {index}: {e}");
                index++;
                return true;
            }
        }

        // Messages from the game client to the stand-in host.
        public void ReceiveMessage(IConnection connection, ArraySegment<byte> message)
        {
            if (message.Count < 2) return;
            ushort id = (ushort)(message.Array[message.Offset] | message.Array[message.Offset + 1] << 8);
            if (id == SceneReadyId)
            {
                if (waitingForScene) log.LogInfo("Replay client finished loading scene; resuming");
                waitingForScene = false;
                ScenesLoaded++;
            }
            else if (id == PingId)
            {
                var bytes = new byte[message.Count];
                Buffer.BlockCopy(message.Array, message.Offset, bytes, 0, message.Count);
                var ping = MessagePacker.Unpack<NetworkPingMessage>(bytes, null);
                var pong = MessagePacker.Pack(new NetworkPongMessage { ClientTime = ping.ClientTime, ServerTime = ServerNetworkTime });
                connection.SendUnreliable(pong, 0, pong.Length);
            }
            else if (unknownClientMessages.Add(id))
            {
                string name = MessagePacker.MessageTypes.TryGetValue(id, out var type) ? type.FullName : "unknown";
                log.LogInfo($"Replay ignores client message {id} ({name})");
            }
        }

        public void Close()
        {
            try { peer.Close(); } catch (Exception e) { log.LogWarning("Replay peer close: " + e.Message); }
        }
    }
}
