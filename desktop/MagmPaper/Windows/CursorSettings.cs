using System.ComponentModel;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace MagmPaper.Windows;

internal sealed record CursorAsset(string Name, byte[] Bytes);

internal sealed record CursorPack(string Name, string Repository, string Tag, string File, string Sha256)
{
    public string Source => $"https://github.com/{Repository}/releases/tag/{Tag}";
    public string Download => $"https://github.com/{Repository}/releases/download/{Tag}/{File}.zip";
}

internal sealed class CursorSettings(string directory)
{
    private const string RegistryPath = @"Control Panel\Cursors";
    private const string SchemeSource = "Scheme Source";
    private const int MaxArchiveBytes = 32 * 1024 * 1024;
    private const int MaxEntries = 4096;
    private readonly string backupFile = Path.Combine(directory, "cursor-backup.json");
    private readonly string assetDirectory = Path.Combine(directory, "Cursors");
    private readonly string cacheDirectory = Path.Combine(directory, "CursorPacks");

    private sealed record RegistryValue(RegistryValueKind Kind, string? Text, int Number = 0);
    private static readonly string[] Keys = [.. CursorFiles.Roles.Select(role => role.Key), "", SchemeSource];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static IReadOnlyList<CursorPack> Packs { get; } =
    [
        new("Bibata Modern Ice", "ful1e5/Bibata_Cursor", "v2.0.7", "Bibata-Modern-Ice-Windows",
            "0045e40324da5b540b3bee260f53d0792df62be9cdef91655a024ae9f151bd04"),
        new("Bibata Modern Classic", "ful1e5/Bibata_Cursor", "v2.0.7", "Bibata-Modern-Classic-Windows",
            "b98eca4077d6e7796ff7e710e4d220ca13439f1118160d1e7b3db9e91fdec2be"),
        new("Bibata Modern Amber", "ful1e5/Bibata_Cursor", "v2.0.7", "Bibata-Modern-Amber-Windows",
            "5151e0f8648d00fb734fea501def12b5cc1fbc65ba12a6e28db2e1295ebf8961"),
        new("GoogleDot Black", "ful1e5/Google_Cursor", "v2.0.0", "GoogleDot-Black-Windows",
            "10876e7c232e19e0805aba1aa2ca4393d047e8091cf3fe0266db567078bf2a71")
    ];

    public bool CanRestore => File.Exists(backupFile);

    public Dictionary<string, string?> Read()
    {
        using var registry = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return CursorFiles.Roles.ToDictionary(role => role.Key, role => registry?.GetValue(role.Key) as string);
    }

    public int Apply(Dictionary<string, CursorAsset> cursors)
    {
        if (cursors.Count == 0) throw new InvalidDataException("В наборе нет распознанных курсоров. Назначьте файлы отдельным состояниям.");
        if (cursors.Keys.Any(key => !CursorFiles.Roles.Any(role => role.Key == key)))
            throw new InvalidDataException("Неизвестное состояние курсора.");
        var values = cursors.ToDictionary(pair => pair.Key, pair =>
        {
            string path = Store(pair.Key, pair.Value);
            return (RegistryValue?)new RegistryValue(RegistryValueKind.String, path);
        });
        Update(values);
        return cursors.Count;
    }

    public void Reset(string key)
    {
        if (!CursorFiles.Roles.Any(role => role.Key == key)) throw new ArgumentException(nameof(key));
        Update(new() { [key] = new(RegistryValueKind.String, "") });
    }

    private void Update(Dictionary<string, RegistryValue?> patch)
    {
        if (CanRestore) _ = ReadBackup();
        using var registry = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        var before = Snapshot(registry);
        if (!CanRestore) DataFiles.Write(backupFile, before);
        patch[""] = new(RegistryValueKind.String, "");
        patch[SchemeSource] = new(RegistryValueKind.DWord, null, 0);
        WriteWithRollback(registry, before, patch);
    }

    public void Restore()
    {
        var original = ReadBackup();
        using var registry = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        WriteWithRollback(registry, Snapshot(registry), original);
        File.Delete(backupFile);
    }

    private Dictionary<string, RegistryValue?> ReadBackup()
    {
        var saved = DataFiles.Read<Dictionary<string, RegistryValue?>>(backupFile);
        if (saved.Count != Keys.Length || Keys.Any(key => !saved.ContainsKey(key)) ||
            saved.Values.Any(value => value is not null && value.Kind != RegistryValueKind.DWord &&
                (value.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) || value.Text is null)))
            throw new InvalidDataException("Резервная копия курсоров повреждена. Исходный файл сохранён.");
        return saved;
    }

    private static Dictionary<string, RegistryValue?> Snapshot(RegistryKey registry) => Keys.ToDictionary(key => key, key =>
    {
        object? value = registry.GetValue(key, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            null => null,
            string text => new RegistryValue(registry.GetValueKind(key), text),
            int number => new RegistryValue(RegistryValueKind.DWord, null, number),
            _ => throw new InvalidDataException("Не удалось сохранить исходную схему курсоров Windows.")
        };
    });

    private static void WriteWithRollback(RegistryKey registry, Dictionary<string, RegistryValue?> before,
        Dictionary<string, RegistryValue?> next)
    {
        try { Write(registry, next); }
        catch (Exception cause)
        {
            try { Write(registry, before); }
            catch (Exception rollback)
            {
                throw new IOException("Windows не подтвердила изменения и восстановление. Резервная копия сохранена.",
                    new AggregateException(cause, rollback));
            }
            throw;
        }
    }

    private static void Write(RegistryKey registry, Dictionary<string, RegistryValue?> values)
    {
        foreach (var (key, value) in values)
        {
            if (value is null) registry.DeleteValue(key, throwOnMissingValue: false);
            else registry.SetValue(key, value.Kind == RegistryValueKind.DWord ? (object)value.Number : value.Text!, value.Kind);
        }
        if (!SystemParametersInfo(0x0057, 0, 0, 0x2)) // SPI_SETCURSORS, SPIF_SENDCHANGE
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не смогла обновить курсоры.");
    }

    private string Store(string key, CursorAsset asset)
    {
        string extension = Path.GetExtension(asset.Name).ToLowerInvariant();
        if (!CursorFiles.HasCursorHeader(extension, asset.Bytes))
            throw new InvalidDataException($"Файл {asset.Name} не является курсором Windows.");
        Directory.CreateDirectory(assetDirectory);
        // Keep only our own fixed role name and the file hash in the path presented to Windows.
        string path = Path.Combine(assetDirectory, $"{key}-{Convert.ToHexString(SHA256.HashData(asset.Bytes))[..20]}{extension}");
        bool created = !File.Exists(path);
        if (!created && !ReadFile(path).AsSpan().SequenceEqual(asset.Bytes))
            throw new IOException("Сохранённый файл курсора изменён. Выберите другой файл.");
        if (created)
        {
            using var file = new FileStream(path, FileMode.CreateNew);
            file.Write(asset.Bytes);
        }
        try
        {
            using var cursor = new System.Windows.Input.Cursor(path, scaleWithDpi: true);
        }
        catch (Exception error)
        {
            if (created) File.Delete(path);
            throw new InvalidDataException($"Windows не смогла открыть курсор {asset.Name}.", error);
        }
        return path;
    }

    public static byte[] ReadFile(string path, int max = CursorFiles.MaxFileBytes)
    {
        using var stream = File.OpenRead(path);
        return ReadBytes(stream, stream.Length, max);
    }

    private static byte[] ReadBytes(Stream stream, long size, int max)
    {
        if (size <= 0 || size > max) throw new InvalidDataException($"Файл пуст или превышает {max / (1024 * 1024)} МБ.");
        var bytes = new byte[(int)size];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Размер файла изменился при чтении.");
        return bytes;
    }

    public async Task<Dictionary<string, CursorAsset>> DownloadAsync(CursorPack pack)
    {
        string path = Path.Combine(cacheDirectory, pack.File + ".zip");
        byte[]? bytes = File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaxArchiveBytes
            ? ReadFile(path, MaxArchiveBytes) : null;
        bool Verified(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload)).Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase);
        if (bytes is null || !Verified(bytes))
        {
            using var response = await Http.GetAsync(pack.Download, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(chunk.AsMemory(), timer.Token)) != 0)
            {
                if (buffer.Length + count > MaxArchiveBytes) throw new InvalidDataException("Архив курсоров превышает 32 МБ.");
                buffer.Write(chunk, 0, count);
            }
            bytes = buffer.ToArray();
            if (!Verified(bytes)) throw new InvalidDataException("Контрольная сумма набора не совпадает с релизом автора.");
            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllBytesAsync(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        return ReadArchive(bytes);
    }

    public static Dictionary<string, CursorAsset> ReadArchive(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("В ZIP слишком много записей.");
        var match = FindBest(archive.Entries, entry => entry.FullName.Replace('\\', '/'));
        return match.ToDictionary(pair => pair.Key, pair =>
        {
            using var input = pair.Value.Open();
            return new CursorAsset(pair.Value.Name, ReadBytes(input, pair.Value.Length, CursorFiles.MaxFileBytes));
        });
    }

    public static Dictionary<string, CursorAsset> ReadFolder(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, AttributesToSkip = FileAttributes.ReparsePoint };
        var paths = Directory.EnumerateFiles(folder, "*", options).Take(MaxEntries + 1).ToArray();
        if (paths.Length > MaxEntries) throw new InvalidDataException("В папке слишком много файлов.");
        return FindBest(paths, path => Path.GetRelativePath(folder, path).Replace('\\', '/'))
            .ToDictionary(pair => pair.Key, pair => new CursorAsset(Path.GetFileName(pair.Value), ReadFile(pair.Value)));
    }

    public static Dictionary<string, CursorAsset> ReadZipFile(string path) => ReadArchive(ReadFile(path, MaxArchiveBytes));

    private static Dictionary<string, T> FindBest<T>(IEnumerable<T> files, Func<T, string> name) =>
        files.Where(file => CursorFiles.IsCursor(name(file)))
            .GroupBy(file => Path.GetDirectoryName(name(file)) ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group => (Directory: group.Key, Roles: CursorFiles.Match(group, name)))
            .OrderByDescending(group => group.Roles.Count)
            .ThenByDescending(group => group.Directory.Contains("-Regular-", StringComparison.OrdinalIgnoreCase))
            .ThenBy(group => group.Directory, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault().Roles ?? new();

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, nint data, uint flags);
}
