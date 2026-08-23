using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Dextromethorphan.App.UI;

internal sealed class QueueDragAdorner : Adorner
{
    private readonly VisualCollection _visuals;
    private readonly Grid _preview;
    private readonly TranslateTransform _position = new();

    public QueueDragAdorner(
        UIElement adornedElement,
        FrameworkElement source,
        int itemCount)
        : base(adornedElement)
    {
        _visuals = new VisualCollection(this);
        IsHitTestVisible = false;

        var width = Math.Max(1, Math.Min(source.ActualWidth, adornedElement.RenderSize.Width));
        var height = Math.Max(1, source.ActualHeight);
        var cardVisual = new Border
        {
            Background = new VisualBrush(source)
            {
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Stretch = Stretch.Fill
            },
            CornerRadius = new CornerRadius(10)
        };
        var surface = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 27, 35)),
            CornerRadius = new CornerRadius(10),
            Child = cardVisual,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 8,
                Direction = 270,
                Color = Colors.Black,
                Opacity = 0.48
            }
        };

        _preview = new Grid
        {
            Width = width,
            Height = height,
            Opacity = 0,
            RenderTransform = _position,
            RenderTransformOrigin = new Point(0, 0)
        };
        _preview.Children.Add(surface);

        if (itemCount > 1)
        {
            var countBadge = new Border
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
            _preview.Children.Add(countBadge);
        }

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
        _preview.Opacity = 1;
        if (!animate) return;
        _preview.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 0.96, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    // WPF can query the visual tree while dependency properties are still being
    // initialized. Keep the override safe even if a base-class callback runs
    // before this constructor has assigned the collection.
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
}
