using Dextromethorphan.Infrastructure.Storage;
using Dextromethorphan.PerformanceFixtures;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Tests;

public sealed class PerformanceFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Dextromethorphan.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SyntheticFixtureIsDeterministicAndLoadable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstRoot = Path.Combine(_root, "first");
        var secondRoot = Path.Combine(_root, "second");
        var generator = new PerformanceFixtureGenerator();
        var first = await generator.GenerateAsync(new PerformanceFixtureOptions(80, firstRoot, TracksPerAlbum: 20, AlbumsPerArtist: 2), cancellationToken: cancellationToken);
        var second = await generator.GenerateAsync(new PerformanceFixtureOptions(80, secondRoot, TracksPerAlbum: 20, AlbumsPerArtist: 2), cancellationToken: cancellationToken);

        Assert.Equal(first.ContentSha256, second.ContentSha256);
        Assert.Equal("library-80", first.FixtureKind);
        Assert.Equal(80, first.TrackCount);
        Assert.Equal(4, first.AlbumCount);
        Assert.Equal(4, first.ArtistCount);
        Assert.Equal(4, first.GenreCount);
        Assert.Equal(4, first.ArtworkCount);
        Assert.Equal(20, first.PlaylistCount);
        Assert.True(File.Exists(Path.Combine(firstRoot, PerformanceFixtureGenerator.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(firstRoot, PerformanceFixtureGenerator.MarkerFileName)));
        Assert.Equal(4, Directory.EnumerateFiles(Path.Combine(firstRoot, "artwork"), "*.art").Count());

        var repository = new SqliteLibraryRepository(new AppPaths(firstRoot));
        await repository.InitializeAsync(cancellationToken);
        var stats = await repository.GetStatsAsync(cancellationToken);
        Assert.Equal(80, stats.TrackCount);
        Assert.Equal(4, stats.AlbumCount);
        Assert.Equal(4, stats.ArtistCount);

        var playlists = new SqlitePlaylistRepository(new SqliteLibraryRepository(new AppPaths(firstRoot)));
        Assert.Equal(20, (await playlists.GetAllAsync(cancellationToken)).Count);
        var summaries = await playlists.GetSummariesAsync(cancellationToken);
        Assert.Equal(20, summaries.Count);
        Assert.All(summaries, summary => Assert.True(summary.TrackCount > 0));
        Assert.All(summaries, summary => Assert.NotNull(summary.RepresentativeTrack));
        Assert.Equal(80, (await playlists.GetTracksAsync(1, cancellationToken)).Count);
    }

    [Fact]
    public async Task ForceWillNotReplaceAnUnmarkedDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var output = Path.Combine(_root, "unmarked");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "keep.txt"), "user data", cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PerformanceFixtureGenerator().GenerateAsync(new PerformanceFixtureOptions(20, output, Force: true), cancellationToken: cancellationToken));

        Assert.Contains("unmarked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(output, "keep.txt")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
