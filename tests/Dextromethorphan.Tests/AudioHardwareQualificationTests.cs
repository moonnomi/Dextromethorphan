using Dextromethorphan.Infrastructure.Audio;
using Dextromethorphan.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Text.Json;

namespace Dextromethorphan.Tests;

public sealed class AudioHardwareQualificationTests
{
    [Fact]
    [Trait("Category", "AudioHardware")]
    public async Task ActiveWindowsEndpointReportsMixAndExclusiveMatrix()
    {
        if (!OperatingSystem.IsWindows()
            || Environment.GetEnvironmentVariable(
                "DEXTROMETHORPHAN_RUN_AUDIO_HARDWARE_TESTS") != "1")
            return;

        await using var engine = new WasapiAudioEngine();
        var devices = await engine.GetOutputDevicesAsync(
            TestContext.Current.CancellationToken);
        var requestedDeviceId = Environment.GetEnvironmentVariable(
            "DEXTROMETHORPHAN_AUDIO_DEVICE_ID");
        if (string.IsNullOrWhiteSpace(requestedDeviceId))
            requestedDeviceId = "default";
        var selectedDevice = Assert.Single(
            devices,
            device => device.Id.Equals(
                requestedDeviceId,
                StringComparison.Ordinal));

        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = selectedDevice.Id == "default"
            ? enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia)
            : enumerator.GetDevice(selectedDevice.Id);
        var volumeBefore =
            endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;

        Assert.False(string.IsNullOrWhiteSpace(selectedDevice.MixFormat));
        var capabilities =
            await engine.GetDeviceCapabilitiesAsync(
                selectedDevice.Id,
                TestContext.Current.CancellationToken);

        Assert.Equal(selectedDevice.Id, capabilities.DeviceId);
        Assert.True(capabilities.MixFormat.SampleRate > 0);
        Assert.True(capabilities.MixFormat.Channels > 0);
        Assert.All(
            capabilities.SupportedExclusiveFormats,
            format =>
            {
                Assert.Contains(
                    format.SampleRate,
                    new[]
                    {
                        44_100, 48_000, 88_200, 96_000, 176_400, 192_000,
                        352_800, 384_000, 705_600, 768_000
                    });
                Assert.Contains(format.Channels, new[] { 1, 2 });
            });

        var sharedMatrix = new List<HardwarePlaybackResult>();
        foreach (var channels in new[] { 1, 2 })
        foreach (var rate in new[]
                 {
                     44_100, 48_000, 88_200, 96_000, 176_400, 192_000
                 })
        foreach (var bits in new[] { 16, 24, 32 })
            sharedMatrix.Add(await TryPlaySilenceAsync(
                endpoint,
                AudioClientShareMode.Shared,
                new WaveFormat(rate, bits, channels),
                20,
                TestContext.Current.CancellationToken));

        var exclusiveMatrix = new List<HardwarePlaybackResult>();
        foreach (var format in capabilities.SupportedExclusiveFormats)
            exclusiveMatrix.Add(await TryPlaySilenceAsync(
                endpoint,
                AudioClientShareMode.Exclusive,
                ToWaveFormat(format),
                20,
                TestContext.Current.CancellationToken));

        var mixFormat = endpoint.AudioClient.MixFormat;
        var bufferBoundaries = new List<HardwarePlaybackResult>();
        foreach (var buffer in new[] { 2, 10, 100 })
            bufferBoundaries.Add(await TryPlaySilenceAsync(
                endpoint,
                AudioClientShareMode.Shared,
                mixFormat,
                buffer,
                TestContext.Current.CancellationToken));

        var volumeAfter =
            endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;

        var reportPath = Environment.GetEnvironmentVariable(
            "DEXTROMETHORPHAN_AUDIO_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        capturedAt = DateTimeOffset.UtcNow,
                        device = new
                        {
                            selectedDevice.Name,
                            selectedDevice.IsDefault,
                            id = "redacted",
                            capabilities.MixFormat,
                            capabilities.SupportedExclusiveFormats,
                            capabilities.SupportsEventDrivenExclusive
                        },
                        playback = new
                        {
                            sharedMatrix,
                            exclusiveMatrix,
                            bufferBoundaries
                        },
                        volumeUnchanged =
                            volumeBefore.Equals(volumeAfter)
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }),
                TestContext.Current.CancellationToken);
        }

        Assert.All(
            sharedMatrix,
            result => Assert.True(result.Succeeded, result.Error));
        Assert.All(
            exclusiveMatrix,
            result => Assert.True(result.Succeeded, result.Error));
        Assert.All(
            bufferBoundaries,
            result => Assert.True(result.Succeeded, result.Error));
        Assert.Equal(volumeBefore, volumeAfter);
    }

    private static WaveFormat ToWaveFormat(AudioFormatInfo format) =>
        format.Encoding.Equals(
            WaveFormatEncoding.IeeeFloat.ToString(),
            StringComparison.OrdinalIgnoreCase)
            ? WaveFormat.CreateIeeeFloatWaveFormat(
                format.SampleRate,
                format.Channels)
            : new WaveFormat(
                format.SampleRate,
                format.BitsPerSample,
                format.Channels);

    private static async Task<HardwarePlaybackResult> TryPlaySilenceAsync(
        MMDevice endpoint,
        AudioClientShareMode mode,
        WaveFormat format,
        int bufferMilliseconds,
        CancellationToken cancellationToken)
    {
        var provider = new FiniteSilenceWaveProvider(
            format,
            Math.Max(
                format.SampleRate / 20,
                format.SampleRate * bufferMilliseconds / 500));
        try
        {
            using var output = new WasapiOut(
                endpoint,
                mode,
                true,
                bufferMilliseconds);
            var stopped = new TaskCompletionSource<StoppedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, args) =>
                stopped.TrySetResult(args);
            output.Init(provider);
            output.Play();
            var result = await stopped.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (result.Exception is not null)
                throw result.Exception;
            return new(
                mode.ToString(),
                format.SampleRate,
                format.BitsPerSample,
                format.Channels,
                format.Encoding.ToString(),
                bufferMilliseconds,
                true,
                provider.Callbacks,
                null);
        }
        catch (Exception exception)
        {
            return new(
                mode.ToString(),
                format.SampleRate,
                format.BitsPerSample,
                format.Channels,
                format.Encoding.ToString(),
                bufferMilliseconds,
                false,
                provider.Callbacks,
                $"0x{exception.HResult:X8}: " +
                exception.GetBaseException().Message);
        }
    }

    private sealed class FiniteSilenceWaveProvider(
        WaveFormat format,
        long frames) : IWaveProvider
    {
        private long _bytesRemaining = frames * format.BlockAlign;
        public WaveFormat WaveFormat { get; } = format;
        public int Callbacks { get; private set; }

        public int Read(byte[] buffer, int offset, int count)
        {
            Callbacks++;
            var read = (int)Math.Min(count, _bytesRemaining);
            read -= read % WaveFormat.BlockAlign;
            if (read <= 0) return 0;
            Array.Clear(buffer, offset, read);
            _bytesRemaining -= read;
            return read;
        }
    }

    private sealed record HardwarePlaybackResult(
        string Mode,
        int SampleRate,
        int BitsPerSample,
        int Channels,
        string Encoding,
        int BufferMilliseconds,
        bool Succeeded,
        int Callbacks,
        string? Error);
}
