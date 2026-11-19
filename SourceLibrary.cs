using System.IO;
using System.Text.Json;

namespace M3UPlayer;

internal sealed record PlaylistSource(Guid Id, string Name, string? Location, List<Channel> Channels)
{
    public override string ToString() => Name;
}

internal sealed record SourceLibrary(List<PlaylistSource> Sources, Guid? ActiveId)
{
    public static SourceLibrary Load(string directory, string? legacyLocation)
    {
        var path = Path.Combine(directory, "sources.json");
        if (File.Exists(path))
            return JsonSerializer.Deserialize<SourceLibrary>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid source library.");

        var legacyPath = Path.Combine(directory, "playlist.m3u");
        Uri.TryCreate(legacyLocation, UriKind.Absolute, out var baseUri);
        var channels = File.Exists(legacyPath) ? Playlist.Parse(File.ReadAllText(legacyPath), baseUri) : [];
        if (channels.Count == 0 && string.IsNullOrWhiteSpace(legacyLocation)) return new([], null);
        var source = new PlaylistSource(Guid.NewGuid(), "原有播放列表", legacyLocation, channels);
        var library = new SourceLibrary([source], source.Id);
        library.Save(directory);
        return library;
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sources.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
