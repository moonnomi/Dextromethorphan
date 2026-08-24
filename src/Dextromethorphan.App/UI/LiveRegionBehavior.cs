using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;

namespace Dextromethorphan.App.UI;

/// <summary>
/// Turns a changing binding into a real UI Automation live-region event.
/// AutomationProperties.LiveSetting alone describes a region but does not
/// notify screen readers when its content changes.
/// </summary>
public static class LiveRegionBehavior
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, LiveRegionState> States = new();

    public static readonly DependencyProperty AnnouncementProperty =
        DependencyProperty.RegisterAttached(
            "Announcement",
            typeof(object),
            typeof(LiveRegionBehavior),
            new PropertyMetadata(null, AnnouncementChanged));

    public static object? GetAnnouncement(DependencyObject element) =>
        element.GetValue(AnnouncementProperty);

    public static void SetAnnouncement(DependencyObject element, object? value) =>
        element.SetValue(AnnouncementProperty, value);

    private static void AnnouncementChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not FrameworkElement element
            || Equals(eventArgs.OldValue, eventArgs.NewValue))
            return;

        var state = States.GetOrCreateValue(element);
        state.PendingAnnouncement = eventArgs.NewValue?.ToString();
        if (!state.IsHooked)
        {
            element.Loaded += ElementLoaded;
            element.IsVisibleChanged += ElementIsVisibleChanged;
            state.IsHooked = true;
        }
        QueueAnnouncement(element, state);
    }

    private static void ElementLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement element && States.TryGetValue(element, out var state))
            QueueAnnouncement(element, state);
    }

    private static void ElementIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is true
            && sender is FrameworkElement element
            && States.TryGetValue(element, out var state))
            QueueAnnouncement(element, state);
    }

    private static void QueueAnnouncement(FrameworkElement element, LiveRegionState state)
    {
        var setting = AutomationProperties.GetLiveSetting(element);
        if (!LiveRegionAnnouncementPolicy.CanRaise(
                state.PendingAnnouncement,
                element.IsLoaded,
                element.IsVisible,
                setting)
            || state.Operation is { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing })
            return;

        state.Operation = element.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () =>
            {
                state.Operation = null;
                if (!LiveRegionAnnouncementPolicy.CanRaise(
                        state.PendingAnnouncement,
                        element.IsLoaded,
                        element.IsVisible,
                        AutomationProperties.GetLiveSetting(element)))
                    return;

                var peer = UIElementAutomationPeer.FromElement(element)
                           ?? UIElementAutomationPeer.CreatePeerForElement(element);
                if (peer is null) return;
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                state.PendingAnnouncement = null;
            });
    }

    private sealed class LiveRegionState
    {
        public bool IsHooked { get; set; }
        public string? PendingAnnouncement { get; set; }
        public DispatcherOperation? Operation { get; set; }
    }
}

public static class LiveRegionAnnouncementPolicy
{
    public static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    public static bool CanRaise(
        string? announcement,
        bool isLoaded,
        bool isVisible,
        AutomationLiveSetting politeness) =>
        isLoaded
        && isVisible
        && politeness != AutomationLiveSetting.Off
        && Normalize(announcement).Length > 0;
}
