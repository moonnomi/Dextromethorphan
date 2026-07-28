using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class DeferredPageLoadGateTests
{
    [Fact]
    public async Task WaitsThroughLongSmoothScrollInsteadOfAbandoningPage()
    {
        var busyChecks = 0;

        var available = await DeferredPageLoadGate.WaitForIdleAsync(
            () => true,
            () => ++busyChecks < 6,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.True(available);
        Assert.Equal(6, busyChecks);
    }

    [Fact]
    public async Task StopsWhenViewIsNoLongerAvailable()
    {
        var availabilityChecks = 0;

        var available = await DeferredPageLoadGate.WaitForIdleAsync(
            () => ++availabilityChecks < 3,
            () => true,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.False(available);
    }
}
