using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.UI;
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
    private readonly DeveloperDiagnostics _diagnostics;
    private readonly ArtworkPropertyUpdateBatcher _artworkUpdates;
    private readonly ArtworkImageService _artworkImages;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, string?> _resolvedArtwork = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<NavigationEntry> _backHistory = new();
    private readonly Stack<NavigationEntry> _forwardHistory = new();
    private readonly Dictionary<string, CardSelection> _cardSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _trackSelections = new(StringComparer.Ordinal);
    private readonly PresentationCollectionCache<LibraryCardViewModel> _galleryViews = new();
    private readonly PresentationCollectionCache<Track> _trackViews = new();
    private readonly ConcurrentDictionary<long, Lazy<Task<IReadOnlyList<Track>>>> _playlistTrackLoads = new();
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _artworkCancellation;
    private CancellationTokenSource? _queueArtworkCancellation;
    private CancellationTokenSource? _sessionSaveCancellation;
    private CancellationTokenSource? _volumeCancellation;
    private IReadOnlyList<Track> _allTracks = [];
    private IReadOnlyList<LibraryCardViewModel> _activeGroups = [];
    private IReadOnlyList<LibraryCardViewModel> _sidebarCards = [];
    private ObservableCollection<Track> _browseTracks = [];
    private ObservableCollection<LibraryCardViewModel> _galleryGroups = [];
    private PresentationCollection<LibraryCardViewModel>? _activeGalleryPresentation;
    private PresentationCollection<Track>? _activeTrackPresentation;
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
    private bool _diagnosticsVisible;
    private bool _restoringSession;
    private int _albumTileSize = 172;
    private string _activeLyric = "Lyrics will appear here when available.";
    private LyricLineViewModel? _activeLyricLine;
    private bool _hasSyncedLyrics;
    private bool _isArtworkCacheBusy;
    private int _artworkCacheMegabytes = 512;
    private string _contentViewStateKey = "primary:Albums";
    private bool _restoringViewSelection;
    private string _artworkCacheStatus = "Calculating cache size…";

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
        ISystemMediaTransportService systemMedia,
        DeveloperDiagnostics diagnostics,
        ArtworkPropertyUpdateBatcher artworkUpdates,
        ArtworkImageService artworkImages)
    {
        _settings = settings; _repository = repository; _playlists = playlists; _scanner = scanner; _artwork = artwork;
        _audio = audio; _queue = queue; _sleepTimer = sleepTimer; _shortcuts = shortcuts; _systemMedia = systemMedia;
        _diagnostics = diagnostics; _artworkUpdates = artworkUpdates; _artworkImages = artworkImages;
        NavigateCommand = new RelayCommand(p => Navigate(p?.ToString()));
        SelectGroupCommand = new RelayCommand(p => SelectGroup(p as LibraryCardViewModel));
        CloseCollectionCommand = new RelayCommand(_ => CloseCollectionDetail());
        PlayGroupCommand = new AsyncRelayCommand(p => PlayGroupAsync(p as LibraryCardViewModel), p => p is LibraryCardViewModel card && card.TrackCount > 0);
        PlaySelectedCommand = new AsyncRelayCommand(_ => PlaySelectedAsync(), _ => SelectedTrack is not null);
        TogglePlaybackCommand = new AsyncRelayCommand(_ => TogglePlaybackAsync());
        NextCommand = new AsyncRelayCommand(_ => ChangeTrackAsync(_queue.Advance()));
        PreviousCommand = new AsyncRelayCommand(_ => HandlePreviousAsync());
        AddToQueueCommand = new RelayCommand(p => { if (p is Track track) _queue.Add([track]); });
        PlayNextCommand = new RelayCommand(p => { if (p is Track track) _queue.PlayNext([track]); });
        ToggleQueueCommand = new RelayCommand(_ => QueueVisible = !QueueVisible);
        ToggleShuffleCommand = new RelayCommand(_ => { _queue.Shuffle = !_queue.Shuffle; Raise(nameof(IsShuffleEnabled)); Raise(nameof(ShuffleText)); ScheduleSessionSave(); });
        CycleRepeatCommand = new RelayCommand(_ =>
        {
            _queue.RepeatMode = _queue.RepeatMode switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
            Raise(nameof(RepeatText)); Raise(nameof(IsRepeatEnabled)); Raise(nameof(IsRepeatOne));
            ScheduleSessionSave();
        });
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !_scanner.IsScanning && _settings.Current.LibraryFolders.Count > 0);
        RefreshArtworkCacheCommand = new AsyncRelayCommand(_ => RefreshArtworkCacheStatsAsync(), _ => !IsArtworkCacheBusy);
        ClearArtworkCacheCommand = new AsyncRelayCommand(_ => ClearArtworkCacheAsync(), _ => !IsArtworkCacheBusy);
        RebuildArtworkCacheCommand = new AsyncRelayCommand(_ => RebuildArtworkCacheAsync(), _ => !IsArtworkCacheBusy && _allTracks.Count > 0);
        UndoQueueCommand = new RelayCommand(_ => _queue.Undo());
        ClearQueueCommand = new RelayCommand(_ => _queue.Replace([]));
        LoveCommand = new AsyncRelayCommand(_ => ToggleLoveAsync(), _ => CurrentTrack is not null);
        SeekLyricCommand = new AsyncRelayCommand(p => SeekLyricAsync(p as LyricLineViewModel));
        PlayQueueEntryCommand = new AsyncRelayCommand(p => PlayQueueEntryAsync(p as QueueEntryViewModel));
        RemoveQueueEntryCommand = new AsyncRelayCommand(p => RemoveQueueEntryAsync(p as QueueEntryViewModel));
        PlayQueueEntryNextCommand = new RelayCommand(p => MoveQueueEntryNext(p as QueueEntryViewModel));
        ToggleDiagnosticsCommand = new RelayCommand(_ => DiagnosticsVisible = !DiagnosticsVisible);
        _audio.StateChanged += AudioOnStateChanged;
        _audio.TrackTransitioned += AudioOnTrackTransitioned;
        _audio.PlaybackEnded += AudioOnPlaybackEnded;
        _queue.Changed += QueueOnChanged;
        _scanner.ProgressChanged += ScannerOnProgressChanged;
        _scanner.ArtworkChanged += ScannerOnArtworkChanged;
        _sleepTimer.Expired += (_, _) => _ = _audio.StopAsync();
        _shortcuts.ActionInvoked += ShortcutsOnActionInvoked;
        _systemMedia.CommandReceived += SystemMediaOnCommandReceived;
    }

    public ObservableCollection<Track> BrowseTracks { get => _browseTracks; private set => Set(ref _browseTracks, value); }
    public ObservableCollection<LibraryCardViewModel> GalleryGroups { get => _galleryGroups; private set => Set(ref _galleryGroups, value); }
    public IReadOnlyList<LibraryCardViewModel> ActiveGroups { get => _activeGroups; private set => Set(ref _activeGroups, value); }
    public IReadOnlyList<LibraryCardViewModel> SidebarCards { get => _sidebarCards; private set => Set(ref _sidebarCards, value); }
    public ObservableCollection<LibraryCardViewModel> Albums { get; } = new ObservableRangeCollection<LibraryCardViewModel>();
    public ObservableCollection<LibraryCardViewModel> Artists { get; } = new ObservableRangeCollection<LibraryCardViewModel>();
    public ObservableCollection<LibraryCardViewModel> Genres { get; } = new ObservableRangeCollection<LibraryCardViewModel>();
    public ObservableCollection<LibraryCardViewModel> Folders { get; } = new ObservableRangeCollection<LibraryCardViewModel>();
    public ObservableCollection<LibraryCardViewModel> Playlists { get; } = new ObservableRangeCollection<LibraryCardViewModel>();
    public ObservableCollection<QueueEntryViewModel> Queue { get; } = new ObservableRangeCollection<QueueEntryViewModel>();
    public ObservableCollection<LyricLineViewModel> Lyrics { get; } = new ObservableRangeCollection<LyricLineViewModel>();
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new ObservableRangeCollection<AudioDeviceInfo>();

    public Track? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (!Set(ref _selectedTrack, value)) return;
            if (!_restoringViewSelection && value is not null)
                _trackSelections[_contentViewStateKey] = value.Path;
            (PlaySelectedCommand as AsyncRelayCommand)?.CanExecute(value);
        }
    }
    public Track? CurrentTrack { get => _currentTrack; private set { if (Set(ref _currentTrack, value)) { Raise(nameof(HasCurrentTrack)); Raise(nameof(CurrentTitle)); Raise(nameof(CurrentArtist)); Raise(nameof(CurrentArtworkPath)); Raise(nameof(LoveGlyph)); } } }
    public LibraryCardViewModel? SelectedCard
    {
        get => _selectedCard;
        private set
        {
            if (!Set(ref _selectedCard, value)) return;
            Raise(nameof(DetailTabTitle));
            Raise(nameof(HasDetailArtwork));
            Raise(nameof(ContentViewStateKey));
        }
    }
    public bool HasCurrentTrack => CurrentTrack is not null;
    public bool HasLibrary => _allTracks.Count > 0;
    public bool HasBrowseTracks => BrowseTracks.Count > 0;
    public int BrowseTrackSourceCount => _activeTrackPresentation?.Source.Count ?? BrowseTracks.Count;
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
            Raise(nameof(ContentViewStateKey));
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
            Raise(nameof(PrimaryViewStateKey)); Raise(nameof(ContentViewStateKey));
        }
    }
    public string PrimaryViewStateKey => $"primary:{CurrentView}";
    public string ContentViewStateKey => _contentViewStateKey;
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
    public bool DiagnosticsVisible { get => _diagnosticsVisible; set => Set(ref _diagnosticsVisible, value); }
    public bool IsArtworkCacheBusy
    {
        get => _isArtworkCacheBusy;
        private set
        {
            if (!Set(ref _isArtworkCacheBusy, value)) return;
            RefreshArtworkCacheCommand.RaiseCanExecuteChanged();
            ClearArtworkCacheCommand.RaiseCanExecuteChanged();
            RebuildArtworkCacheCommand.RaiseCanExecuteChanged();
        }
    }
    public string ArtworkCacheStatus { get => _artworkCacheStatus; private set => Set(ref _artworkCacheStatus, value); }
    public int ArtworkCacheMegabytes
    {
        get => _artworkCacheMegabytes;
        set
        {
            var normalized = Math.Clamp(value, 64, 4096);
            if (!Set(ref _artworkCacheMegabytes, normalized)) return;
            Raise(nameof(ArtworkCacheLimitText));
            _ = UpdateArtworkCacheLimitAsync(normalized);
        }
    }
    public string ArtworkCacheLimitText => $"{ArtworkCacheMegabytes:N0} MB maximum";
    public bool HasAudioDiagnostics => _audio.Diagnostics is not null;
    public string DiagnosticHeadline => _audio.Diagnostics is { IsBitPerfect: true } ? "Bit-perfect signal path" : _audio.Diagnostics is null ? "No active audio pipeline" : "Processed signal path";
    public string DiagnosticMode => _audio.Diagnostics is { } d ? $"{d.EffectiveMode} WASAPI · {d.PipelineMode}" : "Play a track to inspect the signal path";
    public string DiagnosticSource => _audio.Diagnostics?.SourceFormat?.ToString() ?? "—";
    public string DiagnosticOutput => _audio.Diagnostics?.OutputFormat?.ToString() ?? "—";
    public string DiagnosticDecoder => _audio.Diagnostics?.Decoder ?? "—";
    public string DiagnosticBuffer => $"{(_settings.Current.OutputProfiles.FirstOrDefault(x => x.DeviceId == _settings.Current.ActiveOutputDeviceId)?.BufferMilliseconds ?? 100)} ms · event-driven";
    public string DiagnosticReason => _audio.Diagnostics?.Reason ?? "Start playback to see decoder, format conversion, WASAPI mode, and bit-perfect status.";
    public bool IsShuffleEnabled => _queue.Shuffle;
    public string ShuffleText => IsShuffleEnabled ? "Shuffle on" : "Shuffle off";
    public bool IsRepeatEnabled => _queue.RepeatMode != RepeatMode.Off;
    public bool IsRepeatOne => _queue.RepeatMode == RepeatMode.One;
    public string RepeatText => _queue.RepeatMode switch { RepeatMode.One => "Repeat one", RepeatMode.All => "Repeat all", _ => "Repeat off" };
    public int AlbumTileSize
    {
        get => _albumTileSize;
        set
        {
            if (!Set(ref _albumTileSize, value)) return;
            Raise(nameof(GalleryItemWidth));
            Raise(nameof(GalleryItemHeight));
            _ = _settings.UpdateAsync(x => x.AlbumTileSize = value);
        }
    }
    public double GalleryItemWidth => AlbumTileSize + 14;
    public double GalleryItemHeight => AlbumTileSize + 94;
    public string ActiveLyric { get => _activeLyric; private set => Set(ref _activeLyric, value); }
    public LyricLineViewModel? ActiveLyricLine { get => _activeLyricLine; private set => Set(ref _activeLyricLine, value); }
    public bool HasLyrics => Lyrics.Count > 0;
    public bool HasSyncedLyrics { get => _hasSyncedLyrics; private set { if (Set(ref _hasSyncedLyrics, value)) { Raise(nameof(LyricsModeText)); Raise(nameof(LyricsHintText)); } } }
    public string LyricsModeText => HasSyncedLyrics ? "SYNCED" : "FULL LYRICS";
    public string LyricsHintText => HasSyncedLyrics ? "Click any line to jump to that moment" : "No timing data available";

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
    public AsyncRelayCommand RefreshArtworkCacheCommand { get; }
    public AsyncRelayCommand ClearArtworkCacheCommand { get; }
    public AsyncRelayCommand RebuildArtworkCacheCommand { get; }
    public RelayCommand UndoQueueCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public AsyncRelayCommand LoveCommand { get; }
    public AsyncRelayCommand SeekLyricCommand { get; }
    public AsyncRelayCommand PlayQueueEntryCommand { get; }
    public AsyncRelayCommand RemoveQueueEntryCommand { get; }
    public RelayCommand PlayQueueEntryNextCommand { get; }
    public RelayCommand ToggleDiagnosticsCommand { get; }

    public Task InitializeAsync() =>
        _diagnostics.MeasureAsync("startup", "view-model.initialize", InitializeCoreAsync);

    private async Task InitializeCoreAsync()
    {
        await _settings.InitializeAsync(_lifetime.Token);
        await _repository.InitializeAsync(_lifetime.Token);
        _volume = _settings.Current.Volume; Raise(nameof(Volume));
        _queueVisible = _settings.Current.QueuePanelVisible; Raise(nameof(QueueVisible));
        _albumTileSize = _settings.Current.AlbumTileSize; Raise(nameof(AlbumTileSize)); Raise(nameof(GalleryItemWidth)); Raise(nameof(GalleryItemHeight));
        _animationsEnabled = _settings.Current.AnimationsEnabled; Raise(nameof(AnimationsEnabled));
        _artworkCacheMegabytes = _settings.Current.ArtworkCacheMegabytes;
        Raise(nameof(ArtworkCacheMegabytes)); Raise(nameof(ArtworkCacheLimitText));
        await RefreshLibraryAsync(cancellationToken: _lifetime.Token);
        await RefreshArtworkCacheStatsAsync();
        Replace(OutputDevices, await _audio.GetOutputDevicesAsync(_lifetime.Token));
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
        await RestoreSessionAsync();
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

    private async Task RefreshArtworkCacheStatsAsync()
    {
        var stats = await _artwork.GetStatsAsync(_lifetime.Token);
        ArtworkCacheStatus =
            $"{FormatBytes(stats.TotalBytes)} · {stats.OriginalFiles:N0} originals · {stats.ThumbnailFiles:N0} thumbnails" +
            (stats.TemporaryFiles > 0 ? $" · {stats.TemporaryFiles:N0} temporary" : "");
    }

    private async Task ClearArtworkCacheAsync()
    {
        if (IsArtworkCacheBusy) return;
        IsArtworkCacheBusy = true;
        ArtworkCacheStatus = "Clearing artwork cache…";
        try
        {
            _artworkCancellation?.Cancel();
            _queueArtworkCancellation?.Cancel();
            _artworkImages.ClearMemoryCache();
            _resolvedArtwork.Clear();
            await _artwork.ClearAsync(_lifetime.Token);
            if (CurrentTrack is not null) CurrentTrack = CurrentTrack with { ArtworkPath = null };
            await RefreshLibraryAsync(SearchText, _lifetime.Token);
            await RefreshArtworkCacheStatsAsync();
        }
        finally { IsArtworkCacheBusy = false; }
    }

    private async Task RebuildArtworkCacheAsync()
    {
        if (IsArtworkCacheBusy) return;
        IsArtworkCacheBusy = true;
        ArtworkCacheStatus = "Clearing cache before rebuild…";
        try
        {
            _artworkCancellation?.Cancel();
            _queueArtworkCancellation?.Cancel();
            _artworkImages.ClearMemoryCache();
            _resolvedArtwork.Clear();
            await _artwork.ClearAsync(_lifetime.Token);

            var tracks = _allTracks.ToArray();
            var rebuilt = new ConcurrentBag<Track>();
            var completed = 0;
            await Parallel.ForEachAsync(
                tracks,
                new ParallelOptions
                {
                    CancellationToken = _lifetime.Token,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
                },
                async (track, cancellationToken) =>
                {
                    var artwork = await _artwork.GetOrCreateAsync(track.Path, cancellationToken);
                    if (artwork is not null) rebuilt.Add(track with { ArtworkPath = artwork });
                    var current = Interlocked.Increment(ref completed);
                    if (current == tracks.Length || current % 50 == 0)
                        RunOnUi(() => ArtworkCacheStatus = $"Rebuilding artwork… {current:N0} / {tracks.Length:N0}");
                });
            if (!rebuilt.IsEmpty)
                await _repository.UpsertBatchAsync(rebuilt.ToArray(), _lifetime.Token);
            await RefreshLibraryAsync(SearchText, _lifetime.Token);
            await RefreshArtworkCacheStatsAsync();
        }
        finally { IsArtworkCacheBusy = false; }
    }

    private async Task UpdateArtworkCacheLimitAsync(int megabytes)
    {
        await _settings.UpdateAsync(x => x.ArtworkCacheMegabytes = megabytes, _lifetime.Token);
        await _artwork.PruneAsync(_lifetime.Token);
        await RefreshArtworkCacheStatsAsync();
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1_073_741_824
            ? $"{bytes / 1_073_741_824d:0.00} GB"
            : $"{bytes / 1_048_576d:0.0} MB";

    private async Task RefreshLibraryAsync(string query = "", CancellationToken cancellationToken = default)
    {
        using var refreshScope = _diagnostics.Measure("library", "refresh",
            _diagnostics.Enabled ? new Dictionary<string, object?> { ["queryLength"] = query.Length } : null);
        var tracks = string.IsNullOrWhiteSpace(query)
            ? await _repository.GetAllAsync(cancellationToken)
            : await _repository.SearchAsync(query, 5000, cancellationToken);
        var groups = await Task.Run(() =>
        {
            using var scope = _diagnostics.Measure("library", "group-construction",
                _diagnostics.Enabled ? new Dictionary<string, object?> { ["tracks"] = tracks.Count } : null);
            return BuildGroups(tracks);
        }, cancellationToken);
        IReadOnlyList<LibraryCardViewModel> playlistCards;
        using (_diagnostics.Measure("library", "playlist-card-construction"))
            playlistCards = await BuildPlaylistCardsAsync(query, cancellationToken);
        RunOnUi(() =>
        {
            using var scope = _diagnostics.Measure("view", "library-result-application",
                _diagnostics.Enabled ? new Dictionary<string, object?>
                {
                    ["tracks"] = tracks.Count,
                    ["albums"] = groups.Albums.Count,
                    ["artists"] = groups.Artists.Count,
                    ["folders"] = groups.Folders.Count,
                    ["playlists"] = playlistCards.Count
                } : null);
            _allTracks = tracks;
            _galleryViews.Clear();
            _trackViews.Clear();
            _playlistTrackLoads.Clear();
            _activeGalleryPresentation = null;
            _activeTrackPresentation = null;
            Replace(Albums, groups.Albums); Replace(Artists, groups.Artists); Replace(Genres, groups.Genres); Replace(Folders, groups.Folders); Replace(Playlists, playlistCards);
            StatusText = tracks.Count == 0 ? (_settings.Current.LibraryFolders.Count == 0 ? "Add a music folder to begin" : "No matching tracks") : $"{tracks.Count:N0} tracks · {groups.Albums.Count:N0} albums · {groups.Artists.Count:N0} artists";
            Raise(nameof(HasLibrary));
            ApplyCurrentView(true);
        });
    }

    private static LibraryGroups BuildGroups(IReadOnlyList<Track> tracks)
    {
        var indexed = tracks.Select((track, index) => new IndexedTrack(index, track)).ToArray();
        var albums = indexed.GroupBy(x => new { Album = x.Track.DisplayAlbum, Artist = string.IsNullOrWhiteSpace(x.Track.AlbumArtist) ? x.Track.DisplayArtist : x.Track.AlbumArtist })
            .Select(group =>
            {
                var indexes = group.OrderBy(x => x.Track.DiscNumber).ThenBy(x => x.Track.TrackNumber).ThenBy(x => x.Track.Title).Select(x => x.Index).ToArray();
                return Card("Album", group.Key.Artist + "\0" + group.Key.Album, group.Key.Album,
                    group.Max(x => x.Track.Year) is > 0 and var year ? $"{group.Key.Artist} · {year}" : group.Key.Artist,
                    tracks, indexes);
            })
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var artists = indexed.SelectMany(item => SplitValues(item.Track.DisplayArtist).Select(artist => (Artist: artist, Item: item)))
            .GroupBy(x => x.Artist, StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var albumCount = group.Select(x => x.Item.Track.DisplayAlbum).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
                var indexes = group.OrderBy(x => x.Item.Track.Year).ThenBy(x => x.Item.Track.Album).ThenBy(x => x.Item.Track.TrackNumber).Select(x => x.Item.Index).ToArray();
                return Card("Artist", group.Key, group.Key, albumCount == 1 ? "1 album" : $"{albumCount:N0} albums",
                    tracks, indexes);
            })
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var genres = indexed.SelectMany(item => SplitValues(item.Track.Genre, "Uncategorized").Select(genre => (Genre: genre, Item: item)))
            .GroupBy(x => x.Genre, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => Card(
                "Genre",
                group.Key,
                group.Key,
                "Genre",
                tracks,
                group.OrderBy(x => x.Item.Track.Artist).ThenBy(x => x.Item.Track.Album).ThenBy(x => x.Item.Track.TrackNumber).Select(x => x.Item.Index).ToArray()))
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var folders = indexed.GroupBy(x => Path.GetDirectoryName(x.Track.Path) ?? x.Track.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => Card("Folder", group.Key, Path.GetFileName(group.Key.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : group.Key,
                group.Key, tracks, group.OrderBy(x => x.Track.Path, StringComparer.OrdinalIgnoreCase).Select(x => x.Index).ToArray(), group.Key))
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new LibraryGroups(albums, artists, genres, folders);
    }

    private async Task<IReadOnlyList<LibraryCardViewModel>> BuildPlaylistCardsAsync(string query, CancellationToken cancellationToken)
    {
        var result = new List<LibraryCardViewModel>();
        foreach (var summary in await _playlists.GetSummariesAsync(cancellationToken))
        {
            var playlist = summary.Playlist;
            if (!string.IsNullOrWhiteSpace(query) && !playlist.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
            result.Add(new LibraryCardViewModel
            {
                Kind = "Playlist", Key = playlist.Id.ToString(), PlaylistId = playlist.Id, Title = playlist.Name,
                Subtitle = playlist.Kind == PlaylistKind.Smart ? "Smart playlist" : "Playlist",
                Detail = summary.TrackCount == 1 ? "1 track" : $"{summary.TrackCount:N0} tracks",
                TrackCount = summary.TrackCount,
                RepresentativeTrack = summary.RepresentativeTrack,
                ArtworkPath = ExistingArtwork(summary.RepresentativeTrack)
            });
        }
        return result.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static LibraryCardViewModel Card(
        string kind,
        string key,
        string title,
        string subtitle,
        IReadOnlyList<Track> source,
        IReadOnlyList<int> trackIndexes,
        string detail = "") => new()
    {
        Kind = kind,
        Key = key,
        Title = title,
        Subtitle = subtitle,
        Detail = detail,
        TrackIndexes = trackIndexes,
        TrackCount = trackIndexes.Count,
        RepresentativeTrack = trackIndexes.Count > 0 ? source[trackIndexes[0]] : null,
        ArtworkPath = ExistingArtwork(trackIndexes.Count > 0 ? source[trackIndexes[0]] : null)
    };

    private static string? ExistingArtwork(Track? track) => track?.ArtworkPath is { Length: > 0 } path && File.Exists(path) ? path : null;

    private static IEnumerable<string> SplitValues(string? value, string fallback = "Unknown artist")
    {
        if (string.IsNullOrWhiteSpace(value)) return [fallback];
        var values = value.Split([';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        return values.Length == 0 ? [fallback] : values;
    }

    private void RestartActiveArtworkResolution()
    {
        using var scope = _diagnostics.Measure("artwork", "resolution-queue-build");
        _artworkCancellation?.Cancel(); _artworkCancellation?.Dispose();
        _artworkCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _artworkCancellation.Token;
        var planned = ArtworkResolutionPlanner.ForActiveView(
            CurrentView,
            IsCollectionDetailOpen,
            SelectedCard,
            GalleryGroups,
            SidebarCards);
        var cards = planned
            .Where(x => x.ArtworkPath is null && x.RepresentativeTrack is not null)
            .ToArray();
        if (_diagnostics.Enabled)
            _diagnostics.Mark("artwork", "resolution-started", new Dictionary<string, object?>
            {
                ["view"] = CurrentView,
                ["detailOpen"] = IsCollectionDetailOpen,
                ["plannedCards"] = planned.Count,
                ["unresolvedCards"] = cards.Length
            });
        _ = ResolveCardArtworkAsync(cards, token);
    }

    private async Task ResolveCardArtworkAsync(IReadOnlyList<LibraryCardViewModel> cards, CancellationToken cancellationToken)
    {
        try
        {
            await Parallel.ForEachAsync(cards, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (card, ct) =>
            {
                var path = await ResolveArtworkAsync(card.RepresentativeTrack!, ct);
                if (path is not null)
                    _artworkUpdates.Enqueue(
                        () =>
                        {
                            card.ArtworkPath = path;
                            if (ReferenceEquals(card, SelectedCard)) Raise(nameof(HasDetailArtwork));
                        },
                        ct);
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
        using var scope = _diagnostics.Measure("navigation", "command-application",
            _diagnostics.Enabled ? new Dictionary<string, object?> { ["from"] = CurrentView, ["to"] = view } : null);
        var previous = CaptureNavigation();
        IsCollectionDetailOpen = false;
        CurrentView = Views.First(x => x.Equals(view, StringComparison.OrdinalIgnoreCase));
        Raise(nameof(ViewTitle));
        ApplyCurrentView(false);
        RecordNavigation(previous);
    }

    private void ApplyCurrentView(bool resetSelection)
    {
        using var scope = _diagnostics.Measure("view", "tab-application",
            _diagnostics.Enabled ? new Dictionary<string, object?> { ["view"] = CurrentView, ["resetSelection"] = resetSelection } : null);
        if (resetSelection && IsCollectionDetailOpen) IsCollectionDetailOpen = false;
        switch (CurrentView)
        {
            case "Albums": ViewSubtitle = $"{Albums.Count:N0} albums in your library"; SetActiveGroups(Albums); RestoreGallerySelection(Albums); break;
            case "Artists": ViewSubtitle = $"{Artists.Count:N0} artists in your library"; SetActiveGroups(Artists); RestoreGallerySelection(Artists); break;
            case "Genres": ViewSubtitle = $"{Genres.Count:N0} genres in your library"; SetActiveGroups(Genres); RestoreGallerySelection(Genres); break;
            case "Folders": ViewSubtitle = $"{Folders.Count:N0} folders across {_settings.Current.LibraryFolders.Count:N0} sources"; SidebarCards = Folders; SelectDefault(SidebarCards, resetSelection); break;
            case "Playlists": ViewSubtitle = $"{Playlists.Count:N0} saved and smart playlists"; SidebarCards = Playlists; SelectDefault(SidebarCards, resetSelection); break;
            case "Favorites":
                ViewSubtitle = "Tracks you have marked as loved";
                SetBrowseTracks(_allTracks.Where(x => x.IsLoved).OrderBy(x => x.Artist).ThenBy(x => x.Album).ThenBy(x => x.TrackNumber), "Favorites", $"{_allTracks.Count(x => x.IsLoved):N0} loved tracks", PrimaryViewStateKey);
                break;
            case "Songs":
                ViewSubtitle = $"{_allTracks.Count:N0} tracks, stored offline";
                SetBrowseTracks(_allTracks, "All songs", StatusText, PrimaryViewStateKey, initialCount: 500);
                break;
            case "Now Playing":
                SetContentViewStateKey(PrimaryViewStateKey);
                ViewSubtitle = CurrentTrack is null ? "Choose a track to begin" : CurrentArtist;
                break;
        }
        RestartActiveArtworkResolution();
    }

    private void SelectDefault(IReadOnlyList<LibraryCardViewModel> cards, bool reset)
    {
        _cardSelections.TryGetValue(CurrentView, out var remembered);
        var selected = remembered is not null
            ? cards.FirstOrDefault(x => x.Key == remembered.Key && x.Kind == remembered.Kind)
            : null;
        SelectGroupCore(selected ?? cards.FirstOrDefault(), false, rememberSelection: false);
        if (cards.Count == 0)
            SetBrowseTracks(
                [],
                $"No {CurrentView.ToLowerInvariant()}",
                string.IsNullOrWhiteSpace(SearchText) ? "This view will populate as your library is scanned." : "No results match your search.",
                PrimaryViewStateKey);
    }

    private void SelectGroup(LibraryCardViewModel? card)
    {
        if (card is null) return;
        var previous = CaptureNavigation();
        SelectGroupCore(card, CurrentView is "Albums" or "Artists" or "Genres");
        RestartActiveArtworkResolution();
        RecordNavigation(previous);
    }

    private void SelectGroupCore(LibraryCardViewModel? card, bool openCollectionDetail, bool rememberSelection = true)
    {
        if (card is null) return;
        if (SelectedCard is not null) SelectedCard.IsSelected = false;
        SelectedCard = card;
        card.IsSelected = true;
        if (rememberSelection)
            _cardSelections[CurrentView] = new CardSelection(card.Kind, card.Key);
        var subtitle = string.IsNullOrWhiteSpace(card.Detail) ? $"{card.Subtitle} · {card.CountText}" : $"{card.Detail} · {card.CountText}";
        var tracks = TryGetCardTracks(card);
        if (tracks is null)
        {
            SetContentViewStateKey(CollectionViewStateKey(CurrentView, card));
            _activeTrackPresentation = null;
            BrowseTracks = [];
            SetSelectedTrackForView(null);
            SelectedGroupTitle = card.Title;
            SelectedGroupSubtitle = $"Loading {card.CountText}…";
            Raise(nameof(HasBrowseTracks));
            _ = LoadSelectedPlaylistAsync(card, CurrentView, subtitle);
        }
        else
        {
            SetBrowseTracks(tracks, card.Title, subtitle);
        }
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
        RestartActiveArtworkResolution();
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
        var presentation = _galleryViews.GetOrCreate(
            PrimaryViewStateKey,
            () => groups,
            28,
            out var cacheHit);
        using var scope = _diagnostics.Measure("view", "gallery-application",
            _diagnostics.Enabled ? new Dictionary<string, object?>
            {
                ["groups"] = presentation.Source.Count,
                ["materialized"] = presentation.Items.Count,
                ["cacheHit"] = cacheHit
            } : null);
        _activeGalleryPresentation = presentation;
        ActiveGroups = presentation.Source;
        GalleryGroups = presentation.Items;
    }

    public void LoadMoreGalleryGroups()
    {
        if (_activeGalleryPresentation is null || GalleryGroups.Count >= ActiveGroups.Count) return;
        using var scope = _diagnostics.Measure("view", "gallery-page-application",
            _diagnostics.Enabled ? new Dictionary<string, object?> { ["before"] = GalleryGroups.Count, ["total"] = ActiveGroups.Count } : null);
        PresentationCollectionCache<LibraryCardViewModel>.EnsureMaterialized(
            _activeGalleryPresentation,
            GalleryGroups.Count + 28);
        RestartActiveArtworkResolution();
    }

    public void EnsureGalleryGroupsLoaded(int count)
    {
        if (_activeGalleryPresentation is null
            || !PresentationCollectionCache<LibraryCardViewModel>.EnsureMaterialized(_activeGalleryPresentation, count))
            return;
        RestartActiveArtworkResolution();
    }

    private void RestoreGallerySelection(IReadOnlyList<LibraryCardViewModel> cards)
    {
        if (SelectedCard is not null) SelectedCard.IsSelected = false;
        _cardSelections.TryGetValue(CurrentView, out var remembered);
        SelectedCard = remembered is null
            ? null
            : cards.FirstOrDefault(x => x.Kind == remembered.Kind && x.Key == remembered.Key);
        if (SelectedCard is not null) SelectedCard.IsSelected = true;
        SetContentViewStateKey(PrimaryViewStateKey);
        _activeTrackPresentation = _trackViews.GetOrCreate(
            PrimaryViewStateKey,
            static () => Array.Empty<Track>(),
            int.MaxValue,
            out _);
        BrowseTracks = _activeTrackPresentation.Items;
        SetSelectedTrackForView(null);
        Raise(nameof(HasBrowseTracks));
    }

    private void SetBrowseTracks(IEnumerable<Track> tracks, string title, string subtitle) =>
        SetBrowseTracks(
            tracks,
            title,
            subtitle,
            SelectedCard is null ? PrimaryViewStateKey : CollectionViewStateKey(CurrentView, SelectedCard));

    private void SetBrowseTracks(
        IEnumerable<Track> tracks,
        string title,
        string subtitle,
        string contentStateKey,
        int initialCount = int.MaxValue)
    {
        var presentation = _trackViews.GetOrCreate(
            contentStateKey,
            () => tracks as IReadOnlyList<Track> ?? tracks.ToArray(),
            initialCount,
            out var cacheHit);
        using var scope = _diagnostics.Measure("view", "track-list-application",
            _diagnostics.Enabled ? new Dictionary<string, object?>
            {
                ["tracks"] = presentation.Source.Count,
                ["cacheHit"] = cacheHit
            } : null);
        SetContentViewStateKey(contentStateKey);
        _activeTrackPresentation = presentation;
        BrowseTracks = presentation.Items;
        SelectedGroupTitle = title; SelectedGroupSubtitle = subtitle;
        _trackSelections.TryGetValue(contentStateKey, out var selectedPath);
        SetSelectedTrackForView(
            selectedPath is null
                ? BrowseTracks.FirstOrDefault()
                : BrowseTracks.FirstOrDefault(x => x.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)) ?? BrowseTracks.FirstOrDefault());
        Raise(nameof(HasBrowseTracks));
    }

    public void LoadMoreBrowseTracks()
    {
        if (_activeTrackPresentation is null
            || !PresentationCollectionCache<Track>.EnsureMaterialized(
                _activeTrackPresentation,
                BrowseTracks.Count + 500))
            return;
        Raise(nameof(HasBrowseTracks));
    }

    public void EnsureBrowseTracksLoaded(int count)
    {
        if (_activeTrackPresentation is null
            || !PresentationCollectionCache<Track>.EnsureMaterialized(_activeTrackPresentation, count))
            return;
        Raise(nameof(HasBrowseTracks));
    }

    private void SetSelectedTrackForView(Track? track)
    {
        _restoringViewSelection = true;
        try { SelectedTrack = track; }
        finally { _restoringViewSelection = false; }
    }

    private void SetContentViewStateKey(string key)
    {
        if (_contentViewStateKey == key) return;
        _contentViewStateKey = key;
        Raise(nameof(ContentViewStateKey));
    }

    private static string CollectionViewStateKey(string view, LibraryCardViewModel card) =>
        $"collection:{view}:{card.Kind}:{card.Key}";

    private IReadOnlyList<Track>? TryGetCardTracks(LibraryCardViewModel card)
    {
        if (card.PlaylistId is not { } playlistId)
            return new IndexedReadOnlyList<Track>(_allTracks, card.TrackIndexes);
        if (!_playlistTrackLoads.TryGetValue(playlistId, out var load)
            || !load.IsValueCreated
            || !load.Value.IsCompletedSuccessfully)
            return null;
        return load.Value.Result;
    }

    private async Task<IReadOnlyList<Track>> GetCardTracksAsync(LibraryCardViewModel card)
    {
        if (card.PlaylistId is not { } playlistId)
            return new IndexedReadOnlyList<Track>(_allTracks, card.TrackIndexes);
        var load = _playlistTrackLoads.GetOrAdd(
            playlistId,
            id => new Lazy<Task<IReadOnlyList<Track>>>(
                () => _playlists.GetTracksAsync(id, _lifetime.Token),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try { return await load.Value; }
        catch
        {
            _playlistTrackLoads.TryRemove(new KeyValuePair<long, Lazy<Task<IReadOnlyList<Track>>>>(playlistId, load));
            throw;
        }
    }

    private async Task LoadSelectedPlaylistAsync(LibraryCardViewModel card, string view, string subtitle)
    {
        try
        {
            var tracks = await GetCardTracksAsync(card);
            RunOnUi(() =>
            {
                if (CurrentView != view || SelectedCard?.PlaylistId != card.PlaylistId) return;
                var key = CollectionViewStateKey(view, card);
                _trackViews.Remove(key);
                SetBrowseTracks(tracks, card.Title, subtitle, key);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { RunOnUi(() => StatusText = exception.Message); }
    }

    private async Task PlayGroupAsync(LibraryCardViewModel? card)
    {
        if (card is null || card.TrackCount == 0) return;
        var tracks = await GetCardTracksAsync(card);
        if (tracks.Count == 0) return;
        SelectGroup(card);
        _queue.Replace(tracks, 0);
        await ChangeTrackAsync(tracks[0]);
    }

    private async Task ScanAsync()
    {
        if (_settings.Current.LibraryFolders.Count == 0) { StatusText = "Add a music folder first"; return; }
        try { IsScanning = true; await _scanner.ScanAsync(_settings.Current.LibraryFolders, _settings.Current.ExcludedFolders, _lifetime.Token); await RefreshLibraryAsync(SearchText, _lifetime.Token); }
        finally { IsScanning = false; }
    }

    private void ScannerOnArtworkChanged(string path) => _artworkImages.InvalidatePath(path);

    private async Task PlaySelectedAsync()
    {
        if (SelectedTrack is null) return;
        var source = _activeTrackPresentation?.Source
            ?? (BrowseTracks.Count > 0 ? BrowseTracks.ToArray() : _allTracks);
        var selectedIndex = Enumerable.Range(0, source.Count)
            .FirstOrDefault(index => ReferenceEquals(source[index], SelectedTrack)
                || source[index].Path.Equals(SelectedTrack.Path, StringComparison.OrdinalIgnoreCase));
        _queue.Replace(source, selectedIndex);
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

    private async Task HandlePreviousAsync()
    {
        if (_audio.Snapshot.Track is null) return;
        if (_audio.Snapshot.Position > TimeSpan.FromSeconds(3))
        {
            await CommitSeekAsync(0);
            return;
        }
        var previous = _queue.Previous();
        if (previous is null) return;
        if (_queue.CurrentIndex == 0 && CurrentTrack?.Path.Equals(previous.Path, StringComparison.OrdinalIgnoreCase) == true)
        {
            await CommitSeekAsync(0);
            return;
        }
        await ChangeTrackAsync(previous);
    }

    private async Task PlayQueueEntryAsync(QueueEntryViewModel? entry)
    {
        if (entry is null) return;
        await ChangeTrackAsync(_queue.Select(entry.Entry.Id));
    }

    private async Task RemoveQueueEntryAsync(QueueEntryViewModel? entry)
    {
        if (entry is null) return;
        var wasPlaying = entry.IsPlaying;
        if (!_queue.Remove(entry.Entry.Id)) return;
        if (!wasPlaying) return;
        if (_queue.Current is { } replacement) await ChangeTrackAsync(replacement);
        else await _audio.StopAsync(_lifetime.Token);
    }

    private void MoveQueueEntryNext(QueueEntryViewModel? entry)
    {
        if (entry is null || _queue.Items.Count < 2) return;
        var from = _queue.Items.ToList().FindIndex(x => x.Id == entry.Entry.Id);
        var target = Math.Min(_queue.Items.Count - 1, _queue.CurrentIndex + 1);
        if (from >= 0 && from != target) _queue.Move(from, target);
    }

    public void MoveQueueEntry(Guid sourceId, Guid targetId)
    {
        var items = _queue.Items.ToList();
        var from = items.FindIndex(x => x.Id == sourceId);
        var to = items.FindIndex(x => x.Id == targetId);
        if (from >= 0 && to >= 0) _queue.Move(from, to);
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
        ActiveLyricLine = null;
        HasSyncedLyrics = false;
        if (string.IsNullOrWhiteSpace(track.Lyrics))
        {
            Replace(Lyrics, []);
            ActiveLyric = "No lyrics found. Place an .lrc file beside the track.";
            Raise(nameof(HasLyrics));
            return;
        }
        var synced = LrcParser.Parse(track.Lyrics);
        HasSyncedLyrics = synced.Lines.Count > 0;
        if (!HasSyncedLyrics)
        {
            Replace(
                Lyrics,
                track.Lyrics.Replace("\r\n", "\n")
                    .Split('\n')
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(text => new LyricLineViewModel(new LyricLine(TimeSpan.Zero, null, text.Trim(), []), false)));
        }
        else
        {
            Replace(
                Lyrics,
                synced.Lines
                    .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                    .Select(line => new LyricLineViewModel(line)));
        }
        ActiveLyric = Lyrics.FirstOrDefault()?.Text ?? "No lyrics";
        Raise(nameof(HasLyrics));
        UpdateLyricsPosition(_audio.Snapshot.Position);
    }

    private async Task SeekLyricAsync(LyricLineViewModel? line)
    {
        if (line is not { CanSeek: true } || CurrentTrack is null) return;
        await CommitSeekAsync(line.Line.Start.TotalSeconds);
        UpdateLyricsPosition(line.Line.Start);
    }

    private void UpdateLyricsPosition(TimeSpan position)
    {
        LyricLineViewModel? active = null;
        foreach (var line in Lyrics)
        {
            line.UpdatePosition(position);
            if (line.IsActive) active = line;
        }
        ActiveLyricLine = active;
        if (active is null) return;
        ActiveLyric = active.Text;
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
        Raise(nameof(HasAudioDiagnostics)); Raise(nameof(DiagnosticHeadline)); Raise(nameof(DiagnosticMode)); Raise(nameof(DiagnosticSource)); Raise(nameof(DiagnosticOutput)); Raise(nameof(DiagnosticDecoder)); Raise(nameof(DiagnosticBuffer)); Raise(nameof(DiagnosticReason));
        UpdateLyricsPosition(snapshot.Position);
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
        Replace(Queue, _queue.Items.Select(item =>
        {
            var artwork = ExistingArtwork(item.Track);
            if (artwork is null && _resolvedArtwork.TryGetValue(item.Track.Path, out var cached)) artwork = cached;
            return new QueueEntryViewModel(item, artwork);
        }));
        Raise(nameof(HasQueue));
        Raise(nameof(IsShuffleEnabled)); Raise(nameof(ShuffleText)); Raise(nameof(IsRepeatEnabled)); Raise(nameof(IsRepeatOne)); Raise(nameof(RepeatText));
        _systemMedia.Update(_audio.Snapshot, HasPreviousTrack(), HasNextTrack());
        StartQueueArtworkResolution();
        if (!_restoringSession) ScheduleSessionSave();
    });

    private async Task RestoreSessionAsync()
    {
        var session = _settings.Current.PlaybackSession;
        if (Views.Contains(session.LastView, StringComparer.OrdinalIgnoreCase))
        {
            CurrentView = session.LastView;
            IsCollectionDetailOpen = false;
            ApplyCurrentView(true);
        }
        var byPath = _allTracks.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var restored = session.QueuePaths.Where(byPath.ContainsKey).Select(x => byPath[x]).ToArray();
        if (restored.Length == 0) return;
        _restoringSession = true;
        try
        {
            _queue.Shuffle = session.Shuffle;
            _queue.RepeatMode = session.RepeatMode;
            _queue.Replace(restored, Math.Clamp(session.CurrentIndex, 0, restored.Length - 1));
            if (!_settings.Current.ResumeOnStartup || _queue.Current is not { } track) return;
            var artwork = await ResolveArtworkAsync(track, _lifetime.Token);
            if (artwork is not null) track = track with { ArtworkPath = artwork };
            await _audio.LoadAsync(track, _lifetime.Token);
            if (session.PositionSeconds > 0 && session.PositionSeconds < track.Duration.TotalSeconds)
                await _audio.SeekAsync(TimeSpan.FromSeconds(session.PositionSeconds), _lifetime.Token);
            await _audio.QueueNextAsync(PeekUpcomingTrack(), _lifetime.Token);
            LoadLyrics(track);
            if (session.WasPlaying) await _audio.PlayAsync(_lifetime.Token);
        }
        finally { _restoringSession = false; }
    }

    private void ScheduleSessionSave()
    {
        _sessionSaveCancellation?.Cancel();
        _sessionSaveCancellation?.Dispose();
        _sessionSaveCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _sessionSaveCancellation.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(500, token); await SaveSessionAsync(token); }
            catch (OperationCanceledException) { }
        }, token);
    }

    private Task SaveSessionAsync(CancellationToken cancellationToken)
    {
        var paths = _queue.Items.Select(x => x.Track.Path).ToList();
        var index = Math.Max(0, _queue.CurrentIndex);
        var snapshot = _audio.Snapshot;
        var position = snapshot.Position.TotalSeconds;
        var playing = snapshot.State == PlaybackState.Playing;
        var shuffle = _queue.Shuffle;
        var repeat = _queue.RepeatMode;
        var view = CurrentView;
        return _settings.UpdateAsync(settings =>
        {
            settings.PlaybackSession.QueuePaths = paths;
            settings.PlaybackSession.CurrentIndex = index;
            settings.PlaybackSession.PositionSeconds = position;
            settings.PlaybackSession.WasPlaying = playing;
            settings.PlaybackSession.Shuffle = shuffle;
            settings.PlaybackSession.RepeatMode = repeat;
            settings.PlaybackSession.LastView = view;
        }, cancellationToken);
    }

    private void StartQueueArtworkResolution()
    {
        _queueArtworkCancellation?.Cancel();
        _queueArtworkCancellation?.Dispose();
        _queueArtworkCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _queueArtworkCancellation.Token;
        var groups = Queue.Where(x => x.ArtworkPath is null).GroupBy(x => QueueArtworkKey(x.Track), StringComparer.OrdinalIgnoreCase).Select(x => x.ToArray()).ToArray();
        if (groups.Length > 0) _ = ResolveQueueArtworkAsync(groups, token);
    }

    private async Task ResolveQueueArtworkAsync(IReadOnlyList<QueueEntryViewModel[]> groups, CancellationToken cancellationToken)
    {
        try
        {
            await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (entries, ct) =>
            {
                string? artwork = null;
                foreach (var entry in entries)
                {
                    artwork = await ResolveArtworkAsync(entry.Track, ct);
                    if (artwork is not null) break;
                }
                if (artwork is not null)
                    _artworkUpdates.Enqueue(
                        () => { foreach (var entry in entries) entry.ArtworkPath = artwork; },
                        ct);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { RunOnUi(() => StatusText = exception.Message); }
    }

    private static string QueueArtworkKey(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.Album)) return track.Path;
        var artist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.DisplayArtist : track.AlbumArtist;
        return artist + "\0" + track.DisplayAlbum;
    }

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
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        if (target is not ObservableRangeCollection<T> range)
            throw new InvalidOperationException($"{target.GetType().Name} does not support range replacement.");
        range.ReplaceRange(source);
    }
    private static void RunOnUi(Action action) { if (Application.Current.Dispatcher.CheckAccess()) action(); else Application.Current.Dispatcher.BeginInvoke(action); }

    public async Task ShutdownAsync()
    {
        _sessionSaveCancellation?.Cancel();
        await SaveSessionAsync(CancellationToken.None);
        _lifetime.Cancel(); _searchCancellation?.Cancel(); _artworkCancellation?.Cancel(); _queueArtworkCancellation?.Cancel(); _volumeCancellation?.Cancel(); _scanner.StopWatching();
        _scanner.ArtworkChanged -= ScannerOnArtworkChanged;
        _shortcuts.ActionInvoked -= ShortcutsOnActionInvoked; _systemMedia.CommandReceived -= SystemMediaOnCommandReceived;
        await _settings.UpdateAsync(x => { x.Volume = Volume; x.QueuePanelVisible = QueueVisible; }, CancellationToken.None);
        _searchCancellation?.Dispose(); _artworkCancellation?.Dispose(); _queueArtworkCancellation?.Dispose(); _sessionSaveCancellation?.Dispose(); _volumeCancellation?.Dispose(); _lifetime.Dispose();
    }

    private sealed record LibraryGroups(IReadOnlyList<LibraryCardViewModel> Albums, IReadOnlyList<LibraryCardViewModel> Artists, IReadOnlyList<LibraryCardViewModel> Genres, IReadOnlyList<LibraryCardViewModel> Folders);
    private sealed record NavigationEntry(string View, string? CardKind, string? CardKey, bool IsCollectionDetail);
    private sealed record CardSelection(string Kind, string Key);
    private readonly record struct IndexedTrack(int Index, Track Track);
}
