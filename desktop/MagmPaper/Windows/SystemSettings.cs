using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.Win32;

namespace MagmPaper.Windows;

internal sealed record NativeTaskbarSettings(int Alignment, int Search, bool TaskView, bool Widgets, bool AutoHide);
internal sealed record LegacySizes(int Height = 48, int Icon = 24, int Button = 44, int SmallIcon = 16, int SmallButton = 32);

internal sealed class SystemSettings
{
    internal const string StartupName = "com.magmanitos.magmpaper";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string Mod = @"SOFTWARE\Windhawk\Engine\Mods\taskbar-icon-size";
    private readonly string file;
    private readonly JsonObject backup;
    private static readonly Dictionary<string, (string Key, string Value, int Default, string Label)> Keys = new()
    {
        ["alignment"] = (Advanced, "TaskbarAl", 1, "Расположение кнопок"), ["search"] = (SearchKey, "SearchboxTaskbarMode", 2, "Поиск"),
        ["taskView"] = (Advanced, "ShowTaskViewButton", 1, "Представление задач"), ["widgets"] = (Advanced, "TaskbarDa", 1, "Виджеты")
    };
    private static readonly string[] SizeKeys = ["TaskbarHeight", "IconSize", "TaskbarButtonWidth", "IconSizeSmall", "TaskbarButtonWidthSmall"];
    public SystemSettings(string directory)
    {
        file = Path.Combine(directory, "taskbar-backup.json");
        backup = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file, DataFiles.Utf8))!.AsObject() : new() { ["native"] = new JsonObject(), ["sizes"] = null };
        if (backup["native"] is not JsonObject native) throw new InvalidDataException("Повреждена резервная копия панели; файл сохранён.");
        foreach (var item in native)
        {
            if (item.Key == "autoHide") { _ = item.Value!.GetValue<bool>(); continue; }
            if (!Keys.ContainsKey(item.Key) || item.Value is not null && item.Value.Deserialize<long>() is < 0 or > uint.MaxValue) throw new InvalidDataException("Повреждена резервная копия панели; файл сохранён.");
        }
        if (backup["sizes"] is not null) ValidateLegacyBackup(backup["sizes"]!.AsObject());
    }
    private static JsonNode? Read(string key)
    {
        if (key == "autoHide") return JsonValue.Create((AppBar(4) & 1) != 0);
        var spec = Keys[key]; using var registry = Registry.CurrentUser.OpenSubKey(spec.Key);
        var value = registry?.GetValue(spec.Value);
        return value is int number ? JsonValue.Create(unchecked((uint)number)) : null;
    }
    private static void Write(string key, JsonNode? value)
    {
        if (key == "autoHide") { var flags = AppBar(4); AppBar(10, value!.GetValue<bool>() ? flags | 1 : flags & ~1u); return; }
        var spec = Keys[key]; using var registry = Registry.CurrentUser.CreateSubKey(spec.Key, true);
        if (value is null) registry.DeleteValue(spec.Value, false);
        else registry.SetValue(spec.Value, unchecked((int)value.Deserialize<uint>()), RegistryValueKind.DWord);
    }
    private static uint AppBar(uint message, uint value = 0)
    {
        var window = Win32.FindWindow("Shell_TrayWnd", null); if (window == 0) throw new IOException("Панель задач Windows сейчас недоступна.");
        var data = new Win32.AppBarData { Size = (uint)Marshal.SizeOf<Win32.AppBarData>(), Window = window, Param = (nint)value };
        return (uint)Win32.SHAppBarMessage(message, ref data);
    }
    public NativeTaskbarSettings ReadTaskbar() => new((int)(Read("alignment")?.GetValue<uint>() ?? 1), (int)(Read("search")?.GetValue<uint>() ?? 2),
        (Read("taskView")?.GetValue<uint>() ?? 1) != 0, (Read("widgets")?.GetValue<uint>() ?? 1) != 0, Read("autoHide")!.GetValue<bool>());
    public bool CanRestore => backup["native"]!.AsObject().Count > 0;
    public bool CanRestoreLegacy => backup["sizes"] is not null;
    private void Persist() => DataFiles.Replace(file, backup.ToJsonString(DataFiles.Json));
    private static string Failure(string key, Exception error) => $"{(key == "autoHide" ? "Скрытие панели" : Keys[key].Label)}: " +
        (error is UnauthorizedAccessException || error is System.Security.SecurityException ? "Windows запретила изменение. Используйте «Открыть параметры Windows»." : error.Message);
    public string Apply(NativeTaskbarSettings next)
    {
        if (next.Alignment is not (0 or 1) || next.Search is < 0 or > 3) throw new InvalidDataException("Некорректные параметры панели.");
        var patch = new JsonObject { ["alignment"] = next.Alignment, ["search"] = next.Search, ["taskView"] = next.TaskView ? 1 : 0, ["autoHide"] = next.AutoHide };
        var original = backup["native"]!.AsObject(); var changed = new List<string>(); var failures = new List<string>();
        foreach (var item in patch)
        {
            var before = Read(item.Key); var effective = before ?? JsonValue.Create(Keys[item.Key].Default);
            if (JsonNode.DeepEquals(effective, item.Value)) continue;
            bool saved = original.ContainsKey(item.Key); if (!saved) original[item.Key] = before?.DeepClone(); Persist();
            try
            {
                Write(item.Key, item.Value); if (!JsonNode.DeepEquals(Read(item.Key), item.Value)) throw new IOException("Windows не подтвердила изменение.");
                changed.Add(item.Key);
            }
            catch (Exception error)
            {
                failures.Add(Failure(item.Key, error));
                try { if (!JsonNode.DeepEquals(Read(item.Key), before)) Write(item.Key, before); if (!saved && JsonNode.DeepEquals(Read(item.Key), before)) original.Remove(item.Key); } catch { }
            }
            Persist();
        }
        if (changed.Count > 0) Notify();
        return (changed.Count > 0 ? "Настройки поведения сохранены. Некоторым изменениям нужен повторный вход в Windows. " : "Изменений нет. ") + string.Join(" ", failures);
    }
    public string Restore()
    {
        var original = backup["native"]!.AsObject(); var failures = new List<string>(); bool changed = false;
        foreach (var item in original.ToArray())
        {
            try
            {
                if (!JsonNode.DeepEquals(Read(item.Key), item.Value)) { Write(item.Key, item.Value); changed = true; }
                if (!JsonNode.DeepEquals(Read(item.Key), item.Value)) throw new IOException("Восстановление не подтверждено.");
                original.Remove(item.Key);
            }
            catch (Exception error) { failures.Add(Failure(item.Key, error)); }
        }
        Persist(); if (changed) Notify();
        return failures.Count == 0 ? "Исходные настройки поведения возвращены." : string.Join(" ", failures);
    }
    private static void Notify() => Win32.Broadcast(0xffff, 0x1a, 0, "TraySettings", 2, 1000, out _);
    public static bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            using var approved = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            var bytes = approved?.GetValue(StartupName) as byte[];
            return key?.GetValue(StartupName) is string value && value.Length > 0 && !(bytes is { Length: > 0 } && bytes[0] is 3 or 7);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value)
            {
                key.SetValue(StartupName, $"\"{Environment.ProcessPath}\" --autostart", RegistryValueKind.String);
                using var approved = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true);
                approved?.DeleteValue(StartupName, false);
            }
            else key.DeleteValue(StartupName, false);
            if (StartupEnabled != value) throw new IOException("Windows не подтвердила автозапуск. Проверьте параметры автозагрузки Windows.");
        }
    }
    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    public bool LegacyAvailable { get { using var key = Registry.LocalMachine.OpenSubKey(Mod); return key?.GetValue("Disabled") is int d && d == 0; } }
    public LegacySizes ReadLegacy()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Mod + @"\Settings");
        int[] fallback = [48, 24, 44, 16, 32]; var values = SizeKeys.Select((name, i) => key?.GetValue(name) as int? ?? fallback[i]).ToArray();
        return new(values[0], values[1], values[2], values[3], values[4]);
    }
    private static void ValidateLegacyBackup(JsonObject values)
    {
        foreach (var item in values) if (!SizeKeys.Contains(item.Key) || item.Value is not null && item.Value.Deserialize<long>() is < 0 or > uint.MaxValue) throw new InvalidDataException("Повреждены резервные размеры панели.");
    }
    public async Task ApplyLegacyAsync(LegacySizes sizes, bool restore = false)
    {
        if (!LegacyAvailable) throw new IOException("Включите прежний мод размеров Windhawk для этой операции.");
        int[] input = [sizes.Height, sizes.Icon, sizes.Button, sizes.SmallIcon, sizes.SmallButton];
        int[] min = [32, 16, 28, 16, 24], max = [96, 64, 100, 32, 64];
        if (!restore && (input.Where((n, i) => n < min[i] || n > max[i]).Any() || sizes.Icon + 8 > sizes.Height || sizes.Button < sizes.Icon || sizes.SmallButton < sizes.SmallIcon)) throw new InvalidDataException("Некорректные размеры Windhawk.");
        if (backup["sizes"] is null && !restore)
        {
            using var key = Registry.LocalMachine.OpenSubKey(Mod + @"\Settings"); var values = new JsonObject();
            foreach (var name in SizeKeys) values[name] = key?.GetValue(name) is int v ? JsonValue.Create(unchecked((uint)v)) : null;
            backup["sizes"] = values; Persist();
        }
        var patch = restore ? backup["sizes"]?.AsObject() ?? throw new IOException("Нет сохранённых размеров.") : new JsonObject(SizeKeys.Select((name, i) => KeyValuePair.Create<string, JsonNode?>(name, JsonValue.Create(input[i]))));
        ValidateLegacyBackup(patch);
        var script = new StringBuilder("$ErrorActionPreference='Stop'\n$p='Registry::HKEY_LOCAL_MACHINE\\" + Mod + "\\Settings'\n$entry=Get-Item -LiteralPath $p\n$old=@{}\n");
        foreach (var name in SizeKeys) script.AppendLine($"$old['{name}']=$entry.GetValue('{name}',$null)");
        script.AppendLine("try {");
        foreach (var item in patch)
            script.AppendLine(item.Value is null ? $"Remove-ItemProperty -LiteralPath $p -Name '{item.Key}' -ErrorAction SilentlyContinue" : $"New-ItemProperty -LiteralPath $p -Name '{item.Key}' -Value ([uint32]{item.Value.Deserialize<long>()}) -PropertyType DWord -Force | Out-Null");
        script.AppendLine("$m=Split-Path $p\n$t=[int]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -band 2147483647)\n$v=(Get-Item -LiteralPath $m).GetValue('SettingsChangeTime',0)\nif($t -le $v){$t=($v+1) -band 2147483647}\nNew-ItemProperty -LiteralPath $m -Name SettingsChangeTime -Value $t -PropertyType DWord -Force | Out-Null\nexit 0\n} catch { foreach($n in $old.Keys){if($null -eq $old[$n]){Remove-ItemProperty -LiteralPath $p -Name $n -ErrorAction SilentlyContinue}else{New-ItemProperty -LiteralPath $p -Name $n -Value $old[$n] -PropertyType DWord -Force | Out-Null}}; exit 1 }");
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить настройку Windhawk.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        if (process.ExitCode != 0) throw new IOException("Windhawk не подтвердил изменение размеров.");
        using var readback = Registry.LocalMachine.OpenSubKey(Mod + @"\Settings");
        foreach (var item in patch)
        {
            var actual = readback?.GetValue(item.Key) is int n ? (long?)unchecked((uint)n) : null;
            if (actual != item.Value?.Deserialize<long>()) throw new IOException("Windhawk не сохранил размеры. Резервная копия сохранена.");
        }
        if (restore) { backup["sizes"] = null; Persist(); }
    }
}
