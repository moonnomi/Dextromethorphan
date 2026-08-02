using Dextromethorphan.App.Library;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class ScheduledScanPolicyTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void ConservativeDefaultsAvoidBatteryAndMeteredWork(bool battery, bool metered, bool expected)
    {
        var decision = ScheduledScanDecision.Evaluate(new AppSettings(), new ScanEnvironment(battery, metered));
        Assert.Equal(expected, decision.CanScan);
    }

    [Fact]
    public void ExplicitOverridesPermitScheduledWork()
    {
        var settings = new AppSettings
        {
            AllowScheduledScanOnBattery = true,
            AllowScheduledScanOnMeteredNetwork = true
        };

        Assert.True(ScheduledScanDecision.Evaluate(settings, new ScanEnvironment(true, true)).CanScan);
    }

    [Fact]
    public void DisabledScheduleNeverRuns()
    {
        var settings = new AppSettings { ScheduledLibraryScanEnabled = false };
        Assert.False(ScheduledScanDecision.Evaluate(settings, new ScanEnvironment(false, false)).CanScan);
    }
}
