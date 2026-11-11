using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MagmPaper.Graphics;
using MagmPaper.Windows;

namespace MagmPaper;

internal static class SmokeTests
{
    private const string Red = "precision mediump float; void main(){gl_FragColor=vec4(1.0,0.0,0.0,1.0);}";
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static IEnumerable<TextBlock> TextElements(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock text) yield return text;
            foreach (var descendant in TextElements(child)) yield return descendant;
        }
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte value) { double channel = value / 255.0; return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
    private static void AssertReadable(Brush foreground, Brush background, string label)
    {
        Assert(foreground is SolidColorBrush && background is SolidColorBrush, $"{label}: missing opaque theme brushes");
        double text = Luminance(((SolidColorBrush)foreground).Color), surface = Luminance(((SolidColorBrush)background).Color);
        Assert((Math.Max(text, surface) + .05) / (Math.Min(text, surface) + .05) >= 4.5, $"{label}: unreadable text contrast");
    }
    private static async Task Until(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(message); await Task.Delay(30); }
    }
    public static async Task RunAsync(MainWindow window, string output)
    {
        Directory.CreateDirectory(output);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.Navigate("wallpapers");
        await Until(() => !window.Busy && window.PreviewSurface is not null, "native preview did not initialize");
        var surface = window.PreviewSurface!;
        Assert(Win32.GetLayeredWindowAttributes(window.Preview.NativeWindow, out _, out byte previewOpacity, out _) && previewOpacity == 255, "preview bypasses DWM composition");
        surface.Configure(new(Paused: true));
        await surface.LoadAsync(Red); var red = await surface.CaptureAsync();
        int center = (red.Width * (red.Height / 2) + red.Width / 2) * 4;
        Assert(red.Rgba[center] > 240 && red.Rgba[center + 1] < 10, "ANGLE did not produce actual GLSL pixels");
        bool rejected = false;
        try { await surface.LoadAsync("bad shader"); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "invalid GLSL was accepted");
        Assert((await surface.CaptureAsync()).Rgba[center] > 240, "invalid GLSL replaced the previous program");
        await surface.LoadAsync("#version 100\nprecision mediump float;\n#extension GL_OES_standard_derivatives : enable\nvoid main(){gl_FragColor=vec4(dFdx(gl_FragCoord.x),0.0,0.0,1.0);}");
        Assert((await surface.CaptureAsync()).Rgba[center] > 240, "legacy late extension did not compile/render");
        await surface.LoadAsync("precision mediump float; uniform vec2 resolution; uniform sampler2D backbuffer; void main(){gl_FragColor=vec4(texture2D(backbuffer,gl_FragCoord.xy/resolution).r+0.1,0.0,0.0,1.0);}");
        byte first = (await surface.CaptureAsync()).Rgba[center], second = (await surface.CaptureAsync()).Rgba[center];
        Assert(second > first + 15, "GLSL backbuffer accumulation failed");
        surface.Configure(new(Paused: true, Quality: .5));
        await surface.LoadAsync(Red); var scaled = await surface.CaptureAsync();
        Assert(scaled.Width == red.Width && scaled.Rgba[0] > 240 && scaled.Rgba[^4] > 240, "scaled rendering does not fill the output surface");
        surface.Configure(new(Paused: false, Fps: 30));
        await Until(() => surface.Frames > 2, "render loop did not start");
        surface.Configure(new(Paused: true)); await Task.Delay(150); var count = surface.Frames; await Task.Delay(350);
        Assert(surface.Frames == count, "paused preview keeps rendering");
        Diagnostics.Trace("Pixels, shader rollback, backbuffer, scaling and pause passed");
        nint previousDpi = Win32.SetThreadDpiAwarenessContext(-2), dpiParent;
        try { dpiParent = Win32.CreateSurface(0); }
        finally { Win32.SetThreadDpiAwarenessContext(previousDpi); }
        nint dpiChild = 0;
        try
        {
            dpiChild = Win32.CreateDesktopSurface(dpiParent);
            Assert(Win32.AreDpiAwarenessContextsEqual(Win32.GetWindowDpiAwarenessContext(dpiParent), Win32.GetWindowDpiAwarenessContext(dpiChild)), "desktop child DPI differs from Explorer host");
            Assert(Win32.AreDpiAwarenessContextsEqual(Win32.GetThreadDpiAwarenessContext(), previousDpi), "wallpaper creation changed WPF thread DPI");
        }
        finally { if (dpiChild != 0) Win32.DestroyWindow(dpiChild); Win32.DestroyWindow(dpiParent); }
        var host = Win32.CreateSurface(0); nint child = 0;
        bool? composedPixels = null;
        try
        {
            Win32.SetWindowPos(host, 0, 37, 53, 800, 600, 0x14);
            child = Win32.CreateSurface(0, true); DesktopHost.Fit(child, host); Win32.GetClientRect(child, out var rect);
            var monitor = Win32.PrimaryMonitor().Monitor; Assert(rect.Width == monitor.Width && rect.Height == monitor.Height, "native wallpaper has borders or incorrect physical geometry");
            var origin = new Win32.Point(0, 0); Win32.MapWindowPoints(child, 0, ref origin, 1);
            Assert(origin.X == monitor.Left && origin.Y == monitor.Top, "wallpaper origin does not match monitor");
            Assert(((uint)Win32.GetWindowLongPtr(child, -20) & 0x20) != 0, "wallpaper is not click-through");
            Assert(Win32.GetLayeredWindowAttributes(child, out _, out byte opacity, out uint layerFlags) && opacity == 255 && (layerFlags & 2) != 0, "wallpaper is not using opaque DWM composition");
            nint icons = Win32.CreateSurface(host), staticBackground = Win32.CreateSurface(host);
            try
            {
                DesktopHost.Fit(child, host, icons, staticBackground);
                Assert(Win32.GetParent(child) == host && Win32.GetWindow(child, 3) == icons && Win32.GetWindow(staticBackground, 2) == 0, "24H2 wallpaper must sit between icons and static background");
            }
            finally { Win32.DestroyWindow(icons); Win32.DestroyWindow(staticBackground); }
            Win32.ShowWindow(host, 4);
            Win32.SetWindowPos(host, 0, monitor.Left, monitor.Top, monitor.Width, monitor.Height, 0x14);
            await Task.Delay(100);
            Assert(DesktopHost.Inspect(host, child, Win32.PrimaryMonitor().Work.Area) == 1, "fullscreen native window did not pause wallpaper");
            Win32.SetWindowPos(host, 0, 37, 53, monitor.Width / 2, monitor.Height / 2, 0x14);
            await Task.Delay(100);
            Assert(DesktopHost.Inspect(host, child, Win32.PrimaryMonitor().Work.Area) == 0, "partial window incorrectly pauses wallpaper");
            Win32.SetWindowPos(host, 0, monitor.Left, monitor.Top, monitor.Width, monitor.Height, 0x14);
            Win32.ShowWindow(host, 6);
            Assert(DesktopHost.Inspect(host, child, Win32.PrimaryMonitor().Work.Area) == 0, "minimized window incorrectly pauses wallpaper");
            Win32.ShowWindow(host, 4); Win32.SetWindowLongPtr(host, -20, 0x20);
            Assert(DesktopHost.Inspect(host, child, Win32.PrimaryMonitor().Work.Area) == 0, "click-through overlay incorrectly pauses wallpaper");
            DesktopHost.Fit(child, host);
            using var secondSurface = new RenderSurface(child, true); await secondSurface.LoadAsync(Red);
            nint inputDesktop = Win32.OpenInputDesktop(0, false, 1);
            if (inputDesktop != 0)
            {
                Win32.CloseDesktop(inputDesktop);
                Win32.SetWindowPos(host, -1, monitor.Left, monitor.Top, monitor.Width, monitor.Height, 0x50);
                secondSurface.Configure(new(Paused: false));
                // Verify a presented pixel on the actual composed desktop, not
                // just glReadPixels from the application's own framebuffer.
                await Until(() =>
                {
                    nint dc = Win32.GetDC(0);
                    try { return dc != 0 && Win32.GetPixel(dc, monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2) == 0x000000ff; }
                    finally { if (dc != 0) Win32.ReleaseDC(0, dc); }
                }, "opaque layered GLSL surface is not visible on the composed desktop");
                composedPixels = true;
            }
            await secondSurface.LoadAsync("precision mediump float; uniform vec2 mouse; void main(){gl_FragColor=vec4(mouse,0.0,1.0);}");
            Win32.GetCursorPos(out var oldCursor);
            try
            {
                // The surface is transparent to mouse input but reads the real OS cursor.
                var upperRight = new Win32.Point(monitor.Left + monitor.Width * 3 / 4, monitor.Top + monitor.Height / 4);
                Assert(Win32.SetCursorPos(upperRight.X, upperRight.Y), "could not position test cursor");
                secondSurface.Configure(new(Paused: false)); await Task.Delay(250);
                var mousePixels = await secondSurface.CaptureAsync();
                Assert(Math.Abs(mousePixels.Rgba[0] - 191) < 5 && Math.Abs(mousePixels.Rgba[1] - 191) < 5, "desktop cursor did not reach GLSL uniforms");
                secondSurface.Configure(new(Paused: false, MouseEnabled: false)); await Task.Delay(100);
                mousePixels = await secondSurface.CaptureAsync();
                Assert(Math.Abs(mousePixels.Rgba[0] - 128) < 3 && Math.Abs(mousePixels.Rgba[1] - 128) < 3, "disabled mouse is not centered");
                secondSurface.Configure(new(Paused: true));
            }
            finally { Win32.SetCursorPos(oldCursor.X, oldCursor.Y); }
            for (int attempt = 0; attempt < 6; attempt++)
            {
                bool otherPaused = attempt % 2 == 0;
                secondSurface.Configure(new(Paused: otherPaused, Quality: .5));
                surface.Configure(new(Paused: false));
                await Task.Delay(15 + attempt * 7);
                long before = surface.Frames; AngleDevice.Reset(AngleDevice.Generation);
                await Until(() => surface.Frames > before + 3, "GPU contexts did not recreate after device reset");
                Assert((await surface.CaptureAsync()).Rgba[0] > 240, "preview pixels were lost after reset");
            }
            secondSurface.Configure(new(Paused: true));
            surface.Configure(new(Paused: true));
        }
        finally { if (child != 0) Win32.DestroyWindow(child); Win32.DestroyWindow(host); }
        Diagnostics.Trace("Native monitor geometry, coverage, cursor and GPU reset passed");
        // Real Windows autostart round-trip under a unique value; never edit
        // the user's actual MagmPaper preference or StartupApproved entry.
        string startupTestName = "MagmPaper.Smoke." + Guid.NewGuid();
        using (var startup = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        using (var approved = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"))
        {
            try
            {
                Assert(!SystemSettings.ReadStartup(startupTestName), "test autostart already exists");
                SystemSettings.WriteStartup(startupTestName, true);
                Assert((string?)startup.GetValue(startupTestName) == $"\"{Environment.ProcessPath}\" --autostart", "autostart executable was not quoted");
                approved.SetValue(startupTestName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Microsoft.Win32.RegistryValueKind.Binary);
                Assert(!SystemSettings.ReadStartup(startupTestName), "StartupApproved disabled state was ignored");
                SystemSettings.WriteStartup(startupTestName, true);
                Assert(SystemSettings.ReadStartup(startupTestName), "autostart did not reenable");
                SystemSettings.WriteStartup(startupTestName, false);
                Assert(!SystemSettings.ReadStartup(startupTestName), "autostart did not disable");
            }
            finally { startup.DeleteValue(startupTestName, false); approved.DeleteValue(startupTestName, false); }
        }
        // Exercise real WPF controls and their async handlers, not a renderer mock.
        window.AddWallpaperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.NameInput.Text = "Нативный UI тест"; window.CodeInput.Text = Red;
        window.WallpaperScroll.ScrollToVerticalOffset(140);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        nint region = Win32.CreateRectRgn(0, 0, 0, 0);
        try
        {
            Assert(!Win32.IsWindowVisible(window.Preview.NativeWindow) ||
                (Win32.GetWindowRgn(window.Preview.NativeWindow, region) != 0 && Win32.GetRgnBox(region, out var clipping) != 0 && clipping.Top > 0), "scrolled native preview overlaps WPF controls");
        }
        finally { Win32.DeleteObject(region); }
        window.WallpaperScroll.ScrollToTop();
        window.Navigate("settings");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert(!Win32.IsWindowVisible(window.Preview.NativeWindow), "native preview is still visible over settings");
        window.Navigate("settings"); window.Navigate("wallpapers");
        Assert(window.NameInput.Text == "Нативный UI тест" && window.EditorPanel.Visibility == Visibility.Visible, "navigation lost editor draft");
        window.SaveWallpaperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.Busy, "WPF save did not complete");
        Assert(window.Library.State.Items.Any(x => x.Name == "Нативный UI тест" && x.Code == Red), "WPF save did not persist GLSL");
        var added = window.Library.State.Items.First(x => x.Name == "Нативный UI тест");
        window.Library.Remove(added.Id);
        window.Navigate("settings"); window.FpsInput.SelectedIndex = 1;
        await Until(() => !window.Busy, "settings did not save"); Assert(window.Library.State.Settings.Fps == 30, "native performance preference lost");
        window.Navigate("taskbar");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (window.TaskbarPage.ScrollableHeight > 80)
        {
            var wheelTarget = TextElements(window.TaskbarPage).First(text => text.Text == "Панель задач");
            wheelTarget.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
            await Task.Delay(300);
            double expected = Math.Min(window.TaskbarPage.ScrollableHeight, SystemParameters.WheelScrollLines < 0 ? window.TaskbarPage.ViewportHeight : SystemParameters.WheelScrollLines * 22);
            Assert(Math.Abs(window.TaskbarPage.VerticalOffset - expected) < 1, "mouse wheel did not smoothly scroll the page");
            window.TaskbarPage.ScrollToTop();
        }
        window.AlignmentInput.IsDropDownOpen = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var dropdown = (System.Windows.Controls.Primitives.Popup)window.AlignmentInput.Template.FindName("PART_Popup", window.AlignmentInput);
        Assert(dropdown.IsOpen && dropdown.Child.IsVisible && dropdown.Child.RenderSize.Width >= window.AlignmentInput.ActualWidth - 1, "themed dropdown did not open at control width");
        AssertReadable(window.AlignmentInput.Foreground, window.AlignmentInput.Background, "dropdown selection");
        window.AlignmentInput.IsDropDownOpen = false;
        var picker = new Views.ColorPicker("#0F8CDB") { Owner = window };
        picker.Show(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert(picker.SelectedColor == "#0F8CDB", "HSV picker changed the initial RGB color");
        picker.Hex.Text = "#123456"; Assert(picker.SelectedColor == "#123456" && picker.Accept.IsEnabled, "HEX entry did not update color");
        picker.Hex.Text = "#12"; Assert(!picker.Accept.IsEnabled, "invalid color can be accepted");
        picker.SetColor("#BCA6FF");
        CaptureWindow(picker, Path.Combine(output, "color-picker.png"));
        picker.Close();
        // Check rendered control text as well as the window. Implicit Window
        // styles do not automatically apply to a derived MainWindow class.
        Assert(window.Background is SolidColorBrush background && Luminance(background.Color) < .05, "native window lost its dark background");
        AssertReadable(window.Foreground, window.Background, "window");
        var heading = TextElements(window.TaskbarPage).Single(text => text.Text == "Поведение панели");
        AssertReadable(heading.Foreground, (Brush)window.FindResource("PanelBrush"), "card heading");
        var buttonText = TextElements(window.AddWallpaperButton).First(text => !string.IsNullOrEmpty(text.Text));
        AssertReadable(buttonText.Foreground, window.AddWallpaperButton.Background, "primary button");
        CaptureWindow(window, Path.Combine(output, "native-interface.png"));
        long idleFrames = surface.Frames; await Task.Delay(150); idleFrames = surface.Frames; await Task.Delay(400);
        Assert(surface.Frames == idleFrames, "hidden preview consumes frames");
        await Task.Delay(3000); // Let startup compilation, layout and deferred GPU release settle.
        var process = Process.GetCurrentProcess(); var startCpu = process.TotalProcessorTime; var clockIdle = Stopwatch.StartNew(); await Task.Delay(5000); process.Refresh();
        var report = new { passed = true, nativeUi = "WPF", gpuTests = "ANGLE D3D11 WARP", composedDesktopPixels = composedPixels, workingSetBytes = process.WorkingSet64, idleCpuMilliseconds = (process.TotalProcessorTime - startCpu).TotalMilliseconds, idleSampleMilliseconds = clockIdle.Elapsed.TotalMilliseconds,
            tests = new[] { "real GLSL pixels", "late GLSL extension compatibility", "compile error preserves program", "backbuffer", "quality fills window", "pause stops frames", "physical monitor geometry", "24H2 child layer order and opaque composition", "native window coverage/minimize/transparent overlay", "click-through and real cursor uniforms", "mouse toggle", "six GPU resets with active and paused second context", "Windows autostart and StartupApproved", "WPF editor/save/navigation", "native preview visibility and scroll clipping", "smooth scrolling", "HSV and HEX color picker", "settings", "WPF theme contrast", "hidden preview" } };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, DataFiles.Json));
    }
    private static void CaptureWindow(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var image = File.Create(path); encoder.Save(image);
    }
}
