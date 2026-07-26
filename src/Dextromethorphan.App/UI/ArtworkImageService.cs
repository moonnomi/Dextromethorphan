using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Dextromethorphan.App.Diagnostics;

namespace Dextromethorphan.App.UI;

public sealed class ArtworkImageService
{
    private const long DefaultBudgetBytes = 96L * 1024 * 1024;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = [];
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeveloperDiagnostics _diagnostics;
    private long _cacheBytes;

    public ArtworkImageService(DeveloperDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
        Current = this;
    }

    internal static ArtworkImageService? Current { get; private set; }

    internal async Task<BitmapSource?> GetAsync(string path, int decodePixelWidth, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var size = Math.Clamp(decodePixelWidth, 32, 1200);
        var key = $"{Path.GetFullPath(path)}|{size}";
        if (TryGet(key, out var cached))
        {
            if (_diagnostics.Verbose)
                _diagnostics.Mark("artwork", "thumbnail.strong-cache-hit", Data(path, size));
            return cached;
        }
        if (_failures.TryGetValue(key, out var failedAt) && failedAt >= DateTimeOffset.UtcNow.AddMinutes(-5))
            return null;

        var candidate = new Lazy<Task<BitmapSource?>>(
            () => DecodeAsync(key, path, size),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var pending = _inflight.GetOrAdd(key, candidate);
        if (!ReferenceEquals(candidate, pending) && _diagnostics.Enabled)
            _diagnostics.Mark("artwork", "thumbnail.request-deduplicated", Data(path, size));
        try
        {
            return await pending.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (pending.IsValueCreated && pending.Value.IsCompleted)
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<BitmapSource?>>>(key, pending));
        }
    }

    private Task<BitmapSource?> DecodeAsync(string key, string path, int size) =>
        Task.Run<BitmapSource?>(() =>
        {
            var timer = Stopwatch.StartNew();
            Exception? error = null;
            try
            {
                if (!File.Exists(path))
                {
                    _failures[key] = DateTimeOffset.UtcNow;
                    return null;
                }
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
                _failures[key] = DateTimeOffset.UtcNow;
                _diagnostics.Error("artwork", "thumbnail.decode", exception, Data(path, size));
                return null;
            }
            finally
            {
                _diagnostics.RecordDuration("artwork", "thumbnail.decode-off-thread", timer.Elapsed, Data(path, size), error);
            }
        });

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

public static class AsyncArtwork
{
    private static readonly ConditionalWeakTable<Image, RequestState> States = new();

    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(AsyncArtwork), new PropertyMetadata(null, OnRequestChanged));

    public static readonly DependencyProperty DecodePixelWidthProperty = DependencyProperty.RegisterAttached(
        "DecodePixelWidth", typeof(int), typeof(AsyncArtwork), new PropertyMetadata(256, OnRequestChanged));

    public static readonly RoutedEvent ArtworkLoadedEvent = EventManager.RegisterRoutedEvent(
        "ArtworkLoaded", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(AsyncArtwork));

    public static string? GetPath(DependencyObject element) => (string?)element.GetValue(PathProperty);
    public static void SetPath(DependencyObject element, string? value) => element.SetValue(PathProperty, value);
    public static int GetDecodePixelWidth(DependencyObject element) => (int)element.GetValue(DecodePixelWidthProperty);
    public static void SetDecodePixelWidth(DependencyObject element, int value) => element.SetValue(DecodePixelWidthProperty, value);
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
        return state;
    }

    private sealed class RequestState(Image image)
    {
        private CancellationTokenSource? _cancellation;
        private int _version;

        public void OnLoaded(object sender, RoutedEventArgs args) => Restart();

        public void OnUnloaded(object sender, RoutedEventArgs args)
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            image.Source = null;
        }

        public void Restart()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            image.Source = null;
            var path = GetPath(image);
            if (!image.IsLoaded || string.IsNullOrWhiteSpace(path) || ArtworkImageService.Current is null) return;
            var source = new CancellationTokenSource();
            _cancellation = source;
            var version = ++_version;
            _ = LoadAsync(path, Math.Clamp(GetDecodePixelWidth(image), 32, 1200), version, source.Token);
        }

        private async Task LoadAsync(string path, int size, int version, CancellationToken cancellationToken)
        {
            try
            {
                var bitmap = await ArtworkImageService.Current!.GetAsync(path, size, cancellationToken);
                if (bitmap is null || cancellationToken.IsCancellationRequested || version != _version || !image.IsLoaded) return;
                image.Source = bitmap;
                image.RaiseEvent(new RoutedEventArgs(ArtworkLoadedEvent, image));
            }
            catch (OperationCanceledException) { }
        }
    }
}
