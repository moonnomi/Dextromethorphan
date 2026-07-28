using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.Performance;

internal sealed record PerformanceBenchmarkOptions(
    string OutputPath,
    string RunKind,
    int ScanFileCount,
    bool MeasureWorkloads)
{
    public static PerformanceBenchmarkOptions? Parse(IReadOnlyList<string> args)
    {
        string? output = null;
        var runKind = "warm";
        var scanFileCount = 1_000;
        var workloads = false;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--performance-benchmark":
                    output = RequireValue(args, ref index, "--performance-benchmark");
                    break;
                case "--benchmark-kind":
                    runKind = RequireValue(args, ref index, "--benchmark-kind").ToLowerInvariant();
                    break;
                case "--benchmark-scan-files":
                    scanFileCount = int.Parse(RequireValue(args, ref index, "--benchmark-scan-files"));
                    break;
                case "--benchmark-workloads":
                    workloads = true;
                    break;
            }
        }

        if (output is null) return null;
        if (runKind is not ("cold" or "warm"))
            throw new ArgumentException("--benchmark-kind must be cold or warm.");
        if (scanFileCount is < 100 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(args), "--benchmark-scan-files must be between 100 and 10000.");
        return new PerformanceBenchmarkOptions(Path.GetFullPath(output), runKind, scanFileCount, workloads);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string name)
    {
        if (++index >= args.Count) throw new ArgumentException($"{name} requires a value.");
        return args[index];
    }
}

internal sealed record StartupPerformanceTimestamps(
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset WindowShownAt,
    DateTimeOffset FirstContentRenderedAt,
    DateTimeOffset LibraryReadyAt,
    DateTimeOffset InteractiveAt);

internal sealed class PerformanceBenchmarkReport
{
    public int SchemaVersion { get; init; } = 4;
    public required string RunKind { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required string FixtureRoot { get; init; }
    public required FixtureIdentity Fixture { get; init; }
    public required MachineIdentity Machine { get; init; }
    public required StartupPerformanceMetrics Startup { get; init; }
    public required IReadOnlyList<TabSwitchPerformanceSample> TabSwitches { get; init; }
    public required NavigationHistoryPerformanceMetrics NavigationHistory { get; init; }
    public required HiddenViewReleaseMetrics HiddenViewRelease { get; init; }
    public required PagedSongsPerformanceMetrics PagedSongs { get; init; }
    public required FramePerformanceMetrics AlbumScroll { get; init; }
    public required ResourcePerformanceMetrics Resources { get; init; }
    public required CpuPerformanceMetrics Cpu { get; init; }
    public ScanPerformanceMetrics? Scan { get; init; }
    public ConcurrentWorkloadPerformanceMetrics? ConcurrentWorkload { get; init; }
    public string? WorkloadError { get; init; }
}

internal sealed record FixtureIdentity(string Kind, int Tracks, int Albums, int Artwork, string ContentSha256);
internal sealed record MachineIdentity(string Os, string Runtime, string Architecture, string Processor, int LogicalProcessors, long AvailableMemoryBytes, string Commit);
internal sealed record StartupPerformanceMetrics(double ProcessToWindowShownMs, double ProcessToFirstRenderMs, double ProcessToFirstArtworkMs, double ProcessToLibraryReadyMs, double ProcessToInteractiveMs);
internal sealed record TabSwitchPerformanceSample(string View, string Pass, double LatencyMs);
internal sealed record NavigationHistoryPerformanceMetrics(
    double BackLatencyMs,
    double ForwardLatencyMs,
    bool CollectionReused,
    bool ScrollOffsetRestored,
    bool SelectionRestored,
    bool MaterializedCountRestored,
    double ExpectedVerticalOffset,
    double RestoredVerticalOffset,
    int ExpectedMaterializedCount,
    int RestoredMaterializedCount)
{
    public bool Passed => CollectionReused && ScrollOffsetRestored && SelectionRestored && MaterializedCountRestored;
}
internal sealed record HiddenViewReleaseMetrics(int SourcesBeforeHide, int SourcesAfterHide)
{
    public bool Passed => SourcesBeforeHide > 0 && SourcesAfterHide == 0;
}
internal sealed record PagedSongsPerformanceMetrics(
    int SourceTracks,
    int InitialMaterializedTracks,
    int MaterializedTracksAfterNextPage)
{
    public bool Passed =>
        SourceTracks > 500
        && InitialMaterializedTracks == 500
        && MaterializedTracksAfterNextPage == 1_000;
}
internal sealed record FramePerformanceMetrics(int Samples, double AverageMs, double P50Ms, double P95Ms, double P99Ms, double MaximumMs, int Over16_67Ms, int Over33_33Ms, int Over50Ms, int GalleryCardsLoaded);
internal sealed record ResourcePerformanceMetrics(
    long WorkingSetAfterStartupBytes,
    long WorkingSetAfterNavigationBytes,
    long WorkingSetAfterScrollBytes,
    long PeakWorkingSetBytes,
    long ManagedHeapBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ActiveArtworkSourcesAfterStartup,
    int ActiveArtworkSourcesAfterNavigation,
    int ActiveArtworkSourcesAfterScroll,
    long PeakWorkingSetAfterStartupBytes,
    long PeakWorkingSetAfterNavigationBytes,
    long PeakWorkingSetAfterScrollBytes);
internal sealed record CpuPerformanceMetrics(double IdlePercent, double? PlaybackPercent, string? PlaybackStatus);
internal sealed record ScanPerformanceMetrics(int Files, int Imported, int Failed, double ElapsedMs, double FilesPerSecond);
internal sealed record ConcurrentWorkloadPerformanceMetrics(
    bool PlaybackAvailable,
    bool PlaybackStayedHealthy,
    int PlaybackInterruptions,
    double PlaybackPositionAdvancedMs,
    int ScanFiles,
    int ScanImported,
    int ScanFailed,
    double ScanElapsedMs,
    double NavigationP95Ms,
    double NavigationMaximumMs,
    long MaximumWorkingSetBytes,
    string Status)
{
    public bool Passed =>
        PlaybackAvailable
        && PlaybackStayedHealthy
        && PlaybackInterruptions == 0
        && PlaybackPositionAdvancedMs >= 250
        && ScanImported == ScanFiles
        && ScanFailed == 0
        && NavigationMaximumMs < 100;
}

internal static class PerformanceBenchmarkRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<PerformanceBenchmarkReport> RunAsync(
        MainWindow window,
        PerformanceBenchmarkOptions options,
        StartupPerformanceTimestamps timestamps,
        IAudioEngine audio,
        ISettingsService settings,
        CancellationToken cancellationToken = default)
    {
        var fixtureRoot = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(fixtureRoot))
            throw new InvalidOperationException($"{AppPaths.DataRootEnvironmentVariable} must point at a performance fixture.");
        fixtureRoot = Path.GetFullPath(fixtureRoot);
        var fixture = await ReadFixtureAsync(fixtureRoot, cancellationToken);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var startupWorkingSet = process.WorkingSet64;
        var startupPeakWorkingSet = process.PeakWorkingSet64;
        var startupArtworkSources = window.ArtworkMetrics.ActiveImageSources;

        var tabSwitches = await window.MeasureTabSwitchPerformanceAsync(cancellationToken);
        var navigationHistory = await window.MeasureNavigationHistoryPerformanceAsync(cancellationToken);
        var hiddenViewRelease = await window.MeasureHiddenViewReleaseAsync(cancellationToken);
        var pagedSongs = await window.MeasurePagedSongsPerformanceAsync(cancellationToken);
        process.Refresh();
        var navigationWorkingSet = process.WorkingSet64;
        var navigationPeakWorkingSet = process.PeakWorkingSet64;
        var navigationArtworkSources = window.ArtworkMetrics.ActiveImageSources;

        var scroll = await window.MeasureAlbumScrollPerformanceAsync(cancellationToken);
        process.Refresh();
        var scrollWorkingSet = process.WorkingSet64;
        var scrollPeakWorkingSet = process.PeakWorkingSet64;
        var scrollArtworkSources = window.ArtworkMetrics.ActiveImageSources;
        await window.WaitForBackgroundIdleAsync(cancellationToken);
        var idleCpu = await MeasureCpuAsync(TimeSpan.FromSeconds(2), cancellationToken);

        ScanPerformanceMetrics? scan = null;
        ConcurrentWorkloadPerformanceMetrics? concurrentWorkload = null;
        double? playbackCpu = null;
        string? playbackStatus = options.MeasureWorkloads ? "Not attempted" : "Skipped on warm run";
        string? workloadError = null;
        if (options.MeasureWorkloads)
        {
            try
            {
                scan = await MeasureScanAsync(options, settings, cancellationToken);
                (playbackCpu, playbackStatus) = await MeasurePlaybackAsync(options, audio, cancellationToken);
                concurrentWorkload = await MeasureConcurrentWorkloadAsync(
                    window,
                    options,
                    settings,
                    audio,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                workloadError = exception.GetBaseException().Message;
                playbackStatus ??= "Unavailable";
            }
        }

        process.Refresh();
        var firstArtwork = window.FirstGalleryArtworkRenderedAt ?? timestamps.LibraryReadyAt;
        var report = new PerformanceBenchmarkReport
        {
            RunKind = options.RunKind,
            CapturedAt = DateTimeOffset.UtcNow,
            FixtureRoot = fixtureRoot,
            Fixture = fixture,
            Machine = CaptureMachine(),
            Startup = new StartupPerformanceMetrics(
                Elapsed(timestamps.ProcessStartedAt, timestamps.WindowShownAt),
                Elapsed(timestamps.ProcessStartedAt, timestamps.FirstContentRenderedAt),
                Elapsed(timestamps.ProcessStartedAt, firstArtwork),
                Elapsed(timestamps.ProcessStartedAt, timestamps.LibraryReadyAt),
                Elapsed(timestamps.ProcessStartedAt, timestamps.InteractiveAt)),
            TabSwitches = tabSwitches,
            NavigationHistory = navigationHistory,
            HiddenViewRelease = hiddenViewRelease,
            PagedSongs = pagedSongs,
            AlbumScroll = scroll,
            Resources = new ResourcePerformanceMetrics(
                startupWorkingSet,
                navigationWorkingSet,
                scrollWorkingSet,
                scrollPeakWorkingSet,
                GC.GetTotalMemory(false),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                startupArtworkSources,
                navigationArtworkSources,
                scrollArtworkSources,
                startupPeakWorkingSet,
                navigationPeakWorkingSet,
                scrollPeakWorkingSet),
            Cpu = new CpuPerformanceMetrics(idleCpu, playbackCpu, playbackStatus),
            Scan = scan,
            ConcurrentWorkload = concurrentWorkload,
            WorkloadError = workloadError
        };

        var directory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        return report;
    }

    private static async Task<FixtureIdentity> ReadFixtureAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "fixture.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Performance fixture manifest was not found.", path);
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var value = document.RootElement;
        return new FixtureIdentity(
            Property(value, "FixtureKind").GetString() ?? "unknown",
            Property(value, "TrackCount").GetInt32(),
            Property(value, "AlbumCount").GetInt32(),
            Property(value, "ArtworkCount").GetInt32(),
            Property(value, "ContentSha256").GetString() ?? "");
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        foreach (var property in value.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        throw new InvalidDataException($"Fixture manifest property is missing: {name}");
    }

    private static MachineIdentity CaptureMachine() => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.FrameworkDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown processor",
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        Environment.GetEnvironmentVariable("DEXTROMETHORPHAN_BENCHMARK_COMMIT") ?? "working-tree");

    private static async Task<double> MeasureCpuAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        await Task.Delay(duration, cancellationToken);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        return Math.Round(cpu.TotalMilliseconds / timer.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100, 3);
    }

    private static async Task<ScanPerformanceMetrics> MeasureScanAsync(
        PerformanceBenchmarkOptions options,
        ISettingsService settings,
        CancellationToken cancellationToken)
    {
        var workloadRoot = Path.Combine(Path.GetDirectoryName(options.OutputPath)!, "workload");
        var mediaRoot = Path.Combine(workloadRoot, "scan-media");
        var dataRoot = Path.Combine(workloadRoot, "scan-data");
        RecreateDirectory(workloadRoot);
        Directory.CreateDirectory(mediaRoot);
        var wav = GeneratedWaveWorkload.CreatePcmWave(TimeSpan.FromMilliseconds(40));
        for (var index = 0; index < options.ScanFileCount; index++)
            await File.WriteAllBytesAsync(Path.Combine(mediaRoot, $"synthetic-{index:D5}.wav"), wav, cancellationToken);

        var paths = new AppPaths(dataRoot);
        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);
        var artwork = new ArtworkCache(paths, settings);
        await using var scanner = new LibraryScanner(repository, new TagLibMetadataReader(), artwork);
        ScanProgress? final = null;
        scanner.ProgressChanged += (_, progress) => { if (progress.IsComplete) final = progress; };
        var timer = Stopwatch.StartNew();
        await scanner.ScanAsync([mediaRoot], cancellationToken: cancellationToken);
        timer.Stop();
        var stats = await repository.GetStatsAsync(cancellationToken);
        var failed = final?.Failed ?? Math.Max(0, options.ScanFileCount - (int)stats.TrackCount);
        return new ScanPerformanceMetrics(
            options.ScanFileCount,
            (int)stats.TrackCount,
            failed,
            Math.Round(timer.Elapsed.TotalMilliseconds, 3),
            Math.Round(options.ScanFileCount / timer.Elapsed.TotalSeconds, 2));
    }

    private static async Task<(double? Cpu, string Status)> MeasurePlaybackAsync(
        PerformanceBenchmarkOptions options,
        IAudioEngine audio,
        CancellationToken cancellationToken)
    {
        var workloadRoot = Path.Combine(Path.GetDirectoryName(options.OutputPath)!, "workload");
        Directory.CreateDirectory(workloadRoot);
        var path = Path.Combine(workloadRoot, "playback-silence.wav");
        await File.WriteAllBytesAsync(path, GeneratedWaveWorkload.CreatePcmWave(TimeSpan.FromSeconds(12)), cancellationToken);
        var info = new FileInfo(path);
        var track = new Track
        {
            Path = path,
            Title = "Generated playback benchmark",
            Artist = "Dextromethorphan",
            AlbumArtist = "Dextromethorphan",
            Album = "Performance workload",
            Codec = "WAV",
            Duration = TimeSpan.FromSeconds(12),
            SampleRate = GeneratedWaveWorkload.SampleRate,
            BitsPerSample = 16,
            Channels = 2,
            FileSize = info.Length,
            FileModifiedAt = info.LastWriteTimeUtc
        };
        try
        {
            await audio.LoadAsync(track, cancellationToken);
            await audio.PlayAsync(cancellationToken);
            var cpu = await MeasureCpuAsync(TimeSpan.FromSeconds(3), cancellationToken);
            await audio.StopAsync(cancellationToken);
            return (cpu, "Measured with generated 44.1 kHz 16-bit stereo silence");
        }
        catch (Exception exception)
        {
            try { await audio.StopAsync(CancellationToken.None); } catch { }
            return (null, $"Unavailable: {exception.GetBaseException().Message}");
        }
    }

    private static async Task<ConcurrentWorkloadPerformanceMetrics> MeasureConcurrentWorkloadAsync(
        MainWindow window,
        PerformanceBenchmarkOptions options,
        ISettingsService settings,
        IAudioEngine audio,
        CancellationToken cancellationToken)
    {
        var workloadRoot = Path.Combine(
            Path.GetDirectoryName(options.OutputPath)!,
            "workload",
            "concurrent");
        var mediaRoot = Path.Combine(workloadRoot, "scan-media");
        var dataRoot = Path.Combine(workloadRoot, "scan-data");
        RecreateDirectory(workloadRoot);
        Directory.CreateDirectory(mediaRoot);
        var scanWave = GeneratedWaveWorkload.CreatePcmWave(TimeSpan.FromMilliseconds(40));
        for (var index = 0; index < options.ScanFileCount; index++)
            await File.WriteAllBytesAsync(
                Path.Combine(mediaRoot, $"concurrent-{index:D5}.wav"),
                scanWave,
                cancellationToken);

        var playbackPath = Path.Combine(workloadRoot, "playback-silence.wav");
        await File.WriteAllBytesAsync(
            playbackPath,
            GeneratedWaveWorkload.CreatePcmWave(TimeSpan.FromSeconds(30)),
            cancellationToken);
        var playbackTrack = GeneratedTrack(
            playbackPath,
            "Concurrent scan and navigation benchmark",
            TimeSpan.FromSeconds(30));

        var paths = new AppPaths(dataRoot);
        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);
        var artwork = new ArtworkCache(paths, settings);
        await using var scanner = new LibraryScanner(
            repository,
            new TagLibMetadataReader(),
            artwork);
        ScanProgress? final = null;
        scanner.ProgressChanged += (_, progress) =>
        {
            if (progress.IsComplete) final = progress;
        };

        var playbackInterruptions = 0;
        var playbackStarted = false;
        var playbackHealthy = true;
        string? playbackError = null;
        void OnAudioStateChanged(object? sender, PlaybackSnapshot snapshot)
        {
            if (!playbackStarted) return;
            if (snapshot.State is PlaybackState.Faulted or PlaybackState.Buffering)
            {
                Interlocked.Increment(ref playbackInterruptions);
                playbackHealthy = false;
            }
            if (!string.IsNullOrWhiteSpace(snapshot.Error))
            {
                playbackHealthy = false;
                playbackError = snapshot.Error;
            }
        }

        audio.StateChanged += OnAudioStateChanged;
        try
        {
            await audio.ConfigureOutputAsync(new AudioOutputProfile
            {
                DeviceId = "default",
                Name = "System default",
                Mode = WasapiMode.Shared,
                BufferMilliseconds = 100,
                HardwareVolume = false,
                PreferBitPerfect = false,
                FallbackPolicy = OutputFallbackPolicy.SharedMode
            }, cancellationToken);
            await audio.SetPlaybackOptionsAsync(new AudioPlaybackOptions
            {
                ReplayGainMode = ReplayGainMode.Off,
                TransitionMode = TransitionMode.Gapless,
                PreventClipping = true
            }, cancellationToken);
            await audio.SetVolumeAsync(1, cancellationToken);
            await audio.LoadAsync(playbackTrack, cancellationToken);
            await audio.PlayAsync(cancellationToken);
            await Task.Delay(250, cancellationToken);
            if (audio.Snapshot.State != PlaybackState.Playing)
            {
                var snapshot = audio.Snapshot;
                return UnavailableConcurrentWorkload(
                    options.ScanFileCount,
                    snapshot.Error ?? $"Playback entered {snapshot.State}.");
            }

            playbackStarted = true;
            var initialPosition = audio.Snapshot.Position;
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var maximumWorkingSet = process.WorkingSet64;
            var scanTimer = Stopwatch.StartNew();
            var scanTask = scanner.ScanAsync(
                [mediaRoot],
                cancellationToken: cancellationToken);
            var navigationTask = window.MeasureTabSwitchPerformanceAsync(cancellationToken);

            while (!scanTask.IsCompleted || !navigationTask.IsCompleted)
            {
                await Task.Delay(50, cancellationToken);
                var snapshot = audio.Snapshot;
                if (snapshot.State != PlaybackState.Playing)
                {
                    playbackHealthy = false;
                    Interlocked.Increment(ref playbackInterruptions);
                }
                if (!string.IsNullOrWhiteSpace(snapshot.Error))
                    playbackError = snapshot.Error;
                process.Refresh();
                maximumWorkingSet = Math.Max(maximumWorkingSet, process.WorkingSet64);
            }

            await scanTask;
            scanTimer.Stop();
            var navigation = await navigationTask;
            var finalPosition = audio.Snapshot.Position;
            var stats = await repository.GetStatsAsync(cancellationToken);
            var failed = final?.Failed
                ?? Math.Max(0, options.ScanFileCount - (int)stats.TrackCount);
            var navigationFrames = PerformanceStatistics.Frames(
                navigation.Select(sample => sample.LatencyMs),
                0);
            var positionAdvanced = Math.Max(
                0,
                (finalPosition - initialPosition).TotalMilliseconds);
            var status = playbackHealthy
                ? "Playback advanced continuously while scanning and switching all primary tabs."
                : playbackError ?? "Playback left the Playing state during the concurrent workload.";

            return new ConcurrentWorkloadPerformanceMetrics(
                true,
                playbackHealthy,
                playbackInterruptions,
                Math.Round(positionAdvanced, 3),
                options.ScanFileCount,
                (int)stats.TrackCount,
                failed,
                Math.Round(scanTimer.Elapsed.TotalMilliseconds, 3),
                navigationFrames.P95Ms,
                navigationFrames.MaximumMs,
                maximumWorkingSet,
                status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return UnavailableConcurrentWorkload(
                options.ScanFileCount,
                exception.GetBaseException().Message);
        }
        finally
        {
            playbackStarted = false;
            audio.StateChanged -= OnAudioStateChanged;
            try { await audio.StopAsync(CancellationToken.None); } catch { }
        }
    }

    private static Track GeneratedTrack(
        string path,
        string title,
        TimeSpan duration)
    {
        var info = new FileInfo(path);
        return new Track
        {
            Path = path,
            Title = title,
            Artist = "Dextromethorphan",
            AlbumArtist = "Dextromethorphan",
            Album = "Performance workload",
            Codec = "WAV",
            Duration = duration,
            SampleRate = GeneratedWaveWorkload.SampleRate,
            BitsPerSample = 16,
            Channels = 2,
            FileSize = info.Length,
            FileModifiedAt = info.LastWriteTimeUtc
        };
    }

    private static ConcurrentWorkloadPerformanceMetrics UnavailableConcurrentWorkload(
        int scanFiles,
        string status) => new(
            false,
            false,
            0,
            0,
            scanFiles,
            0,
            scanFiles,
            0,
            0,
            0,
            0,
            $"Unavailable: {status}");

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        Directory.CreateDirectory(path);
    }

    private static double Elapsed(DateTimeOffset start, DateTimeOffset end) =>
        Math.Round(Math.Max(0, (end - start).TotalMilliseconds), 3);
}

internal static class PerformanceStatistics
{
    public static FramePerformanceMetrics Frames(IEnumerable<double> durations, int galleryCardsLoaded)
    {
        var values = durations.Where(double.IsFinite).Where(x => x >= 0).Order().ToArray();
        if (values.Length == 0) return new FramePerformanceMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, galleryCardsLoaded);
        return new FramePerformanceMetrics(
            values.Length,
            Math.Round(values.Average(), 3),
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            Math.Round(values[^1], 3),
            values.Count(x => x > 16.67),
            values.Count(x => x > 33.33),
            values.Count(x => x > 50),
            galleryCardsLoaded);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var index = (int)Math.Ceiling((sorted.Count - 1) * percentile);
        return Math.Round(sorted[Math.Clamp(index, 0, sorted.Count - 1)], 3);
    }
}

internal static class GeneratedWaveWorkload
{
    public const int SampleRate = 44_100;

    public static byte[] CreatePcmWave(TimeSpan duration)
    {
        const short channels = 2;
        const short bitsPerSample = 16;
        const short blockAlign = channels * bitsPerSample / 8;
        var sampleFrames = Math.Max(1, checked((int)Math.Round(duration.TotalSeconds * SampleRate)));
        var dataLength = checked(sampleFrames * blockAlign);
        var result = new byte[44 + dataLength];
        using var stream = new MemoryStream(result, writable: true);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);
        return result;
    }
}
