using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dextromethorphan.App.UI;

public static class SmoothScrollBehavior
{
    private static readonly Dictionary<ScrollViewer, ScrollTarget> Targets = [];
    private static long _lastFrameTimestamp;
    private static bool _isRendering;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScrollBehavior), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    internal static bool IsAnimating(DependencyObject element)
    {
        var viewer = element as ScrollViewer ?? FindDescendant<ScrollViewer>(element);
        return viewer is not null && Targets.ContainsKey(viewer);
    }
    internal static void Cancel(DependencyObject element) => RemoveViewer(element);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement control) return;
        if (args.NewValue is true)
        {
            control.PreviewMouseWheel += OnPreviewMouseWheel;
            if (control is FrameworkElement framework) framework.Unloaded += OnControlUnloaded;
            control.IsVisibleChanged += OnControlVisibilityChanged;
        }
        else
        {
            control.PreviewMouseWheel -= OnPreviewMouseWheel;
            if (control is FrameworkElement framework) framework.Unloaded -= OnControlUnloaded;
            control.IsVisibleChanged -= OnControlVisibilityChanged;
            RemoveViewer(control);
        }
    }

    private static void OnControlUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is DependencyObject control) RemoveViewer(control);
    }

    private static void OnControlVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is false && sender is DependencyObject control) RemoveViewer(control);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is not DependencyObject element || args.Delta == 0) return;
        var viewer = element as ScrollViewer ?? FindDescendant<ScrollViewer>(element);
        if (viewer is null || !viewer.IsVisible) return;

        var horizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && viewer.ScrollableWidth > 0;
        var state = Targets.TryGetValue(viewer, out var pending)
            ? pending
            : new ScrollTarget(viewer.HorizontalOffset, viewer.VerticalOffset);
        var delta = args.Delta * 0.72;
        var next = horizontal
            ? state with { Horizontal = Math.Clamp(state.Horizontal - delta, 0, viewer.ScrollableWidth) }
            : state with { Vertical = Math.Clamp(state.Vertical - delta, 0, viewer.ScrollableHeight) };
        if (horizontal
                ? !SmoothScrollMath.CanMove(state.Horizontal, next.Horizontal, viewer.HorizontalOffset)
                : !SmoothScrollMath.CanMove(state.Vertical, next.Vertical, viewer.VerticalOffset))
            return;

        args.Handled = true;
        if (!MotionPolicy.IsEnabled(
                Window.GetWindow(viewer)?.DataContext is not ViewModels.MainViewModel
                    || ((ViewModels.MainViewModel)Window.GetWindow(viewer)!.DataContext).AnimationsEnabled))
        {
            viewer.ScrollToHorizontalOffset(next.Horizontal);
            viewer.ScrollToVerticalOffset(next.Vertical);
            Targets.Remove(viewer);
            StopRenderingIfIdle();
            return;
        }

        Targets[viewer] = next;
        StartRendering();
    }

    private static void StartRendering()
    {
        if (_isRendering) return;
        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        _isRendering = true;
    }

    private static void OnRendering(object? sender, EventArgs args)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastFrameTimestamp, now);
        _lastFrameTimestamp = now;
        foreach (var pair in Targets.ToArray())
        {
            var viewer = pair.Key;
            if (!viewer.IsLoaded || !viewer.IsVisible)
            {
                Targets.Remove(viewer);
                continue;
            }
            if (!MotionPolicy.IsEnabled(
                    Window.GetWindow(viewer)?.DataContext is not ViewModels.MainViewModel
                        || ((ViewModels.MainViewModel)Window.GetWindow(viewer)!.DataContext).AnimationsEnabled))
            {
                viewer.ScrollToHorizontalOffset(pair.Value.Horizontal);
                viewer.ScrollToVerticalOffset(pair.Value.Vertical);
                Targets.Remove(viewer);
                continue;
            }

            var horizontalTarget = Math.Clamp(pair.Value.Horizontal, 0, viewer.ScrollableWidth);
            var verticalTarget = Math.Clamp(pair.Value.Vertical, 0, viewer.ScrollableHeight);
            var horizontal = SmoothScrollMath.Next(viewer.HorizontalOffset, horizontalTarget, elapsed);
            var vertical = SmoothScrollMath.Next(viewer.VerticalOffset, verticalTarget, elapsed);
            viewer.ScrollToHorizontalOffset(horizontal);
            viewer.ScrollToVerticalOffset(vertical);
            if (SmoothScrollMath.IsSettled(horizontal, horizontalTarget)
                && SmoothScrollMath.IsSettled(vertical, verticalTarget))
            {
                viewer.ScrollToHorizontalOffset(horizontalTarget);
                viewer.ScrollToVerticalOffset(verticalTarget);
                Targets.Remove(viewer);
            }
        }
        StopRenderingIfIdle();
    }

    private static void RemoveViewer(DependencyObject control)
    {
        var viewer = control as ScrollViewer ?? FindDescendant<ScrollViewer>(control);
        if (viewer is not null) Targets.Remove(viewer);
        StopRenderingIfIdle();
    }

    private static void StopRenderingIfIdle()
    {
        if (Targets.Count != 0 || !_isRendering) return;
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

    private readonly record struct ScrollTarget(double Horizontal, double Vertical);
}

internal static class SmoothScrollMath
{
    private static readonly TimeSpan ResponseTime = TimeSpan.FromMilliseconds(70);

    public static bool CanMove(double pendingTarget, double nextTarget, double currentOffset) =>
        Math.Abs(nextTarget - pendingTarget) > 0.01
        || Math.Abs(nextTarget - currentOffset) > 0.01;

    public static double Next(double current, double target, TimeSpan elapsed)
    {
        if (IsSettled(current, target)) return target;
        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, 0.1);
        var responseSeconds = ResponseTime.TotalSeconds;
        var progress = 1 - Math.Exp(-seconds / responseSeconds);
        return current + ((target - current) * progress);
    }

    public static bool IsSettled(double current, double target) => Math.Abs(target - current) < 0.35;
}
