using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.App.UI;

public sealed class ArtworkImageService : IDisposable
{
    private const long DefaultBudgetBytes = 16L * 1024 * 1024;
    private const int DecoderWorkers = 2;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ArtworkFailureEntry> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _latestRequestKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly PriorityWorkScheduler<BitmapSource?> _scheduler;
    private readonly DeveloperDiagnostics _diagnostics;
    private readonly ArtworkPropertyUpdateBatcher _artworkUpdates;
    private readonly PersistentArtworkThumbnailStore _persistentThumbnails;
    private long _cacheBytes;
    private long _requests;
    private long _cacheHits;
    private long _cacheMisses;
    private long _decodes;
    private long _decodeFailures;

    public ArtworkImageService(
        DeveloperDiagnostics diagnostics,
        ArtworkPropertyUpdateBatcher artworkUpdates,
        PersistentArtworkThumbnailStore persistentThumbnails)
    {
        _diagnostics = diagnostics;
        _artworkUpdates = artworkUpdates;
        _persistentThumbnails = persistentThumbnails;
        _scheduler = new PriorityWorkScheduler<BitmapSource?>(DecoderWorkers);
        Current = this;
    }

    internal static ArtworkImageService? Current { get; private set; }
    internal static event Action<string>? SourceInvalidated;

    internal async Task<BitmapSource?> GetAsync(
        string path,
        int decodePixelWidth,
        ArtworkRequestPriority priority,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        Interlocked.Increment(ref _requests);
        var requestedSize = Math.Clamp(decodePixelWidth, 32, 1200);
        var size = ArtworkThumbnailVariant.ForRequestedWidth(requestedSize).PixelWidth;
        var fullPath = Path.GetFullPath(path);
        var identity = await Task.Run(
            () => _persistentThumbnails.GetSourceIdentity(fullPath),
            cancellationToken).ConfigureAwait(false);
        var requestAlias = $"{fullPath}|{size}";
        var key = $"{fullPath}|{identity ?? "missing"}|{size}";
        _latestRequestKeys[requestAlias] = key;
        if (TryGet(key, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            if (_diagnostics.Verbose)
                _diagnostics.Mark("artwork", "thumbnail.strong-cache-hit", Data(path, size));
            return cached;
        }
        Interlocked.Increment(ref _cacheMisses);
        if (_failures.TryGetValue(key, out var failure)
            && (failure.Kind == ArtworkFailureKind.Permanent || failure.RetryAt > DateTimeOffset.UtcNow))
            return null;

        var before = _scheduler.GetMetrics();
        var result = await _scheduler.RunAsync(
            key,
            priority,
            ct => Task.FromResult(Decode(key, path, requestedSize, size, ct)),
            cancellationToken).ConfigureAwait(false);
        var after = _scheduler.GetMetrics();
        if (after.Deduplicated > before.Deduplicated)
        {
            if (_diagnostics.Enabled)
                _diagnostics.Mark("artwork", "thumbnail.request-deduplicated", Data(path, size));
        }
        return result;
    }

    internal void EnqueuePropertyUpdate(Action update, CancellationToken cancellationToken) =>
        _artworkUpdates.Enqueue(update, cancellationToken);

    internal ArtworkFailureSnapshot GetFailure(string path, int decodePixelWidth)
    {
        var size = ArtworkThumbnailVariant.ForRequestedWidth(
            Math.Clamp(decodePixelWidth, 32, 1200)).PixelWidth;
        var alias = $"{Path.GetFullPath(path)}|{size}";
        if (!_latestRequestKeys.TryGetValue(alias, out var key)
            || !_failures.TryGetValue(key, out var failure))
            return ArtworkFailureSnapshot.None;
        return new ArtworkFailureSnapshot(
            failure.Kind,
            failure.Reason,
            failure.Attempts,
            failure.Kind == ArtworkFailureKind.Permanent
                ? TimeSpan.Zero
                : failure.RetryAt - DateTimeOffset.UtcNow);
    }

    private BitmapSource? Decode(
        string key,
        string path,
        int requestedSize,
        int variantSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _decodes);
        var timer = Stopwatch.StartNew();
        Exception? error = null;
        try
        {
            var prepared = _persistentThumbnails.GetOrCreate(path, requestedSize, cancellationToken);
            if (prepared is { SourceRejected: true })
            {
                Interlocked.Increment(ref _decodeFailures);
                RecordFailure(
                    key,
                    ArtworkFailureKind.Permanent,
                    prepared.Value.Rejection.ToString());
                return null;
            }
            var decodePath = prepared?.Path ?? path;
            if (!File.Exists(decodePath))
            {
                Interlocked.Increment(ref _decodeFailures);
                RecordFailure(key, ArtworkFailureKind.Transient, "SourceMissing");
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.Open(decodePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = variantSize;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Add(key, image);
            _failures.TryRemove(key, out _);
            return image;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException)
        {
            error = exception;
            Interlocked.Increment(ref _decodeFailures);
            RecordFailure(
                key,
                ArtworkFailurePolicy.Classify(exception),
                exception.GetType().Name);
            _diagnostics.Error("artwork", "thumbnail.decode", exception, Data(path, variantSize));
            return null;
        }
        finally
        {
            _diagnostics.RecordDuration("artwork", "thumbnail.decode-off-thread", timer.Elapsed, Data(path, variantSize), error);
        }
    }

    internal ArtworkRuntimeMetrics GetRuntimeMetrics()
    {
        int cacheEntries;
        long cacheBytes;
        lock (_cacheGate)
        {
            cacheEntries = _cache.Count;
            cacheBytes = _cacheBytes;
        }
        var queue = _scheduler.GetMetrics();
        var persistent = _persistentThumbnails.GetMetrics();
        return new ArtworkRuntimeMetrics(
            queue.Queued + queue.Active,
            queue.Queued,
            queue.Active,
            AsyncArtwork.ActiveSourceCount,
            cacheEntries,
            cacheBytes,
            Interlocked.Read(ref _requests),
            Interlocked.Read(ref _cacheHits),
            Interlocked.Read(ref _cacheMisses),
            queue.Deduplicated,
            Interlocked.Read(ref _decodes),
            Interlocked.Read(ref _decodeFailures),
            queue.Promoted,
            queue.DroppedBeforeStart,
            persistent.Requests,
            persistent.Hits,
            persistent.SourceDecodes,
            persistent.VariantsGenerated,
            persistent.Failures);
    }

    internal void ClearMemoryCache()
    {
        lock (_cacheGate)
        {
            _cache.Clear();
            _lru.Clear();
            _cacheBytes = 0;
        }
        _failures.Clear();
        _latestRequestKeys.Clear();
        if (_diagnostics.Enabled)
            _diagnostics.Mark("artwork", "thumbnail.memory-cache-cleared");
    }

    internal void TrimMemoryCache(long targetBytes)
    {
        targetBytes = Math.Clamp(
            targetBytes,
            0,
            DefaultBudgetBytes);
        lock (_cacheGate)
        {
            while (_cacheBytes > targetBytes
                   && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                if (!_cache.Remove(last.Value, out var removed)) continue;
                _cacheBytes -= removed.Bytes;
            }
        }
        if (_diagnostics.Enabled)
            _diagnostics.Mark(
                "artwork",
                "thumbnail.memory-cache-trimmed",
                new Dictionary<string, object?>
                {
                    ["targetBytes"] = targetBytes,
                    ["cacheBytes"] = _cacheBytes
                });
    }

    internal void InvalidatePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = fullPath + "|";
        lock (_cacheGate)
        {
            foreach (var key in _cache.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                if (!_cache.Remove(key, out var removed)) continue;
                _lru.Remove(removed.Node);
                _cacheBytes -= removed.Bytes;
            }
        }
        foreach (var key in _failures.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            _failures.TryRemove(key, out _);
        foreach (var alias in _latestRequestKeys.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            _latestRequestKeys.TryRemove(alias, out _);
        SourceInvalidated?.Invoke(fullPath);
        if (_diagnostics.Enabled)
            _diagnostics.Mark("artwork", "thumbnail.source-invalidated",
                new Dictionary<string, object?> { ["extension"] = Path.GetExtension(fullPath) });
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        _scheduler.Dispose();
    }

    private bool TryGet(string key, out BitmapSource? value)
    {
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(key, out var entry))
            {
                value = null;
                return false;
            }
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
            value = entry.Value;
            return true;
        }
    }

    private void Add(string key, BitmapSource value)
    {
        var bytes = Math.Max(1L, value.PixelWidth) * Math.Max(1L, value.PixelHeight) * 4;
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _cacheBytes -= existing.Bytes;
                _lru.Remove(existing.Node);
            }
            var node = _lru.AddFirst(key);
            _cache[key] = new CacheEntry(value, bytes, node);
            _cacheBytes += bytes;
            while (_cacheBytes > DefaultBudgetBytes && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                if (!_cache.Remove(last.Value, out var removed)) continue;
                _cacheBytes -= removed.Bytes;
                if (_diagnostics.Verbose)
                    _diagnostics.Mark("artwork", "thumbnail.evicted", new Dictionary<string, object?> { ["cacheBytes"] = _cacheBytes });
            }
        }
    }

    private void RecordFailure(string key, ArtworkFailureKind kind, string reason)
    {
        _failures.AddOrUpdate(
            key,
            _ => ArtworkFailureEntry.Create(kind, reason, 1),
            (_, previous) =>
            {
                var attempts = previous.Kind == kind ? previous.Attempts + 1 : 1;
                return ArtworkFailureEntry.Create(kind, reason, attempts);
            });
    }

    private static Dictionary<string, object?> Data(string path, int size) => new()
    {
        ["size"] = size,
        ["extension"] = Path.GetExtension(path)
    };

    private sealed record CacheEntry(BitmapSource Value, long Bytes, LinkedListNode<string> Node);
    private sealed record ArtworkFailureEntry(
        ArtworkFailureKind Kind,
        string Reason,
        int Attempts,
        DateTimeOffset RetryAt)
    {
        internal static ArtworkFailureEntry Create(
            ArtworkFailureKind kind,
            string reason,
            int attempts) =>
            new(
                kind,
                reason,
                attempts,
                kind == ArtworkFailureKind.Permanent
                    ? DateTimeOffset.MaxValue
                    : DateTimeOffset.UtcNow + ArtworkFailurePolicy.RetryDelay(attempts));
    }
}

internal enum ArtworkFailureKind
{
    None,
    Transient,
    Permanent
}

internal readonly record struct ArtworkFailureSnapshot(
    ArtworkFailureKind Kind,
    string Reason,
    int Attempts,
    TimeSpan RetryAfter)
{
    internal static ArtworkFailureSnapshot None => new(
        ArtworkFailureKind.None,
        "",
        0,
        TimeSpan.Zero);
}

internal static class ArtworkFailurePolicy
{
    internal const int MaximumAutomaticAttempts = 3;

    internal static ArtworkFailureKind Classify(Exception exception) =>
        exception is IOException or UnauthorizedAccessException
            ? ArtworkFailureKind.Transient
            : ArtworkFailureKind.Permanent;

    internal static TimeSpan RetryDelay(int attempts) =>
        attempts switch
        {
            <= 1 => TimeSpan.FromMilliseconds(400),
            2 => TimeSpan.FromMilliseconds(1_500),
            _ => TimeSpan.FromSeconds(5)
        };
}

internal readonly record struct ArtworkRuntimeMetrics(
    int QueueDepth,
    int Queued,
    int Active,
    int ActiveImageSources,
    int CacheEntries,
    long CacheBytes,
    long Requests,
    long CacheHits,
    long CacheMisses,
    long DeduplicatedRequests,
    long Decodes,
    long DecodeFailures,
    long PromotedRequests,
    long DroppedBeforeDecode,
    long PersistentRequests,
    long PersistentHits,
    long PersistentSourceDecodes,
    long PersistentVariantsGenerated,
    long PersistentFailures)
{
    public double CacheHitRate => CacheHits + CacheMisses == 0
        ? 0
        : CacheHits * 100d / (CacheHits + CacheMisses);

    public double PersistentHitRate => PersistentRequests == 0
        ? 0
        : PersistentHits * 100d / PersistentRequests;
}

public static class AsyncArtwork
{
    private static readonly ConditionalWeakTable<Image, RequestState> States = new();
    private static int _activeSourceCount;
    private static readonly DependencyPropertyKey StatePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "State",
        typeof(ArtworkLoadState),
        typeof(AsyncArtwork),
        new PropertyMetadata(ArtworkLoadState.Empty));
    private static readonly DependencyPropertyKey FailureReasonPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "FailureReason",
        typeof(string),
        typeof(AsyncArtwork),
        new PropertyMetadata(""));

    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(AsyncArtwork), new PropertyMetadata(null, OnRequestChanged));

    public static readonly DependencyProperty DecodePixelWidthProperty = DependencyProperty.RegisterAttached(
        "DecodePixelWidth", typeof(int), typeof(AsyncArtwork), new PropertyMetadata(256, OnRequestChanged));

    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached(
        "Priority", typeof(ArtworkRequestPriority), typeof(AsyncArtwork),
        new PropertyMetadata(ArtworkRequestPriority.Deferred, OnRequestChanged));

    public static readonly DependencyProperty StateProperty = StatePropertyKey.DependencyProperty;
    public static readonly DependencyProperty FailureReasonProperty = FailureReasonPropertyKey.DependencyProperty;

    public static readonly RoutedEvent ArtworkLoadedEvent = EventManager.RegisterRoutedEvent(
        "ArtworkLoaded", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(AsyncArtwork));

    internal static int ActiveSourceCount => Math.Max(0, Volatile.Read(ref _activeSourceCount));

    public static string? GetPath(DependencyObject element) => (string?)element.GetValue(PathProperty);
    public static void SetPath(DependencyObject element, string? value) => element.SetValue(PathProperty, value);
    public static int GetDecodePixelWidth(DependencyObject element) => (int)element.GetValue(DecodePixelWidthProperty);
    public static void SetDecodePixelWidth(DependencyObject element, int value) => element.SetValue(DecodePixelWidthProperty, value);
    public static ArtworkRequestPriority GetPriority(DependencyObject element) => (ArtworkRequestPriority)element.GetValue(PriorityProperty);
    public static void SetPriority(DependencyObject element, ArtworkRequestPriority value) => element.SetValue(PriorityProperty, value);
    public static ArtworkLoadState GetState(DependencyObject element) => (ArtworkLoadState)element.GetValue(StateProperty);
    public static string GetFailureReason(DependencyObject element) => (string)element.GetValue(FailureReasonProperty);
    public static void AddArtworkLoadedHandler(DependencyObject element, RoutedEventHandler handler) => ((UIElement)element).AddHandler(ArtworkLoadedEvent, handler);
    public static void RemoveArtworkLoadedHandler(DependencyObject element, RoutedEventHandler handler) => ((UIElement)element).RemoveHandler(ArtworkLoadedEvent, handler);

    private static void OnRequestChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Image image) return;
        var state = States.GetValue(image, Attach);
        state.Restart();
    }

    private static RequestState Attach(Image image)
    {
        var state = new RequestState(image);
        image.Loaded += state.OnLoaded;
        image.Unloaded += state.OnUnloaded;
        image.IsVisibleChanged += state.OnIsVisibleChanged;
        return state;
    }

    private sealed class RequestState(Image image)
    {
        private CancellationTokenSource? _cancellation;
        private int _version;
        private bool _hasSource;

        public void OnLoaded(object sender, RoutedEventArgs args) => Restart();
        public void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (args.NewValue is true) Restart();
            else
            {
                ArtworkImageService.SourceInvalidated -= OnSourceInvalidated;
                Cancel(clearSource: true);
            }
        }

        public void OnUnloaded(object sender, RoutedEventArgs args)
        {
            ArtworkImageService.SourceInvalidated -= OnSourceInvalidated;
            Cancel(clearSource: true);
        }

        private void Cancel(bool clearSource)
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            if (clearSource)
            {
                image.BeginAnimation(UIElement.OpacityProperty, null);
                ClearSource();
                image.Opacity = 0;
                SetVisualState(ArtworkLoadState.Empty);
            }
        }

        public void Restart()
        {
            var version = ++_version;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            image.BeginAnimation(UIElement.OpacityProperty, null);
            ClearSource();
            image.Opacity = 0;
            SetVisualState(ArtworkLoadState.Empty);
            var path = GetPath(image);
            if (!image.IsLoaded || !image.IsVisible || string.IsNullOrWhiteSpace(path) || ArtworkImageService.Current is null) return;
            ArtworkImageService.SourceInvalidated -= OnSourceInvalidated;
            ArtworkImageService.SourceInvalidated += OnSourceInvalidated;
            var source = new CancellationTokenSource();
            _cancellation = source;
            SetVisualState(ArtworkLoadState.Loading);
            _ = LoadAsync(
                path,
                Math.Clamp(GetDecodePixelWidth(image), 32, 1200),
                GetPriority(image),
                version,
                source.Token);
        }

        private void OnSourceInvalidated(string path)
        {
            var current = GetPath(image);
            if (string.IsNullOrWhiteSpace(current)
                || !Path.GetFullPath(current).Equals(path, StringComparison.OrdinalIgnoreCase))
                return;
            _ = image.Dispatcher.BeginInvoke(Restart);
        }

        private async Task LoadAsync(
            string path,
            int size,
            ArtworkRequestPriority priority,
            int version,
            CancellationToken cancellationToken)
        {
            try
            {
                var service = ArtworkImageService.Current;
                if (service is null) return;
                for (var attempt = 1; attempt <= ArtworkFailurePolicy.MaximumAutomaticAttempts; attempt++)
                {
                    var bitmap = await service.GetAsync(path, size, priority, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested) return;
                    if (bitmap is not null)
                    {
                        service.EnqueuePropertyUpdate(
                            () =>
                            {
                                if (version != _version || !image.IsLoaded || !image.IsVisible) return;
                                SetSource(bitmap);
                                SetVisualState(ArtworkLoadState.Loaded);
                                Reveal();
                                image.RaiseEvent(new RoutedEventArgs(ArtworkLoadedEvent, image));
                            },
                            cancellationToken);
                        return;
                    }

                    var failure = service.GetFailure(path, size);
                    if (failure.Kind == ArtworkFailureKind.Transient
                        && attempt < ArtworkFailurePolicy.MaximumAutomaticAttempts)
                    {
                        service.EnqueuePropertyUpdate(
                            () =>
                            {
                                if (version != _version || !image.IsLoaded || !image.IsVisible) return;
                                SetVisualState(ArtworkLoadState.Retrying, failure.Reason);
                            },
                            cancellationToken);
                        var delay = failure.RetryAfter <= TimeSpan.Zero
                            ? ArtworkFailurePolicy.RetryDelay(attempt)
                            : failure.RetryAfter;
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var state = failure.Kind == ArtworkFailureKind.Permanent
                        ? ArtworkLoadState.FailedPermanent
                        : ArtworkLoadState.FailedTransient;
                    service.EnqueuePropertyUpdate(
                        () =>
                        {
                            if (version != _version || !image.IsLoaded || !image.IsVisible) return;
                            SetVisualState(state, failure.Reason);
                        },
                        cancellationToken);
                    return;
                }
            }
            catch (OperationCanceledException) { }
        }

        private void SetVisualState(ArtworkLoadState state, string reason = "")
        {
            image.SetValue(StatePropertyKey, state);
            image.SetValue(FailureReasonPropertyKey, reason);
        }

        private void SetSource(BitmapSource source)
        {
            image.Source = source;
            if (_hasSource) return;
            _hasSource = true;
            Interlocked.Increment(ref _activeSourceCount);
        }

        private void ClearSource()
        {
            image.Source = null;
            if (!_hasSource) return;
            _hasSource = false;
            Interlocked.Decrement(ref _activeSourceCount);
        }

        private void Reveal()
        {
            image.BeginAnimation(UIElement.OpacityProperty, null);
            image.Opacity = 1;
            if (!MotionPolicy.IsEnabled(
                    Window.GetWindow(image)?.DataContext is not MainViewModel viewModel
                        || viewModel.AnimationsEnabled))
                return;

            var animation = new DoubleAnimation(
                0,
                1,
                new Duration(TimeSpan.FromMilliseconds(160)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            var revealVersion = _version;
            animation.Completed += (_, _) =>
            {
                if (revealVersion != _version) return;
                image.BeginAnimation(UIElement.OpacityProperty, null);
                image.Opacity = 1;
            };
            image.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }
}

public enum ArtworkLoadState
{
    Empty,
    Loading,
    Retrying,
    Loaded,
    FailedTransient,
    FailedPermanent
}
