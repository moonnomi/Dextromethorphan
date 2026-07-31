using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Tests;

public sealed class DatabaseRecoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RebuildFromFilesRestoresRecoverableUserState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        var repository = new SqliteLibraryRepository(paths);
        var playlists = new SqlitePlaylistRepository(repository);
        await repository.InitializeAsync(cancellationToken);
        var track = Track(Path.Combine(_root, "music", "song.flac"));
        await repository.UpsertAsync(track, cancellationToken);
        var persisted = Assert.Single(
            await repository.GetAllAsync(cancellationToken));
        await repository.SetRatingAsync(
            persisted.Id,
            4,
            true,
            cancellationToken);
        await repository.RecordPlayAsync(
            persisted.Id,
            cancellationToken);
        await repository.SaveBookmarkAsync(
            persisted.Id,
            TimeSpan.FromSeconds(33),
            cancellationToken);
        var playlistId = await playlists.CreateManualAsync(
            "Recovered",
            cancellationToken);
        await playlists.AddTracksAsync(
            playlistId,
            [persisted.Id],
            cancellationToken);
        var scanner = new RebuildScanner(repository, track);
        var recovery = new DatabaseRecoveryService(
            paths,
            repository,
            scanner,
            settings,
            new NullApplicationLog());

        var result = await recovery.RebuildFromFilesAsync(
            cancellationToken);

        var restored = Assert.Single(
            await repository.GetAllAsync(cancellationToken));
        Assert.Equal(4, restored.Rating);
        Assert.True(restored.IsLoved);
        Assert.Equal(1, restored.PlayCount);
        Assert.Equal(
            TimeSpan.FromSeconds(33),
            await repository.GetBookmarkAsync(
                restored.Id,
                cancellationToken));
        Assert.Equal(
            "Recovered",
            Assert.Single(
                await playlists.GetAllAsync(cancellationToken)).Name);
        Assert.Equal(1, result.RestoredTracks);
        Assert.Equal(1, result.RestoredPlaylists);
        Assert.True(File.Exists(result.UserStateSnapshot));
        Assert.True(File.Exists(result.DatabaseBackupOrQuarantine));
    }

    private static Track Track(string path) => new()
    {
        Path = path,
        Title = "Song",
        Artist = "Artist",
        AlbumArtist = "Artist",
        Album = "Album",
        Genre = "Tests",
        Duration = TimeSpan.FromMinutes(1),
        Codec = "FLAC",
        FileModifiedAt = DateTimeOffset.UtcNow,
        FileSize = 10
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class RebuildScanner(
        ILibraryRepository repository,
        Track track) : ILibraryScanner
    {
        public bool IsScanning => false;
        public ScanLifecycleState State => ScanLifecycleState.Idle;
        public IReadOnlyList<LibrarySourceStatus> SourceStatuses => [];
        public event EventHandler<ScanProgress>? ProgressChanged;
        public event EventHandler? SourceStatusesChanged;
        public event EventHandler<LibraryFilesChangedEventArgs>? FilesChanged;
        public event Action<string>? ArtworkChanged;

        public async Task ScanAsync(
            IEnumerable<string> roots,
            IEnumerable<string>? excluded = null,
            CancellationToken cancellationToken = default)
        {
            await repository.UpsertAsync(track, cancellationToken);
        }

        public void Pause() { }
        public void Resume() { }
        public void Cancel() { }
        public void StartWatching(IEnumerable<string> roots) { }
        public void StopWatching() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullApplicationLog : IApplicationLog
    {
        public void Write(
            ApplicationLogLevel level,
            string category,
            string operation,
            IReadOnlyDictionary<string, object?>? data = null,
            Exception? exception = null)
        {
        }

        public Task CompleteAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
