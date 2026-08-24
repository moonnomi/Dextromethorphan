using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.App.UI;

/// <summary>
/// Gives sliders explicit fine/coarse keyboard semantics. Playback-position
/// sliders can also opt in to committing the adjusted value to the audio
/// engine; the ordinary WPF slider behavior only changes its local value.
/// </summary>
public static class SliderAccessibilityBehavior
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(SliderAccessibilityBehavior),
            new PropertyMetadata(false, EnabledChanged));

    public static readonly DependencyProperty CommitPlaybackSeekProperty =
        DependencyProperty.RegisterAttached(
            "CommitPlaybackSeek",
            typeof(bool),
            typeof(SliderAccessibilityBehavior),
            new PropertyMetadata(false));

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetCommitPlaybackSeek(DependencyObject element) =>
        (bool)element.GetValue(CommitPlaybackSeekProperty);

    public static void SetCommitPlaybackSeek(
        DependencyObject element,
        bool value) =>
        element.SetValue(CommitPlaybackSeekProperty, value);

    private static void EnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not Slider slider) return;
        slider.PreviewKeyDown -= SliderPreviewKeyDown;
        if (eventArgs.NewValue is true)
            slider.PreviewKeyDown += SliderPreviewKeyDown;
    }

    private static async void SliderPreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (sender is not Slider slider) return;
        var target = SliderKeyboardStepPolicy.TargetFor(
            eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key,
            eventArgs.KeyboardDevice.Modifiers,
            slider.Value,
            slider.Minimum,
            slider.Maximum,
            slider.SmallChange,
            slider.LargeChange);
        if (target is null) return;

        slider.SetCurrentValue(RangeBase.ValueProperty, target.Value);
        eventArgs.Handled = true;

        if (GetCommitPlaybackSeek(slider)
            && slider.DataContext is MainViewModel viewModel)
            await viewModel.CommitSeekAsync(target.Value);
    }
}

public static class SliderKeyboardStepPolicy
{
    public static double? TargetFor(
        Key key,
        ModifierKeys modifiers,
        double value,
        double minimum,
        double maximum,
        double smallChange,
        double largeChange)
    {
        if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return null;

        var coarse = modifiers.HasFlag(ModifierKeys.Control);
        var fineStep = NormalizeStep(smallChange, maximum - minimum);
        var coarseStep = NormalizeStep(largeChange, maximum - minimum);
        var step = coarse ? coarseStep : fineStep;
        var target = key switch
        {
            Key.Left or Key.Down => value - step,
            Key.Right or Key.Up => value + step,
            Key.PageDown => value - coarseStep,
            Key.PageUp => value + coarseStep,
            Key.Home => minimum,
            Key.End => maximum,
            _ => double.NaN
        };
        return double.IsNaN(target)
            ? null
            : Math.Clamp(target, minimum, maximum);
    }

    private static double NormalizeStep(double step, double range) =>
        double.IsFinite(step) && step > 0
            ? step
            : Math.Max(0.01, Math.Abs(range) / 100d);
}
