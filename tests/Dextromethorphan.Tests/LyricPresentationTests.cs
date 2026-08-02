using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class LyricPresentationTests
{
    [Fact]
    public void WordProgressTracksAuthoritativeTimelineInBothDirections()
    {
        var word = new LyricWordViewModel(new LyricWord(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            "hello"));

        word.UpdatePosition(TimeSpan.FromSeconds(1.75));
        Assert.Equal(0.75, word.Progress, 3);

        word.UpdatePosition(TimeSpan.FromSeconds(1.25));
        Assert.Equal(0.25, word.Progress, 3);

        word.UpdatePosition(TimeSpan.FromSeconds(3));
        Assert.Equal(1, word.Progress);
    }

    [Fact]
    public void ReducedMotionSnapsWordHighlightWithoutChangingTiming()
    {
        var word = new LyricWordViewModel(new LyricWord(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            "hello"));

        word.UpdatePosition(TimeSpan.FromSeconds(1.01), animate: false);

        Assert.Equal(1, word.Progress);
    }
}
