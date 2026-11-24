using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MagmPaper.Views;

public static class ToggleMotion
{
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(ToggleMotion), new PropertyMetadata(0.0, OffsetChanged));

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
        if (!knob.IsLoaded || !knob.IsVisible || !SystemParameters.ClientAreaAnimation || current == target)
        {
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }

        // Read the animated position before changing the destination so a
        // second toggle can reverse immediately without jumping to an endpoint.
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }
}
