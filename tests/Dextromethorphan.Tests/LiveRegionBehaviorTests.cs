using System.Windows.Automation;
using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class LiveRegionBehaviorTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  Error: Playback failed  ", "Error: Playback failed")]
    public void AnnouncementTextIsNormalized(string? value, string expected) =>
        Assert.Equal(expected, LiveRegionAnnouncementPolicy.Normalize(value));

    [Theory]
    [InlineData(true, true, AutomationLiveSetting.Polite, true)]
    [InlineData(true, true, AutomationLiveSetting.Assertive, true)]
    [InlineData(false, true, AutomationLiveSetting.Polite, false)]
    [InlineData(true, false, AutomationLiveSetting.Polite, false)]
    [InlineData(true, true, AutomationLiveSetting.Off, false)]
    public void AnnouncementRequiresAVisibleLoadedLiveRegion(
        bool isLoaded,
        bool isVisible,
        AutomationLiveSetting politeness,
        bool expected) =>
        Assert.Equal(
            expected,
            LiveRegionAnnouncementPolicy.CanRaise(
                "Status changed",
                isLoaded,
                isVisible,
                politeness));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankAnnouncementsAreIgnored(string? value) =>
        Assert.False(LiveRegionAnnouncementPolicy.CanRaise(
            value,
            isLoaded: true,
            isVisible: true,
            AutomationLiveSetting.Polite));

    [Fact]
    public void BehaviorRaisesTheWpfLiveRegionEventAndHandlesReappearance()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Dextromethorphan.App",
            "UI",
            "LiveRegionBehavior.cs"));

        Assert.Contains(
            "RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("IsVisibleChanged +=", source, StringComparison.Ordinal);
        Assert.Contains("eventArgs.NewValue is true", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dextromethorphan.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
