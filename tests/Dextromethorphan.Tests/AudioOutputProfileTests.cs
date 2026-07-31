using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using NAudio.Wave;

namespace Dextromethorphan.Tests;

public sealed class AudioOutputProfileTests
{
    [Fact]
    public void DraftRoundTripsEveryOutputDecision()
    {
        var source = new AudioOutputProfile
        {
            DeviceId = "dac",
            Name = "DAC",
            Mode = WasapiMode.Exclusive,
            BufferMilliseconds = 37,
            SampleRatePolicy = SampleRatePolicy.Fixed,
            PreferredSampleRate = 96_000,
            BitDepthPolicy = BitDepthPolicy.Fixed,
            PreferredBitDepth = 24,
            ChannelPolicy = ChannelPolicy.RejectNonStereo,
            FallbackPolicy = OutputFallbackPolicy.Never,
            VolumeControl = VolumeControlMode.Fixed,
            DsdMode = DsdMode.Dop,
            PreferBitPerfect = true,
            CrossfadeSeconds = 2.5,
            RecoveryMaximumAttempts = 6,
            RecoveryInitialDelayMilliseconds = 350
        };
        var draft = new AudioOutputProfileDraft();

        draft.Load(source);
        var result = draft.ToProfile();

        Assert.Equal(source.DeviceId, result.DeviceId);
        Assert.Equal(source.Mode, result.Mode);
        Assert.Equal(source.BufferMilliseconds, result.BufferMilliseconds);
        Assert.Equal(source.SampleRatePolicy, result.SampleRatePolicy);
        Assert.Equal(source.PreferredSampleRate, result.PreferredSampleRate);
        Assert.Equal(source.BitDepthPolicy, result.BitDepthPolicy);
        Assert.Equal(source.PreferredBitDepth, result.PreferredBitDepth);
        Assert.Equal(source.ChannelPolicy, result.ChannelPolicy);
        Assert.Equal(source.FallbackPolicy, result.FallbackPolicy);
        Assert.Equal(source.VolumeControl, result.VolumeControl);
        Assert.Equal(source.DsdMode, result.DsdMode);
        Assert.Equal(source.CrossfadeSeconds, result.CrossfadeSeconds);
        Assert.Equal(
            source.RecoveryMaximumAttempts,
            result.RecoveryMaximumAttempts);
        Assert.Equal(
            source.RecoveryInitialDelayMilliseconds,
            result.RecoveryInitialDelayMilliseconds);
    }

    [Theory]
    [InlineData("Bluetooth headphones", 180)]
    [InlineData("NVIDIA High Definition Audio (HDMI)", 100)]
    public void WirelessAndDisplayOutputsUseResilientSharedDefaults(
        string name,
        int expectedBuffer)
    {
        var profile = AudioOutputProfileDefaults.For(
            new AudioDeviceInfo("device", name, false, "Active"));

        Assert.Equal(WasapiMode.Shared, profile.Mode);
        Assert.Equal(expectedBuffer, profile.BufferMilliseconds);
        Assert.Equal(
            SampleRatePolicy.EndpointMixFormat,
            profile.SampleRatePolicy);
        Assert.Equal(
            OutputFallbackPolicy.SystemDefaultShared,
            profile.FallbackPolicy);
        Assert.False(profile.PreferBitPerfect);
    }

    [Theory]
    [InlineData(1, 200)]
    [InlineData(2, 500)]
    [InlineData(3, 1000)]
    [InlineData(4, 2000)]
    [InlineData(99, 5000)]
    public void RecoveryBackoffIsBounded(
        int attempt,
        int expectedMilliseconds)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            AudioRecoveryPolicy.DelayForAttempt(attempt));
    }

    [Fact]
    public void DeviceInvalidationIsRecoverableButCodecFailureIsNot()
    {
        var invalidated = new Exception
        {
            HResult = unchecked((int)0x88890004)
        };

        Assert.True(AudioRecoveryPolicy.IsRecoverable(invalidated));
        Assert.False(
            AudioRecoveryPolicy.IsRecoverable(
                new NotSupportedException("codec")));
    }

    [Fact]
    public void MultichannelPolicyProducesBoundedStereoFrames()
    {
        var input = new ArraySampleProvider(
            [1, -1, 0.5f, 0.25f, 0.75f, -0.75f],
            48_000,
            6);
        var downmix = new MultichannelToStereoSampleProvider(input);
        var output = new float[2];

        Assert.Equal(2, downmix.Read(output, 0, output.Length));
        Assert.All(output, value => Assert.InRange(value, -1f, 1f));
        Assert.Equal(2, downmix.WaveFormat.Channels);
    }

    [Theory]
    [InlineData(16, 4)]
    [InlineData(24, 6)]
    [InlineData(32, 8)]
    public void FixedPcmProviderEmitsRequestedBitDepth(
        int bits,
        int expectedBytes)
    {
        var input = new ArraySampleProvider(
            [1f, -1f],
            48_000,
            2);
        var provider = new PcmSampleWaveProvider(input, bits);
        var bytes = new byte[expectedBytes];

        Assert.Equal(expectedBytes, provider.Read(bytes, 0, bytes.Length));
        Assert.Equal(bits, provider.WaveFormat.BitsPerSample);
        Assert.Equal(WaveFormatEncoding.Pcm, provider.WaveFormat.Encoding);
    }

    private sealed class ArraySampleProvider(
        float[] samples,
        int sampleRate,
        int channels) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(
                sampleRate,
                channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }
}
