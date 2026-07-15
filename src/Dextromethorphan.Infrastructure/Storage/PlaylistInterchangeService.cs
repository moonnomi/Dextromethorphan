using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Storage;

public sealed class PlaylistInterchangeService : IPlaylistInterchangeService
{
    private static readonly XNamespace Xspf = "http://xspf.org/ns/0/";

    public async Task<ImportedPlaylist> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".m3u" or ".m3u8" => await ImportM3uAsync(path, cancellationToken),
            ".pls" => await ImportPlsAsync(path, cancellationToken),
            ".xspf" => await ImportXspfAsync(path, cancellationToken),
            _ => throw new NotSupportedException("Supported playlist formats are M3U8, PLS, and XSPF.")
        };
    }

    public async Task ExportAsync(string path, string name, IReadOnlyList<Track> tracks, PlaylistFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var contents = format switch
        {
            PlaylistFormat.M3U8 => ExportM3u(tracks),
            PlaylistFormat.PLS => ExportPls(tracks),
            PlaylistFormat.XSPF => ExportXspf(name, tracks),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false), cancellationToken);
    }

    private static async Task<ImportedPlaylist> ImportM3uAsync(string path, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        return new ImportedPlaylist
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Locations = lines.Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).Select(x => ResolveLocation(path, x)).ToArray()
        };
    }

    private static async Task<ImportedPlaylist> ImportPlsAsync(string path, CancellationToken cancellationToken)
    {
        var entries = new SortedDictionary<int, string>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            var separator = line.IndexOf('=');
            if (separator <= 4 || !line[..4].Equals("File", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(line.AsSpan(4, separator - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                entries[index] = ResolveLocation(path, line[(separator + 1)..].Trim());
        }
        return new ImportedPlaylist { Name = Path.GetFileNameWithoutExtension(path), Locations = entries.Values.ToArray() };
    }

    private static async Task<ImportedPlaylist> ImportXspfAsync(string path, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        await using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        var root = document.Root ?? throw new InvalidDataException("The XSPF document has no root element.");
        var title = root.Element(Xspf + "title")?.Value.Trim();
        var locations = root.Element(Xspf + "trackList")?.Elements(Xspf + "track")
            .Select(x => x.Element(Xspf + "location")?.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => ResolveLocation(path, x!))
            .ToArray() ?? [];
        return new ImportedPlaylist { Name = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title, Locations = locations };
    }

    private static string ExportM3u(IReadOnlyList<Track> tracks)
    {
        var builder = new StringBuilder("#EXTM3U\n");
        foreach (var track in tracks)
        {
            builder.Append("#EXTINF:").Append((long)track.Duration.TotalSeconds).Append(',').Append(LineSafe(track.DisplayArtist)).Append(" - ").AppendLine(LineSafe(track.Title));
            builder.AppendLine(track.Path);
        }
        return builder.ToString();
    }

    private static string ExportPls(IReadOnlyList<Track> tracks)
    {
        var builder = new StringBuilder("[playlist]\n");
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var number = i + 1;
            builder.Append("File").Append(number).Append('=').AppendLine(track.Path);
            builder.Append("Title").Append(number).Append('=').Append(LineSafe(track.DisplayArtist)).Append(" - ").AppendLine(LineSafe(track.Title));
            builder.Append("Length").Append(number).Append('=').AppendLine(((long)track.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }
        builder.Append("NumberOfEntries=").AppendLine(tracks.Count.ToString(CultureInfo.InvariantCulture)).AppendLine("Version=2");
        return builder.ToString();
    }

    private static string ExportXspf(string name, IReadOnlyList<Track> tracks)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Xspf + "playlist",
                new XAttribute("version", "1"),
                new XElement(Xspf + "title", name),
                new XElement(Xspf + "trackList", tracks.Select(track =>
                    new XElement(Xspf + "track",
                        new XElement(Xspf + "location", new Uri(Path.GetFullPath(track.Path)).AbsoluteUri),
                        new XElement(Xspf + "creator", track.DisplayArtist),
                        new XElement(Xspf + "title", track.Title),
                        new XElement(Xspf + "album", track.DisplayAlbum),
                        new XElement(Xspf + "duration", (long)track.Duration.TotalMilliseconds))))));
        return document.Declaration + Environment.NewLine + document.ToString(SaveOptions.DisableFormatting);
    }

    private static string ResolveLocation(string playlistPath, string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri)) return uri.IsFile ? Path.GetFullPath(uri.LocalPath) : uri.AbsoluteUri;
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(playlistPath)!, location.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string LineSafe(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
