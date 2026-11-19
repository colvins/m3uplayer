using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace M3UPlayer;

// App-private loopback relay: normalize nonstandard HLS and keep HTTP redirects,
// relative URLs and headers consistent for all nested playlists and segments.
public sealed class HlsRelay : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, Uri> resources = new();
    private readonly ConcurrentDictionary<string, string> ids = new();
    private readonly HttpClient client;
    private readonly string root;
    private int sequence;
    public event Action<string>? Notice;
    public HlsRelay(Channel channel)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var origin = $"http://127.0.0.1:{port}/";
        root = origin + Guid.NewGuid().ToString("N") + "/";
        listener.Prefixes.Add(origin); listener.Start();
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(8), ConnectCallback = Connect };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", channel.UserAgent ?? "VLC/3.0.24 LibVLC/3.0.24");
        if (channel.Referrer != null) client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", channel.Referrer);
        _ = Accept();
    }
    public string Register(Uri uri, string extension = ".ts")
    {
        if (uri.Scheme is not ("http" or "https")) throw new InvalidDataException("Unsupported nested protocol");
        var id = ids.GetOrAdd(uri.AbsoluteUri, _ => Interlocked.Increment(ref sequence).ToString(CultureInfo.InvariantCulture) + extension);
        resources[id] = uri;
        return root + id;
    }
    private async Task Accept()
    {
        try { while (!lifetime.IsCancellationRequested) { var ctx = await listener.GetContextAsync(); _ = Serve(ctx); } }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
    }
    private async Task Serve(HttpListenerContext context)
    {
        try
        {
            var address = context.Request.Url?.AbsoluteUri ?? "";
            if (!address.StartsWith(root, StringComparison.Ordinal) || !resources.TryGetValue(address[root.Length..], out var uri)) { context.Response.StatusCode = 404; return; }
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            if (!response.IsSuccessStatusCode)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                Notice?.Invoke($"频道服务器返回 HTTP {(int)response.StatusCode}。");
                return;
            }
            using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
            var prefix = new byte[512]; var size = await stream.ReadAsync(prefix, lifetime.Token);
            var first = Encoding.UTF8.GetString(prefix, 0, size).TrimStart('\uFEFF', '\r', '\n', ' ');
            if (first.StartsWith("#EXTM3U"))
            {
                using var buffer = new MemoryStream(); buffer.Write(prefix, 0, size);
                await stream.CopyToAsync(buffer, lifetime.Token);
                var text = Encoding.UTF8.GetString(buffer.ToArray());
                var normalized = Normalize(text, response.RequestMessage!.RequestUri!, Register);
                var bytes = Encoding.UTF8.GetBytes(normalized);
                context.Response.ContentType = "application/vnd.apple.mpegurl";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, lifetime.Token);
            }
            else
            {
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                if (response.Content.Headers.ContentLength is long len) context.Response.ContentLength64 = len;
                else context.Response.SendChunked = true;
                await context.Response.OutputStream.WriteAsync(prefix.AsMemory(0, size), lifetime.Token);
                await stream.CopyToAsync(context.Response.OutputStream, lifetime.Token);
            }
        }
        catch (Exception)
        {
            try { context.Response.StatusCode = 502; } catch { }
            if (!lifetime.IsCancellationRequested) Notice?.Invoke("源连接失败或超时，请重试或切换频道。");
        }
        finally { try { context.Response.Close(); } catch { } }
    }
    public static string Normalize(string text, Uri origin, Func<Uri, string, string> map)
    {
        var master = text.Contains("#EXT-X-STREAM-INF:");
        var durations = Regex.Matches(text, @"#EXTINF:([\d.]+)").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        var max = Math.Ceiling(durations.DefaultIfEmpty(1).Max());
        var lines = text.TrimStart('\uFEFF').Split('\n').Select(raw =>
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-TARGETDURATION:") && double.TryParse(line.Split(':')[1], out var target) && target < max)
                return "#EXT-X-TARGETDURATION:" + max.ToString(CultureInfo.InvariantCulture);
            if (line.Length > 0 && !line.StartsWith('#')) return map(new Uri(origin, line), master ? ".m3u8" : ".ts");
            return Regex.Replace(line, "URI=\"([^\"]+)\"", m => "URI=\"" + map(new Uri(origin, m.Groups[1].Value), line.StartsWith("#EXT-X-KEY") ? ".key" : line.StartsWith("#EXT-X-MAP") ? ".mp4" : ".m3u8") + "\"");
        });
        return string.Join("\n", lines);
    }
    private static readonly HttpClient Dns = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly ConcurrentDictionary<string, (DateTime Until, IPAddress[] Ips)> DnsCache = new();
    private async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var host = context.DnsEndPoint.Host;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(context.DnsEndPoint, timeout.Token); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is SocketException or OperationCanceledException)
        {
            // Resolve only this failing media host; never change system DNS settings.
            if (!DnsCache.TryGetValue(host, out var cached) || cached.Until < DateTime.UtcNow)
            {
                var json = await Dns.GetStringAsync("https://dns.google/resolve?type=A&name=" + Uri.EscapeDataString(host), token);
                using var doc = JsonDocument.Parse(json);
                var ips = doc.RootElement.TryGetProperty("Answer", out var answers)
                    ? answers.EnumerateArray().Where(a => a.GetProperty("type").GetInt32() == 1).Select(a => IPAddress.Parse(a.GetProperty("data").GetString()!)).ToArray() : [];
                cached = (DateTime.UtcNow.AddMinutes(3), ips); DnsCache[host] = cached;
            }
            foreach (var ip in cached.Ips)
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await socket.ConnectAsync(ip, context.DnsEndPoint.Port, timeout.Token);
                    Notice?.Invoke("已使用备用 DNS 连接频道源。");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); }
            }
            throw new HttpRequestException("Media host unavailable");
        }
    }
    public void Dispose() { lifetime.Cancel(); listener.Close(); client.Dispose(); }
}
