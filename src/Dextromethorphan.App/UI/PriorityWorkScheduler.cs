namespace Dextromethorphan.App.UI;

public enum ArtworkRequestPriority
{
    Deferred = 0,
    Visible = 1,
    Immediate = 2
}

internal sealed class PriorityWorkScheduler<TResult> : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkItem> _requests = new(StringComparer.OrdinalIgnoreCase);
    private readonly PriorityQueue<QueueTicket, (int Priority, long Sequence)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private long _sequence;
    private long _deduplicated;
    private long _promoted;
    private long _droppedBeforeStart;
    private int _active;
    private bool _disposed;
    private bool _resourcesDisposed;

    public PriorityWorkScheduler(int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        _workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(WorkerAsync))
            .ToArray();
    }

    public async Task<TResult> RunAsync(
        string key,
        ArtworkRequestPriority priority,
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        WorkItem request;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_requests.TryGetValue(key, out request!))
            {
                request.Waiters++;
                Interlocked.Increment(ref _deduplicated);
                if (request.State == WorkState.Queued && priority > request.Priority)
                {
                    request.Priority = priority;
                    request.QueueVersion++;
                    Enqueue(request);
                    Interlocked.Increment(ref _promoted);
                }
            }
            else
            {
                request = new WorkItem(key, priority, work);
                _requests.Add(key, request);
                Enqueue(request);
            }
        }

        try
        {
            return await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseWaiter(request);
        }
    }

    public WorkQueueMetrics GetMetrics()
    {
        lock (_gate)
        {
            return new WorkQueueMetrics(
                _requests.Values.Count(item => item.State == WorkState.Queued),
                _active,
                Interlocked.Read(ref _deduplicated),
                Interlocked.Read(ref _promoted),
                Interlocked.Read(ref _droppedBeforeStart));
        }
    }

    private void Enqueue(WorkItem request)
    {
        var ticket = new QueueTicket(request, request.QueueVersion);
        _queue.Enqueue(ticket, (-(int)request.Priority, _sequence++));
        _signal.Release();
    }

    private void ReleaseWaiter(WorkItem request)
    {
        lock (_gate)
        {
            request.Waiters--;
            if (request.Waiters > 0 || request.State is WorkState.Completed or WorkState.Canceled) return;

            request.Cancellation.Cancel();
            if (_requests.TryGetValue(request.Key, out var current) && ReferenceEquals(current, request))
                _requests.Remove(request.Key);
            if (request.State != WorkState.Queued) return;

            request.State = WorkState.Canceled;
            request.Completion.TrySetCanceled(request.Cancellation.Token);
            Interlocked.Increment(ref _droppedBeforeStart);
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token);
                WorkItem? request = null;
                lock (_gate)
                {
                    while (_queue.TryDequeue(out var ticket, out _))
                    {
                        if (ticket.Request.State != WorkState.Queued ||
                            ticket.Version != ticket.Request.QueueVersion ||
                            ticket.Request.Waiters == 0)
                            continue;

                        request = ticket.Request;
                        request.State = WorkState.Running;
                        _active++;
                        break;
                    }
                }
                if (request is null) continue;

                try
                {
                    var result = await request.Work(request.Cancellation.Token);
                    Complete(request, result);
                }
                catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested || _shutdown.IsCancellationRequested)
                {
                    Cancel(request);
                }
                catch (Exception exception)
                {
                    Fail(request, exception);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private void Complete(WorkItem request, TResult result)
    {
        lock (_gate)
        {
            Finish(request, WorkState.Completed);
            request.Completion.TrySetResult(result);
        }
    }

    private void Cancel(WorkItem request)
    {
        lock (_gate)
        {
            Finish(request, WorkState.Canceled);
            request.Completion.TrySetCanceled(request.Cancellation.Token);
        }
    }

    private void Fail(WorkItem request, Exception exception)
    {
        lock (_gate)
        {
            Finish(request, WorkState.Completed);
            request.Completion.TrySetException(exception);
        }
    }

    private void Finish(WorkItem request, WorkState state)
    {
        request.State = state;
        _active--;
        if (_requests.TryGetValue(request.Key, out var current) && ReferenceEquals(current, request))
            _requests.Remove(request.Key);
    }

    public void Dispose()
    {
        BeginDispose();
        _ = Task.WhenAll(_workers).ContinueWith(
            _ => DisposeResources(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        BeginDispose();
        var workers = Task.WhenAll(_workers);
        if (await Task.WhenAny(workers, Task.Delay(250)).ConfigureAwait(false) == workers)
        {
            try { await workers.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            DisposeResources();
            return;
        }
        _ = workers.ContinueWith(
            _ => DisposeResources(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void BeginDispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            foreach (var request in _requests.Values)
            {
                request.Cancellation.Cancel();
                request.Completion.TrySetCanceled(request.Cancellation.Token);
            }
            _requests.Clear();
        }
    }

    private void DisposeResources()
    {
        lock (_gate)
        {
            if (_resourcesDisposed) return;
            _resourcesDisposed = true;
            _signal.Dispose();
            _shutdown.Dispose();
        }
    }

    private sealed class WorkItem(
        string key,
        ArtworkRequestPriority priority,
        Func<CancellationToken, Task<TResult>> work)
    {
        public string Key { get; } = key;
        public ArtworkRequestPriority Priority { get; set; } = priority;
        public Func<CancellationToken, Task<TResult>> Work { get; } = work;
        public TaskCompletionSource<TResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = new();
        public WorkState State { get; set; } = WorkState.Queued;
        public int QueueVersion { get; set; }
        public int Waiters { get; set; } = 1;
    }

    private readonly record struct QueueTicket(WorkItem Request, int Version);
    private enum WorkState { Queued, Running, Completed, Canceled }
}

internal readonly record struct WorkQueueMetrics(
    int Queued,
    int Active,
    long Deduplicated,
    long Promoted,
    long DroppedBeforeStart);
