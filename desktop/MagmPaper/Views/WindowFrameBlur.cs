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
    private double? radiusOverride;

    public void Configure(WindowBlurOptions next, double? radius = null)
    {
        if (options == next && radiusOverride == radius) return;
        options = next;
        radiusOverride = radius;
        foreach (var frame in frames.Values) frame.Schedule();
    }

    public void Track(Window window, double cornerRadius)
    {
        var frame = new Frame(window, this, cornerRadius);
        frames.Add(window, frame);
        frame.Schedule();
    }

    private sealed class Frame
    {
        private const uint NoActivate = 0x0010, NoZOrder = 0x0004, ShowWindow = 0x0040, NoOwnerZOrder = 0x0200;
        private readonly Window window;
        private readonly WindowFrameBlur manager;
        private readonly double cornerRadius;
        private HwndSource? layer;
        private DrawingVisual? drawing;
        private (WindowBlurOptions Style, Size Window, Size Padding, Size Corner)? rendered;
        private bool pending, closed;

        public Frame(Window window, WindowFrameBlur manager, double cornerRadius)
        {
            this.window = window;
            this.manager = manager;
            this.cornerRadius = cornerRadius;
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
            double radius = manager.radiusOverride ?? cornerRadius;
            var next = (Style: style,
                Window: new Size(width * transform.M11, height * transform.M22),
                Padding: new Size(horizontal * transform.M11, vertical * transform.M22),
                Corner: new Size(radius * dpi.DpiScaleX * transform.M11, radius * dpi.DpiScaleY * transform.M22));
            if (next == rendered) return;
            rendered = next;
            using var context = drawing!.RenderOpen();
            DrawContour(context, next.Style, next.Window, next.Padding, next.Corner);
        }

        private void ReleaseLayer()
        {
            layer?.Dispose();
            layer = null;
            drawing = null;
            rendered = null;
        }
    }

    internal static void DrawContour(DrawingContext context, WindowBlurOptions style, Size window, Size padding, Size corner)
    {
        double width = window.Width, height = window.Height;
        double x = padding.Width, y = padding.Height;
        double rx = Math.Min(corner.Width, width / 2), ry = Math.Min(corner.Height, height / 2);
        bool rounded = rx > 0 && ry > 0;
        Color tint = (Color)ColorConverter.ConvertFromString(style.Color);
        double strength = style.Intensity / 100.0;

        GradientStopCollection Stops(double inner)
        {
            GradientStop Stop(double distance, double alpha) => new(
                Color.FromArgb((byte)Math.Round(alpha * strength * 255), tint.R, tint.G, tint.B),
                inner + distance * (1 - inner));
            var stops = new GradientStopCollection
            {
                Stop(0, .85), Stop(.18, .73), Stop(.40, .47), Stop(.66, .19), Stop(.86, .045), Stop(1, 0)
            };
            stops.Freeze();
            return stops;
        }

        var edges = Stops(0);
        var corners = rounded ? Stops(rx / (rx + x)) : edges;
        if (rounded)
        {
            // The halo continues into the transparent corners of the window
            // and fades outward from the same curve as its visible border.
            var outer = new RectangleGeometry(new Rect(0, 0, width + 2 * x, height + 2 * y));
            var inner = new RectangleGeometry(new Rect(x, y, width, height), rx, ry);
            context.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner));
        }

        void Edge(Rect bounds, Point from, Point to) => context.DrawRectangle(new LinearGradientBrush(edges, from, to), null, bounds);
        void Corner(Rect bounds, Point origin) => context.DrawRectangle(new RadialGradientBrush(corners)
        {
            Center = origin, GradientOrigin = origin, RadiusX = 1, RadiusY = 1
        }, null, bounds);

        double right = x + width - rx, bottom = y + height - ry;
        Edge(new(x + rx, 0, width - 2 * rx, y), new(0, 1), new(0, 0));
        Edge(new(x + rx, y + height, width - 2 * rx, y), new(0, 0), new(0, 1));
        Edge(new(0, y + ry, x, height - 2 * ry), new(1, 0), new(0, 0));
        Edge(new(x + width, y + ry, x, height - 2 * ry), new(0, 0), new(1, 0));
        Corner(new(0, 0, x + rx, y + ry), new(1, 1));
        Corner(new(right, 0, x + rx, y + ry), new(0, 1));
        Corner(new(0, bottom, x + rx, y + ry), new(1, 0));
        Corner(new(right, bottom, x + rx, y + ry), new(0, 0));

        if (rounded) context.Pop();
    }
}
