using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Dextromethorphan.App.Diagnostics;

namespace Dextromethorphan.App.ViewModels;

public sealed class ArtworkPropertyUpdateBatcher : IDisposable
{
    internal const int DefaultMaximumBatchSize = 12;
    private static readonly TimeSpan DefaultCoalesceDelay = TimeSpan.FromMilliseconds(12);
    private readonly ConcurrentQueue<PendingUpdate> _pending = new();
    private readonly Action<Action> _schedule;
    private readonly DeveloperDiagnostics? _diagnostics;
    private readonly int _maximumBatchSize;
    private int _flushScheduled;
    private int _disposed;
    private long _enqueued;
    private long _applied;
    private long _dropped;
    private long _batches;
    private int _largestBatch;

    public ArtworkPropertyUpdateBatcher(DeveloperDiagnostics diagnostics)
        : this(ScheduleOnDispatcher, diagnostics, DefaultMaximumBatchSize) { }

    internal ArtworkPropertyUpdateBatcher(
        Action<Action> schedule,
        DeveloperDiagnostics? diagnostics = null,
        int maximumBatchSize = DefaultMaximumBatchSize)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBatchSize, 1);
        _schedule = schedule;
        _diagnostics = diagnostics;
        _maximumBatchSize = maximumBatchSize;
    }

    public void Enqueue(Action update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _pending.Enqueue(new PendingUpdate(update, cancellationToken));
        Interlocked.Increment(ref _enqueued);
        ScheduleFlush();
    }

    internal ArtworkUpdateBatchMetrics GetMetrics() => new(
        _pending.Count,
        Interlocked.Read(ref _enqueued),
        Interlocked.Read(ref _applied),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _batches),
        Volatile.Read(ref _largestBatch));

    private void ScheduleFlush()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
            return;

        try
        {
            _schedule(Flush);
        }
        catch
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            throw;
        }
    }

    private void Flush()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            DrainAsDropped();
            Interlocked.Exchange(ref _flushScheduled, 0);
            return;
        }

        var timer = Stopwatch.StartNew();
        var dequeued = 0;
        var applied = 0;
        var dropped = 0;
        while (dequeued < _maximumBatchSize && _pending.TryDequeue(out var pending))
        {
            dequeued++;
            if (pending.CancellationToken.IsCancellationRequested)
            {
                dropped++;
                continue;
            }

            try
            {
                pending.Update();
                applied++;
            }
            catch (Exception exception)
            {
                dropped++;
                _diagnostics?.Error("artwork", "property-update", exception);
            }
        }

        if (dequeued > 0)
        {
            Interlocked.Add(ref _applied, applied);
            Interlocked.Add(ref _dropped, dropped);
            Interlocked.Increment(ref _batches);
            UpdateLargestBatch(dequeued);
            _diagnostics?.RecordDuration(
                "artwork",
                "property-update-batch",
                timer.Elapsed,
                new Dictionary<string, object?>
                {
                    ["dequeued"] = dequeued,
                    ["applied"] = applied,
                    ["dropped"] = dropped,
                    ["remaining"] = _pending.Count
                });
        }

        Interlocked.Exchange(ref _flushScheduled, 0);
        if (!_pending.IsEmpty) ScheduleFlush();
    }

    private void UpdateLargestBatch(int count)
    {
        while (true)
        {
            var current = Volatile.Read(ref _largestBatch);
            if (current >= count ||
                Interlocked.CompareExchange(ref _largestBatch, count, current) == current)
                return;
        }
    }

    private void DrainAsDropped()
    {
        long dropped = 0;
        while (_pending.TryDequeue(out _)) dropped++;
        if (dropped > 0) Interlocked.Add(ref _dropped, dropped);
    }

    private static void ScheduleOnDispatcher(Action flush)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("The application dispatcher is unavailable.");
        _ = dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(DefaultCoalesceDelay);
            flush();
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DrainAsDropped();
    }

    private readonly record struct PendingUpdate(Action Update, CancellationToken CancellationToken);
}

internal readonly record struct ArtworkUpdateBatchMetrics(
    int Pending,
    long Enqueued,
    long Applied,
    long Dropped,
    long Batches,
    int LargestBatch);
