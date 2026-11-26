using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MagmPaper.Windows;

internal sealed class DesktopIconLayout : IDisposable
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private readonly IFolderView view;
    private readonly List<(nint Item, Win32.Point Position)> icons = [];

    private DesktopIconLayout(IFolderView view) => this.view = view;

    internal static DesktopIconLayout? Capture()
    {
        DesktopIconLayout? layout = null;
        try
        {
            if (OpenDesktopView() is not { } view) return null;
            layout = new(view);
            // S_FALSE means Explorer is keeping manually assigned positions.
            const uint allItems = 2;
            if (view.GetAutoArrange() == 1 && view.ItemCount(allItems, out int count) == 0)
            {
                for (int index = 0; index < count; index++)
                {
                    if (view.Item(index, out nint item) != 0 || item == 0) continue;
                    if (view.GetItemPosition(item, out var position) == 0) layout.icons.Add((item, position));
                    else Marshal.FreeCoTaskMem(item);
                }
            }
            if (layout.icons.Count != 0) return layout;
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { }
        layout?.Dispose();
        return null;
    }

    internal void RestoreAfterLayout(Area previousWork)
    {
        // Explorer can acknowledge the appbar flag before it lays out the desktop.
        // Allow a late work-area update, then let the view finish its reflow.
        long start = Environment.TickCount64, quietSince = start;
        bool workChanged = false;
        try
        {
            while (true)
            {
                var work = Win32.PrimaryMonitor().Work.Area;
                long now = Environment.TickCount64;
                if (work != previousWork) { previousWork = work; quietSince = now; workChanged = true; }
                if (now - start >= 480 || (workChanged ? now - quietSince >= 160 : now - start >= 300)) break;
                Thread.Sleep(25);
            }
        }
        catch (Win32Exception) { }
        RestoreMovedIcons();
    }

    private void RestoreMovedIcons()
    {
        try
        {
            if (view.GetAutoArrange() != 1) return;
            var items = new List<nint>();
            var positions = new List<Win32.Point>();
            foreach (var (item, before) in icons)
            {
                // An item may have been removed while Explorer updated its work area.
                if (view.GetItemPosition(item, out var now) != 0 || (now.X == before.X && now.Y == before.Y)) continue;
                items.Add(item);
                positions.Add(before);
            }
            if (items.Count != 0)
            {
                const uint positionItems = 0x80, noFocus = 0x40000000;
                view.SelectAndPositionItems(items.Count, items.ToArray(), positions.ToArray(), positionItems | noFocus);
            }
        }
        catch (COMException) { } // Explorer may have restarted since capture.
    }

    private static IFolderView? OpenDesktopView()
    {
        var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass)!)!;
        object? desktop = null, browser = null, shellView = null;
        try
        {
            object location = 0, root = null!;
            const int desktopClass = 8, needDispatch = 1;
            desktop = windows.FindWindowSW(ref location, ref root, desktopClass, out _, needDispatch);
            if (desktop is not IServiceProvider services) return null;
            var browserId = typeof(IShellBrowser).GUID;
            if (services.QueryService(TopLevelBrowser, browserId, out browser) != 0 || browser is not IShellBrowser site ||
                site.QueryActiveShellView(out shellView) != 0 || shellView is not IFolderView result) return null;
            shellView = null; // Ownership passes to the snapshot.
            return result;
        }
        finally
        {
            if (shellView is not null) Marshal.ReleaseComObject(shellView);
            if (browser is not null) Marshal.ReleaseComObject(browser);
            if (desktop is not null) Marshal.ReleaseComObject(desktop);
            Marshal.ReleaseComObject(windows);
        }
    }

    public void Dispose()
    {
        foreach (var (item, _) in icons) Marshal.FreeCoTaskMem(item);
        icons.Clear();
        Marshal.ReleaseComObject(view);
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [DispId(0x60020008)]
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([In] ref object location, [In] ref object root, int type, out int window, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(in Guid service, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void _VtblGap1_12();
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object? result);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void _VtblGap1_3();
        [PreserveSig] int Item(int index, out nint item);
        [PreserveSig] int ItemCount(uint flags, out int count);
        void _VtblGap2_3();
        [PreserveSig] int GetItemPosition(nint item, out Win32.Point point);
        void _VtblGap3_2();
        [PreserveSig] int GetAutoArrange();
        void _VtblGap4_1();
        [PreserveSig] int SelectAndPositionItems(int count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] nint[] items,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Win32.Point[] points, uint flags);
    }
}
