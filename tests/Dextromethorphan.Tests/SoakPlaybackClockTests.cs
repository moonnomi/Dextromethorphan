using Dextromethorphan.AudioSoak;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class SoakPlaybackClockTests
{
    [Fact]
    public void CountsOnlyObservedPlayingIntervals()
    {
        var clock = new SoakPlaybackClock(TimeSpan.FromSeconds(1));

        clock.Observe(PlaybackState.Playing, TimeSpan.FromMilliseconds(25));
        clock.Observe(PlaybackState.Buffering, TimeSpan.FromMilliseconds(40));

        Assert.Equal(TimeSpan.FromMilliseconds(25), clock.Playing);
        Assert.Equal(TimeSpan.FromMilliseconds(40), clock.NonPlaying);
        Assert.Equal(TimeSpan.Zero, clock.UnobservedGap);
    }

    [Fact]
    public void DoesNotCountSleepOrStallGapAsPlayback()
    {
        var clock = new SoakPlaybackClock(TimeSpan.FromSeconds(1));

        clock.Observe(PlaybackState.Playing, TimeSpan.FromMinutes(20));

        Assert.Equal(TimeSpan.Zero, clock.Playing);
        Assert.Equal(TimeSpan.FromMinutes(20), clock.UnobservedGap);
        Assert.Equal(TimeSpan.Zero, clock.NonPlaying);
    }
}
