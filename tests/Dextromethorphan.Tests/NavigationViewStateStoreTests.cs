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
}
