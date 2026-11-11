using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MagmPaper.Views;

public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached("Controller", typeof(Controller), typeof(SmoothScroll));
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ScrollViewer viewer) return;
        if (viewer.GetValue(ControllerProperty) is Controller previous) previous.Dispose();
        viewer.SetValue(ControllerProperty, (bool)args.NewValue ? new Controller(viewer) : null);
    }
    private static DependencyObject? Parent(DependencyObject child) => child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
    internal static bool IsAnimating(ScrollViewer viewer) => viewer.GetValue(ControllerProperty) is Controller { Running: true };

    private sealed class Controller : IDisposable
    {
        private readonly ScrollViewer viewer;
        private readonly ScrollMotion motion = new(0);
        private TimeSpan? lastFrame;
        private double requestedOffset;
        public bool Running { get; private set; }
        public Controller(ScrollViewer viewer)
        {
            this.viewer = viewer;
            viewer.PreviewMouseWheel += Wheel; viewer.PreviewMouseDown += Cancel; viewer.PreviewKeyDown += Cancel;
            viewer.Unloaded += Cancel; viewer.IsVisibleChanged += VisibilityChanged; viewer.ScrollChanged += Scrolled;
        }
        private void Wheel(object sender, MouseWheelEventArgs args)
        {
            if (args.Handled) return;
            // A single-line input cannot scroll vertically. Keep the page's
            // smooth wheel path when the pointer passes over taskbar fields.
            for (var node = args.OriginalSource as DependencyObject; node is not null && node != viewer; node = Parent(node))
            {
                if (node is ScrollViewer inner && inner.ScrollableHeight > 0) return;
                if (node is ComboBox { IsDropDownOpen: true } or TextBox { AcceptsReturn: true } or RichTextBox) return;
            }
            int lines = SystemParameters.WheelScrollLines;
            if (lines == 0 || viewer.ScrollableHeight <= 0) return;
            double distance = lines < 0 ? viewer.ViewportHeight : lines * 22;
            double target = Math.Clamp((Running ? motion.Target : viewer.VerticalOffset) - args.Delta / 120.0 * distance, 0, viewer.ScrollableHeight);
            if (Math.Abs(target - viewer.VerticalOffset) < .1 && !Running) return;
            args.Handled = true;
            if (!SystemParameters.ClientAreaAnimation || !viewer.IsVisible) { Stop(); viewer.ScrollToVerticalOffset(target); return; }
            if (!Running)
            {
                motion.Reset(viewer.VerticalOffset); requestedOffset = viewer.VerticalOffset; lastFrame = null;
                Running = true; CompositionTarget.Rendering += Frame;
            }
            motion.MoveTo(target);
        }
        private void Frame(object? sender, EventArgs args)
        {
            if (!viewer.IsVisible) { Stop(); return; }
            var now = ((RenderingEventArgs)args).RenderingTime;
            if (lastFrame is not { } previous) { lastFrame = now; return; }
            if (now <= previous) return;
            lastFrame = now;
            motion.MoveTo(Math.Clamp(motion.Target, 0, viewer.ScrollableHeight));
            motion.Advance((now - previous).TotalSeconds);
            requestedOffset = Math.Clamp(motion.Settled ? motion.Target : motion.Position, 0, viewer.ScrollableHeight);
            viewer.ScrollToVerticalOffset(requestedOffset);
            if (motion.Settled) Stop();
        }
        private void Scrolled(object sender, ScrollChangedEventArgs args)
        {
            if (!ReferenceEquals(args.OriginalSource, viewer) || !Running) return;
            // Respect scrollbars, keyboard navigation and programmatic jumps.
            if (args.VerticalChange != 0 && Math.Abs(viewer.VerticalOffset - requestedOffset) > 1) Stop();
        }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!(bool)args.NewValue) Stop(); }
        private void Cancel(object sender, RoutedEventArgs args) => Stop();
        private void Stop()
        {
            if (Running) CompositionTarget.Rendering -= Frame;
            Running = false; lastFrame = null;
        }
        public void Dispose()
        {
            Stop(); viewer.PreviewMouseWheel -= Wheel; viewer.PreviewMouseDown -= Cancel; viewer.PreviewKeyDown -= Cancel;
            viewer.Unloaded -= Cancel; viewer.IsVisibleChanged -= VisibilityChanged; viewer.ScrollChanged -= Scrolled;
        }
    }
}
