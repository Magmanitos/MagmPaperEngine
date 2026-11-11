namespace MagmPaper.Graphics;

// All contexts share one ANGLE display. A device reset first releases every
// context, including a paused preview, before terminating/reinitializing it.
internal static class AngleDevice
{
    private static readonly object Gate = new();
    private static nint display, config;
    private static int references, generation;
    private static bool resetting;
    public static bool SoftwareForTests { get; set; }
    public static int Generation => Volatile.Read(ref generation);
    public static event Action? ResetRequested;
    public static (nint Display, nint Config, int Generation) Acquire()
    {
        lock (Gate)
        {
            while (resetting && references != 0) Monitor.Wait(Gate);
            if (resetting && display != 0) { Gpu.EglTerminate(display); display = 0; }
            if (display == 0)
            {
                Diagnostics.Trace("EGL get display");
                var candidate = Gpu.EglGetPlatformDisplayEXT(0x3202, 0, [0x3203, 0x3208, 0x3209, SoftwareForTests ? 0x320b : 0x320a, 0x3038]);
                Diagnostics.Trace("EGL initialize");
                if (candidate == 0 || Gpu.EglInitialize(candidate, out _, out _) == 0)
                    throw new IOException($"Не удалось запустить Direct3D 11 для обоев (EGL 0x{Gpu.EglGetError():X}). Обновите драйвер видеокарты.");
                Diagnostics.Trace("EGL choose config");
                int[] attributes = [0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 0, 0x3033, 4, 0x3040, 4, 0x3038];
                if (Gpu.EglChooseConfig(candidate, attributes, out config, 1, out var count) == 0 || count != 1)
                { Gpu.EglTerminate(candidate); throw new IOException("Не найден подходящий формат GPU-поверхности."); }
                display = candidate; resetting = false;
            }
            references++; return (display, config, generation);
        }
    }
    public static void Release() { lock (Gate) { references--; Monitor.PulseAll(Gate); } }
    public static void Reset(int failedGeneration)
    {
        lock (Gate)
        {
            if (failedGeneration != generation) return;
            resetting = true; Interlocked.Increment(ref generation);
        }
        ResetRequested?.Invoke();
    }
    public static void Shutdown()
    {
        lock (Gate) { if (references == 0 && display != 0) { Gpu.EglTerminate(display); display = 0; } }
    }
}
