using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
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
    private static void AssertDarkSurface(Window window, FrameworkElement control, string label)
    {
        var bitmap = WindowBitmap(window);
        foreach (var local in new[] { new Point(2, 2), new Point(control.ActualWidth / 2, control.ActualHeight - 2) })
        {
            var point = control.TranslatePoint(local, window); var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
            Assert(pixel[0] < 120 && pixel[1] < 120 && pixel[2] < 120, $"{label}: disabled/inactive control turned light");
        }
    }
    private static bool Wheel(UIElement source, int delta)
    {
        var args = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta)
            { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent };
        source.RaiseEvent(args); return args.Handled;
    }
    private static void CheckRoundedFrameBlur()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            Views.WindowFrameBlur.DrawContour(context, new(Intensity: 100), new Size(100, 80), new Size(24, 24), new Size(14, 14));

        var bitmap = new RenderTargetBitmap(148, 128, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte Alpha(int x, int y)
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return pixel[3];
        }

        foreach (bool right in new[] { false, true })
        foreach (bool bottom in new[] { false, true })
        {
            byte AtCorner(int x, int y) => Alpha(right ? 147 - x : x, bottom ? 127 - y : y);
            Assert(AtCorner(25, 25) > 0, "rounded blur did not reach the transparent corner of the notes card");
            Assert(AtCorner(34, 34) == 0, "rounded blur covered the interior of the notes card");
        }
    }

    private static async Task CheckToggleMotion(CheckBox checkBox)
    {
        checkBox.ApplyTemplate();
        var knob = (FrameworkElement)checkBox.Template.FindName("Knob", checkBox);
        bool? original = checkBox.IsChecked;
        checkBox.IsChecked = false;
        await Until(() => knob.RenderTransform.Value.OffsetX == 0, "toggle did not return to the off position");
        checkBox.IsChecked = true;
        var motion = knob.RenderTransform as TranslateTransform;
        Assert(motion is not null && motion.HasAnimatedProperties == SystemParameters.ClientAreaAnimation,
            "toggle did not respect the system animation preference");
        await Until(() => knob.RenderTransform.Value.OffsetX == 16, "toggle did not reach the on position");
        checkBox.IsChecked = original;
        await Until(() => knob.RenderTransform.Value.OffsetX == (original == true ? 16 : 0), "toggle did not restore its position");
    }

    private static async Task UseWallpaperDialogAsync(MainWindow window, Button button, Action<Views.WallpaperDialog> interact)
    {
        var interaction = window.Dispatcher.InvokeAsync(() =>
        {
            var dialog = window.OwnedWindows.OfType<Views.WallpaperDialog>().SingleOrDefault(item => item.IsVisible)
                ?? throw new InvalidOperationException("wallpaper dialog did not open");
            try { interact(dialog); }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await interaction;
    }

    private static void CheckWallpaperNameLabel(Views.WallpaperDialog dialog)
    {
        dialog.UpdateLayout();
        var label = TextElements(dialog.NamePanel).Single(text => text.Text == "Название обоев");
        AssertReadable(label.Foreground, dialog.DialogCard.Background, "wallpaper name label");
    }

    private static async Task CheckMediaAsync(MainWindow window)
    {
        foreach (var (extension, format) in new[] { ("gif", WallpaperFormat.Gif), ("mp4", WallpaperFormat.Mp4) })
        {
            string file = Path.Combine(window.Library.DirectoryPath, $"preview.{extension}");
            using (var fixture = typeof(SmokeTests).Assembly.GetManifestResourceStream($"MagmPaper.Smoke.sample.{extension}")!)
            using (var output = File.Create(file)) fixture.CopyTo(output);
            window.Navigate("wallpapers");
            await Until(() => !window.Busy, "gallery did not return before media import");
            string name = $"Обои {extension} на заказ";
            await UseWallpaperDialogAsync(window, window.AddWallpaperButton, dialog =>
            {
                dialog.ChooseMedia(file, format);
                CheckWallpaperNameLabel(dialog);
                Assert(dialog.NameInput.Text == "preview" && dialog.NamePanel.Visibility == Visibility.Visible,
                    "media file did not open a name field with the suggested name");
                dialog.NameInput.Text = "   ";
                Assert(!dialog.AcceptName.IsEnabled, "wallpaper dialog allowed an empty name");
                dialog.NameInput.Text = name;
                dialog.AcceptName.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            await Until(() => !window.Busy, $"{extension} import did not finish");
            Assert(window.MediaPreview.MediaReady && window.ViewCodeButton.Visibility == Visibility.Collapsed &&
                window.WallpaperTitle.Text == name && window.RenameWallpaperButton.IsVisible,
                $"{extension} did not open in the media preview");
            var wallpaper = window.Library.State.Items.Single(item => item.Format == format);
            Assert(wallpaper.Name == name && File.Exists(window.Library.MediaPath(wallpaper)), $"{extension} import lost the chosen name or file");
            string renamed = $"Новое имя для {extension}";
            await UseWallpaperDialogAsync(window, window.RenameWallpaperButton, dialog =>
            {
                Assert(dialog.NameInput.Text == name, "rename dialog did not open with the current name");
                dialog.NameInput.Text = renamed;
                dialog.AcceptName.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert(window.WallpaperTitle.Text == renamed && window.Library.Get(wallpaper.Id) == wallpaper with { Name = renamed } &&
                window.MediaPreview.MediaReady, $"{extension} rename did not update the name while preserving playback and the file");
            await Until(() => window.MediaPreview.Playing && window.MediaPreview.PlaybackPosition > TimeSpan.FromMilliseconds(100),
                $"{extension} animation did not advance");
            if (format == WallpaperFormat.Gif)
                await Until(() => window.MediaPreview.Frames > 0, "GIF never changed frames");
            nint native = Win32.CreateSurface(0, composed: true);
            try
            {
                Win32.SetWindowPos(native, 0, 0, 0, 320, 180, 0x14);
                using var renderer = new DesktopMediaSurface(native);
                await renderer.LoadAsync(window.Library.MediaPath(wallpaper));
                renderer.Configure(new(Paused: false, Fps: 30, Quality: .75));
                if (format == WallpaperFormat.Gif)
                    await Until(() => renderer.Frames > 0, "GIF desktop surface never changed frames");
                nint child = Win32.GetWindow(native, 5);
                Assert(child != 0 && Win32.GetClientRect(child, out var bounds) && bounds.Width == 320 && bounds.Height == 180,
                    $"{extension} desktop surface did not fit the host HWND");
                renderer.Configure(new(Paused: true, Fps: 30, Quality: .75));
            }
            finally { Win32.DestroyWindow(native); }
            window.Navigate("settings");
            Assert(!window.MediaPreview.Playing, $"{extension} kept playing on another page");
            window.Navigate("wallpapers");
            await Until(() => !window.Busy, "media detail did not resume");
            window.BackWallpaperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var card = window.Gallery.Cards.Single(tile => tile.Item.Id == wallpaper.Id);
            await Until(() => card.Loaded && (card.Still is not null || card.PreviewStatus.StartsWith("▶")), $"{extension} gallery poster did not load");
            Assert(card.Item.Name == renamed, $"{extension} gallery did not show the new name");
            window.Library.Remove(wallpaper.Id);
            window.Navigate("wallpapers");
            await Until(() => !window.Busy, "gallery did not reload after removing the media");
            Assert(File.Exists(file), $"{extension} removal deleted the original file");
        }
    }

    public static async Task RunAsync(MainWindow window, string output)
    {
        Directory.CreateDirectory(output);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.Navigate("wallpapers");
        await Until(() => !window.Busy && window.Gallery.Cards.Count > 0, "gallery did not load the collection");
        var firstCard = window.Gallery.Cards[0];
        await Until(() => firstCard.Still is not null, "gallery did not generate a GLSL thumbnail");
        window.Gallery.StartHover(firstCard, window.Gallery);
        await Until(() => firstCard.Image is WriteableBitmap, "hover did not start the gallery renderer");
        window.Gallery.ClearHover();
        Assert(ReferenceEquals(firstCard.Image, firstCard.Still), "leaving the card did not restore its thumbnail");
        using (var renderer = new GalleryRenderer())
        {
            await renderer.LoadAsync("precision mediump float; uniform vec2 mouse; uniform float time; void main(){gl_FragColor=vec4(mouse.x,mouse.y,time,1.0);}");
            byte[] cardFrame = await renderer.FrameAsync(.4, .7, .2);
            Assert(Math.Abs(cardFrame[0] - 102) < 3 && Math.Abs(cardFrame[1] - 51) < 3 && Math.Abs(cardFrame[2] - 178) < 3,
                "gallery shader did not receive the hover time and pointer uniforms");
        }
        await window.OpenWallpaperAsync(firstCard.Item);
        await Until(() => !window.Busy && window.PreviewSurface is not null, "native preview did not initialize");
        var surface = window.PreviewSurface!;
        Assert(Win32.GetLayeredWindowAttributes(window.Preview.NativeWindow, out _, out byte previewOpacity, out _) && previewOpacity == 255, "preview bypasses DWM composition");
        surface.Configure(new(Paused: true));
        await surface.LoadAsync(Red); var red = await surface.CaptureAsync();
        int center = (red.Width * (red.Height / 2) + red.Width / 2) * 4;
        Assert(red.Rgba[center] > 240 && red.Rgba[center + 1] < 10, "ANGLE did not produce actual GLSL pixels");
        await surface.LoadAsync("precision mediump float; uniform vec2 resolution; void main(){gl_FragColor=gl_FragCoord.x<resolution.x*0.5 ? vec4(1.0,0.0,0.0,1.0) : vec4(0.0,0.0,1.0,1.0);}");
        var strip = await surface.CaptureAsync(captureRows: 12);
        Assert(strip.Width == red.Width && strip.Height == Math.Min(12, red.Height) &&
            WallpaperColors.FromRgba(strip.Rgba, strip.Width, strip.Height) == new WallpaperColors("#FF0000", "#0000FF"),
            "wallpaper palette was not extracted from the GLSL frame");
        await surface.LoadAsync(Red);
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
        window.ViewCodeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(window.EditorPanel.Visibility == Visibility.Visible && window.CodeInput.IsReadOnly && window.CodeInput.Text == firstCard.Item.Code,
            "open wallpaper did not expose its GLSL code");
        window.EditWallpaperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(!window.CodeInput.IsReadOnly, "existing wallpaper code could not be edited");
        window.BackWallpaperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.AddShaderAsync();
        await Until(() => !window.Busy, "new wallpaper preview did not load");
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
        window.BackWallpaperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Library.Remove(added.Id);
        window.Navigate("wallpapers"); await Until(() => !window.Busy, "library refresh did not complete");
        var collectionBeforeActivation = window.Gallery.Cards;
        Assert(window.GalleryPage.Visibility == Visibility.Visible && window.WallpaperScroll.Visibility == Visibility.Collapsed,
            "returning to the gallery kept the detail view visible");
        var other = new Window { Owner = window, Title = "Activation test", Width = 200, Height = 120, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)window.FindResource("PanelBrush") };
        other.Show(); other.Activate();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.PageContainer.IsEnabled = false;
        AssertDarkSurface(window, window.Gallery.Scroller, "wallpaper gallery after deactivation");
        CaptureWindow(window, Path.Combine(output, "inactive-interface.png"));
        window.Navigate("taskbar");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        AssertDarkSurface(window, window.HeightInput, "taskbar input while applying");
        window.PageContainer.IsEnabled = true; other.Close(); window.Activate();
        window.Navigate("wallpapers"); await Until(() => !window.Busy, "activation refresh did not complete");
        Assert(ReferenceEquals(collectionBeforeActivation, window.Gallery.Cards),
            "activation/navigation unnecessarily rebuilt the unchanged gallery");
        window.Navigate("settings"); window.FpsInput.SelectedIndex = 1;
        await Until(() => !window.Busy, "settings did not save"); Assert(window.Library.State.Settings.Fps == 30, "native performance preference lost");
        window.Navigate("taskbar");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (window.TaskbarPage.ScrollableHeight > 80 && SystemParameters.WheelScrollLines != 0)
        {
            var wheelTarget = TextElements(window.TaskbarPage).First(text => text.Text == "Панель задач");
            Assert(Wheel(wheelTarget, -120), "page did not own the wheel event");
            await Until(() => !Views.SmoothScroll.IsAnimating(window.TaskbarPage), "page scroll animation did not settle");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            double expected = Math.Min(window.TaskbarPage.ScrollableHeight, SystemParameters.WheelScrollLines < 0 ? window.TaskbarPage.ViewportHeight : SystemParameters.WheelScrollLines * 22);
            Assert(Math.Abs(window.TaskbarPage.VerticalOffset - expected) < 1, "mouse wheel did not smoothly scroll the page");
            window.TaskbarPage.ScrollToTop();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            int alignment = window.AlignmentInput.SelectedIndex;
            var track = (System.Windows.Controls.Primitives.Track)window.HeightInput.Template.FindName("PART_Track", window.HeightInput);
            foreach (var target in new UIElement[] { track.Thumb, window.ColorButton, window.AlignmentInput })
            {
                Assert(Wheel(target, -120), "taskbar field bypassed smooth page scrolling"); await Task.Delay(20);
            }
            await Until(() => !Views.SmoothScroll.IsAnimating(window.TaskbarPage), "repeated wheel input did not settle");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(Math.Abs(window.TaskbarPage.VerticalOffset - Math.Min(window.TaskbarPage.ScrollableHeight, expected * 3)) < 1,
                "wheel bursts over input controls lost distance or interrupted the animation");
            Assert(window.AlignmentInput.SelectedIndex == alignment, "scrolling the page changed a closed dropdown value");
            window.TaskbarPage.ScrollToTop();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Wheel(wheelTarget, -120); window.Navigate("settings");
            Assert(!Views.SmoothScroll.IsAnimating(window.TaskbarPage), "hidden page kept its rendering callback");
            window.Navigate("taskbar"); window.TaskbarPage.ScrollToTop();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        // Exercise the preview without sending a request to the real Explorer.
        var previewButton = (Border)window.TaskbarSampleIcons.Children[0];
        var previewIcon = (Border)previewButton.Child;
        var neighborIcon = (Border)((Border)window.TaskbarSampleIcons.Children[1]).Child;
        previewButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        window.UpdateTaskbarSampleEffects(TaskbarIconEffect.Lift);
        var previewTransforms = (TransformGroup)previewIcon.RenderTransform;
        var previewLift = (TranslateTransform)previewTransforms.Children[2];
        Assert(previewLift.HasAnimatedProperties == SystemParameters.ClientAreaAnimation,
            "taskbar preview did not start the expected hover animation");
        await Until(() => previewLift.Y == -5, "taskbar preview icon did not lift");
        window.UpdateTaskbarSampleEffects(TaskbarIconEffect.Carousel);
        var neighborAngle = (RotateTransform)((TransformGroup)neighborIcon.RenderTransform).Children[1];
        await Until(() => neighborAngle.Angle == -8, "carousel preview did not tilt the adjacent icon");
        window.UpdateTaskbarSampleEffects(TaskbarIconEffect.Zoom);
        var previewScale = (ScaleTransform)previewTransforms.Children[0];
        await Until(() => previewScale.ScaleX == 1.14 && neighborAngle.Angle == 0 && previewLift.Y == 0,
            "zoom preview did not enlarge just the selected icon");
        previewButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        Assert(previewScale.ScaleX == 1, "taskbar preview icon did not return to rest");

        // Change the draft with styling disabled so the smoke test never touches Explorer.
        window.HeightInput.Value = 72;
        window.IconInput.Value = 54;
        Assert(window.TaskbarSample.Height == 72 && window.ButtonInput.Value == 54, "preview did not follow live slider values");
        window.HeightInput.Value = 38;
        window.OpacityInput.Value = 43;
        Assert(window.TaskbarSample.Height == 38 && window.IconInput.Value == 30, "reducing the height did not keep the icon within bounds");
        Assert(window.PageContainer.IsEnabled, "appearance settings blocked slider input");
        await window.FinishTaskbarUpdatesAsync();
        var draft = DataFiles.Read<AppearanceSettings>(Path.Combine(window.Library.DirectoryPath, "taskbar-appearance.json"));
        Assert(!draft.Enabled && draft.Height == 38 && draft.Icon == 30 && draft.Button == 54 && draft.Opacity == 43,
            "automatic appearance settings did not persist the last slider values");
        window.SetTaskbarColor("#ABCDEF", second: true);
        window.GlassInput.IsChecked = true;
        window.GlassInput.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Assert(window.TaskbarGlassArea.Visibility == Visibility.Visible && window.TaskbarGlassArea.Opacity == .43 &&
            window.TaskbarSample.CornerRadius.TopLeft == 0 &&
            window.GlassSwatch.Background is SolidColorBrush endTint && endTint.Color == Color.FromRgb(0xAB, 0xCD, 0xEF),
            "glass preview does not reflect the selected tint, opacity and square corners");
        window.GlassOutlineInput.IsChecked = false;
        window.GlassOutlineInput.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Assert(window.TaskbarGlassArea.Visibility == Visibility.Visible && window.TaskbarGlassArea.Opacity == .43,
            "hiding the outline changed the glass preview");
        window.OpacityInput.Value = 0;
        Assert(window.TaskbarGlassArea.Visibility == Visibility.Collapsed && window.TaskbarSample.Background == Brushes.Transparent,
            "zero opacity should hide the glass preview");
        window.OpacityInput.Value = 43;
        await window.FinishTaskbarUpdatesAsync();
        draft = DataFiles.Read<AppearanceSettings>(Path.Combine(window.Library.DirectoryPath, "taskbar-appearance.json"));
        Assert(draft.Glass && draft.GlassColor == "#ABCDEF" && !draft.GlassOutline, "glass settings were not saved");
        window.WallpaperColorsInput.IsChecked = true;
        window.WallpaperColorsInput.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        await window.FinishTaskbarUpdatesAsync();
        draft = DataFiles.Read<AppearanceSettings>(Path.Combine(window.Library.DirectoryPath, "taskbar-appearance.json"));
        Assert(draft.MatchWallpaperColors && draft.GlassColor == "#ABCDEF" && window.GlassColorButton.IsEnabled,
            "wallpaper matching did not preserve the manual fallback");
        window.WallpaperColorsInput.IsChecked = false;
        window.WallpaperColorsInput.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        await window.FinishTaskbarUpdatesAsync();
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
        double originalHeight = window.Height;
        window.Height = window.MinHeight;
        window.Navigate("cursors");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var cursorPage = window.CursorsPage;
        var packs = cursorPage.Gallery;
        await Until(() => cursorPage.Tiles[0].Frames.Count > 0, "current Windows cursor did not appear in the gallery");
        Assert(packs.Items.Count == 5 && cursorPage.Tiles.Skip(1).All(tile => tile.Frames.Count >= 15) &&
            TextElements(packs).Any(text => text.Text == "Bibata Modern Ice"), "offline cursor previews are missing from the gallery");
        var container = (ContentPresenter)packs.ItemContainerGenerator.ContainerFromIndex(1);
        var tile = (Border)container.ContentTemplate.FindName("Tile", container);
        var openPack = (Button)container.ContentTemplate.FindName("Open", container);
        var preview = (Views.CursorTilePreview)container.ContentTemplate.FindName("Preview", container);
        Assert(preview.Caption.Text == "Основной указатель" && preview.Incoming.Source is not null, "cursor tile must start with its primary pointer");
        tile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        Assert(preview.Cycling && preview.Caption.Text == "Основной указатель", "cursor slideshow must wait before changing states");
        await Until(() => preview.Caption.Text == "Выбор справки", "cursor tile did not advance to a different state");
        var cursorScroll = window.CursorsPage.PageScroll;
        if (cursorScroll.ScrollableHeight >= 2) cursorScroll.ScrollToVerticalOffset(2);
        else tile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        await Until(() => !preview.Cycling && preview.Caption.Text == "Основной указатель", "the gallery should reset the cursor preview");
        window.Height = originalHeight;
        var current = (ContentPresenter)packs.ItemContainerGenerator.ContainerFromIndex(0);
        var openCurrent = (Button)current.ContentTemplate.FindName("Open", current);
        foreach (var (button, isPack) in new[] { (openPack, true), (openCurrent, false) })
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var details = cursorPage.DetailsHost.Content as Views.CursorDetails
                ?? throw new InvalidOperationException("cursor tile did not open its details");
            Assert(details.IsVisible && !cursorScroll.IsVisible && details.ActualWidth >= cursorPage.ActualWidth - 1 &&
                details.Roles.Items.Count == CursorFiles.Roles.Count, "wide cursor details did not open with all states");
            details.Roles.SelectedIndex = 1;
            Assert(details.RoleTitle.Text == CursorFiles.Roles[1].Label &&
                (isPack ? details.RoleImage.Source is not null : details.ChooseFileButton.IsVisible),
                "cursor details did not show the selected role and its controls");
            details.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(cursorScroll.IsVisible && cursorPage.DetailsHost.Content is null, "back button did not reopen the cursor gallery");
        }
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 255 }, 8);
        var pngEncoder = new PngBitmapEncoder();
        pngEncoder.Frames.Add(BitmapFrame.Create(source));
        using var png = new MemoryStream();
        pngEncoder.Save(png);
        var hotspot = new Views.CursorHotspot(png.ToArray()) { Owner = window };
        hotspot.XInput.Value = 1;
        using (var cursorBytes = new MemoryStream(hotspot.CursorBytes()))
        using (var cursor = new System.Windows.Input.Cursor(cursorBytes)) { }
        hotspot.Close();

        window.Navigate("settings");
        window.OpenUpdateNotesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var notes = window.OwnedWindows.OfType<Views.UpdateNotes>().Single();
        await Until(() => notes.IsVisible && notes.Opacity == 1, "changelog was not positioned after layout");
        Assert(notes.Changes.Items.Count > 0 && notes.VersionText.Text == Views.UpdateNotes.VersionLabel,
            "changelog was not shown with notes for this build");
        nint mainHandle = new WindowInteropHelper(window).Handle;
        nint notesHandle = new WindowInteropHelper(notes).Handle;
        var work = System.Windows.Forms.Screen.FromHandle(mainHandle).WorkingArea;
        Assert(Win32.GetWindowRect(mainHandle, out var mainRect), "cannot read the main window bounds");
        Assert(Win32.GetWindowRect(notesHandle, out var notesRect), "cannot read the changelog bounds");
        double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        if (work.Width >= Math.Ceiling((window.MinWidth + 10) * dpi) + notesRect.Right - notesRect.Left)
            Assert(mainRect.Right <= notesRect.Left || notesRect.Right <= mainRect.Left, "changelog overlapped the main window despite available space");
        notes.DismissButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(!notes.IsVisible, "changelog close button did not dismiss the window");
        window.ShowUpdateNotesInput.IsChecked = false;
        window.ShowUpdateNotesInput.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        using var preferences = JsonDocument.Parse(File.ReadAllText(Path.Combine(window.Library.DirectoryPath, "ui.json")));
        Assert(!preferences.RootElement.GetProperty("showUpdateNotesOnStartup").GetBoolean(), "changelog preference was not saved");
        window.ThemeInput.IsChecked = false;
        window.ThemeInput.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert(window.Background is SolidColorBrush light && Luminance(light.Color) is > .53 and < .65,
            "light theme did not use the softer background");
        AssertReadable(window.Foreground, window.Background, "light window");
        AssertReadable((Brush)window.FindResource("MutedBrush"), (Brush)window.FindResource("PanelBrush"), "light secondary text");
        AssertReadable((Brush)window.FindResource("AccentBrush"), (Brush)window.FindResource("InputBrush"), "light accent labels");
        AssertReadable(window.AddWallpaperButton.Foreground, window.AddWallpaperButton.Background, "light primary button");
        var lightNameDialog = new Views.WallpaperDialog("Обои") { Owner = window };
        lightNameDialog.Show();
        try { CheckWallpaperNameLabel(lightNameDialog); }
        finally { lightNameDialog.Close(); }
        using (var themePreference = JsonDocument.Parse(File.ReadAllText(Path.Combine(window.Library.DirectoryPath, "ui.json"))))
            Assert(themePreference.RootElement.GetProperty("lightTheme").GetBoolean(), "theme preference was not saved");
        window.ThemeInput.IsChecked = true;
        window.ThemeInput.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert(window.Background is SolidColorBrush dark && Luminance(dark.Color) < .05, "dark theme did not return");
        window.NavOverview.ApplyTemplate();
        var selectedSurface = (FrameworkElement)window.NavOverview.Template.FindName("Selection", window.NavOverview);
        window.Navigate("overview");
        Assert(selectedSurface.HasAnimatedProperties == SystemParameters.ClientAreaAnimation,
            "navigation did not respect the system animation preference");
        await Until(() => selectedSurface.Opacity == 1 && selectedSurface.RenderTransform.Value.OffsetX == 0,
            "current navigation item did not finish its transition");
        window.Navigate("windowTheme");
        await Until(() => selectedSurface.Opacity == 0, "previous navigation item remained highlighted");
        Assert(window.WindowThemePage.IsVisible && window.NavWindowTheme.IsChecked == true
            && window.WindowsAnimationsInput.IsChecked == WindowsVisualSettings.AnimationsEnabled,
            "the window theme page did not read the current Windows animation preference");
        window.WindowMaterialInput.SelectedIndex = (int)WindowMaterial.Gradient;
        Assert(window.WindowThemeSample.Background is LinearGradientBrush, "window theme failed to preview a gradient");
        window.WindowMaterialInput.SelectedIndex = (int)WindowMaterial.Glass;
        window.WindowRadiusInput.Value = 19;
        window.WindowOpacityInput.Value = 85;
        window.WindowThemeEnabled.IsChecked = true;
        window.WindowThemeEnabled.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Assert(window.WindowThemeSample.Background is DrawingBrush && window.WindowThemeSample.CornerRadius.TopLeft == 19
            && window.WindowTitleBar.Background is DrawingBrush, "glass theme failed to reach the preview and live window");
        await Until(() => System.Windows.Shell.WindowChrome.GetWindowChrome(window).CornerRadius.TopLeft == 19,
            "the window chrome did not apply the selected rounding");
        nint themedRegion = Win32.CreateRectRgn(0, 0, 0, 0);
        try
        {
            Assert(themedRegion != 0 && Win32.GetWindowRgn(mainHandle, themedRegion) == 3,
                "the native window did not apply a rounded region");
        }
        finally { if (themedRegion != 0) _ = Win32.DeleteObject(themedRegion); }
        var windowTheme = DataFiles.Read<System.Text.Json.Nodes.JsonObject>(Path.Combine(window.Library.DirectoryPath, "ui.json"))["windowTheme"];
        Assert(windowTheme?["radius"]?.GetValue<int>() == 19 && windowTheme?["opacity"]?.GetValue<int>() == 85,
            "the window theme was not saved");
        window.Navigate("wallpapers");
        await Until(() => !window.Busy, "gallery did not open for the themed dialog test");
        await UseWallpaperDialogAsync(window, window.AddWallpaperButton, dialog =>
            Assert(dialog.Opacity == .85 && dialog.FrameRadius == 19 && dialog.DialogCard.Background is DrawingBrush,
                "the dialog did not apply the selected glass, rounding and transparency"));
        window.Navigate("windowTheme");
        window.WindowMotionInput.SelectedIndex = (int)WindowOpeningMotion.Rise;
        if (SystemParameters.ClientAreaAnimation)
        {
            window.WindowMotionPreviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(window.WindowThemeSample.RenderTransform is TranslateTransform { HasAnimatedProperties: true },
                "window theme preview did not start the selected animation");
        }
        window.WindowMotionInput.SelectedIndex = (int)WindowOpeningMotion.Off;
        window.WindowThemeEnabled.IsChecked = false;
        window.WindowThemeEnabled.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        await Until(() => System.Windows.Shell.WindowChrome.GetWindowChrome(window).CornerRadius.TopLeft == 0,
            "turning off the window theme did not restore the native corners");
        CheckRoundedFrameBlur();
        await CheckToggleMotion(window.WindowBlurInput);
        window.WindowBlurInput.IsChecked = true;
        window.WindowBlurInput.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        const string frameTitle = "MagmPaper.WindowFrameBlur";
        if (mainRect.Left > work.Left || mainRect.Top > work.Top || mainRect.Right < work.Right || mainRect.Bottom < work.Bottom)
        {
            await Until(() => Win32.FindWindow(null, frameTitle) != 0, "window blur did not create an overlay");
            nint frame = Win32.FindWindow(null, frameTitle);
            Assert((Win32.GetWindowLongPtr(frame, -20).ToInt64() & 0x08000020) == 0x08000020,
                "window blur intercepts input or activates");
            Assert(Win32.GetWindowRect(frame, out var frameRect) && frameRect.Left < mainRect.Left &&
                frameRect.Right > mainRect.Right && frameRect.Top < mainRect.Top && frameRect.Bottom > mainRect.Bottom,
                "window blur was not placed around the main window");
            window.WindowState = WindowState.Maximized;
            await Until(() => Win32.FindWindow(null, frameTitle) == 0, "window blur was kept when maximized");
            window.WindowState = WindowState.Normal;
            await Until(() => Win32.FindWindow(null, frameTitle) != 0, "window blur did not return in windowed mode");
        }
        window.Navigate("wallpapers");
        await Until(() => !window.Busy, "gallery did not open for the wallpaper dialog test");
        await UseWallpaperDialogAsync(window, window.AddWallpaperButton, dialog =>
        {
            byte[] pixel = new byte[4];
            WindowBitmap(dialog).CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert(dialog.FrameRadius > 0 && dialog.AllowsTransparency && pixel[3] == 0,
                "wallpaper dialog did not have transparent rounded corners");
            nint dialogHandle = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
            bool found = false;
            Win32.EnumWindows((handle, _) =>
            {
                found = Win32.GetWindow(handle, 4) == dialogHandle &&
                    (Win32.GetWindowLongPtr(handle, Win32.ExtendedStyle).ToInt64() & 0x08000020) == 0x08000020;
                return !found;
            }, 0);
            Assert(found, "rounded wallpaper dialog did not have its own blur overlay");
        });
        window.Navigate("windowTheme");
        window.WindowBlurRadius.Value = 48;
        window.WindowBlurIntensity.Value = 55;
        window.WindowBlurInput.IsChecked = false;
        window.WindowBlurInput.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        await Until(() => Win32.FindWindow(null, frameTitle) == 0, "window blur stayed visible after disabling it");
        var savedBlur = DataFiles.Read<System.Text.Json.Nodes.JsonObject>(Path.Combine(window.Library.DirectoryPath, "ui.json"))["windowBlur"];
        Assert(savedBlur?["radius"]?.GetValue<int>() == 48 && savedBlur?["intensity"]?.GetValue<int>() == 55, "window blur settings were not saved");
        await CheckMediaAsync(window);
        window.Navigate("settings");
        bool desktopIconSnapshotAvailable;
        using (var layout = DesktopIconLayout.Capture()) desktopIconSnapshotAvailable = layout is not null;
        long idleFrames = surface.Frames; await Task.Delay(150); idleFrames = surface.Frames; await Task.Delay(400);
        Assert(surface.Frames == idleFrames, "hidden preview consumes frames");
        await Task.Delay(3000); // Let startup compilation, layout and deferred GPU release settle.
        var process = Process.GetCurrentProcess(); var startCpu = process.TotalProcessorTime; var clockIdle = Stopwatch.StartNew(); await Task.Delay(5000); process.Refresh();
        var report = new { passed = true, version = typeof(App).Assembly.GetName().Version?.ToString(), nativeUi = "WPF", gpuTests = "ANGLE D3D11 WARP", composedDesktopPixels = composedPixels, desktopIconSnapshotAvailable, workingSetBytes = process.WorkingSet64, idleCpuMilliseconds = (process.TotalProcessorTime - startCpu).TotalMilliseconds, idleSampleMilliseconds = clockIdle.Elapsed.TotalMilliseconds,
            tests = new[] { "real GLSL pixels", "late GLSL extension compatibility", "compile error preserves program", "backbuffer", "quality fills window", "pause stops frames", "physical monitor geometry", "24H2 child layer order and opaque composition", "native window coverage/minimize/transparent overlay", "click-through and real cursor uniforms", "mouse toggle", "six GPU resets with active and paused second context", "Windows autostart and StartupApproved", "gallery thumbnails, hover shader uniforms and enlarged preview", "GIF and H.264 MP4 import, naming, and playback", "WPF editor/save/navigation", "native preview visibility and scroll clipping", "smooth scrolling over inputs and repeated wheel events", "inactive and disabled control colors", "stable collection after activation/navigation", "HSV and HEX color picker", "live taskbar preview and automatic slider persistence", "glass gradient settings and wallpaper palette", "light and dark themes", "cursor catalogue caption, scroll and PNG hotspot", "changelog positioning and preference", "window blur toggle, rounded notes and wallpaper dialogs, and settings", "animated toggles and navigation", "settings", "WPF theme contrast", "hidden preview" } };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, DataFiles.Json));
    }
    private static RenderTargetBitmap WindowBitmap(Window window)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        return bitmap;
    }
    private static void CaptureWindow(Window window, string path)
    {
        var bitmap = WindowBitmap(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var image = File.Create(path); encoder.Save(image);
    }
}
