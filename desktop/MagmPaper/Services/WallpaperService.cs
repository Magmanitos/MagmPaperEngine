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
    private RenderSurface? surface;
    private CancellationTokenSource? recovery;
    private bool locked, sleeping, disposed, recovering;
    private int generation;
    private Area? previousMonitor;
    public string? RunningId { get; private set; }
    public string? PauseReason { get; private set; }
    public event Action<string, bool>? Status;
    public event Action? Changed;
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
        if (e.Mode == PowerModes.Suspend) { sleeping = true; CancelRecovery(); Destroy(); }
        if (e.Mode == PowerModes.Resume) { sleeping = false; ScheduleRecovery(); }
        Refresh();
    });
    private void DisplayChanged(object? sender, EventArgs e) => Post(() => ScheduleRecovery());
    private void Destroy()
    {
        surface?.Dispose(); surface = null;
        if (window != 0) { Win32.DestroyWindow(window); window = 0; }
        worker = 0; RunningId = null; Changed?.Invoke();
    }
    private void CancelRecovery() { generation++; recovery?.Cancel(); recovery?.Dispose(); recovery = null; recovering = false; }
    public async Task ApplyAsync(string id)
    {
        CancelRecovery(); int request = generation; var item = library.Get(id);
        // Build and validate a new surface before replacing the current wallpaper.
        nint candidate = Win32.CreateSurface(0, true); RenderSurface? renderer = null;
        try
        {
            var parent = DesktopHost.Attach(candidate); renderer = new RenderSurface(candidate, true);
            await renderer.LoadAsync(item.Code);
            if (request != generation || disposed) return;
            library.Activate(id); Destroy(); window = candidate; worker = parent; surface = renderer;
            candidate = 0; renderer = null; RunningId = id; previousMonitor = Win32.PrimaryMonitor().Monitor.Area;
            surface.Error += message => Post(() => Status?.Invoke(message, true));
            surface.Recovered += message => Post(() => Status?.Invoke(message, false));
            health.Start(); Refresh(); Changed?.Invoke(); Status?.Invoke("На рабочем столе: " + item.Name, false);
        }
        finally { renderer?.Dispose(); if (candidate != 0) Win32.DestroyWindow(candidate); }
    }
    public void Stop()
    {
        CancelRecovery(); health.Stop(); library.Activate(null); Destroy(); PauseReason = null;
        Changed?.Invoke(); Status?.Invoke("Обои остановлены.", false);
    }
    public void Refresh()
    {
        if (disposed || sleeping) return;
        if (library.State.ActiveId is null) { if (window != 0) { CancelRecovery(); Destroy(); health.Stop(); } return; }
        if (window == 0 || !Win32.IsWindow(window) || !Win32.IsWindow(worker) || Win32.GetParent(window) != worker) { ScheduleRecovery(); return; }
        try
        {
            var monitor = Win32.PrimaryMonitor().Monitor.Area;
            if (monitor != previousMonitor) { previousMonitor = monitor; ScheduleRecovery(); return; }
            if (!Win32.IsWindowVisible(window) || Win32.IsIconic(window)) Win32.ShowWindow(window, 4);
            bool battery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
            var settings = library.State.Settings;
            var reason = PlaybackPolicy.PauseReason(settings, locked, battery, settings.PauseWhenCovered && DesktopHost.Covered(window));
            surface?.Configure(new(reason is not null, settings.Fps, settings.RenderScale, settings.MouseEnabled));
            if (reason != PauseReason) { PauseReason = reason; Changed?.Invoke(); }
        }
        catch (Exception error) { Status?.Invoke("Автопауза временно недоступна: " + error.Message, true); }
    }
    public void RestoreOnStart() { if (library.State.ActiveId is not null) { health.Start(); ScheduleRecovery(); } }
    private void ScheduleRecovery()
    {
        if (disposed || sleeping || library.State.ActiveId is null || recovering) return;
        recovering = true; int request = ++generation; recovery = new CancellationTokenSource();
        _ = RecoverAsync(request, recovery.Token);
    }
    private async Task RecoverAsync(int request, CancellationToken token)
    {
        try
        {
            await Task.Delay(600, token); // Coalesce display/DPI changes from exclusive games.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (token.IsCancellationRequested || request != generation || sleeping || library.State.ActiveId is not { } id) return;
                nint candidate = 0; RenderSurface? renderer = null;
                try
                {
                    candidate = Win32.CreateSurface(0, true); var parent = DesktopHost.Attach(candidate);
                    renderer = new RenderSurface(candidate, true); await renderer.LoadAsync(library.Get(id).Code);
                    if (token.IsCancellationRequested || request != generation) return;
                    Destroy(); window = candidate; worker = parent; surface = renderer; candidate = 0; renderer = null; RunningId = id;
                    surface.Error += message => Post(() => Status?.Invoke(message, true));
                    surface.Recovered += message => Post(() => Status?.Invoke(message, false));
                    previousMonitor = Win32.PrimaryMonitor().Monitor.Area; recovering = false; health.Start(); Refresh(); Changed?.Invoke(); return;
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
