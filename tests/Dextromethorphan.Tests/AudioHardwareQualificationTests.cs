using Dextromethorphan.Infrastructure.Audio;
using NAudio.CoreAudioApi;
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

        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(
            DataFlow.Render,
            Role.Multimedia);
        var volumeBefore =
            endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        await using var engine = new WasapiAudioEngine();
        var devices = await engine.GetOutputDevicesAsync(
            TestContext.Current.CancellationToken);
        var systemDefault = Assert.Single(
            devices,
            device => device.Id == "default");

        Assert.False(string.IsNullOrWhiteSpace(systemDefault.MixFormat));
        var capabilities =
            await engine.GetDeviceCapabilitiesAsync(
                systemDefault.Id,
                TestContext.Current.CancellationToken);

        Assert.Equal("default", capabilities.DeviceId);
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
                        44_100, 48_000, 88_200, 96_000, 176_400, 192_000
                    });
                Assert.Contains(format.Channels, new[] { 1, 2 });
            });
        var volumeAfter =
            endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        Assert.Equal(volumeBefore, volumeAfter);

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
                            systemDefault.Name,
                            id = "redacted",
                            capabilities.MixFormat,
                            capabilities.SupportedExclusiveFormats,
                            capabilities.SupportsEventDrivenExclusive
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
    }
}
