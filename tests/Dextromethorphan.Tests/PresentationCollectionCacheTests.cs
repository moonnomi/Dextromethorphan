using Dextromethorphan.App.UI;
using System.Collections.ObjectModel;

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

    [Fact]
    public void MaximumInitialCountExposesTheCompleteVirtualizedSource()
    {
        var cache = new PresentationCollectionCache<int>();

        var presentation = cache.GetOrCreate(
            "Albums",
            () => Enumerable.Range(1, 302).ToArray(),
            int.MaxValue,
            out _);

        Assert.Equal(302, presentation.Source.Count);
        Assert.Equal(presentation.Source, presentation.Items);
    }

    [Fact]
    public void CompleteObservableSourceRemainsLiveAfterCaching()
    {
        var source = new ObservableCollection<int>([1, 2, 3]);
        var cache = new PresentationCollectionCache<int>();
        var presentation = cache.GetOrCreate(
            "Albums",
            () => source,
            int.MaxValue,
            out _);

        source.Add(4);

        Assert.Same(source, presentation.Items);
        Assert.Equal([1, 2, 3, 4], presentation.Items);
    }

    [Fact]
    public void IdleCleanupCanRemoveOnlyStaleEntries()
    {
        var cache = new PresentationCollectionCache<int>();
        cache.GetOrCreate("primary:Songs", () => [1], 1, out _);
        cache.GetOrCreate("collection:old", () => [2], 1, out _);
        cache.GetOrCreate("collection:current", () => [3], 1, out _);

        var removed = cache.RemoveWhere(
            key => key.StartsWith(
                       "collection:",
                       StringComparison.Ordinal)
                   && key != "collection:current");

        Assert.Equal(1, removed);
        var retained = cache.GetOrCreate(
            "collection:current",
            () => [9],
            1,
            out var hit);
        Assert.True(hit);
        Assert.Equal([3], retained.Items);
    }
}
