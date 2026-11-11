using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MagmPaper.Services;

internal sealed class AppearanceService : IAsyncDisposable
{
    private readonly string directory, file;
    private readonly SemaphoreSlim serial = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private Process? process;
    private int sequence;
    private string? loadError;
    private volatile bool terminating;
    public AppearanceSettings Settings { get; private set; } = new();
    public bool Active { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    public bool Available => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "taskbar", "MagmTaskbarHost.exe"));
    public AppearanceService(string directory)
    {
        this.directory = directory; file = Path.Combine(directory, "taskbar-appearance.json");
        try { if (File.Exists(file)) Settings = DataFiles.Read<AppearanceSettings>(file).Validate(); }
        catch { Error = loadError = "Не удалось прочитать taskbar-appearance.json. Исходный файл сохранён."; }
    }
    private static string NativeError(int code) => code switch
    {
        50 or unchecked((int)0x80004002) => "На этой сборке Windows не найдена совместимая XAML-панель задач.",
        5 or unchecked((int)0x80070005) => "Windows запретила подключение модуля оформления к панели задач.",
        258 => "Панель задач не подтвердила изменение вовремя.",
        _ => $"Модуль панели не подтвердил изменение (код {code})."
    };
    private string Cache()
    {
        string[] names = ["MagmTaskbarHost.exe", "MagmTaskbar.dll"];
        var contents = names.Select(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "taskbar", name))).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var bytes in contents) hash.AppendData(bytes);
        var destination = Path.Combine(directory, "NativeModules", Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24]);
        Directory.CreateDirectory(destination);
        for (int i = 0; i < names.Length; i++)
        {
            var path = Path.Combine(destination, names[i]);
            if (!File.Exists(path)) { using var output = new FileStream(path, FileMode.CreateNew); output.Write(contents[i]); }
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(contents[i])) throw new IOException("Копия модуля панели повреждена. Исходные файлы приложения не изменены.");
        }
        return Path.Combine(destination, names[0]);
    }
    private void Start()
    {
        if (terminating) throw new ObjectDisposedException(nameof(AppearanceService));
        if (process is not null) return;
        if (!Available) throw new IOException("Нужна полная сборка MagmPaper для Windows 11 x64 с модулем панели.");
        foreach (var name in new[] { "taskbar-icon-size", "windows-11-taskbar-styler" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Windhawk\Engine\Mods\" + name);
            if (key?.GetValue("Disabled") is int value && value == 0) throw new IOException("Отключите моды размеров и оформления панели в Windhawk перед включением оформления MagmPaper.");
        }
        var info = new ProcessStartInfo(Cache()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false) };
        info.ArgumentList.Add("--parent"); info.ArgumentList.Add(Environment.ProcessId.ToString());
        var child = Process.Start(info) ?? throw new IOException("Не удалось запустить модуль панели задач.");
        process = child; _ = ReadAsync(child); _ = child.StandardError.ReadToEndAsync();
    }
    private async Task ReadAsync(Process child)
    {
        try
        {
            while (await child.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > 65536) throw new IOException("Некорректный ответ модуля панели.");
                using var data = JsonDocument.Parse(line); var root = data.RootElement;
                int id = root.TryGetProperty("sequence", out var number) ? number.GetInt32() : 0;
                var kind = root.GetProperty("event").GetString();
                if (kind is "applied" or "error")
                {
                    if (pending.TryRemove(id, out var request))
                    {
                        if (kind == "error") request.TrySetException(new IOException(NativeError(root.GetProperty("code").GetInt32())));
                        else request.TrySetResult(root.Clone());
                    }
                    else if (kind == "error") { Error = NativeError(root.GetProperty("code").GetInt32()); _ = ShutdownAsync(); Changed?.Invoke(); }
                }
            }
        }
        catch (Exception error) { Error = error.Message; }
        finally
        {
            foreach (var entry in pending.ToArray()) if (pending.TryRemove(entry.Key, out var request)) request.TrySetException(new IOException(Error ?? "Модуль панели завершился до подтверждения настроек."));
            if (ReferenceEquals(process, child)) { process = null; Active = false; Changed?.Invoke(); }
        }
    }
    private async Task SendAsync(AppearanceSettings settings)
    {
        Start(); int id = Interlocked.Increment(ref sequence);
        var request = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); pending[id] = request;
        try
        {
            await process!.StandardInput.WriteLineAsync(settings.Encode(id)); await process.StandardInput.FlushAsync();
            var result = await request.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (result.GetProperty("errors").GetInt32() != 0 || (settings.Enabled && new[] { "frames", "icons", "backgrounds" }.Any(key => !result.TryGetProperty(key, out var count) || count.GetInt32() < 1)))
                throw new IOException("Модуль не подтвердил все элементы оформления панели.");
            Active = settings.Enabled;
        }
        finally { pending.TryRemove(id, out _); }
    }
    public async Task ApplyAsync(AppearanceSettings next)
    {
        next.Validate(); await serial.WaitAsync(); var previous = Settings; bool wasRunning = process is not null;
        try
        {
            if (loadError is not null) throw new InvalidDataException(loadError);
            if (next.Enabled || process is not null) await SendAsync(next);
            DataFiles.Write(file, next); Settings = next; Error = null; Changed?.Invoke();
        }
        catch (Exception failure)
        {
            if (!terminating && wasRunning && process is not null) { try { await SendAsync(previous); } catch { await ShutdownAsync(); } }
            else await ShutdownAsync();
            Error = failure.Message; Changed?.Invoke(); throw;
        }
        finally { serial.Release(); }
    }
    public async Task RestoreOnStartAsync() { if (Settings.Enabled && loadError is null) try { await ApplyAsync(Settings); } catch { } }
    public async Task ShutdownAsync(bool final = false)
    {
        if (final) terminating = true;
        var child = process; if (child is null) return;
        foreach (var entry in pending.ToArray()) if (pending.TryRemove(entry.Key, out var request)) request.TrySetCanceled();
        try
        {
            child.StandardInput.Close();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); }
            catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        }
        catch (InvalidOperationException) { }
        finally { if (ReferenceEquals(process, child)) process = null; Active = false; Changed?.Invoke(); }
    }
    public async ValueTask DisposeAsync() { await ShutdownAsync(final: true); }
}
