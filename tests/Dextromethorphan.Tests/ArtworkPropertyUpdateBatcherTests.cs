using System.Collections.Concurrent;
using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.Tests;

public sealed class ArtworkPropertyUpdateBatcherTests
{
    [Fact]
    public void CoalescesUpdatesAndLimitsEveryFlush()
    {
        var scheduled = new Queue<Action>();
        using var batcher = new ArtworkPropertyUpdateBatcher(scheduled.Enqueue, maximumBatchSize: 4);
        var applied = new List<int>();

        for (var index = 0; index < 10; index++)
        {
            var value = index;
            batcher.Enqueue(() => applied.Add(value), CancellationToken.None);
        }

        Assert.Single(scheduled);
        scheduled.Dequeue()();
        Assert.Equal([0, 1, 2, 3], applied);
        Assert.Single(scheduled);
        scheduled.Dequeue()();
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], applied);
        Assert.Single(scheduled);
        scheduled.Dequeue()();

        Assert.Equal(Enumerable.Range(0, 10), applied);
        Assert.Empty(scheduled);
        var metrics = batcher.GetMetrics();
        Assert.Equal(3, metrics.Batches);
        Assert.Equal(4, metrics.LargestBatch);
        Assert.Equal(10, metrics.Applied);
        Assert.Equal(0, metrics.Pending);
    }

    [Fact]
    public void DropsCanceledGenerationsBeforeApplyingBindings()
    {
        var scheduled = new Queue<Action>();
        using var batcher = new ArtworkPropertyUpdateBatcher(scheduled.Enqueue);
        using var stale = new CancellationTokenSource();
        var applied = new List<string>();

        batcher.Enqueue(() => applied.Add("current"), CancellationToken.None);
        batcher.Enqueue(() => applied.Add("stale"), stale.Token);
        stale.Cancel();
        scheduled.Dequeue()();

        Assert.Equal(["current"], applied);
        var metrics = batcher.GetMetrics();
        Assert.Equal(1, metrics.Applied);
        Assert.Equal(1, metrics.Dropped);
    }

    [Fact]
    public void ConcurrentProducersScheduleOneFlushAndApplyEveryUpdateOnce()
    {
        var scheduled = new ConcurrentQueue<Action>();
        using var batcher = new ArtworkPropertyUpdateBatcher(scheduled.Enqueue, maximumBatchSize: 12);
        var applied = new ConcurrentDictionary<int, int>();

        Parallel.For(0, 100, index =>
            batcher.Enqueue(
                () => applied.AddOrUpdate(index, 1, (_, count) => count + 1),
                CancellationToken.None));

        Assert.Single(scheduled);
        while (scheduled.TryDequeue(out var flush)) flush();

        Assert.Equal(100, applied.Count);
        Assert.All(applied.Values, count => Assert.Equal(1, count));
        var metrics = batcher.GetMetrics();
        Assert.Equal(100, metrics.Applied);
        Assert.Equal(9, metrics.Batches);
        Assert.Equal(0, metrics.Pending);
    }
}
