using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class LibraryPlaylistTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Dextromethorphan.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BatchUpsertMaintainsFtsIndexAndFileIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteLibraryRepository(new AppPaths(_root));
        await repository.InitializeAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await repository.UpsertBatchAsync([
            Track("halo.flac", "Halo", "Beyoncé", "Pop", now),
            Track("blue.flac", "Cerulean Dream", "Miles Davis", "Jazz", now),
            Track("take-five.flac", "Take Five", "Dave Brubeck", "Jazz", now)
        ], cancellationToken);

        Assert.Equal("Halo", Assert.Single(await repository.SearchAsync("beyon", cancellationToken: cancellationToken)).Title);
        var blue = Assert.Single(await repository.SearchAsync("ceru dre", cancellationToken: cancellationToken));
        await repository.UpsertAsync(Track("blue.flac", "Azure in Green", "Miles Davis", "Jazz", now) with { Comment = "modal masterpiece" }, cancellationToken);
        Assert.Empty(await repository.SearchAsync("cerulean dream", cancellationToken: cancellationToken));
        Assert.Equal("Azure in Green", Assert.Single(await repository.SearchAsync("modal mast", cancellationToken: cancellationToken)).Title);

        var index = await repository.GetFileIndexAsync(cancellationToken);
        Assert.Equal(3, index.Count);
        Assert.True(index.ContainsKey(Path.Combine(_root, "halo.flac")));
        Assert.True(blue.Id > 0);
    }

    [Fact]
    public async Task ExistingLibraryGetsOneTimeFtsRebuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);
        await repository.UpsertAsync(Track("legacy.flac", "Legacy Song", "Archive", "Jazz", DateTimeOffset.UtcNow), cancellationToken);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={paths.DatabaseFile}"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER tracks_ai; DROP TRIGGER tracks_ad; DROP TRIGGER tracks_au; DROP TABLE tracks_fts;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await new SqliteLibraryRepository(paths).InitializeAsync(cancellationToken);
        Assert.Equal("Legacy Song", Assert.Single(await repository.SearchAsync("legacy", cancellationToken: cancellationToken)).Title);
    }

    [Fact]
    public async Task ManualAndSmartPlaylistsReturnExpectedTracks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var library = new SqliteLibraryRepository(new AppPaths(_root));
        await library.InitializeAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await library.UpsertBatchAsync([
            Track("a.flac", "Alpha", "Quartet", "Jazz", now),
            Track("b.mp3", "Beta", "Band", "Rock", now),
            Track("c.flac", "Charlie", "Trio", "Jazz", now)
        ], cancellationToken);
        var tracks = await library.SearchAsync("", cancellationToken: cancellationToken);
        var alpha = tracks.Single(x => x.Title == "Alpha");
        var beta = tracks.Single(x => x.Title == "Beta");
        var charlie = tracks.Single(x => x.Title == "Charlie");
        await library.SetRatingAsync(alpha.Id, 5, true, cancellationToken);
        await library.SetRatingAsync(charlie.Id, 4, false, cancellationToken);

        var playlists = new SqlitePlaylistRepository(library);
        var manualId = await playlists.CreateManualAsync("Morning", cancellationToken);
        await playlists.ReplaceTracksAsync(manualId, [charlie.Id, beta.Id, alpha.Id], cancellationToken);
        Assert.Equal(["Charlie", "Beta", "Alpha"], (await playlists.GetTracksAsync(manualId, cancellationToken)).Select(x => x.Title));

        var smartId = await playlists.CreateSmartAsync("Highly rated jazz", new SmartPlaylistDefinition
        {
            Root = new SmartRuleGroup
            {
                Match = SmartRuleMatch.All,
                Conditions = [
                    new SmartRuleCondition { Field = SmartField.Genre, Operator = SmartOperator.Equals, Value = "Jazz" },
                    new SmartRuleCondition { Field = SmartField.Rating, Operator = SmartOperator.GreaterOrEqual, Value = "4" }
                ]
            },
            SortBy = SmartField.Title,
            SortDescending = true
        }, cancellationToken);

        Assert.Equal(["Charlie", "Alpha"], (await playlists.GetTracksAsync(smartId, cancellationToken)).Select(x => x.Title));
        var summaries = await playlists.GetSummariesAsync(cancellationToken);
        var manualSummary = summaries.Single(x => x.Playlist.Id == manualId);
        var smartSummary = summaries.Single(x => x.Playlist.Id == smartId);
        Assert.Equal(3, manualSummary.TrackCount);
        Assert.Equal("Charlie", manualSummary.RepresentativeTrack?.Title);
        Assert.Equal(2, smartSummary.TrackCount);
        Assert.Equal("Charlie", smartSummary.RepresentativeTrack?.Title);
        await Assert.ThrowsAsync<InvalidOperationException>(() => playlists.AddTracksAsync(smartId, [beta.Id], cancellationToken));
        await playlists.UpdateSmartRulesAsync(smartId, new SmartPlaylistDefinition
        {
            Root = new SmartRuleGroup
            {
                Conditions = [new SmartRuleCondition { Field = SmartField.Loved, Operator = SmartOperator.IsTrue }]
            }
        }, cancellationToken);
        Assert.Equal("Alpha", Assert.Single(await playlists.GetTracksAsync(smartId, cancellationToken)).Title);
    }

    [Fact]
    public async Task HighLevelImportCreatesOrderedLibraryPlaylist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var library = new SqliteLibraryRepository(new AppPaths(_root));
        await library.InitializeAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await library.UpsertBatchAsync([
            Track("first.flac", "First", "Artist", "Jazz", now),
            Track("second.flac", "Second", "Artist", "Jazz", now)
        ], cancellationToken);
        var tracks = await library.SearchAsync("", cancellationToken: cancellationToken);
        var repository = new SqlitePlaylistRepository(library);
        var sourceId = await repository.CreateManualAsync("Imported order", cancellationToken);
        await repository.ReplaceTracksAsync(sourceId, tracks.OrderByDescending(x => x.Title).Select(x => x.Id).ToArray(), cancellationToken);
        var interchange = new PlaylistInterchangeService();
        var files = new PlaylistFileService(repository, library, interchange);
        var path = Path.Combine(_root, "ordered.m3u8");
        await files.ExportAsync(sourceId, path, PlaylistFormat.M3U8, cancellationToken);

        var importedId = await files.ImportAsync(path, cancellationToken);
        Assert.Equal(["Second", "First"], (await repository.GetTracksAsync(importedId, cancellationToken)).Select(x => x.Title));
        Assert.Equal("ordered", (await repository.GetAsync(importedId, cancellationToken))!.Name);
    }

    [Theory]
    [InlineData(PlaylistFormat.M3U8, "mix.m3u8")]
    [InlineData(PlaylistFormat.PLS, "mix.pls")]
    [InlineData(PlaylistFormat.XSPF, "mix.xspf")]
    public async Task PlaylistFormatsRoundTripLocations(PlaylistFormat format, string fileName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = new PlaylistInterchangeService();
        var path = Path.Combine(_root, fileName);
        var now = DateTimeOffset.UtcNow;
        var tracks = new[]
        {
            Track("one.flac", "One", "Artist", "Jazz", now),
            Track("two.mp3", "Two", "Artist", "Jazz", now)
        };
        await service.ExportAsync(path, "Road Trip", tracks, format, cancellationToken);
        var imported = await service.ImportAsync(path, cancellationToken);

        Assert.Equal(tracks.Select(x => Path.GetFullPath(x.Path)), imported.Locations.Select(Path.GetFullPath));
        Assert.Equal(format == PlaylistFormat.XSPF ? "Road Trip" : "mix", imported.Name);
    }

    private Track Track(string fileName, string title, string artist, string genre, DateTimeOffset modifiedAt) => new()
    {
        Path = Path.Combine(_root, fileName),
        Title = title,
        Artist = artist,
        Album = "Test Album",
        Genre = genre,
        Duration = TimeSpan.FromMinutes(3),
        FileModifiedAt = modifiedAt,
        FileSize = 1024,
        Codec = Path.GetExtension(fileName).TrimStart('.')
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
