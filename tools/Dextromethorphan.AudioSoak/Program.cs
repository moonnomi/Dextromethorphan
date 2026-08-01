using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using NAudio.CoreAudioApi;

namespace Dextromethorphan.AudioSoak;

internal static class Program
{
    private static readonly TimeSpan MilestoneQualificationDuration =
        TimeSpan.FromHours(8);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The audio soak runner requires Windows WASAPI.");
            return 1;
        }

        SoakOptions options;
        try
        {
            options = SoakOptions.Parse(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            SoakOptions.PrintUsage();
            return 1;
        }

        if (options.Help)
        {
            SoakOptions.PrintUsage();
            return 0;
        }

        var outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "dextromethorphan-audio-soak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = new Stopwatch();
        var process = Process.GetCurrentProcess();
        var initialCpu = process.TotalProcessorTime;
        var faults = new ConcurrentQueue<string>();
        var transitionSignals = new ConcurrentQueue<int>();
        var samples = new List<SoakSample>();
        var transitionCount = 0;
        var playbackEndedCount = 0;
        var cancelled = false;
        var completed = false;
        var deviceName = "Unavailable";
        var resolvedDeviceId = options.DeviceId;
        float? volumeBefore = null;
        float? volumeAfter = null;
        var endpointVolumeEverChanged = false;
        long initialWorkingSet = 0;
        long peakWorkingSet = 0;
        long finalWorkingSet = 0;
        var playbackClock = new SoakPlaybackClock(
            TimeSpan.FromSeconds(1));
        AudioDiagnostics? finalDiagnostics = null;
        string? fatalError = null;

        SoakReport CreateReport(
            string reportState,
            bool reportCompleted,
            bool reportCancelled,
            bool runPassed,
            AudioDiagnostics? diagnostics)
        {
            process.Refresh();
            finalWorkingSet = process.WorkingSet64;
            peakWorkingSet = Math.Max(
                peakWorkingSet,
                Math.Max(finalWorkingSet, process.PeakWorkingSet64));
            var volumeUnchanged = volumeBefore.HasValue
                                  && volumeAfter.HasValue
                                  && volumeBefore.Value.Equals(volumeAfter.Value)
                                  && !endpointVolumeEverChanged;
            var memoryGrowth = finalWorkingSet - initialWorkingSet;
            var peakMemoryGrowth = peakWorkingSet - initialWorkingSet;
            var cpuTime = process.TotalProcessorTime - initialCpu;
            var cpuPercent = stopwatch.Elapsed.TotalSeconds <= 0
                ? 0
                : cpuTime.TotalSeconds
                  / stopwatch.Elapsed.TotalSeconds
                  / Environment.ProcessorCount
                  * 100;
            var milestoneQualified = SoakQualificationPolicy.IsQualified(
                options.Duration,
                playbackClock.Playing,
                runPassed,
                MilestoneQualificationDuration);
            return new SoakReport(
                3,
                reportState,
                Environment.ProcessId,
                startedAt,
                DateTimeOffset.UtcNow,
                MilestoneQualificationDuration.TotalSeconds,
                options.Duration.TotalSeconds,
                stopwatch.Elapsed.TotalSeconds,
                playbackClock.Playing.TotalSeconds,
                playbackClock.UnobservedGap.TotalSeconds,
                playbackClock.NonPlaying.TotalSeconds,
                reportCompleted,
                reportCancelled,
                runPassed,
                milestoneQualified,
                new DeviceReport(deviceName, HashDeviceId(resolvedDeviceId)),
                new ConfigurationReport(
                    "Shared WASAPI",
                    options.BufferMilliseconds,
                    options.TrackSeconds,
                    options.CrossfadeSeconds,
                    "Generated PCM silence (44.1/48 kHz, stereo, 16-bit)",
                    "Fixed; endpoint writes disabled"),
                Volatile.Read(ref transitionCount),
                Volatile.Read(ref playbackEndedCount),
                faults.Distinct().ToArray(),
                diagnostics,
                new MemoryReport(
                    initialWorkingSet,
                    peakWorkingSet,
                    finalWorkingSet,
                    memoryGrowth,
                    peakMemoryGrowth),
                new CpuReport(cpuTime.TotalSeconds, cpuPercent),
                new VolumeReport(
                    volumeBefore,
                    volumeAfter,
                    volumeUnchanged,
                    endpointVolumeEverChanged),
                samples.ToArray());
        }

        try
        {
            var endpoint = ReadEndpoint(options.DeviceId);
            resolvedDeviceId = endpoint.Id;
            deviceName = endpoint.Name;
            volumeBefore = endpoint.Volume;
            volumeAfter = endpoint.Volume;
            var fixturePaths = new[]
            {
                Path.Combine(fixtureRoot, "silence-44100.wav"),
                Path.Combine(fixtureRoot, "silence-48000.wav")
            };
            WritePcmSilence(
                fixturePaths[0],
                44_100,
                options.TrackSeconds);
            WritePcmSilence(
                fixturePaths[1],
                48_000,
                options.TrackSeconds);
            var tracks = new[]
            {
                CreateTrack(fixturePaths[0], 44_100, options.TrackSeconds),
                CreateTrack(fixturePaths[1], 48_000, options.TrackSeconds)
            };

            await using var engine = new WasapiAudioEngine();
            engine.TrackTransitioned += (_, _) =>
            {
                Interlocked.Increment(ref transitionCount);
                transitionSignals.Enqueue(1);
            };
            engine.PlaybackEnded += (_, _) =>
                Interlocked.Increment(ref playbackEndedCount);
            engine.StateChanged += (_, snapshot) =>
            {
                if (snapshot.State == PlaybackState.Faulted)
                    faults.Enqueue(snapshot.Error ?? "Unknown audio engine fault");
            };

            await engine.ConfigureOutputAsync(
                new AudioOutputProfile
                {
                    DeviceId = resolvedDeviceId,
                    Name = deviceName,
                    Mode = WasapiMode.Shared,
                    BufferMilliseconds = options.BufferMilliseconds,
                    SampleRatePolicy = SampleRatePolicy.EndpointMixFormat,
                    BitDepthPolicy = BitDepthPolicy.MatchSource,
                    ChannelPolicy = ChannelPolicy.DownmixToStereo,
                    DsdMode = DsdMode.Disabled,
                    VolumeControl = VolumeControlMode.Fixed,
                    HardwareVolume = false,
                    PreferBitPerfect = false,
                    FallbackPolicy = OutputFallbackPolicy.SystemDefaultShared
                },
                cancellation.Token);
            await engine.SetPlaybackOptionsAsync(
                new AudioPlaybackOptions
                {
                    ReplayGainMode = ReplayGainMode.Off,
                    PreventClipping = true,
                    TransitionMode = options.CrossfadeSeconds > 0
                        ? TransitionMode.Crossfade
                        : TransitionMode.Gapless,
                    CrossfadeSeconds = options.CrossfadeSeconds,
                    Speed = 1,
                    PreservePitch = true
                },
                cancellation.Token);
            await engine.LoadAsync(tracks[0], cancellation.Token);
            await engine.QueueNextAsync(tracks[1], cancellation.Token);

            process.Refresh();
            initialWorkingSet = process.WorkingSet64;
            peakWorkingSet = initialWorkingSet;
            var nextTrackIndex = 0;
            var nextSampleAt = TimeSpan.Zero;
            startedAt = DateTimeOffset.UtcNow;
            initialCpu = process.TotalProcessorTime;
            stopwatch.Restart();
            await engine.PlayAsync(cancellation.Token);
            var previousState = engine.Snapshot.State;
            var previousObservation = Stopwatch.GetTimestamp();

            Console.WriteLine($"Audio soak started on {deviceName}.");
            Console.WriteLine(
                $"Observed playing time: {options.Duration}; report: {outputPath}");
            while (playbackClock.Playing < options.Duration)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var observedAt = Stopwatch.GetTimestamp();
                playbackClock.Observe(
                    previousState,
                    Stopwatch.GetElapsedTime(
                        previousObservation,
                        observedAt));
                previousObservation = observedAt;

                while (transitionSignals.TryDequeue(out _))
                {
                    await engine.QueueNextAsync(
                        tracks[nextTrackIndex],
                        cancellation.Token);
                    nextTrackIndex = (nextTrackIndex + 1) % tracks.Length;
                }

                if (Volatile.Read(ref playbackEndedCount) > 0)
                    throw new InvalidOperationException(
                        "Playback ended before the requested soak duration.");
                if (!faults.IsEmpty)
                    throw new InvalidOperationException(
                        "The audio engine entered the faulted state.");
                if (playbackClock.NonPlaying > TimeSpan.FromMinutes(5))
                    throw new InvalidOperationException(
                        "Playback did not remain active for five observed minutes.");

                var snapshot = engine.Snapshot;
                previousState = snapshot.State;

                if (stopwatch.Elapsed >= nextSampleAt)
                {
                    process.Refresh();
                    peakWorkingSet = Math.Max(
                        peakWorkingSet,
                        Math.Max(
                            process.WorkingSet64,
                            process.PeakWorkingSet64));
                    samples.Add(new SoakSample(
                        stopwatch.Elapsed.TotalSeconds,
                        playbackClock.Playing.TotalSeconds,
                        snapshot.State.ToString(),
                        snapshot.Position.TotalSeconds,
                        process.WorkingSet64,
                        GC.GetTotalMemory(false),
                        snapshot.Diagnostics?.Underruns ?? 0,
                        snapshot.Diagnostics?.RecoveryAttempts ?? 0,
                        snapshot.Diagnostics?.LastCallbackMilliseconds ?? 0,
                        snapshot.Diagnostics?.MaximumCallbackMilliseconds ?? 0));
                    try
                    {
                        volumeAfter = ReadEndpoint(resolvedDeviceId).Volume;
                        if (volumeBefore.HasValue
                            && !volumeBefore.Value.Equals(volumeAfter.Value))
                            endpointVolumeEverChanged = true;
                    }
                    catch (Exception exception)
                    {
                        faults.Enqueue(
                            "Endpoint-volume checkpoint failed: " +
                            exception.Message);
                        throw;
                    }
                    await WriteReportAsync(
                        outputPath,
                        CreateReport(
                            "Running",
                            false,
                            false,
                            false,
                            snapshot.Diagnostics));
                    nextSampleAt = stopwatch.Elapsed
                                   + options.SampleInterval;
                    Console.WriteLine(
                        $"wall={stopwatch.Elapsed:hh\\:mm\\:ss}  " +
                        $"playing={playbackClock.Playing:hh\\:mm\\:ss}  " +
                        $"transitions={Volatile.Read(ref transitionCount)}  " +
                        $"deadline-misses={snapshot.Diagnostics?.Underruns ?? 0}  " +
                        $"working-set={process.WorkingSet64 / 1_048_576d:0.0} MiB");
                }

                await Task.Delay(25, cancellation.Token);
            }

            finalDiagnostics = engine.Diagnostics;
            await engine.StopAsync(CancellationToken.None);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            fatalError = exception.GetBaseException().Message;
            faults.Enqueue(fatalError);
        }
        finally
        {
            stopwatch.Stop();
            process.Refresh();
            finalWorkingSet = process.WorkingSet64;
            peakWorkingSet = Math.Max(
                peakWorkingSet,
                Math.Max(finalWorkingSet, process.PeakWorkingSet64));
            try
            {
                volumeAfter = ReadEndpoint(resolvedDeviceId).Volume;
                if (volumeBefore.HasValue
                    && !volumeBefore.Value.Equals(volumeAfter.Value))
                    endpointVolumeEverChanged = true;
            }
            catch (Exception exception)
            {
                faults.Enqueue("Final endpoint-volume read failed: " + exception.Message);
            }

            var volumeUnchanged = volumeBefore.HasValue
                                  && volumeAfter.HasValue
                                  && volumeBefore.Value.Equals(volumeAfter.Value)
                                  && !endpointVolumeEverChanged;
            var peakMemoryGrowth = peakWorkingSet - initialWorkingSet;
            var runPassed = completed
                            && !cancelled
                            && faults.IsEmpty
                            && Volatile.Read(ref playbackEndedCount) == 0
                            && (finalDiagnostics?.Underruns ?? 0) == 0
                            && (finalDiagnostics?.RecoveryAttempts ?? 0) == 0
                            && volumeUnchanged
                            && peakMemoryGrowth <= 128L * 1_048_576;
            var reportState = completed
                ? "Completed"
                : cancelled
                    ? "Cancelled"
                    : "Failed";
            var report = CreateReport(
                reportState,
                completed,
                cancelled,
                runPassed,
                finalDiagnostics);
            try
            {
                await WriteReportAsync(outputPath, report);
                Console.WriteLine($"Report written to {outputPath}");
                Console.WriteLine(report.Qualified
                    ? "EIGHT-HOUR MILESTONE GATE QUALIFIED"
                    : report.RunPassed
                        ? "RUN PASSED; requested duration is below the eight-hour milestone gate."
                        : "NOT QUALIFIED; inspect the report before changing HW-004.");
            }
            finally
            {
                try
                {
                    Directory.Delete(fixtureRoot, true);
                }
                catch
                {
                    // The report remains valid if Windows briefly retains a fixture handle.
                }
            }

            if (fatalError is not null)
                Console.Error.WriteLine(fatalError);
            Environment.ExitCode = report.RunPassed ? 0 : 2;
        }

        return Environment.ExitCode;
    }

    private static Track CreateTrack(
        string path,
        int sampleRate,
        int trackSeconds) => new()
    {
        Path = path,
        Title = $"Generated {sampleRate / 1000d:0.#} kHz silence",
        Artist = "Dextromethorphan qualification",
        Album = "Generated audio soak",
        Duration = TimeSpan.FromSeconds(trackSeconds),
        Codec = "WAV",
        SampleRate = sampleRate,
        BitsPerSample = 16,
        Channels = 2,
        FileSize = new FileInfo(path).Length,
        FileModifiedAt = File.GetLastWriteTimeUtc(path)
    };

    private static void WritePcmSilence(
        string path,
        int sampleRate,
        int seconds)
    {
        const short channels = 2;
        const short bitsPerSample = 16;
        const short blockAlign = channels * bitsPerSample / 8;
        var byteRate = sampleRate * blockAlign;
        var dataLength = checked(byteRate * seconds);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);
        stream.SetLength(stream.Position + dataLength);
    }

    private static async Task WriteReportAsync(
        string outputPath,
        SoakReport report)
    {
        var temporaryPath = outputPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(report, JsonOptions));
        File.Move(temporaryPath, outputPath, true);
    }

    private static ResolvedEndpoint ReadEndpoint(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = deviceId.Equals(
            "default",
            StringComparison.OrdinalIgnoreCase)
            ? enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
            Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        return new(
            endpoint.ID,
            endpoint.FriendlyName,
            endpoint.AudioEndpointVolume.MasterVolumeLevelScalar);
    }

    private static string HashDeviceId(string deviceId) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)));
}

internal sealed record SoakOptions(
    TimeSpan Duration,
    int TrackSeconds,
    double CrossfadeSeconds,
    int BufferMilliseconds,
    TimeSpan SampleInterval,
    string DeviceId,
    string OutputPath,
    bool Help)
{
    private static readonly HashSet<string> ValueNames = new(
        [
            "--duration",
            "--track-seconds",
            "--crossfade",
            "--buffer",
            "--sample-interval",
            "--device-id",
            "--output"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static SoakOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var help = args.Any(argument => argument is "--help" or "-h");
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--help" or "-h") continue;
            if (!args[index].StartsWith("--", StringComparison.Ordinal)
                || !ValueNames.Contains(args[index])
                || index + 1 >= args.Length)
                throw new ArgumentException($"Invalid argument: {args[index]}");
            values[args[index]] = args[++index];
        }

        var duration = ReadTimeSpan(values, "--duration", TimeSpan.FromHours(8));
        var trackSeconds = ReadInt(values, "--track-seconds", 8, 2, 600);
        var crossfade = ReadDouble(values, "--crossfade", 0.5, 0, 10);
        if (crossfade >= trackSeconds)
            throw new ArgumentException("--crossfade must be shorter than --track-seconds.");
        var buffer = ReadInt(values, "--buffer", 100, 2, 1_000);
        var sampleInterval = ReadTimeSpan(
            values,
            "--sample-interval",
            TimeSpan.FromSeconds(30));
        if (duration <= TimeSpan.Zero || sampleInterval <= TimeSpan.Zero)
            throw new ArgumentException("Durations must be greater than zero.");
        var output = values.GetValueOrDefault(
            "--output",
            Path.Combine(
                "artifacts",
                "audio-soak",
                $"audio-soak-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json"));
        return new(
            duration,
            trackSeconds,
            crossfade,
            buffer,
            sampleInterval,
            values.GetValueOrDefault("--device-id", "default"),
            output,
            help);
    }

    public static void PrintUsage() => Console.WriteLine(
        "Dextromethorphan.AudioSoak [options]\n\n" +
        "  --duration HH:MM:SS       Run length (default 08:00:00)\n" +
        "  --track-seconds N         Generated fixture length (default 8)\n" +
        "  --crossfade N             Crossfade seconds (default 0.5)\n" +
        "  --buffer N                WASAPI buffer milliseconds (default 100)\n" +
        "  --sample-interval HH:MM:SS Report sample interval (default 00:00:30)\n" +
        "  --device-id ID            Render endpoint ID (default system endpoint)\n" +
        "  --output PATH             JSON report path\n" +
        "  --help                    Show this help");

    private static TimeSpan ReadTimeSpan(
        IReadOnlyDictionary<string, string> values,
        string name,
        TimeSpan fallback) => !values.TryGetValue(name, out var raw)
        ? fallback
        : TimeSpan.TryParse(raw, out var result)
            ? result
            : throw new ArgumentException($"{name} must use HH:MM:SS format.");

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string name,
        int fallback,
        int minimum,
        int maximum) => !values.TryGetValue(name, out var raw)
        ? fallback
        : int.TryParse(raw, out var result) && result >= minimum && result <= maximum
            ? result
            : throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");

    private static double ReadDouble(
        IReadOnlyDictionary<string, string> values,
        string name,
        double fallback,
        double minimum,
        double maximum) => !values.TryGetValue(name, out var raw)
        ? fallback
        : double.TryParse(raw, out var result) && result >= minimum && result <= maximum
            ? result
            : throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
}

internal sealed record SoakReport(
    int SchemaVersion,
    string ReportState,
    int RunnerProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    double MinimumQualificationDurationSeconds,
    double RequestedDurationSeconds,
    double ActualDurationSeconds,
    double ObservedPlayingSeconds,
    double UnobservedGapSeconds,
    double NonPlayingSeconds,
    bool Completed,
    bool Cancelled,
    bool RunPassed,
    bool Qualified,
    DeviceReport Device,
    ConfigurationReport Configuration,
    int Transitions,
    int PrematurePlaybackEnds,
    IReadOnlyList<string> Faults,
    AudioDiagnostics? FinalDiagnostics,
    MemoryReport Memory,
    CpuReport Cpu,
    VolumeReport EndpointVolume,
    IReadOnlyList<SoakSample> Samples);

internal sealed record DeviceReport(string Name, string IdSha256);
internal sealed record ConfigurationReport(
    string OutputMode,
    int BufferMilliseconds,
    int TrackSeconds,
    double CrossfadeSeconds,
    string Fixtures,
    string VolumeControl);
internal sealed record MemoryReport(
    long InitialWorkingSetBytes,
    long PeakWorkingSetBytes,
    long FinalWorkingSetBytes,
    long GrowthBytes,
    long PeakGrowthBytes);
internal sealed record CpuReport(double TotalProcessorSeconds, double AveragePercent);
internal sealed record VolumeReport(
    float? Before,
    float? After,
    bool Unchanged,
    bool ObservedChanged);
internal sealed record SoakSample(
    double ElapsedSeconds,
    double ObservedPlayingSeconds,
    string State,
    double TrackPositionSeconds,
    long WorkingSetBytes,
    long ManagedMemoryBytes,
    long DeadlineMisses,
    int RecoveryAttempts,
    double LastCallbackMilliseconds,
    double MaximumCallbackMilliseconds);
internal sealed record ResolvedEndpoint(string Id, string Name, float Volume);

internal sealed class SoakPlaybackClock(TimeSpan maximumObservedInterval)
{
    public TimeSpan Playing { get; private set; }
    public TimeSpan UnobservedGap { get; private set; }
    public TimeSpan NonPlaying { get; private set; }

    public void Observe(PlaybackState precedingState, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) return;
        if (interval > maximumObservedInterval)
        {
            UnobservedGap += interval;
            return;
        }

        if (precedingState == PlaybackState.Playing)
            Playing += interval;
        else
            NonPlaying += interval;
    }
}

internal static class SoakQualificationPolicy
{
    internal static bool IsQualified(
        TimeSpan requested,
        TimeSpan observedPlaying,
        bool runPassed,
        TimeSpan? minimumDuration = null)
    {
        var minimum = minimumDuration ?? TimeSpan.FromHours(8);
        return runPassed
               && requested >= minimum
               && observedPlaying >= minimum;
    }
}
