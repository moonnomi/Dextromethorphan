using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class MissingFileRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingTrackCanBeRelinkedWithoutLosingUserState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(Path.Combine(_root, "state"));
        var library = new SqliteLibraryRepository(paths);
        var playlists = new SqlitePlaylistRepository(library);
        await library.InitializeAsync(cancellationToken);
        var previousPath = Path.Combine(_root, "old", "song.flac");
        var replacementPath = Path.Combine(_root, "new", "renamed.flac");
        await library.UpsertAsync(
            CreateTrack(previousPath, "Original title"),
            cancellationToken);
        var stored = Assert.Single(
            await library.GetAllAsync(cancellationToken));
        await library.SetRatingAsync(stored.Id, 5, true, cancellationToken);
        await library.RecordPlayAsync(stored.Id, cancellationToken);
        await library.SaveBookmarkAsync(
            stored.Id,
            TimeSpan.FromSeconds(42),
            cancellationToken);
        var playlistId = await playlists.CreateManualAsync(
            "Recovery",
            cancellationToken);
        await playlists.AddTracksAsync(
            playlistId,
            [stored.Id],
            cancellationToken);

        await library.MarkMissingAsync(
            [previousPath],
            cancellationToken);
        var missing = Assert.Single(
            await library.GetMissingAsync(cancellationToken));
        Assert.True(missing.IsMissing);

        await library.RelinkMissingAsync(
            stored.Id,
            CreateTrack(replacementPath, "Updated title"),
            cancellationToken);

        var relinked = Assert.Single(
            await library.GetAllAsync(cancellationToken));
        Assert.Equal(stored.Id, relinked.Id);
        Assert.Equal(Path.GetFullPath(replacementPath), relinked.Path);
        Assert.Equal("Updated title", relinked.Title);
        Assert.False(relinked.IsMissing);
        Assert.Equal(5, relinked.Rating);
        Assert.True(relinked.IsLoved);
        Assert.Equal(1, relinked.PlayCount);
        Assert.Equal(
            TimeSpan.FromSeconds(42),
            await library.GetBookmarkAsync(
                relinked.Id,
                cancellationToken));
        Assert.Equal(
            replacementPath,
            Assert.Single(
                await playlists.GetTracksAsync(
                    playlistId,
                    cancellationToken)).Path);
    }

    [Fact]
    public async Task SourceCountExcludesMissingRecordsUntilTheyReturn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "music");
        var library = new SqliteLibraryRepository(
            new AppPaths(Path.Combine(_root, "state")));
        await library.InitializeAsync(cancellationToken);
        var first = Path.Combine(source, "first.flac");
        var second = Path.Combine(source, "nested", "second.flac");
        await library.UpsertBatchAsync(
            [CreateTrack(first, "First"), CreateTrack(second, "Second")],
            cancellationToken);

        Assert.Equal(
            2,
            await library.CountUnderRootAsync(source, cancellationToken));
        await library.MarkMissingAsync([second], cancellationToken);
        Assert.Equal(
            1,
            await library.CountUnderRootAsync(source, cancellationToken));

        await library.UpsertAsync(
            CreateTrack(second, "Second"),
            cancellationToken);
        Assert.Equal(
            2,
            await library.CountUnderRootAsync(source, cancellationToken));
    }

    private static Track CreateTrack(string path, string title) => new()
    {
        Path = path,
        Title = title,
        Artist = "Artist",
        Album = "Album",
        Duration = TimeSpan.FromMinutes(3),
        FileModifiedAt = DateTimeOffset.UtcNow,
        FileSize = 123
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
