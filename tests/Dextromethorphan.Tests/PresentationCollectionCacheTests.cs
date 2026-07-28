using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class PresentationCollectionCacheTests
{
    [Fact]
    public void ReusesPresentationCollectionWithoutRebuildingSource()
    {
        var cache = new PresentationCollectionCache<int>();
        var builds = 0;

        var first = cache.GetOrCreate("Albums", Build, 2, out var firstHit);
        PresentationCollectionCache<int>.EnsureMaterialized(first, 4);
        var second = cache.GetOrCreate("Albums", Build, 2, out var secondHit);

        Assert.False(firstHit);
        Assert.True(secondHit);
        Assert.Same(first, second);
        Assert.Same(first.Items, second.Items);
        Assert.Equal([1, 2, 3, 4], second.Items);
        Assert.Equal(1, builds);
        return;

        IReadOnlyList<int> Build()
        {
            builds++;
            return [1, 2, 3, 4, 5];
        }
    }

    [Fact]
    public void ClearCreatesAFreshPresentationForANewLibraryGeneration()
    {
        var cache = new PresentationCollectionCache<int>();
        var first = cache.GetOrCreate("Songs", () => [1], int.MaxValue, out _);

        cache.Clear();
        var second = cache.GetOrCreate("Songs", () => [2], int.MaxValue, out var cacheHit);

        Assert.False(cacheHit);
        Assert.NotSame(first, second);
        Assert.Equal([2], second.Items);
    }
}
