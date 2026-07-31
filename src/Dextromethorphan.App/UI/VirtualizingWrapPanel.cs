using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Dextromethorphan.App.UI;

public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    private static readonly DependencyProperty RealizedIndexProperty =
        DependencyProperty.RegisterAttached(
            "RealizedIndex",
            typeof(int),
            typeof(VirtualizingWrapPanel),
            new PropertyMetadata(-1));

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(186d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(266d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _itemsPerRow = 1;

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        var itemCount = owner?.Items.Count ?? 0;
        var itemWidth = Math.Max(1, ItemWidth);
        var itemHeight = Math.Max(1, ItemHeight);
        var viewportWidth = double.IsInfinity(availableSize.Width) ? Math.Max(itemWidth, ScrollOwner?.ViewportWidth ?? itemWidth) : Math.Max(1, availableSize.Width);
        var viewportHeight = double.IsInfinity(availableSize.Height) ? Math.Max(itemHeight, ScrollOwner?.ViewportHeight ?? itemHeight) : Math.Max(1, availableSize.Height);
        _viewport = new Size(viewportWidth, viewportHeight);
        _itemsPerRow = Math.Max(1, (int)Math.Floor(viewportWidth / itemWidth));
        var rows = itemCount == 0 ? 0 : (int)Math.Ceiling(itemCount / (double)_itemsPerRow);
        UpdateExtent(new Size(viewportWidth, rows * itemHeight));
        SetVerticalOffset(_offset.Y);

        if (itemCount == 0)
        {
            CleanupItems(0, -1);
            return availableSize;
        }

        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / itemHeight) - 1);
        var visibleRows = Math.Max(1, (int)Math.Ceiling(viewportHeight / itemHeight) + 2);
        var startIndex = Math.Min(itemCount - 1, firstRow * _itemsPerRow);
        var endIndex = Math.Min(itemCount - 1, ((firstRow + visibleRows) * _itemsPerRow) - 1);
        CleanupItems(startIndex, endIndex);
        RealizeItems(startIndex, endIndex, new Size(itemWidth, itemHeight));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var itemWidth = Math.Max(1, ItemWidth);
        var itemHeight = Math.Max(1, ItemHeight);
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var child = InternalChildren[childIndex];
            var itemIndex = (int)child.GetValue(RealizedIndexProperty);
            if (itemIndex < 0)
                itemIndex = generator.IndexFromGeneratorPosition(
                    new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0) continue;
            var row = itemIndex / _itemsPerRow;
            var column = itemIndex % _itemsPerRow;
            child.Arrange(new Rect(
                column * itemWidth,
                (row * itemHeight) - _offset.Y,
                itemWidth,
                itemHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0) return;
        SetVerticalOffset((index / Math.Max(1, _itemsPerRow)) * Math.Max(1, ItemHeight));
    }

    private void RealizeItems(int startIndex, int endIndex, Size childSize)
    {
        var generator = ItemContainerGenerator;
        var startPosition = generator.GeneratorPositionFromIndex(startIndex);
        var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;
        using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
        {
            for (var itemIndex = startIndex; itemIndex <= endIndex; itemIndex++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var newlyRealized);
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                // Generator-position lookups can be transiently unavailable
                // while WPF removes and recreates a distant virtualized range.
                // Keep the authoritative source index on the container so an
                // otherwise valid card can never be skipped by ArrangeOverride.
                child.SetValue(RealizedIndexProperty, itemIndex);
                child.Measure(childSize);
            }
        }
    }

    private void CleanupItems(int startIndex, int endIndex)
    {
        var generator = ItemContainerGenerator;
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var child = InternalChildren[childIndex];
            var position = new GeneratorPosition(childIndex, 0);
            var itemIndex = (int)child.GetValue(RealizedIndexProperty);
            if (itemIndex < 0)
                itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex >= startIndex && itemIndex <= endIndex) continue;
            // WPF's recycling generator can return a reused ListBoxItem before
            // its template bindings and attached artwork state have caught up
            // with the new data item.  In a wrap panel that realizes disjoint
            // ranges after a large pixel scroll, that can leave the viewport
            // with zero prepared containers or Images cancelled by Unloaded.
            //
            // Removing containers is still virtualized (only the buffered
            // visible rows exist), but makes each realization deterministic:
            // a fresh container is prepared and its artwork request starts on
            // Loaded.  Correctness matters more here than retaining a pool of
            // roughly 20 very small ListBoxItems.
            child.ClearValue(RealizedIndexProperty);
            generator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void UpdateExtent(Size value)
    {
        if (_extent == value) return;
        _extent = value;
        ScrollOwner?.InvalidateScrollInfo();
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(VerticalOffset - 32);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 32);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 96);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 96);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() { }
    public void PageRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        var maximum = Math.Max(0, ExtentHeight - ViewportHeight);
        var clamped = Math.Clamp(offset, 0, maximum);
        if (Math.Abs(clamped - _offset.Y) < 0.01) return;
        var itemHeight = Math.Max(1, ItemHeight);
        var previousRow = (int)Math.Floor(_offset.Y / itemHeight);
        var nextRow = (int)Math.Floor(clamped / itemHeight);
        _offset.Y = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        if (previousRow == nextRow)
            InvalidateArrange();
        else
            InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement child) return rectangle;
        var childIndex = InternalChildren.IndexOf(child);
        if (childIndex < 0) return rectangle;
        var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        if (itemIndex < 0) return rectangle;
        var top = (itemIndex / Math.Max(1, _itemsPerRow)) * Math.Max(1, ItemHeight);
        var bottom = top + Math.Max(1, ItemHeight);
        if (top < VerticalOffset) SetVerticalOffset(top);
        else if (bottom > VerticalOffset + ViewportHeight) SetVerticalOffset(bottom - ViewportHeight);
        return new Rect(0, top, Math.Max(1, ItemWidth), Math.Max(1, ItemHeight));
    }
}
