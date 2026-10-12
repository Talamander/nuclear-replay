using System.Net;

namespace NuclearReplay.Core.Tests;

/// <summary>
/// Stands in for the network: static files such as the ffmpeg zip. Tests never reach the real internet.
/// </summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public List<(string Url, Dictionary<string, string> Form)> Requests { get; } = new();

    /// <summary>Every request fails like a missing connection.</summary>
    public bool Offline { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var form = new Dictionary<string, string>();
        if (request.Content != null)
        {
            var text = await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                form[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
            }
        }
        Requests.Add((url, form));
        if (Offline) throw new HttpRequestException("No such host is known.");
        if (Files.TryGetValue(url, out var bytes))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") };
    }
}
