using System.ComponentModel;

namespace MagmPaper.Windows;

internal static class DesktopHost
{
    public static nint Attach(nint window)
    {
        var progman = Win32.FindWindow("Progman", null);
        if (progman == 0) throw new IOException("Рабочий стол Explorer ещё не готов.");
        Win32.SendMessageTimeout(progman, 0x052c, 0xd, 1, 2, 2000, out _);
        // Windows 11 24H2/HDR desktops place DefView and WorkerW under a
        // non-redirected Progman. Our layered child belongs BETWEEN them,
        // not inside the opaque WorkerW which renders the static wallpaper.
        if (IsRaised(progman))
        {
            if (Win32.FindWindowEx(progman, 0, "SHELLDLL_DefView", null) == 0 ||
                Win32.FindWindowEx(progman, 0, "WorkerW", null) == 0)
                throw new IOException("Windows ещё создаёт слои рабочего стола. Повторите установку обоев.");
            Fit(window, progman); return progman;
        }
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
        bool raised = IsRaised(worker);
        Fit(window, worker, raised ? Win32.FindWindowEx(worker, 0, "SHELLDLL_DefView", null) : 0,
            raised ? Win32.FindWindowEx(worker, 0, "WorkerW", null) : 0);
    }
    internal static void Fit(nint window, nint worker, nint icons, nint background)
    {
        var rect = Win32.PrimaryMonitor().Monitor;
        var style = (uint)Win32.GetWindowLongPtr(window, Win32.Style);
        Win32.SetWindowLongPtr(window, Win32.Style, (nint)((style & ~(Win32.Popup | 0x00c40000)) | Win32.Child));
        Win32.SetParent(window, worker);
        if (Win32.GetParent(window) != worker) throw new IOException("Не удалось прикрепить обои за значками рабочего стола.");
        var point = new Win32.Point(rect.Left, rect.Top); Win32.MapWindowPoints(0, worker, ref point, 1);
        if (!Win32.SetWindowPos(window, icons, point.X, point.Y, rect.Width, rect.Height, icons != 0 ? 0x0030u : 0x0034u)) throw new Win32Exception();
        if (icons != 0)
        {
            if (background != 0) Win32.SetWindowPos(background, 1, 0, 0, 0, 0, 0x13);
        }
        Win32.ShowWindow(window, 4);
    }
    private static bool IsRaised(nint parent) => ((uint)Win32.GetWindowLongPtr(parent, Win32.ExtendedStyle) & 0x00200000) != 0;
    internal static void EnsureOrder(nint window, nint parent)
    {
        if (!IsRaised(parent)) return;
        var icons = Win32.FindWindowEx(parent, 0, "SHELLDLL_DefView", null);
        if (icons != 0 && Win32.GetWindow(window, 3) != icons) Fit(window, parent);
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
