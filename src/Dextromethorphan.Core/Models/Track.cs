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
    public string AlbumArtist { get; init; } = "";
    public string Album { get; init; } = "Unknown album";
    public string Genre { get; init; } = "";
    public string Comment { get; init; } = "";
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
    public string QualityText => SampleRate > 0 ? $"{Codec.ToUpperInvariant()} · {SampleRate / 1000d:0.#} kHz · {BitsPerSample}-bit" : Codec.ToUpperInvariant();
}
