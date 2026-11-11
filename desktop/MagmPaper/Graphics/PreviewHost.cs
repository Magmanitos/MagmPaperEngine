using System.Runtime.InteropServices;
using System.Windows.Interop;
using MagmPaper.Windows;

namespace MagmPaper.Graphics;

public sealed class PreviewHost : HwndHost
{
    internal RenderSurface? Surface { get; private set; }
    internal event Action? Ready;
    private nint window;
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        window = Win32.CreateSurface(parent.Handle);
        Surface = new RenderSurface(window); Ready?.Invoke();
        return new HandleRef(this, window);
    }
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Surface?.Dispose(); Surface = null;
        Win32.DestroyWindow(hwnd.Handle); window = 0;
    }
}
