using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using Dextromethorphan.Infrastructure.Audio.Dsp;
using NAudio.Wave;

namespace Dextromethorphan.Tests;

public sealed class DspQualificationTests
{
    private static readonly string Corpus = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "AudioFormats");

    [Fact]
    public void GaplessJoinsChunkedStereoFixturesWithoutLossOrDuplication()
    {
        var firstSamples = Enumerable.Range(1, 514)
            .Select(value => value / 10_000f)
            .ToArray();
        var secondSamples = Enumerable.Range(1_001, 386)
            .Select(value => value / 10_000f)
            .ToArray();
        var first = new ChunkedSampleProvider(
            firstSamples,
            48_000,
            2,
            37);
        var second = new ChunkedSampleProvider(
            secondSamples,
            48_000,
            2,
            23);
        using var transition = new TransitionSampleProvider(
            first,
            firstSamples.Length);
        transition.QueueNext(second, secondSamples.Length);

        var output = Drain(transition, [62, 254, 18, 512]);

        Assert.Equal(
            firstSamples.Concat(secondSamples),
            output);
    }

    [Fact]
    public void CrossfadeHasEqualPowerEndpointsAndNoBoundarySilence()
    {
        var first = new ChunkedSampleProvider(
            Enumerable.Repeat(1f, 8).ToArray(),
            4,
            1,
            2);
        var second = new ChunkedSampleProvider(
            Enumerable.Repeat(0.5f, 8).ToArray(),
            4,
            1,
            3);
        using var transition = new TransitionSampleProvider(
            first,
            8,
            crossfadeSeconds: 1);
        transition.QueueNext(second, 8);

        var output = Drain(transition, [3, 2, 7]);

        Assert.Equal(12, output.Length);
        var fade = output.Skip(4).Take(4).ToArray();
        for (var frame = 0; frame < fade.Length; frame++)
        {
            var angle = frame / 3d * Math.PI / 2;
            var expected = Math.Cos(angle)
                           + 0.5 * Math.Sin(angle);
            Assert.Equal(expected, fade[frame], precision: 6);
        }
        Assert.All(output, sample => Assert.True(sample >= 0.5f));
        Assert.Equal(1f, fade[0]);
        Assert.Equal(0.5f, fade[^1]);
    }

    [Fact]
    public void CrossfadeClampsToVeryShortIncomingTrackWithoutDroppingTail()
    {
        var outgoing = Enumerable.Range(1, 20)
            .Select(value => value / 100f)
            .ToArray();
        var incoming = new[] { 0.7f, 0.8f, 0.9f, 1f };
        using var transition = new TransitionSampleProvider(
            new ChunkedSampleProvider(outgoing, 4, 1, 5),
            outgoing.Length,
            crossfadeSeconds: 3);
        transition.QueueNext(
            new ChunkedSampleProvider(incoming, 4, 1, 2),
            incoming.Length);

        var output = Drain(transition, [7, 3, 9]);

        Assert.Equal(20, output.Length);
        Assert.Equal(outgoing.Take(16), output.Take(16));
        Assert.Equal(outgoing[16], output[16], precision: 6);
        Assert.Equal(incoming[^1], output[^1], precision: 6);
    }

    [Fact]
    public void CrossfadeNormalizesDifferentRatesChannelsAndBitDepths()
    {
        var first = AudioDecoderFactory.Open(
            TrackFor("transition-mono-44100.wav"));
        var second = AudioDecoderFactory.Open(
            TrackFor("transition-stereo-48000.wav"));
        var target = WaveFormat.CreateIeeeFloatWaveFormat(
            48_000,
            2);
        var firstTotal = AudioDecoderFactory.TotalSamples(first, target);
        var secondTotal = AudioDecoderFactory.TotalSamples(second, target);
        const double fadeSeconds = 0.02;
        var fadeSamples = (long)(
            fadeSeconds * target.SampleRate * target.Channels);
        using var transition = new TransitionSampleProvider(
            AudioDecoderFactory.Normalize(first, target),
            firstTotal,
            fadeSeconds,
            first);
        transition.QueueNext(
            AudioDecoderFactory.Normalize(second, target),
            secondTotal,
            second);

        var output = Drain(transition, [510, 2048, 126]);

        Assert.Equal(
            firstTotal + secondTotal - fadeSamples,
            output.LongLength);
        Assert.All(
            output.Skip((int)(firstTotal - fadeSamples))
                .Take((int)fadeSamples),
            sample => Assert.True(
                Math.Abs(sample) > 0.15f,
                $"Unexpected transition silence: {sample}"));
        // The WDL resampler has a short, expected filter warm-up. Verify the
        // steady-state level rather than treating its first tap as audio loss.
        Assert.InRange(output[2_000], 0.34f, 0.36f);
        Assert.InRange(output[^1], 0.19f, 0.21f);
    }

    private static Track TrackFor(string file) =>
        new()
        {
            Path = Path.Combine(Corpus, file),
            Title = file,
            Duration = file.Contains("44100", StringComparison.Ordinal)
                ? TimeSpan.FromMilliseconds(80)
                : TimeSpan.FromMilliseconds(30)
        };

    private static float[] Drain(
        ISampleProvider provider,
        IReadOnlyList<int> callbackSizes)
    {
        var output = new List<float>();
        var index = 0;
        while (true)
        {
            var requested = callbackSizes[index++ % callbackSizes.Count];
            requested -= requested % provider.WaveFormat.Channels;
            var buffer = new float[requested];
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            output.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        return output.ToArray();
    }

    private sealed class ChunkedSampleProvider(
        float[] samples,
        int sampleRate,
        int channels,
        int maximumRead) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(
                sampleRate,
                channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(
                Math.Min(count, maximumRead),
                samples.Length - _position);
            available -= available % channels;
            if (available <= 0) return 0;
            Array.Copy(
                samples,
                _position,
                buffer,
                offset,
                available);
            _position += available;
            return available;
        }
    }
}
