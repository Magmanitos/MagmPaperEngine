using System.ComponentModel;
using System.Windows.Interop;
using MagmPaper.Windows;

namespace MagmPaper.Graphics;

internal sealed class DesktopMediaSurface : WallpaperSurface
{
    private readonly nint parent;
    private readonly HwndSource host;
    private readonly MediaWallpaperView view = new();
    private string? source;

    public DesktopMediaSurface(nint parent)
    {
        this.parent = parent;
        var previous = Win32.SetThreadDpiAwarenessContext(Win32.GetWindowDpiAwarenessContext(parent));
        if (previous == 0) throw new Win32Exception();
        try
        {
            host = new HwndSource(new HwndSourceParameters("MagmPaper desktop media", 1, 1)
            {
                ParentWindow = parent,
                WindowStyle = (int)(Win32.Child | Win32.Visible),
                ExtendedWindowStyle = 0x08000020, // Do not activate or intercept desktop input.
                HwndSourceHook = HitTest
            });
        }
        finally { Win32.SetThreadDpiAwarenessContext(previous); }
        try { host.RootVisual = view; Fit(); }
        catch { host.Dispose(); throw; }
    }

    private static nint HitTest(nint hwnd, int message, nint wp, nint lp, ref bool handled)
    {
        handled = message is 0x84 or 0x21;
        return message switch { 0x84 => -1, 0x21 => 3, _ => 0 };
    }

    public event Action<string>? Error { add => view.Error += value; remove => view.Error -= value; }
    public string Renderer => source is not null && Library.FormatOf(source) == WallpaperFormat.Gif ? "GIF / WPF" : "MP4 / Windows Media";
    public long Frames => view.Frames;
    public Task LoadAsync(string path) { source = path; return view.LoadAsync(path); }
    public void Configure(RenderOptions options) => view.Configure(options);
    public Task<WallpaperColors?> ReadColorsAsync() => source is { } path
        ? MediaThumbnails.ColorsAsync(path) : Task.FromResult<WallpaperColors?>(null);

    public void Fit()
    {
        if (!Win32.GetClientRect(parent, out var size) ||
            !Win32.SetWindowPos(host.Handle, 0, 0, 0, size.Width, size.Height, 0x14))
            throw new Win32Exception();
    }

    public void Dispose()
    {
        view.Dispose();
        host.Dispose();
    }
}
