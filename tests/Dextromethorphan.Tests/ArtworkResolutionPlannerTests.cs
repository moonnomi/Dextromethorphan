using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class ArtworkResolutionPlannerTests
{
    [Theory]
    [InlineData("Albums")]
    [InlineData("Artists")]
    [InlineData("Genres")]
    public void GalleryViewsResolveOnlyExposedGalleryCards(string view)
    {
        var gallery = new[] { Card("gallery-1"), Card("gallery-2") };
        var sidebar = new[] { Card("sidebar") };

        var planned = ArtworkResolutionPlanner.ForActiveView(view, false, null, gallery, sidebar);

        Assert.Same(gallery, planned);
        Assert.DoesNotContain(sidebar[0], planned);
    }

    [Theory]
    [InlineData("Folders")]
    [InlineData("Playlists")]
    public void SidebarViewsResolveOnlyTheActiveSidebar(string view)
    {
        var gallery = new[] { Card("gallery") };
        var sidebar = new[] { Card("sidebar-1"), Card("sidebar-2") };

        var planned = ArtworkResolutionPlanner.ForActiveView(view, false, null, gallery, sidebar);

        Assert.Same(sidebar, planned);
        Assert.DoesNotContain(gallery[0], planned);
    }

    [Fact]
    public void CollectionDetailResolvesOnlyTheSelectedCard()
    {
        var selected = Card("selected");
        var planned = ArtworkResolutionPlanner.ForActiveView(
            "Albums",
            true,
            selected,
            [Card("gallery")],
            [Card("sidebar")]);

        Assert.Single(planned);
        Assert.Same(selected, planned[0]);
    }

    [Theory]
    [InlineData("Songs")]
    [InlineData("Favorites")]
    [InlineData("Now Playing")]
    public void TrackAndPlaybackViewsDoNotStartCollectionArtworkResolution(string view)
    {
        var planned = ArtworkResolutionPlanner.ForActiveView(
            view,
            false,
            Card("selected"),
            [Card("gallery")],
            [Card("sidebar")]);

        Assert.Empty(planned);
    }

    private static LibraryCardViewModel Card(string key) => new()
    {
        Kind = "Album",
        Key = key,
        Title = key,
        Tracks = [new Track { Path = $"{key}.flac", Title = key }]
    };
}
