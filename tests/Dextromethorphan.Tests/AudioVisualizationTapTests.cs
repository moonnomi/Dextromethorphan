using Dextromethorphan.Infrastructure.Audio;
using NAudio.Wave;

namespace Dextromethorphan.Tests;

public sealed class AudioVisualizationTapTests
{
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
