using System.Windows;
using System.Windows.Media;

namespace MagmPaper.Views;

public sealed class TaskbarGlassPreview : FrameworkElement
{
    private (Color First, Color Second)? colors;
    private Brush? tint, sheen;
    private Pen? edge, innerEdge;
    private double rounding;
    private bool showOutline;

    internal void Configure(Color first, Color second, double radius, bool outline)
    {
        bool recolor = colors != (first, second);
        if (recolor)
        {
            colors = (first, second);
            tint = Gradient(new(0, .12), new(1, .82), (0, Glint(first, .18, 115)), (.23, Alpha(first, 127)),
                (.52, Alpha(first, 78)), (.81, Alpha(second, 104)), (1, Glint(second, .10, 113)));
            var shadow = Color.FromRgb(0x11, 0x10, 0x20);
            sheen = Gradient(new(0, 0), new(0, 1), (0, Glint(first, .82, 58)), (.11, Glint(first, .45, 22)),
                (.34, Glint(first, .45, 0)), (.70, Alpha(shadow, 0)), (.94, Alpha(shadow, 59)), (1, Glint(second, .38, 23)));
            edge = new Pen(Gradient(new(0, .14), new(1, .86), (0, Glint(first, .95, 223)), (.20, Glint(first, .76, 148)),
                (.49, Glint(first, .55, 70)), (.68, Glint(second, .56, 98)), (1, Glint(second, .96, 208))), 1.35);
            innerEdge = new Pen(Gradient(new(0, .14), new(1, .86), (0, Glint(first, .84, 83)), (.43, Glint(first, .8, 18)),
                (.73, Glint(second, .36, 32)), (1, Glint(second, .89, 106))), .65);
            edge.Freeze();
            innerEdge.Freeze();
        }
        if (recolor || rounding != radius || showOutline != outline)
        {
            rounding = radius;
            showOutline = outline;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var rect = new Rect(RenderSize);
        if (rect.Width < 5 || rect.Height < 5 || tint is null) return;
        drawing.DrawRoundedRectangle(tint, null, rect, rounding, rounding);
        drawing.DrawRoundedRectangle(sheen, null, rect, rounding, rounding);
        if (showOutline)
        {
            DrawEdge(drawing, rect, edge!, .8);
            DrawEdge(drawing, rect, innerEdge!, 2.15);
        }
    }

    private void DrawEdge(DrawingContext drawing, Rect rect, Pen pen, double inset)
    {
        rect.Inflate(-inset, -inset);
        double radius = Math.Max(0, rounding - inset);
        drawing.DrawRoundedRectangle(null, pen, rect, radius, radius);
    }

    private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Glint(Color color, double light, byte alpha)
    {
        byte Mix(byte channel) => (byte)Math.Round(channel + (255 - channel) * light);
        return Color.FromArgb(alpha, Mix(color.R), Mix(color.G), Mix(color.B));
    }

    private static Brush Gradient(Point start, Point end, params (double At, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var stop in stops) brush.GradientStops.Add(new(stop.Color, stop.At));
        brush.Freeze();
        return brush;
    }
}
