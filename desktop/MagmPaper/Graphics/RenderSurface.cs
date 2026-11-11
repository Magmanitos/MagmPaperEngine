using System.Collections.Concurrent;
using System.Diagnostics;
using MagmPaper.Windows;

namespace MagmPaper.Graphics;

internal sealed record RenderOptions(bool Paused = true, int Fps = 60, double Quality = 1, bool MouseEnabled = true);

internal sealed class RenderSurface : IDisposable
{
    private sealed record Command(Action<ShaderContext> Run, Action<Exception> Fail);
    private readonly nint window;
    private readonly bool desktop;
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new(false);
    private readonly ConcurrentQueue<Command> commands = new();
    private RenderOptions options = new();
    private volatile bool stopping;
    private string? source;
    private double seconds;
    private double mouseX = .5, mouseY = .5;
    private long frames;
    public long Frames => Interlocked.Read(ref frames);
    public string Renderer { get; private set; } = "Не создан";
    public event Action<string>? Error;
    public event Action<string>? Recovered;
    public RenderSurface(nint hwnd, bool desktop = false)
    {
        window = hwnd; this.desktop = desktop;
        AngleDevice.ResetRequested += Wake;
        thread = new Thread(Loop) { IsBackground = true, Name = desktop ? "MagmPaper wallpaper GPU" : "MagmPaper preview GPU" };
        thread.Start();
    }
    private void Wake() { try { wake.Set(); } catch (ObjectDisposedException) { } }
    public void Configure(RenderOptions value)
    {
        new PlaybackSettings { Fps = value.Fps, RenderScale = value.Quality }.Validate();
        if (Volatile.Read(ref options) == value) return;
        Volatile.Write(ref options, value); Wake();
    }
    private Task<T> Submit<T>(Func<ShaderContext, T> action)
    {
        if (stopping) return Task.FromException<T>(new ObjectDisposedException(nameof(RenderSurface)));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Enqueue(new(c => { try { completion.TrySetResult(action(c)); } catch (Exception error) { completion.TrySetException(error); } }, error => completion.TrySetException(error)));
        Wake(); return completion.Task;
    }
    public Task LoadAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100000) return Task.FromException(new InvalidDataException("Вставьте GLSL-код, не более 100 000 символов."));
        return Submit(context => { context.Load(code); source = code; seconds = 0; return true; });
    }
    public Task<PixelFrame> CaptureAsync() => Submit(context =>
    {
        if (!Win32.GetClientRect(window, out var rect)) throw new IOException("Окно предпросмотра закрыто.");
        return context.Draw(Math.Max(1, rect.Width), Math.Max(1, rect.Height), options.Quality, seconds, mouseX, mouseY, true)
            ?? throw new InvalidOperationException("Сначала загрузите шейдер.");
    });
    private void Loop()
    {
        ShaderContext? context = null; var clock = Stopwatch.StartNew();
        var motion = new MouseMotion(desktop ? 20 : 0); double previous = clock.Elapsed.TotalMilliseconds, deadline = previous;
        nint timer = Win32.CreateWaitableTimer(0, null, 2, 0x1f0003);
        if (timer == 0) timer = Win32.CreateWaitableTimer(0, null, 0, 0x1f0003);
        nint[] waits = [wake.SafeWaitHandle.DangerousGetHandle(), timer];
        int failures = 0, successful = 0; bool faulted = false;
        RenderOptions? lastOptions = null; int frameRate = 60;
        try
        {
            while (!stopping)
            {
                if (context is not null && context.Generation != AngleDevice.Generation) { context.Dispose(); context = null; }
                while (commands.TryDequeue(out var command))
                {
                    try { context ??= new ShaderContext(window); Renderer = context.Renderer; command.Run(context); faulted = false; failures = 0; }
                    catch (Exception error) { command.Fail(error); faulted = true; Error?.Invoke(error.Message); }
                }
                var current = Volatile.Read(ref options);
                if (!ReferenceEquals(current, lastOptions)) { frameRate = current.Fps == 0 ? Win32.RefreshRate() : current.Fps; lastOptions = current; }
                if (current.Paused || source is null || faulted)
                {
                    if (timer != 0) Win32.CancelWaitableTimer(timer);
                    wake.WaitOne(); previous = deadline = clock.Elapsed.TotalMilliseconds; continue;
                }
                try
                {
                    if (context is null)
                    {
                        context = new ShaderContext(window); context.Load(source); seconds = 0;
                        Renderer = context.Renderer;
                        previous = deadline = clock.Elapsed.TotalMilliseconds;
                        Recovered?.Invoke("GPU-рендерер восстановлен.");
                    }
                    if (!Win32.GetClientRect(window, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0) throw new IOException("Окно обоев недоступно.");
                    var now = clock.Elapsed.TotalMilliseconds; var elapsed = Math.Max(0, now - previous); previous = now; seconds += elapsed / 1000;
                    if (current.MouseEnabled && Win32.GetCursorPos(out var point))
                    {
                        Win32.ScreenToClient(window, ref point); motion.Target((double)point.X / bounds.Width, 1 - (double)point.Y / bounds.Height, true);
                    }
                    else motion.Target(.5, .5, false);
                    motion.Advance(elapsed);
                    mouseX = motion.X; mouseY = motion.Y;
                    context.Draw(bounds.Width, bounds.Height, current.Quality, seconds, motion.X, motion.Y);
                    Interlocked.Increment(ref frames);
                    if (++successful >= 60) { failures = 0; successful = 0; }
                    deadline += 1000.0 / frameRate;
                    if (deadline < clock.Elapsed.TotalMilliseconds) deadline = clock.Elapsed.TotalMilliseconds + 1000.0 / frameRate;
                    double delay = Math.Max(0, deadline - clock.Elapsed.TotalMilliseconds);
                    if (timer != 0)
                    {
                        long due = -(long)Math.Max(1, delay * 10000);
                        Win32.SetWaitableTimer(timer, ref due, 0, 0, 0, false);
                        Win32.WaitForMultipleObjects(2, waits, false, uint.MaxValue);
                    }
                    else wake.WaitOne((int)Math.Ceiling(delay));
                }
                catch (DeviceLostException error)
                {
                    int failedGeneration = context?.Generation ?? AngleDevice.Generation;
                    context?.Dispose(); context = null; AngleDevice.Reset(failedGeneration);
                    if (++failures > 5) { faulted = true; Error?.Invoke(error.Message + " Повторите установку обоев."); }
                    else { Recovered?.Invoke("Восстановление видеодрайвера…"); wake.WaitOne(500); }
                    previous = deadline = clock.Elapsed.TotalMilliseconds;
                }
                catch (Exception error) { faulted = true; Error?.Invoke(error.Message); }
            }
        }
        finally
        {
            while (commands.TryDequeue(out var command)) command.Fail(new ObjectDisposedException(nameof(RenderSurface)));
            context?.Dispose();
            if (timer != 0) Win32.CloseHandle(timer);
        }
    }
    public void Dispose()
    {
        if (stopping) return;
        stopping = true; AngleDevice.ResetRequested -= Wake; Wake();
        // Never free handles while an outstanding GPU call might still use them.
        if (thread.Join(5000)) wake.Dispose();
    }
}
