using System.ComponentModel;

namespace MagmPaper.Windows;

internal static class DesktopHost
{
    public static nint Attach(nint window)
    {
        var progman = Win32.FindWindow("Progman", null);
        if (progman == 0) throw new IOException("Рабочий стол Explorer ещё не готов.");
        Win32.SendMessageTimeout(progman, 0x052c, 0, 0, 2, 2000, out _);
        nint worker = 0;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (Win32.FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null) != 0)
                worker = Win32.FindWindowEx(0, hwnd, "WorkerW", null);
            return worker == 0;
        }, 0);
        if (worker == 0) worker = Win32.FindWindowEx(progman, 0, "WorkerW", null);
        if (worker == 0) throw new IOException("Windows не предоставила слой обоев WorkerW.");
        Fit(window, worker); return worker;
    }
    public static void Fit(nint window, nint worker)
    {
        var rect = Win32.PrimaryMonitor().Monitor;
        var style = (uint)Win32.GetWindowLongPtr(window, Win32.Style);
        Win32.SetWindowLongPtr(window, Win32.Style, (nint)((style & ~(Win32.Popup | 0x00c40000)) | Win32.Child));
        Win32.SetParent(window, worker);
        if (Win32.GetParent(window) != worker) throw new IOException("Не удалось прикрепить обои за значками рабочего стола.");
        var point = new Win32.Point(rect.Left, rect.Top); Win32.MapWindowPoints(0, worker, ref point, 1);
        if (!Win32.SetWindowPos(window, 0, point.X, point.Y, rect.Width, rect.Height, 0x0034)) throw new Win32Exception();
        Win32.ShowWindow(window, 4);
    }
    public static bool Covered(nint wallpaper)
    {
        var foreground = Win32.GetForegroundWindow();
        var name = Win32.ClassName(Win32.GetAncestor(foreground, 2));
        if (name is "Progman" or "WorkerW" or "SHELLDLL_DefView") return false;
        var work = Win32.PrimaryMonitor().Work.Area; bool covered = false;
        Win32.EnumWindows((hwnd, _) =>
        {
            var kind = Inspect(hwnd, wallpaper, work);
            if (kind == 1) covered = true;
            return kind == 0;
        }, 0);
        return covered;
    }
    internal static int Inspect(nint hwnd, nint wallpaper, Area area)
    {
        if (hwnd == wallpaper || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return 0;
        if (Win32.Cloaked(hwnd, 14, out int cloak, 4) == 0 && cloak != 0) return 0;
        var name = Win32.ClassName(hwnd);
        if (name is "Progman" or "WorkerW") return Win32.FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null) == 0 ? 0 : 2;
        if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "SHELLDLL_DefView") return 0;
        var style = (uint)Win32.GetWindowLongPtr(hwnd, Win32.Style); var extended = (uint)Win32.GetWindowLongPtr(hwnd, Win32.ExtendedStyle);
        if ((style & Win32.Child) != 0 || (extended & 0x080000a0) != 0) return 0;
        if ((extended & 0x00080000) != 0 && (!Win32.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) || (flags & 1) != 0 || (flags & 2) == 0 || alpha != 255)) return 0;
        if ((Win32.Bounds(hwnd, 9, out var bounds, 16) != 0 || bounds.Width <= 0 || bounds.Height <= 0) && !Win32.GetWindowRect(hwnd, out bounds)) return 0;
        return bounds.Area.Covers(area) ? 1 : 0;
    }
}
