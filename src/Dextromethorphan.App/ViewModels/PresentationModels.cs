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
    public required int TrackCount { get; init; }
    public Track? RepresentativeTrack { get; init; }
    public string Initial => string.IsNullOrWhiteSpace(Title) ? "?" : Title[..1].ToUpperInvariant();
    public string CountText => TrackCount == 1 ? "1 track" : $"{TrackCount:N0} tracks";
    public string? ArtworkPath { get => _artworkPath; set => Set(ref _artworkPath, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed record GalleryRowViewModel(
    int StartIndex,
    IReadOnlyList<LibraryCardViewModel> Cards);

public sealed class QueueEntryViewModel : ObservableObject
{
    private string? _artworkPath;

    public QueueEntryViewModel(
        QueueEntry entry,
        string? artworkPath,
        int index = 0,
        bool isNext = false,
        string? playbackError = null)
    {
        Entry = entry;
        _artworkPath = artworkPath;
        Index = index;
        IsNext = isNext;
        PlaybackError = playbackError;
    }

    public QueueEntry Entry { get; }
    public Track Track => Entry.Track;
    public bool IsPlaying => Entry.IsPlaying;
    public int Index { get; }
    public bool IsNext { get; }
    public bool HasPlaybackError => !string.IsNullOrWhiteSpace(PlaybackError);
    public string? PlaybackError { get; }
    public string SecondaryText => HasPlaybackError
        ? PlaybackError!
        : Track.DisplayArtist;
    public string PlaybackStateDescription => IsPlaying
        ? "Currently playing"
        : IsNext
            ? "Plays next"
            : "Queued for later";
    public string AutomationName =>
        $"{PlaybackStateDescription}. {Track.Title} by {Track.DisplayArtist}, {Track.DurationText}";
    public string ToolTipText => HasPlaybackError
        ? $"Playback error: {PlaybackError}"
        : AutomationName;
    public string? ArtworkPath { get => _artworkPath; set => Set(ref _artworkPath, value); }
    public override string ToString() => AutomationName;
}

public sealed record QueueHistoryEntryViewModel(
    Track Track,
    DateTimeOffset PlayedAt)
{
    public string PlayedAtText => PlayedAt.ToLocalTime().ToString("t");
}

public sealed class LyricLineViewModel(LyricLine line, bool isSynced = true) : ObservableObject
{
    private bool _isActive;
    private bool _isPast;
    private bool _isPrevious;
    private bool _isNext;
    public LyricLine Line { get; } = line;
    public string Text => Line.Text;
    public LyricLineRole Role => Line.Role;
    public bool IsSecondary => Role is LyricLineRole.Translation or LyricLineRole.Romanization;
    public bool IsInstrumental => Role == LyricLineRole.Instrumental;
    public bool IsSynced { get; } = isSynced;
    public bool CanSeek => IsSynced;
    public IReadOnlyList<LyricWordViewModel> Words { get; } = line.Words.Select(word => new LyricWordViewModel(word)).ToArray();
    public bool HasWords => Words.Count > 0;
    public string TimestampText => IsSynced ? FormatTime(Line.Start) : "";
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool IsPast { get => _isPast; private set => Set(ref _isPast, value); }
    public bool IsPrevious { get => _isPrevious; set => Set(ref _isPrevious, value); }
    public bool IsNext { get => _isNext; set => Set(ref _isNext, value); }

    public void UpdatePosition(TimeSpan position, bool animateWords = true)
    {
        if (!IsSynced) return;
        IsActive = Line.IsActive(position);
        IsPast = !IsActive && Line.Start < position;
        foreach (var word in Words) word.UpdatePosition(position, animateWords);
    }

    private static string FormatTime(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
}

public sealed class LyricWordViewModel(LyricWord word) : ObservableObject
{
    private double _progress;
    public string Text => word.Text;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public void UpdatePosition(TimeSpan position, bool animate = true)
    {
        if (position <= word.Start) { Progress = 0; return; }
        if (!animate || word.End is not { } end || end <= word.Start) { Progress = 1; return; }
        Progress = Math.Clamp((position - word.Start).TotalMilliseconds / (end - word.Start).TotalMilliseconds, 0, 1);
    }
}
