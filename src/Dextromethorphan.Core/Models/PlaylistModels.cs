namespace Dextromethorphan.Core.Models;

public enum PlaylistKind { Manual, Smart }
public enum PlaylistFormat { M3U8, PLS, XSPF }
public enum SmartRuleMatch { All, Any }

public enum SmartField
{
    Title, Artist, AlbumArtist, Album, Genre, Comment, Year, Rating, Loved,
    PlayCount, LastPlayed, DateAdded, Duration, Codec, Bitrate, SampleRate, Path
}

public enum SmartOperator
{
    Contains, NotContains, Equals, NotEquals, GreaterThan, GreaterOrEqual,
    LessThan, LessOrEqual, Before, After, IsTrue, IsFalse, InLastDays, NotInLastDays
}

public sealed record SmartRuleCondition
{
    public SmartField Field { get; init; }
    public SmartOperator Operator { get; init; }
    public string? Value { get; init; }
}

public sealed record SmartRuleGroup
{
    public SmartRuleMatch Match { get; init; } = SmartRuleMatch.All;
    public IReadOnlyList<SmartRuleCondition> Conditions { get; init; } = [];
    public IReadOnlyList<SmartRuleGroup> Groups { get; init; } = [];
}

public sealed record SmartPlaylistDefinition
{
    public SmartRuleGroup Root { get; init; } = new();
    public SmartField SortBy { get; init; } = SmartField.Title;
    public bool SortDescending { get; init; }
    public int? Limit { get; init; }
}

public sealed record Playlist
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public PlaylistKind Kind { get; init; }
    public SmartPlaylistDefinition? Rules { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ImportedPlaylist
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Locations { get; init; } = [];
}

public sealed record LibraryFileStamp(long Id, DateTimeOffset ModifiedAt, long Size);
