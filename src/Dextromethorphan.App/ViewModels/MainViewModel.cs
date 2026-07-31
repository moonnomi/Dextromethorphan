using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.UI;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Lyrics;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly string[] Views = ["Albums", "Artists", "Genres", "Songs", "Folders", "Playlists", "Favorites", "Missing", "Now Playing"];
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
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, string?> _resolvedArtwork = new(StringComparer.OrdinalIgnoreCase);
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
    private CancellationTokenSource? _artworkCancellation;
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
    private IReadOnlyList<Track> _allTracks = [];
    private IReadOnlyList<LibraryCardViewModel> _activeGroups = [];
    private IReadOnlyList<LibraryCardViewModel> _sidebarCards = [];
    private ObservableCollection<Track> _browseTracks = [];
    private ObservableCollection<LibraryCardViewModel> _galleryGroups = [];
    private PresentationCollection<LibraryCardViewModel>? _activeGalleryPresentation;
    private PresentationCollection<LibraryCardViewModel>? _activeSidebarPresentation;
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
        ReplayGainAnalysisService replayGainAnalysis)
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
        UndoQueueCommand = new RelayCommand(_ => _queue.Undo());
        ClearQueueCommand = new RelayCommand(_ => _queue.Replace([]));
        RemoveMissingTrackCommand = new AsyncRelayCommand(
            parameter => RemoveMissingTrackAsync(parameter as Track),
            parameter => parameter is Track { IsMissing: true });
        LoveCommand = new AsyncRelayCommand(_ => ToggleLoveAsync(), _ => CurrentTrack is not null);
        SeekLyricCommand = new AsyncRelayCommand(p => SeekLyricAsync(p as LyricLineViewModel));
        SeekChapterCommand = new AsyncRelayCommand(
            p => p is AudioChapter chapter
                ? CommitSeekAsync(chapter.Start.TotalSeconds)
                : Task.CompletedTask,
            p => p is AudioChapter);
        PlayQueueEntryCommand = new AsyncRelayCommand(p => PlayQueueEntryAsync(p as QueueEntryViewModel));
        RemoveQueueEntryCommand = new AsyncRelayCommand(p => RemoveQueueEntryAsync(p as QueueEntryViewModel));
        PlayQueueEntryNextCommand = new RelayCommand(p => MoveQueueEntryNext(p as QueueEntryViewModel));
        ToggleDiagnosticsCommand = new RelayCommand(_ => DiagnosticsVisible = !DiagnosticsVisible);
        _audio.StateChanged += AudioOnStateChanged;
        _audio.TrackTransitioned += AudioOnTrackTransitioned;
        _audio.PlaybackEnded += AudioOnPlaybackEnded;
        _audio.OutputDevicesChanged += AudioOnOutputDevicesChanged;
        _queue.Changed += QueueOnChanged;
        _scanner.ProgressChanged += ScannerOnProgressChanged;
        _scanner.SourceStatusesChanged += ScannerOnSourceStatusesChanged;
        _scanner.FilesChanged += ScannerOnFilesChanged;
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
    public ObservableCollection<LibrarySourceStatus> LibrarySources { get; } = new ObservableRangeCollection<LibrarySourceStatus>();
    public ObservableCollection<DuplicateTrackGroup> DuplicateGroups { get; } = new ObservableRangeCollection<DuplicateTrackGroup>();
    public ObservableCollection<DecoderCapability> DecoderCapabilities { get; } = new ObservableRangeCollection<DecoderCapability>();
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
        set => Set(ref _playbackSpeed, Math.Clamp(value, 0.5, 1.5));
    }
    public double PitchSemitones
    {
        get => _pitchSemitones;
        set => Set(ref _pitchSemitones, Math.Clamp(value, -12, 12));
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
    public bool HasLibrary => _allTracks.Any(track => !track.IsMissing);
    public bool HasMissingTracks => _allTracks.Any(track => track.IsMissing);
    public bool HasBrowseTracks => BrowseTracks.Count > 0;
    public int BrowseTrackSourceCount => _activeTrackPresentation?.Source.Count ?? BrowseTracks.Count;
    public bool HasQueue => Queue.Count > 0;
    public bool IsGroupView => !IsCollectionDetailOpen && CurrentView is "Albums" or "Artists" or "Genres";
    public bool IsCollectionDetailView => IsCollectionDetailOpen && CurrentView is "Albums" or "Artists" or "Genres";
    public bool IsTrackView => !IsCollectionDetailOpen && CurrentView is "Songs" or "Favorites" or "Missing";
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
    public bool IsLibraryReady { get => _isLibraryReady; private set => Set(ref _isLibraryReady, value); }
    public bool IsSafeMode { get => _isSafeMode; private set => Set(ref _isSafeMode, value); }
    public bool IsScanPaused => _scanner.State == ScanLifecycleState.Paused;
    public string ScanPauseGlyph => IsScanPaused ? "\uE768" : "\uE769";
    public string ScanPauseText => IsScanPaused ? "Resume library scan" : "Pause library scan";
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
    public RelayCommand ToggleScanPauseCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public AsyncRelayCommand RefreshArtworkCacheCommand { get; }
    public AsyncRelayCommand ClearArtworkCacheCommand { get; }
    public AsyncRelayCommand RebuildArtworkCacheCommand { get; }
    public RelayCommand UndoQueueCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public AsyncRelayCommand LoveCommand { get; }
    public AsyncRelayCommand SeekLyricCommand { get; }
    public AsyncRelayCommand SeekChapterCommand { get; }
    public AsyncRelayCommand PlayQueueEntryCommand { get; }
    public AsyncRelayCommand RemoveQueueEntryCommand { get; }
    public AsyncRelayCommand RemoveMissingTrackCommand { get; }
    public RelayCommand PlayQueueEntryNextCommand { get; }
    public RelayCommand ToggleDiagnosticsCommand { get; }

    public async Task InitializeAsync()
    {
        await InitializeShellAsync();
        await InitializeLibraryAsync();
    }

    public void EnableSafeMode()
    {
        IsSafeMode = true;
        _animationsEnabled = false;
        _queueVisible = false;
        Raise(nameof(AnimationsEnabled));
        Raise(nameof(QueueVisible));
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
        _volume = _settings.Current.Volume; Raise(nameof(Volume));
        _queueVisible = !IsSafeMode && _settings.Current.QueuePanelVisible; Raise(nameof(QueueVisible));
        _albumTileSize = _settings.Current.AlbumTileSize; Raise(nameof(AlbumTileSize)); Raise(nameof(GalleryItemWidth)); Raise(nameof(GalleryItemHeight));
        _animationsEnabled = !IsSafeMode && _settings.Current.AnimationsEnabled; Raise(nameof(AnimationsEnabled));
        _artworkCacheMegabytes = _settings.Current.ArtworkCacheMegabytes;
        Raise(nameof(ArtworkCacheMegabytes)); Raise(nameof(ArtworkCacheLimitText));
        _replayGainMode = _settings.Current.ReplayGainMode;
        _replayGainPreampDb = _settings.Current.ReplayGainPreampDb;
        _preventClipping = _settings.Current.PreventClipping;
        _playbackSpeed = _settings.Current.PlaybackSpeed;
        _pitchSemitones = _settings.Current.PitchSemitones;
        _preservePitch = _settings.Current.PreservePitch;
        Raise(nameof(ReplayGainMode));
        Raise(nameof(ReplayGainPreampDb));
        Raise(nameof(PreventClipping));
        Raise(nameof(PlaybackSpeed));
        Raise(nameof(PitchSemitones));
        Raise(nameof(PreservePitch));
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
        _scanner.StartWatching(_settings.Current.LibraryFolders);
        Replace(LibrarySources, _scanner.SourceStatuses);
        if (!IsSafeMode)
            await RestoreSessionAsync();
        IsLibraryReady = true;
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

    private AudioPlaybackOptions CurrentPlaybackOptions()
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
        Raise(nameof(DiagnosticBuffer));
    }

    public async Task AddLibraryFolderAsync(string folder)
    {
        await _settings.UpdateAsync(x => { if (!x.LibraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) x.LibraryFolders.Add(folder); }, _lifetime.Token);
        _scanner.StartWatching(_settings.Current.LibraryFolders);
        await ScanAsync();
    }

    internal async Task OpenLaunchTargetsAsync(IEnumerable<string> targets)
    {
        await InitializeLibraryAsync();
        var normalized = targets
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var folders = normalized.Where(Directory.Exists).ToArray();
        if (folders.Length > 0)
        {
            await _settings.UpdateAsync(settings =>
            {
                foreach (var folder in folders)
                    if (!settings.LibraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                        settings.LibraryFolders.Add(folder);
            }, _lifetime.Token);
            _scanner.StartWatching(_settings.Current.LibraryFolders);
            await ScanAsync();
        }

        var firstFile = normalized.FirstOrDefault(File.Exists);
        if (firstFile is null) return;
        try
        {
            var track = await _repository.GetByPathAsync(firstFile, _lifetime.Token)
                ?? await _metadataReader.ReadAsync(firstFile, _lifetime.Token);
            _queue.Replace([track]);
            await ChangeTrackAsync(track);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException
                or NotSupportedException or InvalidDataException)
        {
            StatusText = $"Could not open {Path.GetFileName(firstFile)} · {exception.GetBaseException().Message}";
            _applicationLog.Write(
                ApplicationLogLevel.Warning,
                "launch",
                "open-target-failed",
                new Dictionary<string, object?> { ["path"] = firstFile },
                exception);
        }
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
                    var artwork = await _artwork.GetOrCreateAsync(track.EffectiveMediaPath, cancellationToken);
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
                return _groupingIndex.Reset(tracks);
            }, cancellationToken);
        }
        finally
        {
            _groupingGate.Release();
        }
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
        var resolved = await _artwork.GetOrCreateAsync(track.EffectiveMediaPath, cancellationToken);
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
            case "Folders": ViewSubtitle = $"{Folders.Count:N0} folders across {_settings.Current.LibraryFolders.Count:N0} sources"; SetSidebarGroups(Folders); SelectDefault(Folders, resetSelection, selectFirst: true); break;
            case "Playlists": ViewSubtitle = $"{Playlists.Count:N0} saved and smart playlists"; SetSidebarGroups(Playlists); SelectDefault(Playlists, resetSelection, selectFirst: false); break;
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
            case "Now Playing":
                SetContentViewStateKey(PrimaryViewStateKey);
                ViewSubtitle = CurrentTrack is null ? "Choose a track to begin" : CurrentArtist;
                break;
        }
        RestartActiveArtworkResolution();
    }

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

    private void SetSidebarGroups(IReadOnlyList<LibraryCardViewModel> groups)
    {
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
        try
        {
            IsScanning = true;
            UpdateScanState();
            _activeScanTask = _scanner.ScanAsync(
                _settings.Current.LibraryFolders,
                _settings.Current.ExcludedFolders,
                _lifetime.Token);
            await _activeScanTask;
            await RefreshLibraryAsync(SearchText, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested)
                StatusText = "Library scan cancelled · progress will resume next time";
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
            update = await Task.Run(
                () => _groupingIndex.Apply(trackUpdates),
                cancellationToken);
        }
        finally
        {
            _groupingGate.Release();
        }

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
                _sidebarViews.Remove("primary:Folders");
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
        await ApplyImportedSettingsAsync();
        StatusText = "Settings imported";
    }

    public async Task ResetSettingsAsync(SettingsResetScope scope)
    {
        await _settings.ResetAsync(scope, _lifetime.Token);
        await ApplyImportedSettingsAsync();
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
        await ApplyImportedSettingsAsync();
        await RefreshLibraryAsync(
            cancellationToken: _lifetime.Token);
        _scanner.StartWatching(_settings.Current.LibraryFolders);
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

    private async Task ApplyImportedSettingsAsync()
    {
        _volume = _settings.Current.Volume;
        _queueVisible =
            !IsSafeMode && _settings.Current.QueuePanelVisible;
        _albumTileSize = _settings.Current.AlbumTileSize;
        _animationsEnabled =
            !IsSafeMode && _settings.Current.AnimationsEnabled;
        _artworkCacheMegabytes =
            _settings.Current.ArtworkCacheMegabytes;
        _replayGainMode = _settings.Current.ReplayGainMode;
        _replayGainPreampDb = _settings.Current.ReplayGainPreampDb;
        _preventClipping = _settings.Current.PreventClipping;
        _playbackSpeed = _settings.Current.PlaybackSpeed;
        _pitchSemitones = _settings.Current.PitchSemitones;
        _preservePitch = _settings.Current.PreservePitch;
        Raise(nameof(Volume));
        Raise(nameof(QueueVisible));
        Raise(nameof(AlbumTileSize));
        Raise(nameof(GalleryItemWidth));
        Raise(nameof(GalleryItemHeight));
        Raise(nameof(AnimationsEnabled));
        Raise(nameof(ArtworkCacheMegabytes));
        Raise(nameof(ArtworkCacheLimitText));
        Raise(nameof(ReplayGainMode));
        Raise(nameof(ReplayGainPreampDb));
        Raise(nameof(PreventClipping));
        Raise(nameof(PlaybackSpeed));
        Raise(nameof(PitchSemitones));
        Raise(nameof(PreservePitch));
        _shortcuts.Refresh(_settings.Current.Shortcuts);
        _scanner.StopWatching();
        _scanner.StartWatching(_settings.Current.LibraryFolders);
        var profile = _settings.Current.OutputProfiles.FirstOrDefault(
                          item => item.DeviceId.Equals(
                              _settings.Current.ActiveOutputDeviceId,
                              StringComparison.OrdinalIgnoreCase))
                      ?? _settings.Current.OutputProfiles[0];
        await _audio.ConfigureOutputAsync(
            profile,
            _lifetime.Token);
        await _audio.SetPlaybackOptionsAsync(
            CurrentPlaybackOptions(),
            _lifetime.Token);
        await _audio.SetVolumeAsync(
            _volume,
            _lifetime.Token);
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
                _resolvedArtwork.TryRemove(key, out _);
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
        await ChangeTrackAsync(SelectedTrack);
    }

    private async Task ChangeTrackAsync(
        Track? track,
        HashSet<string>? failedPaths = null)
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
            if (track.Id > 0) await _repository.RecordPlayAsync(track.Id, _lifetime.Token);
            LoadLyrics(track);
        }
        catch (Exception exception) when (TrackFailurePolicy.IsRecoverable(exception))
        {
            failedPaths ??= new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            failedPaths.Add(track.Path);
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
        if (e.Current.Id > 0) await _repository.RecordPlayAsync(e.Current.Id, _lifetime.Token);
        var artwork = await ResolveArtworkAsync(e.Current, _lifetime.Token);
        RunOnUi(() => { LoadLyrics(e.Current); if (CurrentTrack?.Path.Equals(e.Current.Path, StringComparison.OrdinalIgnoreCase) == true && artwork is not null) CurrentTrack = e.Current with { ArtworkPath = artwork }; });
        await _audio.QueueNextAsync(PeekUpcomingTrack(), _lifetime.Token);
    }

    private void AudioOnPlaybackEnded(object? sender, EventArgs e) => _ = HandlePlaybackEndedAsync();
    private async Task HandlePlaybackEndedAsync() { _sleepTimer.NotifyTrackEnded(); var next = _queue.Advance(); if (next is not null) await ChangeTrackAsync(next); }

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
            Replace(LibrarySources, statuses);
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

    private void UpdateScanState()
    {
        Raise(nameof(IsScanPaused));
        Raise(nameof(ScanPauseGlyph));
        Raise(nameof(ScanPauseText));
        ScanCommand.RaiseCanExecuteChanged();
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

    private static string FormatTime(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
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
        _libraryChangeCancellation?.Cancel();
        _replayGainAnalysisCancellation?.Cancel();
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
            },
            CancellationToken.None);
        await _settings.SaveAsync(CancellationToken.None);
        _lifetime.Cancel(); _searchCancellation?.Cancel(); _artworkCancellation?.Cancel(); _queueArtworkCancellation?.Cancel(); _volumeCancellation?.Cancel(); _scanner.StopWatching();
        _scanner.ArtworkChanged -= ScannerOnArtworkChanged;
        _scanner.ProgressChanged -= ScannerOnProgressChanged;
        _scanner.SourceStatusesChanged -= ScannerOnSourceStatusesChanged;
        _scanner.FilesChanged -= ScannerOnFilesChanged;
        _audio.StateChanged -= AudioOnStateChanged;
        _audio.TrackTransitioned -= AudioOnTrackTransitioned;
        _audio.PlaybackEnded -= AudioOnPlaybackEnded;
        _audio.OutputDevicesChanged -= AudioOnOutputDevicesChanged;
        _shortcuts.ActionInvoked -= ShortcutsOnActionInvoked; _systemMedia.CommandReceived -= SystemMediaOnCommandReceived;
        await _scanner.DisposeAsync();
        _searchCancellation?.Dispose(); _artworkCancellation?.Dispose(); _queueArtworkCancellation?.Dispose(); _sessionSaveCancellation?.Dispose(); _volumeCancellation?.Dispose(); _libraryChangeCancellation?.Dispose(); _replayGainAnalysisCancellation?.Dispose(); _lifetime.Dispose();
        _applicationLog.Write(ApplicationLogLevel.Information, "shutdown", "state-flushed");
    }

    private sealed record LibraryGroups(IReadOnlyList<LibraryCardViewModel> Albums, IReadOnlyList<LibraryCardViewModel> Artists, IReadOnlyList<LibraryCardViewModel> Genres, IReadOnlyList<LibraryCardViewModel> Folders);
    private sealed record NavigationEntry(string View, string? CardKind, string? CardKey, bool IsCollectionDetail);
    private sealed record CardSelection(string Kind, string Key);
    private readonly record struct IndexedTrack(int Index, Track Track);
}
