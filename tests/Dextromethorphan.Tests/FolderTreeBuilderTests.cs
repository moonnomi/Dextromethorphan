using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class FolderTreeBuilderTests
{
    [Fact]
    public void BuildsNestedSourceHierarchyWithAggregateTrackCounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "Dextromethorphan.Tree", Guid.NewGuid().ToString("N"));
        var tracks = new[]
        {
            TrackAt(root, "Artist", "Album", "Disc 1", "one.flac"),
            TrackAt(root, "Artist", "Album", "Disc 2", "two.flac"),
            TrackAt(root, "Loose", "three.mp3")
        };

        var tree = FolderTreeBuilder.Build(tracks, [new LibrarySourceSettings { Path = root }]);

        var source = Assert.Single(tree);
        Assert.Equal(3, source.TrackCount);
        var artist = Assert.Single(source.Children, node => node.Name == "Artist");
        Assert.Equal(2, artist.TrackCount);
        var album = Assert.Single(artist.Children);
        Assert.Equal(2, album.Children.Count);
        Assert.All(album.Children, disc => Assert.Equal(1, disc.TrackCount));
        Assert.Single(source.Children, node => node.Name == "Loose");
    }

    [Fact]
    public void DisabledSourcesAndMissingTracksAreNotShown()
    {
        var root = Path.Combine(Path.GetTempPath(), "Dextromethorphan.Tree", Guid.NewGuid().ToString("N"));
        var tree = FolderTreeBuilder.Build(
            [TrackAt(root, "missing.flac") with { IsMissing = true }],
            [new LibrarySourceSettings { Path = root, Enabled = false }]);
        Assert.Empty(tree);
    }

    private static Track TrackAt(string root, params string[] segments) => new()
    {
        Path = Path.Combine([root, .. segments]),
        Title = Path.GetFileNameWithoutExtension(segments[^1]),
        Artist = "Test",
        Album = "Test"
    };
}
