using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MagmPaper.Core;

public enum WallpaperFormat { Glsl, Gif, Mp4 }
public sealed record Wallpaper(string Id, string Name, string File, string Code, long MediaStamp = 0)
{
    public WallpaperFormat Format => Library.FormatOf(File);
}
public sealed record LibraryState(IReadOnlyList<Wallpaper> Items, string? ActiveId, PlaybackSettings Settings, string? Revision = null);
public sealed record FileChange(string File, string? Before, string? After);
public sealed record MediaFileChange(string File, string Stage, bool Import, long? Stamp = null);
public sealed record FileTransaction(string Revision, FileChange[] Changes, MediaFileChange[]? MediaChanges = null);

public sealed class Library
{
    public string DirectoryPath { get; }
    public string ShaderDirectory { get; }
    public string Manifest { get; }
    public LibraryState State { get; private set; } = new([], null, new());
    public bool IsNew { get; }
    private static readonly Regex BadName = new("[<>:\"/\\\\|?*\\x00-\\x1f]", RegexOptions.Compiled);
    private static readonly Regex Reserved = new("^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\\.|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Library(string directory)
    {
        DirectoryPath = directory;
        ShaderDirectory = Path.Combine(directory, "Wallpapers"); Manifest = Path.Combine(directory, "wallpapers.json");
        Directory.CreateDirectory(ShaderDirectory);
        Recover(); IsNew = !File.Exists(Manifest);
        if (!IsNew)
        {
            var root = JsonNode.Parse(File.ReadAllText(Manifest, DataFiles.Utf8))!.AsObject();
            var parsed = Parse(root, ShaderDirectory);
            // Preserve the original Electron manifest before the first native launch.
            if (!File.Exists(Manifest + ".electron-backup")) File.Copy(Manifest, Manifest + ".electron-backup");
            if (root["version"]!.GetValue<int>() == 1)
            {
                if (!File.Exists(Manifest + ".v1-backup")) File.Copy(Manifest, Manifest + ".v1-backup");
                var occupied = Occupied();
                var items = parsed.Items.Select(item => { var file = FileName(item.Name, occupied); occupied.Add(file); return item with { File = file }; }).ToArray();
                Commit(parsed with { Items = items });
            }
            else State = parsed;
        }
        Refresh();
    }

    public static WallpaperFormat FormatOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".gif" => WallpaperFormat.Gif,
        ".mp4" => WallpaperFormat.Mp4,
        _ => WallpaperFormat.Glsl
    };
    private static bool HasSupportedExtension(string file) => Path.GetExtension(file).ToLowerInvariant() is ".glsl" or ".gif" or ".mp4";
    public static bool SafeFile(string? file) => file is not null && file.Length <= 180 && HasSupportedExtension(file) &&
        !BadName.IsMatch(file) && !Reserved.IsMatch(file);
    public static string FileName(string name, HashSet<string> occupied, string extension = ".glsl")
    {
        var basis = BadName.Replace(name.Normalize(NormalizationForm.FormC), "_").TrimEnd('.', ' ').Trim();
        if (basis.Length == 0) basis = "Обои";
        if (Reserved.IsMatch(basis)) basis = "_" + basis;
        var file = basis + extension;
        for (var i = 2; occupied.Contains(file); i++) file = $"{basis} ({i}){extension}";
        return file;
    }
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new InvalidDataException("Введите название от 1 до 80 символов.");
    }
    public static string SuggestedMediaName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Trim();
        if (name.Length > 80) name = name[..80].TrimEnd();
        return name.Length > 0 ? name : "Обои";
    }
    public static void Validate(string name, string code)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100000) throw new InvalidDataException("Вставьте GLSL-код, не более 100 000 символов.");
    }
    private HashSet<string> Occupied(string? except = null) => new(Directory.EnumerateFileSystemEntries(ShaderDirectory)
        .Select(Path.GetFileName).Where(x => x != except).Select(x => x!), StringComparer.OrdinalIgnoreCase);
    public static string? ReadCode(string directory, string file)
    {
        if (!SafeFile(file) || FormatOf(file) != WallpaperFormat.Glsl) throw new InvalidDataException("Некорректное имя GLSL-файла.");
        var path = Path.Combine(directory, file);
        if (!File.Exists(path)) { if (Directory.Exists(path)) throw new IOException("Вместо GLSL-файла найдена папка."); return null; }
        var info = new FileInfo(path);
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Length > 400000)
            throw new IOException($"Недоступен файл обоев: {file}");
        return File.ReadAllText(path, DataFiles.Utf8);
    }
    private static FileInfo? MediaInfo(string directory, string file)
    {
        if (!SafeFile(file) || FormatOf(file) == WallpaperFormat.Glsl)
            throw new InvalidDataException("Некорректное имя медиафайла.");
        var info = new FileInfo(Path.Combine(directory, file));
        if (!info.Exists) return null;
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException($"Недоступен файл обоев: {file}");
        return info;
    }

    public string MediaPath(Wallpaper item) => MediaInfo(ShaderDirectory, item.File)?.FullName
        ?? throw new IOException($"Не найден файл обоев: {item.File}");

    private static Wallpaper? ReadItem(Wallpaper item, string directory)
    {
        ValidateName(item.Name);
        if (item.Format == WallpaperFormat.Glsl)
        {
            var code = ReadCode(directory, item.File);
            if (code is null) return null;
            Validate(item.Name, code);
            return item with { Code = code };
        }
        var info = MediaInfo(directory, item.File);
        return info is null ? null : item with { MediaStamp = Stamp(info) };
    }

    private static long Stamp(FileInfo file) => file.LastWriteTimeUtc.Ticks ^ file.Length;

    private static LibraryState Parse(JsonObject root, string shaderDirectory)
    {
        int version = root["version"]?.GetValue<int>() ?? 0;
        if (version is not (1 or 2) || root["items"] is not JsonArray list) throw new InvalidDataException("Неизвестный формат коллекции.");
        var ids = new HashSet<string>(); var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var items = new List<Wallpaper>();
        foreach (var node in list)
        {
            var item = node?.AsObject() ?? throw new InvalidDataException("Повреждена запись коллекции.");
            string id = item["id"]?.GetValue<string>() ?? "", name = item["name"]?.GetValue<string>() ?? "";
            string file = version == 2 ? item["file"]?.GetValue<string>() ?? "" : "";
            string code = version == 1 ? item["code"]?.GetValue<string>() ?? "" : "metadata";
            Validate(name, code);
            if (id.Length == 0 || !ids.Add(id) || (version == 2 && (!SafeFile(file) || !files.Add(file)))) throw new InvalidDataException("Повреждены идентификаторы или имена файлов коллекции.");
            var entry = new Wallpaper(id, name, file, version == 1 ? code : "");
            items.Add(version == 1 ? entry : ReadItem(entry, shaderDirectory) ?? entry);
        }
        var active = root["activeId"]?.GetValue<string>();
        if (active is not null && !ids.Contains(active)) throw new InvalidDataException("Повреждён выбор активных обоев.");
        var settings = root["settings"]?.Deserialize<PlaybackSettings>(DataFiles.Json)?.Validate() ?? new();
        return new(items, active, settings, root["revision"]?.GetValue<string>());
    }
    private static string Serialize(LibraryState state, string revision) => JsonSerializer.Serialize(new
    {
        version = 2, items = state.Items.Select(x => new { id = x.Id, name = x.Name, file = x.File }),
        activeId = state.ActiveId, settings = state.Settings, revision
    }, DataFiles.Json);
    public void Recover()
    {
        var journal = Manifest + ".journal";
        if (!File.Exists(journal)) return;
        var transaction = DataFiles.Read<FileTransaction>(journal);
        var revision = File.Exists(Manifest) ? JsonNode.Parse(File.ReadAllText(Manifest, DataFiles.Utf8))?["revision"]?.GetValue<string>() : null;
        bool committed = transaction.Revision == revision;
        if (!committed)
            foreach (var change in transaction.Changes)
            {
                var current = ReadCode(ShaderDirectory, change.File);
                if (current == change.Before) continue;
                if (current != change.After) throw new IOException($"Файл {change.File} изменён другим редактором. Файл и журнал сохранены: {journal}");
                DataFiles.Replace(Path.Combine(ShaderDirectory, change.File), change.Before);
            }
        foreach (var change in transaction.MediaChanges ?? [])
        {
            var (file, stage) = MediaChangePaths(change);
            if (!committed && !change.Import && File.Exists(stage))
            {
                if (File.Exists(file)) throw new IOException($"Файл {change.File} изменён другим редактором. Резервная копия сохранена.");
                File.Move(stage, file);
            }
            if (!committed && change.Import && !File.Exists(stage) && File.Exists(file))
            {
                if (change.Stamp is { } expected && Stamp(new FileInfo(file)) != expected)
                    throw new IOException($"Файл {change.File} изменён другим редактором. Журнал сохранён.");
                File.Move(file, stage);
            }
            File.Delete(stage);
        }
        File.Delete(journal);
    }
    private (string File, string Stage) MediaChangePaths(MediaFileChange change)
    {
        if (!SafeFile(change.File) || FormatOf(change.File) == WallpaperFormat.Glsl ||
            !change.Stage.StartsWith(".media-", StringComparison.Ordinal) || !change.Stage.EndsWith(".tmp", StringComparison.Ordinal) ||
            !Guid.TryParseExact(change.Stage[7..^4], "N", out _))
            throw new InvalidDataException("Повреждён журнал медиатеки.");
        return (Path.Combine(ShaderDirectory, change.File), Path.Combine(ShaderDirectory, change.Stage));
    }
    private static string MediaStage() => ".media-" + Guid.NewGuid().ToString("N") + ".tmp";

    private void Commit(LibraryState next, MediaFileChange? import = null)
    {
        Recover(); next.Settings.Validate();
        var changes = new List<FileChange>();
        var media = new List<MediaFileChange>();
        if (import is not null) media.Add(import);
        foreach (var item in next.Items.Where(item => item.Format == WallpaperFormat.Glsl))
        {
            Validate(item.Name, item.Code);
            var before = ReadCode(ShaderDirectory, item.File);
            if (before != item.Code) changes.Add(new(item.File, before, item.Code));
        }
        foreach (var item in State.Items.Where(x => !next.Items.Any(n => n.File == x.File)))
        {
            if (item.Format != WallpaperFormat.Glsl)
            {
                if (MediaInfo(ShaderDirectory, item.File) is not null) media.Add(new(item.File, MediaStage(), false));
            }
            else
            {
                var before = ReadCode(ShaderDirectory, item.File);
                if (before is not null) changes.Add(new(item.File, before, null));
            }
        }
        var revision = Guid.NewGuid().ToString();
        File.WriteAllText(Manifest + ".tmp", Serialize(next, revision), DataFiles.Utf8);
        DataFiles.Write(Manifest + ".journal", new FileTransaction(revision, changes.ToArray(), media.ToArray()));
        try
        {
            foreach (var change in changes)
            {
                if (ReadCode(ShaderDirectory, change.File) != change.Before) throw new IOException($"Файл {change.File} изменён другим редактором. Обновите коллекцию.");
                DataFiles.Replace(Path.Combine(ShaderDirectory, change.File), change.After);
            }
            foreach (var change in media)
            {
                var (file, stage) = MediaChangePaths(change);
                File.Move(change.Import ? stage : file, change.Import ? file : stage);
            }
            File.Move(Manifest + ".tmp", Manifest, true);
        }
        catch { Recover(); throw; }
        State = next with { Revision = revision };
        try { Recover(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public LibraryState Refresh()
    {
        Recover(); var items = new List<Wallpaper>(); var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in State.Items)
        {
            var current = ReadItem(item, ShaderDirectory);
            if (current is null) continue;
            items.Add(current); known.Add(item.File);
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(ShaderDirectory).OrderBy(x => x, StringComparer.Ordinal))
        {
            var file = Path.GetFileName(path);
            if (!HasSupportedExtension(file) || known.Contains(file)) continue;
            var name = Path.GetFileNameWithoutExtension(file); if (name.Length > 80) name = name[..80];
            items.Add(ReadItem(new Wallpaper(Guid.NewGuid().ToString(), name, file, ""), ShaderDirectory)
                ?? throw new IOException($"Файл исчез: {file}"));
        }
        var next = State with { Items = items, ActiveId = items.Any(i => i.Id == State.ActiveId) ? State.ActiveId : null };
        if (!State.Items.Select(x => (x.Id, x.Name, x.File)).SequenceEqual(items.Select(x => (x.Id, x.Name, x.File))) || next.ActiveId != State.ActiveId) Commit(next);
        else State = next;
        return State;
    }
    public Wallpaper Get(string id) => Refresh().Items.FirstOrDefault(x => x.Id == id) ?? throw new InvalidDataException("Обои не найдены.");
    public Wallpaper Save(string? id, string name, string code)
    {
        name = name.Trim(); Validate(name, code); Refresh();
        var previous = id is null ? null : Get(id);
        if (previous is not null && previous.Format != WallpaperFormat.Glsl) throw new InvalidDataException("Код можно редактировать только у GLSL-обоев.");
        var file = previous?.Name == name ? previous.File : FileName(name, Occupied(previous?.File));
        if (string.Equals(previous?.File, file, StringComparison.OrdinalIgnoreCase)) file = previous!.File;
        var item = new Wallpaper(previous?.Id ?? Guid.NewGuid().ToString(), name, file, code);
        var items = State.Items.ToList();
        if (previous is null) items.Add(item); else items[items.FindIndex(x => x.Id == id)] = item;
        Commit(State with { Items = items }); return item;
    }
    public async Task<Wallpaper> ImportMediaAsync(string source, WallpaperFormat format, string? name = null)
    {
        if (format is not (WallpaperFormat.Gif or WallpaperFormat.Mp4) || FormatOf(source) != format)
            throw new InvalidDataException("Выберите файл нужного формата.");
        string suggestedName = SuggestedMediaName(source);
        name = (name ?? suggestedName).Trim();
        ValidateName(name);
        var stage = MediaStage();
        var pending = Path.Combine(ShaderDirectory, stage);
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = File.Open(pending, FileMode.CreateNew, FileAccess.Write))
                await input.CopyToAsync(output);
            Refresh();
            var file = FileName(suggestedName, Occupied(), format == WallpaperFormat.Gif ? ".gif" : ".mp4");
            var item = new Wallpaper(Guid.NewGuid().ToString(), name, file, "");
            Commit(State with { Items = [.. State.Items, item] }, new(file, stage, true, Stamp(new FileInfo(pending))));
            return Get(item.Id);
        }
        finally
        {
            // An unresolved transaction still owns its staging file.
            if (!File.Exists(Manifest + ".journal")) File.Delete(pending);
        }
    }

    public Wallpaper RenameMedia(string id, string name)
    {
        name = name.Trim();
        ValidateName(name);
        var previous = Get(id);
        if (previous.Format == WallpaperFormat.Glsl) throw new InvalidDataException("Измените название GLSL-обоев в редакторе.");
        if (name == previous.Name) return previous;
        var item = previous with { Name = name };
        Commit(State with { Items = State.Items.Select(current => current.Id == id ? item : current).ToArray() });
        return item;
    }

    public void Remove(string id)
    {
        Get(id); Commit(State with { Items = State.Items.Where(x => x.Id != id).ToArray(), ActiveId = State.ActiveId == id ? null : State.ActiveId });
    }
    public void Activate(string? id)
    {
        Refresh(); if (id is not null) Get(id);
        if (State.ActiveId != id) Commit(State with { ActiveId = id });
    }
    public void Configure(PlaybackSettings settings) { Refresh(); Commit(State with { Settings = settings.Validate() }); }

    public static string MigrateDataDirectory(string appData)
    {
        var target = Path.Combine(appData, "MagmPaper"); var old = Path.Combine(appData, "magmpaper-engine");
        var source = Path.Combine(old, "wallpapers.json"); var manifest = Path.Combine(target, "wallpapers.json");
        if (!File.Exists(manifest) && File.Exists(source))
        {
            // Validate using the old directory before publishing the destination manifest.
            var root = JsonNode.Parse(File.ReadAllText(source, DataFiles.Utf8))!.AsObject();
            var version = root["version"]?.GetValue<int>();
            if (version is not (1 or 2) || root["items"] is not JsonArray list) throw new InvalidDataException("Повреждена старая коллекция; файл сохранён.");
            Parse(root, Path.Combine(old, "Wallpapers"));
            Directory.CreateDirectory(Path.Combine(target, "Wallpapers"));
            foreach (var item in list)
            {
                var name = item!["name"]!.GetValue<string>();
                if (version == 1) { Validate(name, item["code"]!.GetValue<string>()); continue; }
                var file = item["file"]!.GetValue<string>();
                if (FormatOf(file) != WallpaperFormat.Glsl)
                {
                    var media = MediaInfo(Path.Combine(old, "Wallpapers"), file) ?? throw new IOException($"В старой коллекции отсутствует {file}.");
                    var existingMedia = MediaInfo(Path.Combine(target, "Wallpapers"), file);
                    if (existingMedia is null) File.Copy(media.FullName, Path.Combine(target, "Wallpapers", file));
                    else
                    {
                        using var from = media.OpenRead();
                        using var to = existingMedia.OpenRead();
                        if (!System.Security.Cryptography.SHA256.HashData(from).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(to)))
                            throw new IOException($"Файл {file} уже существует в новой папке.");
                    }
                    continue;
                }
                var code = ReadCode(Path.Combine(old, "Wallpapers"), file) ?? throw new IOException($"В старой коллекции отсутствует {file}."); Validate(name, code);
                var existing = ReadCode(Path.Combine(target, "Wallpapers"), file);
                if (existing is not null && existing != code) throw new IOException($"Файл {file} уже существует в новой папке.");
                if (existing is null) File.Copy(Path.Combine(old, "Wallpapers", file), Path.Combine(target, "Wallpapers", file));
            }
            File.Copy(source, manifest);
        }
        return target;
    }
}
