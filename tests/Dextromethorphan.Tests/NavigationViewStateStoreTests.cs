using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class NavigationViewStateStoreTests
{
    [Fact]
    public void KeepsIndependentOffsetsAndMaterializedCounts()
    {
        var store = new NavigationViewStateStore();

        store.Capture("primary:Albums", 1840.5, 112);
        store.Capture("primary:Artists", 320.25, 56);

        Assert.Equal(new NavigationViewState(1840.5, 112), store.Get("primary:Albums"));
        Assert.Equal(new NavigationViewState(320.25, 56), store.Get("primary:Artists"));
        Assert.Equal(NavigationViewState.Empty, store.Get("primary:Genres"));
    }

    [Fact]
    public void NormalizesInvalidScrollState()
    {
        var store = new NavigationViewStateStore();

        store.Capture("collection:Albums:Album:one", -20, -4);

        Assert.Equal(NavigationViewState.Empty, store.Get("collection:Albums:Album:one"));
    }

    [Fact]
    public void KeepsAStableGalleryRowAnchorAlongsideDiagnosticPixelOffset()
    {
        var store = new NavigationViewStateStore();

        store.Capture("primary:Albums", 721.5, 302, 3, 41.25);

        Assert.Equal(
            new NavigationViewState(721.5, 302, 3, 41.25),
            store.Get("primary:Albums"));
    }

    [Fact]
    public void TrimPreservesActiveKeysAndDropsStaleHistory()
    {
        var store = new NavigationViewStateStore();
        store.Capture("primary:Albums", 100, 302);
        store.Capture("collection:old", 200, 20);
        store.Capture("collection:current", 300, 30);

        var removed = store.Trim(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "primary:Albums",
                "collection:current"
            },
            DateTimeOffset.UtcNow.AddSeconds(1),
            maximumEntries: 2);

        Assert.Equal(1, removed);
        Assert.Equal(
            NavigationViewState.Empty,
            store.Get("collection:old"));
        Assert.Equal(
            new NavigationViewState(300, 30),
            store.Get("collection:current"));
    }
}
