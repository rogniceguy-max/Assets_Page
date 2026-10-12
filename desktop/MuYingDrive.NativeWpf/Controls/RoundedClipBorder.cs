using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MuYingDrive.NativeWpf.Controls;

/// <summary>
/// A native Border that clips its child to the rounded inner bounds. WPF's Border
/// draws rounded corners but does not clip child visuals to them by default.
/// </summary>
public sealed class RoundedClipBorder : Border
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arrangedSize = base.ArrangeOverride(finalSize);
        ApplyChildClip();
        return arrangedSize;
    }

    private void ApplyChildClip()
    {
        if (Child is null)
        {
            return;
        }

        var childSize = Child.RenderSize;
        if (childSize.Width <= 0d || childSize.Height <= 0d)
        {
            Child.Clip = Geometry.Empty;
            return;
        }

        var borderInset = Math.Max(BorderThickness.Left, BorderThickness.Top);
        var radius = Math.Max(0d, CornerRadius.TopLeft - borderInset);
        radius = Math.Min(radius, Math.Min(childSize.Width, childSize.Height) / 2d);

        var clip = new RectangleGeometry(new Rect(childSize), radius, radius);
        if (clip.CanFreeze)
        {
            clip.Freeze();
        }

        Child.Clip = clip;
    }
}
