using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MagmPaper.Windows;

namespace MagmPaper.Views;

internal sealed record WindowBlurOptions(bool Enabled = false, string Color = "#A78BFA", int Radius = 30, int Intensity = 42)
{
    internal WindowBlurOptions Normalize() => this with
    {
        Color = Color is not null && Regex.IsMatch(Color, @"\A#[0-9a-fA-F]{6}\z") ? Color : "#A78BFA",
        Radius = Math.Clamp(Radius, 6, 72),
        Intensity = Math.Clamp(Intensity, 0, 100)
    };
}

internal sealed class WindowFrameBlur
{
    private readonly Dictionary<Window, Frame> frames = new();
    private WindowBlurOptions options = new();

    public void Configure(WindowBlurOptions next)
    {
        options = next;
        foreach (var frame in frames.Values) frame.Schedule();
    }

    public void Track(Window window)
    {
        var frame = new Frame(window, this);
        frames.Add(window, frame);
        frame.Schedule();
    }

    private sealed class Frame
    {
        private const uint NoActivate = 0x0010, NoZOrder = 0x0004, ShowWindow = 0x0040, NoOwnerZOrder = 0x0200;
        private readonly Window window;
        private readonly WindowFrameBlur manager;
        private HwndSource? layer;
        private DrawingVisual? drawing;
        private (WindowBlurOptions Style, double Width, double Height, double X, double Y)? rendered;
        private bool pending, closed;

        public Frame(Window window, WindowFrameBlur manager)
        {
            this.window = window;
            this.manager = manager;
            window.LocationChanged += (_, _) => Schedule();
            window.SizeChanged += (_, _) => Schedule();
            window.StateChanged += (_, _) => Schedule();
            window.IsVisibleChanged += (_, _) => Schedule();
            window.DpiChanged += (_, _) => Schedule();
            window.Closed += (_, _) =>
            {
                closed = true;
                ReleaseLayer();
                manager.frames.Remove(window);
            };
        }

        public void Schedule()
        {
            if (pending || closed) return;
            pending = true;
            _ = window.Dispatcher.InvokeAsync(() =>
            {
                pending = false;
                if (!closed) Refresh();
            }, DispatcherPriority.Render);
        }

        private void Refresh()
        {
            var style = manager.options;
            if (!style.Enabled || style.Intensity == 0 || !window.IsVisible || window.WindowState != WindowState.Normal)
            {
                ReleaseLayer();
                return;
            }

            nint handle = new WindowInteropHelper(window).Handle;
            if (handle == 0 || !Win32.GetWindowRect(handle, out var bounds)) return;
            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            if (width <= 0 || height <= 0) return;

            var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            if (bounds.Left <= work.Left && bounds.Top <= work.Top && bounds.Right >= work.Right && bounds.Bottom >= work.Bottom)
            {
                ReleaseLayer();
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(window);
            int horizontal = (int)Math.Ceiling(style.Radius * dpi.DpiScaleX);
            int vertical = (int)Math.Ceiling(style.Radius * dpi.DpiScaleY);
            int x = bounds.Left - horizontal, y = bounds.Top - vertical;
            if (layer is null)
            {
                // A layered owned window follows the application's z-order. Its
                // transparent center and click-through style keep all input on the windows below it.
                const int popup = unchecked((int)0x80000000);
                const int transparent = 0x20, toolWindow = 0x80, noActivate = 0x08000000;
                layer = new HwndSource(new HwndSourceParameters("MagmPaper.WindowFrameBlur", 1, 1)
                {
                    ParentWindow = handle,
                    PositionX = x,
                    PositionY = y,
                    WindowStyle = popup,
                    ExtendedWindowStyle = transparent | toolWindow | noActivate,
                    UsesPerPixelOpacity = true
                });
                drawing = new DrawingVisual();
                layer.RootVisual = drawing;
                layer.DpiChanged += (_, _) => Schedule();
            }
            _ = Win32.SetWindowPos(layer.Handle, 0, x, y, width + horizontal * 2, height + vertical * 2,
                NoActivate | NoZOrder | NoOwnerZOrder | ShowWindow);

            var transform = layer.CompositionTarget!.TransformFromDevice;
            var next = (style, width * transform.M11, height * transform.M22, horizontal * transform.M11, vertical * transform.M22);
            if (next == rendered) return;
            rendered = next;
            Paint(next.style, next.Item2, next.Item3, next.Item4, next.Item5);
        }

        private void Paint(WindowBlurOptions style, double width, double height, double x, double y)
        {
            Color tint = (Color)ColorConverter.ConvertFromString(style.Color);
            double strength = style.Intensity / 100.0;
            GradientStop Stop(double offset, double alpha) => new(Color.FromArgb((byte)Math.Round(alpha * strength * 255),
                tint.R, tint.G, tint.B), offset);
            var stops = new GradientStopCollection
            {
                Stop(0, .85), Stop(.18, .73), Stop(.40, .47), Stop(.66, .19), Stop(.86, .045), Stop(1, 0)
            };
            stops.Freeze();

            using var dc = drawing!.RenderOpen();
            void Edge(Rect bounds, Point from, Point to) => dc.DrawRectangle(new LinearGradientBrush(stops, from, to), null, bounds);
            void Corner(Rect bounds, Point origin) => dc.DrawRectangle(new RadialGradientBrush(stops)
            {
                Center = origin, GradientOrigin = origin, RadiusX = 1, RadiusY = 1
            }, null, bounds);

            Edge(new(x, 0, width, y), new(0, 1), new(0, 0));
            Edge(new(x, y + height, width, y), new(0, 0), new(0, 1));
            Edge(new(0, y, x, height), new(1, 0), new(0, 0));
            Edge(new(x + width, y, x, height), new(0, 0), new(1, 0));
            Corner(new(0, 0, x, y), new(1, 1));
            Corner(new(x + width, 0, x, y), new(0, 1));
            Corner(new(0, y + height, x, y), new(1, 0));
            Corner(new(x + width, y + height, x, y), new(0, 0));
        }

        private void ReleaseLayer()
        {
            layer?.Dispose();
            layer = null;
            drawing = null;
            rendered = null;
        }
    }
}
