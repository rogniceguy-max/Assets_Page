using System.Windows;
using System.Windows.Controls;

namespace MuYingDrive.ProjectWorkbooks;

// Wide rows share proportional columns and fixed-width columns; narrow rows stack their labelled fields.
// Every child is measured at the actual available width, so text/images never request a horizontal scroll.
public sealed class ResponsiveColumnsPanel : Panel
{
    public string Weights { get; set; } = "1,1,1,2,3";
    public double Breakpoint { get; set; } = 760;

    private readonly struct ColumnSpec
    {
        public readonly bool IsFixed;
        public readonly double Value;
        public ColumnSpec(bool isFixed, double value) { IsFixed = isFixed; Value = value; }
    }

    private ColumnSpec[] ParseSpecs()
    {
        var tokens = Weights.Split(',');
        var list = new List<ColumnSpec>();
        foreach (var raw in tokens)
        {
            var t = raw.Trim();
            if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(t[..^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px) && px > 0)
                {
                    list.Add(new ColumnSpec(true, px));
                    continue;
                }
            }
            if (t.EndsWith('*'))
            {
                t = t[..^1];
            }
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num) && num > 0)
            {
                list.Add(new ColumnSpec(false, num));
            }
            else
            {
                list.Add(new ColumnSpec(false, 1));
            }
        }
        return list.ToArray();
    }

    private double[] Widths(double totalWidth)
    {
        var specs = ParseSpecs();
        var count = InternalChildren.Count;
        var fullSpecs = new ColumnSpec[count];
        for (int i = 0; i < count; i++)
        {
            fullSpecs[i] = i < specs.Length ? specs[i] : new ColumnSpec(false, 1);
        }

        double fixedTotal = 0;
        double starTotal = 0;
        foreach (var s in fullSpecs)
        {
            if (s.IsFixed) fixedTotal += s.Value;
            else starTotal += s.Value;
        }

        var widths = new double[count];
        if (totalWidth <= fixedTotal || starTotal <= 0)
        {
            double scale = fixedTotal > 0 ? Math.Min(1.0, totalWidth / fixedTotal) : 1.0;
            for (int i = 0; i < count; i++)
            {
                widths[i] = fullSpecs[i].IsFixed ? fullSpecs[i].Value * scale : Math.Max(0, totalWidth - fixedTotal) / Math.Max(1, count);
            }
        }
        else
        {
            double remainingWidth = Math.Max(0, totalWidth - fixedTotal);
            for (int i = 0; i < count; i++)
            {
                widths[i] = fullSpecs[i].IsFixed ? fullSpecs[i].Value : remainingWidth * fullSpecs[i].Value / starTotal;
            }
        }
        return widths;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? Breakpoint : Math.Max(0, availableSize.Width);
        var compact = width < Breakpoint;
        var widths = Widths(width);
        double height = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(compact ? width : widths[i], double.PositiveInfinity));
            height = compact ? height + child.DesiredSize.Height : Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var compact = finalSize.Width < Breakpoint;
        var widths = Widths(finalSize.Width);
        double offset = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Arrange(compact ? new Rect(0, offset, finalSize.Width, child.DesiredSize.Height)
                : new Rect(offset, 0, widths[i], finalSize.Height));
            offset += compact ? child.DesiredSize.Height : widths[i];
        }
        return finalSize;
    }
}
