using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Dextromethorphan.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SettingsRoundTripAndNormalizeValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var service = new JsonSettingsService(paths);
        await service.InitializeAsync(cancellationToken);
        await service.UpdateAsync(x =>
        {
            x.AccentColor = "#123456"; x.Volume = 4; x.AlbumTileSize = 10; x.ArtworkCacheMegabytes = 1;
            x.PlaybackSession.QueuePaths = [@"C:\music\one.flac", @"C:\music\one.flac", @"C:\music\two.flac"];
            x.PlaybackSession.CurrentIndex = 2;
            x.PlaybackSession.PositionSeconds = 42.5;
            x.PlaybackSession.Shuffle = true;
            x.PlaybackSession.RepeatMode = RepeatMode.All;
            x.PlaybackSession.LastView = "Songs";
        }, cancellationToken);

        var reloaded = new JsonSettingsService(paths);
        await reloaded.InitializeAsync(cancellationToken);
        Assert.Equal("#123456", reloaded.Current.AccentColor);
        Assert.Equal(1, reloaded.Current.Volume);
        Assert.Equal(80, reloaded.Current.AlbumTileSize);
        Assert.Equal(64, reloaded.Current.ArtworkCacheMegabytes);
        Assert.Equal(3, reloaded.Current.PlaybackSession.QueuePaths.Count);
        Assert.Equal(2, reloaded.Current.PlaybackSession.CurrentIndex);
        Assert.Equal(42.5, reloaded.Current.PlaybackSession.PositionSeconds);
        Assert.True(reloaded.Current.PlaybackSession.Shuffle);
        Assert.Equal(RepeatMode.All, reloaded.Current.PlaybackSession.RepeatMode);
        Assert.Equal("Songs", reloaded.Current.PlaybackSession.LastView);
    }

    [Fact]
    public async Task LibraryUpsertPreservesUserStateAndSearchesMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteLibraryRepository(new AppPaths(_root));
        await repository.InitializeAsync(cancellationToken);
        var artworkPath = Path.Combine(_root, "artwork", "blue.art");
        var track = new Track { Path = Path.Combine(_root, "blue.flac"), Title = "Blue in Green", Artist = "Miles Davis", Album = "Kind of Blue", Duration = TimeSpan.FromMinutes(5), FileModifiedAt = DateTimeOffset.UtcNow, FileSize = 42, ArtworkPath = artworkPath };
        await repository.UpsertAsync(track, cancellationToken);
        var stored = Assert.Single(await repository.SearchAsync("Miles", cancellationToken: cancellationToken));
        await repository.SetRatingAsync(stored.Id, 5, true, cancellationToken);
        await repository.UpsertAsync(track with { Id = stored.Id, Comment = "modal jazz" }, cancellationToken);

        var updated = Assert.Single(await repository.SearchAsync("modal jazz", cancellationToken: cancellationToken));
        Assert.Equal(5, updated.Rating);
        Assert.True(updated.IsLoved);
        Assert.Equal(artworkPath, updated.ArtworkPath);
        Assert.Equal(1, (await repository.GetStatsAsync(cancellationToken)).TrackCount);
    }

    [Fact]
    public async Task ArtworkCacheUsesStableKeysAndFileVersions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        var cache = new ArtworkCache(paths, settings);
        var modifiedAt = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        var first = await cache.StoreAsync(Path.Combine(_root, "song.flac"), modifiedAt, bytes, cancellationToken);
        var repeated = await cache.StoreAsync(Path.Combine(_root, "song.flac"), modifiedAt, bytes, cancellationToken);
        var changed = await cache.StoreAsync(Path.Combine(_root, "song.flac"), modifiedAt.AddSeconds(1), bytes, cancellationToken);

        Assert.NotNull(first);
        Assert.Equal(first, repeated);
        Assert.NotEqual(first, changed);
        Assert.Equal(".png", Path.GetExtension(first));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first!, cancellationToken));
        Assert.Equal(2, Directory.EnumerateFiles(paths.ArtworkCache, "*.png").Count());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
