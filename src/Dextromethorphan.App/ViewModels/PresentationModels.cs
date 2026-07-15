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
    public IReadOnlyList<Track> Tracks { get; init; } = [];
    public Track? RepresentativeTrack => Tracks.FirstOrDefault();
    public string Initial => string.IsNullOrWhiteSpace(Title) ? "?" : Title[..1].ToUpperInvariant();
    public string CountText => Tracks.Count == 1 ? "1 track" : $"{Tracks.Count:N0} tracks";
    public string? ArtworkPath { get => _artworkPath; set => Set(ref _artworkPath, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class LyricLineViewModel(LyricLine line) : ObservableObject
{
    private bool _isActive;
    public LyricLine Line { get; } = line;
    public string Text => Line.Text;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
}
