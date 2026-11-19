using M3UPlayer;
var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightM3UPlayer", "playlist.m3u");
foreach (var channel in Playlist.Parse(File.ReadAllText(file)).Where(c => args.Contains(c.Name) && c.Group != "香港旧"))
{
    using var player = new Mpv(0, headless: true);
    using var relay = channel.Name == "無綫新聞台" ? new HlsRelay(channel) : null;
    player.Play(relay?.Register(new Uri(channel.Url), ".m3u8") ?? channel.Url, channel);
    var passed = false;
    for (var i = 0; i < 25; i++)
    {
        await Task.Delay(1000);
        player.Poll(_ => {});
        if (player.Get("video-out-params/w") is string w && player.Get("audio-params/samplerate") != null && player.Get("playback-time") is string t && double.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 2)
        {
            Console.WriteLine($"PASS {channel.Name} {w}x{player.Get("video-out-params/h")} playback={t}s");
            passed = true;
            break;
        }
    }
    if (!passed) { Console.WriteLine($"FAIL {channel.Name}"); Environment.ExitCode = 1; }
}
