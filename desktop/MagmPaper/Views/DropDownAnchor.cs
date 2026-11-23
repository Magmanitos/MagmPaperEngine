using System.Windows;
using System.Windows.Controls;

namespace MagmPaper.Views;

// A WPF Popup is a separate HWND. Dismiss it when scrolling moves its anchor.
public static class DropDownAnchor
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(DropDownAnchor), new PropertyMetadata(false, Changed));

    private static readonly DependencyProperty GuardProperty = DependencyProperty.RegisterAttached(
        "Guard", typeof(Guard), typeof(DropDownAnchor));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ComboBox box) return;
        if ((bool)e.NewValue)
        {
            box.DropDownOpened += Opened;
            box.DropDownClosed += Closed;
        }
        else
        {
            box.DropDownOpened -= Opened;
            box.DropDownClosed -= Closed;
            Detach(box);
        }
    }

    private static void Opened(object? sender, EventArgs e)
    {
        if (sender is ComboBox box && Window.GetWindow(box) is { } owner && box.GetValue(GuardProperty) is null)
            box.SetValue(GuardProperty, new Guard(box, owner));
    }

    private static void Closed(object? sender, EventArgs e)
    {
        if (sender is ComboBox box) Detach(box);
    }

    private static void Detach(ComboBox box)
    {
        if (box.GetValue(GuardProperty) is not Guard guard) return;
        guard.Dispose();
        box.ClearValue(GuardProperty);
    }

    private sealed class Guard : IDisposable
    {
        private readonly ComboBox box;
        private readonly Window owner;

        public Guard(ComboBox box, Window owner)
        {
            this.box = box;
            this.owner = owner;
            owner.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled), true);
            owner.LocationChanged += OwnerMoved;
            owner.SizeChanged += OwnerResized;
            box.IsVisibleChanged += VisibilityChanged;
        }

        private void Scrolled(object sender, ScrollChangedEventArgs e)
        {
            if (e.OriginalSource is ScrollViewer viewer && viewer.IsAncestorOf(box) &&
                (e.HorizontalChange != 0 || e.VerticalChange != 0))
                Close();
        }

        private void OwnerMoved(object? sender, EventArgs e) => Close();
        private void OwnerResized(object sender, SizeChangedEventArgs e) => Close();
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Close(); }
        private void Close() => box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);

        public void Dispose()
        {
            owner.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
            owner.LocationChanged -= OwnerMoved;
            owner.SizeChanged -= OwnerResized;
            box.IsVisibleChanged -= VisibilityChanged;
        }
    }
}
