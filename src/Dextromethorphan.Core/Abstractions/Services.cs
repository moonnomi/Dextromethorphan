using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Core.Abstractions;

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default);
}

public interface ILibraryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<Track?> GetByPathAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, LibraryFileStamp>> GetFileIndexAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(Track track, CancellationToken cancellationToken = default);
    Task UpsertBatchAsync(IReadOnlyCollection<Track> tracks, CancellationToken cancellationToken = default);
    Task RemoveMissingAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> SearchAsync(string query, int limit = 250, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetRecentlyAddedAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<LibraryStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task SetRatingAsync(long trackId, int rating, bool loved, CancellationToken cancellationToken = default);
    Task RecordPlayAsync(long trackId, CancellationToken cancellationToken = default);
    Task SaveBookmarkAsync(long trackId, TimeSpan position, CancellationToken cancellationToken = default);
    Task<TimeSpan?> GetBookmarkAsync(long trackId, CancellationToken cancellationToken = default);
}

public interface IPlaylistRepository
{
    Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Playlist?> GetAsync(long playlistId, CancellationToken cancellationToken = default);
    Task<long> CreateManualAsync(string name, CancellationToken cancellationToken = default);
    Task<long> CreateSmartAsync(string name, SmartPlaylistDefinition rules, CancellationToken cancellationToken = default);
    Task UpdateSmartRulesAsync(long playlistId, SmartPlaylistDefinition rules, CancellationToken cancellationToken = default);
    Task RenameAsync(long playlistId, string name, CancellationToken cancellationToken = default);
    Task DeleteAsync(long playlistId, CancellationToken cancellationToken = default);
    Task ReplaceTracksAsync(long playlistId, IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default);
    Task AddTracksAsync(long playlistId, IReadOnlyList<long> trackIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetTracksAsync(long playlistId, CancellationToken cancellationToken = default);
}

public interface IPlaylistInterchangeService
{
    Task<ImportedPlaylist> ImportAsync(string path, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, string name, IReadOnlyList<Track> tracks, PlaylistFormat format, CancellationToken cancellationToken = default);
}

public interface IPlaylistFileService
{
    Task<long> ImportAsync(string path, CancellationToken cancellationToken = default);
    Task ExportAsync(long playlistId, string path, PlaylistFormat format, CancellationToken cancellationToken = default);
}

public interface ITrackMetadataReader
{
    Task<Track> ReadAsync(string path, CancellationToken cancellationToken = default);
}

public interface IArtworkCache
{
    Task<string?> StoreAsync(string mediaPath, DateTimeOffset modifiedAt, ReadOnlyMemory<byte> artwork, CancellationToken cancellationToken = default);
    Task<string?> GetOrCreateAsync(string mediaPath, CancellationToken cancellationToken = default);
    Task PruneAsync(CancellationToken cancellationToken = default);
}

public interface ILibraryScanner : IAsyncDisposable
{
    bool IsScanning { get; }
    event EventHandler<ScanProgress>? ProgressChanged;
    Task ScanAsync(IEnumerable<string> roots, IEnumerable<string>? excluded = null, CancellationToken cancellationToken = default);
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
}

public interface IPlaybackQueue
{
    IReadOnlyList<QueueEntry> Items { get; }
    int CurrentIndex { get; }
    RepeatMode RepeatMode { get; set; }
    bool Shuffle { get; set; }
    event EventHandler? Changed;
    void Replace(IEnumerable<Track> tracks, int startIndex = 0);
    void Add(IEnumerable<Track> tracks);
    void PlayNext(IEnumerable<Track> tracks);
    void Move(int fromIndex, int toIndex);
    bool Remove(Guid id);
    Track? Current { get; }
    Track? Select(Guid id);
    Track? Advance();
    Track? Previous();
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
