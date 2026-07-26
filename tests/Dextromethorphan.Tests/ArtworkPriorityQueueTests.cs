using System.Collections.Concurrent;
using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class ArtworkPriorityQueueTests
{
    [Fact]
    public async Task ImmediateAndVisibleWorkOvertakeDeferredWork()
    {
        await using var scheduler = new PriorityWorkScheduler<string>(1);
        var order = new ConcurrentQueue<string>();
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocker = scheduler.RunAsync("blocker", ArtworkRequestPriority.Immediate, async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
            order.Enqueue("blocker");
            return "blocker";
        }, CancellationToken.None);
        await blockerStarted.Task;

        var deferred = Enqueue("deferred", ArtworkRequestPriority.Deferred);
        var visible = Enqueue("visible", ArtworkRequestPriority.Visible);
        var immediate = Enqueue("immediate", ArtworkRequestPriority.Immediate);
        releaseBlocker.TrySetResult();

        await Task.WhenAll(blocker, deferred, visible, immediate);
        Assert.Equal(["blocker", "immediate", "visible", "deferred"], order);

        Task<string> Enqueue(string key, ArtworkRequestPriority priority) =>
            scheduler.RunAsync(key, priority, _ =>
            {
                order.Enqueue(key);
                return Task.FromResult(key);
            }, CancellationToken.None);
    }

    [Fact]
    public async Task CanceledQueuedWorkIsDroppedBeforeItRuns()
    {
        await using var scheduler = new PriorityWorkScheduler<string>(1);
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = false;

        var blocker = scheduler.RunAsync("blocker", ArtworkRequestPriority.Immediate, async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
            return "blocker";
        }, CancellationToken.None);
        await blockerStarted.Task;

        using var cancellation = new CancellationTokenSource();
        var stale = scheduler.RunAsync("stale", ArtworkRequestPriority.Deferred, _ =>
        {
            executed = true;
            return Task.FromResult("stale");
        }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);

        releaseBlocker.TrySetResult();
        await blocker;
        await Task.Delay(25, TestContext.Current.CancellationToken);

        Assert.False(executed);
        Assert.Equal(1, scheduler.GetMetrics().DroppedBeforeStart);
    }

    [Fact]
    public async Task DuplicateQueuedWorkCanBePromoted()
    {
        await using var scheduler = new PriorityWorkScheduler<string>(1);
        var order = new ConcurrentQueue<string>();
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        var blocker = scheduler.RunAsync("blocker", ArtworkRequestPriority.Immediate, async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
            return "blocker";
        }, CancellationToken.None);
        await blockerStarted.Task;

        var sharedDeferred = scheduler.RunAsync("shared", ArtworkRequestPriority.Deferred, _ =>
        {
            Interlocked.Increment(ref executions);
            order.Enqueue("shared");
            return Task.FromResult("decoded");
        }, CancellationToken.None);
        var visible = scheduler.RunAsync("visible", ArtworkRequestPriority.Visible, _ =>
        {
            order.Enqueue("visible");
            return Task.FromResult("visible");
        }, CancellationToken.None);
        var sharedImmediate = scheduler.RunAsync("shared", ArtworkRequestPriority.Immediate, _ =>
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult("wrong factory");
        }, CancellationToken.None);

        releaseBlocker.TrySetResult();
        await Task.WhenAll(blocker, sharedDeferred, visible, sharedImmediate);

        Assert.Equal(["shared", "visible"], order);
        Assert.Equal("decoded", await sharedImmediate);
        Assert.Equal(1, executions);
        var metrics = scheduler.GetMetrics();
        Assert.Equal(1, metrics.Deduplicated);
        Assert.Equal(1, metrics.Promoted);
    }
}
