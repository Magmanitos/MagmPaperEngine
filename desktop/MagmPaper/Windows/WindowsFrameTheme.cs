using System.Windows;
using System.Windows.Threading;

namespace MagmPaper.Windows;

internal sealed class WindowsFrameTheme : IDisposable
{
    private const uint Destroyed = 0x8001, Shown = 0x8002;
    private const uint Corner = 33, Border = 34, Caption = 35, CaptionText = 36, Backdrop = 38;
    private const uint DefaultColor = 0xffffffffu;
    private readonly Dictionary<nint, Frame> frames = new();
    private readonly DispatcherTimer update;
    private readonly Win32.WinEventProc events;
    private nint hook;
    private Style? next;
    private bool hookAttempted;

    internal readonly record struct Style(uint Color, uint Text, WindowCorner Corners, WindowsWindowBackdrop Material);
    private sealed record Frame(uint Process, uint Thread)
    {
        internal Dictionary<uint, (uint Original, uint Applied)> Attributes { get; } = new();
        internal Style? Appearance { get; set; }
    }

    internal event Action<int>? Changed;

    internal WindowsFrameTheme(Dispatcher dispatcher)
    {
        events = OnWindowEvent;
        update = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(110) };
        update.Tick += (_, _) => ApplyChanges();
    }

    internal void Configure(Style? style)
    {
        if (style == next) return;
        next = style;
        if (style is null) { RestoreAll(); return; }
        if (!hookAttempted)
        {
            hookAttempted = true;
            const uint SkipOwnProcess = 2;
            hook = Win32.SetWinEventHook(Destroyed, Shown, 0, events, 0, 0, SkipOwnProcess);
        }
        Schedule();
    }

    private void OnWindowEvent(nint _, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != 0 || childId != 0 || window == 0 || next is null) return;
        if (kind == Destroyed) { if (frames.Remove(window)) Schedule(); return; }
        if (kind == Shown && Eligible(window))
        {
            if (frames.TryGetValue(window, out var frame) && frame.Attributes.Count == 0) frames.Remove(window);
            Schedule();
        }
    }

    private void Schedule()
    {
        if (!update.IsEnabled) update.Start();
    }

    private void ApplyChanges()
    {
        update.Stop();
        if (next is not { } style) return;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (Eligible(hwnd) && (!frames.TryGetValue(hwnd, out var frame) || frame.Appearance != style || !SameWindow(hwnd, frame)))
                Apply(hwnd, style);
            return true;
        }, 0);
        foreach (var (handle, frame) in frames.ToArray())
            if (!SameWindow(handle, frame)) frames.Remove(handle);
        Changed?.Invoke(frames.Values.Count(frame => frame.Attributes.Count > 0));
    }

    private static bool Eligible(nint hwnd)
    {
        const long CaptionStyle = 0x00C00000, ToolWindow = 0x80;
        return Win32.IsWindowVisible(hwnd) && Win32.GetWindow(hwnd, 4) == 0 &&
            (Win32.GetWindowLongPtr(hwnd, Win32.Style).ToInt64() & CaptionStyle) == CaptionStyle &&
            (Win32.GetWindowLongPtr(hwnd, Win32.ExtendedStyle).ToInt64() & ToolWindow) == 0 &&
            Win32.GetWindowThreadProcessId(hwnd, out uint process) != 0 && process != Environment.ProcessId &&
            Win32.Cloaked(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked == 0;
    }

    private static bool SameWindow(nint hwnd, Frame frame) =>
        Win32.GetWindowThreadProcessId(hwnd, out uint process) == frame.Thread && process == frame.Process;

    private void Apply(nint hwnd, Style style)
    {
        if (!frames.TryGetValue(hwnd, out var frame) || !SameWindow(hwnd, frame))
        {
            uint thread = Win32.GetWindowThreadProcessId(hwnd, out uint process);
            if (thread == 0) return;
            frames[hwnd] = frame = new(process, thread);
        }
        ApplyAttribute(hwnd, frame, Border, style.Color, DefaultColor);
        if (style.Material == WindowsWindowBackdrop.System) RestoreAttribute(hwnd, frame, Backdrop);
        if (style.Material != WindowsWindowBackdrop.System && ApplyAttribute(hwnd, frame, Backdrop, (uint)style.Material))
        {
            RestoreAttribute(hwnd, frame, Caption);
            RestoreAttribute(hwnd, frame, CaptionText);
        }
        else
        {
            ApplyAttribute(hwnd, frame, Caption, style.Color, DefaultColor);
            ApplyAttribute(hwnd, frame, CaptionText, style.Text, DefaultColor);
        }
        if (style.Corners == WindowCorner.System) RestoreAttribute(hwnd, frame, Corner);
        else ApplyAttribute(hwnd, frame, Corner, (uint)style.Corners);
        frame.Appearance = style;
    }

    private static bool Read(nint hwnd, uint attribute, out uint value)
    {
        int result = Win32.Cloaked(hwnd, attribute, out int raw, sizeof(int));
        value = unchecked((uint)raw);
        return result == 0;
    }

    private static bool ApplyAttribute(nint hwnd, Frame frame, uint attribute, uint value, uint fallback = 0)
    {
        bool readable = Read(hwnd, attribute, out uint now);
        bool saved = frame.Attributes.TryGetValue(attribute, out var previous);
        if (readable ? now == value : saved && previous.Applied == value) return true;
        uint original = saved && (!readable || previous.Applied == now) ? previous.Original : readable ? now : fallback;
        if (Win32.DwmSetWindowAttribute(hwnd, attribute, value, sizeof(uint)) != 0) return false;
        frame.Attributes[attribute] = (original, value);
        return true;
    }

    private static void RestoreAttribute(nint hwnd, Frame frame, uint attribute)
    {
        if (frame.Attributes.Remove(attribute, out var saved) && (!Read(hwnd, attribute, out uint now) || now == saved.Applied))
            _ = Win32.DwmSetWindowAttribute(hwnd, attribute, saved.Original, sizeof(uint));
    }

    private void RestoreAll()
    {
        update.Stop();
        if (hook != 0) { _ = Win32.UnhookWinEvent(hook); hook = 0; }
        hookAttempted = false;
        foreach (var (hwnd, frame) in frames)
            if (SameWindow(hwnd, frame))
                foreach (uint attribute in frame.Attributes.Keys.ToArray()) RestoreAttribute(hwnd, frame, attribute);
        frames.Clear();
        Changed?.Invoke(0);
    }

    public void Dispose()
    {
        next = null;
        RestoreAll();
    }
}
