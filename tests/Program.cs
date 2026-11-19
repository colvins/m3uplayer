using M3UPlayer;
var text = "\uFEFF#EXTM3U\r\n#EXTINF:-1 group-title=\"新闻,高清\",频道,一\n#EXTVLCOPT:http-user-agent=okhttp/3.8\n#EXTVLCOPT:http-referrer=https://example.org/\n/live/a\n#EXTINF:-1,频道二\nhttps://example.org/play.php?id=2\n#EXTINF:-1,无效项\njavascript:alert(1)";
var items = Playlist.Parse(text, new Uri("https://example.org/list/main.m3u"));
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
Check(items.Count == 2, "valid channels / unsafe scheme excluded");
Check(items[0].Name == "频道,一" && items[0].Group == "新闻,高清", "quoted attribute commas / unicode");
Check(items[0].Url == "https://example.org/live/a", "relative URL resolution");
Check(items[0].UserAgent == "okhttp/3.8" && items[0].Referrer == "https://example.org/", "per-channel headers");
Check(items[1].UserAgent == null && items[1].Referrer == null && items[1].Group == "未分组", "headers do not leak to next channel");
Check(Playlist.Parse("#EXTM3U\n# empty").Count == 0, "empty playlist");
Check(Playlist.Parse("https://example.org/live")[0].Name == "频道 1", "plain playlist");
var targets = new List<(Uri, string)>();
var normalized = HlsRelay.Normalize("#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:9\n#EXT-X-KEY:METHOD=AES-128,URI=\"../key?k=1\"\n#EXTINF:9.03,\n../segment?id=2\n", new Uri("https://example.org/redirected/live/index"), (u, ext) => { targets.Add((u, ext)); return "http://127.0.0.1/token/file" + ext; });
Check(normalized.Contains("#EXT-X-TARGETDURATION:10"), "HLS target duration covers longest segment");
Check(targets[0] == (new Uri("https://example.org/redirected/key?k=1"), ".key"), "relative HLS key URI preserves query");
Check(targets[1] == (new Uri("https://example.org/redirected/segment?id=2"), ".ts"), "extensionless HLS segment uses redirected base");
Check(normalized.Contains("#EXT-X-MEDIA-SEQUENCE:9"), "HLS sequence preserved");
targets.Clear();
HlsRelay.Normalize("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=40000000\nvariant\n", new Uri("https://example.org/live/"), (u, ext) => { targets.Add((u, ext)); return u.ToString(); });
Check(targets[0].Item2 == ".m3u8", "nested master playlist recognized");

var libraryDir = Path.Combine(Path.GetTempPath(), "light-m3u-library-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(libraryDir);
try
{
    const string oldAddress = "https://example.org/previous.m3u";
    var legacy = Path.Combine(libraryDir, "playlist.m3u");
    File.WriteAllText(legacy, "#EXTM3U\n#EXTINF:-1,旧频道\nhttps://example.org/previous.ts");
    var migrated = SourceLibrary.Load(libraryDir, oldAddress);
    var original = migrated.Sources.Single();
    Check(original.Location == oldAddress && original.Channels.Single().Name == "旧频道", "legacy URL and cached channels migrated");
    var added = new PlaylistSource(Guid.NewGuid(), "第二个来源", "https://example.net/next.m3u",
        Playlist.Parse("#EXTM3U\n#EXTINF:-1,新频道\nhttps://example.net/next.ts"));
    new SourceLibrary([original, added], added.Id).Save(libraryDir);
    var restored = SourceLibrary.Load(libraryDir, oldAddress);
    Check(restored.Sources.Count == 2 && restored.ActiveId == added.Id, "two sources and selected source survive restart");
    Check(restored.Sources[0].Channels.Single().Name == "旧频道" && File.Exists(legacy), "new source leaves previous source and legacy cache intact");
    File.Delete(legacy);
    File.Delete(Path.Combine(libraryDir, "sources.json"));
    Check(SourceLibrary.Load(libraryDir, oldAddress).Sources.Single().Location == oldAddress, "legacy address survives missing cache");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(libraryDir)) File.Delete(file);
    Directory.Delete(libraryDir);
}
