namespace Dextromethorphan.Core.Models;

public sealed record Track
{
    public long Id { get; init; }
    public required string Path { get; init; }
    public string? MediaPath { get; init; }
    public string? CueSheetPath { get; init; }
    public TimeSpan SegmentStart { get; init; }
    public TimeSpan? SegmentEnd { get; init; }
    public required string Title { get; init; }
    public string Artist { get; init; } = "Unknown artist";
    /// <summary>Optional sort key supplied by the file's artist-sort tag.</summary>
    public string ArtistSort { get; init; } = "";
    public string AlbumArtist { get; init; } = "";
    /// <summary>Optional sort key supplied by the file's album-artist-sort tag.</summary>
    public string AlbumArtistSort { get; init; } = "";
    public string Album { get; init; } = "Unknown album";
    /// <summary>Optional sort key supplied by the file's album-sort tag.</summary>
    public string AlbumSort { get; init; } = "";
    public string Genre { get; init; } = "";
    public string Comment { get; init; } = "";
    public string Grouping { get; init; } = "";
    public string Composer { get; init; } = "";
    public string Conductor { get; init; } = "";
    public string ReleaseType { get; init; } = "";
    public bool IsCompilation { get; init; }
    public int Year { get; init; }
    public int TrackNumber { get; init; }
    public int DiscNumber { get; init; }
    public TimeSpan Duration { get; init; }
    public int Bitrate { get; init; }
    public int SampleRate { get; init; }
    public int BitsPerSample { get; init; }
    public int Channels { get; init; }
    public string Codec { get; init; } = "";
    public double? ReplayGainTrackDb { get; init; }
    public double? ReplayGainAlbumDb { get; init; }
    public double? ReplayPeak { get; init; }
    public int Rating { get; init; }
    public bool IsLoved { get; init; }
    public long PlayCount { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public DateTimeOffset FileModifiedAt { get; init; }
    public long FileSize { get; init; }
    public byte[]? Artwork { get; init; }
    public string? ArtworkPath { get; init; }
    public string Lyrics { get; init; } = "";
    public IReadOnlyList<AudioChapter> Chapters { get; init; } = [];
    public bool IsMissing { get; init; }

    public string EffectiveMediaPath =>
        string.IsNullOrWhiteSpace(MediaPath) ? Path : MediaPath;
    public bool IsCueTrack =>
        !string.IsNullOrWhiteSpace(CueSheetPath);

    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown artist" : Artist;
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "Unknown album" : Album;
    public string DurationText => Duration.ToString(Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
    public string DiscTrackText => DiscNumber > 1 ? $"{DiscNumber}-{TrackNumber}" : TrackNumber > 0 ? TrackNumber.ToString() : "—";
    public string ReplayGainText => ReplayGainAlbumDb is { } album ? $"Album {album:+0.0;-0.0;0.0} dB" : ReplayGainTrackDb is { } track ? $"Track {track:+0.0;-0.0;0.0} dB" : "";
    public string QualityText
    {
        get
        {
            if (SampleRate <= 0) return Codec.ToUpperInvariant();
            var sampleRate = (SampleRate / 1000d).ToString(
                "0.#",
                System.Globalization.CultureInfo.InvariantCulture);
            var quality = $"{Codec.ToUpperInvariant()} · {sampleRate} kHz";
            return BitsPerSample > 0 ? $"{quality} · {BitsPerSample}-bit" : quality;
        }
    }

    public string BitrateText => Bitrate > 0
        ? $"{Bitrate.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} kbps"
        : "Unknown";
    public string ChannelText => Channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        > 2 => $"{Channels} channels",
        _ => "Unknown"
    };
    public string FileSizeText => FileSize switch
    {
        >= 1_073_741_824 => $"{FileSize / 1_073_741_824d:0.##} GB",
        >= 1_048_576 => $"{FileSize / 1_048_576d:0.#} MB",
        >= 1_024 => $"{FileSize / 1_024d:0.#} KB",
        > 0 => $"{FileSize:N0} bytes",
        _ => "Unknown"
    };
    public string YearText => Year > 0
        ? Year.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "Unknown";
    public string GenreText => string.IsNullOrWhiteSpace(Genre) ? "Unknown" : Genre;
    public string ReplayGainDisplayText => string.IsNullOrWhiteSpace(ReplayGainText)
        ? "Not tagged"
        : ReplayGainText;
    public string PlayCountText => PlayCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
    public string MetadataAutomationText =>
        $"{Title} by {DisplayArtist}, album {DisplayAlbum}, {QualityText}, " +
        $"{BitrateText}, {ChannelText}, duration {DurationText}, source {Path}";
}
