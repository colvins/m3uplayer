using System.IO;
using System.Text.RegularExpressions;
namespace M3UPlayer;

public sealed record Channel(string Name, string Group, string Url, string? UserAgent, string? Referrer)
{
    public int SourceNumber { get; init; }
    public string DisplayName => SourceNumber > 0 ? $"{Name} · 源 {SourceNumber}" : Name;
    public override string ToString() => DisplayName;
}

public static class Playlist
{
    public static List<Channel> Parse(string text, Uri? source = null)
    {
        var result = new List<Channel>();
        string? name = null, agent = null, referer = null;
        var group = "未分组";
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var quoted = false; var comma = -1;
                for (var i = 0; i < line.Length; i++)
                {
                    if (line[i] == '"') quoted = !quoted;
                    if (line[i] == ',' && !quoted) { comma = i; break; }
                }
                name = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                var match = Regex.Match(line, "group-title=\"([^\"]*)\"", RegexOptions.IgnoreCase);
                group = match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : "未分组";
                agent = referer = null;
            }
            else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase)) group = line[8..].Trim();
            else if (line.StartsWith("#EXTVLCOPT:http-user-agent=", StringComparison.OrdinalIgnoreCase)) agent = line.Split('=', 2)[1];
            else if (line.StartsWith("#EXTVLCOPT:http-referrer=", StringComparison.OrdinalIgnoreCase)) referer = line.Split('=', 2)[1];
            else if (!line.StartsWith('#'))
            {
                if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) && source != null) Uri.TryCreate(source, line, out uri);
                if (uri != null && new[] { "http", "https", "rtsp", "rtmp", "udp", "rtp", "file" }.Contains(uri.Scheme))
                    result.Add(new Channel(string.IsNullOrWhiteSpace(name) ? $"频道 {result.Count + 1}" : name, group, uri.AbsoluteUri, agent, referer));
                name = agent = referer = null; group = "未分组";
            }
        }
        return result;
    }
}
