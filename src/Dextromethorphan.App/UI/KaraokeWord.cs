using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dextromethorphan.App.UI;

public sealed class KaraokeWord : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(KaraokeWord),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(KaraokeWord),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceProgress));
    public static readonly DependencyProperty InactiveBrushProperty = DependencyProperty.Register(
        nameof(InactiveBrush), typeof(Brush), typeof(KaraokeWord),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush InactiveBrush { get => (Brush)GetValue(InactiveBrushProperty); set => SetValue(InactiveBrushProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        var text = Format();
        return new Size(Math.Min(text.WidthIncludingTrailingWhitespace, constraint.Width), Math.Min(text.Height, constraint.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var text = Format();
        drawingContext.DrawText(text, new Point(0, 0));
        var width = text.WidthIncludingTrailingWhitespace * Progress;
        if (width <= 0) return;
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, width, Math.Max(ActualHeight, text.Height))));
        text.SetForegroundBrush(Foreground);
        drawingContext.DrawText(text, new Point(0, 0));
        drawingContext.Pop();
    }

    private FormattedText Format() => new(
        Text ?? string.Empty,
        CultureInfo.CurrentUICulture,
        FlowDirection,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
        FontSize,
        InactiveBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static object CoerceProgress(DependencyObject dependencyObject, object value) =>
        Math.Clamp((double)value, 0, 1);
}
