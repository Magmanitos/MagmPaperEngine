using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MagmPaper.Windows;

internal static class Win32
{
    internal const uint Child = 0x40000000, Visible = 0x10000000, Popup = 0x80000000;
    internal const int Style = -16, ExtendedStyle = -20;
    internal delegate bool EnumProc(nint window, nint parameter);
    internal delegate nint WindowProc(nint window, uint message, nint wp, nint lp);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly Area Area => new(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct AppBarData { public uint Size; public nint Window; public uint Callback, Edge; public Rect Rect; public nint Param; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public WindowProc Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background;
        public string? Menu; public string Name;
    }
    private static readonly WindowProc SurfaceProcedure = (hwnd, msg, wp, lp) => msg switch { 0x14 => 1, 0x84 => -1, 0x21 => 3, _ => DefWindowProc(hwnd, msg, wp, lp) };
    private static readonly string SurfaceClass = "MagmPaper.Native.Surface.1";
    private static bool registered;
    public static nint CreateSurface(nint parent, bool desktop = false)
    {
        if (!registered)
        {
            var value = new WindowClass { Instance = GetModuleHandle(null), Name = SurfaceClass, Procedure = SurfaceProcedure };
            if (RegisterClass(ref value) == 0) throw new Win32Exception(); registered = true;
        }
        var hwnd = CreateWindowEx(desktop ? 0x080000a0u : 0u, SurfaceClass, "MagmPaper GLSL", parent != 0 ? Child | Visible : Popup,
            0, 0, 16, 16, parent, 0, GetModuleHandle(null), 0);
        if (hwnd == 0) throw new Win32Exception(); return hwnd;
    }
    public static MonitorInfo PrimaryMonitor()
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(0, 1), ref info)) throw new Win32Exception(); return info;
    }
    public static string ClassName(nint hwnd) { var value = new StringBuilder(256); GetClassName(hwnd, value, value.Capacity); return value.ToString(); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassW", SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)] internal static extern nint CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static extern nint DefWindowProc(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int mode);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect value);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect value);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern int MapWindowPoints(nint from, nint to, ref Point point, uint count);
    [DllImport("user32.dll")] internal static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint child);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] internal static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")] internal static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint hwnd, StringBuilder buffer, int count);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] internal static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wp, nint lp, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] internal static extern nint Broadcast(nint hwnd, uint message, nuint wp, string lp, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern bool GetLayeredWindowAttributes(nint hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int Cloaked(nint hwnd, uint key, out int value, uint size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int Bounds(nint hwnd, uint key, out Rect value, uint size);
    [DllImport("shell32.dll")] internal static extern nuint SHAppBarMessage(uint message, ref AppBarData value);
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWaitableTimer(nint security, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] internal static extern bool SetWaitableTimer(nint timer, ref long due, int period, nint callback, nint arg, bool resume);
    [DllImport("kernel32.dll")] internal static extern bool CancelWaitableTimer(nint timer);
    [DllImport("kernel32.dll")] internal static extern uint WaitForMultipleObjects(uint count, nint[] handles, bool all, uint milliseconds);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
}
