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
        TagLib.File file;
        try { file = TagLib.File.Create(path); }
        catch (TagLib.UnsupportedFormatException) { return ReadFallback(info); }
        using (file)
        {
            return ReadTagged(path, info, file);
        }
    }

    private static Track ReadTagged(string path, FileInfo info, TagLib.File file)
    {
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
            ArtistSort = First(tag.PerformersSort) ?? "",
            AlbumArtist = albumArtist,
            AlbumArtistSort = First(tag.AlbumArtistsSort) ?? "",
            Album = string.IsNullOrWhiteSpace(tag.Album) ? "Unknown album" : tag.Album.Trim(),
            AlbumSort = tag.AlbumSort?.Trim() ?? "",
            Genre = Join(tag.Genres),
            Comment = ReadComment(file, tag),
            Grouping = tag.Grouping?.Trim() ?? "",
            Composer = Join(tag.Composers),
            Conductor = tag.Conductor?.Trim() ?? "",
            ReleaseType = ReadTagValue(file, "RELEASETYPE") ?? ReadTagValue(file, "RELEASE_TYPE") ?? "",
            IsCompilation = IsCompilation(tag),
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
            Lyrics = sidecarLyrics ?? tag.Lyrics ?? "",
            Chapters = ChapterMetadataReader.Read(
                file,
                path,
                properties.Duration)
        };
    }

    private static Track ReadFallback(FileInfo info) => new()
    {
        Path = info.FullName,
        Title = Path.GetFileNameWithoutExtension(info.Name),
        Artist = "Unknown artist",
        Album = "Unknown album",
        Codec = Path.GetExtension(info.Name).TrimStart('.').ToUpperInvariant(),
        FileModifiedAt = info.LastWriteTimeUtc,
        FileSize = info.Length
    };

    private static string Join(IEnumerable<string> values) => string.Join("; ", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
    private static string? First(IEnumerable<string> values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static bool IsCompilation(TagLib.Tag tag) => tag switch
    {
        TagLib.Mpeg4.AppleTag apple => apple.IsCompilation,
        TagLib.Id3v2.Tag id3 => id3.IsCompilation,
        _ => false
    };

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

    private static string ReadComment(TagLib.File file, TagLib.Tag tag)
    {
        var value = tag.Comment;
        if (string.IsNullOrWhiteSpace(value)
            && file.GetTag(TagLib.TagTypes.Xiph, false)
                is TagLib.Ogg.XiphComment xiph)
            value = xiph.GetFirstField("COMMENT")
                    ?? xiph.GetFirstField("DESCRIPTION");
        return value?.Trim() ?? "";
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
            var flacOffset = FindFlacOffset(stream);
            if (flacOffset < 0) return 0;
            stream.Position = flacOffset;
            Span<byte> header = stackalloc byte[22];
            if (stream.Read(header) != header.Length || !header[..4].SequenceEqual("fLaC"u8) || (header[4] & 0x7F) != 0) return 0;
            return (((header[20] & 0x01) << 4) | (header[21] >> 4)) + 1;
        }
        catch { return 0; }
    }

    private static long FindFlacOffset(Stream stream)
    {
        Span<byte> marker = stackalloc byte[4];
        stream.Position = 0;
        if (stream.Read(marker) == marker.Length
            && marker.SequenceEqual("fLaC"u8))
            return 0;

        stream.Position = 0;
        Span<byte> id3 = stackalloc byte[10];
        if (stream.Read(id3) != id3.Length
            || !id3[..3].SequenceEqual("ID3"u8))
            return -1;

        var tagSize = (id3[6] << 21)
            | (id3[7] << 14)
            | (id3[8] << 7)
            | id3[9];
        var footerSize = (id3[5] & 0x10) != 0 ? 10 : 0;
        var offset = 10L + tagSize + footerSize;
        if (offset > stream.Length - marker.Length) return -1;
        stream.Position = offset;
        return stream.Read(marker) == marker.Length
            && marker.SequenceEqual("fLaC"u8)
            ? offset
            : -1;
    }
}
