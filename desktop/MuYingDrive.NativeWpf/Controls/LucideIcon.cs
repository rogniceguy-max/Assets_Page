using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace MuYingDrive.NativeWpf.Controls;

/// <summary>
/// A small, native WPF renderer for the Lucide outline icon set used by MuYing Drive.
/// The vector data is compiled into WPF Geometry objects; this control has no runtime
/// SVG, browser, JavaScript, or third-party package dependency.
/// </summary>
/// <remarks>
/// Icon path data follows the Lucide definitions distributed with lucide-react 0.454.0.
/// Keep the source version pinned when updating these paths. MoreHorizontal is retained
/// as a compatibility name for Lucide's current Ellipsis icon.
/// </remarks>
public sealed class LucideIcon : FrameworkElement
{
    private const double ViewBoxSize = 24d;

    private static readonly IReadOnlyDictionary<string, Geometry> Geometries =
        new ReadOnlyDictionary<string, Geometry>(CreateGeometries());

    static LucideIcon()
    {
        WidthProperty.OverrideMetadata(
            typeof(LucideIcon),
            new FrameworkPropertyMetadata(ViewBoxSize, FrameworkPropertyMetadataOptions.AffectsMeasure));
        HeightProperty.OverrideMetadata(
            typeof(LucideIcon),
            new FrameworkPropertyMetadata(ViewBoxSize, FrameworkPropertyMetadataOptions.AffectsMeasure));
    }

    public LucideIcon()
    {
        Focusable = false;
        IsHitTestVisible = false;
    }

    /// <summary>Lucide icon name, for example <c>FolderOpen</c> or <c>PanelLeft</c>.</summary>
    public string Icon
    {
        get => (string?)GetValue(IconProperty) ?? string.Empty;
        set => SetValue(IconProperty, value);
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(string),
        typeof(LucideIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Outline color. Bind this to the current theme's icon/text brush.</summary>
    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(LucideIcon),
        new FrameworkPropertyMetadata(Brushes.SlateGray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Lucide's nominal stroke is 2 units in its 24 by 24 view box.</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(LucideIcon),
        new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsRender),
        static value => value is double thickness && double.IsFinite(thickness) && thickness > 0d);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (ActualWidth <= 0d || ActualHeight <= 0d || Stroke is null)
        {
            return;
        }

        if (!Geometries.TryGetValue(Icon ?? string.Empty, out var geometry) || geometry.Bounds.IsEmpty)
        {
            // Unknown names intentionally have no fallback glyph.
            return;
        }

        var scale = Math.Min(ActualWidth, ActualHeight) / ViewBoxSize;
        var offsetX = (ActualWidth - ViewBoxSize * scale) / 2d;
        var offsetY = (ActualHeight - ViewBoxSize * scale) / 2d;

        var pen = new Pen(Stroke, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            DashCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
            MiterLimit = 2d
        };

        drawingContext.PushTransform(new MatrixTransform(new Matrix(scale, 0d, 0d, scale, offsetX, offsetY)));
        drawingContext.DrawGeometry(null, pen, geometry);
        drawingContext.Pop();
    }

    private static Dictionary<string, Geometry> CreateGeometries()
    {
        var icons = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase)
        {
            ["PanelLeft"] = Group(
                RoundRect(3, 3, 18, 18, 2),
                Path("M9 3v18")),
            ["Menu"] = Path("M4 6h16 M4 12h16 M4 18h16"),
            ["Folder"] = Path("M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"),
            ["FolderOpen"] = Path("m6 14 1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2"),
            ["Files"] = Group(
                Path("M20 7h-3a2 2 0 0 1-2-2V2"),
                Path("M9 18a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h7l4 4v10a2 2 0 0 1-2 2Z"),
                Path("M3 7.6v12.8A1.6 1.6 0 0 0 4.6 22h9.8")),
            ["Upload"] = Group(
                Path("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"),
                Path("m17 8-5-5-5 5"),
                Path("M12 3v12")),
            ["Download"] = Group(
                Path("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"),
                Path("m7 10 5 5 5-5"),
                Path("M12 15V3")),
            ["Image"] = Group(
                RoundRect(3, 3, 18, 18, 2),
                Ellipse(9, 9, 2),
                Path("m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21")),
            ["Video"] = Group(
                Path("m16 13 5.223 3.482a.5.5 0 0 0 .777-.416V7.87a.5.5 0 0 0-.752-.432L16 10.5"),
                RoundRect(2, 6, 14, 12, 2)),
            ["List"] = Path("M3 12h.01 M3 18h.01 M3 6h.01 M8 12h13 M8 18h13 M8 6h13"),
            ["Grid2x2"] = Group(
                RoundRect(3, 3, 18, 18, 2),
                Path("M3 12h18 M12 3v18")),
            ["Search"] = Group(
                Ellipse(11, 11, 8),
                Path("m21 21-4.3-4.3")),
            ["UserRound"] = Group(
                Ellipse(12, 8, 5),
                Path("M20 21a8 8 0 0 0-16 0")),
            ["Sun"] = Group(
                Ellipse(12, 12, 4),
                Path("M12 2v2 M12 20v2 M4.93 4.93l1.41 1.41 M17.66 17.66l1.41 1.41 M2 12h2 M20 12h2 M6.34 17.66l-1.41 1.41 M19.07 4.93l-1.41 1.41")),
            ["Moon"] = Path("M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z"),
            ["Monitor"] = Group(
                RoundRect(2, 3, 20, 14, 2),
                Path("M8 21h8 M12 17v4")),
            // Both were referenced by XAML but never defined, so the controls rendered
            // blank: ChevronLeft on the file view's navigate-up button, X on the
            // transfer view's cancel button.
            ["ChevronLeft"] = Path("m15 18-6-6 6-6"),
            ["ChevronRight"] = Path("m9 18 6-6-6-6"),
            ["X"] = Path("M18 6 6 18M6 6l12 12"),
            ["Lock"] = Group(
                RoundRect(3, 11, 18, 11, 2),
                Path("M7 11V7a5 5 0 0 1 10 0v4")),
            ["File"] = Group(
                Path("M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z"),
                Path("M14 2v4a2 2 0 0 0 2 2h4")),
            ["CircleHelp"] = Group(
                Ellipse(12, 12, 10),
                Path("M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3 M12 17h.01")),
            ["MoreHorizontal"] = Group(
                Ellipse(6, 12, 1),
                Ellipse(12, 12, 1),
                Ellipse(18, 12, 1)),
            ["Play"] = Path("M6 3l14 9-14 9Z"),
            ["Pause"] = Group(
                RoundRect(6, 4, 4, 16, 1),
                RoundRect(14, 4, 4, 16, 1)),
            ["RefreshCw"] = Group(
                Path("M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5"),
                Path("M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5")),
            ["Plus"] = Path("M5 12h14 M12 5v14")
        };

        foreach (var geometry in icons.Values)
        {
            if (geometry.CanFreeze)
            {
                geometry.Freeze();
            }
        }

        return icons;
    }

    private static Geometry Path(string pathData) => Geometry.Parse(pathData);

    private static Geometry Ellipse(double centerX, double centerY, double radius) =>
        new EllipseGeometry(new Point(centerX, centerY), radius, radius);

    private static Geometry RoundRect(double x, double y, double width, double height, double radius) =>
        new RectangleGeometry(new Rect(x, y, width, height), radius, radius);

    private static Geometry Group(params Geometry[] parts)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var part in parts)
        {
            group.Children.Add(part);
        }

        return group;
    }
}
