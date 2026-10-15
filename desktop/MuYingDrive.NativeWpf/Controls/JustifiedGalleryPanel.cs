using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MuYingDrive.NativeWpf.Controls;

/// <summary>
/// A vertically virtualized, justified-row panel for an ItemsControl.
/// Item containers are generated only for rows in the viewport plus one row of overscan.
/// </summary>
public sealed class JustifiedGalleryPanel : VirtualizingPanel, IScrollInfo
{
    private const double Gap = 12d;
    private const double MinimumLastRowHeight = 82d;
    private const double MinimumRatio = 0.45d;
    private const double MaximumRatio = 3.4d;
    private const int OverscanRows = 1;
    private const double LineScrollAmount = 42d;

    private static readonly ConcurrentDictionary<Type, RatioAccessor> RatioAccessors = new();

    private readonly List<GalleryRow> _rows = [];
    private double[] _ratios = [];
    private int[] _rowByItem = [];
    private double _layoutWidth;
    private double _extentHeight;
    private double _viewportHeight;
    private double _verticalOffset;
    private double _horizontalOffset;
    private int _realizedStart = -1;
    private int _realizedCount;
    private bool _layoutValid;
    private bool _canHorizontallyScroll;
    private bool _canVerticallyScroll = true;

    public JustifiedGalleryPanel()
    {
        ClipToBounds = true;
    }

    public bool CanHorizontallyScroll
    {
        get => _canHorizontallyScroll;
        set
        {
            if (_canHorizontallyScroll == value)
            {
                return;
            }

            _canHorizontallyScroll = value;
            if (!value)
            {
                SetHorizontalOffset(0d);
            }

            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public bool CanVerticallyScroll
    {
        get => _canVerticallyScroll;
        set
        {
            if (_canVerticallyScroll == value)
            {
                return;
            }

            _canVerticallyScroll = value;
            if (!value)
            {
                SetVerticalOffset(0d);
            }

            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public double ExtentWidth => _layoutWidth;

    public double ExtentHeight => _extentHeight;

    public double ViewportWidth => _layoutWidth;

    public double ViewportHeight => _viewportHeight;

    public double HorizontalOffset => _horizontalOffset;

    public double VerticalOffset => _verticalOffset;

    public ScrollViewer? ScrollOwner { get; set; }

    /// <summary>
    /// Call after changing an item's AspectRatio/Ratio without changing the Items collection.
    /// </summary>
    public void InvalidateLayout()
    {
        _layoutValid = false;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null)
        {
            RemoveAllRealizedContainers(generator: null, removeFromGenerator: false);
            _rows.Clear();
            _ratios = [];
            _rowByItem = [];
            _layoutWidth = 0d;
            _extentHeight = 0d;
            _viewportHeight = 0d;
            _verticalOffset = 0d;
            ScrollOwner?.InvalidateScrollInfo();
            return new Size();
        }

        var width = ResolveWidth(availableSize.Width, owner);
        var viewportHeight = ResolveViewportHeight(availableSize.Height);

        if (!_layoutValid || Math.Abs(width - _layoutWidth) > 0.5d || _ratios.Length != owner.Items.Count)
        {
            BuildRows(owner, width);
        }

        _viewportHeight = viewportHeight;
        _verticalOffset = Clamp(_verticalOffset, 0d, Math.Max(0d, _extentHeight - _viewportHeight));
        _horizontalOffset = 0d;
        ScrollOwner?.InvalidateScrollInfo();

        // Use the generator attached to this panel. ItemsControl.ItemContainerGenerator can
        // describe the owner's collection while this panel has a different realized range.
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            RemoveAllRealizedContainers(generator: null, removeFromGenerator: false);
            return new Size(width, Math.Min(_extentHeight, _viewportHeight));
        }
        if (_rows.Count == 0 || _ratios.Length == 0)
        {
            UpdateRealizedRange(generator, 0, -1);
        }
        else
        {
            GetRealizationRange(_verticalOffset, _viewportHeight, out var firstIndex, out var lastIndex);
            UpdateRealizedRange(generator, firstIndex, lastIndex);
        }

        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var itemIndex = GetItemIndexForChild(generator, childIndex);
            if ((uint)itemIndex >= (uint)_ratios.Length)
            {
                InternalChildren[childIndex].Measure(new Size());
                continue;
            }

            var row = _rows[_rowByItem[itemIndex]];
            var localIndex = itemIndex - row.StartIndex;
            InternalChildren[childIndex].Measure(new Size(GetItemWidth(row, localIndex), row.Height));
        }

        var desiredHeight = Math.Min(_extentHeight, _viewportHeight);
        if (_extentHeight == 0d)
        {
            desiredHeight = 0d;
        }

        return new Size(width, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var itemIndex = generator is null ? -1 : GetItemIndexForChild(generator, childIndex);
            if ((uint)itemIndex >= (uint)_ratios.Length)
            {
                InternalChildren[childIndex].Arrange(Rect.Empty);
                continue;
            }

            var row = _rows[_rowByItem[itemIndex]];
            var localIndex = itemIndex - row.StartIndex;
            var x = 0d;
            for (var previousIndex = 0; previousIndex < localIndex; previousIndex++)
            {
                x += GetItemWidth(row, previousIndex) + Gap;
            }

            var rect = new Rect(
                x,
                row.Top - _verticalOffset,
                GetItemWidth(row, localIndex),
                row.Height);
            InternalChildren[childIndex].Arrange(rect);
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        // Collection edits can shift every later item index. Clearing the small realized
        // window keeps the generator and our contiguous index range in sync.
        var generator = ItemContainerGenerator;
        RemoveAllRealizedContainers(
            generator,
            removeFromGenerator: generator is not null && args.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
        _realizedStart = -1;
        _realizedCount = 0;
        _layoutValid = false;
        InvalidateMeasure();
    }

    public void LineUp() => SetVerticalOffset(_verticalOffset - LineScrollAmount);

    public void LineDown() => SetVerticalOffset(_verticalOffset + LineScrollAmount);

    public void LineLeft() => SetHorizontalOffset(_horizontalOffset - LineScrollAmount);

    public void LineRight() => SetHorizontalOffset(_horizontalOffset + LineScrollAmount);

    public void PageUp() => SetVerticalOffset(_verticalOffset - Math.Max(LineScrollAmount, _viewportHeight - LineScrollAmount));

    public void PageDown() => SetVerticalOffset(_verticalOffset + Math.Max(LineScrollAmount, _viewportHeight - LineScrollAmount));

    public void PageLeft() => SetHorizontalOffset(_horizontalOffset - Math.Max(LineScrollAmount, _layoutWidth - LineScrollAmount));

    public void PageRight() => SetHorizontalOffset(_horizontalOffset + Math.Max(LineScrollAmount, _layoutWidth - LineScrollAmount));

    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - GetMouseWheelAmount());

    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + GetMouseWheelAmount());

    public void MouseWheelLeft() => SetHorizontalOffset(_horizontalOffset - GetMouseWheelAmount());

    public void MouseWheelRight() => SetHorizontalOffset(_horizontalOffset + GetMouseWheelAmount());

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0)
        {
            return;
        }

        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is not null && (!_layoutValid || _rowByItem.Length != owner.Items.Count))
        {
            BuildRows(owner, ResolveWidth(ActualWidth, owner));
        }

        if ((uint)index >= (uint)_rowByItem.Length)
        {
            return;
        }

        var row = _rows[_rowByItem[index]];
        if (row.Top < _verticalOffset)
        {
            SetVerticalOffset(row.Top);
        }
        else if (row.Top + row.Height > _verticalOffset + _viewportHeight)
        {
            SetVerticalOffset(row.Top + row.Height - _viewportHeight);
        }
    }

    public void SetHorizontalOffset(double offset)
    {
        if (!CanHorizontallyScroll)
        {
            offset = 0d;
        }

        var nextOffset = Clamp(offset, 0d, Math.Max(0d, ExtentWidth - ViewportWidth));
        if (Math.Abs(nextOffset - _horizontalOffset) < 0.1d)
        {
            return;
        }

        _horizontalOffset = nextOffset;
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetVerticalOffset(double offset)
    {
        if (!CanVerticallyScroll)
        {
            offset = 0d;
        }

        var nextOffset = Clamp(offset, 0d, Math.Max(0d, ExtentHeight - ViewportHeight));
        if (Math.Abs(nextOffset - _verticalOffset) < 0.1d)
        {
            return;
        }

        _verticalOffset = nextOffset;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var itemIndex = FindRealizedItemIndex(visual);
        if (itemIndex < 0 || itemIndex >= _rowByItem.Length)
        {
            return Rect.Empty;
        }

        var row = _rows[_rowByItem[itemIndex]];
        if (row.Top < _verticalOffset)
        {
            SetVerticalOffset(row.Top);
        }
        else if (row.Top + row.Height > _verticalOffset + _viewportHeight)
        {
            SetVerticalOffset(row.Top + row.Height - _viewportHeight);
        }

        var localIndex = itemIndex - row.StartIndex;
        var x = 0d;
        for (var previousIndex = 0; previousIndex < localIndex; previousIndex++)
        {
            x += GetItemWidth(row, previousIndex) + Gap;
        }

        return new Rect(x, row.Top - _verticalOffset, GetItemWidth(row, localIndex), row.Height);
    }

    private void BuildRows(ItemsControl owner, double width)
    {
        _layoutWidth = width;
        _rows.Clear();

        var itemCount = owner.Items.Count;
        _ratios = new double[itemCount];
        _rowByItem = new int[itemCount];
        for (var index = 0; index < itemCount; index++)
        {
            _ratios[index] = ReadAspectRatio(owner.Items[index]);
        }

        var targetHeight = GetTargetRowHeight(width);
        var nextTop = 0d;
        var rowStart = 0;

        while (rowStart < itemCount)
        {
            var rowCount = 0;
            var ratioSum = 0d;
            var closedAsFullRow = false;

            while (rowStart + rowCount < itemCount)
            {
                ratioSum += _ratios[rowStart + rowCount];
                rowCount++;

                var projectedHeight = GetProjectedHeight(width, rowCount, ratioSum);
                var hasMoreItems = rowStart + rowCount < itemCount;
                if (rowCount >= 2 && projectedHeight <= targetHeight && hasMoreItems)
                {
                    AddRow(rowStart, rowCount, projectedHeight, isJustified: true, ref nextTop);
                    rowStart += rowCount;
                    closedAsFullRow = true;
                    break;
                }
            }

            if (closedAsFullRow)
            {
                continue;
            }

            // The trailing row keeps the target height and its natural aspect-ratio widths.
            // It is only reduced when those widths would exceed the available gallery width.
            var trailingProjectedHeight = GetProjectedHeight(width, rowCount, ratioSum);
            var naturalHeight = Math.Min(targetHeight, trailingProjectedHeight);
            var lastRowHeight = Math.Min(Math.Max(MinimumLastRowHeight, naturalHeight), trailingProjectedHeight);
            AddRow(rowStart, rowCount, lastRowHeight, isJustified: false, ref nextTop);
            rowStart += rowCount;
        }

        _extentHeight = _rows.Count == 0 ? 0d : _rows[^1].Top + _rows[^1].Height;
        _verticalOffset = Clamp(_verticalOffset, 0d, Math.Max(0d, _extentHeight - _viewportHeight));
        _layoutValid = true;
    }

    private void AddRow(int startIndex, int count, double height, bool isJustified, ref double nextTop)
    {
        if (_rows.Count > 0)
        {
            nextTop += Gap;
        }

        var row = new GalleryRow(startIndex, count, Math.Max(0d, height), nextTop, isJustified);
        var rowIndex = _rows.Count;
        _rows.Add(row);
        for (var index = startIndex; index < startIndex + count; index++)
        {
            _rowByItem[index] = rowIndex;
        }

        nextTop += row.Height;
    }

    private double GetItemWidth(GalleryRow row, int localIndex)
    {
        if (localIndex < 0 || localIndex >= row.Count)
        {
            return 0d;
        }

        if (row.IsJustified && localIndex == row.Count - 1)
        {
            var usedWidth = 0d;
            for (var index = 0; index < row.Count - 1; index++)
            {
                usedWidth += _ratios[row.StartIndex + index] * row.Height + Gap;
            }

            return Math.Max(0d, _layoutWidth - usedWidth);
        }

        return _ratios[row.StartIndex + localIndex] * row.Height;
    }

    private double GetProjectedHeight(double width, int count, double ratioSum)
    {
        if (count <= 0 || ratioSum <= 0d)
        {
            return 0d;
        }

        var contentWidth = Math.Max(0d, width - Gap * (count - 1));
        return contentWidth / ratioSum;
    }

    private void GetRealizationRange(double offset, double viewportHeight, out int firstIndex, out int lastIndex)
    {
        var visibleBottom = offset + Math.Max(viewportHeight, 1d);
        var low = 0;
        var high = _rows.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_rows[middle].Top + _rows[middle].Height <= offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        var firstRow = low;

        if (firstRow >= _rows.Count)
        {
            firstRow = _rows.Count - 1;
        }

        var lastRow = firstRow;
        while (lastRow + 1 < _rows.Count && _rows[lastRow + 1].Top < visibleBottom)
        {
            lastRow++;
        }

        firstRow = Math.Max(0, firstRow - OverscanRows);
        lastRow = Math.Min(_rows.Count - 1, lastRow + OverscanRows);
        firstIndex = _rows[firstRow].StartIndex;
        var last = _rows[lastRow];
        lastIndex = last.StartIndex + last.Count - 1;
    }

    private void UpdateRealizedRange(IItemContainerGenerator generator, int firstIndex, int lastIndex)
    {
        if (!IsRealizedRangeSynchronized(generator))
        {
            // Repair stale bookkeeping before using it to virtualize anything. The generator
            // is authoritative; removal positions below are relative to realized children.
            RemoveAllRealizedContainers(generator, removeFromGenerator: true);
        }

        if (firstIndex < 0 || lastIndex < firstIndex)
        {
            RemoveAllRealizedContainers(generator, removeFromGenerator: true);
            _realizedStart = -1;
            _realizedCount = 0;
            return;
        }

        if (_realizedCount > 0)
        {
            var currentLast = _realizedStart + _realizedCount - 1;
            if (lastIndex < _realizedStart || firstIndex > currentLast)
            {
                RemoveAllRealizedContainers(generator, removeFromGenerator: true);
                _realizedStart = -1;
                _realizedCount = 0;
            }
        }

        if (_realizedCount > 0 && _realizedStart < firstIndex)
        {
            var removeCount = Math.Min(firstIndex - _realizedStart, _realizedCount);
            RemoveRealizedRange(generator, 0, removeCount);
            _realizedStart += removeCount;
            _realizedCount -= removeCount;
            if (_realizedCount == 0)
            {
                _realizedStart = -1;
            }
        }

        if (_realizedCount > 0)
        {
            var currentLast = _realizedStart + _realizedCount - 1;
            if (currentLast > lastIndex)
            {
                var removeCount = currentLast - lastIndex;
                RemoveRealizedRange(generator, _realizedCount - removeCount, removeCount);
                _realizedCount -= removeCount;
            }
        }

        if (_realizedCount == 0)
        {
            _realizedStart = firstIndex;
            _realizedCount = GenerateRange(generator, firstIndex, lastIndex);
            return;
        }

        if (firstIndex < _realizedStart)
        {
            var prefixCount = GenerateRange(generator, firstIndex, _realizedStart - 1);
            if (prefixCount > 0)
            {
                _realizedStart = firstIndex;
                _realizedCount += prefixCount;
            }
        }

        var realizedLast = _realizedStart + _realizedCount - 1;
        if (lastIndex > realizedLast)
        {
            var suffixStart = realizedLast + 1;
            var suffixCount = GenerateRange(generator, suffixStart, lastIndex);
            _realizedCount += suffixCount;
        }
    }

    private int GenerateRange(IItemContainerGenerator generator, int firstIndex, int lastIndex)
    {
        if (firstIndex > lastIndex)
        {
            return 0;
        }

        var startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        // GeneratorPosition.Index is a realized-child index. Offset > 0 means the
        // requested item is unrealized and belongs immediately after the preceding child.
        var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;
        childIndex = Math.Clamp(childIndex, 0, InternalChildren.Count);
        var generatedCount = 0;
        using (generator.StartAt(startPosition, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++)
            {
                if (generator.GenerateNext(out var isNewlyRealized) is not UIElement child)
                {
                    // Stop at the first missing container. A partial contiguous range is safe
                    // and can be completed on the next measure pass; skipping would create a
                    // hole in the realized range and corrupt subsequent child indices.
                    return generatedCount;
                }

                var existingChildIndex = InternalChildren.IndexOf(child);
                var wasAlreadyInPanel = existingChildIndex >= 0;
                var needsInsert = !wasAlreadyInPanel;
                if (wasAlreadyInPanel && existingChildIndex != childIndex)
                {
                    RemoveInternalChildRange(existingChildIndex, 1);
                    if (existingChildIndex < childIndex)
                    {
                        childIndex--;
                    }

                    needsInsert = true;
                }

                if (needsInsert)
                {
                    InsertInternalChild(Math.Min(childIndex, InternalChildren.Count), child);
                }

                if (isNewlyRealized || !wasAlreadyInPanel)
                {
                    generator.PrepareItemContainer(child);
                }

                childIndex++;
                generatedCount++;
            }
        }

        return generatedCount;
    }

    private void RemoveRealizedRange(IItemContainerGenerator generator, int childIndex, int count)
    {
        if (count <= 0 || childIndex < 0 || childIndex + count > InternalChildren.Count)
        {
            return;
        }

        // Remove from the end so each local child position stays valid as the sequence shifts.
        // Generator positions passed to Recycle/Remove are child indices, never item indices.
        for (var index = childIndex + count - 1; index >= childIndex; index--)
        {
            var position = new GeneratorPosition(index, 0);
            if (generator.IndexFromGeneratorPosition(position) >= 0)
            {
                RecycleOrRemove(generator, position, 1);
            }

            RemoveInternalChildRange(index, 1);
        }
    }

    private void RemoveAllRealizedContainers(IItemContainerGenerator? generator, bool removeFromGenerator)
    {
        if (removeFromGenerator && generator is not null)
        {
            RemoveRealizedRange(generator, 0, InternalChildren.Count);
        }
        else if (InternalChildren.Count > 0)
        {
            RemoveInternalChildRange(0, InternalChildren.Count);
        }

        _realizedStart = -1;
        _realizedCount = 0;
    }

    private static void RecycleOrRemove(IItemContainerGenerator generator, GeneratorPosition position, int count)
    {
        if (generator is IRecyclingItemContainerGenerator recyclingGenerator)
        {
            recyclingGenerator.Recycle(position, count);
        }
        else
        {
            generator.Remove(position, count);
        }
    }

    private bool IsRealizedRangeSynchronized(IItemContainerGenerator generator)
    {
        if (_realizedCount != InternalChildren.Count)
        {
            return false;
        }

        if (InternalChildren.Count == 0)
        {
            return _realizedCount == 0;
        }

        var firstIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(0, 0));
        if (firstIndex < 0)
        {
            return false;
        }

        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex != firstIndex + childIndex)
            {
                return false;
            }
        }

        _realizedStart = firstIndex;
        _realizedCount = InternalChildren.Count;
        return true;
    }

    private static int GetItemIndexForChild(IItemContainerGenerator generator, int childIndex)
    {
        return generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
    }

    private int FindRealizedItemIndex(Visual visual)
    {
        DependencyObject? current = visual;
        while (current is not null && current != this)
        {
            if (current is UIElement element)
            {
                var childIndex = InternalChildren.IndexOf(element);
                if (childIndex >= 0)
                {
                    var generator = ItemContainerGenerator;
                    return generator is null ? -1 : GetItemIndexForChild(generator, childIndex);
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return -1;
    }

    private double ResolveWidth(double availableWidth, ItemsControl owner)
    {
        if (!double.IsNaN(availableWidth) && !double.IsInfinity(availableWidth) && availableWidth > 0d)
        {
            return availableWidth;
        }

        if (ActualWidth > 0d)
        {
            return ActualWidth;
        }

        if (owner.ActualWidth > 0d)
        {
            return owner.ActualWidth;
        }

        return 800d;
    }

    private double ResolveViewportHeight(double availableHeight)
    {
        if (!double.IsNaN(availableHeight) && !double.IsInfinity(availableHeight) && availableHeight > 0d)
        {
            return availableHeight;
        }

        return ActualHeight > 0d ? ActualHeight : 600d;
    }

    private double ReadAspectRatio(object? item)
    {
        if (item is null)
        {
            return 1d;
        }

        var model = item is FrameworkElement element ? element.DataContext ?? item : item;
        if (model is null)
        {
            return 1d;
        }

        var accessor = RatioAccessors.GetOrAdd(model.GetType(), static type =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var property = type.GetProperty("AspectRatio", flags) ?? type.GetProperty("Ratio", flags);
            return new RatioAccessor(property);
        });

        if (accessor.Property?.CanRead == true)
        {
            try
            {
                var value = accessor.Property.GetValue(model);
                var ratio = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(ratio) && ratio > 0d)
                {
                    return Clamp(ratio, MinimumRatio, MaximumRatio);
                }
            }
            catch (Exception exception) when (exception is InvalidCastException or FormatException or
                                               TargetInvocationException or ArgumentException or
                                               MethodAccessException or OverflowException)
            {
                // Invalid or unavailable metadata uses the square-card fallback.
            }
        }

        return 1d;
    }

    private static double GetTargetRowHeight(double width)
    {
        if (width <= 520d)
        {
            return 126d;
        }

        if (width <= 900d)
        {
            return 142d;
        }

        if (width <= 1500d)
        {
            return 158d;
        }

        return 166d;
    }

    private double GetMouseWheelAmount()
    {
        var lines = SystemParameters.WheelScrollLines;
        return lines < 0 ? Math.Max(LineScrollAmount, _viewportHeight - LineScrollAmount) : Math.Max(1, lines) * LineScrollAmount;
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value))
        {
            return minimum;
        }

        return Math.Max(minimum, Math.Min(maximum, value));
    }

    private readonly record struct GalleryRow(int StartIndex, int Count, double Height, double Top, bool IsJustified);

    private readonly record struct RatioAccessor(PropertyInfo? Property);
}
