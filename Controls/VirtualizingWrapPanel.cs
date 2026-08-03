using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Picall.Controls;

public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(194d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(244d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemsControl = ItemsControl.GetItemsOwner(this);
        if (itemsControl is null || itemsControl.Items.Count == 0)
        {
            UpdateScrollInfo(availableSize, 0, 1);
            return availableSize;
        }

        var width = double.IsInfinity(availableSize.Width) ? Math.Max(ItemWidth, ActualWidth) : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;
        var columns = Math.Max(1, (int)Math.Floor(width / ItemWidth));
        var rowCount = (int)Math.Ceiling(itemsControl.Items.Count / (double)columns);
        UpdateScrollInfo(new Size(width, height), rowCount * ItemHeight, columns);

        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / ItemHeight) - 1);
        var visibleRows = (int)Math.Ceiling(height / ItemHeight) + 3;
        var firstIndex = Math.Min(itemsControl.Items.Count - 1, firstRow * columns);
        var lastIndex = Math.Min(itemsControl.Items.Count - 1, (firstRow + visibleRows) * columns - 1);

        CleanUpItems(firstIndex, lastIndex);
        var generator = ItemContainerGenerator;
        var startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;
        using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var newlyRealized)!;
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, (int)Math.Floor(finalSize.Width / ItemWidth));
        var horizontalInset = Math.Max(0, (finalSize.Width - columns * ItemWidth) / 2);
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0) continue;
            var row = itemIndex / columns;
            var column = itemIndex % columns;
            InternalChildren[childIndex].Arrange(new Rect(
                horizontalInset + column * ItemWidth,
                row * ItemHeight - _offset.Y,
                ItemWidth,
                ItemHeight));
        }
        return finalSize;
    }

    private void CleanUpItems(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex) continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private void UpdateScrollInfo(Size viewport, double extentHeight, int columns)
    {
        var nextViewport = new Size(viewport.Width, viewport.Height);
        var nextExtent = new Size(viewport.Width, Math.Max(viewport.Height, extentHeight));
        if (nextViewport != _viewport || nextExtent != _extent)
        {
            _viewport = nextViewport;
            _extent = nextExtent;
            _offset.Y = Math.Clamp(_offset.Y, 0, Math.Max(0, _extent.Height - _viewport.Height));
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public void LineUp() => SetVerticalOffset(VerticalOffset - 48);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 48);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight * 0.9);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight * 0.9);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 110);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 110);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (Math.Abs(offset - _offset.Y) < 0.1) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var childIndex = InternalChildren.IndexOf((UIElement)visual);
        if (childIndex < 0) return rectangle;
        var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        var columns = Math.Max(1, (int)Math.Floor(ViewportWidth / ItemWidth));
        var top = itemIndex / columns * ItemHeight;
        if (top < VerticalOffset) SetVerticalOffset(top);
        else if (top + ItemHeight > VerticalOffset + ViewportHeight) SetVerticalOffset(top + ItemHeight - ViewportHeight);
        return rectangle;
    }
}
