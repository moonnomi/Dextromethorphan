using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class SmoothScrollMathTests
{
    [Fact]
    public void AnimationProgressIsRefreshRateIndependent()
    {
        var oneFrame = SmoothScrollMath.Next(0, 100, TimeSpan.FromMilliseconds(16));
        var twoHalfFrames = SmoothScrollMath.Next(
            SmoothScrollMath.Next(0, 100, TimeSpan.FromMilliseconds(8)),
            100,
            TimeSpan.FromMilliseconds(8));

        Assert.InRange(Math.Abs(oneFrame - twoHalfFrames), 0, 0.001);
    }

    [Fact]
    public void BoundaryInputCanHandOffToAParentScroller()
    {
        Assert.False(SmoothScrollMath.CanMove(0, 0, 0));
        Assert.True(SmoothScrollMath.CanMove(10, 0, 10));
        Assert.True(SmoothScrollMath.CanMove(0, 10, 0));
    }

    [Fact]
    public void LargeFrameGapsAreBoundedAndConvergent()
    {
        var next = SmoothScrollMath.Next(0, 100, TimeSpan.FromSeconds(5));

        Assert.InRange(next, 0.1, 99.9);
        Assert.True(SmoothScrollMath.IsSettled(99.8, 100));
    }
}
