using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using MagmPaper.Graphics;
using MagmPaper.Services;
using MagmPaper.Windows;

namespace MagmPaper;

public partial class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? activation, exit;
    private RegisteredWaitHandle? activationWait, exitWait;
    private System.Windows.Forms.NotifyIcon? tray;
    private WallpaperService? wallpapers;
    private AppearanceService? appearance;
    private MainWindow? manager;
    private bool quitting, ownsMutex;
    private const string Instance = @"Local\MagmPaper.Native.1";
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool testing = e.Args.Contains("--smoke-test");
        if (testing)
        {
            int testIndex = Array.IndexOf(e.Args, "--smoke-test");
            Diagnostics.Output = e.Args.Length > testIndex + 1 ? e.Args[testIndex + 1] : Path.GetTempPath();
        }
        Diagnostics.Trace("OnStartup");
        try
        {
            SetCurrentProcessExplicitAppUserModelID(SystemSettings.StartupName);
            if (!testing)
            {
                mutex = new Mutex(true, Instance, out ownsMutex);
                if (!ownsMutex)
                {
                    bool close = e.Args.Contains("--quit"), finished = !close;
                    try
                    {
                        using var signal = EventWaitHandle.OpenExisting(Instance + (close ? ".Exit" : ".Activate"));
                        if (!e.Args.Contains("--autostart")) signal.Set();
                        if (close)
                        {
                            try { finished = mutex.WaitOne(TimeSpan.FromSeconds(45)); }
                            catch (AbandonedMutexException) { finished = true; }
                            if (finished) mutex.ReleaseMutex();
                        }
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    Shutdown(finished ? 0 : 1); return;
                }
                if (e.Args.Contains("--quit")) { Shutdown(); return; }
                if (Process.GetProcessesByName("MagmPaper").Any(p => p.Id != Environment.ProcessId))
                    throw new IOException("Закройте прежнюю версию MagmPaper через трей → «Выход», затем запустите новую версию.");
                activation = new EventWaitHandle(false, EventResetMode.AutoReset, Instance + ".Activate");
                exit = new EventWaitHandle(false, EventResetMode.AutoReset, Instance + ".Exit");
                activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(OpenManager), null, -1, false);
                exitWait = ThreadPool.RegisterWaitForSingleObject(exit, (_, _) => Dispatcher.BeginInvoke(async () => await QuitAsync()), null, -1, false);
            }
            string directory = testing ? Path.Combine(Path.GetTempPath(), "MagmPaper-native-smoke-" + Guid.NewGuid()) : Library.MigrateDataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            Diagnostics.Trace("Load library");
            var library = new Library(directory);
            if (library.IsNew)
            {
                using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("MagmPaper.Assets.example.glsl") ?? throw new IOException("Встроенный пример отсутствует.");
                using var reader = new StreamReader(input); library.Save(null, "Северное сияние", reader.ReadToEnd());
            }
            wallpapers = new WallpaperService(library, Dispatcher); appearance = new AppearanceService(directory);
            Diagnostics.Trace("Create WPF window");
            manager = new MainWindow(library, wallpapers, appearance); MainWindow = manager;
            if (testing)
            {
                AngleDevice.SoftwareForTests = true; Diagnostics.Trace("Show WPF window"); manager.Show();
                Diagnostics.Trace("Start smoke assertions");
                var index = Array.IndexOf(e.Args, "--smoke-test"); var report = index + 1 < e.Args.Length ? e.Args[index + 1] : Path.Combine(Path.GetTempPath(), "MagmPaper-smoke");
                try { await SmokeTests.RunAsync(manager, report); await QuitAsync(false); }
                catch (Exception error) { Directory.CreateDirectory(report); File.WriteAllText(Path.Combine(report, "failure.txt"), error.ToString()); await QuitAsync(false, 1); }
                return;
            }
            using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MagmPaper.Assets.icon.ico") ?? throw new IOException("Значок приложения отсутствует.");
            tray = new System.Windows.Forms.NotifyIcon { Icon = new System.Drawing.Icon(iconStream), Text = "MagmPaper", Visible = true };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Открыть MagmPaper", null, (_, _) => Dispatcher.Invoke(OpenManager));
            menu.Items.Add("Папка обоев", null, (_, _) => Dispatcher.Invoke(manager.OpenCollectionFolder));
            menu.Items.Add("Параметры", null, (_, _) => Dispatcher.Invoke(() => { OpenManager(); manager.Navigate("settings"); }));
            menu.Items.Add("Остановить обои", null, (_, _) => Dispatcher.Invoke(() => { try { wallpapers.Stop(); } catch (Exception error) { manager.Status(error.Message, true); } }));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, async (_, _) => await Dispatcher.InvokeAsync(() => QuitAsync()).Task.Unwrap());
            tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenManager);
            if (!e.Args.Contains("--autostart")) manager.Show();
            wallpapers.RestoreOnStart(); _ = appearance.RestoreOnStartAsync();
        }
        catch (Exception error)
        {
            if (testing)
            {
                int index = Array.IndexOf(e.Args, "--smoke-test"); string report = index + 1 < e.Args.Length ? e.Args[index + 1] : Path.GetTempPath();
                Directory.CreateDirectory(report); File.WriteAllText(Path.Combine(report, "failure.txt"), error.ToString());
            }
            else MessageBox.Show(error.Message + "\nИсходные файлы коллекции сохранены.", "MagmPaper", MessageBoxButton.OK, MessageBoxImage.Error);
            await QuitAsync(false, 1);
        }
    }
    private void OpenManager() { if (manager is null || quitting) return; manager.Show(); if (manager.WindowState == WindowState.Minimized) manager.WindowState = WindowState.Normal; manager.Activate(); }
    private async Task QuitAsync(bool confirm = true, int code = 0)
    {
        if (quitting || (confirm && manager?.ConfirmExit() == false)) return; quitting = true;
        wallpapers?.Dispose();
        if (appearance is not null) try { await appearance.ShutdownAsync(final: true); } catch { }
        if (manager is not null) { manager.Quitting = true; manager.Close(); }
        AngleDevice.Shutdown(); tray?.Dispose();
        Shutdown(code);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        activationWait?.Unregister(null); exitWait?.Unregister(null); activation?.Dispose(); exit?.Dispose();
        if (ownsMutex) mutex?.ReleaseMutex(); mutex?.Dispose(); base.OnExit(e);
    }
}
