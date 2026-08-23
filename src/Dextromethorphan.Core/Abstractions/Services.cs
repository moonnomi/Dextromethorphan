using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Abstractions;

public enum ApplicationLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public interface IApplicationLog
{
    void Write(
        ApplicationLogLevel level,
        string category,
        string operation,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? exception = null);
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, CancellationToken cancellationToken = default);
    Task ImportAsync(string path, CancellationToken cancellationToken = default);
    Task ResetAsync(SettingsResetScope scope, CancellationToken cancellationToken = default);
}

public interface ILibraryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<Track?> GetByPathAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, LibraryFileStamp>> GetFileIndexAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(Track track, CancellationToken cancellationToken = default);
    Task UpsertBatchAsync(IReadOnlyCollection<Track> tracks, CancellationToken cancellationToken = default);
    Task ReconcileCueSheetAsync(string cueSheetPath, IReadOnlyCollection<Track> tracks, CancellationToken cancellationToken = default);
    Task RemoveMissingAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken = default);
    Task MarkMissingAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);
    Task RelinkAsync(string previousPath, Track replacement, CancellationToken cancellationToken = default);
    Task RelinkMissingAsync(long trackId, Track replacement, CancellationToken cancellationToken = default);
    Task RemoveTracksAsync(IReadOnlyCollection<long> trackIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetMissingAsync(CancellationToken cancellationToken = default);
    Task<long> CountUnderRootAsync(string root, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> SearchAsync(string query, int limit = 250, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetRecentlyAddedAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<LibraryStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task SetRatingAsync(long trackId, int rating, bool loved, CancellationToken cancellationToken = default);
    Task RecordPlayAsync(long trackId, CancellationToken cancellationToken = default);
    Task SaveBookmarkAsync(long trackId, TimeSpan position, CancellationToken cancellationToken = default);
    Task<TimeSpan?> GetBookmarkAsync(long trackId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlaybackBookmark>> GetBookmarksAsync(long trackId, CancellationToken cancellationToken = default);
    Task<PlaybackBookmark> CreateBookmarkAsync(long trackId, string name, TimeSpan position, CancellationToken cancellationToken = default);
    Task RenameBookmarkAsync(long bookmarkId, string name, CancellationToken cancellationToken = default);
    Task DeleteBookmarkAsync(long bookmarkId, CancellationToken cancellationToken = default);
}

public interface IPlaylistRepository
{
    Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlaylistSummary>> GetSummariesAsync(CancellationToken cancellationToken = default);
    Task<Playlist?> GetAsync(long playlistId, CancellationToken cancellationToken = default);
    Task<long> CreateManualAsync(string name, CancellationToken cancellationToken = default);
    Task<long> CreateSmartAsync(string name, SmartPlaylistDefinition rules, CancellationToken cancellationToken = default);
    Task<long> DuplicateAsync(long playlistId, string? name = null, CancellationToken cancellationToken = default);
    Task UpdateSmartRulesAsync(long playlistId, SmartPlaylistDefinition rules, CancellationToken cancellationToken = default);
    Task RenameAsync(long playlistId, string name, CancellationToken cancellationToken = default);
    Task UpdateDetailsAsync(long playlistId, string description, string? coverPath, CancellationToken cancellationToken = default);
    Task DeleteAsync(long playlistId, CancellationToken cancellationToken = default);
    Task ReplaceTracksAsync(long playlistId, IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default);
    Task AddTracksAsync(long playlistId, IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default);
    Task MoveTracksAsync(long playlistId, IReadOnlyList<long> trackIds, int destinationIndex, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetTracksAsync(long playlistId, CancellationToken cancellationToken = default);
}

public interface IPlaylistInterchangeService
{
    Task<ImportedPlaylist> ImportAsync(string path, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, string name, IReadOnlyList<Track> tracks, PlaylistFormat format, CancellationToken cancellationToken = default);
}

public interface IPlaylistFileService
{
    PlaylistImportReport? LastImportReport { get; }
    Task<long> ImportAsync(string path, CancellationToken cancellationToken = default);
    Task ExportAsync(long playlistId, string path, PlaylistFormat format, CancellationToken cancellationToken = default);
}

public interface IPlaylistBackupService
{
    Task BackupAsync(CancellationToken cancellationToken = default);
}

public interface ITrackMetadataReader
{
    Task<Track> ReadAsync(string path, CancellationToken cancellationToken = default);
}

public interface IMetadataEditService
{
    Task<MetadataEditResult> ApplyAsync(
        Track track,
        MetadataEditPatch patch,
        MetadataWriteMode mode,
        CancellationToken cancellationToken = default);
    Task RestoreAsync(MetadataEditResult result, CancellationToken cancellationToken = default);
}

public interface IMetadataMatchService
{
    Task<IReadOnlyList<MetadataMatch>> SearchAsync(Track track, MetadataMatchProvider provider, CancellationToken cancellationToken = default);
    Task<ArtistProfile?> GetArtistProfileAsync(string artist, MetadataMatchProvider provider, CancellationToken cancellationToken = default);
}

public interface IArtworkCache
{
    bool IsManagedPath(string? path);
    Task<string?> StoreAsync(string mediaPath, DateTimeOffset modifiedAt, ReadOnlyMemory<byte> artwork, CancellationToken cancellationToken = default);
    Task<string?> GetOrCreateAsync(string mediaPath, CancellationToken cancellationToken = default);
    Task<ArtworkCacheStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task PruneAsync(CancellationToken cancellationToken = default);
}

public sealed record ArtworkCacheStats(
    long TotalBytes,
    int OriginalFiles,
    int ThumbnailFiles,
    int TemporaryFiles)
{
    public int TotalFiles => OriginalFiles + ThumbnailFiles;
}

public interface ILibraryScanner : IAsyncDisposable
{
    bool IsScanning { get; }
    ScanLifecycleState State { get; }
    IReadOnlyList<LibrarySourceStatus> SourceStatuses { get; }
    event EventHandler<ScanProgress>? ProgressChanged;
    event EventHandler<LibraryScanFailure>? FailureOccurred;
    event EventHandler? SourceStatusesChanged;
    event EventHandler<LibraryFilesChangedEventArgs>? FilesChanged;
    event Action<string>? ArtworkChanged;
    Task ScanAsync(IEnumerable<string> roots, IEnumerable<string>? excluded = null, CancellationToken cancellationToken = default);
    void Pause();
    void Resume();
    void Cancel();
    void StartWatching(IEnumerable<string> roots);
    void StopWatching();
}

public interface IAudioEngine : IAsyncDisposable
{
    PlaybackSnapshot Snapshot { get; }
    AudioDiagnostics? Diagnostics { get; }
    event EventHandler<PlaybackSnapshot>? StateChanged;
    event EventHandler<TrackTransitionedEventArgs>? TrackTransitioned;
    event EventHandler? PlaybackEnded;
    event EventHandler<AudioEndpointChangedEventArgs>? OutputDevicesChanged;
    Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default);
    Task<AudioDeviceCapabilities> GetDeviceCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default);
    Task LoadAsync(Track track, CancellationToken cancellationToken = default);
    Task QueueNextAsync(Track? track, CancellationToken cancellationToken = default);
    Task PlayAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);
    Task SetPlaybackOptionsAsync(AudioPlaybackOptions options, CancellationToken cancellationToken = default);
    Task ConfigureOutputAsync(AudioOutputProfile profile, CancellationToken cancellationToken = default);
    void SetVisualizationEnabled(bool enabled) { }
    AudioVisualizationSnapshot GetVisualizationSnapshot(int bandCount = 40) =>
        AudioVisualizationSnapshot.Empty(bandCount);
}

public interface IPlaybackQueue
{
    IReadOnlyList<QueueEntry> Items { get; }
    IReadOnlyList<QueueEntry> PlaybackOrder { get; }
    IReadOnlyList<QueueEntry> TimelineOrder { get; }
    int CurrentIndex { get; }
    RepeatMode RepeatMode { get; set; }
    bool Shuffle { get; set; }
    IReadOnlyList<string> ShuffleUpcomingPaths { get; }
    event EventHandler? Changed;
    void Replace(IEnumerable<Track> tracks, int startIndex = 0);
    void Add(IEnumerable<Track> tracks);
    void PlayNext(IEnumerable<Track> tracks);
    void Move(int fromIndex, int toIndex);
    void MoveMany(IReadOnlyCollection<Guid> ids, int toIndex);
    void MoveInPlaybackOrder(IReadOnlyCollection<Guid> ids, int toIndex);
    bool Remove(Guid id);
    int RemoveMany(IReadOnlyCollection<Guid> ids);
    void MoveToTop(IReadOnlyCollection<Guid> ids);
    void MoveToBottom(IReadOnlyCollection<Guid> ids);
    void ReplaceTrack(string path, Track replacement);
    Track? Current { get; }
    Track? Select(Guid id);
    Track? Advance();
    Track? Previous();
    void RestoreShuffleUpcoming(IEnumerable<string> paths);
    bool Undo();
    bool Redo();
}

public interface ISleepTimerService : IDisposable
{
    SleepTimerSnapshot Snapshot { get; }
    event EventHandler<SleepTimerSnapshot>? Changed;
    event EventHandler? Expired;
    void Start(TimeSpan duration);
    void StopAtEndOfTrack();
    void NotifyTrackEnded();
    void Cancel();
}

public interface IShortcutService : IDisposable
{
    event EventHandler<string>? ActionInvoked;
    IReadOnlyList<ShortcutRegistrationResult> Registrations { get; }
    void Attach(nint windowHandle);
    void Refresh(IEnumerable<ShortcutBinding> bindings);
    bool TryGetInAppAction(ShortcutGesture gesture, out string action);
}

public interface ISystemMediaTransportService : IDisposable
{
    event EventHandler<MediaTransportCommandEventArgs>? CommandReceived;
    bool IsAvailable { get; }
    string? Error { get; }
    void Attach(nint windowHandle);
    void Update(PlaybackSnapshot snapshot, bool hasPrevious, bool hasNext);
}
