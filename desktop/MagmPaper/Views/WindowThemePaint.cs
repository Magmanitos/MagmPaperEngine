using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static MagmPaper.Views.GlassMaterial;

namespace MagmPaper.Views;

internal static class WindowThemePaint
{
    internal static Brush Create(WindowThemeSettings settings, Color first, Color second, Color background)
    {
        // Keep the foreground contrast of the selected light/dark palette.
        bool light = Luminance(background) > .25;
        double weight = light ? .21 : .46;
        var a = Blend(background, first, weight);
        var b = Blend(background, second, weight);
        if (settings.Material == WindowMaterial.Solid)
        {
            var fill = new SolidColorBrush(a);
            fill.Freeze();
            return fill;
        }
        if (settings.Material == WindowMaterial.Gradient)
        {
            var fill = new LinearGradientBrush(a, b, 24);
            fill.Freeze();
            return fill;
        }

        var surface = new Rect(0, 0, 460, 155);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(background), null, surface);
            context.DrawRectangle(Light(a, new(.19, .22), .48), null, surface);
            context.DrawRectangle(Light(b, new(.88, .75), .59), null, surface);
            var glass = new GlassMaterial(Blend(background, first, weight * .9), Blend(background, second, weight * .9));
            glass.Draw(context, surface, 0, settings.GlassOutline);
        }
        var brush = new DrawingBrush(drawing);
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush Light(Color tint, Point center, double radius) => new(tint, Alpha(tint, 0))
    {
        Center = center, GradientOrigin = center, RadiusX = radius, RadiusY = radius * 1.35
    };

    private static Color Blend(Color background, Color tint, double weight) => Color.FromRgb(
        (byte)Math.Round(background.R + (tint.R - background.R) * weight),
        (byte)Math.Round(background.G + (tint.G - background.G) * weight),
        (byte)Math.Round(background.B + (tint.B - background.B) * weight));

    internal static double Luminance(Color color)
    {
        static double Linear(byte value)
        {
            double channel = value / 255.0;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    internal static void Play(FrameworkElement element, WindowThemeSettings settings)
    {
        if (settings.Motion == WindowOpeningMotion.Off || !SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(settings.Duration);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DoubleAnimation Animate(double origin, double destination) =>
            new(origin, destination, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var opacity = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
        element.BeginAnimation(UIElement.OpacityProperty, Animate(0, opacity));
        if (settings.Motion == WindowOpeningMotion.Rise)
        {
            var translate = new TranslateTransform();
            element.RenderTransform = translate;
            translate.BeginAnimation(TranslateTransform.YProperty, Animate(14, 0));
        }
        else if (settings.Motion == WindowOpeningMotion.Scale)
        {
            var scale = new ScaleTransform();
            element.RenderTransformOrigin = new(.5, .84);
            element.RenderTransform = scale;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Animate(.95, 1));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Animate(.95, 1));
        }
    }
}
