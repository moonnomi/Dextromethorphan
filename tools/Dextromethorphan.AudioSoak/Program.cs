using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using NAudio.CoreAudioApi;

namespace Dextromethorphan.AudioSoak;

internal static class Program
{
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
        float? volumeBefore = null;
        float? volumeAfter = null;
        long initialWorkingSet = 0;
        long peakWorkingSet = 0;
        long finalWorkingSet = 0;
        AudioDiagnostics? finalDiagnostics = null;
        string? fatalError = null;

        try
        {
            (deviceName, volumeBefore) = ReadEndpoint(options.DeviceId);
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
                    DeviceId = options.DeviceId,
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

            Console.WriteLine($"Audio soak started on {deviceName}.");
            Console.WriteLine($"Duration: {options.Duration}; report: {outputPath}");
            while (stopwatch.Elapsed < options.Duration)
            {
                cancellation.Token.ThrowIfCancellationRequested();

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

                if (stopwatch.Elapsed >= nextSampleAt)
                {
                    process.Refresh();
                    peakWorkingSet = Math.Max(
                        peakWorkingSet,
                        process.WorkingSet64);
                    var snapshot = engine.Snapshot;
                    samples.Add(new SoakSample(
                        stopwatch.Elapsed.TotalSeconds,
                        snapshot.State.ToString(),
                        snapshot.Position.TotalSeconds,
                        process.WorkingSet64,
                        GC.GetTotalMemory(false),
                        snapshot.Diagnostics?.Underruns ?? 0,
                        snapshot.Diagnostics?.RecoveryAttempts ?? 0,
                        snapshot.Diagnostics?.LastCallbackMilliseconds ?? 0,
                        snapshot.Diagnostics?.MaximumCallbackMilliseconds ?? 0));
                    nextSampleAt = stopwatch.Elapsed
                                   + options.SampleInterval;
                    Console.WriteLine(
                        $"{stopwatch.Elapsed:hh\\:mm\\:ss}  " +
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
            peakWorkingSet = Math.Max(peakWorkingSet, finalWorkingSet);
            try
            {
                (_, volumeAfter) = ReadEndpoint(options.DeviceId);
            }
            catch (Exception exception)
            {
                faults.Enqueue("Final endpoint-volume read failed: " + exception.Message);
            }

            var volumeUnchanged = volumeBefore.HasValue
                                  && volumeAfter.HasValue
                                  && volumeBefore.Value.Equals(volumeAfter.Value);
            var memoryGrowth = finalWorkingSet - initialWorkingSet;
            var cpuTime = process.TotalProcessorTime - initialCpu;
            var cpuPercent = stopwatch.Elapsed.TotalSeconds <= 0
                ? 0
                : cpuTime.TotalSeconds
                  / stopwatch.Elapsed.TotalSeconds
                  / Environment.ProcessorCount
                  * 100;
            var qualified = completed
                            && !cancelled
                            && faults.IsEmpty
                            && playbackEndedCount == 0
                            && (finalDiagnostics?.Underruns ?? 0) == 0
                            && (finalDiagnostics?.RecoveryAttempts ?? 0) == 0
                            && volumeUnchanged
                            && memoryGrowth <= 128L * 1_048_576;
            var report = new SoakReport(
                1,
                startedAt,
                DateTimeOffset.UtcNow,
                options.Duration.TotalSeconds,
                stopwatch.Elapsed.TotalSeconds,
                completed,
                cancelled,
                qualified,
                new DeviceReport(deviceName, "redacted"),
                new ConfigurationReport(
                    "Shared WASAPI",
                    options.BufferMilliseconds,
                    options.TrackSeconds,
                    options.CrossfadeSeconds,
                    "Generated PCM silence (44.1/48 kHz, stereo, 16-bit)",
                    "Fixed; endpoint writes disabled"),
                transitionCount,
                playbackEndedCount,
                faults.Distinct().ToArray(),
                finalDiagnostics,
                new MemoryReport(
                    initialWorkingSet,
                    peakWorkingSet,
                    finalWorkingSet,
                    memoryGrowth),
                new CpuReport(cpuTime.TotalSeconds, cpuPercent),
                new VolumeReport(volumeBefore, volumeAfter, volumeUnchanged),
                samples);
            try
            {
                await File.WriteAllTextAsync(
                    outputPath,
                    JsonSerializer.Serialize(report, JsonOptions));
                Console.WriteLine($"Report written to {outputPath}");
                Console.WriteLine(qualified
                    ? "QUALIFIED"
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
            Environment.ExitCode = qualified ? 0 : 2;
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

    private static (string Name, float Volume) ReadEndpoint(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = deviceId.Equals(
            "default",
            StringComparison.OrdinalIgnoreCase)
            ? enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        return (
            endpoint.FriendlyName,
            endpoint.AudioEndpointVolume.MasterVolumeLevelScalar);
    }
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
    public static SoakOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var help = args.Any(argument => argument is "--help" or "-h");
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--help" or "-h") continue;
            if (!args[index].StartsWith("--", StringComparison.Ordinal)
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
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    double RequestedDurationSeconds,
    double ActualDurationSeconds,
    bool Completed,
    bool Cancelled,
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

internal sealed record DeviceReport(string Name, string Id);
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
    long GrowthBytes);
internal sealed record CpuReport(double TotalProcessorSeconds, double AveragePercent);
internal sealed record VolumeReport(float? Before, float? After, bool Unchanged);
internal sealed record SoakSample(
    double ElapsedSeconds,
    string State,
    double TrackPositionSeconds,
    long WorkingSetBytes,
    long ManagedMemoryBytes,
    long DeadlineMisses,
    int RecoveryAttempts,
    double LastCallbackMilliseconds,
    double MaximumCallbackMilliseconds);
