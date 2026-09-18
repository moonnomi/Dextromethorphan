using Dextromethorphan.Infrastructure.Audio.Dsp;

namespace Dextromethorphan.Tests;

public sealed class DynamicCrossfadeTests
{
    private static BoundaryEnvelope Boundary(double[] head, double[] tail, double duration = 180) => new(head, tail, duration);
    private static double[] Flat(double value, int count = 300) => Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public void LoudBoundariesGetShortOverlap()
    {
        var result = DynamicCrossfadePlanner.Plan(Boundary(Flat(-8), Flat(-8)), Boundary(Flat(-8), Flat(-8)), 8);
        Assert.Equal(.25, result.Seconds);
    }

    [Fact]
    public void SilenceDetectionPreservesQuietMusicAndTransientPeaks()
    {
        var peaks = Flat(-20, 360).Concat(Flat(-100, 40)).ToArray();
        var envelope = new BoundaryEnvelope([], Flat(-100, 400), 180, TailPeakDb: peaks);
        Assert.Equal(1.8, DynamicCrossfadePlanner.TrailingSilenceSeconds(envelope), 6);
        Assert.Equal(0, DynamicCrossfadePlanner.TrailingSilenceSeconds(envelope with { TailPeakDb = Flat(-55, 400) }));
        peaks[^10] = -20;
        Assert.Equal(0, DynamicCrossfadePlanner.TrailingSilenceSeconds(envelope));
        Assert.Equal(0, DynamicCrossfadePlanner.TrailingSilenceSeconds(envelope with { TailPeakDb = Flat(-120, 400) }));
    }

    [Fact]
    public void DynamicCueAdvancesOnlyAfterAConfidentLowEnergyTail()
    {
        var rms = Flat(-10, 300).Concat(Flat(-52, 100)).ToArray();
        var peaks = Flat(-7, 300).Concat(Flat(-46, 100)).ToArray();
        var envelope = new BoundaryEnvelope([], rms, 180, TailPeakDb: peaks);

        Assert.Equal(4.5, DynamicCrossfadePlanner.DynamicTailAdvanceSeconds(envelope), 6);
        peaks[^5] = -5;
        Assert.Equal(0, DynamicCrossfadePlanner.DynamicTailAdvanceSeconds(envelope), 6);
        Assert.Equal(0, DynamicCrossfadePlanner.DynamicTailAdvanceSeconds(
            envelope with { TailDb = Flat(-70, 400), TailPeakDb = Flat(-65, 400) }));
    }

    [Fact]
    public void IncomingCueSkipsSustainedDigitalSilenceButKeepsAttackPreroll()
    {
        var peaks = Flat(-100, 20).Concat(Flat(-8, 280)).ToArray();
        var envelope = new BoundaryEnvelope(Flat(-100, 20).Concat(Flat(-12, 280)).ToArray(), [], 180,
            HeadPeakDb: peaks);

        Assert.Equal(.9, DynamicCrossfadePlanner.LeadingSilenceSeconds(envelope), 6);
        Assert.Equal(0, DynamicCrossfadePlanner.LeadingSilenceSeconds(
            envelope with { HeadPeakDb = Flat(-100, 300) }));
        peaks[5] = -8;
        Assert.Equal(0, DynamicCrossfadePlanner.LeadingSilenceSeconds(envelope), 6);
    }

    [Fact]
    public void QuietTailAndIntroAllowLongerOverlapWithinMaximum()
    {
        var tail = Flat(-8, 340).Concat(Flat(-35, 60)).ToArray();
        var head = Flat(-40, 40).Concat(Flat(-8, 260)).ToArray();
        var result = DynamicCrossfadePlanner.Plan(Boundary(Flat(-8), tail), Boundary(head, Flat(-8)), 8);
        Assert.InRange(result.Seconds, 4.5, 5.6);
        var limited = DynamicCrossfadePlanner.Plan(Boundary(Flat(-8), tail), Boundary(head, Flat(-8)), 2);
        Assert.Equal(2, limited.Seconds);
    }

    [Fact]
    public void ShortPercussiveDipsDoNotLookLikeAnOutro()
    {
        var drums = Enumerable.Range(0, 400).Select(i => i % 3 == 0 ? -6d : -45d).ToArray();
        var result = DynamicCrossfadePlanner.Plan(Boundary(Flat(-8), drums), Boundary(Flat(-8), Flat(-8)), 8);
        Assert.Equal(.25, result.Seconds);
    }

    [Fact]
    public void SilenceAndInvalidDataAreConservative()
    {
        Assert.Equal(0, DynamicCrossfadePlanner.Plan(Boundary(Flat(-8), Flat(-120)), Boundary(Flat(-8), Flat(-8)), 8).Seconds);
        Assert.Equal(0, DynamicCrossfadePlanner.Plan(Boundary([], [double.NaN]), Boundary(Flat(-8), Flat(-8)), 8).Seconds);
        Assert.Equal(0, DynamicCrossfadePlanner.Plan(Boundary(Flat(-8), Flat(-8)), Boundary(Flat(-8), Flat(-8)), 0).Seconds);
    }

    [Fact]
    public void ShortTracksNeverUseMoreThanQuarterTheirLength()
    {
        var result = DynamicCrossfadePlanner.Plan(Boundary(Flat(-8), Flat(-40), 2), Boundary(Flat(-40), Flat(-8), 2), 10);
        Assert.InRange(result.Seconds, 0, .5);
    }
}
