using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NuclearReplay.Cli;

/// <summary>
/// Serves preview frames on 127.0.0.1 for <c>nr preview --mjpeg</c>: "/" is a multipart/x-mixed-replace MJPEG stream
/// (opens in a browser, VLC, ffplay or OBS), "/latest.jpg" the newest frame. Local only, no authentication.
/// </summary>
public sealed class MjpegServer : IDisposable
{
    private const string Boundary = "ncframe";
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cts = new();
    private readonly object gate = new();
    private byte[]? latest;
    private long version;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/";

    public MjpegServer(int port)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public void Push(byte[] jpeg)
    {
        lock (gate)
        {
            latest = jpeg;
            version++;
            Monitor.PulseAll(gate);
        }
    }

    private async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = ReadRequestLine(stream);
                var path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                if (path.StartsWith("/latest.jpg", StringComparison.Ordinal))
                {
                    byte[]? frame;
                    lock (gate) frame = latest;
                    if (frame == null) { Write(stream, "HTTP/1.0 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n"); return; }
                    Write(stream, $"HTTP/1.0 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\nCache-Control: no-cache\r\n\r\n");
                    stream.Write(frame);
                    return;
                }
                Write(stream, $"HTTP/1.0 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary={Boundary}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
                long seen = -1;
                while (!cts.IsCancellationRequested)
                {
                    byte[]? frame;
                    lock (gate)
                    {
                        while (version == seen && !cts.IsCancellationRequested) Monitor.Wait(gate, 1000);
                        frame = latest;
                        seen = version;
                    }
                    if (frame == null) continue;
                    Write(stream, $"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
                    stream.Write(frame);
                    Write(stream, "\r\n");
                }
            }
            catch (Exception) { /* viewer went away */ }
        }
    }

    private static string ReadRequestLine(NetworkStream stream)
    {
        stream.ReadTimeout = 5000;
        var line = new StringBuilder();
        int previous = -1, b;
        // Read the request headers up to the blank line; only the first line matters.
        var text = new StringBuilder();
        while ((b = stream.ReadByte()) >= 0)
        {
            text.Append((char)b);
            if (previous == '\n' && b == '\r') { stream.ReadByte(); break; }
            if (previous == '\n' && b == '\n') break;
            previous = b;
            if (text.Length > 8192) break;
        }
        var first = text.ToString().Split('\n')[0].Trim();
        line.Append(first);
        return line.ToString();
    }

    private static void Write(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));

    public void Dispose()
    {
        cts.Cancel();
        lock (gate) Monitor.PulseAll(gate);
        listener.Stop();
    }
}
