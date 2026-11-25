using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MagmPaper.Views;

public static class ToggleMotion
{
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(ToggleMotion), new PropertyMetadata(0.0, OffsetChanged));

    public static readonly DependencyProperty FadeProperty = DependencyProperty.RegisterAttached(
        "Fade", typeof(double), typeof(ToggleMotion), new PropertyMetadata(0.0, FadeChanged));

    public static double GetFade(DependencyObject element) => (double)element.GetValue(FadeProperty);
    public static void SetFade(DependencyObject element, double value) => element.SetValue(FadeProperty, value);

    public static double GetOffset(DependencyObject element) => (double)element.GetValue(OffsetProperty);
    public static void SetOffset(DependencyObject element, double value) => element.SetValue(OffsetProperty, value);

    private static void OffsetChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement knob) return;
        if (knob.RenderTransform is not TranslateTransform slide)
        {
            slide = new TranslateTransform((double)e.OldValue, 0);
            knob.RenderTransform = slide;
        }

        double current = slide.X;
        double target = (double)e.NewValue;
        slide.X = target;
        // Start at the current animated value so rapid changes reverse without a jump.
        slide.BeginAnimation(TranslateTransform.XProperty, Transition(knob, current, target));
    }

    private static void FadeChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement visual) return;
        double current = visual.Opacity;
        double target = (double)e.NewValue;
        visual.Opacity = target;
        visual.BeginAnimation(UIElement.OpacityProperty, Transition(visual, current, target));
    }

    private static DoubleAnimation? Transition(FrameworkElement element, double from, double to) =>
        element.IsLoaded && element.IsVisible && SystemParameters.ClientAreaAnimation && from != to
            ? new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            }
            : null;
}
