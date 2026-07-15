using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dextromethorphan.App.UI;

public static class SmoothScrollBehavior
{
    private static readonly Dictionary<ScrollViewer, double> Targets = [];
    private static bool _isRendering;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScrollBehavior), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement control) return;
        if (args.NewValue is true) control.PreviewMouseWheel += OnPreviewMouseWheel;
        else control.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is not DependencyObject element) return;
        var viewer = element as ScrollViewer ?? FindDescendant<ScrollViewer>(element);
        if (viewer is null || viewer.ScrollableHeight <= 0) return;

        var currentTarget = Targets.TryGetValue(viewer, out var pending) ? pending : viewer.VerticalOffset;
        Targets[viewer] = Math.Clamp(currentTarget - (args.Delta * 0.72), 0, viewer.ScrollableHeight);
        args.Handled = true;
        if (_isRendering) return;
        CompositionTarget.Rendering += OnRendering;
        _isRendering = true;
    }

    private static void OnRendering(object? sender, EventArgs args)
    {
        foreach (var pair in Targets.ToArray())
        {
            var viewer = pair.Key;
            if (!viewer.IsLoaded)
            {
                Targets.Remove(viewer);
                continue;
            }

            var target = Math.Clamp(pair.Value, 0, viewer.ScrollableHeight);
            var distance = target - viewer.VerticalOffset;
            if (Math.Abs(distance) < 0.35)
            {
                viewer.ScrollToVerticalOffset(target);
                Targets.Remove(viewer);
            }
            else
            {
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + (distance * 0.24));
            }
        }

        if (Targets.Count != 0) return;
        CompositionTarget.Rendering -= OnRendering;
        _isRendering = false;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) return result;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
