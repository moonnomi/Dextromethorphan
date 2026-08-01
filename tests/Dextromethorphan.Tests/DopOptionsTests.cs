using Dextromethorphan.DopQualification;

namespace Dextromethorphan.Tests;

public sealed class DopOptionsTests
{
    [Fact]
    public void PlaybackRequiresExactEndpointAndTraceableHardwareMetadata()
    {
        var options = DopOptions.Parse(
        [
            "--confirm-compatible-dac",
            "--device-id", "endpoint-123",
            "--dac-model", "Example DAC",
            "--driver-version", "1.2.3",
            "--connection", "USB"
        ]);

        options.ValidateForPlayback();

        Assert.Equal("endpoint-123", options.DeviceId);
        Assert.True(options.HasCompleteHardwareMetadata);
    }

    [Fact]
    public void MutableDefaultEndpointIsRejectedEvenAfterConfirmation()
    {
        var options = DopOptions.Parse(
        [
            "--confirm-compatible-dac",
            "--dac-model", "Example DAC",
            "--driver-version", "1.2.3",
            "--connection", "USB"
        ]);

        var exception = Assert.Throws<InvalidOperationException>(
            options.ValidateForPlayback);

        Assert.Contains("exact --device-id", exception.Message);
    }

    [Fact]
    public void MissingSafetyAcknowledgementIsRejected()
    {
        var options = DopOptions.Parse(
        [
            "--device-id", "endpoint-123",
            "--dac-model", "Example DAC",
            "--driver-version", "1.2.3",
            "--connection", "USB"
        ]);

        Assert.Throws<InvalidOperationException>(options.ValidateForPlayback);
    }

    [Fact]
    public void UnknownArgumentsAreRejectedInsteadOfSilentlyIgnored()
    {
        Assert.Throws<ArgumentException>(() => DopOptions.Parse(
            ["--device-id", "endpoint-123", "--surprise", "value"]));
    }

    [Fact]
    public void ListModeDoesNotImplyPlaybackConfirmation()
    {
        var options = DopOptions.Parse(["--list-devices"]);

        Assert.True(options.ListDevices);
        Assert.False(options.ConfirmedDacConnected);
    }
}
