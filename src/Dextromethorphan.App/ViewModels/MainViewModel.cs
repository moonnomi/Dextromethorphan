using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.Library;
using Dextromethorphan.App.Lyrics;
using Dextromethorphan.App.UI;
using Dextromethorphan.App.UI.Controls;
using Dextromethorphan.App.UI.Views;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Lyrics;
using Dextromethorphan.Core.Library;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly string[] Views = ["Albums", "Artists", "Genres", "Songs", "Folders", "Playlists", "Favorites", "Missing", "Recently Added", "Recently Played", "Most Played", "Never Played", "History", "Now Playing"];
    private readonly ISettingsService _settings;
    private readonly ILibraryRepository _repository;
    private readonly IPlaylistRepository _playlists;
    private readonly ILibraryScanner _scanner;
    private readonly IArtworkCache _artwork;
    private readonly ITrackMetadataReader _metadataReader;
    private readonly IAudioEngine _audio;
    private readonly IPlaybackQueue _queue;
    private readonly ISleepTimerService _sleepTimer;
    private readonly IShortcutService _shortcuts;
    private readonly ISystemMediaTransportService _systemMedia;
    private readonly IApplicationLog _applicationLog;
    private readonly DeveloperDiagnostics _diagnostics;
    private readonly ArtworkPropertyUpdateBatcher _artworkUpdates;
    private readonly ArtworkImageService _artworkImages;
    private readonly DiagnosticsBundleExporter _diagnosticsBundles;
    private readonly UserDataBackupService _userDataBackups;
    private readonly DuplicateDetectionService _duplicates;
    private readonly AudioDecoderCapabilityService _decoderCapabilities;
    private readonly ReplayGainAnalysisService _replayGainAnalysis;
    private readonly LyricsDocumentService _lyricsDocuments;
    private readonly LrclibLyricsProvider _onlineLyrics;
    private readonly IMetadataEditService _metadataEditor;
    private readonly IMetadataMatchService _metadataMatcher;
    private readonly IPlaylistFileService _playlistFiles;
    private readonly IPlaylistBackupService _playlistBackups;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ArtworkResolutionState _resolvedArtwork = new();
    private readonly ConcurrentDictionary<string, string> _playbackFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<NavigationEntry> _backHistory = new();
    private readonly Stack<NavigationEntry> _forwardHistory = new();
    private readonly Dictionary<string, CardSelection> _cardSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _trackSelections = new(StringComparer.Ordinal);
    private readonly PresentationCollectionCache<LibraryCardViewModel> _galleryViews = new();
    private readonly PresentationCollectionCache<LibraryCardViewModel> _sidebarViews = new();
    private readonly PresentationCollectionCache<Track> _trackViews = new();
    private readonly LibraryGroupingIndex _groupingIndex = new();
    private readonly SemaphoreSlim _groupingGate = new(1, 1);
    private readonly object _pendingLibraryChangeGate = new();
    private readonly List<LibraryFileChange> _pendingLibraryChanges = [];
    private readonly ConcurrentDictionary<long, Lazy<Task<IReadOnlyList<Track>>>> _playlistTrackLoads = new();
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _quickFilterCancellation;
    private CancellationTokenSource? _artworkCancellation;
    private CancellationTokenSource? _artworkReconciliationCancellation;
    private CancellationTokenSource? _queueArtworkCancellation;
    private CancellationTokenSource? _sessionSaveCancellation;
    private CancellationTokenSource? _volumeCancellation;
    private CancellationTokenSource? _libraryChangeCancellation;
    private CancellationTokenSource? _replayGainAnalysisCancellation;
    private Task? _shellInitialization;
    private Task? _libraryInitialization;
    private Task? _activeScanTask;
    private Task? _shutdownTask;
    private Task _artworkResolutionTask = Task.CompletedTask;
    private Task _artworkReconciliationTask = Task.CompletedTask;
    private readonly DispatcherTimer _scheduledScanTimer;
    private DateTimeOffset _lastCompletedLibraryScan = DateTimeOffset.UtcNow;
    private bool _scheduledScanEnabled = true;
    private int _scheduledScanIntervalMinutes = 60;
    private bool _allowScheduledScanOnBattery;
    private bool _allowScheduledScanOnMeteredNetwork;
    private string _scheduledScanStatus = "Scheduled scan has not run yet.";
    private IReadOnlyList<Track> _allTracks = [];
    private IReadOnlyList<LibraryCardViewModel> _activeGroups = [];
    private IReadOnlyList<LibraryCardViewModel> _sidebarCards = [];
    private ObservableCollection<Track> _browseTracks = [];
    private ObservableCollection<LibraryCardViewModel> _galleryGroups = [];
    private PresentationCollection<LibraryCardViewModel>? _activeGalleryPresentation;
    private PresentationCollection<LibraryCardViewModel>? _activeSidebarPresentation;
    private PresentationCollection<Track>? _activeTrackPresentation;
    private Track? _selectedTrack;
    private IReadOnlyList<Track> _selectedTracks = [];
    private Track? _currentTrack;
    private IReadOnlyList<QueueEntryViewModel> _selectedQueueEntries = [];
    private LibraryCardViewModel? _selectedCard;
    private FolderTreeNodeViewModel? _selectedFolderNode;
    private string _currentView = "Albums";
    private string _viewSubtitle = "Your music, organized locally";
    private string _selectedGroupTitle = "All albums";
    private string _selectedGroupSubtitle = "Select a collection to see its tracks";
    private string _artistProfileText = "";
    private string _artistProfileAttribution = "";
    private string _artistDiscographyText = "";
    private string _searchText = "";
    private string _statusText = "Starting…";
    private string _playGlyph = "▶";
    private string _positionText = "0:00";
    private string _durationText = "0:00";
    private double _positionSeconds;
    private double _durationSeconds = 1;
    private double _volume = 0.82;
    private bool _isScanning;
    private int _scanDiscovered;
    private int _scanProcessed;
    private int _scanAdded;
    private int _scanUpdated;
    private int _scanFailed;
    private string _scanCurrentPath = "No scan running";
    private string _scanCurrentSource = "—";
    private bool _queueVisible = true;
    private bool _queuePanelCompact;
    private PanelDockSide _queuePanelDockSide = PanelDockSide.Right;
    private bool _isCollectionDetailOpen;
    private bool _isUserSeeking;
    private bool _animationsEnabled = true;
    private bool _visualizerEnabled;
    private bool _diagnosticsVisible;
    private bool _restoringSession;
    private bool _stopAfterCurrent;
    private bool _stopAfterQueue;
    private string _userNotice = "";
    private ToastNotification? _activeToast;
    private bool _isUserNoticeVisible;
    private readonly DispatcherTimer _noticeTimer;
    private int _albumTileSize = 172;
    private string _sortBy = "Title";
    private bool _sortDescending;
    private LibraryDensity _density = LibraryDensity.Comfortable;
    private string _quickFilter = "";
    private IReadOnlyList<string> _dashboardModules = ["Artwork", "Lyrics", "Metadata"];
    private string _nowPlayingInspectorTab = "Lyrics";
    private int _galleryColumnCount = 1;
    private readonly ObservableRangeCollection<GalleryRowViewModel> _galleryRows = [];
    private string _activeLyric = "Lyrics will appear here when available.";
    private LyricLineViewModel? _activeLyricLine;
    private bool _hasSyncedLyrics;
    private LyricsDocument? _currentLyricsDocument;
    private int _lyricsOffsetMilliseconds;
    private LyricsDisplayMode _lyricsDisplayMode = LyricsDisplayMode.Automatic;
    private double _lyricsFontSize = 24;
    private LyricsTextAlignment _lyricsAlignment = LyricsTextAlignment.Left;
    private double _lyricsLineSpacing = 1.15;
    private double _lyricsBlurStrength = 5;
    private bool _karaokeWordAnimation = true;
    private bool _onlineLyricsEnabled;
    private bool _metadataLookupEnabled;
    private bool _musicBrainzLookupEnabled;
    private bool _discogsLookupEnabled;
    private string _discogsUserToken = "";
    private bool _isOnlineLyricsBusy;
    private bool _lyricsEditorVisible;
    private string _lyricsEditorText = string.Empty;
    private string _lyricsStatus = "Local lyrics only";
    private IReadOnlyList<MetadataEditResult> _metadataUndo = [];
    private readonly Stack<PlaylistHistoryEntry> _playlistUndo = new();
    private readonly Stack<PlaylistHistoryEntry> _playlistRedo = new();
    private bool _isArtworkCacheBusy;
    private int _artworkCacheMegabytes = 512;
    private string _contentViewStateKey = "primary:Albums";
    private bool _restoringViewSelection;
    private bool _isLibraryReady;
    private bool _isSafeMode;
    private PlaybackState? _lastLoggedPlaybackState;
    private string? _lastLoggedTrackPath;
    private string? _lastLoggedAudioError;
    private string _artworkCacheStatus = "Calculating cache size…";

    private string _duplicateScanStatus =
        "Content analysis has not been run.";
    private AudioDeviceInfo? _selectedOutputDevice;
    private AudioDeviceCapabilities? _outputCapabilities;
    private string _outputProfileStatus =
        "Select an output to inspect its capabilities.";
    private bool _isOutputProfileBusy;
    private string _decoderCapabilityStatus =
        "Codec availability has not been checked.";
    private bool _isDecoderCapabilityBusy;
    private ReplayGainMode _replayGainMode = ReplayGainMode.Track;
    private double _replayGainPreampDb;
    private bool _preventClipping = true;
    private double _playbackSpeed = 1;
    private double _pitchSemitones;
    private bool _preservePitch = true;
    private bool _isReplayGainAnalysisBusy;
    private double _replayGainAnalysisProgress;
    private string _replayGainAnalysisStatus =
        "Loudness has not been analyzed in this session.";

    public MainViewModel(
        ISettingsService settings,
        ILibraryRepository repository,
        IPlaylistRepository playlists,
        ILibraryScanner scanner,
        IArtworkCache artwork,
        ITrackMetadataReader metadataReader,
        IAudioEngine audio,
        IPlaybackQueue queue,
        ISleepTimerService sleepTimer,
        IShortcutService shortcuts,
        ISystemMediaTransportService systemMedia,
        IApplicationLog applicationLog,
        DeveloperDiagnostics diagnostics,
        ArtworkPropertyUpdateBatcher artworkUpdates,
        ArtworkImageService artworkImages,
        DiagnosticsBundleExporter diagnosticsBundles,
        UserDataBackupService userDataBackups,
        DuplicateDetectionService duplicates,
        AudioDecoderCapabilityService decoderCapabilities,
        ReplayGainAnalysisService replayGainAnalysis,
        LyricsDocumentService lyricsDocuments,
        LrclibLyricsProvider onlineLyrics,
        IMetadataEditService metadataEditor,
        IMetadataMatchService metadataMatcher,
        IPlaylistFileService playlistFiles,
        IPlaylistBackupService playlistBackups)
    {
        _settings = settings; _repository = repository; _playlists = playlists; _scanner = scanner; _artwork = artwork; _metadataReader = metadataReader;
        _audio = audio; _queue = queue; _sleepTimer = sleepTimer; _shortcuts = shortcuts; _systemMedia = systemMedia;
        _applicationLog = applicationLog;
        _diagnostics = diagnostics; _artworkUpdates = artworkUpdates; _artworkImages = artworkImages;
        _diagnosticsBundles = diagnosticsBundles;
        _userDataBackups = userDataBackups;
        _duplicates = duplicates;
        _decoderCapabilities = decoderCapabilities;
        _replayGainAnalysis = replayGainAnalysis;
        _lyricsDocuments = lyricsDocuments;
        _onlineLyrics = onlineLyrics;
        _metadataEditor = metadataEditor;
        _metadataMatcher = metadataMatcher;
        _playlistFiles = playlistFiles;
        _playlistBackups = playlistBackups;
        SettingsWorkspace = new SettingsWorkspaceViewModel(
            _settings,
            _shortcuts,
            this);
        SettingsWorkspace.ThemeApplyRequested += (_, _) => ApplyWorkspaceTheme();
        OutputProfile.PropertyChanged += (_, _) =>
            SettingsWorkspace.NotifyOutputProfileDraftChanged();
        _scheduledScanTimer = new DispatcherTimer(
            TimeSpan.FromMinutes(1),
            DispatcherPriority.Background,
            ScheduledScanTimerOnTick,
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
        _noticeTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(3.2),
            DispatcherPriority.Background,
            NoticeTimerOnTick,
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
        DismissNoticeCommand = new RelayCommand(_ => DismissNotice());
        NavigateCommand = new RelayCommand(p => Navigate(p?.ToString()));
        SelectGroupCommand = new RelayCommand(p => SelectGroup(p as LibraryCardViewModel));
        CloseCollectionCommand = new RelayCommand(_ => CloseCollectionDetail());
        PlayGroupCommand = new AsyncRelayCommand(p => PlayGroupAsync(p as LibraryCardViewModel), p => p is LibraryCardViewModel card && card.TrackCount > 0);
        PlayGroupNextCommand = new AsyncRelayCommand(p => PlayGroupNextAsync(p as LibraryCardViewModel), p => p is LibraryCardViewModel card && card.TrackCount > 0);
        AddGroupToQueueCommand = new AsyncRelayCommand(p => AddGroupToQueueAsync(p as LibraryCardViewModel), p => p is LibraryCardViewModel card && card.TrackCount > 0);
        PlaySelectedCommand = new AsyncRelayCommand(_ => PlaySelectedAsync(), _ => SelectedTrack is not null);
        TogglePlaybackCommand = new AsyncRelayCommand(_ => TogglePlaybackAsync());
        NextCommand = new AsyncRelayCommand(_ => ChangeTrackAsync(_queue.Advance()));
        PreviousCommand = new AsyncRelayCommand(_ => HandlePreviousAsync());
        AddToQueueCommand = new RelayCommand(p => { if (p is Track track) _queue.Add([track]); });
        AddSelectedToQueueCommand = new RelayCommand(_ => { if (SelectedTracks.Count > 0) _queue.Add(SelectedTracks); });
        PlayNextCommand = new RelayCommand(p => { if (p is Track track) _queue.PlayNext([track]); });
        ToggleQueueCommand = new RelayCommand(_ => ToggleQueueVisibility());
        ToggleQueueCompactCommand = new RelayCommand(_ =>
            QueuePanelCompact = !QueuePanelCompact);
        ExpandQueueCommand = new RelayCommand(_ =>
        {
            QueueVisible = true;
            QueuePanelCompact = false;
        });
        SetQueueDockSideCommand = new RelayCommand(
            parameter => SetQueueDockSide(parameter));
        ToggleVisualizerCommand = new RelayCommand(
            _ => VisualizerEnabled = !VisualizerEnabled);
        ToggleShuffleCommand = new RelayCommand(_ => { _queue.Shuffle = !_queue.Shuffle; Raise(nameof(IsShuffleEnabled)); Raise(nameof(ShuffleText)); ScheduleSessionSave(); });
        CycleRepeatCommand = new RelayCommand(_ =>
        {
            _queue.RepeatMode = _queue.RepeatMode switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
            Raise(nameof(RepeatText)); Raise(nameof(IsRepeatEnabled)); Raise(nameof(IsRepeatOne));
            ScheduleSessionSave();
        });
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !_scanner.IsScanning && EnabledSourcePaths().Count > 0);
        RescanSourceCommand = new AsyncRelayCommand(p => RescanSourceAsync(p as LibrarySourceViewModel), p => p is LibrarySourceViewModel { Enabled: true } && !_scanner.IsScanning);
        ToggleLibrarySourceCommand = new AsyncRelayCommand(p => ToggleLibrarySourceAsync(p as LibrarySourceViewModel), p => p is LibrarySourceViewModel && !_scanner.IsScanning);
        ToggleSourceWatcherCommand = new AsyncRelayCommand(p => ToggleSourceWatcherAsync(p as LibrarySourceViewModel), p => p is LibrarySourceViewModel { Enabled: true });
        RemoveLibrarySourceCommand = new AsyncRelayCommand(p => RemoveLibrarySourceAsync(p as LibrarySourceViewModel), p => p is LibrarySourceViewModel && !_scanner.IsScanning);
        RemoveExclusionCommand = new AsyncRelayCommand(p => RemoveExclusionAsync(p?.ToString()), p => p is string);
        ToggleScanPauseCommand = new RelayCommand(
            _ =>
            {
                if (_scanner.State == ScanLifecycleState.Paused) _scanner.Resume();
                else _scanner.Pause();
                UpdateScanState();
            },
            _ => _scanner.State is ScanLifecycleState.Running or ScanLifecycleState.Paused);
        CancelScanCommand = new RelayCommand(
            _ =>
            {
                _scanner.Cancel();
                UpdateScanState();
            },
            _ => _scanner.State is ScanLifecycleState.Running or ScanLifecycleState.Paused);
        RefreshArtworkCacheCommand = new AsyncRelayCommand(_ => RefreshArtworkCacheStatsAsync(), _ => !IsArtworkCacheBusy);
        ClearArtworkCacheCommand = new AsyncRelayCommand(_ => ClearArtworkCacheAsync(), _ => !IsArtworkCacheBusy);
        RebuildArtworkCacheCommand = new AsyncRelayCommand(_ => RebuildArtworkCacheAsync(), _ => !IsArtworkCacheBusy && _allTracks.Count > 0);
        UndoQueueCommand = new RelayCommand(_ =>
        {
            var changed = _queue.Undo();
            ShowNotice(
                changed ? "Queue change undone" : "Nothing to undo",
                changed ? ToastSeverity.Success : ToastSeverity.Information);
        });
        RedoQueueCommand = new RelayCommand(_ =>
        {
            var changed = _queue.Redo();
            ShowNotice(
                changed ? "Queue change restored" : "Nothing to redo",
                changed ? ToastSeverity.Success : ToastSeverity.Information);
        });
        ClearQueueCommand = new RelayCommand(_ =>
        {
            if (_queue.Items.Count == 0) return;
            _queue.Replace([]);
            ShowNotice("Queue cleared", ToastSeverity.Success);
        });
        RemoveSelectedQueueCommand = new AsyncRelayCommand(_ => RemoveSelectedQueueAsync(), _ => SelectedQueueEntries.Count > 0);
        MoveSelectedQueueTopCommand = new RelayCommand(_ => MoveSelectedQueue(toTop: true), _ => SelectedQueueEntries.Any(entry => entry.CanReorder));
        MoveSelectedQueueBottomCommand = new RelayCommand(_ => MoveSelectedQueue(toTop: false), _ => SelectedQueueEntries.Any(entry => entry.CanReorder));
        ToggleStopAfterCurrentCommand = new RelayCommand(_ => ToggleStopMode(stopAfterCurrent: true));
        ToggleStopAfterQueueCommand = new RelayCommand(_ => ToggleStopMode(stopAfterCurrent: false));
        SetPlaybackSpeedCommand = new AsyncRelayCommand(p => SetPlaybackSpeedAsync(p));
        SetPitchCommand = new AsyncRelayCommand(p => SetPitchAsync(p));
        TogglePreservePitchCommand = new AsyncRelayCommand(_ => TogglePreservePitchAsync());
        ResetPlaybackProcessingCommand = new AsyncRelayCommand(_ => ResetPlaybackProcessingAsync());
        SaveTrackPlaybackOverrideCommand = new AsyncRelayCommand(_ => SaveTrackPlaybackOverrideAsync(), _ => CurrentTrack is not null);
        ClearTrackPlaybackOverrideCommand = new AsyncRelayCommand(_ => ClearTrackPlaybackOverrideAsync(), _ => CurrentTrackHasPlaybackOverride);
        SetCurrentRatingCommand = new AsyncRelayCommand(p => SetCurrentRatingAsync(p), _ => CurrentTrack is not null);
        AddBookmarkCommand = new AsyncRelayCommand(p => AddBookmarkAsync(p?.ToString()), _ => CurrentTrack is not null);
        ToggleBookmarkResumeCommand = new AsyncRelayCommand(_ => ToggleBookmarkResumeAsync());
        SeekBookmarkCommand = new AsyncRelayCommand(p => SeekBookmarkAsync(p as PlaybackBookmark), p => p is PlaybackBookmark);
        RenameBookmarkCommand = new AsyncRelayCommand(p => RenameBookmarkAsync(p), p => p is BookmarkRenameRequest);
        DeleteBookmarkCommand = new AsyncRelayCommand(p => DeleteBookmarkAsync(p as PlaybackBookmark), p => p is PlaybackBookmark);
        RemoveMissingTrackCommand = new AsyncRelayCommand(
            parameter => RemoveMissingTrackAsync(parameter as Track),
            parameter => parameter is Track { IsMissing: true });
        LoveCommand = new AsyncRelayCommand(_ => ToggleLoveAsync(), _ => CurrentTrack is not null);
        EditMetadataCommand = new AsyncRelayCommand(_ => EditMetadataAsync(), _ => SelectedTracks.Count > 0);
        UndoMetadataCommand = new AsyncRelayCommand(_ => UndoMetadataAsync(), _ => _metadataUndo.Count > 0);
        NewPlaylistCommand = new RelayCommand(_ => RequestPlaylistEdit((Playlist?)null));
        EditPlaylistCommand = new AsyncRelayCommand(p => EditPlaylistAsync(p as LibraryCardViewModel), p => p is LibraryCardViewModel { PlaylistId: not null });
        DuplicatePlaylistCommand = new AsyncRelayCommand(p => DuplicatePlaylistAsync(p as LibraryCardViewModel), p => p is LibraryCardViewModel { PlaylistId: not null });
        DeletePlaylistCommand = new AsyncRelayCommand(p => DeletePlaylistAsync(p as LibraryCardViewModel), p => p is LibraryCardViewModel { PlaylistId: not null });
        UndoPlaylistCommand = new AsyncRelayCommand(_ => UndoPlaylistAsync(), _ => _playlistUndo.Count > 0);
        RedoPlaylistCommand = new AsyncRelayCommand(_ => RedoPlaylistAsync(), _ => _playlistRedo.Count > 0);
        AddSelectedToPlaylistCommand = new AsyncRelayCommand(p => AddSelectedToPlaylistAsync(p as LibraryCardViewModel), p => p is LibraryCardViewModel { PlaylistId: not null } && SelectedTracks.Count > 0);
        SaveQueueAsPlaylistCommand = new RelayCommand(_ => RequestPlaylistEdit(new PlaylistEditContext(null, Queue.Select(entry => entry.Track).ToArray())));
        SeekLyricCommand = new AsyncRelayCommand(p => SeekLyricAsync(p as LyricLineViewModel));
        ReloadLyricsCommand = new AsyncRelayCommand(_ => ReloadLyricsAsync(), _ => CurrentTrack is not null);
        FetchOnlineLyricsCommand = new AsyncRelayCommand(_ => FetchOnlineLyricsAsync(), _ => CurrentTrack is not null && OnlineLyricsEnabled && !IsOnlineLyricsBusy);
        SelectLyricsSourceCommand = new AsyncRelayCommand(p => SelectLyricsSourceAsync(p as LyricsDocument), p => p is LyricsDocument);
        ToggleLyricsEditorCommand = new RelayCommand(_ => ToggleLyricsEditor(), _ => CurrentTrack is not null);
        SaveLyricsCommand = new AsyncRelayCommand(_ => SaveLyricsAsync(), _ => CurrentTrack is not null && LyricsEditorVisible);
        CancelLyricsEditorCommand = new RelayCommand(_ => LyricsEditorVisible = false);
        SeekChapterCommand = new AsyncRelayCommand(
            p => p is AudioChapter chapter
                ? CommitSeekAsync(chapter.Start.TotalSeconds)
                : Task.CompletedTask,
            p => p is AudioChapter);
        PlayQueueEntryCommand = new AsyncRelayCommand(p => PlayQueueEntryAsync(p as QueueEntryViewModel));
        RemoveQueueEntryCommand = new AsyncRelayCommand(p => RemoveQueueEntryAsync(p as QueueEntryViewModel));
        PlayQueueEntryNextCommand = new RelayCommand(p => MoveQueueEntryNext(p as QueueEntryViewModel));
        ToggleDiagnosticsCommand = new RelayCommand(_ => DiagnosticsVisible = !DiagnosticsVisible);
        ClearSearchHistoryCommand = new AsyncRelayCommand(_ => ClearSearchHistoryAsync());
        UseSearchHistoryCommand = new RelayCommand(parameter => { if (parameter is string value) SearchText = value; });
        SetQuickFilterCommand = new RelayCommand(parameter => QuickFilter = parameter?.ToString() ?? "");
        ClearQuickFilterCommand = new RelayCommand(_ => QuickFilter = "");
        ToggleSortDirectionCommand = new RelayCommand(_ => SortDescending = !SortDescending);
        ToggleTrackColumnCommand = new RelayCommand(parameter => ToggleTrackColumn(parameter?.ToString()));
        MoveTrackColumnCommand = new RelayCommand(parameter => MoveTrackColumn(parameter?.ToString()));
        SetTrackColumnWidthCommand = new RelayCommand(parameter => SetTrackColumnWidth(parameter?.ToString()));
        SetDensityCommand = new RelayCommand(parameter => SetDensity(parameter?.ToString()));
        SetAlbumTileSizeCommand = new RelayCommand(parameter => SetAlbumTileSize(parameter?.ToString()));
        ToggleDashboardModuleCommand = new RelayCommand(parameter => ToggleDashboardModule(parameter?.ToString()));
        SelectNowPlayingInspectorCommand = new RelayCommand(parameter => SelectNowPlayingInspector(parameter?.ToString()));
        _audio.StateChanged += AudioOnStateChanged;
        _audio.TrackTransitioned += AudioOnTrackTransitioned;
        _audio.PlaybackEnded += AudioOnPlaybackEnded;
        _audio.OutputDevicesChanged += AudioOnOutputDevicesChanged;
        _queue.Changed += QueueOnChanged;
        _scanner.ProgressChanged += ScannerOnProgressChanged;
        _scanner.FailureOccurred += ScannerOnFailureOccurred;
        _scanner.SourceStatusesChanged += ScannerOnSourceStatusesChanged;
        _scanner.FilesChanged += ScannerOnFilesChanged;
        _scanner.ArtworkChanged += ScannerOnArtworkChanged;
        _sleepTimer.Expired += (_, _) => _ = _audio.StopAsync();
        _shortcuts.ActionInvoked += ShortcutsOnActionInvoked;
        _systemMedia.CommandReceived += SystemMediaOnCommandReceived;
    }

    public ObservableCollection<Track> BrowseTracks { get => _browseTracks; private set => Set(ref _browseTracks, value); }
    public ObservableCollection<LibraryCardViewModel> GalleryGroups { get => _galleryGroups; private set => Set(ref _galleryGroups, value); }
    public ObservableCollection<GalleryRowViewModel> GalleryRows => _galleryRows;
    public IReadOnlyList<LibraryCardViewModel> ActiveGroups { get => _activeGroups; private set => Set(ref _activeGroups, value); }
    public IReadOnlyList<LibraryCardViewModel> SidebarCards { get => _sidebarCards; private set => Set(ref _sidebarCards, value); }
    public ObservableCollection<LibraryCardViewModel> Albums { get; } = new ObservableRangeCollection<LibraryCardViewModel>();
    public ObservableCollection<LibraryCardViewModel> Artists { get; } = new ObservableRangeCollection<LibraryCardViewModel>();
    public ObservableCollection<LibraryCardViewModel> Genres { get; } = new ObservableRangeCollection<LibraryCardViewModel>();
    public ObservableCollection<LibraryCardViewModel> Folders { get; } = new ObservableRangeCollection<LibraryCardViewModel>();
    public ObservableCollection<FolderTreeNodeViewModel> FolderTree { get; } = new ObservableRangeCollection<FolderTreeNodeViewModel>();
    public ObservableCollection<LibraryCardViewModel> Playlists { get; } = new ObservableRangeCollection<LibraryCardViewModel>();
    public ObservableCollection<QueueEntryViewModel> Queue { get; } = new ObservableRangeCollection<QueueEntryViewModel>();
    public ObservableCollection<QueueHistoryEntryViewModel> QueueHistory { get; } = new ObservableRangeCollection<QueueHistoryEntryViewModel>();
    public ObservableCollection<PlaybackBookmark> Bookmarks { get; } = new ObservableRangeCollection<PlaybackBookmark>();
    public ObservableCollection<LyricLineViewModel> Lyrics { get; } = new ObservableRangeCollection<LyricLineViewModel>();
    public ObservableCollection<LyricsDocument> LyricsSources { get; } = new ObservableRangeCollection<LyricsDocument>();
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new ObservableRangeCollection<AudioDeviceInfo>();
    public ObservableCollection<LibrarySourceViewModel> LibrarySources { get; } = new ObservableRangeCollection<LibrarySourceViewModel>();
    public ObservableCollection<string> LibraryExclusions { get; } = new ObservableRangeCollection<string>();
    public ObservableCollection<LibraryScanFailure> ScanFailures { get; } = new ObservableRangeCollection<LibraryScanFailure>();
    public ObservableCollection<DuplicateTrackGroup> DuplicateGroups { get; } = new ObservableRangeCollection<DuplicateTrackGroup>();
    public ObservableCollection<DecoderCapability> DecoderCapabilities { get; } = new ObservableRangeCollection<DecoderCapability>();
    public SettingsWorkspaceViewModel SettingsWorkspace { get; }
    public string DuplicateScanStatus { get => _duplicateScanStatus; private set => Set(ref _duplicateScanStatus, value); }
    public AudioOutputProfileDraft OutputProfile { get; } = new();
    public AudioDeviceInfo? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set => Set(ref _selectedOutputDevice, value);
    }
    public AudioDeviceCapabilities? OutputCapabilities
    {
        get => _outputCapabilities;
        private set
        {
            if (!Set(ref _outputCapabilities, value)) return;
            Raise(nameof(SupportedExclusiveFormats));
            Raise(nameof(OutputMixFormat));
            Raise(nameof(SupportsEventDrivenExclusive));
        }
    }
    public IReadOnlyList<AudioFormatInfo> SupportedExclusiveFormats =>
        OutputCapabilities?.SupportedExclusiveFormats ?? [];
    public string OutputMixFormat =>
        OutputCapabilities?.MixFormat.ToString() ?? "Not queried";
    public bool SupportsEventDrivenExclusive =>
        OutputCapabilities?.SupportsEventDrivenExclusive == true;
    public string OutputProfileStatus
    {
        get => _outputProfileStatus;
        private set => Set(ref _outputProfileStatus, value);
    }
    public bool IsOutputProfileBusy
    {
        get => _isOutputProfileBusy;
        private set => Set(ref _isOutputProfileBusy, value);
    }
    public string DecoderCapabilityStatus
    {
        get => _decoderCapabilityStatus;
        private set => Set(ref _decoderCapabilityStatus, value);
    }
    public bool IsDecoderCapabilityBusy
    {
        get => _isDecoderCapabilityBusy;
        private set => Set(ref _isDecoderCapabilityBusy, value);
    }
    public IReadOnlyList<ReplayGainMode> ReplayGainModes { get; } =
        Enum.GetValues<ReplayGainMode>();
    public ReplayGainMode ReplayGainMode
    {
        get => _replayGainMode;
        set => Set(ref _replayGainMode, value);
    }
    public double ReplayGainPreampDb
    {
        get => _replayGainPreampDb;
        set => Set(ref _replayGainPreampDb, Math.Clamp(value, -20, 20));
    }
    public bool PreventClipping
    {
        get => _preventClipping;
        set => Set(ref _preventClipping, value);
    }
    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set { if (Set(ref _playbackSpeed, Math.Clamp(value, 0.5, 1.5))) Raise(nameof(PlaybackProcessingText)); }
    }
    public double PitchSemitones
    {
        get => _pitchSemitones;
        set { if (Set(ref _pitchSemitones, Math.Clamp(value, -12, 12))) Raise(nameof(PlaybackProcessingText)); }
    }
    public bool PreservePitch
    {
        get => _preservePitch;
        set => Set(ref _preservePitch, value);
    }
    public bool IsReplayGainAnalysisBusy
    {
        get => _isReplayGainAnalysisBusy;
        private set => Set(ref _isReplayGainAnalysisBusy, value);
    }
    public double ReplayGainAnalysisProgress
    {
        get => _replayGainAnalysisProgress;
        private set => Set(ref _replayGainAnalysisProgress, value);
    }
    public string ReplayGainAnalysisStatus
    {
        get => _replayGainAnalysisStatus;
        private set => Set(ref _replayGainAnalysisStatus, value);
    }
    public IReadOnlyList<WasapiMode> WasapiModes { get; } =
        Enum.GetValues<WasapiMode>();
    public IReadOnlyList<SampleRatePolicy> SampleRatePolicies { get; } =
        Enum.GetValues<SampleRatePolicy>();
    public IReadOnlyList<int> SampleRates { get; } =
        [44_100, 48_000, 88_200, 96_000, 176_400, 192_000];
    public IReadOnlyList<BitDepthPolicy> BitDepthPolicies { get; } =
        Enum.GetValues<BitDepthPolicy>();
    public IReadOnlyList<int> BitDepths { get; } = [16, 24, 32];
    public IReadOnlyList<ChannelPolicy> ChannelPolicies { get; } =
        Enum.GetValues<ChannelPolicy>();
    public IReadOnlyList<OutputFallbackPolicy> OutputFallbackPolicies { get; } =
        Enum.GetValues<OutputFallbackPolicy>();
    public IReadOnlyList<VolumeControlMode> VolumeControlModes { get; } =
        Enum.GetValues<VolumeControlMode>();
    public IReadOnlyList<DsdMode> DsdModes { get; } =
        Enum.GetValues<DsdMode>();

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
    public IReadOnlyList<Track> SelectedTracks
    {
        get => _selectedTracks;
        private set
        {
            if (!Set(ref _selectedTracks, value)) return;
            EditMetadataCommand.RaiseCanExecuteChanged();
        }
    }
    public Track? CurrentTrack { get => _currentTrack; private set { if (Set(ref _currentTrack, value)) { Raise(nameof(HasCurrentTrack)); Raise(nameof(CurrentTitle)); Raise(nameof(CurrentArtist)); Raise(nameof(CurrentArtworkPath)); Raise(nameof(LoveGlyph)); Raise(nameof(LoveText)); Raise(nameof(CurrentRating)); Raise(nameof(CanEditLyrics)); Raise(nameof(CurrentTrackHasPlaybackOverride)); Raise(nameof(PlaybackProcessingText)); RaiseLyricsState(); ReloadLyricsCommand.RaiseCanExecuteChanged(); FetchOnlineLyricsCommand.RaiseCanExecuteChanged(); ToggleLyricsEditorCommand.RaiseCanExecuteChanged(); SaveLyricsCommand.RaiseCanExecuteChanged(); SetCurrentRatingCommand.RaiseCanExecuteChanged(); SaveTrackPlaybackOverrideCommand.RaiseCanExecuteChanged(); ClearTrackPlaybackOverrideCommand.RaiseCanExecuteChanged(); AddBookmarkCommand.RaiseCanExecuteChanged(); } } }
    public IReadOnlyList<QueueEntryViewModel> SelectedQueueEntries
    {
        get => _selectedQueueEntries;
        private set
        {
            if (!Set(ref _selectedQueueEntries, value)) return;
            RemoveSelectedQueueCommand.RaiseCanExecuteChanged();
            MoveSelectedQueueTopCommand.RaiseCanExecuteChanged();
            MoveSelectedQueueBottomCommand.RaiseCanExecuteChanged();
        }
    }
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
    public IReadOnlyList<Track> AllTracks => _allTracks;
    public bool HasLibrary => _allTracks.Any(track => !track.IsMissing);
    public bool HasMissingTracks => _allTracks.Any(track => track.IsMissing);
    public bool HasBrowseTracks => BrowseTracks.Count > 0;
    public bool HasGalleryResults => GalleryRows.Count > 0;
    public bool HasOfflineSources => LibrarySources.Any(source => source.Enabled && !source.IsOnline);
    public UiStateKind LibraryStateKind => !IsLibraryReady || IsScanning
        ? UiStateKind.Loading
        : HasScanFailures && !HasLibrary
            ? UiStateKind.Error
            : HasOfflineSources && !HasLibrary
                ? UiStateKind.Offline
                : UiStateKind.Empty;
    public string LibraryStateTitle => LibraryStateKind switch
    {
        UiStateKind.Loading => IsScanning ? "Scanning your library" : "Loading your library",
        UiStateKind.Error => "The library could not be loaded",
        UiStateKind.Offline => "A music source is offline",
        _ when !string.IsNullOrWhiteSpace(SearchText) || HasQuickFilter => "No matching music",
        _ when LibrarySources.Count == 0 => "Your library is ready for music",
        _ => "Nothing is available in this view"
    };
    public string LibraryStateDescription => LibraryStateKind switch
    {
        UiStateKind.Loading => "Dextromethorphan is preparing tracks and artwork.",
        UiStateKind.Error => "Review the scan details in Settings, then try the scan again.",
        UiStateKind.Offline => "Reconnect the drive or network share. Existing library records are kept safely.",
        _ when !string.IsNullOrWhiteSpace(SearchText) || HasQuickFilter => "Try a different search or clear the current filter.",
        _ when LibrarySources.Count == 0 => "Add a local folder or network source from Settings.",
        _ => "Choose another library view or run a scan to refresh this source."
    };
    public string LibraryStateActionText =>
        !IsScanning && LibrarySources.Any(source => source.Enabled)
            ? "Scan library"
            : string.Empty;
    public int BrowseTrackSourceCount => _activeTrackPresentation?.Source.Count ?? BrowseTracks.Count;
    public bool HasQueue => Queue.Count > 0;
    public bool HasQueueHistory => QueueHistory.Count > 0;
    public bool HasBookmarks => Bookmarks.Count > 0;
    public bool ResumeTrackBookmarks => _settings.Current.ResumeTrackBookmarks;
    public bool IsGroupView => !IsCollectionDetailOpen && CurrentView is "Albums" or "Artists" or "Genres";
    public bool IsCollectionDetailView => IsCollectionDetailOpen && CurrentView is "Albums" or "Artists" or "Genres";
    public bool IsTrackView => !IsCollectionDetailOpen && CurrentView is "Songs" or "Favorites" or "Missing" or "Recently Added" or "Recently Played" or "Most Played" or "Never Played" or "History";
    public bool IsSidebarView => !IsCollectionDetailOpen && CurrentView is "Folders" or "Playlists";
    public bool IsFolderView => !IsCollectionDetailOpen && CurrentView == "Folders";
    public bool IsPlaylistView => !IsCollectionDetailOpen && CurrentView == "Playlists";
    public bool IsNowPlayingView => !IsCollectionDetailOpen && CurrentView == "Now Playing";
    public FolderTreeNodeViewModel? SelectedFolderNode
    {
        get => _selectedFolderNode;
        private set
        {
            if (_selectedFolderNode is not null) _selectedFolderNode.IsSelected = false;
            if (!Set(ref _selectedFolderNode, value)) return;
            if (value is not null) value.IsSelected = true;
        }
    }
    public string CurrentTitle => CurrentTrack?.Title ?? "Nothing playing";
    public string CurrentArtist => CurrentTrack is null ? "Choose something from your library" : $"{CurrentTrack.DisplayArtist} — {CurrentTrack.DisplayAlbum}";
    public string? CurrentArtworkPath => CurrentTrack?.ArtworkPath;
    public string LoveGlyph => CurrentTrack?.IsLoved == true ? "♥" : "♡";
    public string LoveText => CurrentTrack?.IsLoved == true
        ? "Remove love from current track"
        : "Love current track";
    public int CurrentRating => CurrentTrack?.Rating ?? 0;
    public string UserNotice { get => _userNotice; private set => Set(ref _userNotice, value); }
    public ToastNotification? ActiveToast { get => _activeToast; private set => Set(ref _activeToast, value); }
    public bool IsUserNoticeVisible { get => _isUserNoticeVisible; private set => Set(ref _isUserNoticeVisible, value); }
    public bool StopAfterCurrent
    {
        get => _stopAfterCurrent;
        private set
        {
            if (!Set(ref _stopAfterCurrent, value)) return;
            Raise(nameof(HasStopMode));
            Raise(nameof(StopModeText));
        }
    }
    public bool StopAfterQueue
    {
        get => _stopAfterQueue;
        private set
        {
            if (!Set(ref _stopAfterQueue, value)) return;
            Raise(nameof(HasStopMode));
            Raise(nameof(StopModeText));
        }
    }
    public bool HasStopMode => StopAfterCurrent || StopAfterQueue;
    public string StopModeText => StopAfterCurrent
        ? "Stop after current track"
        : StopAfterQueue
            ? "Stop after queue"
            : "Continuous playback";
    public bool CurrentTrackHasPlaybackOverride => CurrentTrack is not null
        && _settings.Current.TrackPlaybackOverrides.ContainsKey(CurrentTrack.Path);
    public string PlaybackProcessingText =>
        $"{PlaybackSpeed:0.00}× · {PitchSemitones:+0.#;-0.#;0} st" +
        (CurrentTrackHasPlaybackOverride ? " · track override" : "");
    public bool IsCollectionDetailOpen
    {
        get => _isCollectionDetailOpen;
        private set
        {
            if (!Set(ref _isCollectionDetailOpen, value)) return;
            Raise(nameof(IsGroupView)); Raise(nameof(IsCollectionDetailView)); Raise(nameof(IsTrackView)); Raise(nameof(IsSidebarView)); Raise(nameof(IsFolderView)); Raise(nameof(IsPlaylistView)); Raise(nameof(IsNowPlayingView));
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
            Raise(nameof(IsGroupView)); Raise(nameof(IsCollectionDetailView)); Raise(nameof(IsTrackView)); Raise(nameof(IsSidebarView)); Raise(nameof(IsFolderView)); Raise(nameof(IsPlaylistView)); Raise(nameof(IsNowPlayingView));
            Raise(nameof(ViewTitle));
            Raise(nameof(PrimaryViewStateKey)); Raise(nameof(ContentViewStateKey));
            LoadViewSettings();
        }
    }
    public string PrimaryViewStateKey => $"primary:{CurrentView}";
    public string ContentViewStateKey => _contentViewStateKey;
    public string ViewTitle => IsCollectionDetailOpen ? DetailTabTitle : CurrentView;
    public string ViewSubtitle { get => _viewSubtitle; private set => Set(ref _viewSubtitle, value); }
    public string SelectedGroupTitle { get => _selectedGroupTitle; private set => Set(ref _selectedGroupTitle, value); }
    public string SelectedGroupSubtitle { get => _selectedGroupSubtitle; private set => Set(ref _selectedGroupSubtitle, value); }
    public string ArtistProfileText { get => _artistProfileText; private set => Set(ref _artistProfileText, value); }
    public string ArtistProfileAttribution { get => _artistProfileAttribution; private set => Set(ref _artistProfileAttribution, value); }
    public string ArtistDiscographyText { get => _artistDiscographyText; private set => Set(ref _artistDiscographyText, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) { Raise(nameof(SearchSuggestions)); RaiseLibraryState(); DebounceSearch(); } } }
    internal static IReadOnlyList<string> SupportedSortOptions { get; } =
    [
        "Title", "Artist", "Album", "Year", "Duration", "Date added",
        "Last played", "Play count", "Rating", "Codec"
    ];
    public IReadOnlyList<string> SortOptions => SupportedSortOptions;
    public IReadOnlyList<LibraryDensity> DensityOptions { get; } = Enum.GetValues<LibraryDensity>();
    public string SortBy
    {
        get => _sortBy;
        set
        {
            var normalized = NormalizeSortField(value);
            if (Set(ref _sortBy, normalized)) ApplySortSettings();
        }
    }
    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (!Set(ref _sortDescending, value)) return;
            Raise(nameof(SortDirectionGlyph));
            Raise(nameof(SortDirectionLabel));
            Raise(nameof(SortDirectionToolTip));
            ApplySortSettings();
        }
    }
    public string SortDirectionGlyph => SortDescending ? "\uE74B" : "\uE74A";
    public string SortDirectionLabel => SortDescending ? "Descending" : "Ascending";
    public string SortDirectionToolTip => SortDescending
        ? "Sorted descending. Activate to sort ascending."
        : "Sorted ascending. Activate to sort descending.";
    public LibraryDensity Density
    {
        get => _density;
        set
        {
            if (!Set(ref _density, value)) return;
            PersistViewSettings();
            Raise(nameof(IsGridDensity));
            Raise(nameof(IsCompactDensity));
            Raise(nameof(GalleryMetadataHeight));
            Raise(nameof(GalleryItemHeight));
            Raise(nameof(TrackRowHeight));
        }
    }
    public bool IsGridDensity => Density == LibraryDensity.Grid;
    public bool IsCompactDensity => Density == LibraryDensity.Compact;
    public string QuickFilter
    {
        get => _quickFilter;
        set
        {
            if (!Set(ref _quickFilter, value ?? "")) return;
            Raise(nameof(HasQuickFilter));
            Raise(nameof(QuickFilterLabel));
            Raise(nameof(QuickFilterToolTip));
            RaiseLibraryState();
            PersistViewSettings();
            DebounceQuickFilter();
        }
    }
    public bool HasQuickFilter => !string.IsNullOrWhiteSpace(QuickFilter);
    public string QuickFilterLabel => QuickFilter.Trim().ToLowerInvariant() switch
    {
        "" => "Filter",
        "lossless" => "Lossless",
        "loved" => "Loved",
        "unloved" => "Unloved",
        "compilation" => "Compilations",
        "rating:>=4" => "Rating 4+",
        _ => "Custom filter"
    };
    public string QuickFilterToolTip => HasQuickFilter
        ? $"Filter this view: {QuickFilter}"
        : "Filter this view";
    public IReadOnlyList<string> SearchHistory => _settings.Current.SearchHistory;
    public IReadOnlyList<string> SearchSuggestions => string.IsNullOrWhiteSpace(SearchText)
        ? SearchHistory.Take(8).ToArray()
        : SearchHistory.Where(item => item.StartsWith(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase)).Take(8).ToArray();
    public IReadOnlyList<string> DashboardModuleNames { get; } = ["Artwork", "Lyrics", "Metadata"];
    public IReadOnlyList<string> DashboardModules => _dashboardModules;
    public bool IsDashboardModuleVisible(string module) => _dashboardModules.Contains(module, StringComparer.OrdinalIgnoreCase);
    public bool IsArtworkDashboardAvailable => IsDashboardModuleVisible("Artwork");
    public bool IsNowPlayingInspectorVisible =>
        IsDashboardModuleVisible("Lyrics")
        || IsDashboardModuleVisible("Metadata");
    public bool IsLyricsInspectorAvailable => IsDashboardModuleVisible("Lyrics");
    public bool IsMetadataInspectorAvailable => IsDashboardModuleVisible("Metadata");
    public bool IsLyricsInspectorSelected =>
        IsLyricsInspectorAvailable
        && _nowPlayingInspectorTab.Equals("Lyrics", StringComparison.OrdinalIgnoreCase);
    public bool IsMetadataInspectorSelected =>
        IsMetadataInspectorAvailable
        && _nowPlayingInspectorTab.Equals("Metadata", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> TrackColumnNames { get; } =
        TrackListColumnLayout.ColumnNames;
    public IReadOnlyList<string> VisibleTrackColumns => GetViewSettings().VisibleColumns;
    public IReadOnlyList<string> TrackColumnOrder => GetViewSettings().ColumnOrder;
    public IReadOnlyDictionary<string, double> TrackColumnWidths => GetViewSettings().ColumnWidths;
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string PlayGlyph
    {
        get => _playGlyph;
        private set
        {
            if (Set(ref _playGlyph, value))
                Raise(nameof(PlaybackActionText));
        }
    }
    public string PlaybackActionText => PlayGlyph == "Ⅱ" ? "Pause" : "Play";
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }
    public string DurationText { get => _durationText; private set => Set(ref _durationText, value); }
    public double PositionSeconds { get => _positionSeconds; private set => Set(ref _positionSeconds, value); }
    public double DurationSeconds { get => _durationSeconds; private set => Set(ref _durationSeconds, Math.Max(1, value)); }
    public double Volume { get => _volume; set { if (Set(ref _volume, Math.Clamp(value, 0, 1))) QueueVolumeUpdate(_volume); } }
    public bool IsScanning { get => _isScanning; private set { if (Set(ref _isScanning, value)) RaiseLibraryState(); } }
    public int ScanDiscovered { get => _scanDiscovered; private set { if (Set(ref _scanDiscovered, value)) Raise(nameof(ScanProgressMaximum)); } }
    public int ScanProcessed { get => _scanProcessed; private set { if (Set(ref _scanProcessed, value)) Raise(nameof(ScanProgressValue)); } }
    public int ScanAdded { get => _scanAdded; private set => Set(ref _scanAdded, value); }
    public int ScanUpdated { get => _scanUpdated; private set => Set(ref _scanUpdated, value); }
    public int ScanFailed { get => _scanFailed; private set => Set(ref _scanFailed, value); }
    public double ScanProgressMaximum => Math.Max(1, ScanDiscovered);
    public double ScanProgressValue => Math.Min(ScanProcessed, ScanProgressMaximum);
    public string ScanCurrentPath { get => _scanCurrentPath; private set => Set(ref _scanCurrentPath, value); }
    public string ScanCurrentSource { get => _scanCurrentSource; private set => Set(ref _scanCurrentSource, value); }
    public bool HasScanFailures => ScanFailures.Count > 0;
    public bool ScheduledScanEnabled
    {
        get => _scheduledScanEnabled;
        set { if (Set(ref _scheduledScanEnabled, value)) _ = _settings.UpdateAsync(settings => settings.ScheduledLibraryScanEnabled = value); }
    }
    public int ScheduledScanIntervalMinutes
    {
        get => _scheduledScanIntervalMinutes;
        set
        {
            var normalized = Math.Clamp(value, 15, 1440);
            if (Set(ref _scheduledScanIntervalMinutes, normalized))
                _ = _settings.UpdateAsync(settings => settings.ScheduledLibraryScanIntervalMinutes = normalized);
        }
    }
    public bool AllowScheduledScanOnBattery
    {
        get => _allowScheduledScanOnBattery;
        set { if (Set(ref _allowScheduledScanOnBattery, value)) _ = _settings.UpdateAsync(settings => settings.AllowScheduledScanOnBattery = value); }
    }
    public bool AllowScheduledScanOnMeteredNetwork
    {
        get => _allowScheduledScanOnMeteredNetwork;
        set { if (Set(ref _allowScheduledScanOnMeteredNetwork, value)) _ = _settings.UpdateAsync(settings => settings.AllowScheduledScanOnMeteredNetwork = value); }
    }
    public string ScheduledScanStatus { get => _scheduledScanStatus; private set => Set(ref _scheduledScanStatus, value); }
    public bool IsLibraryReady { get => _isLibraryReady; private set { if (Set(ref _isLibraryReady, value)) RaiseLibraryState(); } }
    public bool IsSafeMode { get => _isSafeMode; private set => Set(ref _isSafeMode, value); }
    public bool IsScanPaused => _scanner.State == ScanLifecycleState.Paused;
    public string ScanPauseGlyph => IsScanPaused ? "\uE768" : "\uE769";
    public string ScanPauseText => IsScanPaused ? "Resume library scan" : "Pause library scan";
    public bool QueueVisible
    {
        get => _queueVisible;
        set
        {
            if (!Set(ref _queueVisible, value)) return;
            RaiseQueuePanelState();
            _ = _settings.UpdateAsync(settings => settings.QueuePanelVisible = value);
        }
    }
    public bool QueuePanelCompact
    {
        get => _queuePanelCompact;
        set
        {
            if (!Set(ref _queuePanelCompact, value)) return;
            RaiseQueuePanelState();
            _ = _settings.UpdateAsync(settings => settings.QueuePanelCompact = value);
        }
    }
    public PanelDockSide QueuePanelDockSide
    {
        get => _queuePanelDockSide;
        set
        {
            var normalized = Enum.IsDefined(value)
                ? value
                : PanelDockSide.Right;
            if (!Set(ref _queuePanelDockSide, normalized)) return;
            RaiseQueuePanelState();
            _ = _settings.UpdateAsync(settings => settings.QueuePanelDockSide = normalized);
        }
    }
    public bool QueuePanelExpanded => QueueVisible && !QueuePanelCompact;
    public bool QueuePanelCollapsed => QueueVisible && QueuePanelCompact;
    public int QueuePanelColumn => QueuePanelDockSide == PanelDockSide.Left ? 0 : 2;
    public string QueuePanelDockText => QueuePanelDockSide == PanelDockSide.Left
        ? "Docked left"
        : "Docked right";
    public string QueuePanelCollapseGlyph => QueuePanelDockSide == PanelDockSide.Left
        ? "\uE76B"
        : "\uE76C";
    public bool AnimationsEnabled { get => _animationsEnabled; set { if (Set(ref _animationsEnabled, value)) _ = _settings.UpdateAsync(x => x.AnimationsEnabled = value); } }
    public bool VisualizerEnabled
    {
        get => _visualizerEnabled;
        set
        {
            if (!Set(ref _visualizerEnabled, value)) return;
            _audio.SetVisualizationEnabled(value);
            Raise(nameof(VisualizerText));
            _ = _settings.UpdateAsync(x => x.VisualizerEnabled = value);
        }
    }
    public string VisualizerText => VisualizerEnabled
        ? "Hide audio spectrum"
        : "Show audio spectrum";
    public AudioVisualizationSnapshot GetVisualizationSnapshot(int bandCount = 40) =>
        _audio.GetVisualizationSnapshot(bandCount);
    public bool DiagnosticsVisible { get => _diagnosticsVisible; set => Set(ref _diagnosticsVisible, value); }

    private ViewSettings GetViewSettings()
    {
        if (_settings.Current.ViewSettings.TryGetValue(CurrentView, out var existing)) return existing;
        var created = new ViewSettings { CoverSize = _albumTileSize };
        _settings.Current.ViewSettings[CurrentView] = created;
        return created;
    }

    private void LoadViewSettings()
    {
        var view = GetViewSettings();
        _sortBy = NormalizeSortField(view.SortBy);
        _sortDescending = view.SortDescending;
        _density = view.Density;
        _quickFilter = view.QuickFilter;
        _albumTileSize = Math.Clamp(view.CoverSize, 80, 400);
        Raise(nameof(SortBy)); Raise(nameof(SortDescending)); Raise(nameof(SortDirectionGlyph)); Raise(nameof(SortDirectionLabel)); Raise(nameof(SortDirectionToolTip)); Raise(nameof(Density)); Raise(nameof(IsGridDensity)); Raise(nameof(IsCompactDensity)); Raise(nameof(TrackRowHeight)); Raise(nameof(GalleryMetadataHeight)); Raise(nameof(QuickFilter)); Raise(nameof(HasQuickFilter)); Raise(nameof(QuickFilterLabel)); Raise(nameof(QuickFilterToolTip)); Raise(nameof(AlbumTileSize)); Raise(nameof(GalleryItemWidth)); Raise(nameof(GalleryItemHeight)); Raise(nameof(VisibleTrackColumns)); Raise(nameof(TrackColumnOrder)); Raise(nameof(TrackColumnWidths));
    }

    private void PersistViewSettings()
    {
        var viewName = CurrentView;
        _ = _settings.UpdateAsync(settings =>
        {
            if (!settings.ViewSettings.TryGetValue(viewName, out var view)) settings.ViewSettings[viewName] = view = new ViewSettings();
            view.SortBy = _sortBy; view.SortDescending = _sortDescending; view.Density = _density; view.QuickFilter = _quickFilter; view.CoverSize = _albumTileSize;
        });
    }

    private void ApplySortSettings()
    {
        PersistViewSettings();
        ApplyViewPresentationSettings();
    }

    private void ApplyViewPresentationSettings()
    {
        _galleryViews.Clear();
        _sidebarViews.Clear();
        _trackViews.Clear();
        _activeGalleryPresentation = null;
        _activeSidebarPresentation = null;
        _activeTrackPresentation = null;
        if (IsCollectionDetailOpen
            && SelectedCard is { } selected
            && TryGetCardTracks(selected) is { } selectedTracks)
        {
            SetBrowseTracks(
                selectedTracks,
                selected.Title,
                FormatCollectionSubtitle(selected, selectedTracks));
            RestartActiveArtworkResolution();
            return;
        }
        ApplyCurrentView(false);
    }
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
    public string DiagnosticEndpoint => _audio.Diagnostics is { } diagnostics
        ? diagnostics.FallbackActive
            ? $"{diagnostics.RequestedDevice} → {diagnostics.EffectiveDevice}"
            : diagnostics.EffectiveDevice
        : "—";
    public string DiagnosticTiming => _audio.Diagnostics is { } diagnostics
        ? $"last {diagnostics.LastCallbackMilliseconds:0.###} ms · max {diagnostics.MaximumCallbackMilliseconds:0.###} ms · {diagnostics.Underruns} underruns · {diagnostics.RecoveryAttempts} recovery attempts" +
          (diagnostics.ProcessingLatencyMilliseconds > 0
              ? $" · {diagnostics.Processor} · {diagnostics.ProcessingLatencyMilliseconds:0.#} ms processing latency · {diagnostics.TimelineClock}"
              : string.Empty)
        : "—";
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
            value = Math.Clamp(value, 80, 400);
            if (!Set(ref _albumTileSize, value)) return;
            Raise(nameof(GalleryItemWidth));
            Raise(nameof(GalleryItemHeight));
            var viewName = CurrentView;
            var view = GetViewSettings();
            view.CoverSize = value;
            _ = _settings.UpdateAsync(settings =>
            {
                settings.AlbumTileSize = value;
                settings.ViewSettings[viewName] = view;
            });
        }
    }
    public double GalleryItemWidth => AlbumTileSize + 14;
    public double GalleryMetadataHeight =>
        TrackListColumnLayout.GalleryMetadataHeight(Density);
    public double GalleryItemHeight =>
        AlbumTileSize + GalleryMetadataHeight + 18;
    public double TrackRowHeight =>
        TrackListColumnLayout.TrackRowHeight(Density);
    public string ActiveLyric { get => _activeLyric; private set => Set(ref _activeLyric, value); }
    public LyricLineViewModel? ActiveLyricLine { get => _activeLyricLine; private set => Set(ref _activeLyricLine, value); }
    public bool HasLyrics => Lyrics.Count > 0;
    public UiStateKind LyricsStateKind => CurrentTrack is null
        ? UiStateKind.Disabled
        : IsOnlineLyricsBusy
            ? UiStateKind.Loading
            : LyricsStatus.Contains("failed", StringComparison.OrdinalIgnoreCase)
              || LyricsStatus.Contains("could not", StringComparison.OrdinalIgnoreCase)
                ? UiStateKind.Error
                : UiStateKind.Empty;
    public string LyricsStateTitle => LyricsStateKind switch
    {
        UiStateKind.Disabled => "Play a track to see lyrics",
        UiStateKind.Loading => "Finding lyrics",
        UiStateKind.Error => "Lyrics are unavailable",
        _ => "No lyrics found"
    };
    public string LyricsStateDescription => LyricsStateKind switch
    {
        UiStateKind.Disabled => "Lyrics appear here when playback starts.",
        UiStateKind.Loading => "Searching the enabled local and online sources.",
        UiStateKind.Error => "Try reloading, choosing a local lyric file, or checking the online lookup setting.",
        _ when OnlineLyricsEnabled => "Reload the track, choose a lyric file, or search the online provider.",
        _ => "Choose a local lyric file, or enable online lookup in Lyrics settings."
    };
    public string LyricsStateActionText => CurrentTrack is not null && OnlineLyricsEnabled
        ? "Search online"
        : string.Empty;
    public bool HasSyncedLyrics { get => _hasSyncedLyrics; private set { if (Set(ref _hasSyncedLyrics, value)) { Raise(nameof(LyricsModeText)); Raise(nameof(LyricsHintText)); } } }
    public string LyricsModeText => HasSyncedLyrics ? "SYNCED" : "FULL LYRICS";
    public string LyricsHintText => HasSyncedLyrics ? "Click any line to jump to that moment" : "No timing data available";
    public LyricsDocument? CurrentLyricsDocument
    {
        get => _currentLyricsDocument;
        private set
        {
            if (!Set(ref _currentLyricsDocument, value)) return;
            Raise(nameof(LyricsSourceText));
            Raise(nameof(CanEditLyrics));
            Raise(nameof(CanRemoveLyrics));
        }
    }
    public string LyricsSourceText => CurrentLyricsDocument is null
        ? "No lyric source"
        : CurrentLyricsDocument.Attribution is { Length: > 0 } attribution
            ? $"{CurrentLyricsDocument.DisplayName} · {attribution}"
            : CurrentLyricsDocument.DisplayName;
    public bool CanEditLyrics => CurrentTrack is not null;
    public bool CanRemoveLyrics => CurrentLyricsDocument?.CanEdit == true;
    public int LyricsOffsetMilliseconds
    {
        get => _lyricsOffsetMilliseconds;
        set
        {
            var normalized = Math.Clamp(value, -30_000, 30_000);
            if (!Set(ref _lyricsOffsetMilliseconds, normalized)) return;
            Raise(nameof(LyricsOffsetText));
            if (CurrentTrack is { } track)
            {
                _ = _settings.UpdateAsync(settings => settings.LyricOffsetsMilliseconds[track.Path] = normalized);
                if (CurrentLyricsDocument is not null) ApplyLyricsDocument(CurrentLyricsDocument, track);
            }
        }
    }
    public string LyricsOffsetText => LyricsOffsetMilliseconds == 0 ? "0 ms" : $"{LyricsOffsetMilliseconds:+0;-0} ms";
    public IReadOnlyList<LyricsDisplayMode> LyricsDisplayModes { get; } = Enum.GetValues<LyricsDisplayMode>();
    public IReadOnlyList<LyricsTextAlignment> LyricsAlignments { get; } = Enum.GetValues<LyricsTextAlignment>();
    public LyricsDisplayMode LyricsDisplayMode
    {
        get => _lyricsDisplayMode;
        set { if (Set(ref _lyricsDisplayMode, value)) { _ = _settings.UpdateAsync(settings => settings.LyricsDisplayMode = value); if (CurrentTrack is { } track && CurrentLyricsDocument is not null) ApplyLyricsDocument(CurrentLyricsDocument, track); } }
    }
    public double LyricsFontSize { get => _lyricsFontSize; set { var normalized = Math.Clamp(value, 14, 52); if (Set(ref _lyricsFontSize, normalized)) _ = _settings.UpdateAsync(settings => settings.LyricsFontSize = normalized); } }
    public LyricsTextAlignment LyricsAlignment { get => _lyricsAlignment; set { if (Set(ref _lyricsAlignment, value)) { Raise(nameof(LyricsHorizontalAlignment)); Raise(nameof(LyricsTextAlignmentValue)); _ = _settings.UpdateAsync(settings => settings.LyricsAlignment = value); } } }
    public HorizontalAlignment LyricsHorizontalAlignment => LyricsAlignment switch { LyricsTextAlignment.Center => HorizontalAlignment.Center, LyricsTextAlignment.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Left };
    public TextAlignment LyricsTextAlignmentValue => LyricsAlignment switch { LyricsTextAlignment.Center => TextAlignment.Center, LyricsTextAlignment.Right => TextAlignment.Right, _ => TextAlignment.Left };
    public double LyricsLineSpacing { get => _lyricsLineSpacing; set { var normalized = Math.Clamp(value, 0.8, 2); if (Set(ref _lyricsLineSpacing, normalized)) { Raise(nameof(LyricsLineMargin)); _ = _settings.UpdateAsync(settings => settings.LyricsLineSpacing = normalized); } } }
    public Thickness LyricsLineMargin => new(0, Math.Max(1, (LyricsLineSpacing - 0.8) * 10), 0, Math.Max(1, (LyricsLineSpacing - 0.8) * 10));
    public double LyricsBlurStrength { get => _lyricsBlurStrength; set { var normalized = Math.Clamp(value, 0, 20); if (Set(ref _lyricsBlurStrength, normalized)) _ = _settings.UpdateAsync(settings => settings.LyricsBlurStrength = normalized); } }
    public bool KaraokeWordAnimation { get => _karaokeWordAnimation; set { if (Set(ref _karaokeWordAnimation, value)) _ = _settings.UpdateAsync(settings => settings.KaraokeWordAnimation = value); } }
    public bool OnlineLyricsEnabled
    {
        get => _onlineLyricsEnabled;
        set
        {
            if (!Set(ref _onlineLyricsEnabled, value)) return;
            _ = _settings.UpdateAsync(settings => settings.OnlineLyricsEnabled = value);
            FetchOnlineLyricsCommand.RaiseCanExecuteChanged();
            if (!value && CurrentLyricsDocument?.Kind == LyricsSourceKind.OnlineCache)
                _ = ReloadLyricsAsync();
        }
    }
    public bool MetadataLookupEnabled { get => _metadataLookupEnabled; set { if (Set(ref _metadataLookupEnabled, value)) _ = _settings.UpdateAsync(settings => settings.MetadataLookupEnabled = value); } }
    public bool MusicBrainzLookupEnabled { get => _musicBrainzLookupEnabled; set { if (Set(ref _musicBrainzLookupEnabled, value)) _ = _settings.UpdateAsync(settings => settings.MusicBrainzLookupEnabled = value); } }
    public bool DiscogsLookupEnabled { get => _discogsLookupEnabled; set { if (Set(ref _discogsLookupEnabled, value)) _ = _settings.UpdateAsync(settings => settings.DiscogsLookupEnabled = value); } }
    public string DiscogsUserToken { get => _discogsUserToken; set { if (Set(ref _discogsUserToken, value ?? "")) _ = _settings.UpdateAsync(settings => settings.DiscogsUserToken = value ?? ""); } }
    public bool IsOnlineLyricsBusy
    {
        get => _isOnlineLyricsBusy;
        private set
        {
            if (!Set(ref _isOnlineLyricsBusy, value)) return;
            FetchOnlineLyricsCommand.RaiseCanExecuteChanged();
            RaiseLyricsState();
        }
    }
    public bool LyricsEditorVisible { get => _lyricsEditorVisible; private set { if (Set(ref _lyricsEditorVisible, value)) SaveLyricsCommand.RaiseCanExecuteChanged(); } }
    public string LyricsEditorText { get => _lyricsEditorText; set => Set(ref _lyricsEditorText, value); }
    public string LyricsStatus { get => _lyricsStatus; private set { if (Set(ref _lyricsStatus, value)) RaiseLyricsState(); } }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand SelectGroupCommand { get; }
    public RelayCommand CloseCollectionCommand { get; }
    public AsyncRelayCommand PlayGroupCommand { get; }
    public AsyncRelayCommand PlayGroupNextCommand { get; }
    public AsyncRelayCommand AddGroupToQueueCommand { get; }
    public AsyncRelayCommand PlaySelectedCommand { get; }
    public AsyncRelayCommand TogglePlaybackCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public RelayCommand AddToQueueCommand { get; }
    public RelayCommand AddSelectedToQueueCommand { get; }
    public RelayCommand PlayNextCommand { get; }
    public RelayCommand ToggleQueueCommand { get; }
    public RelayCommand ToggleQueueCompactCommand { get; }
    public RelayCommand ExpandQueueCommand { get; }
    public RelayCommand SetQueueDockSideCommand { get; }
    public RelayCommand ToggleVisualizerCommand { get; }
    public RelayCommand ToggleShuffleCommand { get; }
    public RelayCommand CycleRepeatCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand RescanSourceCommand { get; }
    public AsyncRelayCommand ToggleLibrarySourceCommand { get; }
    public AsyncRelayCommand ToggleSourceWatcherCommand { get; }
    public AsyncRelayCommand RemoveLibrarySourceCommand { get; }
    public AsyncRelayCommand RemoveExclusionCommand { get; }
    public RelayCommand ToggleScanPauseCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public AsyncRelayCommand RefreshArtworkCacheCommand { get; }
    public AsyncRelayCommand ClearArtworkCacheCommand { get; }
    public AsyncRelayCommand RebuildArtworkCacheCommand { get; }
    public RelayCommand UndoQueueCommand { get; }
    public RelayCommand RedoQueueCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public AsyncRelayCommand RemoveSelectedQueueCommand { get; }
    public RelayCommand MoveSelectedQueueTopCommand { get; }
    public RelayCommand MoveSelectedQueueBottomCommand { get; }
    public RelayCommand ToggleStopAfterCurrentCommand { get; }
    public RelayCommand ToggleStopAfterQueueCommand { get; }
    public AsyncRelayCommand SetPlaybackSpeedCommand { get; }
    public AsyncRelayCommand SetPitchCommand { get; }
    public AsyncRelayCommand TogglePreservePitchCommand { get; }
    public AsyncRelayCommand ResetPlaybackProcessingCommand { get; }
    public AsyncRelayCommand SaveTrackPlaybackOverrideCommand { get; }
    public AsyncRelayCommand ClearTrackPlaybackOverrideCommand { get; }
    public AsyncRelayCommand SetCurrentRatingCommand { get; }
    public AsyncRelayCommand AddBookmarkCommand { get; }
    public AsyncRelayCommand ToggleBookmarkResumeCommand { get; }
    public AsyncRelayCommand SeekBookmarkCommand { get; }
    public AsyncRelayCommand RenameBookmarkCommand { get; }
    public AsyncRelayCommand DeleteBookmarkCommand { get; }
    public AsyncRelayCommand LoveCommand { get; }
    public AsyncRelayCommand EditMetadataCommand { get; }
    public AsyncRelayCommand UndoMetadataCommand { get; }
    public AsyncRelayCommand SeekLyricCommand { get; }
    public AsyncRelayCommand ReloadLyricsCommand { get; }
    public AsyncRelayCommand FetchOnlineLyricsCommand { get; }
    public AsyncRelayCommand SelectLyricsSourceCommand { get; }
    public RelayCommand ToggleLyricsEditorCommand { get; }
    public AsyncRelayCommand SaveLyricsCommand { get; }
    public RelayCommand CancelLyricsEditorCommand { get; }
    public AsyncRelayCommand SeekChapterCommand { get; }
    public AsyncRelayCommand PlayQueueEntryCommand { get; }
    public AsyncRelayCommand RemoveQueueEntryCommand { get; }
    public AsyncRelayCommand RemoveMissingTrackCommand { get; }
    public RelayCommand PlayQueueEntryNextCommand { get; }
    public RelayCommand ToggleDiagnosticsCommand { get; }
    public RelayCommand NewPlaylistCommand { get; }
    public AsyncRelayCommand EditPlaylistCommand { get; }
    public AsyncRelayCommand DuplicatePlaylistCommand { get; }
    public AsyncRelayCommand DeletePlaylistCommand { get; }
    public AsyncRelayCommand UndoPlaylistCommand { get; }
    public AsyncRelayCommand RedoPlaylistCommand { get; }
    public AsyncRelayCommand AddSelectedToPlaylistCommand { get; }
    public RelayCommand SaveQueueAsPlaylistCommand { get; }
    public AsyncRelayCommand ClearSearchHistoryCommand { get; }
    public RelayCommand UseSearchHistoryCommand { get; }
    public RelayCommand SetQuickFilterCommand { get; }
    public RelayCommand ClearQuickFilterCommand { get; }
    public RelayCommand ToggleSortDirectionCommand { get; }
    public RelayCommand ToggleTrackColumnCommand { get; }
    public RelayCommand MoveTrackColumnCommand { get; }
    public RelayCommand SetTrackColumnWidthCommand { get; }
    public RelayCommand SetDensityCommand { get; }
    public RelayCommand SetAlbumTileSizeCommand { get; }
    public RelayCommand ToggleDashboardModuleCommand { get; }
    public RelayCommand SelectNowPlayingInspectorCommand { get; }
    public RelayCommand DismissNoticeCommand { get; }

    public void UpdateQueueSelection(IEnumerable<QueueEntryViewModel> entries) =>
        SelectedQueueEntries = entries.Distinct().ToArray();

    public void ShowNotice(
        string message,
        ToastSeverity severity = ToastSeverity.Information)
    {
        var notification = new ToastNotification(message, severity);
        ActiveToast = notification;
        UserNotice = notification.Message;
        IsUserNoticeVisible = true;
        _noticeTimer.Stop();
        _noticeTimer.Interval = notification.DisplayDuration;
        _noticeTimer.Start();
    }

    private void RaiseLibraryState()
    {
        Raise(nameof(HasGalleryResults));
        Raise(nameof(HasOfflineSources));
        Raise(nameof(LibraryStateKind));
        Raise(nameof(LibraryStateTitle));
        Raise(nameof(LibraryStateDescription));
        Raise(nameof(LibraryStateActionText));
    }

    private void RaiseLyricsState()
    {
        Raise(nameof(LyricsStateKind));
        Raise(nameof(LyricsStateTitle));
        Raise(nameof(LyricsStateDescription));
        Raise(nameof(LyricsStateActionText));
    }

    private void ToggleQueueVisibility()
    {
        if (QueueVisible)
        {
            QueueVisible = false;
            return;
        }

        QueuePanelCompact = false;
        QueueVisible = true;
    }

    private void SetQueueDockSide(object? parameter)
    {
        if (parameter is PanelDockSide side)
        {
            QueuePanelDockSide = side;
            return;
        }

        if (Enum.TryParse<PanelDockSide>(
                parameter?.ToString(),
                ignoreCase: true,
                out var parsed))
            QueuePanelDockSide = parsed;
    }

    private void RaiseQueuePanelState()
    {
        Raise(nameof(QueueVisible));
        Raise(nameof(QueuePanelExpanded));
        Raise(nameof(QueuePanelCollapsed));
        Raise(nameof(QueuePanelColumn));
        Raise(nameof(QueuePanelDockText));
        Raise(nameof(QueuePanelCollapseGlyph));
    }

    private void NoticeTimerOnTick(object? sender, EventArgs e)
    {
        DismissNotice();
    }

    private void DismissNotice()
    {
        _noticeTimer.Stop();
        IsUserNoticeVisible = false;
    }

    private async Task RemoveSelectedQueueAsync()
    {
        var selected = SelectedQueueEntries;
        if (selected.Count == 0) return;
        var playingWasRemoved = selected.Any(entry => entry.IsPlaying);
        var removed = _queue.RemoveMany(selected.Select(entry => entry.Entry.Id).ToArray());
        if (removed == 0) return;
        SelectedQueueEntries = [];
        ShowNotice(
            removed == 1
                ? "Removed 1 track from queue"
                : $"Removed {removed} tracks from queue",
            ToastSeverity.Success);
        if (!playingWasRemoved) return;
        if (_queue.Current is { } replacement) await ChangeTrackAsync(replacement);
        else await _audio.StopAsync(_lifetime.Token);
    }

    private void MoveSelectedQueue(bool toTop)
    {
        var ids = SelectedQueueEntries
            .Where(entry => entry.CanReorder)
            .Select(entry => entry.Entry.Id)
            .ToArray();
        if (ids.Length == 0) return;
        _queue.MoveInPlaybackOrder(ids, toTop ? 1 : QueuePlaybackCount());
        ShowNotice(
            toTop ? "Moved selection to top" : "Moved selection to bottom",
            ToastSeverity.Success);
    }

    private void ToggleStopMode(bool stopAfterCurrent)
    {
        if (stopAfterCurrent)
        {
            StopAfterCurrent = !StopAfterCurrent;
            if (StopAfterCurrent) StopAfterQueue = false;
        }
        else
        {
            StopAfterQueue = !StopAfterQueue;
            if (StopAfterQueue) StopAfterCurrent = false;
        }
        _ = _settings.UpdateAsync(settings =>
        {
            settings.StopAfterCurrent = StopAfterCurrent;
            settings.StopAfterQueue = StopAfterQueue;
        });
        _ = RefreshQueuedTransitionAsync();
        ShowNotice(StopModeText);
    }

    private async Task RefreshQueuedTransitionAsync()
    {
        try { await _audio.QueueNextAsync(PeekUpcomingTrack(), _lifetime.Token); }
        catch (Exception exception) when (TrackFailurePolicy.IsRecoverable(exception))
        {
            _applicationLog.Write(ApplicationLogLevel.Warning, "audio", "predecode-next-failed", exception: exception);
        }
    }

    private async Task SetPlaybackSpeedAsync(object? parameter)
    {
        if (!TryDouble(parameter, out var value)) return;
        PlaybackSpeed = value;
        await SavePlaybackProcessingAsync();
    }

    private async Task SetPitchAsync(object? parameter)
    {
        if (!TryDouble(parameter, out var value)) return;
        PitchSemitones = value;
        await SavePlaybackProcessingAsync();
    }

    private async Task TogglePreservePitchAsync()
    {
        PreservePitch = !PreservePitch;
        await SavePlaybackProcessingAsync();
        ShowNotice(PreservePitch ? "Pitch preservation enabled" : "Pitch preservation disabled");
    }

    private async Task ResetPlaybackProcessingAsync()
    {
        PlaybackSpeed = 1;
        PitchSemitones = 0;
        PreservePitch = true;
        await SavePlaybackProcessingAsync();
        ShowNotice("Playback speed and pitch reset");
    }

    private async Task SavePlaybackProcessingAsync()
    {
        await _settings.UpdateAsync(settings =>
        {
            settings.PlaybackSpeed = PlaybackSpeed;
            settings.PitchSemitones = PitchSemitones;
            settings.PreservePitch = PreservePitch;
        }, _lifetime.Token);
        await _audio.SetPlaybackOptionsAsync(CurrentPlaybackOptions(CurrentTrack), _lifetime.Token);
        Raise(nameof(PlaybackProcessingText));
    }

    private async Task SaveTrackPlaybackOverrideAsync()
    {
        if (CurrentTrack is not { } track) return;
        await _settings.UpdateAsync(settings => settings.TrackPlaybackOverrides[track.Path] = new TrackPlaybackOverrideSettings
        {
            Speed = PlaybackSpeed,
            PitchSemitones = PitchSemitones,
            PreservePitch = PreservePitch
        }, _lifetime.Token);
        Raise(nameof(CurrentTrackHasPlaybackOverride));
        Raise(nameof(PlaybackProcessingText));
        ClearTrackPlaybackOverrideCommand.RaiseCanExecuteChanged();
        ShowNotice("Playback settings saved for this track");
    }

    private async Task ClearTrackPlaybackOverrideAsync()
    {
        if (CurrentTrack is not { } track) return;
        await _settings.UpdateAsync(settings => settings.TrackPlaybackOverrides.Remove(track.Path), _lifetime.Token);
        PlaybackSpeed = _settings.Current.PlaybackSpeed;
        PitchSemitones = _settings.Current.PitchSemitones;
        PreservePitch = _settings.Current.PreservePitch;
        await _audio.SetPlaybackOptionsAsync(CurrentPlaybackOptions(track), _lifetime.Token);
        Raise(nameof(CurrentTrackHasPlaybackOverride));
        Raise(nameof(PlaybackProcessingText));
        ClearTrackPlaybackOverrideCommand.RaiseCanExecuteChanged();
        ShowNotice("Track playback override removed");
    }

    private async Task SetCurrentRatingAsync(object? parameter)
    {
        if (!int.TryParse(parameter?.ToString(), out var rating)) return;
        await SetRatingAsync(rating == CurrentRating ? 0 : rating);
        ShowNotice(CurrentRating == 0 ? "Rating cleared" : $"Rated {CurrentRating} stars");
    }

    public async Task SetTrackRatingAsync(Track track, int rating)
    {
        rating = Math.Clamp(rating, 0, 5);
        await _repository.SetRatingAsync(track.Id, rating, track.IsLoved, _lifetime.Token);
        ReplaceTrackState(track with { Rating = rating });
        ShowNotice(rating == 0 ? "Rating cleared" : $"Rated {rating} stars");
    }

    private async Task AddBookmarkAsync(string? name)
    {
        if (CurrentTrack is not { Id: > 0 } track) return;
        name = string.IsNullOrWhiteSpace(name) ? $"Bookmark {FormatTime(_audio.Snapshot.Position)}" : name.Trim();
        await _repository.CreateBookmarkAsync(track.Id, name, _audio.Snapshot.Position, _lifetime.Token);
        await LoadBookmarksAsync(track);
        ShowNotice($"Bookmark added at {FormatTime(_audio.Snapshot.Position)}");
    }

    private async Task ToggleBookmarkResumeAsync()
    {
        await _settings.UpdateAsync(settings => settings.ResumeTrackBookmarks = !settings.ResumeTrackBookmarks, _lifetime.Token);
        Raise(nameof(ResumeTrackBookmarks));
        ShowNotice(ResumeTrackBookmarks ? "Automatic track resume enabled" : "Automatic track resume disabled");
    }

    private async Task SeekBookmarkAsync(PlaybackBookmark? bookmark)
    {
        if (bookmark is null) return;
        await CommitSeekAsync(bookmark.Position.TotalSeconds);
    }

    private async Task RenameBookmarkAsync(object? parameter)
    {
        if (parameter is not BookmarkRenameRequest request || string.IsNullOrWhiteSpace(request.Name)) return;
        await _repository.RenameBookmarkAsync(request.Bookmark.Id, request.Name.Trim(), _lifetime.Token);
        if (CurrentTrack is { } track) await LoadBookmarksAsync(track);
        ShowNotice("Bookmark renamed");
    }

    private async Task DeleteBookmarkAsync(PlaybackBookmark? bookmark)
    {
        if (bookmark is null) return;
        await _repository.DeleteBookmarkAsync(bookmark.Id, _lifetime.Token);
        if (CurrentTrack is { } track) await LoadBookmarksAsync(track);
        ShowNotice("Bookmark removed");
    }

    private async Task LoadBookmarksAsync(Track track)
    {
        var bookmarks = track.Id > 0
            ? await _repository.GetBookmarksAsync(track.Id, _lifetime.Token)
            : [];
        RunOnUi(() =>
        {
            Replace(Bookmarks, bookmarks);
            Raise(nameof(HasBookmarks));
        });
    }

    private static bool TryDouble(object? parameter, out double value) =>
        double.TryParse(parameter?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
        || double.TryParse(parameter?.ToString(), out value);

    private async Task ClearSearchHistoryAsync()
    {
        await _settings.UpdateAsync(settings => settings.SearchHistory.Clear(), _lifetime.Token);
        Raise(nameof(SearchHistory));
        Raise(nameof(SearchSuggestions));
    }

    private void ToggleTrackColumn(string? column)
    {
        if (string.IsNullOrWhiteSpace(column) || !TrackColumnNames.Contains(column, StringComparer.OrdinalIgnoreCase)) return;
        var viewName = CurrentView;
        var view = GetViewSettings();
        if (view.VisibleColumns.Contains(column, StringComparer.OrdinalIgnoreCase)) view.VisibleColumns.RemoveAll(item => item.Equals(column, StringComparison.OrdinalIgnoreCase));
        else view.VisibleColumns.Add(column);
        _ = _settings.UpdateAsync(settings => settings.ViewSettings[viewName] = view);
        Raise(nameof(VisibleTrackColumns));
    }

    private void SetDensity(string? value)
    {
        if (!Enum.TryParse<LibraryDensity>(value, ignoreCase: true, out var density)) return;
        Density = density;
    }

    private void SetAlbumTileSize(string? value)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var size)) return;
        AlbumTileSize = Math.Clamp(size, 80, 400);
    }

    private void MoveTrackColumn(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter)) return;
        var parts = parameter.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TrackColumnNames.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return;
        var order = GetViewSettings().ColumnOrder;
        if (order.Count == 0)
            order.AddRange(TrackColumnNames);
        var index = order.FindIndex(item => item.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var target = parts[1].Equals("left", StringComparison.OrdinalIgnoreCase) ? index - 1 : index + 1;
        if (target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        var viewName = CurrentView;
        var view = GetViewSettings();
        _ = _settings.UpdateAsync(settings => settings.ViewSettings[viewName] = view);
        Raise(nameof(TrackColumnOrder));
    }

    private void SetTrackColumnWidth(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter)) return;
        var parts = parameter.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TrackColumnNames.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width)) return;
        var viewName = CurrentView;
        var view = GetViewSettings();
        view.ColumnWidths[parts[0]] = Math.Clamp(width, 40, 800);
        _ = _settings.UpdateAsync(settings => settings.ViewSettings[viewName] = view);
        Raise(nameof(TrackColumnWidths));
    }

    private void ToggleDashboardModule(string? module)
    {
        if (string.IsNullOrWhiteSpace(module) || !DashboardModuleNames.Contains(module, StringComparer.OrdinalIgnoreCase)) return;
        var modules = _dashboardModules.ToList();
        var existing = modules.FirstOrDefault(item => item.Equals(module, StringComparison.OrdinalIgnoreCase));
        if (existing is null) modules.Add(DashboardModuleNames.First(item => item.Equals(module, StringComparison.OrdinalIgnoreCase)));
        else modules.Remove(existing);
        if (modules.Count == 0) modules.Add("Artwork");
        _dashboardModules = modules;
        _ = _settings.UpdateAsync(settings => settings.DashboardModules = modules.ToList());
        Raise(nameof(DashboardModules));
        Raise(nameof(IsDashboardModuleVisible));
        Raise(nameof(IsArtworkDashboardAvailable));
        NormalizeInspectorSelection();
        RaiseInspectorState();
    }

    private void SelectNowPlayingInspector(string? tab)
    {
        if (tab is null
            || tab.Equals("Artwork", StringComparison.OrdinalIgnoreCase)
            || !DashboardModuleNames.Contains(tab, StringComparer.OrdinalIgnoreCase)
            || !IsDashboardModuleVisible(tab))
            return;

        _nowPlayingInspectorTab = tab;
        RaiseInspectorState();
    }

    private void NormalizeInspectorSelection()
    {
        if (_nowPlayingInspectorTab.Equals("Lyrics", StringComparison.OrdinalIgnoreCase)
            && IsLyricsInspectorAvailable)
            return;
        if (_nowPlayingInspectorTab.Equals("Metadata", StringComparison.OrdinalIgnoreCase)
            && IsMetadataInspectorAvailable)
            return;
        _nowPlayingInspectorTab = IsLyricsInspectorAvailable
            ? "Lyrics"
            : "Metadata";
    }

    private void RaiseInspectorState()
    {
        Raise(nameof(IsNowPlayingInspectorVisible));
        Raise(nameof(IsLyricsInspectorAvailable));
        Raise(nameof(IsMetadataInspectorAvailable));
        Raise(nameof(IsLyricsInspectorSelected));
        Raise(nameof(IsMetadataInspectorSelected));
    }

    private void RequestPlaylistEdit(Playlist? playlist) => PlaylistEditRequested?.Invoke(this, new PlaylistEditContext(playlist));
    private void RequestPlaylistEdit(PlaylistEditContext context) => PlaylistEditRequested?.Invoke(this, context);

    private async Task EditPlaylistAsync(LibraryCardViewModel? card)
    {
        if (card?.PlaylistId is not { } id) return;
        var playlist = await _playlists.GetAsync(id, _lifetime.Token);
        if (playlist is not null) RequestPlaylistEdit(playlist);
    }

    public async Task SavePlaylistAsync(Playlist? existing, PlaylistEditRequest request, IReadOnlyList<Track>? initialTracks = null)
    {
        if (existing is null)
        {
            var id = request.Kind == PlaylistKind.Smart
                ? await _playlists.CreateSmartAsync(request.Name, request.Rules ?? new SmartPlaylistDefinition(), _lifetime.Token)
                : await _playlists.CreateManualAsync(request.Name, _lifetime.Token);
            await _playlists.UpdateDetailsAsync(id, request.Description, request.CoverPath, _lifetime.Token);
            if (initialTracks is { Count: > 0 }) await _playlists.AddTracksAsync(id, initialTracks.Select(track => track.Id).ToArray(), _lifetime.Token);
            PushPlaylistHistory(
                async () => await _playlists.DeleteAsync(id, _lifetime.Token),
                async () =>
                {
                    var recreated = request.Kind == PlaylistKind.Smart
                        ? await _playlists.CreateSmartAsync(request.Name, request.Rules ?? new SmartPlaylistDefinition(), _lifetime.Token)
                        : await _playlists.CreateManualAsync(request.Name, _lifetime.Token);
                    await _playlists.UpdateDetailsAsync(recreated, request.Description, request.CoverPath, _lifetime.Token);
                    if (initialTracks is { Count: > 0 }) await _playlists.AddTracksAsync(recreated, initialTracks.Select(track => track.Id).ToArray(), _lifetime.Token);
                });
            StatusText = $"Playlist '{request.Name}' created";
            ShowNotice(StatusText, ToastSeverity.Success);
        }
        else
        {
            var before = existing;
            await _playlists.RenameAsync(existing.Id, request.Name, _lifetime.Token);
            await _playlists.UpdateDetailsAsync(existing.Id, request.Description, request.CoverPath, _lifetime.Token);
            if (existing.Kind == PlaylistKind.Smart && request.Rules is not null) await _playlists.UpdateSmartRulesAsync(existing.Id, request.Rules, _lifetime.Token);
            PushPlaylistHistory(
                async () =>
                {
                    await _playlists.RenameAsync(before.Id, before.Name, _lifetime.Token);
                    await _playlists.UpdateDetailsAsync(before.Id, before.Description, before.CoverPath, _lifetime.Token);
                    if (before.Kind == PlaylistKind.Smart && before.Rules is not null) await _playlists.UpdateSmartRulesAsync(before.Id, before.Rules, _lifetime.Token);
                },
                async () =>
                {
                    await _playlists.RenameAsync(before.Id, request.Name, _lifetime.Token);
                    await _playlists.UpdateDetailsAsync(before.Id, request.Description, request.CoverPath, _lifetime.Token);
                    if (before.Kind == PlaylistKind.Smart && request.Rules is not null) await _playlists.UpdateSmartRulesAsync(before.Id, request.Rules, _lifetime.Token);
                });
            StatusText = $"Playlist '{request.Name}' updated";
            ShowNotice(StatusText, ToastSeverity.Success);
        }
        _ = BackupPlaylistsAsync();
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        RefreshPlaylistHistoryCommands();
    }

    private async Task DuplicatePlaylistAsync(LibraryCardViewModel? card)
    {
        if (card?.PlaylistId is not { } id) return;
        var duplicate = await _playlists.DuplicateAsync(id, null, _lifetime.Token);
        PushPlaylistHistory(async () => await _playlists.DeleteAsync(duplicate, _lifetime.Token), async () => await _playlists.DuplicateAsync(id, null, _lifetime.Token));
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        _ = BackupPlaylistsAsync();
        StatusText = "Playlist duplicated";
        ShowNotice(StatusText, ToastSeverity.Success);
    }

    private async Task DeletePlaylistAsync(LibraryCardViewModel? card)
    {
        if (card?.PlaylistId is not { } id) return;
        var playlist = await _playlists.GetAsync(id, _lifetime.Token);
        if (playlist is null) return;
        var tracks = playlist.Kind == PlaylistKind.Manual ? await _playlists.GetTracksAsync(id, _lifetime.Token) : [];
        await _playlists.DeleteAsync(id, _lifetime.Token);
        long? restoredId = null;
        PushPlaylistHistory(
            async () =>
            {
                restoredId = playlist.Kind == PlaylistKind.Smart
                    ? await _playlists.CreateSmartAsync(playlist.Name, playlist.Rules ?? new SmartPlaylistDefinition(), _lifetime.Token)
                    : await _playlists.CreateManualAsync(playlist.Name, _lifetime.Token);
                await _playlists.UpdateDetailsAsync(restoredId.Value, playlist.Description, playlist.CoverPath, _lifetime.Token);
                if (tracks.Count > 0) await _playlists.AddTracksAsync(restoredId.Value, tracks.Select(track => track.Id).ToArray(), _lifetime.Token);
            },
            async () => { if (restoredId is { } idToDelete) await _playlists.DeleteAsync(idToDelete, _lifetime.Token); });
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        _ = BackupPlaylistsAsync();
        StatusText = $"Playlist '{playlist.Name}' deleted";
        ShowNotice(StatusText, ToastSeverity.Success);
    }

    private async Task AddSelectedToPlaylistAsync(LibraryCardViewModel? card)
    {
        if (card?.PlaylistId is not { } id || SelectedTracks.Count == 0) return;
        var before = await _playlists.GetTracksAsync(id, _lifetime.Token);
        var selected = SelectedTracks.ToArray();
        await _playlists.AddTracksAsync(id, selected.Select(track => track.Id).ToArray(), _lifetime.Token);
        PushPlaylistHistory(
            async () => await _playlists.ReplaceTracksAsync(id, before.Select(track => track.Id).ToArray(), _lifetime.Token),
            async () => await _playlists.AddTracksAsync(id, selected.Select(track => track.Id).ToArray(), _lifetime.Token));
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        _ = BackupPlaylistsAsync();
        StatusText = $"Added {SelectedTracks.Count:N0} tracks to playlist";
        ShowNotice(StatusText, ToastSeverity.Success);
    }

    public async Task AddPathsToPlaylistAsync(LibraryCardViewModel? card, IEnumerable<string> paths)
    {
        if (card?.PlaylistId is not { } id) return;
        var pathSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tracks = _allTracks.Where(track => pathSet.Contains(track.Path)).ToArray();
        if (tracks.Length == 0) return;
        var before = await _playlists.GetTracksAsync(id, _lifetime.Token);
        await _playlists.AddTracksAsync(id, tracks.Select(track => track.Id).ToArray(), _lifetime.Token);
        PushPlaylistHistory(
            async () => await _playlists.ReplaceTracksAsync(id, before.Select(track => track.Id).ToArray(), _lifetime.Token),
            async () => await _playlists.AddTracksAsync(id, tracks.Select(track => track.Id).ToArray(), _lifetime.Token));
        _ = BackupPlaylistsAsync();
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        StatusText = $"Added {tracks.Length:N0} tracks to playlist";
        ShowNotice(StatusText, ToastSeverity.Success);
    }

    public async Task MoveTracksInPlaylistAsync(LibraryCardViewModel? card, IEnumerable<string> paths, int destinationIndex)
    {
        if (card?.PlaylistId is not { } id) return;
        var playlist = await _playlists.GetAsync(id, _lifetime.Token);
        if (playlist?.Kind != PlaylistKind.Manual) return;
        var pathSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = await _playlists.GetTracksAsync(id, _lifetime.Token);
        var selectedIds = before.Where(track => pathSet.Contains(track.Path)).Select(track => track.Id).ToArray();
        if (selectedIds.Length == 0) return;
        var after = before.Where(track => !selectedIds.Contains(track.Id)).ToList();
        destinationIndex = Math.Clamp(destinationIndex, 0, after.Count);
        after.InsertRange(destinationIndex, before.Where(track => selectedIds.Contains(track.Id)));
        await _playlists.MoveTracksAsync(id, selectedIds, destinationIndex, _lifetime.Token);
        PushPlaylistHistory(
            async () => await _playlists.ReplaceTracksAsync(id, before.Select(track => track.Id).ToArray(), _lifetime.Token),
            async () => await _playlists.ReplaceTracksAsync(id, after.Select(track => track.Id).ToArray(), _lifetime.Token));
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        _ = BackupPlaylistsAsync();
        StatusText = $"Moved {selectedIds.Length:N0} playlist track{(selectedIds.Length == 1 ? "" : "s")}";
    }

    public void AddPathsToQueue(
        IEnumerable<string> paths,
        int? targetPlaybackIndex = null)
    {
        var tracks = MatchUniqueQueueTracks(_allTracks, paths);
        if (tracks.Length == 0) return;
        if (targetPlaybackIndex is > 0)
        {
            _queue.InsertAtPlaybackIndex(tracks, targetPlaybackIndex.Value);
            return;
        }
        _queue.Add(tracks);
    }

    internal static Track[] MatchUniqueQueueTracks(
        IEnumerable<Track> library,
        IEnumerable<string> paths)
    {
        var byPath = library
            .Where(track => !track.IsMissing && !string.IsNullOrWhiteSpace(track.Path))
            .GroupBy(track => track.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => byPath.GetValueOrDefault(path))
            .Where(track => track is not null)
            .Cast<Track>()
            .ToArray();
    }

    public IReadOnlyList<string> GetCardPaths(LibraryCardViewModel? card) => card is null ? [] : card.PlaylistId is null ? card.TrackIndexes.Where(index => index >= 0 && index < _allTracks.Count).Select(index => _allTracks[index].Path).ToArray() : [];

    public string? GetTrackArtworkPath(Track track)
    {
        var artwork = ExistingArtwork(track);
        return artwork is null
               && _resolvedArtwork.TryGet(track.Path, out var cached)
            ? cached
            : artwork;
    }

    public async Task<long> ImportPlaylistFileAsync(string path)
    {
        var id = await _playlistFiles.ImportAsync(path, _lifetime.Token);
        _ = BackupPlaylistsAsync();
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        var report = _playlistFiles.LastImportReport;
        var importedCount = report?.ImportedTracks ?? 0;
        StatusText = report is { MissingLocations: > 0 }
            ? $"Playlist imported · {importedCount:N0} tracks · {report.MissingLocations:N0} missing locations"
            : $"Playlist imported · {importedCount:N0} tracks";
        return id;
    }

    public async Task ExportPlaylistFileAsync(long playlistId, string path, PlaylistFormat format)
    {
        await _playlistFiles.ExportAsync(playlistId, path, format, _lifetime.Token);
        StatusText = "Playlist exported";
    }

    private void PushPlaylistHistory(Func<Task> undo, Func<Task> redo)
    {
        _playlistUndo.Push(new PlaylistHistoryEntry(undo, redo));
        _playlistRedo.Clear();
        RefreshPlaylistHistoryCommands();
    }

    private void RefreshPlaylistHistoryCommands()
    {
        UndoPlaylistCommand.RaiseCanExecuteChanged();
        RedoPlaylistCommand.RaiseCanExecuteChanged();
    }

    private async Task BackupPlaylistsAsync()
    {
        try { await _playlistBackups.BackupAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private async Task UndoPlaylistAsync()
    {
        if (_playlistUndo.Count == 0) return;
        var entry = _playlistUndo.Pop();
        await entry.Undo();
        _playlistRedo.Push(entry);
        _ = BackupPlaylistsAsync();
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        StatusText = "Playlist change undone";
        RefreshPlaylistHistoryCommands();
    }

    private async Task RedoPlaylistAsync()
    {
        if (_playlistRedo.Count == 0) return;
        var entry = _playlistRedo.Pop();
        await entry.Redo();
        _playlistUndo.Push(entry);
        _ = BackupPlaylistsAsync();
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        StatusText = "Playlist change redone";
        RefreshPlaylistHistoryCommands();
    }
    public event EventHandler? NavigationStarting;
    public event EventHandler<PlaylistEditContext>? PlaylistEditRequested;

    public async Task InitializeAsync()
    {
        await InitializeShellAsync();
        await InitializeLibraryAsync();
    }

    public void EnableSafeMode()
    {
        IsSafeMode = true;
        _animationsEnabled = false;
        _visualizerEnabled = false;
        _audio.SetVisualizationEnabled(false);
        _queueVisible = false;
        Raise(nameof(AnimationsEnabled));
        RaiseQueuePanelState();
    }

    public Task InitializeShellAsync() =>
        _shellInitialization ??= _diagnostics.MeasureAsync(
            "startup",
            "view-model.shell-initialize",
            InitializeShellCoreAsync);

    public Task InitializeLibraryAsync() =>
        _libraryInitialization ??= _diagnostics.MeasureAsync(
            "startup",
            "view-model.library-initialize",
            InitializeLibraryCoreAsync);

    private async Task InitializeShellCoreAsync()
    {
        await _settings.InitializeAsync(_lifetime.Token);
        SettingsWorkspace.Reload();
        ApplyWorkspaceTheme();
        LoadViewSettings();
        _volume = _settings.Current.Volume; Raise(nameof(Volume));
        _queueVisible = !IsSafeMode && _settings.Current.QueuePanelVisible;
        _queuePanelCompact = _settings.Current.QueuePanelCompact;
        _queuePanelDockSide = _settings.Current.QueuePanelDockSide;
        RaiseQueuePanelState();
        _albumTileSize = _settings.Current.AlbumTileSize; Raise(nameof(AlbumTileSize)); Raise(nameof(GalleryItemWidth)); Raise(nameof(GalleryItemHeight));
        _dashboardModules = NormalizeDashboardModules(_settings.Current.DashboardModules);
        NormalizeInspectorSelection();
        Raise(nameof(DashboardModules));
        RaiseInspectorState();
        _animationsEnabled = !IsSafeMode && _settings.Current.AnimationsEnabled; Raise(nameof(AnimationsEnabled));
        _visualizerEnabled = !IsSafeMode && _settings.Current.VisualizerEnabled;
        _audio.SetVisualizationEnabled(_visualizerEnabled);
        Raise(nameof(VisualizerEnabled));
        Raise(nameof(VisualizerText));
        _artworkCacheMegabytes = _settings.Current.ArtworkCacheMegabytes;
        _scheduledScanEnabled = _settings.Current.ScheduledLibraryScanEnabled;
        _scheduledScanIntervalMinutes = _settings.Current.ScheduledLibraryScanIntervalMinutes;
        _allowScheduledScanOnBattery = _settings.Current.AllowScheduledScanOnBattery;
        _allowScheduledScanOnMeteredNetwork = _settings.Current.AllowScheduledScanOnMeteredNetwork;
        Raise(nameof(ScheduledScanEnabled));
        Raise(nameof(ScheduledScanIntervalMinutes));
        Raise(nameof(AllowScheduledScanOnBattery));
        Raise(nameof(AllowScheduledScanOnMeteredNetwork));
        Raise(nameof(ArtworkCacheMegabytes)); Raise(nameof(ArtworkCacheLimitText));
        _replayGainMode = _settings.Current.ReplayGainMode;
        _replayGainPreampDb = _settings.Current.ReplayGainPreampDb;
        _preventClipping = _settings.Current.PreventClipping;
        _playbackSpeed = _settings.Current.PlaybackSpeed;
        _pitchSemitones = _settings.Current.PitchSemitones;
        _preservePitch = _settings.Current.PreservePitch;
        _stopAfterCurrent = _settings.Current.StopAfterCurrent;
        _stopAfterQueue = _settings.Current.StopAfterQueue;
        ApplyLyricsSettings();
        Raise(nameof(ReplayGainMode));
        Raise(nameof(ReplayGainPreampDb));
        Raise(nameof(PreventClipping));
        Raise(nameof(PlaybackSpeed));
        Raise(nameof(PitchSemitones));
        Raise(nameof(PreservePitch));
        Raise(nameof(StopAfterCurrent));
        Raise(nameof(StopAfterQueue));
        Raise(nameof(HasStopMode));
        Raise(nameof(StopModeText));
        _shortcuts.Refresh(_settings.Current.Shortcuts);
        StatusText = IsSafeMode
            ? "Safe mode · session restore and visual effects are disabled"
            : "Loading your library in the background…";
    }

    private async Task InitializeLibraryCoreAsync()
    {
        await _repository.InitializeAsync(_lifetime.Token);
        var refreshLibrary = RefreshLibraryAsync(cancellationToken: _lifetime.Token);
        var refreshArtwork = RefreshArtworkCacheStatsAsync();
        await RefreshOutputDevicesAsync();
        var profile = _settings.Current.OutputProfiles.FirstOrDefault(x => x.DeviceId == _settings.Current.ActiveOutputDeviceId) ?? _settings.Current.OutputProfiles[0];
        await _audio.SetPlaybackOptionsAsync(
            CurrentPlaybackOptions(),
            _lifetime.Token);
        await _audio.ConfigureOutputAsync(profile, _lifetime.Token);
        await _audio.SetVolumeAsync(_volume, _lifetime.Token);
        await Task.WhenAll(refreshLibrary, refreshArtwork);
        RestartSourceWatchers();
        await RefreshLibrarySourceCountsAsync();
        if (!IsSafeMode)
            await RestoreSessionAsync();
        IsLibraryReady = true;
        _lastCompletedLibraryScan = DateTimeOffset.UtcNow;
        _scheduledScanTimer.Start();
        StartArtworkReferenceReconciliation();
    }

    private async void ScheduledScanTimerOnTick(object? sender, EventArgs e)
    {
        if (!IsLibraryReady || _scanner.IsScanning || EnabledSourcePaths().Count == 0) return;
        if (DateTimeOffset.UtcNow - _lastCompletedLibraryScan < TimeSpan.FromMinutes(ScheduledScanIntervalMinutes)) return;
        var decision = ScheduledScanDecision.Evaluate(_settings.Current, ScheduledScanEnvironment.Capture());
        if (!decision.CanScan)
        {
            ScheduledScanStatus = decision.Reason;
            return;
        }
        try
        {
            ScheduledScanStatus = "Scheduled scan running…";
            await ScanAsync();
            ScheduledScanStatus = $"Last scheduled scan {DateTime.Now:g}";
        }
        catch (Exception exception)
        {
            ScheduledScanStatus = "Scheduled scan failed · " + exception.GetBaseException().Message;
        }
        finally
        {
            _lastCompletedLibraryScan = DateTimeOffset.UtcNow;
        }
    }

    private async Task RefreshLibrarySourceCountsAsync()
    {
        var statuses = _scanner.SourceStatuses.ToDictionary(status => status.Root, StringComparer.OrdinalIgnoreCase);
        var refreshed = new List<LibrarySourceViewModel>(_settings.Current.LibrarySources.Count);
        foreach (var source in _settings.Current.LibrarySources)
        {
            statuses.TryGetValue(source.Path, out var status);
            try
            {
                var count = await _repository.CountUnderRootAsync(
                    source.Path,
                    _lifetime.Token);
                status = (status ?? new LibrarySourceStatus(
                    source.Path,
                    LibrarySourceKind.Unknown,
                    Directory.Exists(source.Path),
                    false,
                    null,
                    Directory.Exists(source.Path) ? null : "Source is offline.")) with { TrackCount = count };
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
            refreshed.Add(new LibrarySourceViewModel(source, status));
        }
        Replace(LibrarySources, refreshed);
        Replace(LibraryExclusions, _settings.Current.ExcludedFolders);
        RaiseLibraryState();
    }

    public async Task SaveReplayGainSettingsAsync()
    {
        await _settings.UpdateAsync(
            settings =>
            {
                settings.ReplayGainMode = ReplayGainMode;
                settings.ReplayGainPreampDb = ReplayGainPreampDb;
                settings.PreventClipping = PreventClipping;
                settings.PlaybackSpeed = PlaybackSpeed;
                settings.PitchSemitones = PitchSemitones;
                settings.PreservePitch = PreservePitch;
            },
            _lifetime.Token);
        await _audio.SetPlaybackOptionsAsync(
            CurrentPlaybackOptions(),
            _lifetime.Token);
        ReplayGainAnalysisStatus =
            $"Playback processing saved · {ReplayGainMode} gain · " +
            $"{ReplayGainPreampDb:+0.0;-0.0;0.0} dB preamp · " +
            (PreventClipping
                ? "sample-peak guard on"
                : "sample-peak guard off") +
            $" · {PlaybackSpeed:0.00}× speed · " +
            $"{PitchSemitones:+0.0;-0.0;0.0} semitones";
    }

    public async Task AnalyzeMissingReplayGainAsync()
    {
        if (IsReplayGainAnalysisBusy) return;
        _replayGainAnalysisCancellation?.Dispose();
        _replayGainAnalysisCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token);
        var token = _replayGainAnalysisCancellation.Token;
        IsReplayGainAnalysisBusy = true;
        ReplayGainAnalysisProgress = 0;
        ReplayGainAnalysisStatus =
            "Preparing missing track and album gain analysis…";
        var progress = new Progress<ReplayGainAnalysisProgress>(item =>
        {
            ReplayGainAnalysisProgress = item.Total == 0
                ? 0
                : item.Completed * 100d / item.Total;
            ReplayGainAnalysisStatus = string.IsNullOrWhiteSpace(
                item.CurrentTrack)
                ? item.State
                : $"{item.State} · {item.Completed:N0}/{item.Total:N0} · " +
                  item.CurrentTrack;
        });
        try
        {
            var summary = await _replayGainAnalysis.AnalyzeMissingAsync(
                _allTracks,
                progress,
                token);
            ReplayGainAnalysisProgress = 100;
            ReplayGainAnalysisStatus = summary.Analyzed == 0
                ? "Every available track already has loudness and peak data."
                : $"Analyzed {summary.Analyzed:N0} · updated " +
                  $"{summary.Updated:N0} · skipped {summary.Failed:N0}";
            if (summary.Updated > 0)
                await RefreshLibraryAsync(cancellationToken: token);
        }
        catch (OperationCanceledException)
        {
            ReplayGainAnalysisStatus =
                "Loudness analysis cancelled. Completed results were kept.";
        }
        finally
        {
            IsReplayGainAnalysisBusy = false;
        }
    }

    public void CancelReplayGainAnalysis() =>
        _replayGainAnalysisCancellation?.Cancel();

    private AudioPlaybackOptions CurrentPlaybackOptions(Track? track = null)
    {
        var profile = _settings.Current.OutputProfiles.FirstOrDefault(
                          output => output.DeviceId ==
                                    _settings.Current.ActiveOutputDeviceId)
                      ?? _settings.Current.OutputProfiles[0];
        return new AudioPlaybackOptions
        {
            ReplayGainMode = ReplayGainMode,
            ReplayGainPreampDb = ReplayGainPreampDb,
            PreventClipping = PreventClipping,
            TransitionMode = profile.CrossfadeSeconds > 0
                ? TransitionMode.Crossfade
                : _settings.Current.TransitionMode,
            CrossfadeSeconds = profile.CrossfadeSeconds > 0
                ? profile.CrossfadeSeconds
                : _settings.Current.CrossfadeSeconds,
            FadeInSeconds = _settings.Current.FadeInSeconds,
            FadeOutSeconds = _settings.Current.FadeOutSeconds,
            Speed = PlaybackSpeed,
            PitchSemitones = PitchSemitones,
            PreservePitch = PreservePitch
        };
    }

    private void ApplyTrackPlaybackSettings(Track track)
    {
        if (_settings.Current.TrackPlaybackOverrides.TryGetValue(track.Path, out var trackOverride))
        {
            PlaybackSpeed = trackOverride.Speed;
            PitchSemitones = trackOverride.PitchSemitones;
            PreservePitch = trackOverride.PreservePitch;
        }
        else
        {
            PlaybackSpeed = _settings.Current.PlaybackSpeed;
            PitchSemitones = _settings.Current.PitchSemitones;
            PreservePitch = _settings.Current.PreservePitch;
        }
        Raise(nameof(CurrentTrackHasPlaybackOverride));
        Raise(nameof(PlaybackProcessingText));
    }

    public async Task RefreshOutputDevicesAsync()
    {
        IsOutputProfileBusy = true;
        try
        {
            var devices = await _audio.GetOutputDevicesAsync(_lifetime.Token);
            Replace(OutputDevices, devices);
            var activeId = _settings.Current.ActiveOutputDeviceId;
            SelectedOutputDevice = devices.FirstOrDefault(device =>
                                       device.Id.Equals(
                                           activeId,
                                           StringComparison.OrdinalIgnoreCase))
                                   ?? devices.FirstOrDefault();
            if (SelectedOutputDevice is not null)
                await SelectOutputDeviceAsync(SelectedOutputDevice);
            else
            {
                OutputCapabilities = null;
                OutputProfileStatus = "No active Windows output endpoint was found.";
            }
        }
        catch (Exception exception)
        {
            OutputCapabilities = null;
            OutputProfileStatus =
                "Output discovery failed: " + exception.GetBaseException().Message;
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "audio",
                "device-discovery-failed",
                exception: exception);
        }
        finally
        {
            IsOutputProfileBusy = false;
        }
    }

    public async Task RefreshDecoderCapabilitiesAsync(
        bool forceRefresh = false)
    {
        if (IsDecoderCapabilityBusy) return;
        IsDecoderCapabilityBusy = true;
        DecoderCapabilityStatus = "Checking installed decoder paths…";
        try
        {
            var capabilities = await _decoderCapabilities.InspectAsync(
                forceRefresh,
                _lifetime.Token);
            Replace(DecoderCapabilities, capabilities);
            var unavailable = capabilities.Count(
                capability => capability.State
                    == DecoderCapabilityState.Unavailable);
            DecoderCapabilityStatus = unavailable == 0
                ? $"All {capabilities.Count} codec paths are available."
                : $"{unavailable} of {capabilities.Count} codec paths are unavailable.";
        }
        finally
        {
            IsDecoderCapabilityBusy = false;
        }
    }

    public async Task SelectOutputDeviceAsync(AudioDeviceInfo device)
    {
        SelectedOutputDevice = device;
        var existing = _settings.Current.OutputProfiles.FirstOrDefault(
            profile => profile.DeviceId.Equals(
                device.Id,
                StringComparison.OrdinalIgnoreCase));
        OutputProfile.Load(
            existing ?? AudioOutputProfileDefaults.For(device));
        IsOutputProfileBusy = true;
        OutputProfileStatus = "Querying exclusive-mode formats…";
        try
        {
            OutputCapabilities = await _audio.GetDeviceCapabilitiesAsync(
                device.Id,
                _lifetime.Token);
            OutputProfileStatus = SupportedExclusiveFormats.Count == 0
                ? "This endpoint reported no tested exclusive PCM formats. Shared mode remains available."
                : $"{SupportedExclusiveFormats.Count:N0} exclusive formats accepted · event-driven WASAPI available";
        }
        catch (Exception exception)
        {
            OutputCapabilities = null;
            OutputProfileStatus =
                "Capability query failed: " + exception.GetBaseException().Message;
        }
        finally
        {
            IsOutputProfileBusy = false;
        }
    }

    public async Task SaveOutputProfileAsync()
    {
        if (SelectedOutputDevice is null) return;
        var profile = OutputProfile.ToProfile();
        profile.DeviceId = SelectedOutputDevice.Id;
        profile.Name = SelectedOutputDevice.Name;
        await _settings.UpdateAsync(
            settings =>
            {
                settings.OutputProfiles.RemoveAll(existing =>
                    existing.DeviceId.Equals(
                        profile.DeviceId,
                        StringComparison.OrdinalIgnoreCase));
                settings.OutputProfiles.Add(profile);
                settings.ActiveOutputDeviceId = profile.DeviceId;
            },
            _lifetime.Token);
        await _audio.ConfigureOutputAsync(profile, _lifetime.Token);
        await _audio.SetVolumeAsync(Volume, _lifetime.Token);
        OutputProfileStatus =
            $"Saved {profile.Name} · {profile.Mode} · {profile.BufferMilliseconds} ms";
        SettingsWorkspace.NotifyOutputProfileDraftChanged();
        Raise(nameof(DiagnosticBuffer));
    }

    public async Task AddLibraryFolderAsync(string folder)
    {
        var normalized = Path.GetFullPath(folder);
        await _settings.UpdateAsync(settings =>
        {
            var source = settings.LibrarySources.FirstOrDefault(item => item.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (source is null)
                settings.LibrarySources.Add(new LibrarySourceSettings { Path = normalized });
            else
                source.Enabled = true;
            if (!settings.LibraryFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase)) settings.LibraryFolders.Add(normalized);
        }, _lifetime.Token);
        RestartSourceWatchers();
        await ScanAsync([normalized]);
    }

    public async Task AddLibraryExclusionAsync(string folder)
    {
        var normalized = Path.GetFullPath(folder);
        await _settings.UpdateAsync(settings =>
        {
            if (!settings.ExcludedFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                settings.ExcludedFolders.Add(normalized);
        }, _lifetime.Token);
        Replace(LibraryExclusions, _settings.Current.ExcludedFolders);
    }

    private async Task RemoveExclusionAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        await _settings.UpdateAsync(settings =>
            settings.ExcludedFolders.RemoveAll(path => path.Equals(folder, StringComparison.OrdinalIgnoreCase)), _lifetime.Token);
        Replace(LibraryExclusions, _settings.Current.ExcludedFolders);
    }

    private async Task ToggleLibrarySourceAsync(LibrarySourceViewModel? source)
    {
        if (source is null) return;
        await _settings.UpdateAsync(settings =>
        {
            var configured = settings.LibrarySources.First(item => item.Path.Equals(source.Root, StringComparison.OrdinalIgnoreCase));
            configured.Enabled = !configured.Enabled;
            settings.LibraryFolders = settings.LibrarySources.Where(item => item.Enabled).Select(item => item.Path).ToList();
        }, _lifetime.Token);
        RestartSourceWatchers();
        await RefreshLibrarySourceCountsAsync();
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        UpdateScanState();
    }

    private async Task ToggleSourceWatcherAsync(LibrarySourceViewModel? source)
    {
        if (source is null) return;
        await _settings.UpdateAsync(settings =>
        {
            var configured = settings.LibrarySources.First(item => item.Path.Equals(source.Root, StringComparison.OrdinalIgnoreCase));
            configured.WatchEnabled = !configured.WatchEnabled;
        }, _lifetime.Token);
        RestartSourceWatchers();
        await RefreshLibrarySourceCountsAsync();
    }

    public async Task RemoveLibrarySourceAsync(LibrarySourceViewModel? source)
    {
        if (source is null) return;
        await _settings.UpdateAsync(settings =>
        {
            settings.LibrarySources.RemoveAll(item => item.Path.Equals(source.Root, StringComparison.OrdinalIgnoreCase));
            settings.LibraryFolders.RemoveAll(path => path.Equals(source.Root, StringComparison.OrdinalIgnoreCase));
            settings.ExcludedFolders.RemoveAll(path => IsWithinSource(path, source.Root));
        }, _lifetime.Token);
        var indexed = await _repository.GetAllAsync(_lifetime.Token);
        var ids = indexed.Where(track => IsWithinSource(track.Path, source.Root)).Select(track => track.Id).Where(id => id > 0).ToArray();
        if (ids.Length > 0) await _repository.RemoveTracksAsync(ids, _lifetime.Token);
        RestartSourceWatchers();
        await RefreshLibrarySourceCountsAsync();
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        UpdateScanState();
    }

    private Task RescanSourceAsync(LibrarySourceViewModel? source) =>
        source is null ? Task.CompletedTask : ScanAsync([source.Root]);

    private IReadOnlyList<string> EnabledSourcePaths() =>
        _settings.Current.LibrarySources.Where(source => source.Enabled).Select(source => source.Path).ToArray();

    private void RestartSourceWatchers() => _scanner.StartWatching(
        _settings.Current.LibrarySources
            .Where(source => source.Enabled && source.WatchEnabled)
            .Select(source => source.Path));

    internal static bool IsWithinSource(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWithinEnabledSources(
        string path,
        IReadOnlyList<string> activeRoots) =>
        activeRoots.Count == 0
        || activeRoots.Any(root => IsWithinSource(path, root));

    public async Task OpenLaunchTargetsAsync(IEnumerable<string> targets)
    {
        await InitializeLibraryAsync();
        var normalized = targets
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(TryFullPath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var folders = normalized.Where(Directory.Exists).ToArray();
        if (folders.Length > 0)
        {
            await _settings.UpdateAsync(settings =>
            {
                foreach (var folder in folders)
                {
                    if (!settings.LibrarySources.Any(source => source.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                        settings.LibrarySources.Add(new LibrarySourceSettings { Path = folder });
                    if (!settings.LibraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                        settings.LibraryFolders.Add(folder);
                }
            }, _lifetime.Token);
            RestartSourceWatchers();
            await ScanAsync(folders);
        }

        var files = normalized
            .Where(path => File.Exists(path) && SupportedMediaFiles.IsSupported(path))
            .ToArray();
        if (files.Length == 0) return;
        try
        {
            var tracks = new List<Track>(files.Length);
            foreach (var file in files)
                tracks.Add(await _repository.GetByPathAsync(file, _lifetime.Token)
                    ?? await _metadataReader.ReadAsync(file, _lifetime.Token));
            _queue.Replace(tracks);
            await ChangeTrackAsync(
                tracks[0],
                startReason: PlaybackStartReason.ExplicitSelection);
            StatusText = files.Length == 1
                ? $"Opened {Path.GetFileName(files[0])}"
                : $"Opened {files.Length:N0} dropped tracks";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException
                or NotSupportedException or InvalidDataException)
        {
            StatusText = $"Could not open dropped music · {exception.GetBaseException().Message}";
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "launch",
                "open-target-failed",
                new Dictionary<string, object?> { ["pathCount"] = files.Length },
                exception);
        }
    }

    private static string? TryFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
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
        var previousPosition = _audio.Snapshot.Position;
        PreviewSeek(seconds);
        try { await SeekAsync(PositionSeconds); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PositionSeconds = Math.Clamp(
                previousPosition.TotalSeconds,
                0,
                DurationSeconds);
            PositionText = FormatTime(previousPosition);
            StatusText = "Could not seek · " + exception.GetBaseException().Message;
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "audio",
                "seek-failed",
                new Dictionary<string, object?>
                {
                    ["track"] = CurrentTrack?.Path,
                    ["requestedSeconds"] = seconds,
                    ["positionSeconds"] = previousPosition.TotalSeconds
                },
                exception);
        }
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
            await StopArtworkReferenceReconciliationAsync();
            _artworkCancellation?.Cancel();
            _queueArtworkCancellation?.Cancel();
            _artworkImages.ClearMemoryCache();
            _resolvedArtwork.Clear();
            await ClearManagedArtworkReferencesAsync(_lifetime.Token);
            await _artwork.ClearAsync(_lifetime.Token);
            if (CurrentTrack is not null && _artwork.IsManagedPath(CurrentTrack.ArtworkPath))
                CurrentTrack = CurrentTrack with { ArtworkPath = null };
            await RefreshLibraryAsync(SearchText, _lifetime.Token);
            await RefreshArtworkCacheStatsAsync();
            ShowNotice("Artwork cache cleared", ToastSeverity.Success);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ArtworkCacheStatus = "Artwork cache could not be cleared · " + exception.GetBaseException().Message;
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "artwork",
                "cache-clear-failed",
                exception: exception);
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
            await StopArtworkReferenceReconciliationAsync();
            _artworkCancellation?.Cancel();
            _queueArtworkCancellation?.Cancel();
            _artworkImages.ClearMemoryCache();
            _resolvedArtwork.Clear();
            var tracks = await ClearManagedArtworkReferencesAsync(_lifetime.Token);
            await _artwork.ClearAsync(_lifetime.Token);

            var rebuilt = new ConcurrentBag<Track>();
            var failures = 0;
            var completed = 0;
            var mediaGroups = tracks
                .Where(track => !track.IsMissing && File.Exists(track.EffectiveMediaPath))
                .GroupBy(track => track.EffectiveMediaPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await Parallel.ForEachAsync(
                mediaGroups,
                new ParallelOptions
                {
                    CancellationToken = _lifetime.Token,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
                },
                async (group, cancellationToken) =>
                {
                    try
                    {
                        var artwork = await _artwork.GetOrCreateAsync(group.Key, cancellationToken);
                        if (artwork is not null)
                        {
                            foreach (var track in group)
                                rebuilt.Add(track with { ArtworkPath = artwork });
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        Interlocked.Increment(ref failures);
                        _applicationLog.Write(
                            ApplicationLogLevel.Warning,
                            "artwork",
                            "cache-rebuild-item-failed",
                            new Dictionary<string, object?> { ["mediaPath"] = group.Key },
                            exception);
                    }
                    finally
                    {
                        var current = Interlocked.Increment(ref completed);
                        if (current == mediaGroups.Length || current % 50 == 0)
                            RunOnUi(() => ArtworkCacheStatus = $"Rebuilding artwork… {current:N0} / {mediaGroups.Length:N0}");
                    }
                });
            if (!rebuilt.IsEmpty)
                await _repository.UpsertBatchAsync(rebuilt.ToArray(), _lifetime.Token);
            await RefreshLibraryAsync(SearchText, _lifetime.Token);
            await RefreshArtworkCacheStatsAsync();
            ShowNotice(failures == 0
                ? $"Rebuilt artwork for {rebuilt.Count:N0} tracks"
                : $"Rebuilt {rebuilt.Count:N0} tracks · {failures:N0} files skipped");
            _applicationLog.Write(
                ApplicationLogLevel.Information,
                "artwork",
                "cache-rebuild-completed",
                new Dictionary<string, object?>
                {
                    ["tracks"] = tracks.Count,
                    ["rebuilt"] = rebuilt.Count,
                    ["failures"] = failures
                });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ArtworkCacheStatus = "Artwork cache could not be rebuilt · " + exception.GetBaseException().Message;
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "artwork",
                "cache-rebuild-failed",
                exception: exception);
        }
        finally { IsArtworkCacheBusy = false; }
    }

    private async Task<IReadOnlyList<Track>> ClearManagedArtworkReferencesAsync(CancellationToken cancellationToken)
    {
        var tracks = await _repository.GetAllAsync(cancellationToken);
        var cleared = tracks
            .Where(track => !track.IsMissing && _artwork.IsManagedPath(track.ArtworkPath))
            .Select(track => track with { ArtworkPath = null })
            .ToArray();
        if (cleared.Length == 0) return tracks;

        await _repository.UpsertBatchAsync(cleared, cancellationToken);
        var clearedByPath = cleared.ToDictionary(track => track.Path, StringComparer.OrdinalIgnoreCase);
        return tracks
            .Select(track => clearedByPath.TryGetValue(track.Path, out var replacement) ? replacement : track)
            .ToArray();
    }

    private void StartArtworkReferenceReconciliation()
    {
        _artworkReconciliationCancellation?.Cancel();
        _artworkReconciliationCancellation?.Dispose();
        _artworkReconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _artworkReconciliationTask = ReconcileMissingArtworkReferencesAsync(_artworkReconciliationCancellation.Token);
    }

    private async Task StopArtworkReferenceReconciliationAsync()
    {
        var cancellation = _artworkReconciliationCancellation;
        if (cancellation is null) return;
        cancellation.Cancel();
        try { await _artworkReconciliationTask; }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(cancellation, _artworkReconciliationCancellation))
            {
                _artworkReconciliationCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task ReconcileMissingArtworkReferencesAsync(CancellationToken cancellationToken)
    {
        var stale = _allTracks
            .Where(track =>
                !track.IsMissing
                && File.Exists(track.EffectiveMediaPath)
                && track.ArtworkPath is { Length: > 0 } artworkPath
                && !File.Exists(artworkPath))
            .ToArray();
        if (stale.Length == 0) return;

        IsArtworkCacheBusy = true;
        ArtworkCacheStatus = $"Repairing artwork… 0 / {stale.Length:N0}";
        _artworkCancellation?.Cancel();
        _queueArtworkCancellation?.Cancel();
        var repaired = new ConcurrentBag<Track>();
        var completed = 0;
        var recovered = 0;
        var failures = 0;
        var groups = stale
            .GroupBy(track => track.EffectiveMediaPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        try
        {
            await Parallel.ForEachAsync(
                groups,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
                },
                async (group, ct) =>
                {
                    try
                    {
                        var artwork = await _artwork.GetOrCreateAsync(group.Key, ct);
                        foreach (var track in group)
                            repaired.Add(track with { ArtworkPath = artwork });
                        if (artwork is not null)
                            Interlocked.Add(ref recovered, group.Count());
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        Interlocked.Increment(ref failures);
                        _applicationLog.Write(
                            ApplicationLogLevel.Warning,
                            "artwork",
                            "reference-repair-item-failed",
                            new Dictionary<string, object?> { ["mediaPath"] = group.Key },
                            exception);
                    }
                    finally
                    {
                        var current = Interlocked.Add(ref completed, group.Count());
                        if (current >= stale.Length || current % 50 == 0)
                            RunOnUi(() => ArtworkCacheStatus = $"Repairing artwork… {Math.Min(current, stale.Length):N0} / {stale.Length:N0}");
                    }
                });

            if (!repaired.IsEmpty)
                await _repository.UpsertBatchAsync(repaired.ToArray(), cancellationToken);
            await RefreshLibraryAsync(SearchText, cancellationToken);
            await RefreshArtworkCacheStatsAsync();
            var cleared = repaired.Count - recovered;
            ShowNotice($"Artwork repaired · {recovered:N0} restored · {cleared:N0} stale references cleared");
            _applicationLog.Write(
                ApplicationLogLevel.Information,
                "artwork",
                "reference-repair-completed",
                new Dictionary<string, object?>
                {
                    ["stale"] = stale.Length,
                    ["recovered"] = recovered,
                    ["cleared"] = cleared,
                    ["failures"] = failures
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ArtworkCacheStatus = "Artwork repair could not finish · " + exception.GetBaseException().Message;
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "artwork",
                "reference-repair-failed",
                new Dictionary<string, object?> { ["stale"] = stale.Length },
                exception);
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
        var parsedSearch = LibrarySearchQuery.Parse(query);
        var indexedTracks = string.IsNullOrWhiteSpace(query)
            ? await _repository.GetAllAsync(cancellationToken)
            : parsedSearch.IsStructured
                ? await _repository.GetAllAsync(cancellationToken)
                : await _repository.SearchAsync(parsedSearch.RepositoryText, 5000, cancellationToken);
        if (!string.IsNullOrWhiteSpace(query)) indexedTracks = indexedTracks.Where(parsedSearch.Matches).ToArray();
        var playlistClauses = parsedSearch.Clauses.Where(clause => clause.Field == "playlist").ToArray();
        if (playlistClauses.Length > 0)
        {
            var playlistMembership = new Dictionary<long, HashSet<long>>();
            var playlistNames = new Dictionary<long, string>();
            foreach (var summary in await _playlists.GetSummariesAsync(cancellationToken))
            {
                playlistNames[summary.Playlist.Id] = summary.Playlist.Name;
                playlistMembership[summary.Playlist.Id] = (await _playlists.GetTracksAsync(summary.Playlist.Id, cancellationToken)).Select(track => track.Id).ToHashSet();
            }
            indexedTracks = indexedTracks.Where(track => playlistClauses.All(clause =>
            {
                var matchingPlaylists = playlistNames.Where(pair => pair.Value.Contains(clause.Value, StringComparison.CurrentCultureIgnoreCase)).Select(pair => pair.Key);
                var belongs = matchingPlaylists.Any(id => playlistMembership.TryGetValue(id, out var membership) && membership.Contains(track.Id));
                return clause.IsNegative ? !belongs : belongs;
            })).ToArray();
        }
        RememberSearch(query);
        var activeRoots = EnabledSourcePaths();
        IReadOnlyList<Track> tracks = indexedTracks
            .Where(track => IsWithinEnabledSources(track.Path, activeRoots))
            .ToArray();
        LibraryGroupSnapshot groups;
        await _groupingGate.WaitAsync(cancellationToken);
        try
        {
            groups = await Task.Run(() =>
            {
                using var scope = _diagnostics.Measure(
                    "library",
                    "group-index-reset",
                    _diagnostics.Enabled
                        ? new Dictionary<string, object?>
                        {
                            ["tracks"] = tracks.Count
                        }
                        : null);
                _groupingIndex.ConfigureSeparators(_settings.Current.MultiValueSeparators);
                return _groupingIndex.Reset(tracks);
            }, cancellationToken);
        }
        finally
        {
            _groupingGate.Release();
        }
        var folderTree = await Task.Run(
            () => FolderTreeBuilder.Build(tracks, _settings.Current.LibrarySources),
            cancellationToken);
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
            _sidebarViews.Clear();
            _trackViews.Clear();
            _playlistTrackLoads.Clear();
            _activeGalleryPresentation = null;
            _activeSidebarPresentation = null;
            _activeTrackPresentation = null;
            Replace(Albums, groups.Albums); Replace(Artists, groups.Artists); Replace(Genres, groups.Genres); Replace(Folders, groups.Folders); Replace(Playlists, playlistCards);
            Replace(FolderTree, folderTree);
            StatusText = tracks.Count == 0 ? (_settings.Current.LibraryFolders.Count == 0 ? "Add a music folder to begin" : "No matching tracks") : $"{tracks.Count:N0} tracks · {groups.Albums.Count:N0} albums · {groups.Artists.Count:N0} artists";
            var availableCount = tracks.Count(track => !track.IsMissing);
            StatusText = availableCount == 0
                ? (_settings.Current.LibraryFolders.Count == 0
                    ? "Add a music folder to begin"
                    : tracks.Count > 0
                        ? $"{tracks.Count:N0} files are currently missing"
                        : "No matching tracks")
                : $"{availableCount:N0} tracks · {groups.Albums.Count:N0} albums · {groups.Artists.Count:N0} artists";
            Raise(nameof(HasLibrary));
            Raise(nameof(HasMissingTracks));
            ApplyCurrentView(true);
            RaiseLibraryState();
        });
    }

    private static LibraryGroups BuildGroups(IReadOnlyList<Track> tracks)
    {
        var indexed = tracks
            .Select((track, index) => new IndexedTrack(index, track))
            .Where(item => !item.Track.IsMissing)
            .ToArray();
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
                Subtitle = string.IsNullOrWhiteSpace(playlist.Description) ? (playlist.Kind == PlaylistKind.Smart ? "Smart playlist" : "Playlist") : playlist.Description,
                Detail = summary.TrackCount == 1 ? "1 track" : $"{summary.TrackCount:N0} tracks",
                TrackCount = summary.TrackCount,
                RepresentativeTrack = summary.RepresentativeTrack,
                ArtworkPath = !string.IsNullOrWhiteSpace(playlist.CoverPath) && File.Exists(playlist.CoverPath) ? playlist.CoverPath : ExistingArtwork(summary.RepresentativeTrack)
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
        _artworkResolutionTask = ResolveCardArtworkAsync(cards, token);
    }

    public async Task WaitForBackgroundWorkAsync(CancellationToken cancellationToken)
    {
        var artworkResolution = _artworkResolutionTask;
        try { await artworkResolution.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (TimeoutException) { }
    }

    private async Task ResolveCardArtworkAsync(IReadOnlyList<LibraryCardViewModel> cards, CancellationToken cancellationToken)
    {
        var resolvedTracks = new ConcurrentBag<Track>();
        try
        {
            await Parallel.ForEachAsync(cards, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (card, ct) =>
            {
                try
                {
                    var track = card.RepresentativeTrack!;
                    var path = await ResolveArtworkAsync(track, ct);
                    if (path is null) return;
                    if (!string.Equals(track.ArtworkPath, path, StringComparison.OrdinalIgnoreCase))
                        resolvedTracks.Add(track with { ArtworkPath = path });
                    _artworkUpdates.Enqueue(
                        () =>
                        {
                            card.ArtworkPath = path;
                            if (ReferenceEquals(card, SelectedCard)) Raise(nameof(HasDetailArtwork));
                        },
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    _applicationLog.Write(
                        ApplicationLogLevel.Warning,
                        "artwork",
                        "card-resolution-item-failed",
                        new Dictionary<string, object?>
                        {
                            ["track"] = card.RepresentativeTrack?.Path,
                            ["card"] = card.Key
                        },
                        exception);
                }
            });
            if (!cancellationToken.IsCancellationRequested
                && !IsArtworkCacheBusy
                && !resolvedTracks.IsEmpty)
            {
                var updates = resolvedTracks
                    .GroupBy(track => track.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .ToArray();
                await _repository.UpsertBatchAsync(updates, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            RunOnUi(() => StatusText = "Artwork could not be fully resolved · " + exception.GetBaseException().Message);
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "artwork",
                "card-resolution-failed",
                exception: exception);
        }
    }

    private async Task<string?> ResolveArtworkAsync(Track track, CancellationToken cancellationToken)
    {
        if (track.ArtworkPath is { Length: > 0 } existing && File.Exists(existing)) return existing;
        if (_resolvedArtwork.TryGet(track.Path, out var cached)) return cached;
        var resolved = await _artwork.GetOrCreateAsync(track.EffectiveMediaPath, cancellationToken);
        _resolvedArtwork.Remember(track.Path, resolved);
        return resolved;
    }

    private void Navigate(string? view)
    {
        if (view is null || !Views.Contains(view, StringComparer.OrdinalIgnoreCase)) return;
        NavigationStarting?.Invoke(this, EventArgs.Empty);
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
        if (ShouldPreserveCollectionDetail(
                CurrentView,
                IsCollectionDetailOpen,
                resetSelection,
                SelectedCard)
            && TryGetCardTracks(SelectedCard!) is { } selectedTracks)
        {
            SetBrowseTracks(
                selectedTracks,
                SelectedCard!.Title,
                FormatCollectionSubtitle(SelectedCard, selectedTracks));
            RestartActiveArtworkResolution();
            return;
        }
        if (IsCollectionDetailOpen)
            IsCollectionDetailOpen = false;
        switch (CurrentView)
        {
            case "Albums": ViewSubtitle = $"{Albums.Count:N0} albums in your library"; SetActiveGroups(Albums); RestoreGallerySelection(ActiveGroups); break;
            case "Artists": ViewSubtitle = $"{Artists.Count:N0} artists in your library"; SetActiveGroups(Artists); RestoreGallerySelection(ActiveGroups); break;
            case "Genres": ViewSubtitle = $"{Genres.Count:N0} genres in your library"; SetActiveGroups(Genres); RestoreGallerySelection(ActiveGroups); break;
            case "Folders": ViewSubtitle = $"{Folders.Count:N0} folders across {_settings.Current.LibraryFolders.Count:N0} sources"; SetSidebarGroups(Folders); SelectDefaultFolder(); break;
            case "Playlists": ViewSubtitle = $"{Playlists.Count:N0} saved and smart playlists"; SetSidebarGroups(Playlists); SelectDefault(SidebarCards, resetSelection, selectFirst: false); break;
            case "Favorites":
                ViewSubtitle = "Tracks you have marked as loved";
                SetBrowseTracks(_allTracks.Where(x => !x.IsMissing && x.IsLoved).OrderBy(x => x.Artist).ThenBy(x => x.Album).ThenBy(x => x.TrackNumber), "Favorites", $"{_allTracks.Count(x => !x.IsMissing && x.IsLoved):N0} loved tracks", PrimaryViewStateKey);
                break;
            case "Songs":
            {
                var available = _allTracks.Where(track => !track.IsMissing).ToArray();
                ViewSubtitle = $"{available.Length:N0} tracks, stored offline";
                SetBrowseTracks(available, "All songs", StatusText, PrimaryViewStateKey, initialCount: 500);
                break;
            }
            case "Missing":
            {
                var missing = _allTracks.Where(track => track.IsMissing).ToArray();
                ViewSubtitle = $"{missing.Length:N0} files need attention";
                SetBrowseTracks(
                    missing,
                    "Missing files",
                    "Locate a moved file, remove its library record, or reconnect its source.",
                    PrimaryViewStateKey);
                break;
            }
            case "Recently Added":
                SetBrowseTracks(_allTracks.Where(track => !track.IsMissing).OrderByDescending(track => track.AddedAt), "Recently added", "Tracks added to your library most recently", PrimaryViewStateKey);
                ViewSubtitle = "Tracks added to your library most recently";
                break;
            case "Recently Played":
                SetBrowseTracks(_allTracks.Where(track => !track.IsMissing && track.LastPlayedAt is not null).OrderByDescending(track => track.LastPlayedAt), "Recently played", "Your latest listening activity", PrimaryViewStateKey);
                ViewSubtitle = "Your latest listening activity";
                break;
            case "Most Played":
                SetBrowseTracks(_allTracks.Where(track => !track.IsMissing && track.PlayCount > 0).OrderByDescending(track => track.PlayCount).ThenByDescending(track => track.LastPlayedAt), "Most played", "Your most played tracks", PrimaryViewStateKey);
                ViewSubtitle = "Your most played tracks";
                break;
            case "Never Played":
                SetBrowseTracks(_allTracks.Where(track => !track.IsMissing && track.PlayCount == 0).OrderBy(track => track.AddedAt), "Never played", "Tracks waiting for their first play", PrimaryViewStateKey);
                ViewSubtitle = "Tracks waiting for their first play";
                break;
            case "History":
                SetBrowseTracks(_allTracks.Where(track => track.LastPlayedAt is not null).OrderByDescending(track => track.LastPlayedAt), "History", "Playback history from this device", PrimaryViewStateKey);
                ViewSubtitle = "Playback history from this device";
                break;
            case "Now Playing":
                SetContentViewStateKey(PrimaryViewStateKey);
                ViewSubtitle = CurrentTrack is null ? "Choose a track to begin" : CurrentArtist;
                break;
        }
        RestartActiveArtworkResolution();
    }

    internal static bool ShouldPreserveCollectionDetail(
        string currentView,
        bool isCollectionDetailOpen,
        bool resetSelection,
        LibraryCardViewModel? selectedCard) =>
        !resetSelection
        && isCollectionDetailOpen
        && selectedCard is not null
        && currentView is "Albums" or "Artists" or "Genres";

    private void SelectDefault(IReadOnlyList<LibraryCardViewModel> cards, bool reset, bool selectFirst)
    {
        _cardSelections.TryGetValue(CurrentView, out var remembered);
        var selected = remembered is not null
            ? cards.FirstOrDefault(x => x.Key == remembered.Key && x.Kind == remembered.Kind)
            : null;
        selected ??= selectFirst ? cards.FirstOrDefault() : null;
        if (selected is not null)
        {
            SelectGroupCore(selected, false, rememberSelection: false);
        }
        else if (cards.Count > 0)
        {
            if (SelectedCard is not null) SelectedCard.IsSelected = false;
            SelectedCard = null;
            SetBrowseTracks([], $"Select a {CurrentView.TrimEnd('s').ToLowerInvariant()}", $"Choose one of {cards.Count:N0} {CurrentView.ToLowerInvariant()} to see its tracks.", PrimaryViewStateKey);
        }
        if (cards.Count == 0)
            SetBrowseTracks(
                [],
                $"No {CurrentView.ToLowerInvariant()}",
                string.IsNullOrWhiteSpace(SearchText) ? "This view will populate as your library is scanned." : "No results match your search.",
                PrimaryViewStateKey);
    }

    private void SelectDefaultFolder()
    {
        _cardSelections.TryGetValue("Folders", out var remembered);
        var selected = remembered is not null
            ? FindFolderNode(FolderTree, remembered.Key)
            : null;
        selected ??= FolderTree.FirstOrDefault();
        if (selected is not null) SelectFolderNodeCore(selected, rememberSelection: false);
        else
        {
            SelectedFolderNode = null;
            SetBrowseTracks([], "No folders", "Add or enable a music source to browse its folder tree.", PrimaryViewStateKey);
        }
    }

    public void SelectFolderNode(FolderTreeNodeViewModel? node)
    {
        if (node is null || CurrentView != "Folders") return;
        NavigationStarting?.Invoke(this, EventArgs.Empty);
        var previous = CaptureNavigation();
        SelectFolderNodeCore(node, rememberSelection: true);
        RecordNavigation(previous);
    }

    private void SelectFolderNodeCore(FolderTreeNodeViewModel node, bool rememberSelection)
    {
        SelectedFolderNode = node;
        var representative = node.TrackIndexes.Count > 0 ? _allTracks[node.TrackIndexes[0]] : null;
        var card = new LibraryCardViewModel
        {
            Kind = "Folder",
            Key = node.FullPath,
            Title = node.Name,
            Subtitle = node.FullPath,
            Detail = node.FullPath,
            TrackIndexes = node.TrackIndexes,
            TrackCount = node.TrackCount,
            RepresentativeTrack = representative,
            ArtworkPath = node.ArtworkPath
        };
        SelectGroupCore(card, false, rememberSelection);
        node.IsExpanded = true;
    }

    private static FolderTreeNodeViewModel? FindFolderNode(
        IEnumerable<FolderTreeNodeViewModel> nodes,
        string path)
    {
        foreach (var node in nodes)
        {
            if (node.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return node;
            var child = FindFolderNode(node.Children, path);
            if (child is not null) return child;
        }
        return null;
    }

    private void SelectGroup(LibraryCardViewModel? card)
    {
        if (card is null) return;
        NavigationStarting?.Invoke(this, EventArgs.Empty);
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
        var subtitle = FormatCollectionSubtitle(card, TryGetCardTracks(card));
        var tracks = TryGetCardTracks(card);
        if (card.Kind.Equals("Artist", StringComparison.OrdinalIgnoreCase) && tracks is { Count: > 0 })
        {
            var albums = tracks.GroupBy(track => track.DisplayAlbum, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new { Name = group.Key, Year = group.Max(track => track.Year), Count = group.Count() })
                .OrderByDescending(album => album.Year)
                .ThenBy(album => album.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var topTracks = tracks.OrderByDescending(track => track.PlayCount).ThenByDescending(track => track.Rating).ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase).Take(3).Select(track => track.Title).ToArray();
            ArtistProfileText = $"{tracks.Count:N0} tracks · {albums.Length:N0} albums · {tracks.Sum(track => track.PlayCount):N0} plays";
            ArtistDiscographyText = $"Discography: {string.Join(" · ", albums.Take(6).Select(album => $"{album.Name}{(album.Year > 0 ? $" ({album.Year})" : string.Empty)} · {album.Count} tracks"))}";
            ArtistProfileAttribution = topTracks.Length == 0 ? "Top tracks are calculated locally" : $"Top tracks: {string.Join(" · ", topTracks)} · calculated locally";
            if (MetadataLookupEnabled && MusicBrainzLookupEnabled)
                _ = LoadRemoteArtistProfileAsync(card.Title, card.Key);
        }
        else { ArtistProfileText = string.Empty; ArtistProfileAttribution = string.Empty; ArtistDiscographyText = string.Empty; }
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

    private async Task LoadRemoteArtistProfileAsync(string artist, string cardKey)
    {
        try
        {
            var profile = await _metadataMatcher.GetArtistProfileAsync(artist, MetadataMatchProvider.MusicBrainz, _lifetime.Token);
            if (profile is null || SelectedCard?.Key is not { } selectedKey || !selectedKey.Equals(cardKey, StringComparison.OrdinalIgnoreCase)) return;
            RunOnUi(() =>
            {
                if (SelectedCard?.Key is { } currentKey && currentKey.Equals(cardKey, StringComparison.OrdinalIgnoreCase))
                    ArtistProfileAttribution = $"{profile.Attribution} · matched as {profile.Name}";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _applicationLog.Write(ApplicationLogLevel.Debug, "metadata", "artist-profile-lookup-failed", exception: exception);
        }
    }

    private static string FormatCollectionSubtitle(LibraryCardViewModel card, IReadOnlyList<Track>? tracks)
    {
        var baseText = string.IsNullOrWhiteSpace(card.Detail) ? card.Subtitle : card.Detail;
        if (tracks is null || tracks.Count == 0) return $"{baseText} · {card.CountText}";
        var discs = tracks.Select(track => track.DiscNumber).Where(number => number > 0).Distinct().Count();
        var gain = tracks.Select(track => track.ReplayGainAlbumDb).FirstOrDefault(value => value.HasValue);
        var suffix = discs > 1 ? $" · {discs} discs" : string.Empty;
        if (card.Kind.Equals("Artist", StringComparison.OrdinalIgnoreCase))
            suffix += $" · {tracks.Select(track => track.DisplayAlbum).Distinct(StringComparer.CurrentCultureIgnoreCase).Count():N0} albums · {tracks.Aggregate(TimeSpan.Zero, (total, track) => total + track.Duration).ToString(@"h\:mm\:ss")}";
        if (gain.HasValue) suffix += $" · album gain {gain.Value:+0.0;-0.0;0.0} dB";
        return $"{baseText} · {tracks.Count:N0} tracks{suffix}";
    }

    private void CloseCollectionDetail()
    {
        NavigationStarting?.Invoke(this, EventArgs.Empty);
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
        NavigationStarting?.Invoke(this, EventArgs.Empty);
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
        groups = ApplyCardViewSettings(
            groups,
            TryGetCardTracks,
            QuickFilter,
            SortBy,
            SortDescending);
        var presentation = _galleryViews.GetOrCreate(
            PrimaryViewStateKey,
            () => groups,
            // Gallery cards are already grouped and allocated before the view is
            // selected.  Giving WPF the complete reference list makes the
            // virtualizing panel's extent authoritative and avoids relying on a
            // near-bottom ScrollChanged notification to reveal the next page.
            // Only visible ListBoxItems and artwork Images are still realized.
            int.MaxValue,
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
        RebuildGalleryRows();
    }

    public void SetGalleryColumnCount(int count)
    {
        count = Math.Clamp(count, 1, 64);
        if (_galleryColumnCount == count) return;
        _galleryColumnCount = count;
        RebuildGalleryRows();
    }

    private void RebuildGalleryRows()
    {
        var rows = GalleryRowLayout.Pack(GalleryGroups, _galleryColumnCount);
        if (GalleryRowLayout.IsEquivalent(_galleryRows, rows)) return;
        _galleryRows.ReplaceRange(rows);
        RaiseLibraryState();
    }

    public void LoadMoreGalleryGroups()
    {
        if (_activeGalleryPresentation is null || GalleryGroups.Count >= ActiveGroups.Count) return;
        using var scope = _diagnostics.Measure("view", "gallery-page-application",
            _diagnostics.Enabled ? new Dictionary<string, object?> { ["before"] = GalleryGroups.Count, ["total"] = ActiveGroups.Count } : null);
        PresentationCollectionCache<LibraryCardViewModel>.EnsureMaterialized(
            _activeGalleryPresentation,
            GalleryGroups.Count + 28);
        RebuildGalleryRows();
        RestartActiveArtworkResolution();
    }

    public void EnsureGalleryGroupsLoaded(int count)
    {
        if (_activeGalleryPresentation is null
            || !PresentationCollectionCache<LibraryCardViewModel>.EnsureMaterialized(_activeGalleryPresentation, count))
            return;
        RebuildGalleryRows();
        RestartActiveArtworkResolution();
    }

    private void SetSidebarGroups(IReadOnlyList<LibraryCardViewModel> groups)
    {
        groups = ApplyCardViewSettings(
            groups,
            TryGetCardTracks,
            QuickFilter,
            SortBy,
            SortDescending);
        var presentation = _sidebarViews.GetOrCreate(
            PrimaryViewStateKey,
            () => groups,
            32,
            out var cacheHit);
        using var scope = _diagnostics.Measure("view", "sidebar-application",
            _diagnostics.Enabled ? new Dictionary<string, object?>
            {
                ["groups"] = presentation.Source.Count,
                ["materialized"] = presentation.Items.Count,
                ["cacheHit"] = cacheHit
            } : null);
        _activeSidebarPresentation = presentation;
        SidebarCards = presentation.Items;
    }

    public void LoadMoreSidebarCards()
    {
        if (_activeSidebarPresentation is null
            || !PresentationCollectionCache<LibraryCardViewModel>.EnsureMaterialized(
                _activeSidebarPresentation,
                SidebarCards.Count + 32))
            return;
        RestartActiveArtworkResolution();
    }

    public void EnsureSidebarCardsLoaded(int count)
    {
        if (_activeSidebarPresentation is null
            || !PresentationCollectionCache<LibraryCardViewModel>.EnsureMaterialized(
                _activeSidebarPresentation,
                count))
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
        RaiseLibraryState();
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
        tracks = ApplyTrackViewSettings(
            tracks,
            QuickFilter,
            SortBy,
            SortDescending);
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
        RaiseLibraryState();
    }

    internal static IReadOnlyList<Track> ApplyTrackViewSettings(
        IEnumerable<Track> tracks,
        string? quickFilter,
        string sortBy,
        bool sortDescending) =>
        SortTracks(
            LibraryFilter.Apply(tracks, quickFilter),
            sortBy,
            sortDescending);

    internal static string NormalizeSortField(string? sortBy)
    {
        var candidate = sortBy?.Trim() ?? "Title";
        if (candidate.Equals("Added", StringComparison.OrdinalIgnoreCase))
            candidate = "Date added";
        else if (candidate.Equals("Played", StringComparison.OrdinalIgnoreCase))
            candidate = "Last played";
        return SupportedSortOptions.FirstOrDefault(option => option.Equals(
                   candidate,
                   StringComparison.OrdinalIgnoreCase))
               ?? "Title";
    }

    internal static IReadOnlyList<LibraryCardViewModel> ApplyCardViewSettings(
        IReadOnlyList<LibraryCardViewModel> cards,
        Func<LibraryCardViewModel, IReadOnlyList<Track>?> trackResolver,
        string? quickFilter,
        string sortBy,
        bool sortDescending)
    {
        ArgumentNullException.ThrowIfNull(trackResolver);
        var filters = LibraryFilter.Parse(quickFilter);
        var filtered = filters.Count == 0
            ? cards
            : cards.Where(card =>
            {
                var tracks = trackResolver(card);
                return tracks is null
                       || tracks.Any(track => filters.All(filter => filter(track)));
            }).ToArray();
        return SortCards(filtered, sortBy, sortDescending);
    }

    internal static IReadOnlyList<LibraryCardViewModel> SortCards(
        IReadOnlyList<LibraryCardViewModel> cards,
        string sortBy,
        bool sortDescending)
    {
        Func<LibraryCardViewModel, object?> key = NormalizeSortField(sortBy) switch
        {
            "Artist" => card => card.Subtitle,
            "Album" => card => card.Title,
            "Year" => card => card.RepresentativeTrack?.Year ?? 0,
            "Date added" => card => card.RepresentativeTrack?.AddedAt ?? DateTimeOffset.MinValue,
            "Last played" => card => card.RepresentativeTrack?.LastPlayedAt ?? DateTimeOffset.MinValue,
            "Play count" => card => card.RepresentativeTrack?.PlayCount ?? 0,
            "Rating" => card => card.RepresentativeTrack?.Rating ?? 0,
            "Duration" => card => card.RepresentativeTrack?.Duration ?? TimeSpan.Zero,
            "Codec" => card => card.RepresentativeTrack?.Codec,
            _ => card => card.Title
        };
        var ordered = sortDescending ? cards.OrderByDescending(key, Comparer<object?>.Create(CompareSortValues)) : cards.OrderBy(key, Comparer<object?>.Create(CompareSortValues));
        return ordered.ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<Track> SortTracks(
        IEnumerable<Track> tracks,
        string sortBy,
        bool sortDescending)
    {
        Func<Track, object?> key = NormalizeSortField(sortBy) switch
        {
            "Artist" => track => track.Artist,
            "Album" => track => track.Album,
            "Year" => track => track.Year,
            "Date added" => track => track.AddedAt,
            "Last played" => track => track.LastPlayedAt ?? DateTimeOffset.MinValue,
            "Play count" => track => track.PlayCount,
            "Rating" => track => track.Rating,
            "Duration" => track => track.Duration,
            "Codec" => track => track.Codec,
            _ => track => track.Title
        };
        var ordered = sortDescending ? tracks.OrderByDescending(key, Comparer<object?>.Create(CompareSortValues)) : tracks.OrderBy(key, Comparer<object?>.Create(CompareSortValues));
        return ordered.ThenBy(track => track.TrackNumber).ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static int CompareSortValues(object? left, object? right)
    {
        if (left is null && right is null) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        if (left is IComparable comparable) return comparable.CompareTo(right);
        return StringComparer.CurrentCultureIgnoreCase.Compare(left.ToString(), right.ToString());
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

    private async Task PlayGroupNextAsync(LibraryCardViewModel? card)
    {
        if (card is null || card.TrackCount == 0) return;
        var tracks = await GetCardTracksAsync(card);
        if (tracks.Count == 0) return;
        _queue.PlayNext(tracks);
        ShowNotice(
            tracks.Count == 1
                ? $"{tracks[0].Title} will play next"
                : $"{tracks.Count:N0} tracks will play next",
            ToastSeverity.Success);
    }

    private async Task AddGroupToQueueAsync(LibraryCardViewModel? card)
    {
        if (card is null || card.TrackCount == 0) return;
        var tracks = await GetCardTracksAsync(card);
        if (tracks.Count == 0) return;
        _queue.Add(tracks);
        ShowNotice(
            tracks.Count == 1
                ? $"Added {tracks[0].Title} to queue"
                : $"Added {tracks.Count:N0} tracks to queue",
            ToastSeverity.Success);
    }

    private async Task ScanAsync(IReadOnlyList<string>? selectedRoots = null)
    {
        var roots = selectedRoots ?? EnabledSourcePaths();
        if (roots.Count == 0) { StatusText = "Add or enable a music folder first"; return; }
        try
        {
            ScanFailures.Clear();
            Raise(nameof(HasScanFailures));
            IsScanning = true;
            UpdateScanState();
            _activeScanTask = _scanner.ScanAsync(
                roots,
                _settings.Current.ExcludedFolders,
                _lifetime.Token);
            await _activeScanTask;
            await RefreshLibraryAsync(SearchText, _lifetime.Token);
            ShowNotice(
                ScanFailed == 0
                    ? $"Library scan complete · {ScanProcessed:N0} files processed"
                    : $"Library scan complete · {ScanProcessed:N0} processed · {ScanFailed:N0} skipped",
                ScanFailed == 0 ? ToastSeverity.Success : ToastSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested)
                StatusText = "Library scan cancelled · progress will resume next time";
        }
        catch (Exception exception)
        {
            var message = exception.GetBaseException().Message;
            StatusText = "Library scan failed: " + message;
            ShowNotice(
                "Library scan failed: " + message,
                ToastSeverity.Error);
            _applicationLog.Write(
                ApplicationLogLevel.Error,
                "scanner",
                "scan-failed",
                new Dictionary<string, object?>
                {
                    ["roots"] = string.Join(";", roots),
                    ["discovered"] = ScanDiscovered,
                    ["processed"] = ScanProcessed,
                    ["failed"] = ScanFailed
                },
                exception);
        }
        finally
        {
            _activeScanTask = null;
            IsScanning = false;
            UpdateScanState();
        }
    }

    private void ScannerOnArtworkChanged(string path) => _artworkImages.InvalidatePath(path);

    private void ScannerOnFilesChanged(
        object? sender,
        LibraryFilesChangedEventArgs args)
    {
        // A scan explicitly started by this view model performs its own refresh
        // after completion. Watcher and overflow-rescan changes arrive here.
        if (_activeScanTask is not null) return;
        lock (_pendingLibraryChangeGate)
            _pendingLibraryChanges.AddRange(args.Changes);

        var previous = _libraryChangeCancellation;
        previous?.Cancel();
        previous?.Dispose();
        _libraryChangeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _libraryChangeCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, token);
                LibraryFileChange[] changes;
                lock (_pendingLibraryChangeGate)
                {
                    changes = _pendingLibraryChanges.ToArray();
                    _pendingLibraryChanges.Clear();
                }

                if (changes.Length == 0) return;
                if (!string.IsNullOrWhiteSpace(SearchText)
                    || changes.Any(change =>
                        change.Kind == LibraryFileChangeKind.FullRefresh))
                {
                    await RefreshLibraryAsync(SearchText, token);
                    return;
                }

                await ApplyLibraryChangesAsync(changes, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                _applicationLog.Write(
                    ApplicationLogLevel.Warning,
                    "library",
                    "watcher-refresh-failed",
                    new Dictionary<string, object?>
                    {
                        ["changes"] = args.Changes.Count,
                        ["fullRefresh"] = args.RequiresFullRefresh
                    },
                    exception);
            }
        }, token);
    }

    private async Task ApplyLibraryChangesAsync(
        IReadOnlyList<LibraryFileChange> changes,
        CancellationToken cancellationToken)
    {
        var trackUpdates = new List<LibraryTrackUpdate>(changes.Count);
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = change.Kind == LibraryFileChangeKind.FullRefresh
                ? null
                : await _repository.GetByPathAsync(
                    change.Path,
                    cancellationToken);
            trackUpdates.Add(new LibraryTrackUpdate(change, track));
        }

        LibraryGroupingUpdate update;
        await _groupingGate.WaitAsync(cancellationToken);
        try
        {
            _groupingIndex.ConfigureSeparators(_settings.Current.MultiValueSeparators);
            update = await Task.Run(
                () => _groupingIndex.Apply(trackUpdates),
                cancellationToken);
        }
        finally
        {
            _groupingGate.Release();
        }
        IReadOnlyList<FolderTreeNodeViewModel>? updatedFolderTree = null;
        if (update.AffectedKinds.Contains("Folder", StringComparer.OrdinalIgnoreCase))
            updatedFolderTree = await Task.Run(
                () => FolderTreeBuilder.Build(update.Tracks, _settings.Current.LibrarySources),
                cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _allTracks = update.Tracks;
            foreach (var mutation in update.Mutations)
                ApplyGroupMutation(mutation);

            // Folder/sidebar and track presentations are intentionally paged
            // copies. Invalidate only those projections; the gallery uses its
            // complete ObservableCollection as a live virtualized source.
            if (update.AffectedKinds.Contains(
                    "Folder",
                    StringComparer.OrdinalIgnoreCase))
            {
                _sidebarViews.Remove("primary:Folders");
                Replace(FolderTree, updatedFolderTree ?? []);
            }
            _trackViews.Clear();
            _activeTrackPresentation = null;

            var affectedViews = update.AffectedKinds
                .Select(kind => kind + "s")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (CurrentView is "Songs" or "Favorites" or "Missing"
                || affectedViews.Contains(CurrentView))
                ApplyCurrentView(false);

            var availableCount = _allTracks.Count(track => !track.IsMissing);
            StatusText =
                $"{availableCount:N0} tracks · {Albums.Count:N0} albums · {Artists.Count:N0} artists";
            Raise(nameof(HasLibrary));
            Raise(nameof(HasMissingTracks));
        });

        _applicationLog.Write(
            ApplicationLogLevel.Information,
            "library",
            "watcher-groups-updated",
            new Dictionary<string, object?>
            {
                ["changes"] = changes.Count,
                ["affectedGroups"] = update.Mutations.Count,
                ["affectedKinds"] = string.Join(
                    ",",
                    update.AffectedKinds)
            });
    }

    private void ApplyGroupMutation(LibraryGroupMutation mutation)
    {
        var target = mutation.Kind switch
        {
            "Album" => Albums,
            "Artist" => Artists,
            "Genre" => Genres,
            "Folder" => Folders,
            _ => null
        };
        if (target is null) return;

        var existingIndex = -1;
        for (var index = 0; index < target.Count; index++)
        {
            if (!target[index].Kind.Equals(
                    mutation.Kind,
                    StringComparison.OrdinalIgnoreCase)
                || !target[index].Key.Equals(
                    mutation.Key,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            existingIndex = index;
            break;
        }

        var wasSelected = existingIndex >= 0
                          && ReferenceEquals(
                              SelectedCard,
                              target[existingIndex]);
        if (existingIndex >= 0) target.RemoveAt(existingIndex);
        if (mutation.Replacement is null)
        {
            if (wasSelected) SelectedCard = null;
            return;
        }

        var insertionIndex = 0;
        while (insertionIndex < target.Count
               && CompareCards(
                   target[insertionIndex],
                   mutation.Replacement) <= 0)
            insertionIndex++;
        target.Insert(insertionIndex, mutation.Replacement);
        if (!wasSelected) return;
        mutation.Replacement.IsSelected = true;
        SelectedCard = mutation.Replacement;
    }

    private static int CompareCards(
        LibraryCardViewModel left,
        LibraryCardViewModel right)
    {
        var title = StringComparer.CurrentCultureIgnoreCase.Compare(
            left.Title,
            right.Title);
        return title != 0
            ? title
            : StringComparer.OrdinalIgnoreCase.Compare(left.Key, right.Key);
    }

    public async Task RelinkMissingTrackAsync(
        Track track,
        string replacementPath)
    {
        if (!track.IsMissing) return;
        var replacement = await _metadataReader.ReadAsync(
            replacementPath,
            _lifetime.Token);
        var artwork = await _artwork.GetOrCreateAsync(
            replacementPath,
            _lifetime.Token);
        if (artwork is not null)
            replacement = replacement with { ArtworkPath = artwork };
        await _repository.RelinkMissingAsync(
            track.Id,
            replacement,
            _lifetime.Token);
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        StatusText = $"Relinked {track.Title}";
    }

    public async Task RelinkPlaybackTrackAsync(
        QueueEntryViewModel entry,
        string replacementPath)
    {
        var replacement = await _metadataReader.ReadAsync(replacementPath, _lifetime.Token);
        var artwork = await _artwork.GetOrCreateAsync(replacementPath, _lifetime.Token);
        if (artwork is not null) replacement = replacement with { ArtworkPath = artwork };
        await _repository.RelinkAsync(entry.Track.Path, replacement, _lifetime.Token);
        _queue.ReplaceTrack(entry.Track.Path, replacement);
        _playbackFailures.TryRemove(entry.Track.Path, out _);
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        ShowNotice(
            $"Located replacement for {entry.Track.Title}",
            ToastSeverity.Success);
        await ChangeTrackAsync(_queue.Select(entry.Entry.Id));
    }

    public async Task ExportDiagnosticsAsync(string destination)
    {
        await _diagnosticsBundles.ExportAsync(
            destination,
            _lifetime.Token);
        StatusText = "Diagnostics bundle exported";
    }

    public async Task ExportSettingsAsync(string destination)
    {
        await _settings.ExportAsync(destination, _lifetime.Token);
        StatusText = "Settings exported";
    }

    public async Task ImportSettingsAsync(string source)
    {
        await _settings.ImportAsync(source, _lifetime.Token);
        await ApplySettingsFromStoreAsync();
        StatusText = "Settings imported";
    }

    public async Task ResetSettingsAsync(SettingsResetScope scope)
    {
        await _settings.ResetAsync(scope, _lifetime.Token);
        await ApplySettingsFromStoreAsync();
        StatusText = $"{scope} settings reset";
    }

    public async Task ExportUserDataBackupAsync(string destination)
    {
        await _userDataBackups.ExportAsync(
            destination,
            _lifetime.Token);
        StatusText = "User-data backup exported";
    }

    public async Task RestoreUserDataBackupAsync(string source)
    {
        _scanner.StopWatching();
        await _userDataBackups.RestoreAsync(
            source,
            _lifetime.Token);
        await ApplySettingsFromStoreAsync();
        await RefreshLibraryAsync(
            cancellationToken: _lifetime.Token);
        RestartSourceWatchers();
        StatusText = "User-data backup restored";
    }

    public async Task FindContentDuplicatesAsync()
    {
        DuplicateScanStatus = "Analyzing same-size files…";
        var groups = await _duplicates.FindContentDuplicatesAsync(
            _lifetime.Token);
        Replace(DuplicateGroups, groups);
        var files = groups.Sum(group => group.Tracks.Count);
        var reclaimable = groups.Sum(
            group => group.ReclaimableBytes);
        DuplicateScanStatus = groups.Count == 0
            ? "No content-identical files found."
            : $"{groups.Count:N0} groups · {files:N0} files · {FormatBytes(reclaimable)} potentially reclaimable";
    }

    public async Task ApplySettingsFromStoreAsync()
    {
        SettingsWorkspace.Reload();
        ApplyWorkspaceTheme();
        _volume = _settings.Current.Volume;
        _queueVisible =
            !IsSafeMode && _settings.Current.QueuePanelVisible;
        _queuePanelCompact = _settings.Current.QueuePanelCompact;
        _queuePanelDockSide = _settings.Current.QueuePanelDockSide;
        _albumTileSize = _settings.Current.AlbumTileSize;
        _dashboardModules = NormalizeDashboardModules(_settings.Current.DashboardModules);
        NormalizeInspectorSelection();
        LoadViewSettings();
        _animationsEnabled =
            !IsSafeMode && _settings.Current.AnimationsEnabled;
        _visualizerEnabled =
            !IsSafeMode && _settings.Current.VisualizerEnabled;
        _audio.SetVisualizationEnabled(_visualizerEnabled);
        _artworkCacheMegabytes =
            _settings.Current.ArtworkCacheMegabytes;
        _scheduledScanEnabled = _settings.Current.ScheduledLibraryScanEnabled;
        _scheduledScanIntervalMinutes = _settings.Current.ScheduledLibraryScanIntervalMinutes;
        _allowScheduledScanOnBattery = _settings.Current.AllowScheduledScanOnBattery;
        _allowScheduledScanOnMeteredNetwork = _settings.Current.AllowScheduledScanOnMeteredNetwork;
        _replayGainMode = _settings.Current.ReplayGainMode;
        _replayGainPreampDb = _settings.Current.ReplayGainPreampDb;
        _preventClipping = _settings.Current.PreventClipping;
        _playbackSpeed = _settings.Current.PlaybackSpeed;
        _pitchSemitones = _settings.Current.PitchSemitones;
        _preservePitch = _settings.Current.PreservePitch;
        _stopAfterCurrent = _settings.Current.StopAfterCurrent;
        _stopAfterQueue = _settings.Current.StopAfterQueue;
        ApplyLyricsSettings();
        Raise(nameof(Volume));
        RaiseQueuePanelState();
        Raise(nameof(AlbumTileSize));
        Raise(nameof(DashboardModules));
        RaiseInspectorState();
        Raise(nameof(GalleryItemWidth));
        Raise(nameof(GalleryItemHeight));
        Raise(nameof(AnimationsEnabled));
        Raise(nameof(VisualizerEnabled));
        Raise(nameof(VisualizerText));
        Raise(nameof(ArtworkCacheMegabytes));
        Raise(nameof(ArtworkCacheLimitText));
        Raise(nameof(ScheduledScanEnabled));
        Raise(nameof(ScheduledScanIntervalMinutes));
        Raise(nameof(AllowScheduledScanOnBattery));
        Raise(nameof(AllowScheduledScanOnMeteredNetwork));
        Raise(nameof(ReplayGainMode));
        Raise(nameof(ReplayGainPreampDb));
        Raise(nameof(PreventClipping));
        Raise(nameof(PlaybackSpeed));
        Raise(nameof(PitchSemitones));
        Raise(nameof(PreservePitch));
        Raise(nameof(StopAfterCurrent));
        Raise(nameof(StopAfterQueue));
        Raise(nameof(HasStopMode));
        Raise(nameof(StopModeText));
        _shortcuts.Refresh(_settings.Current.Shortcuts);

        // Settings can be opened while the library initializes in the
        // background. Import/reset must not manipulate repository-backed
        // watchers or reconfigure audio alongside that initialization.
        await SettingsRuntimeApplyPolicy.EnsureLibraryReadyAsync(
            IsLibraryReady,
            InitializeLibraryAsync);

        _scanner.StopWatching();
        RestartSourceWatchers();
        await RefreshLibrarySourceCountsAsync();
        ApplyCurrentView(false);

        var outputSelection =
            SettingsRuntimeApplyPolicy.ResolveOutputSelection(
                _settings.Current,
                OutputDevices);
        IsOutputProfileBusy = true;
        try
        {
            SelectedOutputDevice = outputSelection.Device;
            OutputProfile.Load(outputSelection.Profile);
            OutputCapabilities = null;
        }
        finally
        {
            IsOutputProfileBusy = false;
        }
        SettingsWorkspace.NotifyOutputProfileDraftChanged();
        await _audio.ConfigureOutputAsync(
            outputSelection.Profile,
            _lifetime.Token);
        await _audio.SetPlaybackOptionsAsync(
            CurrentPlaybackOptions(),
            _lifetime.Token);
        await _audio.SetVolumeAsync(
            _volume,
            _lifetime.Token);
    }

    public async Task ApplyPlaybackSettingsFromStoreAsync()
    {
        _replayGainMode = _settings.Current.ReplayGainMode;
        _replayGainPreampDb = _settings.Current.ReplayGainPreampDb;
        _preventClipping = _settings.Current.PreventClipping;
        _playbackSpeed = _settings.Current.PlaybackSpeed;
        _pitchSemitones = _settings.Current.PitchSemitones;
        _preservePitch = _settings.Current.PreservePitch;
        _stopAfterCurrent = _settings.Current.StopAfterCurrent;
        _stopAfterQueue = _settings.Current.StopAfterQueue;
        Raise(nameof(ReplayGainMode));
        Raise(nameof(ReplayGainPreampDb));
        Raise(nameof(PreventClipping));
        Raise(nameof(PlaybackSpeed));
        Raise(nameof(PitchSemitones));
        Raise(nameof(PreservePitch));
        Raise(nameof(StopAfterCurrent));
        Raise(nameof(StopAfterQueue));
        Raise(nameof(HasStopMode));
        Raise(nameof(StopModeText));
        Raise(nameof(ResumeTrackBookmarks));
        await _audio.SetPlaybackOptionsAsync(CurrentPlaybackOptions(), _lifetime.Token);
    }

    public async Task ApplyMetadataSettingsFromStoreAsync()
    {
        ApplyLyricsSettings();
        if (IsLibraryReady)
            await RefreshLibraryAsync(SearchText, _lifetime.Token);
    }

    public void ApplyViewSettingsFromStore()
    {
        LoadViewSettings();
        if (IsLibraryReady)
            ApplyViewPresentationSettings();
    }

    private void ApplyWorkspaceTheme()
    {
        if (Application.Current is null) return;
        var configuration = IsSafeMode
            ? new ThemeConfiguration(
                ThemeManager.DefaultTheme,
                ThemeManager.DefaultAccent,
                ThemeManager.DefaultFontFamily,
                ThemeManager.DefaultFontSize)
            : new ThemeConfiguration(
                SettingsWorkspace.Theme,
                SettingsWorkspace.AccentColor,
                SettingsWorkspace.FontFamily,
                SettingsWorkspace.InterfaceFontSize,
                SettingsWorkspace.BackgroundOpacity);
        ThemeManager.ApplyToCurrentApplication(configuration);
    }

    public bool HasUnsavedOutputProfileChanges()
    {
        return SettingsRuntimeApplyPolicy.HasOutputProfileChanges(
            _settings.Current,
            SelectedOutputDevice,
            OutputProfile.ToProfile());
    }

    public void RevertOutputProfileDraft()
    {
        if (SelectedOutputDevice is null) return;
        var configured = _settings.Current.OutputProfiles.FirstOrDefault(profile =>
            profile.DeviceId.Equals(SelectedOutputDevice.Id, StringComparison.OrdinalIgnoreCase));
        OutputProfile.Load(configured ?? AudioOutputProfileDefaults.For(SelectedOutputDevice));
        OutputProfileStatus = "Output edits reverted.";
        SettingsWorkspace.NotifyOutputProfileDraftChanged();
    }

    private void ApplyLyricsSettings()
    {
        _lyricsDisplayMode = _settings.Current.LyricsDisplayMode;
        _lyricsFontSize = _settings.Current.LyricsFontSize;
        _lyricsAlignment = _settings.Current.LyricsAlignment;
        _lyricsLineSpacing = _settings.Current.LyricsLineSpacing;
        _lyricsBlurStrength = _settings.Current.LyricsBlurStrength;
        _karaokeWordAnimation = _settings.Current.KaraokeWordAnimation;
        _onlineLyricsEnabled = _settings.Current.OnlineLyricsEnabled;
        _metadataLookupEnabled = _settings.Current.MetadataLookupEnabled;
        _musicBrainzLookupEnabled = _settings.Current.MusicBrainzLookupEnabled;
        _discogsLookupEnabled = _settings.Current.DiscogsLookupEnabled;
        _discogsUserToken = _settings.Current.DiscogsUserToken;
        Raise(nameof(MetadataLookupEnabled)); Raise(nameof(MusicBrainzLookupEnabled)); Raise(nameof(DiscogsLookupEnabled)); Raise(nameof(DiscogsUserToken));
        Raise(nameof(LyricsDisplayMode));
        Raise(nameof(LyricsFontSize));
        Raise(nameof(LyricsAlignment));
        Raise(nameof(LyricsHorizontalAlignment));
        Raise(nameof(LyricsTextAlignmentValue));
        Raise(nameof(LyricsLineSpacing));
        Raise(nameof(LyricsLineMargin));
        Raise(nameof(LyricsBlurStrength));
        Raise(nameof(KaraokeWordAnimation));
        Raise(nameof(OnlineLyricsEnabled));
        FetchOnlineLyricsCommand.RaiseCanExecuteChanged();
    }

    internal async Task RunIdleCleanupAsync()
    {
        TrimStack(_backHistory, 48);
        TrimStack(_forwardHistory, 48);

        var activeTrackKey = ContentViewStateKey;
        _trackViews.RemoveWhere(
            key => !key.StartsWith(
                       "primary:",
                       StringComparison.Ordinal)
                   && !key.Equals(
                       activeTrackKey,
                       StringComparison.Ordinal));
        foreach (var key in _trackSelections.Keys
                     .Where(key => !key.StartsWith(
                                 "primary:",
                                 StringComparison.Ordinal)
                             && !key.Equals(
                                 activeTrackKey,
                                 StringComparison.Ordinal))
                     .Take(Math.Max(0, _trackSelections.Count - 48))
                     .ToArray())
            _trackSelections.Remove(key);

        foreach (var pair in _playlistTrackLoads.ToArray())
        {
            if (pair.Value.IsValueCreated
                && pair.Value.Value.IsCompleted)
                _playlistTrackLoads.TryRemove(pair.Key, out _);
        }

        var retainedArtworkPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        if (CurrentTrack is not null)
            retainedArtworkPaths.Add(CurrentTrack.Path);
        foreach (var entry in Queue)
            retainedArtworkPaths.Add(entry.Track.Path);
        foreach (var card in ArtworkResolutionPlanner.ForActiveView(
                     CurrentView,
                     IsCollectionDetailOpen,
                     SelectedCard,
                     GalleryGroups,
                     SidebarCards))
        {
            if (card.RepresentativeTrack is not null)
                retainedArtworkPaths.Add(card.RepresentativeTrack.Path);
        }
        foreach (var key in _resolvedArtwork.Keys)
        {
            if (!retainedArtworkPaths.Contains(key))
                _resolvedArtwork.Remove(key);
        }

        _artworkImages.TrimMemoryCache(4L * 1024 * 1024);
        await _artwork.PruneAsync(_lifetime.Token);
        _applicationLog.Write(
            ApplicationLogLevel.Information,
            "performance",
            "idle-cleanup-completed",
            new Dictionary<string, object?>
            {
                ["backHistory"] = _backHistory.Count,
                ["forwardHistory"] = _forwardHistory.Count,
                ["resolvedArtwork"] = _resolvedArtwork.Count,
                ["completedPlaylistJobs"] = _playlistTrackLoads.Count
            });
    }

    private static void TrimStack<T>(Stack<T> stack, int maximum)
    {
        if (stack.Count <= maximum) return;
        var retained = stack.Take(maximum).Reverse().ToArray();
        stack.Clear();
        foreach (var item in retained) stack.Push(item);
    }

    private async Task RemoveMissingTrackAsync(Track? track)
    {
        if (track is not { IsMissing: true }) return;
        await _repository.RemoveTracksAsync([track.Id], _lifetime.Token);
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        StatusText = $"Removed missing record for {track.Title}";
    }

    private async Task PlaySelectedAsync()
    {
        if (SelectedTrack is null) return;
        var source = _activeTrackPresentation?.Source
            ?? (BrowseTracks.Count > 0 ? BrowseTracks.ToArray() : _allTracks);
        var selectedIndex = Enumerable.Range(0, source.Count)
            .FirstOrDefault(index => ReferenceEquals(source[index], SelectedTrack)
                || source[index].Path.Equals(SelectedTrack.Path, StringComparison.OrdinalIgnoreCase));
        _queue.Replace(source, selectedIndex);
        await ChangeTrackAsync(
            SelectedTrack,
            startReason: PlaybackStartReason.ExplicitSelection);
    }

    private async Task ChangeTrackAsync(
        Track? track,
        HashSet<string>? failedPaths = null,
        PlaybackStartReason startReason = PlaybackStartReason.QueueNavigation)
    {
        if (track is null) return;
        try
        {
            if (_audio.Snapshot.Track is { Id: > 0 } previous && _audio.Snapshot.Position > TimeSpan.FromSeconds(10))
                await _repository.SaveBookmarkAsync(previous.Id, _audio.Snapshot.Position, _lifetime.Token);
            var artwork = await ResolveArtworkAsync(track, _lifetime.Token);
            if (artwork is not null) track = track with { ArtworkPath = artwork };
            ApplyTrackPlaybackSettings(track);
            await _audio.SetPlaybackOptionsAsync(CurrentPlaybackOptions(track), _lifetime.Token);
            await _audio.LoadAsync(track, _lifetime.Token);
            var bookmark = track.Id > 0
                           && ShouldResumeTrackBookmark(
                               ResumeTrackBookmarks,
                               startReason)
                ? await _repository.GetBookmarkAsync(track.Id, _lifetime.Token)
                : null;
            if (bookmark.HasValue && bookmark.Value > TimeSpan.Zero && bookmark.Value < track.Duration - TimeSpan.FromSeconds(10)) await _audio.SeekAsync(bookmark.Value, _lifetime.Token);
            try
            {
                await _audio.QueueNextAsync(
                    PeekUpcomingTrack(),
                    _lifetime.Token);
            }
            catch (Exception exception) when (TrackFailurePolicy.IsRecoverable(exception))
            {
                _applicationLog.Write(
                    ApplicationLogLevel.Warning,
                    "audio",
                    "predecode-next-failed",
                    exception: exception);
            }
            await _audio.PlayAsync(_lifetime.Token);
            _playbackFailures.TryRemove(track.Path, out _);
            _queue.ReplaceTrack(track.Path, track);
            RecordQueueHistory(track);
            if (track.Id > 0) await _repository.RecordPlayAsync(track.Id, _lifetime.Token);
            await LoadBookmarksAsync(track);
            await LoadLyricsAsync(track);
        }
        catch (Exception exception) when (TrackFailurePolicy.IsRecoverable(exception))
        {
            failedPaths ??= new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            failedPaths.Add(track.Path);
            _playbackFailures[track.Path] = TrackFailurePolicy.FriendlyMessage(exception);
            ShowNotice(
                $"Could not play {track.Title}: {TrackFailurePolicy.FriendlyMessage(exception)}",
                ToastSeverity.Error);
            QueueOnChanged(this, EventArgs.Empty);
            StatusText =
                $"Skipped {track.Title} · {TrackFailurePolicy.FriendlyMessage(exception)}";
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "audio",
                "track-skipped",
                new Dictionary<string, object?>
                {
                    ["path"] = track.Path,
                    ["attempted"] = failedPaths.Count
                },
                exception);
            var next = failedPaths.Count < _queue.Items.Count
                ? _queue.Advance()
                : null;
            if (next is not null
                && !failedPaths.Contains(next.Path))
                await ChangeTrackAsync(next, failedPaths);
            else
                await _audio.StopAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            _playbackFailures[track.Path] = exception.GetBaseException().Message;
            ShowNotice(
                $"Playback failed: {exception.GetBaseException().Message}",
                ToastSeverity.Error);
            QueueOnChanged(this, EventArgs.Empty);
            StatusText = exception.Message;
            _applicationLog.Write(
                ApplicationLogLevel.Error,
                "audio",
                "track-load-failed",
                new Dictionary<string, object?> { ["path"] = track.Path },
                exception);
        }
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
        if (entry is null || !entry.CanReorder || _queue.PlaybackOrder.Count < 2) return;
        _queue.MoveInPlaybackOrder([entry.Entry.Id], 1);
    }

    public void MoveQueueEntry(Guid sourceId, Guid targetId)
    {
        var to = _queue.PlaybackOrder.ToList().FindIndex(x => x.Id == targetId);
        if (to >= 0) _queue.MoveInPlaybackOrder([sourceId], to);
    }

    public void MoveQueueEntries(IReadOnlyCollection<Guid> sourceIds, int targetIndex)
    {
        _queue.MoveInPlaybackOrder(sourceIds, targetIndex);
        ShowNotice(
            sourceIds.Count == 1
                ? "Queue item moved"
                : $"Moved {sourceIds.Count} queue items",
            ToastSeverity.Success);
    }

    public async Task PlayHistoryTrackAsync(Track track)
    {
        _queue.PlayNext([track]);
        var entry = _queue.Items.Last(item => item.Track.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase));
        await ChangeTrackAsync(
            _queue.Select(entry.Id),
            startReason: PlaybackStartReason.ExplicitSelection);
    }

    internal static bool ShouldResumeTrackBookmark(
        bool resumeEnabled,
        PlaybackStartReason startReason) =>
        resumeEnabled
        && startReason == PlaybackStartReason.ExplicitSelection;

    public void MoveSelectedQueueBy(int delta)
    {
        if (SelectedQueueEntries.Count == 0 || delta == 0) return;
        var ordered = SelectedQueueEntries
            .Where(entry => entry.CanReorder)
            .OrderBy(entry => entry.PlaybackIndex)
            .ToArray();
        if (ordered.Length == 0) return;
        var target = delta < 0
            ? Math.Max(1, ordered[0].PlaybackIndex - 1)
            : Math.Min(QueuePlaybackCount(), ordered[^1].PlaybackIndex + 2);
        _queue.MoveInPlaybackOrder(ordered.Select(entry => entry.Entry.Id).ToArray(), target);
        ShowNotice(
            delta < 0 ? "Moved selection up" : "Moved selection down",
            ToastSeverity.Success);
    }

    private int QueuePlaybackCount() =>
        Queue.Count(entry => entry.PlaybackIndex >= 0);

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
        _queue.ReplaceTrack(updated.Path, updated);
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
            case ShortcutActions.Search: SearchFocusRequested?.Invoke(this, EventArgs.Empty); break;
            case ShortcutActions.UndoQueue: UndoQueueCommand.Execute(null); break;
            default: return false;
        }
        return true;
    }

    private async Task LoadLyricsAsync(Track track)
    {
        try
        {
            var sources = (await _lyricsDocuments.DiscoverAsync(track, _lifetime.Token)).ToList();
            var rememberedOnline = await _onlineLyrics.LoadSelectedAsync(track, _lifetime.Token);
            if (rememberedOnline is not null)
            {
                var insertionIndex = sources.FindIndex(source => source.Kind == LyricsSourceKind.Embedded);
                sources.Insert(insertionIndex < 0 ? sources.Count : insertionIndex, rememberedOnline);
            }
            RunOnUi(() =>
            {
                Replace(LyricsSources, sources);
                var selected = sources.FirstOrDefault();
                ApplyLyricsDocument(selected, track);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            RunOnUi(() =>
            {
                LyricsStatus = "Lyrics could not be loaded · " + exception.GetBaseException().Message;
                ApplyLyricsDocument(null, track);
            });
        }
    }

    public void SetSelectedTracks(IEnumerable<Track> tracks)
    {
        SelectedTracks = tracks
            .Where(track => track is not null)
            .GroupBy(track => track.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (SelectedTracks.Count > 0 && SelectedTrack?.Path != SelectedTracks[0].Path)
            SelectedTrack = SelectedTracks[0];
    }

    public async Task ApplyMetadataAsync(
        IReadOnlyList<Track> tracks,
        MetadataEditPatch patch,
        MetadataWriteMode mode)
    {
        if (tracks.Count == 0) return;
        var results = new List<MetadataEditResult>(tracks.Count);
        try
        {
            foreach (var track in tracks)
            {
                var result = await _metadataEditor.ApplyAsync(track, patch, mode, _lifetime.Token);
                results.Add(result);
                await _repository.UpsertAsync(result.After, _lifetime.Token);
            }
        }
        catch
        {
            foreach (var result in results.AsEnumerable().Reverse())
            {
                try
                {
                    await _metadataEditor.RestoreAsync(result, _lifetime.Token);
                    await _repository.UpsertAsync(result.Before, _lifetime.Token);
                }
                catch { }
            }
            throw;
        }
        _metadataUndo = results;
        UndoMetadataCommand.RaiseCanExecuteChanged();
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        if (CurrentTrack is { } current)
        {
            var updated = results.FirstOrDefault(result => result.After.Path.Equals(current.Path, StringComparison.OrdinalIgnoreCase));
            if (updated is not null) CurrentTrack = updated.After;
        }
        StatusText = mode == MetadataWriteMode.WriteToFile
            ? $"Updated tags for {results.Count:N0} track{(results.Count == 1 ? string.Empty : "s")}"
            : $"Updated database metadata for {results.Count:N0} track{(results.Count == 1 ? string.Empty : "s")}";
    }

    private async Task EditMetadataAsync()
    {
        // MainWindow owns the dialog because it is the shell's visual owner.
        MetadataEditRequested?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    public event EventHandler? MetadataEditRequested;
    public event EventHandler? SearchFocusRequested;

    private async Task UndoMetadataAsync()
    {
        if (_metadataUndo.Count == 0) return;
        var undo = _metadataUndo;
        _metadataUndo = [];
        foreach (var result in undo.AsEnumerable().Reverse())
        {
            await _metadataEditor.RestoreAsync(result, _lifetime.Token);
            await _repository.UpsertAsync(result.Before, _lifetime.Token);
        }
        UndoMetadataCommand.RaiseCanExecuteChanged();
        await RefreshLibraryAsync(SearchText, _lifetime.Token);
        StatusText = "Metadata edit undone";
    }

    private void ApplyLyricsDocument(LyricsDocument? document, Track track)
    {
        CurrentLyricsDocument = document;
        ActiveLyricLine = null;
        HasSyncedLyrics = false;
        LyricsEditorVisible = false;
        _lyricsOffsetMilliseconds = _settings.Current.LyricOffsetsMilliseconds.GetValueOrDefault(track.Path);
        Raise(nameof(LyricsOffsetMilliseconds));
        Raise(nameof(LyricsOffsetText));
        if (document is null || string.IsNullOrWhiteSpace(document.Content))
        {
            Replace(Lyrics, []);
            ActiveLyric = "No lyrics found. Choose a local file, create a sidecar, or use the optional online lookup.";
            LyricsStatus = OnlineLyricsEnabled ? "No lyrics found" : "No local lyrics found · online lookup is off";
            Raise(nameof(HasLyrics));
            return;
        }

        var synced = LrcParser.Parse(document.Content);
        var useTiming = synced.Lines.Count > 0 && LyricsDisplayMode != LyricsDisplayMode.Static;
        HasSyncedLyrics = useTiming;
        if (useTiming)
        {
            var offset = TimeSpan.FromMilliseconds(LyricsOffsetMilliseconds);
            Replace(Lyrics, synced.Lines.Select(line => new LyricLineViewModel(Shift(line, offset))));
        }
        else if (synced.Lines.Count > 0)
        {
            Replace(Lyrics, synced.Lines.Select(line => new LyricLineViewModel(line with { Start = TimeSpan.Zero, End = null, Words = [] }, false)));
        }
        else
        {
            Replace(
                Lyrics,
                document.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                    .Split('\n')
                    .Take(LrcParser.MaximumLines)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => new LyricLineViewModel(new LyricLine(TimeSpan.Zero, null, text.Trim(), []), false)));
        }
        ActiveLyric = Lyrics.FirstOrDefault()?.Text ?? "No lyrics";
        LyricsStatus = document.Attribution is { Length: > 0 }
            ? $"Source: {document.Attribution}"
            : document.Kind switch
            {
                LyricsSourceKind.LocalFile => "Local sidecar",
                LyricsSourceKind.OnlineCache => "Cached online lyrics",
                _ => "Embedded metadata"
            };
        Raise(nameof(HasLyrics));
        UpdateLyricsPosition(_audio.Snapshot.Position);
    }

    private static LyricLine Shift(LyricLine line, TimeSpan offset) => line with
    {
        Start = MaxZero(line.Start + offset),
        End = line.End is { } end ? MaxZero(end + offset) : null,
        Words = line.Words.Select(word => word with
        {
            Start = MaxZero(word.Start + offset),
            End = word.End is { } wordEnd ? MaxZero(wordEnd + offset) : null
        }).ToArray()
    };

    private static TimeSpan MaxZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private async Task ReloadLyricsAsync()
    {
        if (CurrentTrack is { } track) await LoadLyricsAsync(track);
    }

    private async Task FetchOnlineLyricsAsync()
    {
        if (CurrentTrack is not { } track) return;
        IsOnlineLyricsBusy = true;
        LyricsStatus = "Searching LRCLIB…";
        try
        {
            var results = await _onlineLyrics.SearchAsync(track, _lifetime.Token);
            if (CurrentTrack?.Path != track.Path) return;
            RunOnUi(() =>
            {
                foreach (var result in results)
                    if (!LyricsSources.Any(source => source.Kind == LyricsSourceKind.OnlineCache && source.SourceId == result.SourceId))
                        LyricsSources.Add(result);
                LyricsStatus = results.Count == 0
                    ? "No LRCLIB matches found"
                    : $"{results.Count} LRCLIB match{(results.Count == 1 ? string.Empty : "es")} · select one to review";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            LyricsStatus = "Online lyric lookup failed · " + exception.GetBaseException().Message;
            _applicationLog.Write(ApplicationLogLevel.Warning, "lyrics", "online-lookup-failed", exception: exception);
        }
        finally
        {
            IsOnlineLyricsBusy = false;
        }
    }

    public async Task SelectLyricsSourceAsync(LyricsDocument? document)
    {
        if (document is null || CurrentTrack is not { } track) return;
        if (document.FilePath is { } path) document = await _lyricsDocuments.ChooseAsync(track, path, _lifetime.Token);
        if (document.Kind == LyricsSourceKind.OnlineCache)
            await _onlineLyrics.RememberSelectionAsync(track, document, _lifetime.Token);
        ApplyLyricsDocument(document, track);
    }

    public async Task ChooseLyricsFileAsync(string path)
    {
        if (CurrentTrack is not { } track) return;
        var document = await _lyricsDocuments.ChooseAsync(track, path, _lifetime.Token);
        await LoadLyricsAsync(track);
        ApplyLyricsDocument(document, track);
    }

    private void ToggleLyricsEditor()
    {
        if (CurrentTrack is null) return;
        LyricsEditorText = CurrentLyricsDocument?.Content ?? string.Empty;
        LyricsEditorVisible = !LyricsEditorVisible;
    }

    private async Task SaveLyricsAsync()
    {
        if (CurrentTrack is not { } track) return;
        var document = await _lyricsDocuments.SaveAsync(track, LyricsEditorText, CurrentLyricsDocument, _lifetime.Token);
        var updated = track with { Lyrics = LyricsEditorText };
        if (updated.Id > 0) await _repository.UpsertAsync(updated, _lifetime.Token);
        CurrentTrack = updated;
        await LoadLyricsAsync(updated);
        ApplyLyricsDocument(document, updated);
        LyricsStatus = "Lyrics saved";
    }

    public async Task RemoveCurrentLyricsAsync()
    {
        if (CurrentTrack is not { } track || CurrentLyricsDocument is not { CanEdit: true } document) return;
        await _lyricsDocuments.RemoveAsync(track, document, _lifetime.Token);
        var updated = track with { Lyrics = string.Empty };
        if (updated.Id > 0) await _repository.UpsertAsync(updated, _lifetime.Token);
        CurrentTrack = updated;
        await LoadLyricsAsync(updated);
        LyricsStatus = "Local lyric file removed";
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
            line.UpdatePosition(position, KaraokeWordAnimation && AnimationsEnabled);
            line.IsPrevious = false;
            line.IsNext = false;
            if (line.IsActive && (active is null || line.Role == LyricLineRole.Primary)) active = line;
        }
        if (active is not null)
        {
            var distinctStarts = Lyrics.Where(line => line.IsSynced).Select(line => line.Line.Start).Distinct().Order().ToArray();
            var index = Array.BinarySearch(distinctStarts, active.Line.Start);
            var previous = index > 0 ? distinctStarts[index - 1] : (TimeSpan?)null;
            var next = index >= 0 && index + 1 < distinctStarts.Length ? distinctStarts[index + 1] : (TimeSpan?)null;
            foreach (var line in Lyrics)
            {
                line.IsPrevious = previous.HasValue && line.Line.Start == previous.Value;
                line.IsNext = next.HasValue && line.Line.Start == next.Value;
            }
        }
        ActiveLyricLine = active;
        if (active is null) return;
        ActiveLyric = active.Text;
    }

    private void AudioOnStateChanged(object? sender, PlaybackSnapshot snapshot)
    {
        var trackPath = snapshot.Track?.Path;
        if (_lastLoggedPlaybackState != snapshot.State
            || !string.Equals(_lastLoggedTrackPath, trackPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_lastLoggedAudioError, snapshot.Error, StringComparison.Ordinal))
        {
            _lastLoggedPlaybackState = snapshot.State;
            _lastLoggedTrackPath = trackPath;
            _lastLoggedAudioError = snapshot.Error;
            _applicationLog.Write(
                string.IsNullOrWhiteSpace(snapshot.Error)
                    ? ApplicationLogLevel.Information
                    : ApplicationLogLevel.Error,
                "audio",
                "state-changed",
                new Dictionary<string, object?>
                {
                    ["state"] = snapshot.State.ToString(),
                    ["track"] = trackPath,
                    ["error"] = snapshot.Error
                });
        }
        RunOnUi(() =>
        {
            var track = snapshot.Track;
            if (track is not null
                && snapshot.Diagnostics?.SourceFormat is { } sourceFormat)
            {
                // Existing database rows may predate a decoder metadata fix.
                // Enrich only this in-memory presentation from the decoder's
                // authoritative source format; never rewrite the user's files
                // or library record during playback.
                track = track with
                {
                    SampleRate = track.SampleRate > 0
                        ? track.SampleRate
                        : sourceFormat.SampleRate,
                    BitsPerSample = track.BitsPerSample > 0
                        ? track.BitsPerSample
                        : sourceFormat.BitsPerSample,
                    Channels = track.Channels > 0
                        ? track.Channels
                        : sourceFormat.Channels
                };
            }
            if (track is not null && _resolvedArtwork.TryGet(track.Path, out var artwork) && artwork is not null) track = track with { ArtworkPath = artwork };
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
            Raise(nameof(HasAudioDiagnostics)); Raise(nameof(DiagnosticHeadline)); Raise(nameof(DiagnosticMode)); Raise(nameof(DiagnosticSource)); Raise(nameof(DiagnosticOutput)); Raise(nameof(DiagnosticDecoder)); Raise(nameof(DiagnosticBuffer)); Raise(nameof(DiagnosticEndpoint)); Raise(nameof(DiagnosticTiming)); Raise(nameof(DiagnosticReason));
            UpdateLyricsPosition(snapshot.Position);
            _systemMedia.Update(snapshot with { Track = track }, HasPreviousTrack(), HasNextTrack());
            if (CurrentView == "Now Playing") { ViewSubtitle = CurrentArtist; Raise(nameof(ViewTitle)); }
        });
    }

    private void AudioOnTrackTransitioned(object? sender, TrackTransitionedEventArgs e) => _ = HandleTrackTransitionedAsync(e);
    private async Task HandleTrackTransitionedAsync(TrackTransitionedEventArgs e)
    {
        _sleepTimer.NotifyTrackEnded(); _queue.Advance();
        _playbackFailures.TryRemove(e.Current.Path, out _);
        RecordQueueHistory(e.Current);
        ApplyTrackPlaybackSettings(e.Current);
        await _audio.SetPlaybackOptionsAsync(CurrentPlaybackOptions(e.Current), _lifetime.Token);
        if (e.Current.Id > 0) await _repository.RecordPlayAsync(e.Current.Id, _lifetime.Token);
        var artwork = await ResolveArtworkAsync(e.Current, _lifetime.Token);
        await LoadBookmarksAsync(e.Current);
        await LoadLyricsAsync(e.Current);
        RunOnUi(() => { if (CurrentTrack?.Path.Equals(e.Current.Path, StringComparison.OrdinalIgnoreCase) == true && artwork is not null) CurrentTrack = e.Current with { ArtworkPath = artwork }; });
        await _audio.QueueNextAsync(PeekUpcomingTrack(), _lifetime.Token);
    }

    private void AudioOnPlaybackEnded(object? sender, EventArgs e) => _ = HandlePlaybackEndedAsync();
    private async Task HandlePlaybackEndedAsync()
    {
        _sleepTimer.NotifyTrackEnded();
        if (StopAfterCurrent || StopAfterQueue && !HasAutomaticQueueSuccessor())
        {
            var completedMode = StopModeText;
            StopAfterCurrent = false;
            StopAfterQueue = false;
            await _settings.UpdateAsync(settings =>
            {
                settings.StopAfterCurrent = false;
                settings.StopAfterQueue = false;
            }, _lifetime.Token);
            await _audio.StopAsync(_lifetime.Token);
            ShowNotice(
                $"{completedMode} completed",
                ToastSeverity.Success);
            return;
        }
        var next = _queue.Advance();
        if (next is not null) await ChangeTrackAsync(next);
    }

    private void AudioOnOutputDevicesChanged(
        object? sender,
        AudioEndpointChangedEventArgs e)
    {
        _applicationLog.Write(
            ApplicationLogLevel.Information,
            "audio",
            "endpoint-changed",
            new Dictionary<string, object?>
            {
                ["kind"] = e.Kind.ToString(),
                ["detail"] = e.Detail
            });
        RunOnUi(() => _ = RefreshOutputDevicesAsync());
    }

    private Track? PeekUpcomingTrack()
    {
        if (StopAfterCurrent || StopAfterQueue && !HasAutomaticQueueSuccessor()) return null;
        if (_queue.PlaybackOrder.Count > 1) return _queue.PlaybackOrder[1].Track;
        if (_queue.Shuffle || _queue.Items.Count == 0) return null;
        return _queue.RepeatMode == RepeatMode.All ? _queue.Items[0].Track : null;
    }

    private bool HasAutomaticQueueSuccessor()
    {
        if (_queue.Items.Count == 0) return false;
        if (_queue.Shuffle) return _queue.ShuffleUpcomingPaths.Count > 0;
        return _queue.CurrentIndex + 1 < _queue.Items.Count;
    }

    private void QueueOnChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        var playbackOrder = _queue.PlaybackOrder;
        var playbackIndexes = playbackOrder
            .Select((item, index) => (item.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);
        var timeline = _queue.TimelineOrder;
        Replace(Queue, timeline.Select((item, index) =>
        {
            var artwork = ExistingArtwork(item.Track);
            if (artwork is null && _resolvedArtwork.TryGet(item.Track.Path, out var cached)) artwork = cached;
            _playbackFailures.TryGetValue(item.Track.Path, out var failure);
            var playbackIndex = playbackIndexes.GetValueOrDefault(item.Id, -1);
            return new QueueEntryViewModel(
                item,
                artwork,
                index,
                playbackIndex,
                playbackIndex < 0,
                playbackIndex == 1,
                failure);
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
            _queue.RestoreShuffleUpcoming(session.ShuffleUpcomingPaths);
            Replace(QueueHistory, session.QueueHistoryPaths
                .Where(byPath.ContainsKey)
                .Select(path => new QueueHistoryEntryViewModel(byPath[path], DateTimeOffset.UtcNow))
                .Take(50));
            Raise(nameof(HasQueueHistory));
            if (!_settings.Current.ResumeOnStartup || _queue.Current is not { } track) return;
            var artwork = await ResolveArtworkAsync(track, _lifetime.Token);
            if (artwork is not null) track = track with { ArtworkPath = artwork };
            ApplyTrackPlaybackSettings(track);
            await _audio.SetPlaybackOptionsAsync(CurrentPlaybackOptions(track), _lifetime.Token);
            // Restore the track and position as paused. Audio output always
            // waits for an explicit play action after launch.
            await _audio.LoadAsync(track, _lifetime.Token);
            if (session.PositionSeconds > 0 && session.PositionSeconds < track.Duration.TotalSeconds)
                await _audio.SeekAsync(TimeSpan.FromSeconds(session.PositionSeconds), _lifetime.Token);
            await _audio.QueueNextAsync(PeekUpcomingTrack(), _lifetime.Token);
            await LoadBookmarksAsync(track);
            await LoadLyricsAsync(track);
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
        var shuffleUpcoming = _queue.ShuffleUpcomingPaths.ToList();
        var historyPaths = QueueHistory.Select(item => item.Track.Path).Take(50).ToList();
        var view = CurrentView;
        return _settings.UpdateAsync(settings =>
        {
            settings.PlaybackSession.QueuePaths = paths;
            settings.PlaybackSession.CurrentIndex = index;
            settings.PlaybackSession.PositionSeconds = position;
            settings.PlaybackSession.WasPlaying = playing;
            settings.PlaybackSession.Shuffle = shuffle;
            settings.PlaybackSession.RepeatMode = repeat;
            settings.PlaybackSession.ShuffleUpcomingPaths = shuffleUpcoming;
            settings.PlaybackSession.QueueHistoryPaths = historyPaths;
            settings.PlaybackSession.LastView = view;
        }, cancellationToken);
    }

    private void RecordQueueHistory(Track track)
    {
        RunOnUi(() =>
        {
            if (QueueHistory.FirstOrDefault()?.Track.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase) == true)
                return;
            QueueHistory.Insert(0, new QueueHistoryEntryViewModel(track, DateTimeOffset.UtcNow));
            while (QueueHistory.Count > 50) QueueHistory.RemoveAt(QueueHistory.Count - 1);
            Raise(nameof(HasQueueHistory));
            ScheduleSessionSave();
        });
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
                try
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
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    _applicationLog.Write(
                        ApplicationLogLevel.Warning,
                        "artwork",
                        "queue-resolution-item-failed",
                        new Dictionary<string, object?> { ["track"] = entries.FirstOrDefault()?.Track.Path },
                        exception);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            RunOnUi(() => StatusText = "Queue artwork could not be fully resolved · " + exception.GetBaseException().Message);
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "artwork",
                "queue-resolution-failed",
                exception: exception);
        }
    }

    private static string QueueArtworkKey(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.Album)) return track.Path;
        var artist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.DisplayArtist : track.AlbumArtist;
        return artist + "\0" + track.DisplayAlbum;
    }

    private bool HasPreviousTrack() => _queue.CurrentIndex > 0 || (_queue.RepeatMode == RepeatMode.All && _queue.Items.Count > 1);
    private bool HasNextTrack() => _queue.PlaybackOrder.Count > 1 || (_queue.RepeatMode == RepeatMode.All && _queue.Items.Count > 1);
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
        if (progress.IsComplete || progress.State != ScanLifecycleState.Running)
            _applicationLog.Write(
                progress.Failed > 0 ? ApplicationLogLevel.Warning : ApplicationLogLevel.Information,
                "scanner",
                progress.IsComplete ? "scan-completed" : "scan-state-changed",
                new Dictionary<string, object?>
                {
                    ["state"] = progress.State.ToString(),
                    ["discovered"] = progress.Discovered,
                    ["processed"] = progress.Processed,
                    ["added"] = progress.Added,
                    ["updated"] = progress.Updated,
                    ["failed"] = progress.Failed,
                    ["resumed"] = progress.ResumedFromCheckpoint
                });
        IsScanning = !progress.IsComplete;
        if (progress.IsComplete) _lastCompletedLibraryScan = DateTimeOffset.UtcNow;
        ScanDiscovered = progress.Discovered;
        ScanProcessed = progress.Processed;
        ScanAdded = progress.Added;
        ScanUpdated = progress.Updated;
        ScanFailed = progress.Failed;
        ScanCurrentPath = progress.IsComplete
            ? "Scan complete"
            : string.IsNullOrWhiteSpace(progress.CurrentPath)
                ? "Discovering files…"
                : progress.CurrentPath;
        ScanCurrentSource = progress.IsComplete || string.IsNullOrWhiteSpace(progress.CurrentPath)
            ? "—"
            : EnabledSourcePaths()
                .Where(root => IsWithinSource(progress.CurrentPath, root))
                .OrderByDescending(root => root.Length)
                .FirstOrDefault() ?? "—";
        StatusText = progress.IsComplete
            ? $"Scan complete · {progress.Added} added · {progress.Updated} updated · {progress.Failed} skipped"
            : progress.State switch
            {
                ScanLifecycleState.Paused => $"Scan paused · {progress.Processed:N0} / {progress.Discovered:N0}",
                ScanLifecycleState.Cancelling => "Cancelling library scan…",
                _ => $"{(progress.ResumedFromCheckpoint ? "Resuming scan" : "Scanning")} {progress.Processed:N0} / {progress.Discovered:N0}"
            };
        UpdateScanState();
    });

    private void ScannerOnSourceStatusesChanged(object? sender, EventArgs args) =>
        RunOnUi(() =>
        {
            var statuses = _scanner.SourceStatuses;
            var byRoot = statuses.ToDictionary(status => status.Root, StringComparer.OrdinalIgnoreCase);
            var sources = _settings.Current.LibrarySources.Select(source =>
            {
                byRoot.TryGetValue(source.Path, out var status);
                return new LibrarySourceViewModel(source, status);
            }).ToArray();
            Replace(LibrarySources, sources);
            RaiseLibraryState();
            foreach (var source in statuses.Where(source => !source.IsOnline || source.Error is not null))
                _applicationLog.Write(
                    ApplicationLogLevel.Warning,
                    "scanner",
                    "source-unavailable",
                    new Dictionary<string, object?>
                    {
                        ["root"] = source.Root,
                        ["kind"] = source.Kind.ToString(),
                        ["online"] = source.IsOnline,
                        ["watching"] = source.IsWatching,
                        ["error"] = source.Error
                    });
        });

    private void ScannerOnFailureOccurred(object? sender, LibraryScanFailure failure) =>
        RunOnUi(() =>
        {
            ScanFailures.Insert(0, failure);
            while (ScanFailures.Count > 200) ScanFailures.RemoveAt(ScanFailures.Count - 1);
            Raise(nameof(HasScanFailures));
            RaiseLibraryState();
        });

    private void UpdateScanState()
    {
        Raise(nameof(IsScanPaused));
        Raise(nameof(ScanPauseGlyph));
        Raise(nameof(ScanPauseText));
        ScanCommand.RaiseCanExecuteChanged();
        RescanSourceCommand.RaiseCanExecuteChanged();
        ToggleLibrarySourceCommand.RaiseCanExecuteChanged();
        RemoveLibrarySourceCommand.RaiseCanExecuteChanged();
        ToggleScanPauseCommand.RaiseCanExecuteChanged();
        CancelScanCommand.RaiseCanExecuteChanged();
    }

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

    private void DebounceQuickFilter()
    {
        _quickFilterCancellation?.Cancel();
        _quickFilterCancellation?.Dispose();
        _quickFilterCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _quickFilterCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(160, token);
                token.ThrowIfCancellationRequested();
                RunOnUi(() =>
                {
                    if (!token.IsCancellationRequested && IsLibraryReady)
                        ApplyViewPresentationSettings();
                });
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void RememberSearch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return;
        var normalized = query.Trim();
        _ = _settings.UpdateAsync(settings =>
        {
            settings.SearchHistory.RemoveAll(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            settings.SearchHistory.Insert(0, normalized);
            if (settings.SearchHistory.Count > 50) settings.SearchHistory.RemoveRange(50, settings.SearchHistory.Count - 50);
        });
        Raise(nameof(SearchHistory));
        Raise(nameof(SearchSuggestions));
    }

    private static string FormatTime(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
    private static IReadOnlyList<string> NormalizeDashboardModules(IEnumerable<string>? modules)
    {
        var options = new[] { "Artwork", "Lyrics", "Metadata" };
        var normalized = (modules ?? []).Where(item => options.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Select(item => options.First(option => option.Equals(item, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? ["Artwork"] : normalized;
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        if (target is not ObservableRangeCollection<T> range)
            throw new InvalidOperationException($"{target.GetType().Name} does not support range replacement.");
        range.ReplaceRange(source);
    }
    private static void RunOnUi(Action action) { if (Application.Current.Dispatcher.CheckAccess()) action(); else Application.Current.Dispatcher.BeginInvoke(action); }

    internal void ReportLibraryInitializationFailure(Exception exception) =>
        RunOnUi(() => StatusText = "Library could not be loaded · " + exception.GetBaseException().Message);

    internal Task RetryLibraryInitializationAsync()
    {
        _libraryInitialization = null;
        return InitializeLibraryAsync();
    }

    public Task ShutdownAsync() =>
        _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _sessionSaveCancellation?.Cancel();
        _quickFilterCancellation?.Cancel();
        _libraryChangeCancellation?.Cancel();
        _replayGainAnalysisCancellation?.Cancel();
        _artworkReconciliationCancellation?.Cancel();
        _scanner.Cancel();
        var activeScan = _activeScanTask;
        if (activeScan is not null)
        {
            try { await activeScan.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException exception)
            {
                _applicationLog.Write(
                    ApplicationLogLevel.Warning,
                    "shutdown",
                    "scan-cancel-timeout",
                    exception: exception);
            }
        }
        try { await _artworkReconciliationTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException exception)
        {
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "shutdown",
                "artwork-repair-cancel-timeout",
                exception: exception);
        }
        if (_audio.Snapshot.Track is { Id: > 0 } current
            && _audio.Snapshot.Position > TimeSpan.Zero)
            await _repository.SaveBookmarkAsync(
                current.Id,
                _audio.Snapshot.Position,
                CancellationToken.None);
        await SaveSessionAsync(CancellationToken.None);
        await _settings.UpdateAsync(
            settings =>
            {
                settings.Volume = Volume;
                settings.QueuePanelVisible = QueueVisible;
                settings.QueuePanelCompact = QueuePanelCompact;
                settings.QueuePanelDockSide = QueuePanelDockSide;
            },
            CancellationToken.None);
        await _settings.SaveAsync(CancellationToken.None);
        _scheduledScanTimer.Stop();
        _lifetime.Cancel(); _searchCancellation?.Cancel(); _artworkCancellation?.Cancel(); _queueArtworkCancellation?.Cancel(); _volumeCancellation?.Cancel(); _scanner.StopWatching();
        _scanner.ArtworkChanged -= ScannerOnArtworkChanged;
        _scanner.ProgressChanged -= ScannerOnProgressChanged;
        _scanner.FailureOccurred -= ScannerOnFailureOccurred;
        _scanner.SourceStatusesChanged -= ScannerOnSourceStatusesChanged;
        _scanner.FilesChanged -= ScannerOnFilesChanged;
        _audio.StateChanged -= AudioOnStateChanged;
        _audio.TrackTransitioned -= AudioOnTrackTransitioned;
        _audio.PlaybackEnded -= AudioOnPlaybackEnded;
        _audio.OutputDevicesChanged -= AudioOnOutputDevicesChanged;
        _shortcuts.ActionInvoked -= ShortcutsOnActionInvoked; _systemMedia.CommandReceived -= SystemMediaOnCommandReceived;
        await _scanner.DisposeAsync();
        _searchCancellation?.Dispose(); _artworkCancellation?.Dispose(); _artworkReconciliationCancellation?.Dispose(); _queueArtworkCancellation?.Dispose(); _sessionSaveCancellation?.Dispose(); _volumeCancellation?.Dispose(); _libraryChangeCancellation?.Dispose(); _replayGainAnalysisCancellation?.Dispose(); _lifetime.Dispose();
        _applicationLog.Write(ApplicationLogLevel.Information, "shutdown", "state-flushed");
    }

    private sealed record LibraryGroups(IReadOnlyList<LibraryCardViewModel> Albums, IReadOnlyList<LibraryCardViewModel> Artists, IReadOnlyList<LibraryCardViewModel> Genres, IReadOnlyList<LibraryCardViewModel> Folders);
    private sealed record NavigationEntry(string View, string? CardKind, string? CardKey, bool IsCollectionDetail);
    private sealed record CardSelection(string Kind, string Key);
    private readonly record struct IndexedTrack(int Index, Track Track);
}

public sealed record PlaylistEditContext(Playlist? Existing, IReadOnlyList<Track>? InitialTracks = null);
public sealed record BookmarkRenameRequest(PlaybackBookmark Bookmark, string Name);
internal sealed record PlaylistHistoryEntry(Func<Task> Undo, Func<Task> Redo);

internal enum PlaybackStartReason
{
    QueueNavigation,
    ExplicitSelection
}
