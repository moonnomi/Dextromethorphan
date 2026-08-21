using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.App.UI;

/// <summary>
/// A capped, presentation-only spectrum. Its timer exists only while the
/// visualizer is visible and enabled, keeping the disabled cost at zero.
/// </summary>
public sealed class SpectrumVisualizer : FrameworkElement
{
    private const int BandCount = 28;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<double> _bands = Array.Empty<double>();

    public SpectrumVisualizer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            DispatcherPriority.Background,
            OnFrame,
            Dispatcher);
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(SpectrumVisualizer),
            new FrameworkPropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!IsActive || ActualWidth <= 0 || ActualHeight <= 0) return;
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.MediumPurple;
        var muted = TryFindResource("TextMutedBrush") as Brush ?? Brushes.Gray;
        var gap = Math.Clamp(ActualWidth / (BandCount * 4), .75, 2);
        var width = Math.Max(1, (ActualWidth - gap * (BandCount - 1)) / BandCount);
        for (var index = 0; index < BandCount; index++)
        {
            var value = index < _bands.Count ? _bands[index] : 0;
            var height = Math.Max(2, value * ActualHeight);
            var x = index * (width + gap);
            var rectangle = new Rect(x, ActualHeight - height, width, height);
            drawingContext.DrawRoundedRectangle(
                value > .035 ? accent : muted,
                null,
                rectangle,
                Math.Min(2, width / 2),
                Math.Min(2, width / 2));
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            _bands = Array.Empty<double>();
            InvalidateVisual();
            return;
        }
        _bands = viewModel.GetVisualizationSnapshot(BandCount).Bands;
        InvalidateVisual();
    }

    private static void OnIsActiveChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var visualizer = (SpectrumVisualizer)dependencyObject;
        if (!(bool)e.NewValue)
            visualizer._bands = Array.Empty<double>();
        visualizer.UpdateTimer();
        visualizer.InvalidateVisual();
    }

    private void UpdateTimer()
    {
        if (IsLoaded && IsVisible && IsActive) _timer.Start();
        else _timer.Stop();
    }
}
