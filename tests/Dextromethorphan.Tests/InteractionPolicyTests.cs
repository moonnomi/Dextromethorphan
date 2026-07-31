using System.Windows.Input;
using Dextromethorphan.App.UI;
using Dextromethorphan.App;

namespace Dextromethorphan.Tests;

public sealed class InteractionPolicyTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void MotionRequiresBothApplicationAndWindowsPermission(
        bool applicationEnabled,
        bool windowsEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            MotionPolicy.AllowsAnimations(applicationEnabled, windowsEnabled));
    }

    [Theory]
    [InlineData(Key.Home, ModifierKeys.None, "Home")]
    [InlineData(Key.End, ModifierKeys.None, "End")]
    [InlineData(Key.PageUp, ModifierKeys.None, "PageUp")]
    [InlineData(Key.PageDown, ModifierKeys.None, "PageDown")]
    [InlineData(Key.Home, ModifierKeys.Control, "Home")]
    [InlineData(Key.End, ModifierKeys.Control, "End")]
    [InlineData(Key.PageDown, ModifierKeys.Shift, "None")]
    [InlineData(Key.Down, ModifierKeys.None, "None")]
    public void ListKeyboardPolicyMapsNavigationKeys(
        Key key,
        ModifierKeys modifiers,
        string expected)
    {
        Assert.Equal(
            expected,
            ListScrollKeyboardPolicy.ActionFor(key, modifiers).ToString());
    }

    [Fact]
    public void OnlyRecoverableOperationFailuresOfferContinue()
    {
        Assert.True(ErrorContinuationPolicy.CanContinue(new IOException("offline")));
        Assert.True(ErrorContinuationPolicy.CanContinue(new NotSupportedException("format")));
        Assert.False(ErrorContinuationPolicy.CanContinue(new InvalidOperationException("state")));
        Assert.False(ErrorContinuationPolicy.CanContinue(new OutOfMemoryException()));
    }

    [Fact]
    public void IdleCleanupWaitsForTrueIdleAndNoForegroundWork()
    {
        var now = new DateTimeOffset(
            2026,
            7,
            29,
            12,
            0,
            0,
            TimeSpan.Zero);

        Assert.True(IdleCleanupPolicy.ShouldRun(
            now,
            now.AddMinutes(-11),
            now.AddMinutes(-3),
            isWindowActive: true,
            isScanning: false,
            artworkQueueDepth: 0));
        Assert.True(IdleCleanupPolicy.ShouldRun(
            now,
            now.AddMinutes(-4),
            now.AddMinutes(-3),
            isWindowActive: false,
            isScanning: false,
            artworkQueueDepth: 0));
        Assert.False(IdleCleanupPolicy.ShouldRun(
            now,
            now.AddMinutes(-20),
            now.AddMinutes(-3),
            isWindowActive: false,
            isScanning: true,
            artworkQueueDepth: 0));
        Assert.False(IdleCleanupPolicy.ShouldRun(
            now,
            now.AddMinutes(-20),
            now.AddMinutes(-3),
            isWindowActive: false,
            isScanning: false,
            artworkQueueDepth: 1));
    }
}
