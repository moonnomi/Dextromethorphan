namespace Dextromethorphan.Infrastructure.Audio;

internal sealed class CallbackTimingAccumulator
{
    private long _deadlineMisses;
    private double _maximumMilliseconds;

    public long DeadlineMisses(TimingWaveProvider? current) =>
        Volatile.Read(ref _deadlineMisses)
        + (current?.DeadlineMisses ?? 0);

    public double MaximumMilliseconds(TimingWaveProvider? current) =>
        Math.Max(
            Volatile.Read(ref _maximumMilliseconds),
            current?.MaximumReadMilliseconds ?? 0);

    public void Capture(TimingWaveProvider provider)
    {
        Interlocked.Add(ref _deadlineMisses, provider.DeadlineMisses);
        var observed = provider.MaximumReadMilliseconds;
        var current = Volatile.Read(ref _maximumMilliseconds);
        while (observed > current)
        {
            var prior = Interlocked.CompareExchange(
                ref _maximumMilliseconds,
                observed,
                current);
            if (prior.Equals(current)) return;
            current = prior;
        }
    }
}
