using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MagmPaper.Windows;

namespace MagmPaper.Graphics;

public sealed class PreviewHost : HwndHost
{
    internal RenderSurface? Surface { get; private set; }
    internal event Action? Ready;
    private nint window;
    private bool active;
    private bool? presenting;
    private (int Left, int Top, int Right, int Bottom)? previousClip;
    internal nint NativeWindow => window;
    public PreviewHost()
    {
        LayoutUpdated += (_, _) => UpdatePresentation();
        IsVisibleChanged += (_, _) => UpdatePresentation();
    }
    internal void SetActive(bool value) { active = value; UpdatePresentation(); }
    protected override void OnWindowPositionChanged(Rect boundingBox)
    {
        base.OnWindowPositionChanged(boundingBox); UpdatePresentation();
    }
    private void UpdatePresentation()
    {
        if (window == 0) return;
        bool visible = active && IsVisible;
        var clip = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        if (visible)
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is FrameworkElement element && (element.ClipToBounds || element is ScrollContentPresenter))
                    clip.Intersect(element.TransformToDescendant(this).TransformBounds(new Rect(element.RenderSize)));
            }
            visible = !clip.IsEmpty && clip.Width > 0 && clip.Height > 0;
        }
        if (visible)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bounds = ((int)Math.Ceiling(clip.Left * dpi.DpiScaleX), (int)Math.Ceiling(clip.Top * dpi.DpiScaleY),
                (int)Math.Floor(clip.Right * dpi.DpiScaleX), (int)Math.Floor(clip.Bottom * dpi.DpiScaleY));
            if (bounds != previousClip)
            {
                nint region = Win32.CreateRectRgn(bounds.Item1, bounds.Item2, bounds.Item3, bounds.Item4);
                if (Win32.SetWindowRgn(window, region, true) == 0) Win32.DeleteObject(region);
                previousClip = bounds;
            }
        }
        // HwndHost has separate native visibility and clipping. Pausing GLES
        // alone leaves its last frame covering other WPF pages (airspace).
        if (Win32.IsWindowVisible(window) != visible) Win32.ShowWindow(window, visible ? 4 : 0);
        if (presenting != visible) { presenting = visible; Surface?.Configure(new(Paused: !visible, Fps: 0)); }
    }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        window = Win32.CreateSurface(parent.Handle, composed: true);
        Surface = new RenderSurface(window); Ready?.Invoke();
        UpdatePresentation();
        return new HandleRef(this, window);
    }
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Surface?.Dispose(); Surface = null;
        Win32.DestroyWindow(hwnd.Handle); window = 0; previousClip = null; presenting = null;
    }
}
