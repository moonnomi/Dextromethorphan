using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class ScannerControlTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanStreamsFilesAndCoalescesProgress()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (paths, repository, settings, media) = await CreateFixtureAsync(120, cancellationToken);
        await using var scanner = new LibraryScanner(
            repository,
            new SyntheticMetadataReader(),
            new ArtworkCache(paths, settings),
            paths);
        var events = new List<ScanProgress>();
        scanner.ProgressChanged += (_, value) => events.Add(value);

        await scanner.ScanAsync([media], cancellationToken: cancellationToken);

        var final = Assert.Single(events, x => x.IsComplete);
        Assert.Equal(120, final.Discovered);
        Assert.Equal(120, final.Processed);
        Assert.Equal(120, final.Added);
        Assert.Equal(ScanLifecycleState.Idle, final.State);
        Assert.True(events.Count < 40, $"Expected coalesced progress, received {events.Count} events.");
        Assert.False(File.Exists(paths.ScanCheckpointFile));
    }

    [Fact]
    public async Task CancelLeavesCheckpointAndNextScanResumesSafely()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (paths, repository, settings, media) = await CreateFixtureAsync(240, cancellationToken);
        var firstReader = new SyntheticMetadataReader(TimeSpan.FromMilliseconds(20));
        await using (var scanner = new LibraryScanner(
                         repository,
                         firstReader,
                         new ArtworkCache(paths, settings),
                         paths))
        {
            var scan = scanner.ScanAsync([media], cancellationToken: cancellationToken);
            await firstReader.FirstRead.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            scanner.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        }

        Assert.True(File.Exists(paths.ScanCheckpointFile));

        var resumed = false;
        await using (var scanner = new LibraryScanner(
                         repository,
                         new SyntheticMetadataReader(),
                         new ArtworkCache(paths, settings),
                         paths))
        {
            scanner.ProgressChanged += (_, value) => resumed |= value.ResumedFromCheckpoint;
            await scanner.ScanAsync([media], cancellationToken: cancellationToken);
        }

        Assert.True(resumed);
        Assert.False(File.Exists(paths.ScanCheckpointFile));
        Assert.Equal(240, (await repository.GetStatsAsync(cancellationToken)).TrackCount);
    }

    [Fact]
    public async Task PauseStopsStartingMetadataWorkUntilResumed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (paths, repository, settings, media) = await CreateFixtureAsync(240, cancellationToken);
        var reader = new SyntheticMetadataReader(TimeSpan.FromMilliseconds(25));
        await using var scanner = new LibraryScanner(
            repository,
            reader,
            new ArtworkCache(paths, settings),
            paths);

        var scan = scanner.ScanAsync([media], cancellationToken: cancellationToken);
        await reader.FirstRead.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        scanner.Pause();
        Assert.Equal(ScanLifecycleState.Paused, scanner.State);
        await Task.Delay(150, cancellationToken);
        var pausedReads = reader.Reads;
        await Task.Delay(150, cancellationToken);
        Assert.Equal(pausedReads, reader.Reads);

        scanner.Resume();
        await scan;

        Assert.Equal(ScanLifecycleState.Idle, scanner.State);
        Assert.Equal(240, (await repository.GetStatsAsync(cancellationToken)).TrackCount);
    }

    [Theory]
    [InlineData(LibrarySourceKind.Local, 8, 8)]
    [InlineData(LibrarySourceKind.Local, 1, 2)]
    [InlineData(LibrarySourceKind.Network, 32, 2)]
    [InlineData(LibrarySourceKind.Removable, 32, 2)]
    [InlineData(LibrarySourceKind.Unknown, 32, 2)]
    public void MetadataConcurrencyIsBoundedPerSource(
        LibrarySourceKind kind,
        int processors,
        int expected)
    {
        Assert.Equal(expected, ScanConcurrencyPolicy.MetadataWorkers(kind, processors));
    }

    [Fact]
    public async Task OfflineSourceRetainsTracksAndReportsStoredCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(Path.Combine(_root, "state"));
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);
        var offlineRoot = Path.Combine(_root, "disconnected");
        await repository.UpsertAsync(
            new Track
            {
                Path = Path.Combine(offlineRoot, "song.flac"),
                Title = "Offline song",
                FileModifiedAt = DateTimeOffset.UtcNow,
                FileSize = 1
            },
            cancellationToken);
        await using var scanner = new LibraryScanner(
            repository,
            new SyntheticMetadataReader(),
            new ArtworkCache(paths, settings),
            paths);

        await scanner.ScanAsync(
            [offlineRoot],
            cancellationToken: cancellationToken);

        var retained = Assert.Single(
            await repository.GetAllAsync(cancellationToken));
        Assert.False(retained.IsMissing);
        var status = Assert.Single(scanner.SourceStatuses);
        Assert.False(status.IsOnline);
        Assert.Equal(1, status.TrackCount);
        Assert.Equal("Source is offline.", status.Error);
    }

    private async Task<(
        AppPaths Paths,
        SqliteLibraryRepository Repository,
        JsonSettingsService Settings,
        string Media)> CreateFixtureAsync(
        int files,
        CancellationToken cancellationToken)
    {
        var paths = new AppPaths(Path.Combine(_root, "state"));
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);
        var media = Path.Combine(_root, "music");
        Directory.CreateDirectory(media);
        for (var index = 0; index < files; index++)
            await File.WriteAllBytesAsync(
                Path.Combine(media, $"track-{index:D4}.flac"),
                [0],
                cancellationToken);
        return (paths, repository, settings, media);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class SyntheticMetadataReader(TimeSpan? delay = null) : ITrackMetadataReader
    {
        private int _reads;
        private readonly TaskCompletionSource _firstRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads => Volatile.Read(ref _reads);
        public Task FirstRead => _firstRead.Task;

        public async Task<Track> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            _firstRead.TrySetResult();
            if (delay is { } value)
                await Task.Delay(value, cancellationToken);
            var info = new FileInfo(path);
            return new Track
            {
                Path = path,
                Title = Path.GetFileNameWithoutExtension(path),
                Artist = "Scanner test",
                AlbumArtist = "Scanner test",
                Album = "Bounded scan",
                Duration = TimeSpan.FromSeconds(1),
                Codec = "FLAC",
                FileModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                FileSize = info.Length
            };
        }
    }
}
