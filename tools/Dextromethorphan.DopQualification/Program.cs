using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using NAudio.CoreAudioApi;

namespace Dextromethorphan.DopQualification;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        DopOptions options;
        try
        {
            options = DopOptions.Parse(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            DopOptions.PrintUsage();
            return 1;
        }

        if (options.Help)
        {
            DopOptions.PrintUsage();
            return 0;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Physical DoP qualification requires Windows WASAPI.");
            return 1;
        }
        if (options.ListDevices)
        {
            ListEndpoints();
            return 0;
        }
        try
        {
            options.ValidateForPlayback();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        var outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "dextromethorphan-dop-qualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var results = new List<DopPlaybackResult>();
        var faults = new List<string>();
        string deviceName = "Unavailable";
        var resolvedDeviceId = options.DeviceId;
        float? volumeBefore = null;
        float? volumeAfter = null;

        try
        {
            var endpoint = ReadEndpoint(options.DeviceId);
            resolvedDeviceId = endpoint.Id;
            deviceName = endpoint.Name;
            volumeBefore = endpoint.Volume;
            Console.WriteLine($"Qualifying {deviceName} in exclusive DoP mode.");
            foreach (var rate in new[] { 2_822_400, 5_644_800 })
            {
                var fixture = DsdSilenceFixture.Write(
                    fixtureRoot,
                    rate,
                    options.SecondsPerCase);
                results.Add(await RunCaseAsync(
                    fixture,
                    options,
                    resolvedDeviceId,
                    deviceName));
            }
        }
        catch (Exception exception)
        {
            faults.Add(exception.GetBaseException().Message);
        }
        finally
        {
            try
            {
                volumeAfter = ReadEndpoint(resolvedDeviceId).Volume;
            }
            catch (Exception exception)
            {
                faults.Add("Final endpoint-volume read failed: " + exception.Message);
            }
            try
            {
                Directory.Delete(fixtureRoot, true);
            }
            catch
            {
                // Generated fixtures contain no user data and can be removed later.
            }
        }

        var volumeUnchanged = volumeBefore.HasValue
                              && volumeAfter.HasValue
                              && volumeBefore.Value.Equals(volumeAfter.Value);
        var automatedPassed = results.Count == 2
                              && results.All(result => result.Succeeded)
                              && faults.Count == 0
                              && volumeUnchanged;
        var indicationComplete = options.Dsd64Indication == DacIndication.Pass
                                 && options.Dsd128Indication == DacIndication.Pass;
        var metadataComplete = options.HasCompleteHardwareMetadata;
        var report = new DopQualificationReport(
            2,
            DateTimeOffset.UtcNow,
            new DeviceReport(
                deviceName,
                HashDeviceId(resolvedDeviceId),
                options.DacModel,
                options.DriverVersion,
                options.Connection),
            results,
            new EndpointVolumeReport(
                volumeBefore,
                volumeAfter,
                volumeUnchanged),
            new IndicationReport(
                options.Dsd64Indication.ToString(),
                options.Dsd128Indication.ToString()),
            options.OperatorNotes,
            automatedPassed,
            indicationComplete,
            metadataComplete,
            automatedPassed && indicationComplete && metadataComplete,
            faults);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"Report written to {outputPath}");
        Console.WriteLine(report.HardwareQualified
            ? "PHYSICAL DoP QUALIFIED"
            : automatedPassed
                ? "Automated DoP checks passed; DAC indication evidence is still incomplete."
                : "DoP qualification failed; inspect the report.");
        return report.HardwareQualified
            ? 0
            : automatedPassed
                ? 3
                : 2;
    }

    private static async Task<DopPlaybackResult> RunCaseAsync(
        GeneratedDsdFixture fixture,
        DopOptions options,
        string deviceId,
        string deviceName)
    {
        var level = fixture.DsdSampleRate == 2_822_400 ? "DSD64" : "DSD128";
        Console.WriteLine(
            $"{level}: requesting {fixture.DopCarrierSampleRate / 1000d:0.#} kHz / 24-bit DoP carrier.");
        var timer = Stopwatch.StartNew();
        try
        {
            await using var engine = new WasapiAudioEngine();
            await engine.ConfigureOutputAsync(new AudioOutputProfile
            {
                DeviceId = deviceId,
                Name = deviceName,
                Mode = WasapiMode.Exclusive,
                BufferMilliseconds = options.BufferMilliseconds,
                SampleRatePolicy = SampleRatePolicy.MatchSource,
                BitDepthPolicy = BitDepthPolicy.MatchSource,
                ChannelPolicy = ChannelPolicy.RejectNonStereo,
                DsdMode = DsdMode.Dop,
                VolumeControl = VolumeControlMode.Fixed,
                HardwareVolume = false,
                PreferBitPerfect = true,
                FallbackPolicy = OutputFallbackPolicy.Never,
                RecoveryMaximumAttempts = 1
            });
            await engine.SetPlaybackOptionsAsync(new AudioPlaybackOptions
            {
                ReplayGainMode = ReplayGainMode.Off,
                TransitionMode = TransitionMode.Gapless,
                CrossfadeSeconds = 0,
                Speed = 1,
                PitchSemitones = 0,
                PreservePitch = true
            });
            var track = new Track
            {
                Path = fixture.Path,
                Title = $"Generated {level} silence",
                Artist = "Dextromethorphan qualification",
                Album = "Physical DoP qualification",
                Duration = fixture.Duration,
                Codec = "DSF",
                SampleRate = fixture.DsdSampleRate,
                BitsPerSample = 1,
                Channels = DsdSilenceFixture.Channels,
                FileSize = fixture.FileSize,
                FileModifiedAt = File.GetLastWriteTimeUtc(fixture.Path)
            };
            await engine.LoadAsync(track);
            var loaded = engine.Diagnostics
                         ?? throw new InvalidOperationException(
                             "The engine did not publish diagnostics after loading.");
            ValidateDiagnostics(loaded, fixture.DopCarrierSampleRate);

            await engine.SeekAsync(TimeSpan.FromMilliseconds(250));
            var firstSeek = engine.Snapshot.Position;
            await engine.PlayAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            var firstPlayback = engine.Snapshot;
            EnsurePlaying(firstPlayback, firstSeek);
            await engine.PauseAsync();

            var secondTarget = TimeSpan.FromSeconds(
                Math.Min(2, fixture.Duration.TotalSeconds / 2));
            await engine.SeekAsync(secondTarget);
            var secondSeek = engine.Snapshot.Position;
            await engine.PlayAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            var secondPlayback = engine.Snapshot;
            EnsurePlaying(secondPlayback, secondSeek);
            var finalDiagnostics = engine.Diagnostics
                                   ?? loaded;
            ValidateDiagnostics(
                finalDiagnostics,
                fixture.DopCarrierSampleRate);
            await engine.StopAsync();

            return new(
                level,
                fixture.DsdSampleRate,
                fixture.DopCarrierSampleRate,
                true,
                firstSeek.TotalSeconds,
                firstPlayback.Position.TotalSeconds,
                secondSeek.TotalSeconds,
                secondPlayback.Position.TotalSeconds,
                finalDiagnostics,
                timer.Elapsed.TotalMilliseconds,
                null);
        }
        catch (Exception exception)
        {
            return new(
                level,
                fixture.DsdSampleRate,
                fixture.DopCarrierSampleRate,
                false,
                0,
                0,
                0,
                0,
                null,
                timer.Elapsed.TotalMilliseconds,
                $"0x{exception.HResult:X8}: {exception.GetBaseException().Message}");
        }
    }

    private static void ValidateDiagnostics(
        AudioDiagnostics diagnostics,
        int carrierRate)
    {
        if (diagnostics.EffectiveMode != WasapiMode.Exclusive
            || diagnostics.FallbackActive
            || !diagnostics.IsBitPerfect
            || !diagnostics.IsEventDriven
            || diagnostics.PipelineMode != AudioPipelineMode.Direct
            || diagnostics.OutputFormat?.SampleRate != carrierRate
            || diagnostics.OutputFormat.BitsPerSample != 24
            || diagnostics.OutputFormat.Channels != 2)
            throw new InvalidOperationException(
                "The engine did not establish the requested direct, exclusive, bit-perfect DoP carrier.");
    }

    private static void EnsurePlaying(
        PlaybackSnapshot snapshot,
        TimeSpan startingPosition)
    {
        if (snapshot.State == PlaybackState.Faulted)
            throw new InvalidOperationException(snapshot.Error ?? "Playback faulted.");
        if (snapshot.Position <= startingPosition + TimeSpan.FromMilliseconds(250))
            throw new InvalidOperationException(
                "The DoP playback position did not advance after seeking.");
    }

    private static ResolvedEndpoint ReadEndpoint(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDevice(deviceId);
        return new(
            endpoint.ID,
            endpoint.FriendlyName,
            endpoint.AudioEndpointVolume.MasterVolumeLevelScalar);
    }

    private static void ListEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = enumerator.EnumerateAudioEndPoints(
            DataFlow.Render,
            DeviceState.Active);
        Console.WriteLine("Active render endpoints (copy the exact ID for --device-id):");
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
            {
                Console.WriteLine($"{endpoint.FriendlyName}\n  {endpoint.ID}");
            }
        }
    }

    private static string HashDeviceId(string deviceId) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)));
}

internal sealed record DopOptions(
    string DeviceId,
    string OutputPath,
    int BufferMilliseconds,
    int SecondsPerCase,
    DacIndication Dsd64Indication,
    DacIndication Dsd128Indication,
    string DacModel,
    string DriverVersion,
    string Connection,
    string OperatorNotes,
    bool ConfirmedDacConnected,
    bool ListDevices,
    bool Help)
{
    private static readonly HashSet<string> FlagNames = new(
        ["--help", "-h", "--confirm-compatible-dac", "--list-devices"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ValueNames = new(
        [
            "--device-id",
            "--buffer",
            "--seconds",
            "--dsd64-indication",
            "--dsd128-indication",
            "--dac-model",
            "--driver-version",
            "--connection",
            "--operator-notes",
            "--output"
        ],
        StringComparer.OrdinalIgnoreCase);

    public bool HasCompleteHardwareMetadata =>
        !string.IsNullOrWhiteSpace(DacModel)
        && !string.IsNullOrWhiteSpace(DriverVersion)
        && !string.IsNullOrWhiteSpace(Connection);

    public static DopOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (FlagNames.Contains(args[index]))
            {
                flags.Add(args[index]);
                continue;
            }
            if (!args[index].StartsWith("--", StringComparison.Ordinal)
                || !ValueNames.Contains(args[index])
                || index + 1 >= args.Length)
                throw new ArgumentException($"Invalid argument: {args[index]}");
            values[args[index]] = args[++index];
        }

        var buffer = ReadInt(values, "--buffer", 100, 2, 1_000);
        var seconds = ReadInt(values, "--seconds", 4, 2, 30);
        var output = values.GetValueOrDefault(
            "--output",
            Path.Combine(
                "artifacts",
                "audio-qualification",
                $"dop-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json"));
        return new(
            values.GetValueOrDefault("--device-id", "default"),
            output,
            buffer,
            seconds,
            ReadIndication(values, "--dsd64-indication"),
            ReadIndication(values, "--dsd128-indication"),
            values.GetValueOrDefault("--dac-model", string.Empty),
            values.GetValueOrDefault("--driver-version", string.Empty),
            values.GetValueOrDefault("--connection", string.Empty),
            values.GetValueOrDefault("--operator-notes", string.Empty),
            flags.Contains("--confirm-compatible-dac"),
            flags.Contains("--list-devices"),
            flags.Contains("--help") || flags.Contains("-h"));
    }

    public void ValidateForPlayback()
    {
        if (!ConfirmedDacConnected)
            throw new InvalidOperationException(
                "Refusing to send DoP without --confirm-compatible-dac. " +
                "DoP sent to a normal PCM endpoint may produce noise.");
        if (string.IsNullOrWhiteSpace(DeviceId)
            || DeviceId.Equals("default", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Physical DoP qualification requires an exact --device-id; " +
                "the mutable Windows default endpoint is not safe.");
        if (!HasCompleteHardwareMetadata)
            throw new InvalidOperationException(
                "--dac-model, --driver-version, and --connection are required " +
                "for traceable physical evidence.");
    }

    public static void PrintUsage() => Console.WriteLine(
        "Dextromethorphan.DopQualification [options]\n\n" +
        "  --confirm-compatible-dac       Required safety acknowledgement\n" +
        "  --list-devices                 List active endpoints without playing audio\n" +
        "  --device-id ID                 Exact DAC endpoint ID (required; no default)\n" +
        "  --dac-model TEXT               DAC manufacturer/model (required)\n" +
        "  --driver-version TEXT          Installed driver version (required)\n" +
        "  --connection TEXT              USB/connection description (required)\n" +
        "  --operator-notes TEXT          Optional observation notes\n" +
        "  --buffer N                     Exclusive WASAPI buffer ms (default 100)\n" +
        "  --seconds N                    Generated silence per case (default 4)\n" +
        "  --dsd64-indication RESULT      unknown|pass|fail\n" +
        "  --dsd128-indication RESULT     unknown|pass|fail\n" +
        "  --output PATH                  JSON report path\n" +
        "  --help                         Show this help");

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string name,
        int fallback,
        int minimum,
        int maximum) => !values.TryGetValue(name, out var raw)
        ? fallback
        : int.TryParse(raw, out var result)
          && result >= minimum
          && result <= maximum
            ? result
            : throw new ArgumentException(
                $"{name} must be between {minimum} and {maximum}.");

    private static DacIndication ReadIndication(
        IReadOnlyDictionary<string, string> values,
        string name) => !values.TryGetValue(name, out var raw)
        ? DacIndication.Unknown
        : Enum.TryParse<DacIndication>(raw, true, out var result)
            ? result
            : throw new ArgumentException(
                $"{name} must be unknown, pass, or fail.");
}

internal enum DacIndication { Unknown, Pass, Fail }

internal sealed record DopQualificationReport(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    DeviceReport Device,
    IReadOnlyList<DopPlaybackResult> Cases,
    EndpointVolumeReport EndpointVolume,
    IndicationReport DacIndication,
    string OperatorNotes,
    bool AutomatedPassed,
    bool PhysicalIndicationComplete,
    bool HardwareMetadataComplete,
    bool HardwareQualified,
    IReadOnlyList<string> Faults);

internal sealed record DopPlaybackResult(
    string DsdLevel,
    int DsdSampleRate,
    int DopCarrierSampleRate,
    bool Succeeded,
    double FirstSeekSeconds,
    double FirstPlaybackSeconds,
    double SecondSeekSeconds,
    double SecondPlaybackSeconds,
    AudioDiagnostics? Diagnostics,
    double ElapsedMilliseconds,
    string? Error);

internal sealed record DeviceReport(
    string Name,
    string IdSha256,
    string Model,
    string DriverVersion,
    string Connection);
internal sealed record EndpointVolumeReport(
    float? Before,
    float? After,
    bool Unchanged);
internal sealed record IndicationReport(string Dsd64, string Dsd128);
internal sealed record ResolvedEndpoint(string Id, string Name, float Volume);
