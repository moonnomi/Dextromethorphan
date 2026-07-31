using System.Windows;
using System.Windows.Input;

namespace Dextromethorphan.App.UI;

internal static class MotionPolicy
{
    public static bool IsEnabled(bool applicationAnimationsEnabled) =>
        AllowsAnimations(
            applicationAnimationsEnabled,
            SystemParameters.ClientAreaAnimation);

    internal static bool AllowsAnimations(
        bool applicationAnimationsEnabled,
        bool windowsAnimationsEnabled) =>
        applicationAnimationsEnabled && windowsAnimationsEnabled;
}

internal enum ListScrollAction
{
    None,
    Home,
    End,
    PageUp,
    PageDown
}

internal static class ListScrollKeyboardPolicy
{
    public static ListScrollAction ActionFor(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & ~(ModifierKeys.Control)) != ModifierKeys.None)
            return ListScrollAction.None;
        return key switch
        {
            Key.Home => ListScrollAction.Home,
            Key.End => ListScrollAction.End,
            Key.PageUp => ListScrollAction.PageUp,
            Key.PageDown => ListScrollAction.PageDown,
            _ => ListScrollAction.None
        };
    }
}

internal static class IdleCleanupPolicy
{
    internal static bool ShouldRun(
        DateTimeOffset now,
        DateTimeOffset lastInteraction,
        DateTimeOffset lastCleanup,
        bool isWindowActive,
        bool isScanning,
        int artworkQueueDepth)
    {
        if (isScanning || artworkQueueDepth > 0) return false;
        if (now - lastCleanup < TimeSpan.FromMinutes(2)) return false;
        var requiredIdle = isWindowActive
            ? TimeSpan.FromMinutes(10)
            : TimeSpan.FromMinutes(3);
        return now - lastInteraction >= requiredIdle;
    }
}
