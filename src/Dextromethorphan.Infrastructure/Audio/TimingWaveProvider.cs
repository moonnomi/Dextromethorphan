using System.Diagnostics;
using NAudio.Wave;

namespace Dextromethorphan.Infrastructure.Audio;

internal sealed class TimingWaveProvider(
    IWaveProvider source) : IWaveProvider
{
    private long _lastTicks;
    private long _maximumTicks;

    public WaveFormat WaveFormat => source.WaveFormat;
    public double LastReadMilliseconds =>
        Volatile.Read(ref _lastTicks) * 1000d / Stopwatch.Frequency;
    public double MaximumReadMilliseconds =>
        Volatile.Read(ref _maximumTicks) * 1000d / Stopwatch.Frequency;

    public int Read(byte[] buffer, int offset, int count)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return source.Read(buffer, offset, count);
        }
        finally
        {
            var elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Exchange(ref _lastTicks, elapsed);
            var current = Volatile.Read(ref _maximumTicks);
            while (elapsed > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maximumTicks,
                    elapsed,
                    current);
                if (observed == current) break;
                current = observed;
            }
        }
    }
}
