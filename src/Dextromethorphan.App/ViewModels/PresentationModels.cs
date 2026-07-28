using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.ViewModels;

public sealed class LibraryCardViewModel : ObservableObject
{
    private string? _artworkPath;
    private bool _isSelected;

    public required string Kind { get; init; }
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string Detail { get; init; } = "";
    public long? PlaylistId { get; init; }
    public IReadOnlyList<int> TrackIndexes { get; init; } = [];
    public IReadOnlyList<Track>? MaterializedTracks { get; init; }
    public required int TrackCount { get; init; }
    public Track? RepresentativeTrack { get; init; }
    public string Initial => string.IsNullOrWhiteSpace(Title) ? "?" : Title[..1].ToUpperInvariant();
    public string CountText => TrackCount == 1 ? "1 track" : $"{TrackCount:N0} tracks";
    public string? ArtworkPath { get => _artworkPath; set => Set(ref _artworkPath, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class QueueEntryViewModel(QueueEntry entry, string? artworkPath) : ObservableObject
{
    private string? _artworkPath = artworkPath;
    public QueueEntry Entry { get; } = entry;
    public Track Track => Entry.Track;
    public bool IsPlaying => Entry.IsPlaying;
    public string? ArtworkPath { get => _artworkPath; set => Set(ref _artworkPath, value); }
}

public sealed class LyricLineViewModel(LyricLine line, bool isSynced = true) : ObservableObject
{
    private bool _isActive;
    private bool _isPast;
    public LyricLine Line { get; } = line;
    public string Text => Line.Text;
    public bool IsSynced { get; } = isSynced;
    public bool CanSeek => IsSynced;
    public string TimestampText => IsSynced ? FormatTime(Line.Start) : "";
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool IsPast { get => _isPast; private set => Set(ref _isPast, value); }

    public void UpdatePosition(TimeSpan position)
    {
        if (!IsSynced) return;
        IsActive = Line.IsActive(position);
        IsPast = !IsActive && Line.Start < position;
    }

    private static string FormatTime(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
}
