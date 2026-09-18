using Dextromethorphan.Infrastructure.Audio;
using NAudio.Wave;
using Dextromethorphan.Infrastructure.Audio.Dsp;

namespace Dextromethorphan.Tests;

public sealed class AudioVisualizationTapTests
{
    [Fact]
    public void TrackFadeOutDoesNotDuckTheIncomingCrossfadeAtBoundary()
    {
        using var transition = new TransitionSampleProvider(new ConstantSamples(.4f), 8, 1);
        transition.QueueNext(new ConstantSamples(.2f), 8);
        var fade = new FadeEnvelopeSampleProvider(transition,
            () => (TimeSpan.FromSeconds(1.99), TimeSpan.FromSeconds(2)),
            () => transition.Measurement.Overlapping)
        { FadeOutSeconds = 1 };
        var buffer = new float[4];
        fade.Read(buffer, 0, 4);
        fade.Read(buffer, 0, 4);
        Assert.True(transition.Measurement.Overlapping);
        Assert.Equal(.4f, buffer[0], 6);
        Assert.Equal(.2f, buffer[^1], 6);
        Assert.All(buffer, sample => Assert.True(sample >= .2f));
    }

    [Fact]
    public void OutputProbeMeasuresWeightedMixAfterFinalGainWithoutChangingBytes()
    {
        using var transition = new TransitionSampleProvider(new ConstantSamples(.4f), 8, 1);
        transition.QueueNext(new ConstantSamples(.2f), 8);
        var gain = new GainLimiterSampleProvider(transition) { Gain = .5 };
        var tap = new AudioVisualizationTapWaveProvider(gain.ToWaveProvider());
        var buffer = new byte[16];
        Assert.Equal(16, tap.Read(buffer, 0, 16));
        Assert.False(transition.Measurement.Overlapping);
        Assert.Equal(16, tap.Read(buffer, 0, 16));
        var samples = Enumerable.Range(0, 4).Select(i => BitConverter.ToSingle(buffer, i * 4)).ToArray();
        Assert.True(transition.Measurement.Overlapping);
        Assert.Equal(4, transition.Measurement.MixedFrames);
        Assert.True(transition.Measurement.OutgoingRms > 0);
        Assert.True(transition.Measurement.IncomingRms > 0);
        Assert.Equal(8, tap.Measurement.Frames);
        Assert.Equal(Math.Sqrt(samples.Average(x => (double)x * x)), tap.Measurement.Rms, 8);
        Assert.Equal(samples.Max(x => Math.Abs((double)x)), tap.Measurement.Peak, 8);
        Assert.Equal(.2, samples[0], 6);
        Assert.Equal(.1, samples[^1], 6);
    }

    [Fact]
    public void OutputProbeDoesNotCancelOppositeStereoChannels()
    {
        var bytes = new byte[8];
        BitConverter.GetBytes(.5f).CopyTo(bytes, 0);
        BitConverter.GetBytes(-.5f).CopyTo(bytes, 4);
        var tap = new AudioVisualizationTapWaveProvider(new MemoryWaveProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), bytes));
        var buffer = new byte[12];
        Assert.Equal(8, tap.Read(buffer, 4, 8));
        Assert.Equal(bytes, buffer.Skip(4));
        Assert.Equal(.5, tap.Measurement.Rms);
        Assert.Equal(.5, tap.Measurement.Peak);
        Assert.Equal(1, tap.Measurement.Frames);
    }

    private sealed class ConstantSamples(float value) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(4, 1);
        public int Read(float[] buffer, int offset, int count) { Array.Fill(buffer, value, offset, count); return count; }
    }

    [Fact]
    public void Read_ReturnsExactSourceBytes_WhenVisualizationIsEnabled()
    {
        var format = new WaveFormat(48000, 16, 2);
        var sourceBytes = CreateSine(format, 4096, 880);
        var source = new MemoryWaveProvider(format, sourceBytes);
        var tap = new AudioVisualizationTapWaveProvider(source)
        {
            Enabled = true
        };
        var output = new byte[sourceBytes.Length];

        var read = tap.Read(output, 0, output.Length);

        Assert.Equal(sourceBytes.Length, read);
        Assert.Equal(sourceBytes, output);
    }

    [Fact]
    public void Snapshot_ReportsFrequencyEnergy_AfterEnoughAudio()
    {
        var format = new WaveFormat(48000, 16, 2);
        var sourceBytes = CreateSine(format, 4096, 1000);
        var tap = new AudioVisualizationTapWaveProvider(
            new MemoryWaveProvider(format, sourceBytes))
        {
            Enabled = true
        };
        tap.Read(new byte[sourceBytes.Length], 0, sourceBytes.Length);

        var snapshot = tap.Snapshot(28);

        Assert.True(snapshot.IsActive);
        Assert.Equal(28, snapshot.Bands.Count);
        Assert.Contains(snapshot.Bands, value => value > .2);
        Assert.InRange(snapshot.Peak, .49, .51);
    }

    [Fact]
    public void DisabledTap_DoesNotRetainVisualizationData()
    {
        var format = new WaveFormat(44100, 16, 2);
        var sourceBytes = CreateSine(format, 4096, 440);
        var tap = new AudioVisualizationTapWaveProvider(
            new MemoryWaveProvider(format, sourceBytes));
        tap.Read(new byte[sourceBytes.Length], 0, sourceBytes.Length);

        var snapshot = tap.Snapshot(28);

        Assert.False(snapshot.IsActive);
        Assert.All(snapshot.Bands, value => Assert.Equal(0, value));
    }

    private static byte[] CreateSine(
        WaveFormat format,
        int frames,
        double frequency)
    {
        var bytes = new byte[frames * format.BlockAlign];
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (short)Math.Round(
                Math.Sin(2 * Math.PI * frequency * frame / format.SampleRate)
                * short.MaxValue * .5);
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * format.BlockAlign + channel * 2;
                bytes[offset] = (byte)sample;
                bytes[offset + 1] = (byte)(sample >> 8);
            }
        }
        return bytes;
    }

    private sealed class MemoryWaveProvider(
        WaveFormat waveFormat,
        byte[] bytes) : IWaveProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = waveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(count, bytes.Length - _position);
            if (available <= 0) return 0;
            Array.Copy(bytes, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
