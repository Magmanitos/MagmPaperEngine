using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MagmPaper.Windows;

namespace MagmPaper.Views;

public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0, (sender, args) => ((ScrollViewer)sender).ScrollToVerticalOffset((double)args.NewValue)));
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached("Target", typeof(double?), typeof(SmoothScroll));
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ScrollViewer viewer) return;
        if ((bool)args.NewValue) { viewer.PreviewMouseWheel += Wheel; viewer.PreviewMouseDown += Cancel; viewer.PreviewKeyDown += Cancel; viewer.Unloaded += Cancel; }
        else { viewer.PreviewMouseWheel -= Wheel; viewer.PreviewMouseDown -= Cancel; viewer.PreviewKeyDown -= Cancel; viewer.Unloaded -= Cancel; Stop(viewer); }
    }
    private static DependencyObject? Parent(DependencyObject child) => child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
    private static void Wheel(object sender, MouseWheelEventArgs args)
    {
        var viewer = (ScrollViewer)sender;
        // A text editor/dropdown or inner scroll area owns its own input.
        for (var node = args.OriginalSource as DependencyObject; node is not null && node != viewer; node = Parent(node))
            if (node is ScrollViewer or ComboBox or System.Windows.Controls.Primitives.TextBoxBase) return;
        int lines = SystemParameters.WheelScrollLines;
        if (lines == 0 || viewer.ScrollableHeight <= 0) return;
        double distance = lines < 0 ? viewer.ViewportHeight : lines * 22;
        double target = Math.Clamp(((double?)viewer.GetValue(TargetProperty) ?? viewer.VerticalOffset) - args.Delta / 120.0 * distance, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - viewer.VerticalOffset) < .1) return;
        args.Handled = true; Animate(viewer, target);
    }
    internal static void Animate(ScrollViewer viewer, double target)
    {
        double start = viewer.VerticalOffset;
        Stop(viewer); viewer.SetValue(OffsetProperty, start); viewer.SetValue(TargetProperty, (double?)target);
        if (!SystemParameters.ClientAreaAnimation) { viewer.ScrollToVerticalOffset(target); viewer.ClearValue(TargetProperty); return; }
        var animation = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
        Timeline.SetDesiredFrameRate(animation, Math.Min(240, Win32.RefreshRate()));
        animation.Completed += (_, _) => { viewer.SetValue(OffsetProperty, target); viewer.BeginAnimation(OffsetProperty, null); viewer.ClearValue(TargetProperty); };
        viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private static void Stop(ScrollViewer viewer)
    {
        double offset = viewer.VerticalOffset;
        viewer.SetValue(OffsetProperty, offset); viewer.BeginAnimation(OffsetProperty, null); viewer.ClearValue(TargetProperty);
    }
    private static void Cancel(object sender, RoutedEventArgs args) => Stop((ScrollViewer)sender);
}
