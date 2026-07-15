using System.Globalization;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Library;

public sealed class TagLibMetadataReader : ITrackMetadataReader
{
    public Task<Track> ReadAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() => Read(path), cancellationToken);

    private static Track Read(string path)
    {
        var info = new FileInfo(path);
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        var properties = file.Properties;
        var albumArtist = First(tag.AlbumArtists) ?? First(tag.Performers) ?? "";
        var artist = Join(tag.Performers);
        var title = string.IsNullOrWhiteSpace(tag.Title) ? Path.GetFileNameWithoutExtension(path) : tag.Title.Trim();
        var sidecarLyrics = FindSidecarLyrics(path);

        return new Track
        {
            Path = info.FullName,
            Title = title,
            Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown artist" : artist,
            AlbumArtist = albumArtist,
            Album = string.IsNullOrWhiteSpace(tag.Album) ? "Unknown album" : tag.Album.Trim(),
            Genre = Join(tag.Genres),
            Comment = tag.Comment?.Trim() ?? "",
            Year = checked((int)tag.Year),
            TrackNumber = checked((int)tag.Track),
            DiscNumber = checked((int)tag.Disc),
            Duration = properties.Duration,
            Bitrate = properties.AudioBitrate,
            SampleRate = properties.AudioSampleRate,
            BitsPerSample = GuessBitsPerSample(path),
            Channels = properties.AudioChannels,
            Codec = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            ReplayGainTrackDb = ReadReplayGain(file, "REPLAYGAIN_TRACK_GAIN") ?? ReadR128Gain(file, "R128_TRACK_GAIN"),
            ReplayGainAlbumDb = ReadReplayGain(file, "REPLAYGAIN_ALBUM_GAIN") ?? ReadR128Gain(file, "R128_ALBUM_GAIN"),
            ReplayPeak = ReadReplayGain(file, "REPLAYGAIN_TRACK_PEAK"),
            FileModifiedAt = info.LastWriteTimeUtc,
            FileSize = info.Length,
            Artwork = tag.Pictures.FirstOrDefault()?.Data.Data,
            Lyrics = sidecarLyrics ?? tag.Lyrics ?? ""
        };
    }

    private static string Join(IEnumerable<string> values) => string.Join("; ", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
    private static string? First(IEnumerable<string> values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string? FindSidecarLyrics(string path)
    {
        var lrc = Path.ChangeExtension(path, ".lrc");
        var txt = Path.ChangeExtension(path, ".txt");
        try
        {
            if (File.Exists(lrc)) return File.ReadAllText(lrc);
            if (File.Exists(txt)) return File.ReadAllText(txt);
        }
        catch (IOException) { }
        return null;
    }

    private static int GuessBitsPerSample(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".dsf" or ".dff" => 1,
        ".flac" => ReadFlacBitsPerSample(path),
        ".wav" or ".wave" => ReadWaveBitsPerSample(path),
        ".mp3" or ".aac" or ".m4a" or ".ogg" or ".opus" or ".wma" => 0,
        _ => 16
    };

    private static double? ReadReplayGain(TagLib.File file, string key)
    {
        var value = ReadTagValue(file, key);
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static double? ReadR128Gain(TagLib.File file, string key)
    {
        var value = ReadTagValue(file, key);
        return short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fixedPoint) ? fixedPoint / 256d : null;
    }

    private static string? ReadTagValue(TagLib.File file, string key)
    {
        string? value = null;
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph) value = xiph.GetFirstField(key);
        if (string.IsNullOrWhiteSpace(value) && file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
            value = id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>().FirstOrDefault(frame => frame.Description.Equals(key, StringComparison.OrdinalIgnoreCase))?.Text.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value) && file.GetTag(TagLib.TagTypes.Ape, false) is TagLib.Ape.Tag ape)
            value = ape.GetItem(key)?.ToString();
        return value;
    }

    private static int ReadWaveBitsPerSample(string path)
    {
        try
        {
            using var reader = new NAudio.Wave.WaveFileReader(path);
            return reader.WaveFormat.BitsPerSample;
        }
        catch { return 16; }
    }

    private static int ReadFlacBitsPerSample(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[22];
            if (stream.Read(header) != header.Length || !header[..4].SequenceEqual("fLaC"u8) || (header[4] & 0x7F) != 0) return 0;
            return (((header[20] & 0x01) << 4) | (header[21] >> 4)) + 1;
        }
        catch { return 0; }
    }
}
