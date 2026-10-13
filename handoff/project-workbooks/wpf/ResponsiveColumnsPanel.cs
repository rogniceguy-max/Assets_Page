using System.Windows;
using System.Windows.Controls;

namespace MuYingDrive.ProjectWorkbooks;

// Wide rows share proportional columns; narrow rows stack their labelled fields.
// Every child is measured at the actual available width, so text/images never request a horizontal scroll.
public sealed class ResponsiveColumnsPanel : Panel
{
    public string Weights { get; set; } = "1,1,1,2,3";
    public double Breakpoint { get; set; } = 760;
    private double[] Widths(double width)
    {
        var parsed = Weights.Split(',').Select(s => double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 1).ToArray();
        var weights = Enumerable.Range(0, InternalChildren.Count).Select(i => i < parsed.Length ? parsed[i] : 1).ToArray();
        return weights.Select(w => width * w / Math.Max(1, weights.Sum())).ToArray();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? Breakpoint : Math.Max(0, availableSize.Width);
        var compact = width < Breakpoint;
        var widths = Widths(width);
        double height = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i]; child.Measure(new Size(compact ? width : widths[i], double.PositiveInfinity));
            height = compact ? height + child.DesiredSize.Height : Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width, height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var compact = finalSize.Width < Breakpoint; var widths = Widths(finalSize.Width); double offset = 0;
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
