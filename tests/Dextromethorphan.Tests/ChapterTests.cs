using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class ChapterTests
{
    private static readonly string Corpus = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "AudioFormats");

    [Theory]
    [InlineData("chaptered.mp3")]
    [InlineData("chaptered.m4a")]
    [InlineData("chaptered.flac")]
    public async Task ReadsContainerChapterMetadata(string file)
    {
        var track = await new TagLibMetadataReader().ReadAsync(
            Path.Combine(Corpus, file),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, track.Chapters.Count);
        Assert.Equal("Opening chapter", track.Chapters[0].Title);
        Assert.Equal(TimeSpan.Zero, track.Chapters[0].Start);
        Assert.InRange(
            track.Chapters[0].End,
            TimeSpan.FromMilliseconds(749),
            TimeSpan.FromMilliseconds(751));
        Assert.Equal("Final chapter", track.Chapters[1].Title);
        Assert.InRange(
            track.Chapters[1].Start,
            TimeSpan.FromMilliseconds(749),
            TimeSpan.FromMilliseconds(751));
        Assert.InRange(
            track.Chapters[1].End,
            TimeSpan.FromMilliseconds(1_990),
            TimeSpan.FromMilliseconds(2_010));
    }

    [Fact]
    public async Task ChaptersSurviveLibraryPersistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            "Dextromethorphan.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteLibraryRepository(
                new AppPaths(root));
            await repository.InitializeAsync(cancellationToken);
            var track = await new TagLibMetadataReader().ReadAsync(
                Path.Combine(Corpus, "chaptered.mp3"),
                cancellationToken);

            await repository.UpsertAsync(track, cancellationToken);
            var restored = await repository.GetByPathAsync(
                track.Path,
                cancellationToken);

            Assert.NotNull(restored);
            Assert.Equal(track.Chapters, restored.Chapters);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
