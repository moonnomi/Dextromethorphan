using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Dextromethorphan.App.UI;
using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.App.Diagnostics;

public sealed class PerformanceOverlayViewModel : ObservableObject, IDisposable
{
    private const double StallThresholdMs = 50;
    private readonly ArtworkImageService _artwork;
    private readonly ArtworkPropertyUpdateBatcher _artworkUpdates;
    private readonly DeveloperDiagnostics _diagnostics;
    private readonly Queue<double> _recentFrames = new();
    private Window? _owner;
    private DispatcherTimer? _sampleTimer;
    private bool _isVisible;
    private long _lastFrameTimestamp;
    private double _lastFrameMs;
    private double _worstFrameMs;
    private int _uiStallCount;
    private DateTimeOffset _lastStallAt;
    private string _healthTitle = "WAITING FOR FRAMES";
    private string _healthDetail = "Open the overlay while reproducing a performance issue.";
    private string _frameTime = "—";
    private string _frameRate = "—";
    private string _uiStalls = "0";
    private string _artworkQueue = "0 active";
    private string _artworkCache = "0 items";
    private string _workingSet = "—";
    private string _garbageCollections = "—";
    private string _diagnosticsStatus = "Overlay only · file trace disabled";

    public PerformanceOverlayViewModel(
        ArtworkImageService artwork,
        ArtworkPropertyUpdateBatcher artworkUpdates,
        DeveloperDiagnostics diagnostics)
    {
        _artwork = artwork;
        _artworkUpdates = artworkUpdates;
        _diagnostics = diagnostics;
        ToggleCommand = new RelayCommand(_ => IsVisible = !IsVisible);
        ResetCommand = new RelayCommand(_ => Reset());
    }

    public RelayCommand ToggleCommand { get; }
    public RelayCommand ResetCommand { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!Set(ref _isVisible, value)) return;
            if (value) Start();
            else Stop();
        }
    }

    public string HealthTitle { get => _healthTitle; private set => Set(ref _healthTitle, value); }
    public string HealthDetail { get => _healthDetail; private set => Set(ref _healthDetail, value); }
    public string FrameTime { get => _frameTime; private set => Set(ref _frameTime, value); }
    public string FrameRate { get => _frameRate; private set => Set(ref _frameRate, value); }
    public string UiStalls { get => _uiStalls; private set => Set(ref _uiStalls, value); }
    public string ArtworkQueue { get => _artworkQueue; private set => Set(ref _artworkQueue, value); }
    public string ArtworkCache { get => _artworkCache; private set => Set(ref _artworkCache, value); }
    public string WorkingSet { get => _workingSet; private set => Set(ref _workingSet, value); }
    public string GarbageCollections { get => _garbageCollections; private set => Set(ref _garbageCollections, value); }
    public string DiagnosticsStatus { get => _diagnosticsStatus; private set => Set(ref _diagnosticsStatus, value); }

    internal void Attach(Window owner)
    {
        _owner = owner;
        if (IsVisible) Start();
    }

    internal static bool IsRequested(IReadOnlyList<string> args)
    {
        if (args.Any(value => value.Equals("--performance-overlay", StringComparison.OrdinalIgnoreCase)))
            return true;
        return Environment.GetEnvironmentVariable("DEXTROMETHORPHAN_PERFORMANCE_OVERLAY")?.Trim().ToLowerInvariant()
            is "1" or "true" or "yes" or "on";
    }

    private void Start()
    {
        if (_owner is null || _sampleTimer is not null) return;
        Reset();
        _sampleTimer = new DispatcherTimer(DispatcherPriority.Background, _owner.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _sampleTimer.Tick += SampleTimerOnTick;
        CompositionTarget.Rendering += CompositionTargetOnRendering;
        _sampleTimer.Start();
        UpdateSnapshot();
        if (_diagnostics.Enabled)
            _diagnostics.Mark("performance", "overlay.opened");
    }

    private void Stop()
    {
        if (_sampleTimer is null) return;
        CompositionTarget.Rendering -= CompositionTargetOnRendering;
        _sampleTimer.Stop();
        _sampleTimer.Tick -= SampleTimerOnTick;
        _sampleTimer = null;
        _lastFrameTimestamp = 0;
        if (_diagnostics.Enabled)
            _diagnostics.Mark("performance", "overlay.closed");
    }

    private void Reset()
    {
        _recentFrames.Clear();
        _lastFrameTimestamp = 0;
        _lastFrameMs = 0;
        _worstFrameMs = 0;
        _uiStallCount = 0;
        _lastStallAt = default;
        UpdateSnapshot();
        if (_diagnostics.Enabled && IsVisible)
            _diagnostics.Mark("performance", "overlay.reset");
    }

    private void CompositionTargetOnRendering(object? sender, EventArgs args)
    {
        if (_owner is null || !_owner.IsActive || _owner.WindowState == WindowState.Minimized)
        {
            _lastFrameTimestamp = 0;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp != 0)
        {
            var milliseconds = Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalMilliseconds;
            if (milliseconds is > 0 and < 1_000)
            {
                _lastFrameMs = milliseconds;
                _worstFrameMs = Math.Max(_worstFrameMs, milliseconds);
                _recentFrames.Enqueue(milliseconds);
                while (_recentFrames.Count > 120) _recentFrames.Dequeue();
                if (milliseconds >= StallThresholdMs)
                {
                    _uiStallCount++;
                    _lastStallAt = DateTimeOffset.UtcNow;
                    _diagnostics.RecordDuration("render", "ui-thread-stall", TimeSpan.FromMilliseconds(milliseconds),
                        new Dictionary<string, object?> { ["view"] = (_owner as MainWindow)?.ViewModel.CurrentView });
                }
            }
        }
        _lastFrameTimestamp = now;
    }

    private void SampleTimerOnTick(object? sender, EventArgs args) => UpdateSnapshot();

    private void UpdateSnapshot()
    {
        var averageFrame = _recentFrames.Count == 0 ? 0 : _recentFrames.Average();
        FrameTime = _lastFrameMs <= 0 ? "Waiting…" : $"{_lastFrameMs:0.0} ms current · {averageFrame:0.0} ms avg";
        FrameRate = averageFrame <= 0 ? "—" : $"{Math.Min(999, 1_000 / averageFrame):0} FPS";
        UiStalls = $"{_uiStallCount:N0} over {StallThresholdMs:0} ms · worst {_worstFrameMs:0.0} ms";

        var artwork = _artwork.GetRuntimeMetrics();
        var updates = _artworkUpdates.GetMetrics();
        ArtworkQueue = $"{artwork.Active:N0} decoding · {artwork.Queued:N0} queued · {updates.Pending:N0} UI pending · {updates.Batches:N0} batches";
        ArtworkCache = $"{artwork.ActiveImageSources:N0} visible sources · {artwork.CacheEntries:N0} memory · {FormatMegabytes(artwork.CacheBytes)} · {artwork.CacheHitRate:0}% RAM hit\n" +
            $"{artwork.PersistentVariantsGenerated:N0} variants · {artwork.PersistentHitRate:0}% disk hit · " +
            $"{artwork.PersistentSourceDecodes:N0} original decodes · {artwork.PersistentFailures:N0} rejected";

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        WorkingSet = $"{FormatMegabytes(process.WorkingSet64)} working · {FormatMegabytes(GC.GetTotalMemory(false))} managed";
        GarbageCollections = $"Gen 0  {GC.CollectionCount(0):N0}   ·   Gen 1  {GC.CollectionCount(1):N0}   ·   Gen 2  {GC.CollectionCount(2):N0}";
        DiagnosticsStatus = _diagnostics.Enabled
            ? $"Recording locally · {Path.GetFileName(_diagnostics.EventLogPath)}"
            : "Overlay only · start a diagnostic session to record events";

        var recentStall = _lastStallAt != default && DateTimeOffset.UtcNow - _lastStallAt < TimeSpan.FromSeconds(2);
        if (recentStall)
        {
            HealthTitle = "UI STALL DETECTED";
            HealthDetail = $"The latest frame took {_lastFrameMs:0.0} ms. Check image queue and GC activity.";
        }
        else if (averageFrame > 25)
        {
            HealthTitle = "UI UNDER LOAD";
            HealthDetail = $"Recent frames average {averageFrame:0.0} ms; 60 Hz needs 16.7 ms.";
        }
        else
        {
            HealthTitle = "SMOOTH";
            HealthDetail = _uiStallCount == 0
                ? "No UI-thread stalls since this overlay was opened."
                : $"{_uiStallCount:N0} stall(s) recorded; worst frame {_worstFrameMs:0.0} ms.";
        }
    }

    private static string FormatMegabytes(long bytes) => $"{bytes / 1_048_576d:0.0} MB";

    public void Dispose()
    {
        Stop();
        _owner = null;
    }
}
