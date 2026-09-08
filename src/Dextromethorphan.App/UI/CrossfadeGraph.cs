using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.UI;

/// <summary>Event-driven preview of the exact gain function used by the audio mixer.</summary>
public sealed class CrossfadeGraph : FrameworkElement
{
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(nameof(Shape), typeof(CrossfadeShape), typeof(CrossfadeGraph), new FrameworkPropertyMetadata(new CrossfadeShape(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SecondsProperty = DependencyProperty.Register(nameof(Seconds), typeof(double), typeof(CrossfadeGraph), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(CrossfadeGraph), new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(CrossfadeGraph), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public CrossfadeShape Shape { get => (CrossfadeShape)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public double Seconds { get => (double)GetValue(SecondsProperty); set => SetValue(SecondsProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = Math.Max(1, ActualWidth - 64);
        var height = Math.Max(1, ActualHeight - 60);
        const double left = 44, top = 28;
        void Label(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        Label("Outgoing —", left, 0);
        Label("Incoming ···", left + 125, 0);
        var gridPen = new Pen(Foreground, .5);
        dc.PushOpacity(.18);
        for (var i = 0; i <= 4; i++)
            dc.DrawLine(gridPen, new Point(left, top + height * i / 4), new Point(left + width, top + height * i / 4));
        dc.Pop();
        Label("100%", 0, top - 7);
        Label("50%", 7, top + height / 2 - 7);
        Label("0%", 14, top + height - 7);
        for (var i = 0; i <= 4; i++) Label($"{Seconds * i / 4:0.##}s", left + width * i / 4 - 8, top + height + 8);
        if (Seconds <= 0)
        {
            Label("No overlap · gapless", left + 12, top + height / 2 - 8);
            return;
        }
        var shape = (Shape ?? new()).Normalize();
        for (var incoming = 0; incoming < 2; incoming++)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                for (var i = 0; i <= 160; i++)
                {
                    var gains = shape.Gains(i / 160d);
                    var point = new Point(left + width * i / 160, top + height * (1 - (incoming == 0 ? gains.Outgoing : gains.Incoming)));
                    if (i == 0) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
                }
            }
            geometry.Freeze();
            var pen = new Pen(incoming == 0 ? Foreground : Accent, 2.5);
            if (incoming == 1) pen.DashStyle = DashStyles.Dash;
            dc.DrawGeometry(null, pen, geometry);
        }
    }
}
