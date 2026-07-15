using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Lyrics;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly string[] Views = ["Albums", "Artists", "Genres", "Songs", "Folders", "Playlists", "Favorites", "Now Playing"];
    private readonly ISettingsService _settings;
    private readonly ILibraryRepository _repository;
    private readonly IPlaylistRepository _playlists;
    private readonly ILibraryScanner _scanner;
    private readonly IArtworkCache _artwork;
    private readonly IAudioEngine _audio;
    private readonly IPlaybackQueue _queue;
    private readonly ISleepTimerService _sleepTimer;
    private readonly IShortcutService _shortcuts;
    private readonly ISystemMediaTransportService _systemMedia;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, string?> _resolvedArtwork = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<NavigationEntry> _backHistory = new();
    private readonly Stack<NavigationEntry> _forwardHistory = new();
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _artworkCancellation;
    private CancellationTokenSource? _volumeCancellation;
    private IReadOnlyList<Track> _allTracks = [];
    private IReadOnlyList<LibraryCardViewModel> _activeGroups = [];
    private IReadOnlyList<LibraryCardViewModel> _galleryGroups = [];
    private IReadOnlyList<LibraryCardViewModel> _sidebarCards = [];
    private Track? _selectedTrack;
    private Track? _currentTrack;
    private LibraryCardViewModel? _selectedCard;
    private string _currentView = "Albums";
    private string _viewSubtitle = "Your music, organized locally";
    private string _selectedGroupTitle = "All albums";
    private string _selectedGroupSubtitle = "Select a collection to see its tracks";
    private string _searchText = "";
    private string _statusText = "Starting…";
    private string _playGlyph = "▶";
    private string _positionText = "0:00";
    private string _durationText = "0:00";
    private double _positionSeconds;
    private double _durationSeconds = 1;
    private double _volume = 0.82;
    private bool _isScanning;
    private bool _queueVisible = true;
    private bool _isCollectionDetailOpen;
    private bool _isUserSeeking;
    private bool _animationsEnabled = true;
    private int _albumTileSize = 172;
    private string _activeLyric = "Lyrics will appear here when available.";

    public MainViewModel(
        ISettingsService settings,
        ILibraryRepository repository,
        IPlaylistRepository playlists,
        ILibraryScanner scanner,
        IArtworkCache artwork,
        IAudioEngine audio,
        IPlaybackQueue queue,
        ISleepTimerService sleepTimer,
        IShortcutService shortcuts,
        ISystemMediaTransportService systemMedia)
    {
        _settings = settings; _repository = repository; _playlists = playlists; _scanner = scanner; _artwork = artwork;
        _audio = audio; _queue = queue; _sleepTimer = sleepTimer; _shortcuts = shortcuts; _systemMedia = systemMedia;
        NavigateCommand = new RelayCommand(p => Navigate(p?.ToString()));
        SelectGroupCommand = new RelayCommand(p => SelectGroup(p as LibraryCardViewModel));
        CloseCollectionCommand = new RelayCommand(_ => CloseCollectionDetail());
        PlayGroupCommand = new AsyncRelayCommand(p => PlayGroupAsync(p as LibraryCardViewModel), p => p is LibraryCardViewModel card && card.Tracks.Count > 0);
        PlaySelectedCommand = new AsyncRelayCommand(_ => PlaySelectedAsync(), _ => SelectedTrack is not null);
        TogglePlaybackCommand = new AsyncRelayCommand(_ => TogglePlaybackAsync());
        NextCommand = new AsyncRelayCommand(_ => ChangeTrackAsync(_queue.Advance()));
        PreviousCommand = new AsyncRelayCommand(_ => ChangeTrackAsync(_queue.Previous()));
        AddToQueueCommand = new RelayCommand(p => { if (p is Track track) _queue.Add([track]); });
        PlayNextCommand = new RelayCommand(p => { if (p is Track track) _queue.PlayNext([track]); });
        ToggleQueueCommand = new RelayCommand(_ => QueueVisible = !QueueVisible);
        ToggleShuffleCommand = new RelayCommand(_ => { _queue.Shuffle = !_queue.Shuffle; Raise(nameof(IsShuffleEnabled)); });
        CycleRepeatCommand = new RelayCommand(_ => { _queue.RepeatMode = _queue.RepeatMode switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off }; Raise(nameof(RepeatText)); Raise(nameof(IsRepeatEnabled)); });
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !_scanner.IsScanning && _settings.Current.LibraryFolders.Count > 0);
        UndoQueueCommand = new RelayCommand(_ => _queue.Undo());
        ClearQueueCommand = new RelayCommand(_ => _queue.Replace([]));
        LoveCommand = new AsyncRelayCommand(_ => ToggleLoveAsync(), _ => CurrentTrack is not null);
        _audio.StateChanged += AudioOnStateChanged;
        _audio.TrackTransitioned += AudioOnTrackTransitioned;
        _audio.PlaybackEnded += AudioOnPlaybackEnded;
        _queue.Changed += QueueOnChanged;
        _scanner.ProgressChanged += ScannerOnProgressChanged;
        _sleepTimer.Expired += (_, _) => _ = _audio.StopAsync();
        _shortcuts.ActionInvoked += ShortcutsOnActionInvoked;
        _systemMedia.CommandReceived += SystemMediaOnCommandReceived;
    }

    public ObservableCollection<Track> BrowseTracks { get; } = [];
    public IReadOnlyList<LibraryCardViewModel> GalleryGroups { get => _galleryGroups; private set => Set(ref _galleryGroups, value); }
    public IReadOnlyList<LibraryCardViewModel> ActiveGroups { get => _activeGroups; private set => Set(ref _activeGroups, value); }
    public IReadOnlyList<LibraryCardViewModel> SidebarCards { get => _sidebarCards; private set => Set(ref _sidebarCards, value); }
    public ObservableCollection<LibraryCardViewModel> Albums { get; } = [];
    public ObservableCollection<LibraryCardViewModel> Artists { get; } = [];
    public ObservableCollection<LibraryCardViewModel> Genres { get; } = [];
    public ObservableCollection<LibraryCardViewModel> Folders { get; } = [];
    public ObservableCollection<LibraryCardViewModel> Playlists { get; } = [];
    public ObservableCollection<QueueEntry> Queue { get; } = [];
    public ObservableCollection<LyricLineViewModel> Lyrics { get; } = [];
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = [];

    public Track? SelectedTrack { get => _selectedTrack; set { if (Set(ref _selectedTrack, value)) (PlaySelectedCommand as AsyncRelayCommand)?.CanExecute(value); } }
    public Track? CurrentTrack { get => _currentTrack; private set { if (Set(ref _currentTrack, value)) { Raise(nameof(HasCurrentTrack)); Raise(nameof(CurrentTitle)); Raise(nameof(CurrentArtist)); Raise(nameof(CurrentArtworkPath)); Raise(nameof(LoveGlyph)); } } }
    public LibraryCardViewModel? SelectedCard { get => _selectedCard; private set { if (Set(ref _selectedCard, value)) { Raise(nameof(DetailTabTitle)); Raise(nameof(HasDetailArtwork)); } } }
    public bool HasCurrentTrack => CurrentTrack is not null;
    public bool HasLibrary => _allTracks.Count > 0;
    public bool HasBrowseTracks => BrowseTracks.Count > 0;
    public bool HasQueue => Queue.Count > 0;
    public bool IsGroupView => !IsCollectionDetailOpen && CurrentView is "Albums" or "Artists" or "Genres";
    public bool IsCollectionDetailView => IsCollectionDetailOpen && CurrentView is "Albums" or "Artists" or "Genres";
    public bool IsTrackView => !IsCollectionDetailOpen && CurrentView is "Songs" or "Favorites";
    public bool IsSidebarView => !IsCollectionDetailOpen && CurrentView is "Folders" or "Playlists";
    public bool IsNowPlayingView => !IsCollectionDetailOpen && CurrentView == "Now Playing";
    public string CurrentTitle => CurrentTrack?.Title ?? "Nothing playing";
    public string CurrentArtist => CurrentTrack is null ? "Choose something from your library" : $"{CurrentTrack.DisplayArtist} — {CurrentTrack.DisplayAlbum}";
    public string? CurrentArtworkPath => CurrentTrack?.ArtworkPath;
    public string LoveGlyph => CurrentTrack?.IsLoved == true ? "♥" : "♡";
    public bool IsCollectionDetailOpen
    {
        get => _isCollectionDetailOpen;
        private set
        {
            if (!Set(ref _isCollectionDetailOpen, value)) return;
            Raise(nameof(IsGroupView)); Raise(nameof(IsCollectionDetailView)); Raise(nameof(IsTrackView)); Raise(nameof(IsSidebarView)); Raise(nameof(IsNowPlayingView));
            Raise(nameof(ViewTitle)); Raise(nameof(DetailTabTitle));
        }
    }
    public string DetailTabTitle => SelectedCard?.Title ?? "Collection";
    public bool HasDetailArtwork => SelectedCard?.ArtworkPath is { Length: > 0 };
    public string CurrentView
    {
        get => _currentView;
        private set
        {
            if (!Set(ref _currentView, value)) return;
            Raise(nameof(IsGroupView)); Raise(nameof(IsCollectionDetailView)); Raise(nameof(IsTrackView)); Raise(nameof(IsSidebarView)); Raise(nameof(IsNowPlayingView));
        }
    }
    public string ViewTitle => IsCollectionDetailOpen ? DetailTabTitle : CurrentView;
    public string ViewSubtitle { get => _viewSubtitle; private set => Set(ref _viewSubtitle, value); }
    public string SelectedGroupTitle { get => _selectedGroupTitle; private set => Set(ref _selectedGroupTitle, value); }
    public string SelectedGroupSubtitle { get => _selectedGroupSubtitle; private set => Set(ref _selectedGroupSubtitle, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) DebounceSearch(); } }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string PlayGlyph { get => _playGlyph; private set => Set(ref _playGlyph, value); }
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }
    public string DurationText { get => _durationText; private set => Set(ref _durationText, value); }
    public double PositionSeconds { get => _positionSeconds; private set => Set(ref _positionSeconds, value); }
    public double DurationSeconds { get => _durationSeconds; private set => Set(ref _durationSeconds, Math.Max(1, value)); }
    public double Volume { get => _volume; set { if (Set(ref _volume, Math.Clamp(value, 0, 1))) QueueVolumeUpdate(_volume); } }
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
    public bool QueueVisible { get => _queueVisible; set { if (Set(ref _queueVisible, value)) _ = _settings.UpdateAsync(x => x.QueuePanelVisible = value); } }
    public bool AnimationsEnabled { get => _animationsEnabled; set { if (Set(ref _animationsEnabled, value)) _ = _settings.UpdateAsync(x => x.AnimationsEnabled = value); } }
    public bool IsShuffleEnabled => _queue.Shuffle;
    public bool IsRepeatEnabled => _queue.RepeatMode != RepeatMode.Off;
    public string RepeatText => _queue.RepeatMode switch { RepeatMode.One => "Repeat one", RepeatMode.All => "Repeat all", _ => "Repeat off" };
    public int AlbumTileSize { get => _albumTileSize; set { if (Set(ref _albumTileSize, value)) _ = _settings.UpdateAsync(x => x.AlbumTileSize = value); } }
    public string ActiveLyric { get => _activeLyric; private set => Set(ref _activeLyric, value); }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand SelectGroupCommand { get; }
    public RelayCommand CloseCollectionCommand { get; }
    public AsyncRelayCommand PlayGroupCommand { get; }
    public AsyncRelayCommand PlaySelectedCommand { get; }
    public AsyncRelayCommand TogglePlaybackCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public RelayCommand AddToQueueCommand { get; }
    public RelayCommand PlayNextCommand { get; }
    public RelayCommand ToggleQueueCommand { get; }
    public RelayCommand ToggleShuffleCommand { get; }
    public RelayCommand CycleRepeatCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand UndoQueueCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public AsyncRelayCommand LoveCommand { get; }

    public async Task InitializeAsync()
    {
        await _settings.InitializeAsync(_lifetime.Token);
        await _repository.InitializeAsync(_lifetime.Token);
        _volume = _settings.Current.Volume; Raise(nameof(Volume));
        _queueVisible = _settings.Current.QueuePanelVisible; Raise(nameof(QueueVisible));
        _albumTileSize = _settings.Current.AlbumTileSize; Raise(nameof(AlbumTileSize));
        _animationsEnabled = _settings.Current.AnimationsEnabled; Raise(nameof(AnimationsEnabled));
        await RefreshLibraryAsync(cancellationToken: _lifetime.Token);
        foreach (var device in await _audio.GetOutputDevicesAsync(_lifetime.Token)) OutputDevices.Add(device);
        var profile = _settings.Current.OutputProfiles.FirstOrDefault(x => x.DeviceId == _settings.Current.ActiveOutputDeviceId) ?? _settings.Current.OutputProfiles[0];
        await _audio.SetPlaybackOptionsAsync(new AudioPlaybackOptions
        {
            ReplayGainMode = _settings.Current.ReplayGainMode,
            ReplayGainPreampDb = _settings.Current.ReplayGainPreampDb,
            PreventClipping = _settings.Current.PreventClipping,
            TransitionMode = profile.CrossfadeSeconds > 0 ? TransitionMode.Crossfade : _settings.Current.TransitionMode,
            CrossfadeSeconds = profile.CrossfadeSeconds > 0 ? profile.CrossfadeSeconds : _settings.Current.CrossfadeSeconds,
            FadeInSeconds = _settings.Current.FadeInSeconds,
            FadeOutSeconds = _settings.Current.FadeOutSeconds,
            Speed = _settings.Current.PlaybackSpeed,
            PitchSemitones = _settings.Current.PitchSemitones,
            PreservePitch = _settings.Current.PreservePitch
        }, _lifetime.Token);
        await _audio.ConfigureOutputAsync(profile, _lifetime.Token);
        await _audio.SetVolumeAsync(_volume, _lifetime.Token);
        _shortcuts.Refresh(_settings.Current.Shortcuts);
        _scanner.StartWatching(_settings.Current.LibraryFolders);
    }

    public async Task AddLibraryFolderAsync(string folder)
    {
        await _settings.UpdateAsync(x => { if (!x.LibraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) x.LibraryFolders.Add(folder); }, _lifetime.Token);
        _scanner.StartWatching(_settings.Current.LibraryFolders);
        await ScanAsync();
    }

    public Task SeekAsync(double seconds) => _audio.SeekAsync(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DurationSeconds)), _lifetime.Token);
    public void BeginSeek() => _isUserSeeking = true;
    public void PreviewSeek(double seconds)
    {
        _isUserSeeking = true;
        PositionSeconds = Math.Clamp(seconds, 0, DurationSeconds);
        PositionText = FormatTime(TimeSpan.FromSeconds(PositionSeconds));
    }
    public async Task CommitSeekAsync(double seconds)
    {
        PreviewSeek(seconds);
        try { await SeekAsync(PositionSeconds); }
        finally { _isUserSeeking = false; }
    }
    public void StartSleepTimer(TimeSpan duration) => _sleepTimer.Start(duration);
    public void StopAtEndOfTrack() => _sleepTimer.StopAtEndOfTrack();
    public void CancelSleepTimer() => _sleepTimer.Cancel();

    private async Task RefreshLibraryAsync(string query = "", CancellationToken cancellationToken = default)
    {
        var tracks = string.IsNullOrWhiteSpace(query)
            ? await _repository.GetAllAsync(cancellationToken)
            : await _repository.SearchAsync(query, 5000, cancellationToken);
        var groups = await Task.Run(() => BuildGroups(tracks), cancellationToken);
        var playlistCards = await BuildPlaylistCardsAsync(query, cancellationToken);
        RunOnUi(() =>
        {
            _allTracks = tracks;
            Replace(Albums, groups.Albums); Replace(Artists, groups.Artists); Replace(Genres, groups.Genres); Replace(Folders, groups.Folders); Replace(Playlists, playlistCards);
            StatusText = tracks.Count == 0 ? (_settings.Current.LibraryFolders.Count == 0 ? "Add a music folder to begin" : "No matching tracks") : $"{tracks.Count:N0} tracks · {groups.Albums.Count:N0} albums · {groups.Artists.Count:N0} artists";
            Raise(nameof(HasLibrary));
            ApplyCurrentView(true);
        });
        StartArtworkResolution(groups.Albums.Concat(groups.Artists).Concat(groups.Genres).Concat(groups.Folders).Concat(playlistCards));
    }

    private static LibraryGroups BuildGroups(IReadOnlyList<Track> tracks)
    {
        var albums = tracks.GroupBy(x => new { Album = x.DisplayAlbum, Artist = string.IsNullOrWhiteSpace(x.AlbumArtist) ? x.DisplayArtist : x.AlbumArtist })
            .Select(group => Card("Album", group.Key.Artist + "\0" + group.Key.Album, group.Key.Album,
                group.Max(x => x.Year) is > 0 and var year ? $"{group.Key.Artist} · {year}" : group.Key.Artist,
                group.OrderBy(x => x.DiscNumber).ThenBy(x => x.TrackNumber).ThenBy(x => x.Title).ToArray()))
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var artists = tracks.SelectMany(track => SplitValues(track.DisplayArtist).Select(artist => (Artist: artist, Track: track)))
            .GroupBy(x => x.Artist, StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var albumCount = group.Select(x => x.Track.DisplayAlbum).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
                return Card("Artist", group.Key, group.Key, albumCount == 1 ? "1 album" : $"{albumCount:N0} albums",
                    group.Select(x => x.Track).DistinctBy(x => x.Id > 0 ? x.Id.ToString() : x.Path).OrderBy(x => x.Year).ThenBy(x => x.Album).ThenBy(x => x.TrackNumber).ToArray());
            })
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var genres = tracks.SelectMany(track => SplitValues(track.Genre, "Uncategorized").Select(genre => (Genre: genre, Track: track)))
            .GroupBy(x => x.Genre, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => Card("Genre", group.Key, group.Key, "Genre", group.Select(x => x.Track).DistinctBy(x => x.Id > 0 ? x.Id.ToString() : x.Path).OrderBy(x => x.Artist).ThenBy(x => x.Album).ThenBy(x => x.TrackNumber).ToArray()))
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var folders = tracks.GroupBy(x => Path.GetDirectoryName(x.Path) ?? x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => Card("Folder", group.Key, Path.GetFileName(group.Key.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : group.Key,
                group.Key, group.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(), group.Key))
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new LibraryGroups(albums, artists, genres, folders);
    }

    private async Task<IReadOnlyList<LibraryCardViewModel>> BuildPlaylistCardsAsync(string query, CancellationToken cancellationToken)
    {
        var result = new List<LibraryCardViewModel>();
        foreach (var playlist in await _playlists.GetAllAsync(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(query) && !playlist.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
            var tracks = await _playlists.GetTracksAsync(playlist.Id, cancellationToken);
            result.Add(new LibraryCardViewModel
            {
                Kind = "Playlist", Key = playlist.Id.ToString(), PlaylistId = playlist.Id, Title = playlist.Name,
                Subtitle = playlist.Kind == PlaylistKind.Smart ? "Smart playlist" : "Playlist", Detail = tracks.Count == 1 ? "1 track" : $"{tracks.Count:N0} tracks",
                Tracks = tracks, ArtworkPath = ExistingArtwork(tracks.FirstOrDefault())
            });
        }
        return result.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static LibraryCardViewModel Card(string kind, string key, string title, string subtitle, IReadOnlyList<Track> tracks, string detail = "") => new()
    {
        Kind = kind, Key = key, Title = title, Subtitle = subtitle, Detail = detail, Tracks = tracks, ArtworkPath = ExistingArtwork(tracks.FirstOrDefault())
    };

    private static string? ExistingArtwork(Track? track) => track?.ArtworkPath is { Length: > 0 } path && File.Exists(path) ? path : null;

    private static IEnumerable<string> SplitValues(string? value, string fallback = "Unknown artist")
    {
        if (string.IsNullOrWhiteSpace(value)) return [fallback];
        var values = value.Split([';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        return values.Length == 0 ? [fallback] : values;
    }

    private void StartArtworkResolution(IEnumerable<LibraryCardViewModel> source)
    {
        _artworkCancellation?.Cancel(); _artworkCancellation?.Dispose();
        _artworkCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _artworkCancellation.Token;
        var cards = source.Where(x => x.ArtworkPath is null && x.RepresentativeTrack is not null).ToArray();
        _ = ResolveCardArtworkAsync(cards, token);
    }

    private async Task ResolveCardArtworkAsync(IReadOnlyList<LibraryCardViewModel> cards, CancellationToken cancellationToken)
    {
        try
        {
            await Parallel.ForEachAsync(cards, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (card, ct) =>
            {
                var path = await ResolveArtworkAsync(card.RepresentativeTrack!, ct);
                if (path is not null) RunOnUi(() => { card.ArtworkPath = path; if (ReferenceEquals(card, SelectedCard)) Raise(nameof(HasDetailArtwork)); });
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { RunOnUi(() => StatusText = exception.Message); }
    }

    private async Task<string?> ResolveArtworkAsync(Track track, CancellationToken cancellationToken)
    {
        if (track.ArtworkPath is { Length: > 0 } existing && File.Exists(existing)) return existing;
        if (_resolvedArtwork.TryGetValue(track.Path, out var cached)) return cached;
        var resolved = await _artwork.GetOrCreateAsync(track.Path, cancellationToken);
        _resolvedArtwork[track.Path] = resolved;
        return resolved;
    }

    private void Navigate(string? view)
    {
        if (view is null || !Views.Contains(view, StringComparer.OrdinalIgnoreCase)) return;
        var previous = CaptureNavigation();
        IsCollectionDetailOpen = false;
        CurrentView = Views.First(x => x.Equals(view, StringComparison.OrdinalIgnoreCase));
        Raise(nameof(ViewTitle));
        ApplyCurrentView(false);
        RecordNavigation(previous);
    }

    private void ApplyCurrentView(bool resetSelection)
    {
        if (resetSelection && IsCollectionDetailOpen) IsCollectionDetailOpen = false;
        switch (CurrentView)
        {
            case "Albums": ViewSubtitle = $"{Albums.Count:N0} albums in your library"; SetActiveGroups(Albums); ClearCollectionSelection(); break;
            case "Artists": ViewSubtitle = $"{Artists.Count:N0} artists in your library"; SetActiveGroups(Artists); ClearCollectionSelection(); break;
            case "Genres": ViewSubtitle = $"{Genres.Count:N0} genres in your library"; SetActiveGroups(Genres); ClearCollectionSelection(); break;
            case "Folders": ViewSubtitle = $"{Folders.Count:N0} folders across {_settings.Current.LibraryFolders.Count:N0} sources"; SidebarCards = Folders; SelectDefault(SidebarCards, resetSelection); break;
            case "Playlists": ViewSubtitle = $"{Playlists.Count:N0} saved and smart playlists"; SidebarCards = Playlists; SelectDefault(SidebarCards, resetSelection); break;
            case "Favorites":
                ViewSubtitle = "Tracks you have marked as loved";
                SetBrowseTracks(_allTracks.Where(x => x.IsLoved).OrderBy(x => x.Artist).ThenBy(x => x.Album).ThenBy(x => x.TrackNumber), "Favorites", $"{_allTracks.Count(x => x.IsLoved):N0} loved tracks");
                break;
            case "Songs":
                ViewSubtitle = $"{_allTracks.Count:N0} tracks, stored offline";
                SetBrowseTracks(_allTracks, "All songs", StatusText);
                break;
            case "Now Playing": ViewSubtitle = CurrentTrack is null ? "Choose a track to begin" : CurrentArtist; break;
        }
    }

    private void SelectDefault(IReadOnlyList<LibraryCardViewModel> cards, bool reset)
    {
        var selected = !reset && SelectedCard is not null ? cards.FirstOrDefault(x => x.Key == SelectedCard.Key && x.Kind == SelectedCard.Kind) : null;
        SelectGroupCore(selected ?? cards.FirstOrDefault(), false);
        if (cards.Count == 0) SetBrowseTracks([], $"No {CurrentView.ToLowerInvariant()}", string.IsNullOrWhiteSpace(SearchText) ? "This view will populate as your library is scanned." : "No results match your search.");
    }

    private void SelectGroup(LibraryCardViewModel? card)
    {
        if (card is null) return;
        var previous = CaptureNavigation();
        SelectGroupCore(card, CurrentView is "Albums" or "Artists" or "Genres");
        RecordNavigation(previous);
    }

    private void SelectGroupCore(LibraryCardViewModel? card, bool openCollectionDetail)
    {
        if (card is null) return;
        if (SelectedCard is not null) SelectedCard.IsSelected = false;
        SelectedCard = card;
        card.IsSelected = true;
        SetBrowseTracks(card.Tracks, card.Title, string.IsNullOrWhiteSpace(card.Detail) ? $"{card.Subtitle} · {card.CountText}" : $"{card.Detail} · {card.CountText}");
        if (openCollectionDetail && CurrentView is "Albums" or "Artists" or "Genres")
        {
            IsCollectionDetailOpen = true;
            ViewSubtitle = string.IsNullOrWhiteSpace(card.Detail) ? $"{card.Subtitle} · {card.CountText}" : $"{card.Detail} · {card.CountText}";
            Raise(nameof(ViewTitle));
        }
    }

    private void CloseCollectionDetail()
    {
        var previous = CaptureNavigation();
        IsCollectionDetailOpen = false;
        ApplyCurrentView(false);
        RecordNavigation(previous);
    }

    public bool NavigateBack()
    {
        if (_backHistory.Count == 0) return false;
        var current = CaptureNavigation();
        var target = _backHistory.Pop();
        _forwardHistory.Push(current);
        RestoreNavigation(target);
        return true;
    }

    public bool NavigateForward()
    {
        if (_forwardHistory.Count == 0) return false;
        var current = CaptureNavigation();
        var target = _forwardHistory.Pop();
        _backHistory.Push(current);
        RestoreNavigation(target);
        return true;
    }

    private NavigationEntry CaptureNavigation() => new(CurrentView, SelectedCard?.Kind, SelectedCard?.Key, IsCollectionDetailOpen);

    private void RecordNavigation(NavigationEntry previous)
    {
        if (previous == CaptureNavigation()) return;
        _backHistory.Push(previous);
        _forwardHistory.Clear();
    }

    private void RestoreNavigation(NavigationEntry entry)
    {
        IsCollectionDetailOpen = false;
        CurrentView = entry.View;
        Raise(nameof(ViewTitle));
        ApplyCurrentView(false);
        if (entry.CardKey is null) return;
        var card = CardsForView(entry.View).FirstOrDefault(x => x.Kind == entry.CardKind && x.Key == entry.CardKey);
        SelectGroupCore(card, entry.IsCollectionDetail);
    }

    private IReadOnlyList<LibraryCardViewModel> CardsForView(string view) => view switch
    {
        "Albums" => Albums,
        "Artists" => Artists,
        "Genres" => Genres,
        "Folders" => Folders,
        "Playlists" => Playlists,
        _ => []
    };

    private void SetActiveGroups(IReadOnlyList<LibraryCardViewModel> groups)
    {
        ActiveGroups = groups;
        GalleryGroups = groups.Take(28).ToArray();
    }

    public void LoadMoreGalleryGroups()
    {
        if (GalleryGroups.Count >= ActiveGroups.Count) return;
        GalleryGroups = ActiveGroups.Take(Math.Min(GalleryGroups.Count + 28, ActiveGroups.Count)).ToArray();
    }

    private void ClearCollectionSelection()
    {
        if (SelectedCard is not null) SelectedCard.IsSelected = false;
        SelectedCard = null;
        BrowseTracks.Clear();
        SelectedTrack = null;
        Raise(nameof(HasBrowseTracks));
    }

    private void SetBrowseTracks(IEnumerable<Track> tracks, string title, string subtitle)
    {
        BrowseTracks.Clear(); foreach (var track in tracks) BrowseTracks.Add(track);
        SelectedGroupTitle = title; SelectedGroupSubtitle = subtitle;
        SelectedTrack = BrowseTracks.FirstOrDefault();
        Raise(nameof(HasBrowseTracks));
    }

    private async Task PlayGroupAsync(LibraryCardViewModel? card)
    {
        if (card is null || card.Tracks.Count == 0) return;
        SelectGroup(card);
        _queue.Replace(card.Tracks, 0);
        await ChangeTrackAsync(card.Tracks[0]);
    }

    private async Task ScanAsync()
    {
        if (_settings.Current.LibraryFolders.Count == 0) { StatusText = "Add a music folder first"; return; }
        try { IsScanning = true; await _scanner.ScanAsync(_settings.Current.LibraryFolders, _settings.Current.ExcludedFolders, _lifetime.Token); await RefreshLibraryAsync(SearchText, _lifetime.Token); }
        finally { IsScanning = false; }
    }

    private async Task PlaySelectedAsync()
    {
        if (SelectedTrack is null) return;
        var source = BrowseTracks.Count > 0 ? BrowseTracks.ToArray() : _allTracks;
        _queue.Replace(source, Math.Max(0, Array.IndexOf(source.ToArray(), SelectedTrack)));
        await ChangeTrackAsync(SelectedTrack);
    }

    private async Task ChangeTrackAsync(Track? track)
    {
        if (track is null) return;
        try
        {
            if (_audio.Snapshot.Track is { Id: > 0 } previous && _audio.Snapshot.Position > TimeSpan.FromSeconds(10))
                await _repository.SaveBookmarkAsync(previous.Id, _audio.Snapshot.Position, _lifetime.Token);
            var artwork = await ResolveArtworkAsync(track, _lifetime.Token);
            if (artwork is not null) track = track with { ArtworkPath = artwork };
            await _audio.LoadAsync(track, _lifetime.Token);
            var bookmark = track.Id > 0 ? await _repository.GetBookmarkAsync(track.Id, _lifetime.Token) : null;
            if (bookmark.HasValue && bookmark.Value > TimeSpan.Zero && bookmark.Value < track.Duration - TimeSpan.FromSeconds(10)) await _audio.SeekAsync(bookmark.Value, _lifetime.Token);
            await _audio.QueueNextAsync(PeekUpcomingTrack(), _lifetime.Token);
            await _audio.PlayAsync(_lifetime.Token);
            if (track.Id > 0) await _repository.RecordPlayAsync(track.Id, _lifetime.Token);
            LoadLyrics(track);
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    private async Task TogglePlaybackAsync()
    {
        if (_audio.Snapshot.State == PlaybackState.Playing) await _audio.PauseAsync(_lifetime.Token);
        else if (_audio.Snapshot.Track is not null) await _audio.PlayAsync(_lifetime.Token);
        else if (SelectedTrack is not null) await PlaySelectedAsync();
    }

    private async Task ToggleLoveAsync()
    {
        if (CurrentTrack is null) return;
        await _repository.SetRatingAsync(CurrentTrack.Id, CurrentTrack.Rating, !CurrentTrack.IsLoved, _lifetime.Token);
        ReplaceTrackState(CurrentTrack with { IsLoved = !CurrentTrack.IsLoved });
    }

    private async Task SetRatingAsync(int rating)
    {
        if (CurrentTrack is null) return;
        rating = Math.Clamp(rating, 0, 5);
        await _repository.SetRatingAsync(CurrentTrack.Id, rating, CurrentTrack.IsLoved, _lifetime.Token);
        ReplaceTrackState(CurrentTrack with { Rating = rating });
    }

    private void ReplaceTrackState(Track updated)
    {
        CurrentTrack = updated;
        var all = _allTracks.ToArray();
        var index = Array.FindIndex(all, x => x.Id == updated.Id && updated.Id > 0 || x.Path.Equals(updated.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) { all[index] = updated; _allTracks = all; }
        var browse = BrowseTracks.ToArray();
        index = Array.FindIndex(browse, x => x.Id == updated.Id && updated.Id > 0 || x.Path.Equals(updated.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) BrowseTracks[index] = updated;
        if (CurrentView == "Favorites" && !updated.IsLoved) ApplyCurrentView(false);
    }

    public bool ExecuteShortcut(string action)
    {
        switch (action)
        {
            case ShortcutActions.TogglePlayback: TogglePlaybackCommand.Execute(null); break;
            case ShortcutActions.Play: _ = _audio.PlayAsync(_lifetime.Token); break;
            case ShortcutActions.Pause: _ = _audio.PauseAsync(_lifetime.Token); break;
            case ShortcutActions.Stop: _ = _audio.StopAsync(_lifetime.Token); break;
            case ShortcutActions.Next: NextCommand.Execute(null); break;
            case ShortcutActions.Previous: PreviousCommand.Execute(null); break;
            case ShortcutActions.SeekForward: _ = SeekAsync(PositionSeconds + _settings.Current.SeekStepSeconds); break;
            case ShortcutActions.SeekBackward: _ = SeekAsync(PositionSeconds - _settings.Current.SeekStepSeconds); break;
            case ShortcutActions.VolumeUp: Volume += _settings.Current.VolumeStep; break;
            case ShortcutActions.VolumeDown: Volume -= _settings.Current.VolumeStep; break;
            case ShortcutActions.RatingUp: _ = SetRatingAsync((CurrentTrack?.Rating ?? 0) + 1); break;
            case ShortcutActions.RatingDown: _ = SetRatingAsync((CurrentTrack?.Rating ?? 0) - 1); break;
            case ShortcutActions.Love: LoveCommand.Execute(null); break;
            case ShortcutActions.UndoQueue: UndoQueueCommand.Execute(null); break;
            default: return false;
        }
        return true;
    }

    private void LoadLyrics(Track track)
    {
        Lyrics.Clear();
        if (string.IsNullOrWhiteSpace(track.Lyrics)) { ActiveLyric = "No lyrics found. Place an .lrc file beside the track."; return; }
        var synced = LrcParser.Parse(track.Lyrics);
        if (synced.Lines.Count == 0) foreach (var text in track.Lyrics.Split('\n')) Lyrics.Add(new LyricLineViewModel(new LyricLine(TimeSpan.Zero, null, text, [])));
        else foreach (var line in synced.Lines) Lyrics.Add(new LyricLineViewModel(line));
        ActiveLyric = Lyrics.FirstOrDefault()?.Text ?? "No lyrics";
    }

    private void AudioOnStateChanged(object? sender, PlaybackSnapshot snapshot) => RunOnUi(() =>
    {
        var track = snapshot.Track;
        if (track is not null && _resolvedArtwork.TryGetValue(track.Path, out var artwork) && artwork is not null) track = track with { ArtworkPath = artwork };
        CurrentTrack = track;
        PlayGlyph = snapshot.State == PlaybackState.Playing ? "Ⅱ" : "▶";
        DurationSeconds = snapshot.Duration.TotalSeconds;
        DurationText = FormatTime(snapshot.Duration);
        if (!_isUserSeeking)
        {
            PositionSeconds = snapshot.Position.TotalSeconds;
            PositionText = FormatTime(snapshot.Position);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Error)) StatusText = snapshot.Error;
        LyricLineViewModel? active = null;
        foreach (var line in Lyrics) { line.IsActive = line.Line.IsActive(snapshot.Position); if (line.IsActive) active = line; }
        if (active is not null) ActiveLyric = active.Text;
        _systemMedia.Update(snapshot with { Track = track }, HasPreviousTrack(), HasNextTrack());
        if (CurrentView == "Now Playing") { ViewSubtitle = CurrentArtist; Raise(nameof(ViewTitle)); }
    });

    private void AudioOnTrackTransitioned(object? sender, TrackTransitionedEventArgs e) => _ = HandleTrackTransitionedAsync(e);
    private async Task HandleTrackTransitionedAsync(TrackTransitionedEventArgs e)
    {
        _sleepTimer.NotifyTrackEnded(); _queue.Advance();
        if (e.Current.Id > 0) await _repository.RecordPlayAsync(e.Current.Id, _lifetime.Token);
        var artwork = await ResolveArtworkAsync(e.Current, _lifetime.Token);
        RunOnUi(() => { LoadLyrics(e.Current); if (CurrentTrack?.Path.Equals(e.Current.Path, StringComparison.OrdinalIgnoreCase) == true && artwork is not null) CurrentTrack = e.Current with { ArtworkPath = artwork }; });
        await _audio.QueueNextAsync(PeekUpcomingTrack(), _lifetime.Token);
    }

    private void AudioOnPlaybackEnded(object? sender, EventArgs e) => _ = HandlePlaybackEndedAsync();
    private async Task HandlePlaybackEndedAsync() { _sleepTimer.NotifyTrackEnded(); var next = _queue.Advance(); if (next is not null) await ChangeTrackAsync(next); }

    private Track? PeekUpcomingTrack()
    {
        if (_queue.Shuffle || _queue.Items.Count == 0) return null;
        var index = _queue.CurrentIndex + 1;
        if (index < _queue.Items.Count) return _queue.Items[index].Track;
        return _queue.RepeatMode == RepeatMode.All ? _queue.Items[0].Track : null;
    }

    private void QueueOnChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        Queue.Clear(); foreach (var item in _queue.Items) Queue.Add(item);
        Raise(nameof(HasQueue));
        Raise(nameof(IsShuffleEnabled)); Raise(nameof(IsRepeatEnabled)); Raise(nameof(RepeatText));
        _systemMedia.Update(_audio.Snapshot, HasPreviousTrack(), HasNextTrack());
    });

    private bool HasPreviousTrack() => _queue.CurrentIndex > 0 || (_queue.RepeatMode == RepeatMode.All && _queue.Items.Count > 1);
    private bool HasNextTrack() => _queue.CurrentIndex >= 0 && (_queue.CurrentIndex + 1 < _queue.Items.Count || (_queue.RepeatMode == RepeatMode.All && _queue.Items.Count > 1));
    private void ShortcutsOnActionInvoked(object? sender, string action) => RunOnUi(() => ExecuteShortcut(action));
    private void SystemMediaOnCommandReceived(object? sender, MediaTransportCommandEventArgs e) => RunOnUi(() =>
    {
        switch (e.Command)
        {
            case MediaTransportCommand.Play: ExecuteShortcut(ShortcutActions.Play); break;
            case MediaTransportCommand.Pause: ExecuteShortcut(ShortcutActions.Pause); break;
            case MediaTransportCommand.Stop: ExecuteShortcut(ShortcutActions.Stop); break;
            case MediaTransportCommand.Next: ExecuteShortcut(ShortcutActions.Next); break;
            case MediaTransportCommand.Previous: ExecuteShortcut(ShortcutActions.Previous); break;
            case MediaTransportCommand.Seek when e.Position.HasValue: _ = SeekAsync(e.Position.Value.TotalSeconds); break;
        }
    });

    private void ScannerOnProgressChanged(object? sender, ScanProgress progress) => RunOnUi(() =>
    {
        IsScanning = !progress.IsComplete;
        StatusText = progress.IsComplete ? $"Scan complete · {progress.Added} added · {progress.Updated} updated · {progress.Failed} skipped" : $"Scanning {progress.Processed:N0} / {progress.Discovered:N0}";
    });

    private void QueueVolumeUpdate(double volume)
    {
        _volumeCancellation?.Cancel();
        _volumeCancellation?.Dispose();
        _volumeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = ApplyVolumeAsync(volume, _volumeCancellation.Token);
    }

    private async Task ApplyVolumeAsync(double volume, CancellationToken cancellationToken)
    {
        try
        {
            await _audio.SetVolumeAsync(volume, cancellationToken);
            await Task.Delay(250, cancellationToken);
            await _settings.UpdateAsync(x => x.Volume = volume, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { RunOnUi(() => StatusText = exception.Message); }
    }

    private void DebounceSearch()
    {
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _searchCancellation.Token;
        _ = Task.Run(async () => { try { await Task.Delay(220, token); await RefreshLibraryAsync(SearchText, token); } catch (OperationCanceledException) { } }, token);
    }

    private static string FormatTime(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source) { target.Clear(); foreach (var item in source) target.Add(item); }
    private static void RunOnUi(Action action) { if (Application.Current.Dispatcher.CheckAccess()) action(); else Application.Current.Dispatcher.BeginInvoke(action); }

    public async Task ShutdownAsync()
    {
        _lifetime.Cancel(); _searchCancellation?.Cancel(); _artworkCancellation?.Cancel(); _volumeCancellation?.Cancel(); _scanner.StopWatching();
        _shortcuts.ActionInvoked -= ShortcutsOnActionInvoked; _systemMedia.CommandReceived -= SystemMediaOnCommandReceived;
        await _settings.UpdateAsync(x => { x.Volume = Volume; x.QueuePanelVisible = QueueVisible; }, CancellationToken.None);
        _searchCancellation?.Dispose(); _artworkCancellation?.Dispose(); _volumeCancellation?.Dispose(); _lifetime.Dispose();
    }

    private sealed record LibraryGroups(IReadOnlyList<LibraryCardViewModel> Albums, IReadOnlyList<LibraryCardViewModel> Artists, IReadOnlyList<LibraryCardViewModel> Genres, IReadOnlyList<LibraryCardViewModel> Folders);
    private sealed record NavigationEntry(string View, string? CardKind, string? CardKey, bool IsCollectionDetail);
}
