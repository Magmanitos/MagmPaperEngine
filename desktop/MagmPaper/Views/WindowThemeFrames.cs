using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using MagmPaper.Windows;

namespace MagmPaper.Views;

internal sealed class WindowThemeFrames
{
    private readonly Dictionary<Window, Frame> frames = new();
    private WindowThemeSettings settings = new();
    private Brush? paint;

    internal void Configure(WindowThemeSettings next, Brush material)
    {
        settings = next;
        paint = material;
        foreach (var frame in frames.Values) frame.Refresh();
    }

    internal void Track(Window window, double radius, FrameworkElement surface, string? baseBrush, bool animate)
    {
        var frame = new Frame(this, window, radius, surface, baseBrush);
        frames.Add(window, frame);
        window.Closed += (_, _) => frames.Remove(window);
        frame.Refresh();
        if (!animate) return;
        void Play() => WindowThemePaint.Play(surface, settings);
        if (window.IsLoaded) Play();
        else
        {
            void Loaded(object sender, RoutedEventArgs args) { window.Loaded -= Loaded; Play(); }
            window.Loaded += Loaded;
        }
    }

    private sealed class Frame
    {
        private readonly WindowThemeFrames theme;
        private readonly Window window;
        private readonly double baseRadius;
        private readonly FrameworkElement surface;
        private readonly string? baseBrush;
        private (int Width, int Height, int DiameterX, int DiameterY)? region;
        private bool styled, geometryPending;

        internal Frame(WindowThemeFrames theme, Window window, double radius, FrameworkElement surface, string? baseBrush)
        {
            this.theme = theme;
            this.window = window;
            baseRadius = radius;
            this.surface = surface;
            this.baseBrush = baseBrush;
            window.SourceInitialized += (_, _) => ApplyNative();
            window.StateChanged += (_, _) => Refresh();
            window.IsVisibleChanged += (_, _) => QueueGeometry();
            window.SizeChanged += (_, _) => QueueGeometry();
            window.DpiChanged += (_, _) => QueueGeometry();
        }

        internal void Refresh()
        {
            var options = theme.settings;
            bool enabled = options.Enabled;
            if (enabled || styled)
            {
                var property = surface is Border ? Border.BackgroundProperty : surface is Panel ? Panel.BackgroundProperty : Window.BackgroundProperty;
                if (enabled) surface.SetValue(property, theme.paint);
                else if (baseBrush is not null) surface.SetResourceReference(property, baseBrush);
                else surface.ClearValue(property);
                if (window.AllowsTransparency && surface is Border border)
                    border.CornerRadius = new(enabled ? options.Radius : baseRadius);
                if (window.AllowsTransparency) window.Opacity = enabled ? options.Opacity / 100d : 1;
                styled = enabled;
            }
            QueueGeometry();
        }

        private void QueueGeometry()
        {
            if (geometryPending) return;
            geometryPending = true;
            _ = window.Dispatcher.InvokeAsync(() =>
            {
                geometryPending = false;
                ApplyNative();
            }, DispatcherPriority.Render);
        }

        private void ApplyNative()
        {
            if (window.AllowsTransparency) return;
            nint hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0 || !Win32.IsWindow(hwnd)) return;
            var options = theme.settings;
            bool normal = options.Enabled && window.WindowState == WindowState.Normal;
            if (WindowChrome.GetWindowChrome(window) is { } chrome)
            {
                chrome.CornerRadius = new(normal ? options.Radius : baseRadius);
                return;
            }

            if (!normal)
            {
                if (region is not null)
                {
                    _ = Win32.SetWindowRgn(hwnd, 0, true);
                    region = null;
                }
                return;
            }
            if (!Win32.GetWindowRect(hwnd, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0) return;
            var dpi = VisualTreeHelper.GetDpi(window);
            var next = (bounds.Width, bounds.Height, (int)Math.Round(2 * options.Radius * dpi.DpiScaleX),
                (int)Math.Round(2 * options.Radius * dpi.DpiScaleY));
            if (next == region) return;
            nint shape = options.Radius == 0
                ? Win32.CreateRectRgn(0, 0, next.Width, next.Height)
                : Win32.CreateRoundRectRgn(0, 0, next.Width + 1, next.Height + 1, next.Item3, next.Item4);
            if (shape == 0) return;
            if (Win32.SetWindowRgn(hwnd, shape, true) != 0) region = next;
            else _ = Win32.DeleteObject(shape);
        }

    }
}
