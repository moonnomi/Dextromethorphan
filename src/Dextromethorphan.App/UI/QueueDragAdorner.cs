using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Dextromethorphan.App.UI;

internal sealed class QueueDragAdorner : Adorner
{
    private const double MusicCardWidth = 292;
    private const double MusicCardHeight = 68;
    private readonly VisualCollection _visuals;
    private readonly Grid _preview;
    private readonly TranslateTransform _position = new();

    public QueueDragAdorner(
        UIElement adornedElement,
        FrameworkElement source,
        int itemCount)
        : this(
            adornedElement,
            CreateVisualPreview(adornedElement, source),
            itemCount)
    {
    }

    public QueueDragAdorner(
        UIElement adornedElement,
        string title,
        string subtitle,
        string? artworkPath,
        string initial,
        int itemCount)
        : this(
            adornedElement,
            CreateMusicCard(title, subtitle, artworkPath, initial),
            itemCount)
    {
    }

    private QueueDragAdorner(
        UIElement adornedElement,
        PreviewContent content,
        int itemCount)
        : base(adornedElement)
    {
        _visuals = new VisualCollection(this);
        IsHitTestVisible = false;

        _preview = new Grid
        {
            Width = content.Width,
            Height = content.Height,
            Opacity = 0,
            RenderTransform = _position,
            RenderTransformOrigin = new Point(0, 0)
        };
        _preview.Children.Add(content.Visual);
        if (itemCount > 1)
            _preview.Children.Add(CreateCountBadge(itemCount));
        _visuals.Add(_preview);
    }

    public void MoveTo(Point pointer, Point grip)
    {
        _position.X = pointer.X - grip.X;
        _position.Y = pointer.Y - grip.Y;
    }

    public void Show(bool animate)
    {
        _preview.BeginAnimation(OpacityProperty, null);
        _preview.Opacity = 0.96;
        if (!animate) return;
        _preview.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 0.96, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private static PreviewContent CreateVisualPreview(
        UIElement adornedElement,
        FrameworkElement source)
    {
        var width = Math.Max(
            1,
            Math.Min(source.ActualWidth, adornedElement.RenderSize.Width));
        var height = Math.Max(1, source.ActualHeight);
        var visual = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 27, 35)),
            CornerRadius = new CornerRadius(10),
            Child = new Border
            {
                Background = new VisualBrush(source)
                {
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    Stretch = Stretch.Fill
                },
                CornerRadius = new CornerRadius(10)
            },
            Effect = PreviewShadow()
        };
        return new PreviewContent(visual, width, height);
    }

    private static PreviewContent CreateMusicCard(
        string title,
        string subtitle,
        string? artworkPath,
        string initial)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        AsyncArtwork.SetDecodePixelWidth(image, 96);
        AsyncArtwork.SetPriority(image, ArtworkRequestPriority.Visible);
        AsyncArtwork.SetPath(image, artworkPath);

        var artwork = new Border
        {
            Width = 48,
            Height = 48,
            Margin = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(37, 42, 53)),
            Child = new Grid
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(initial) ? "♪" : initial,
                        Foreground = new SolidColorBrush(Color.FromRgb(177, 187, 211)),
                        FontSize = 18,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    image
                }
            }
        };

        var text = new StackPanel
        {
            Margin = new Thickness(2, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(title) ? "Untitled track" : title,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(subtitle) ? "Unknown artist" : subtitle,
                    Foreground = new SolidColorBrush(Color.FromRgb(168, 176, 197)),
                    FontSize = 11,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(artwork);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        var visual = new Border
        {
            Width = MusicCardWidth,
            Height = MusicCardHeight,
            Background = new SolidColorBrush(Color.FromRgb(24, 27, 35)),
            CornerRadius = new CornerRadius(12),
            Child = content,
            Effect = PreviewShadow()
        };
        return new PreviewContent(visual, MusicCardWidth, MusicCardHeight);
    }

    private static Border CreateCountBadge(int itemCount) => new()
    {
        MinWidth = 25,
        Height = 25,
        Margin = new Thickness(0, -7, -7, 0),
        Padding = new Thickness(7, 0, 7, 0),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Background = new SolidColorBrush(Color.FromRgb(130, 144, 255)),
        CornerRadius = new CornerRadius(12.5),
        Child = new TextBlock
        {
            Text = itemCount.ToString(),
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static DropShadowEffect PreviewShadow() => new()
    {
        BlurRadius = 22,
        ShadowDepth = 8,
        Direction = 270,
        Color = Colors.Black,
        Opacity = 0.48
    };

    protected override int VisualChildrenCount => _visuals is null ? 0 : _visuals.Count;

    protected override Visual GetVisualChild(int index) =>
        _visuals is null
            ? throw new ArgumentOutOfRangeException(nameof(index))
            : _visuals[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _preview.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _preview.Arrange(new Rect(new Point(), _preview.DesiredSize));
        return finalSize;
    }

    private sealed record PreviewContent(
        FrameworkElement Visual,
        double Width,
        double Height);
}
