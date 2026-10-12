using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NuclearReplay.Preview;

namespace NuclearReplay.Core.Preview;

/// <summary>
/// nr's end of the preview socket: listens on 127.0.0.1 (a free port), accepts the plugin and checks the token
/// it sends in its hello message. Connections with a wrong token are dropped.
/// </summary>
public sealed class PreviewListener : IDisposable
{
    private readonly TcpListener listener;
    public int Port { get; }
    public string Token { get; }

    public PreviewListener(string? token = null)
    {
        Token = token ?? PreviewJobWriter.NewToken();
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Waits for the plugin. Returns the connection after a valid hello.</summary>
    public async Task<PreviewConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            client.NoDelay = true;
            bool accepted = false;
            try
            {
                var stream = client.GetStream();
                using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var hello = await Task.Run(() =>
                {
                    stream.ReadTimeout = 10000;
                    if (!PreviewWire.TryRead(stream, out var kind, out var header, out _) || kind != PreviewWire.Message) return null;
                    return JsonNode.Parse(header) as JsonObject;
                }, helloTimeout.Token).ConfigureAwait(false);
                if (hello != null && (string?)hello["type"] == "hello" && (string?)hello["token"] == Token)
                {
                    stream.ReadTimeout = Timeout.Infinite;
                    accepted = true;
                    return new PreviewConnection(client, hello);
                }
                Log.Warn("Preview: rejected a connection without the right token");
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                Log.Warn("Preview: rejected a connection: " + e.Message);
            }
            finally
            {
                // Also when canceled while waiting for the hello.
                if (!accepted) client.Dispose();
            }
        }
    }

    public void Dispose() => listener.Stop();
}

/// <summary>An accepted plugin connection. Reads on a background thread and raises events; sends commands.</summary>
public sealed class PreviewConnection : IDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly object writeLock = new();
    private int closed;

    public JsonObject Hello { get; }
    public event Action<JsonObject>? MessageReceived;
    public event Action<PreviewFrame>? FrameReceived;
    /// <summary>Raised once when the connection ends; the argument is null for a clean close.</summary>
    public event Action<string?>? Closed;

    internal PreviewConnection(TcpClient client, JsonObject hello)
    {
        this.client = client;
        stream = client.GetStream();
        Hello = hello;
    }

    /// <summary>Starts the read loop. Call after the event handlers are attached.</summary>
    public void Start() => new Thread(ReadLoop) { IsBackground = true, Name = "nc-preview-read" }.Start();

    private void ReadLoop()
    {
        string? error = null;
        try
        {
            while (PreviewWire.TryRead(stream, out var kind, out var header, out var payload))
            {
                if (JsonNode.Parse(header) is not JsonObject o) continue;
                if (kind == PreviewWire.Frame) FrameReceived?.Invoke(PreviewFrame.FromMessage(o, payload));
                else MessageReceived?.Invoke(o);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException or System.Text.Json.JsonException or SocketException)
        {
            if (Volatile.Read(ref closed) == 0) error = e.Message;
        }
        Close(error);
    }

    public bool Send(JsonObject command)
    {
        if (Volatile.Read(ref closed) != 0) return false;
        try
        {
            lock (writeLock) PreviewWire.Write(stream, PreviewWire.Message, command.ToJsonString());
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            Close(e.Message);
            return false;
        }
    }

    private void Close(string? error)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        try { client.Dispose(); } catch (Exception) { }
        Closed?.Invoke(error);
    }

    public void Dispose() => Close(null);
}
