namespace Dextromethorphan.Core.Models;

[Flags]
public enum MetadataEditFields
{
    None = 0,
    Title = 1 << 0,
    Artists = 1 << 1,
    AlbumArtists = 1 << 2,
    Album = 1 << 3,
    Genres = 1 << 4,
    Comment = 1 << 5,
    Year = 1 << 6,
    TrackNumber = 1 << 7,
    DiscNumber = 1 << 8,
    Artwork = 1 << 9,
    ArtistSort = 1 << 10,
    AlbumArtistSort = 1 << 11,
    AlbumSort = 1 << 12,
    Grouping = 1 << 13,
    Composer = 1 << 14,
    Conductor = 1 << 15,
    ReleaseType = 1 << 16,
    Compilation = 1 << 17
}

public enum MetadataWriteMode
{
    DatabaseOnly,
    WriteToFile
}

public sealed record MetadataEditPatch
{
    public MetadataEditFields Fields { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<string>? Artists { get; init; }
    public IReadOnlyList<string>? AlbumArtists { get; init; }
    public string? Album { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }
    public string? Comment { get; init; }
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public byte[]? Artwork { get; init; }
    public bool RemoveArtwork { get; init; }
    public string? ArtistSort { get; init; }
    public string? AlbumArtistSort { get; init; }
    public string? AlbumSort { get; init; }
    public string? Grouping { get; init; }
    public IReadOnlyList<string>? Composers { get; init; }
    public string? Conductor { get; init; }
    public string? ReleaseType { get; init; }
    public bool? IsCompilation { get; init; }
}

public sealed record MetadataEditResult(
    Track Before,
    Track After,
    MetadataWriteMode Mode,
    byte[]? OriginalFileBytes);

public enum MetadataMatchProvider { MusicBrainz, Discogs }

public sealed record MetadataMatch(
    MetadataMatchProvider Provider,
    string ExternalId,
    string Title,
    string Artist,
    string Album,
    int? Year,
    string? ArtworkUrl,
    string? SourceUrl,
    string Attribution);

public sealed record ArtistProfile(
    string ExternalId,
    string Name,
    string? Biography,
    string? ImageUrl,
    string Attribution,
    int TrackCount,
    int AlbumCount);
