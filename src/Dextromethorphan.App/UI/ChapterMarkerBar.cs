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

    public static readonly DependencyProperty BookmarksProperty =
        DependencyProperty.Register(
            nameof(Bookmarks),
            typeof(IEnumerable),
            typeof(ChapterMarkerBar),
            new FrameworkPropertyMetadata(
                null,
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

    public IEnumerable? Bookmarks
    {
        get => (IEnumerable?)GetValue(BookmarksProperty);
        set => SetValue(BookmarksProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Duration <= 0 || ActualWidth <= 0)
            return;
        var brush = TryFindResource("TextBrush") as Brush
                    ?? Brushes.White;
        var pen = new Pen(brush, 1)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        foreach (var chapter in Chapters?.OfType<AudioChapter>() ?? [])
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
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.MediumPurple;
        foreach (var bookmark in Bookmarks?.OfType<PlaybackBookmark>() ?? [])
        {
            var fraction = Math.Clamp(bookmark.Position.TotalSeconds / Duration, 0, 1);
            var x = Math.Round(fraction * ActualWidth);
            var y = Math.Max(1, ActualHeight / 2 - 7);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(x, y), true, true);
                context.LineTo(new Point(x - 4, y - 5), true, false);
                context.LineTo(new Point(x + 4, y - 5), true, false);
            }
            drawingContext.DrawGeometry(accent, null, geometry);
        }
    }
}
