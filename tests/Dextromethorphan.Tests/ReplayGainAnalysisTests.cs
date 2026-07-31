using System.Security.Cryptography;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using Dextromethorphan.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Tests;

public sealed class ReplayGainAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dextromethorphan-replaygain-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AnalysisUpdatesOnlyLocalIndexAndLeavesAudioBytesUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var library = Path.Combine(_root, "music");
        Directory.CreateDirectory(library);
        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "AudioFormats",
            "reference.wav");
        var firstPath = Path.Combine(library, "first.wav");
        var secondPath = Path.Combine(library, "second.wav");
        File.Copy(fixture, firstPath);
        File.Copy(fixture, secondPath);
        var firstHash = Hash(firstPath);
        var secondHash = Hash(secondPath);
        var firstModified = File.GetLastWriteTimeUtc(firstPath);
        var secondModified = File.GetLastWriteTimeUtc(secondPath);

        var repository = new SqliteLibraryRepository(
            new AppPaths(Path.Combine(_root, "state")));
        await repository.InitializeAsync(cancellationToken);
        var tracks = new[]
        {
            Track(firstPath, "First", 1),
            Track(secondPath, "Second", 2)
        };
        await repository.UpsertBatchAsync(tracks, cancellationToken);
        var service = new ReplayGainAnalysisService(
            repository,
            new StubAudioEngine(PlaybackState.Stopped));

        var summary = await service.AnalyzeMissingAsync(
            tracks,
            cancellationToken: cancellationToken);
        var stored = await repository.GetAllAsync(cancellationToken);

        Assert.Equal(2, summary.Analyzed);
        Assert.Equal(2, summary.Updated);
        Assert.Equal(0, summary.Failed);
        Assert.All(stored, track =>
        {
            Assert.InRange(track.ReplayGainTrackDb!.Value, 2.9, 3.2);
            Assert.InRange(track.ReplayGainAlbumDb!.Value, 2.9, 3.2);
            Assert.InRange(track.ReplayPeak!.Value, 0.088, 0.089);
        });
        Assert.Equal(
            stored[0].ReplayGainAlbumDb,
            stored[1].ReplayGainAlbumDb);
        Assert.Equal(4, stored.Single(track => track.Title == "First").Rating);
        Assert.Equal(7, stored.Single(track => track.Title == "First").PlayCount);
        Assert.Equal(firstHash, Hash(firstPath));
        Assert.Equal(secondHash, Hash(secondPath));
        Assert.Equal(firstModified, File.GetLastWriteTimeUtc(firstPath));
        Assert.Equal(secondModified, File.GetLastWriteTimeUtc(secondPath));
    }

    [Fact]
    public async Task AnalysisWaitsDuringPlaybackAndCancelsWithoutIndexChanges()
    {
        var library = Path.Combine(_root, "playing");
        Directory.CreateDirectory(library);
        var path = Path.Combine(library, "waiting.wav");
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "AudioFormats",
                "reference.wav"),
            path);
        var hash = Hash(path);
        var track = Track(path, "Waiting", 1);
        var repository = new SqliteLibraryRepository(
            new AppPaths(Path.Combine(_root, "playing-state")));
        await repository.InitializeAsync(
            TestContext.Current.CancellationToken);
        await repository.UpsertAsync(
            track,
            TestContext.Current.CancellationToken);
        var service = new ReplayGainAnalysisService(
            repository,
            new StubAudioEngine(PlaybackState.Playing));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AnalyzeMissingAsync(
                [track],
                cancellationToken: cancellation.Token));
        var stored = Assert.Single(
            await repository.GetAllAsync(
                TestContext.Current.CancellationToken));

        Assert.Null(stored.ReplayGainTrackDb);
        Assert.Null(stored.ReplayGainAlbumDb);
        Assert.Null(stored.ReplayPeak);
        Assert.Equal(hash, Hash(path));
    }

    private static Track Track(
        string path,
        string title,
        int number) =>
        new()
        {
            Path = path,
            Title = title,
            Artist = "Synthetic artist",
            AlbumArtist = "Synthetic artist",
            Album = "Synthetic album",
            TrackNumber = number,
            Duration = TimeSpan.FromSeconds(2),
            SampleRate = 48_000,
            BitsPerSample = 16,
            Channels = 2,
            Codec = "WAV",
            Rating = number == 1 ? 4 : 0,
            PlayCount = number == 1 ? 7 : 0,
            AddedAt = DateTimeOffset.UtcNow,
            FileModifiedAt = File.GetLastWriteTimeUtc(path),
            FileSize = new FileInfo(path).Length
        };

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class StubAudioEngine(PlaybackState state) : IAudioEngine
    {
        public PlaybackSnapshot Snapshot { get; } = new(
            null,
            state,
            TimeSpan.Zero,
            TimeSpan.Zero,
            1);
        public AudioDiagnostics? Diagnostics => null;
        public event EventHandler<PlaybackSnapshot>? StateChanged
        {
            add { }
            remove { }
        }
        public event EventHandler<TrackTransitionedEventArgs>? TrackTransitioned
        {
            add { }
            remove { }
        }
        public event EventHandler? PlaybackEnded
        {
            add { }
            remove { }
        }
        public event EventHandler<AudioEndpointChangedEventArgs>?
            OutputDevicesChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AudioDeviceInfo>>([]);
        public Task<AudioDeviceCapabilities> GetDeviceCapabilitiesAsync(
            string deviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task LoadAsync(
            Track track,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task QueueNextAsync(
            Track? track,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task PlayAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task PauseAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task StopAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SeekAsync(
            TimeSpan position,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetVolumeAsync(
            double volume,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetPlaybackOptionsAsync(
            AudioPlaybackOptions options,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ConfigureOutputAsync(
            AudioOutputProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
