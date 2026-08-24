using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.Tests;

public sealed class ToastNotificationTests
{
    [Theory]
    [InlineData(ToastSeverity.Information, "\uE946")]
    [InlineData(ToastSeverity.Success, "\uE73E")]
    [InlineData(ToastSeverity.Warning, "\uE7BA")]
    [InlineData(ToastSeverity.Error, "\uEA39")]
    public void SeverityHasAVisibleNonColorGlyph(
        ToastSeverity severity,
        string expectedGlyph)
    {
        var notification = new ToastNotification("Message", severity);

        Assert.Equal(expectedGlyph, notification.Glyph);
        Assert.StartsWith(severity.ToString(), notification.AutomationText);
    }

    [Fact]
    public void ErrorsRemainVisibleLongerThanRoutineFeedback()
    {
        var routine = new ToastNotification("Done", ToastSeverity.Success);
        var error = new ToastNotification("Failed", ToastSeverity.Error);

        Assert.True(error.DisplayDuration > routine.DisplayDuration);
    }
}
