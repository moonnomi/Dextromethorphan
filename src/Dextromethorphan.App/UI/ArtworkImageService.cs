using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.App.UI;

public sealed class ArtworkImageService : IDisposable
{
    private const long DefaultBudgetBytes = 96L * 1024 * 1024;
    private const int DecoderWorkers = 2;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly PriorityWorkScheduler<BitmapSource?> _scheduler;
    private readonly DeveloperDiagnostics _diagnostics;
    private readonly ArtworkPropertyUpdateBatcher _artworkUpdates;
    private long _cacheBytes;
    private long _requests;
    private long _cacheHits;
    private long _cacheMisses;
    private long _decodes;
    private long _decodeFailures;

    public ArtworkImageService(
        DeveloperDiagnostics diagnostics,
        ArtworkPropertyUpdateBatcher artworkUpdates)
    {
        _diagnostics = diagnostics;
        _artworkUpdates = artworkUpdates;
        _scheduler = new PriorityWorkScheduler<BitmapSource?>(DecoderWorkers);
        Current = this;
    }

    internal static ArtworkImageService? Current { get; private set; }

    internal async Task<BitmapSource?> GetAsync(
        string path,
        int decodePixelWidth,
        ArtworkRequestPriority priority,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        Interlocked.Increment(ref _requests);
        var size = Math.Clamp(decodePixelWidth, 32, 1200);
        var key = $"{Path.GetFullPath(path)}|{size}";
        if (TryGet(key, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            if (_diagnostics.Verbose)
                _diagnostics.Mark("artwork", "thumbnail.strong-cache-hit", Data(path, size));
            return cached;
        }
        Interlocked.Increment(ref _cacheMisses);
        if (_failures.TryGetValue(key, out var failedAt) && failedAt >= DateTimeOffset.UtcNow.AddMinutes(-5))
            return null;

        var before = _scheduler.GetMetrics();
        var result = await _scheduler.RunAsync(
            key,
            priority,
            ct => Task.FromResult(Decode(key, path, size, ct)),
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

    private BitmapSource? Decode(string key, string path, int size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _decodes);
        var timer = Stopwatch.StartNew();
        Exception? error = null;
        try
        {
            if (!File.Exists(path))
            {
                Interlocked.Increment(ref _decodeFailures);
                _failures[key] = DateTimeOffset.UtcNow;
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = size;
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
            _failures[key] = DateTimeOffset.UtcNow;
            _diagnostics.Error("artwork", "thumbnail.decode", exception, Data(path, size));
            return null;
        }
        finally
        {
            _diagnostics.RecordDuration("artwork", "thumbnail.decode-off-thread", timer.Elapsed, Data(path, size), error);
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
        return new ArtworkRuntimeMetrics(
            queue.Queued + queue.Active,
            queue.Queued,
            queue.Active,
            cacheEntries,
            cacheBytes,
            Interlocked.Read(ref _requests),
            Interlocked.Read(ref _cacheHits),
            Interlocked.Read(ref _cacheMisses),
            queue.Deduplicated,
            Interlocked.Read(ref _decodes),
            Interlocked.Read(ref _decodeFailures),
            queue.Promoted,
            queue.DroppedBeforeStart);
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

    private static Dictionary<string, object?> Data(string path, int size) => new()
    {
        ["size"] = size,
        ["extension"] = Path.GetExtension(path)
    };

    private sealed record CacheEntry(BitmapSource Value, long Bytes, LinkedListNode<string> Node);
}

internal readonly record struct ArtworkRuntimeMetrics(
    int QueueDepth,
    int Queued,
    int Active,
    int CacheEntries,
    long CacheBytes,
    long Requests,
    long CacheHits,
    long CacheMisses,
    long DeduplicatedRequests,
    long Decodes,
    long DecodeFailures,
    long PromotedRequests,
    long DroppedBeforeDecode)
{
    public double CacheHitRate => CacheHits + CacheMisses == 0
        ? 0
        : CacheHits * 100d / (CacheHits + CacheMisses);
}

public static class AsyncArtwork
{
    private static readonly ConditionalWeakTable<Image, RequestState> States = new();

    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(AsyncArtwork), new PropertyMetadata(null, OnRequestChanged));

    public static readonly DependencyProperty DecodePixelWidthProperty = DependencyProperty.RegisterAttached(
        "DecodePixelWidth", typeof(int), typeof(AsyncArtwork), new PropertyMetadata(256, OnRequestChanged));

    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached(
        "Priority", typeof(ArtworkRequestPriority), typeof(AsyncArtwork),
        new PropertyMetadata(ArtworkRequestPriority.Deferred, OnRequestChanged));

    public static readonly RoutedEvent ArtworkLoadedEvent = EventManager.RegisterRoutedEvent(
        "ArtworkLoaded", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(AsyncArtwork));

    public static string? GetPath(DependencyObject element) => (string?)element.GetValue(PathProperty);
    public static void SetPath(DependencyObject element, string? value) => element.SetValue(PathProperty, value);
    public static int GetDecodePixelWidth(DependencyObject element) => (int)element.GetValue(DecodePixelWidthProperty);
    public static void SetDecodePixelWidth(DependencyObject element, int value) => element.SetValue(DecodePixelWidthProperty, value);
    public static ArtworkRequestPriority GetPriority(DependencyObject element) => (ArtworkRequestPriority)element.GetValue(PriorityProperty);
    public static void SetPriority(DependencyObject element, ArtworkRequestPriority value) => element.SetValue(PriorityProperty, value);
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

        public void OnLoaded(object sender, RoutedEventArgs args) => Restart();
        public void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (args.NewValue is true) Restart();
            else Cancel(clearSource: false);
        }

        public void OnUnloaded(object sender, RoutedEventArgs args)
        {
            Cancel(clearSource: true);
        }

        private void Cancel(bool clearSource)
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            if (clearSource) image.Source = null;
        }

        public void Restart()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            image.Source = null;
            var path = GetPath(image);
            if (!image.IsLoaded || !image.IsVisible || string.IsNullOrWhiteSpace(path) || ArtworkImageService.Current is null) return;
            var source = new CancellationTokenSource();
            _cancellation = source;
            var version = ++_version;
            _ = LoadAsync(
                path,
                Math.Clamp(GetDecodePixelWidth(image), 32, 1200),
                GetPriority(image),
                version,
                source.Token);
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
                var bitmap = await service.GetAsync(path, size, priority, cancellationToken).ConfigureAwait(false);
                if (bitmap is null || cancellationToken.IsCancellationRequested) return;
                service.EnqueuePropertyUpdate(
                    () =>
                    {
                        if (version != _version || !image.IsLoaded || !image.IsVisible) return;
                        image.Source = bitmap;
                        image.RaiseEvent(new RoutedEventArgs(ArtworkLoadedEvent, image));
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) { }
        }
    }
}
