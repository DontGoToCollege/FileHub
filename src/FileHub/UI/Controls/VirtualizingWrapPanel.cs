using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FileHub.UI.Controls;

/// <summary>
/// Uniform-size virtualizing wrap panel (icon / tile views). Only the visible rows are realized,
/// so folders with 100k items stay snappy in icon mode.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(110.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(110.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private Size _extent, _viewport;
    private Point _offset;
    private int _perRow = 1;

    // ---------------------------------------------------------------- layout

    private int ItemCount
    {
        get
        {
            var owner = ItemsControl.GetItemsOwner(this);
            return owner?.Items.Count ?? 0;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren; // forces generator hookup
        int count = ItemCount;
        double width = double.IsInfinity(availableSize.Width) ? Math.Max(ItemWidth, 800) : availableSize.Width;
        double iw = Math.Max(1, ItemWidth), ih = Math.Max(1, ItemHeight);
        _perRow = Math.Max(1, (int)(width / iw));
        int rows = (count + _perRow - 1) / _perRow;
        var extent = new Size(width, rows * ih);
        var viewport = new Size(width, double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
        bool changed = extent != _extent || viewport != _viewport;
        _extent = extent; _viewport = viewport;
        double maxY = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > maxY) { _offset.Y = maxY; changed = true; }
        if (changed) ScrollOwner?.InvalidateScrollInfo();

        if (count == 0)
        {
            if (children.Count > 0) { RemoveInternalChildRange(0, children.Count); }
            return new Size(width, Math.Min(viewport.Height, extent.Height));
        }

        int firstRow = (int)(_offset.Y / ih);
        int lastRow = (int)((_offset.Y + _viewport.Height) / ih);
        int first = Math.Max(0, firstRow * _perRow);
        int last = Math.Min(count - 1, (lastRow + 1) * _perRow - 1);
        if (first > last) first = last;

        var generator = ItemContainerGenerator;
        var startPos = generator.GeneratorPositionFromIndex(first);
        int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (int i = first; i <= last; i++, childIndex++)
            {
                if (generator.GenerateNext(out bool newlyRealized) is not UIElement child) break;
                if (newlyRealized)
                {
                    if (childIndex >= children.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(iw, ih));
            }
        }
        CleanUp(first, last);
        return new Size(width, Math.Min(viewport.Height, extent.Height));
    }

    private void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            int idx = generator.IndexFromGeneratorPosition(pos);
            if (idx < first || idx > last)
            {
                generator.Remove(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        double iw = Math.Max(1, ItemWidth), ih = Math.Max(1, ItemHeight);
        int perRow = Math.Max(1, (int)(finalSize.Width / iw));
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int idx = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (idx < 0) continue;
            int row = idx / perRow, col = idx % perRow;
            InternalChildren[i].Arrange(new Rect(col * iw, row * ih - _offset.Y, iw, ih));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                if (args.Position.Index >= 0 && args.Position.Index < InternalChildren.Count)
                    RemoveInternalChildRange(args.Position.Index, Math.Min(args.ItemUICount, InternalChildren.Count - args.Position.Index));
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                _offset.Y = 0;
                break;
        }
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0) return;
        double ih = Math.Max(1, ItemHeight);
        int row = index / Math.Max(1, _perRow);
        double top = row * ih, bottom = top + ih;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);
        UpdateLayout();
    }

    // ---------------------------------------------------------------- IScrollInfo

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    private double LineSize => Math.Max(24, ItemHeight / 3);
    public void LineUp() => SetVerticalOffset(_offset.Y - LineSize);
    public void LineDown() => SetVerticalOffset(_offset.Y + LineSize);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - Math.Max(48, ItemHeight * 0.9));
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + Math.Max(48, ItemHeight * 0.9));
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        double max = Math.Max(0, _extent.Height - _viewport.Height);
        offset = Math.Clamp(offset, 0, max);
        if (Math.Abs(offset - _offset.Y) < 0.01) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual == null) return rectangle;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (child == visual || (visual is DependencyObject d && child.IsAncestorOf(d)))
            {
                int idx = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                if (idx >= 0) BringIndexIntoView(idx);
                break;
            }
        }
        return rectangle;
    }
}
