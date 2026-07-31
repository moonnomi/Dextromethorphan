using Dextromethorphan.Core.Models;
using Dextromethorphan.Core.Playback;
using Dextromethorphan.Infrastructure.Audio.Dsp;
using Dextromethorphan.Infrastructure.Audio;
using System.Text;
using System.Buffers.Binary;
using NAudio.Wave;

namespace Dextromethorphan.Tests;

public sealed class AudioPipelineTests
{
    [Fact]
    public void ReplayGainUsesAlbumFallbackAndPreventsTaggedPeakClipping()
    {
        var track = NewTrack() with { ReplayGainTrackDb = 6, ReplayGainAlbumDb = 3, ReplayPeak = 0.8 };
        var allowedByPeak = -20 * Math.Log10(0.8);
        Assert.Equal(allowedByPeak, ReplayGainCalculator.GainDecibels(track, ReplayGainMode.Track, 0, true), 8);
        Assert.Equal(3, ReplayGainCalculator.GainDecibels(track, ReplayGainMode.Album, 0, false));
    }

    [Fact]
    public void GaplessProviderFillsOneCallbackAcrossTrackBoundary()
    {
        var first = new ArraySampleProvider([1, 1, 1, 1], 4, 1);
        var second = new ArraySampleProvider([2, 2, 2, 2], 4, 1);
        using var provider = new TransitionSampleProvider(first, 4);
        var changed = false;
        provider.SourceChanged += (_, _) => changed = true;
        provider.QueueNext(second, 4);
        var output = new float[8];

        var read = provider.Read(output, 0, output.Length);

        Assert.Equal(8, read);
        Assert.Equal(new float[] { 1, 1, 1, 1, 2, 2, 2, 2 }, output);
        Assert.True(changed);
    }

    [Fact]
    public void DspPositionCounterPreservesAnAbsoluteSeekOffset()
    {
        var source = new ArraySampleProvider(Enumerable.Repeat(1f, 12).ToArray(), 4, 1);
        using var provider = new TransitionSampleProvider(source, 20, initialPositionSamples: 8);
        var output = new float[4];

        Assert.Equal(8, provider.PositionSamples);
        Assert.Equal(4, provider.Read(output, 0, output.Length));
        Assert.Equal(12, provider.PositionSamples);
    }

    [Fact]
    public void CrossfadeUsesEqualPowerAndDoesNotInsertSilence()
    {
        var first = new ArraySampleProvider(Enumerable.Repeat(1f, 8).ToArray(), 4, 1);
        var second = new ArraySampleProvider(Enumerable.Repeat(0.5f, 8).ToArray(), 4, 1);
        using var provider = new TransitionSampleProvider(first, 8, 1);
        provider.QueueNext(second, 8);
        var output = new float[12];

        var read = provider.Read(output, 0, output.Length);

        Assert.Equal(12, read);
        Assert.All(output, sample => Assert.True(sample > 0.45f));
        Assert.Equal(1f, output[0]);
        Assert.Equal(0.5f, output[^1]);
    }

    [Fact]
    public void SoundTouchChangesTempoWithoutChangingPitch()
    {
        const int sampleRate = 48_000;
        const int inputFrames = sampleRate * 2;
        var source = new ArraySampleProvider(
            Sine(inputFrames, sampleRate, 997),
            sampleRate,
            1);
        var provider = new SoundTouchSampleProvider(
            source,
            inputFrames,
            speed: 1.25,
            pitchSemitones: 0,
            preservePitch: true);

        var output = Drain(provider);

        Assert.Equal(76_800, output.Length);
        Assert.InRange(
            EstimateFrequency(output, sampleRate),
            985,
            1_010);
        Assert.Equal(output.Length, provider.OutputFrames);
        Assert.InRange(provider.ProcessingLatencyMilliseconds, 1, 100);
    }

    [Fact]
    public void SoundTouchChangesPitchWithoutChangingDuration()
    {
        const int sampleRate = 48_000;
        const int inputFrames = sampleRate * 2;
        var provider = new SoundTouchSampleProvider(
            new ArraySampleProvider(
                Sine(inputFrames, sampleRate, 440),
                sampleRate,
                1),
            inputFrames,
            speed: 1,
            pitchSemitones: 12,
            preservePitch: true);

        var output = Drain(provider);

        Assert.Equal(inputFrames, output.Length);
        Assert.InRange(
            EstimateFrequency(output, sampleRate),
            860,
            900);
    }

    [Fact]
    public void SoundTouchReportsAndBoundsMeasuredTimelineDisplacement()
    {
        const int sampleRate = 48_000;
        const int inputFrames = sampleRate * 2;
        const int impulseFrame = sampleRate;
        var impulse = new float[inputFrames];
        impulse[impulseFrame] = 1;
        var provider = new SoundTouchSampleProvider(
            new ArraySampleProvider(impulse, sampleRate, 1),
            inputFrames,
            speed: 1.25,
            pitchSemitones: 0,
            preservePitch: true);

        var output = Drain(provider);
        var outputImpulse = Array.IndexOf(output, output.Max());
        var presentedMediaFrame = outputImpulse * provider.Speed;
        var errorMilliseconds = Math.Abs(
            presentedMediaFrame - impulseFrame) * 1000 / sampleRate;

        Assert.InRange(outputImpulse, 0, output.Length - 1);
        Assert.InRange(
            errorMilliseconds,
            0,
            provider.ProcessingLatencyMilliseconds + 10);
        Assert.InRange(provider.InitialLatencyFrames, 1, sampleRate / 5);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.5)]
    public void SoundTouchSupportsConfiguredSpeedBounds(double speed)
    {
        const int sampleRate = 48_000;
        var provider = new SoundTouchSampleProvider(
            new ArraySampleProvider(
                Sine(sampleRate, sampleRate, 440),
                sampleRate,
                1),
            sampleRate,
            speed,
            pitchSemitones: 0,
            preservePitch: true);

        var output = Drain(provider);

        Assert.Equal(
            (int)Math.Ceiling(sampleRate / speed),
            output.Length);
        Assert.InRange(
            EstimateFrequency(output, sampleRate),
            420,
            460);
    }

    [Fact]
    public void LimiterUsesGainAndPreventsOverflow()
    {
        var provider = new GainLimiterSampleProvider(new ArraySampleProvider([0.25f, -0.75f], 4, 1)) { Gain = 2, PreventClipping = true };
        var output = new float[2];
        provider.Read(output, 0, 2);
        Assert.Equal(0.5f, output[0]);
        Assert.Equal(-1f, output[1]);
        Assert.Equal(1.5, provider.Peak);
    }

    [Fact]
    public void EndOfTrackSleepTimerExpiresOnlyWhenNotified()
    {
        using var timer = new SleepTimerService();
        var expired = false;
        timer.Expired += (_, _) => expired = true;
        timer.StopAtEndOfTrack();
        Assert.True(timer.Snapshot.IsActive);
        timer.NotifyTrackEnded();
        Assert.True(expired);
        Assert.False(timer.Snapshot.IsActive);
    }

    [Fact]
    public void DsfStreamPreservesPayloadAndAddsAlternatingDopMarkers()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dsf");
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, false))
            {
                writer.Write(Encoding.ASCII.GetBytes("DSD ")); writer.Write(28L); writer.Write(100L); writer.Write(0L);
                writer.Write(Encoding.ASCII.GetBytes("fmt ")); writer.Write(52L); writer.Write(1); writer.Write(0); writer.Write(2); writer.Write(2);
                writer.Write(2_822_400); writer.Write(1); writer.Write(32L); writer.Write(4); writer.Write(0);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(20L);
                writer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            }
            using var dop = new DsfDopWaveStream(path);
            var output = new byte[12];
            var read = dop.Read(output, 0, output.Length);
            Assert.Equal(12, read);
            Assert.Equal(new byte[] { 1, 2, 0x05, 5, 6, 0x05, 3, 4, 0xFA, 7, 8, 0xFA }, output);
            Assert.Equal(176_400, dop.WaveFormat.SampleRate);
            Assert.Equal(24, dop.WaveFormat.BitsPerSample);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DffStreamInterleavesChannelsAndAddsDopMarkers()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dff");
        try
        {
            using var properties = new MemoryStream();
            WriteId(properties, "SND ");
            WriteChunk(properties, "FS  ", stream => WriteUInt32(stream, 2_822_400));
            WriteChunk(properties, "CHNL", stream => { WriteUInt16(stream, 2); WriteId(stream, "SLFT"); WriteId(stream, "SRGT"); });
            WriteChunk(properties, "CMPR", stream => { WriteId(stream, "DSD "); stream.WriteByte(0); });
            using (var file = File.Create(path))
            {
                WriteId(file, "FRM8");
                WriteUInt64(file, (ulong)(4 + 12 + properties.Length + 12 + 8));
                WriteId(file, "DSD ");
                WriteId(file, "PROP"); WriteUInt64(file, (ulong)properties.Length); properties.Position = 0; properties.CopyTo(file);
                WriteId(file, "DSD "); WriteUInt64(file, 8); file.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            }
            using var dop = new DffDopWaveStream(path);
            var output = new byte[12];
            Assert.Equal(12, dop.Read(output, 0, output.Length));
            Assert.Equal(new byte[] { 0x80, 0xC0, 0x05, 0x40, 0x20, 0x05, 0xA0, 0xE0, 0xFA, 0x60, 0x10, 0xFA }, output);
            Assert.Equal(176_400, dop.WaveFormat.SampleRate);
        }
        finally { File.Delete(path); }
    }

    private static Track NewTrack() => new() { Path = "test.flac", Title = "Test" };

    private static float[] Sine(
        int frames,
        int sampleRate,
        double frequency) =>
        Enumerable.Range(0, frames)
            .Select(frame => (float)(
                0.5 * Math.Sin(
                    2 * Math.PI * frequency * frame / sampleRate)))
            .ToArray();

    private static float[] Drain(ISampleProvider provider)
    {
        var output = new List<float>();
        var buffer = new float[2048];
        while (true)
        {
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            output.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        return output.ToArray();
    }

    private static double EstimateFrequency(
        IReadOnlyList<float> samples,
        int sampleRate)
    {
        var start = samples.Count / 4;
        var end = samples.Count * 3 / 4;
        var crossings = 0;
        for (var index = start + 1; index < end; index++)
        {
            if (samples[index - 1] <= 0 && samples[index] > 0)
                crossings++;
        }
        return crossings * sampleRate / (double)(end - start);
    }

    private static void WriteChunk(Stream destination, string id, Action<MemoryStream> write)
    {
        using var content = new MemoryStream();
        write(content);
        WriteId(destination, id); WriteUInt64(destination, (ulong)content.Length);
        content.Position = 0; content.CopyTo(destination);
        if ((content.Length & 1) != 0) destination.WriteByte(0);
    }
    private static void WriteId(Stream stream, string id) => stream.Write(Encoding.ASCII.GetBytes(id));
    private static void WriteUInt16(Stream stream, ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); stream.Write(b); }
    private static void WriteUInt32(Stream stream, uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); stream.Write(b); }
    private static void WriteUInt64(Stream stream, ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); stream.Write(b); }

    private sealed class ArraySampleProvider(float[] samples, int sampleRate, int channels) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }
}
