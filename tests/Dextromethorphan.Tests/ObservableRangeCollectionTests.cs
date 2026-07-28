using System.Collections.Specialized;
using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class ObservableRangeCollectionTests
{
    [Fact]
    public void ReplaceRangePublishesOneResetForTheWholeBatch()
    {
        var collection = new ObservableRangeCollection<int> { 9, 8 };
        var changes = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, change) => changes.Add(change);

        collection.ReplaceRange(Enumerable.Range(1, 10_000));

        Assert.Equal(10_000, collection.Count);
        Assert.Equal(1, collection[0]);
        Assert.Equal(10_000, collection[^1]);
        var reset = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Reset, reset.Action);
    }

    [Fact]
    public void ReplaceRangeEnumeratesAStreamingSourceOnce()
    {
        var collection = new ObservableRangeCollection<int>();
        var enumerations = 0;

        collection.ReplaceRange(Values());

        Assert.Equal([1, 2, 3], collection);
        Assert.Equal(1, enumerations);
        return;

        IEnumerable<int> Values()
        {
            enumerations++;
            yield return 1;
            yield return 2;
            yield return 3;
        }
    }
}
