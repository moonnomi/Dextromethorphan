using System.Diagnostics;
using Dextromethorphan.Infrastructure.Audio;
using NAudio.Wave;

namespace Dextromethorphan.Tests;

public sealed class TimingWaveProviderTests
{
    [Fact]
    public void ReadRecordsTimingWithoutInventingDeadlineMisses()
    {
        var source = new TestWaveProvider(TimeSpan.Zero);
        var timed = new TimingWaveProvider(source);
        var buffer = new byte[192_000]; // 1 s at 48 kHz, 16-bit stereo.

        Assert.Equal(buffer.Length, timed.Read(buffer, 0, buffer.Length));

        Assert.True(timed.LastReadMilliseconds >= 0);
        Assert.True(timed.MaximumReadMilliseconds >= timed.LastReadMilliseconds);
        Assert.Equal(0, timed.DeadlineMisses);
    }

    [Fact]
    public void ReadCountsSourceWorkThatMissesTheAudioBufferDeadline()
    {
        var source = new TestWaveProvider(TimeSpan.FromMilliseconds(20));
        var timed = new TimingWaveProvider(source);
        var buffer = new byte[960]; // 5 ms at 48 kHz, 16-bit stereo.

        timed.Read(buffer, 0, buffer.Length);
        timed.Read(buffer, 0, buffer.Length);

        Assert.Equal(2, timed.DeadlineMisses);
        Assert.True(timed.MaximumReadMilliseconds >= 15);
    }

    [Fact]
    public void AccumulatorRetainsMetricsAcrossProviderRebuilds()
    {
        var accumulator = new CallbackTimingAccumulator();
        var first = new TimingWaveProvider(
            new TestWaveProvider(TimeSpan.FromMilliseconds(20)));
        var second = new TimingWaveProvider(
            new TestWaveProvider(TimeSpan.FromMilliseconds(20)));
        var buffer = new byte[960];

        first.Read(buffer, 0, buffer.Length);
        accumulator.Capture(first);
        second.Read(buffer, 0, buffer.Length);

        Assert.Equal(2, accumulator.DeadlineMisses(second));
        Assert.True(accumulator.MaximumMilliseconds(second) >= 15);
    }

    private sealed class TestWaveProvider(TimeSpan delay) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } =
            new(48_000, 16, 2);

        public int Read(byte[] buffer, int offset, int count)
        {
            if (delay > TimeSpan.Zero)
            {
                var until = Stopwatch.GetTimestamp()
                            + (long)(delay.TotalSeconds
                                     * Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < until)
                    Thread.SpinWait(64);
            }
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
