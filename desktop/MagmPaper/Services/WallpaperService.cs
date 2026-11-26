using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;
using MagmPaper.Graphics;
using MagmPaper.Windows;

namespace MagmPaper.Services;

internal sealed class WallpaperService : IDisposable
{
    private readonly Library library;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer health;
    private nint window, worker;
    private WallpaperSurface? surface, paletteSurface;
    private Task<WallpaperColors?>? paletteTask;
    private CancellationTokenSource? recovery;
    private bool locked, sleeping, disposed, recovering;
    private int generation;
    private Area? previousMonitor;
    public string? RunningId { get; private set; }
    public string? PauseReason { get; private set; }
    public event Action<string, bool>? Status;
    public event Action? Changed;
    public event Action? PaletteSourceChanged;

    public async Task<WallpaperColors?> ReadColorsAsync()
    {
        var current = surface;
        if (current is null) return null;
        if (!ReferenceEquals(paletteSurface, current) || paletteTask is null || paletteTask.IsFaulted)
        {
            paletteSurface = current;
            paletteTask = current.ReadColorsAsync();
        }
        try
        {
            var palette = await paletteTask.WaitAsync(TimeSpan.FromSeconds(2));
            return ReferenceEquals(surface, current) ? palette : null;
        }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException or InvalidDataException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    internal string DiagnosticReport()
    {
        var monitor = Win32.PrimaryMonitor().Monitor;
        return $"MagmPaper {typeof(WallpaperService).Assembly.GetName().Version}\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
            $"Display: {monitor.Width}x{monitor.Height}, {Win32.RefreshRate()} Hz\n" +
            $"Wallpaper: {(RunningId is null ? "stopped" : "loaded")}; pause: {PauseReason ?? "none"}; frames: {surface?.Frames ?? 0}\n" +
            $"GPU: {surface?.Renderer ?? "not initialized"}\n" +
            $"Host: {Win32.ClassName(worker)}, visible: {Win32.IsWindowVisible(worker)}, extended style: 0x{Win32.GetWindowLongPtr(worker, Win32.ExtendedStyle):X}\n" +
            $"Surface visible: {Win32.IsWindowVisible(window)}, attached: {window != 0 && Win32.GetParent(window) == worker}\n" +
            $"Rendering: {library.State.Settings.Fps} FPS (0 = monitor), {library.State.Settings.RenderScale:P0}";
    }
    public WallpaperService(Library library, Dispatcher dispatcher)
    {
        this.library = library; this.dispatcher = dispatcher;
        health = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher); health.Stop();
        SystemEvents.SessionSwitch += SessionSwitch; SystemEvents.PowerModeChanged += PowerChanged; SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }
    private void Post(Action action) { if (!disposed && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action); }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e) => Post(() => { if (e.Reason == SessionSwitchReason.SessionLock) locked = true; if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false; Refresh(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Post(() =>
    {
        if (e.Mode == PowerModes.Suspend) { sleeping = true; CancelRecovery(); Destroy(); Changed?.Invoke(); }
        if (e.Mode == PowerModes.Resume) { sleeping = false; ScheduleRecovery(); }
        Refresh();
    });
    private void DisplayChanged(object? sender, EventArgs e) => Post(() => ScheduleRecovery());
    private void Destroy()
    {
        paletteSurface = null;
        paletteTask = null;
        surface?.Dispose(); surface = null;
        if (window != 0) { Win32.DestroyWindow(window); window = 0; }
        worker = 0; RunningId = null; PauseReason = null;
    }
    private void CancelRecovery() { generation++; recovery?.Cancel(); recovery?.Dispose(); recovery = null; recovering = false; }
    public async Task ApplyAsync(string id)
    {
        CancelRecovery(); int request = generation; var item = library.Get(id);
        // Build and validate a new surface before replacing the current wallpaper.
        var attachment = DesktopHost.Create(); nint candidate = attachment.Window; WallpaperSurface? renderer = null;
        try
        {
            var parent = attachment.Parent; renderer = CreateRenderer(candidate, item);
            await renderer.LoadAsync(RenderSource(item));
            if (request != generation || disposed) return;
            // Shader compilation can overlap a game's resolution switch.
            // Fit again on the UI thread before recording the current monitor.
            DesktopHost.Fit(candidate, parent);
            renderer.Fit();
            library.ActivateLoaded(item);
            InstallSurface(id, candidate, parent, renderer);
            candidate = 0; renderer = null;
            SurfaceChanged();
            Status?.Invoke("На рабочем столе: " + item.Name, false);
        }
        finally { renderer?.Dispose(); if (candidate != 0) Win32.DestroyWindow(candidate); }
    }

    private void InstallSurface(string id, nint target, nint parent, WallpaperSurface renderer)
    {
        var monitor = Win32.PrimaryMonitor().Monitor.Area;
        Destroy();
        window = target; worker = parent; surface = renderer; RunningId = id; previousMonitor = monitor;
        renderer.Error += message => Post(() => { if (ReferenceEquals(surface, renderer)) Status?.Invoke(message, true); });
        if (renderer is RenderSurface shader)
            shader.Recovered += message => Post(() => { if (ReferenceEquals(surface, renderer)) Status?.Invoke(message, false); });
    }

    private void SurfaceChanged()
    {
        health.Start();
        Refresh(notify: false);
        Changed?.Invoke();
        PaletteSourceChanged?.Invoke();
    }
    private static WallpaperSurface CreateRenderer(nint target, Wallpaper item) => item.Format == WallpaperFormat.Glsl
        ? new RenderSurface(target, true) : new DesktopMediaSurface(target);

    private string RenderSource(Wallpaper item) => item.Format == WallpaperFormat.Glsl ? item.Code : library.MediaPath(item);

    public void Stop()
    {
        CancelRecovery(); health.Stop(); library.Activate(null); Destroy();
        Changed?.Invoke(); PaletteSourceChanged?.Invoke(); Status?.Invoke("Обои остановлены.", false);
    }
    public void Refresh(bool notify = true)
    {
        if (disposed || sleeping) return;
        if (library.State.ActiveId is null)
        {
            if (window != 0) { CancelRecovery(); Destroy(); Changed?.Invoke(); PaletteSourceChanged?.Invoke(); }
            health.Stop();
            return;
        }
        if (window == 0 || !Win32.IsWindow(window) || !Win32.IsWindow(worker) || Win32.GetParent(window) != worker) { ScheduleRecovery(); return; }
        try
        {
            var monitor = Win32.PrimaryMonitor().Monitor.Area;
            if (monitor != previousMonitor) { previousMonitor = monitor; ScheduleRecovery(); return; }
            if (!Win32.IsWindowVisible(window) || Win32.IsIconic(window)) Win32.ShowWindow(window, 4);
            DesktopHost.EnsureOrder(window, worker);
            var settings = library.State.Settings;
            bool battery = !locked && settings.PauseOnBattery &&
                System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
            var reason = PlaybackPolicy.PauseReason(settings, locked, battery,
                !locked && !battery && settings.PauseWhenCovered && DesktopHost.Covered(window));
            surface?.Configure(new(reason is not null, settings.Fps, settings.RenderScale, settings.MouseEnabled));
            if (reason != PauseReason) { PauseReason = reason; if (notify) Changed?.Invoke(); }
        }
        catch (Exception error) { Status?.Invoke("Автопауза временно недоступна: " + error.Message, true); }
    }
    public void RestoreOnStart() { if (library.State.ActiveId is not null) { health.Start(); ScheduleRecovery(settleDisplay: false); } }
    private void ScheduleRecovery(bool settleDisplay = true)
    {
        if (disposed || sleeping || library.State.ActiveId is null || recovering) return;
        recovering = true; int request = ++generation; recovery = new CancellationTokenSource();
        _ = RecoverAsync(request, recovery.Token, settleDisplay);
    }
    private async Task RecoverAsync(int request, CancellationToken token, bool settleDisplay)
    {
        try
        {
            if (settleDisplay) await Task.Delay(600, token); // Coalesce display/DPI changes from exclusive games.
            else await Task.Yield();
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (token.IsCancellationRequested || request != generation || sleeping || library.State.ActiveId is not { } id) return;
                nint candidate = 0; WallpaperSurface? renderer = null;
                try
                {
                    var attachment = DesktopHost.Create(); candidate = attachment.Window; var parent = attachment.Parent;
                    var item = library.Get(id);
                    renderer = CreateRenderer(candidate, item); await renderer.LoadAsync(RenderSource(item));
                    if (token.IsCancellationRequested || request != generation) return;
                    DesktopHost.Fit(candidate, parent);
                    renderer.Fit();
                    InstallSurface(id, candidate, parent, renderer);
                    candidate = 0; renderer = null;
                    recovering = false;
                    SurfaceChanged();
                    return;
                }
                catch (InvalidDataException error) { Status?.Invoke(error.Message, true); health.Stop(); return; }
                catch (Exception error) { if (attempt == 19) { health.Stop(); Status?.Invoke("Не удалось восстановить обои: " + error.Message + " Повторите «На рабочий стол».", true); return; } }
                finally { renderer?.Dispose(); if (candidate != 0) Win32.DestroyWindow(candidate); }
                await Task.Delay(3000, token);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (request == generation) recovering = false; }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; health.Stop(); CancelRecovery();
        SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        Destroy();
    }
}
