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
        surface.Configure(new(Paused: true));
        await surface.LoadAsync(Red); var red = await surface.CaptureAsync();
        int center = (red.Width * (red.Height / 2) + red.Width / 2) * 4;
        Assert(red.Rgba[center] > 240 && red.Rgba[center + 1] < 10, "ANGLE did not produce actual GLSL pixels");
        bool rejected = false;
        try { await surface.LoadAsync("bad shader"); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "invalid GLSL was accepted");
        Assert((await surface.CaptureAsync()).Rgba[center] > 240, "invalid GLSL replaced the previous program");
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
        var host = Win32.CreateSurface(0); nint child = 0;
        try
        {
            Win32.SetWindowPos(host, 0, 37, 53, 800, 600, 0x14);
            child = Win32.CreateSurface(0, true); DesktopHost.Fit(child, host); Win32.GetClientRect(child, out var rect);
            var monitor = Win32.PrimaryMonitor().Monitor; Assert(rect.Width == monitor.Width && rect.Height == monitor.Height, "native wallpaper has borders or incorrect physical geometry");
            var origin = new Win32.Point(0, 0); Win32.MapWindowPoints(child, 0, ref origin, 1);
            Assert(origin.X == monitor.Left && origin.Y == monitor.Top, "wallpaper origin does not match monitor");
            Assert(((uint)Win32.GetWindowLongPtr(child, -20) & 0x20) != 0, "wallpaper is not click-through");
            using var secondSurface = new RenderSurface(child); await secondSurface.LoadAsync(Red);
            surface.Configure(new(Paused: false)); long before = surface.Frames; AngleDevice.Reset(AngleDevice.Generation);
            await Until(() => surface.Frames > before + 3, "GPU contexts did not recreate after device reset");
            surface.Configure(new(Paused: true));
        }
        finally { if (child != 0) Win32.DestroyWindow(child); Win32.DestroyWindow(host); }
        // Exercise real WPF controls and their async handlers, not a renderer mock.
        window.AddWallpaperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.NameInput.Text = "Нативный UI тест"; window.CodeInput.Text = Red;
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
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var image = File.Create(Path.Combine(output, "native-interface.png"))) encoder.Save(image);
        long idleFrames = surface.Frames; await Task.Delay(150); idleFrames = surface.Frames; await Task.Delay(400);
        Assert(surface.Frames == idleFrames, "hidden preview consumes frames");
        var process = Process.GetCurrentProcess(); var startCpu = process.TotalProcessorTime; var clockIdle = Stopwatch.StartNew(); await Task.Delay(2000); process.Refresh();
        var report = new { passed = true, nativeUi = "WPF", gpuTests = "ANGLE D3D11 WARP", chromiumProcesses = Process.GetProcessesByName("MagmPaper").Count(p => p.Id != Environment.ProcessId), workingSetBytes = process.WorkingSet64, idleCpuMilliseconds = (process.TotalProcessorTime - startCpu).TotalMilliseconds, idleSampleMilliseconds = clockIdle.Elapsed.TotalMilliseconds,
            tests = new[] { "real GLSL pixels", "compile error preserves program", "backbuffer", "quality fills window", "pause stops frames", "physical monitor geometry", "click-through", "GPU reset with paused second context", "WPF editor/save/navigation", "settings", "hidden preview" } };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, DataFiles.Json));
    }
}
