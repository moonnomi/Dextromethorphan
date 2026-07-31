using System.Collections;
using System.Windows;
using System.Windows.Media;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.UI;

public sealed class ChapterMarkerBar : FrameworkElement
{
    public static readonly DependencyProperty ChaptersProperty =
        DependencyProperty.Register(
            nameof(Chapters),
            typeof(IEnumerable),
            typeof(ChapterMarkerBar),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(
            nameof(Duration),
            typeof(double),
            typeof(ChapterMarkerBar),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Chapters
    {
        get => (IEnumerable?)GetValue(ChaptersProperty);
        set => SetValue(ChaptersProperty, value);
    }

    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Duration <= 0
            || ActualWidth <= 0
            || Chapters is null)
            return;
        var brush = TryFindResource("TextBrush") as Brush
                    ?? Brushes.White;
        var pen = new Pen(brush, 1)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        foreach (var chapter in Chapters.OfType<AudioChapter>())
        {
            if (chapter.Start <= TimeSpan.Zero) continue;
            var fraction = Math.Clamp(
                chapter.Start.TotalSeconds / Duration,
                0,
                1);
            var x = Math.Round(fraction * ActualWidth) + 0.5;
            drawingContext.DrawLine(
                pen,
                new Point(x, Math.Max(0, ActualHeight / 2 - 5)),
                new Point(x, Math.Min(ActualHeight, ActualHeight / 2 + 5)));
        }
    }
}
