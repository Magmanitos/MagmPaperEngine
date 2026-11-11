using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MagmPaper.Core;

public sealed record Wallpaper(string Id, string Name, string File, string Code);
public sealed record LibraryState(IReadOnlyList<Wallpaper> Items, string? ActiveId, PlaybackSettings Settings, string? Revision = null);
public sealed record FileChange(string File, string? Before, string? After);
public sealed record FileTransaction(string Revision, FileChange[] Changes);

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

    public static bool SafeFile(string? file) => file is not null && file.Length <= 180 && file.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase) &&
        !BadName.IsMatch(file) && !Reserved.IsMatch(file);
    public static string FileName(string name, HashSet<string> occupied)
    {
        var basis = BadName.Replace(name.Normalize(NormalizationForm.FormC), "_").TrimEnd('.', ' ').Trim();
        if (basis.Length == 0) basis = "Обои";
        if (Reserved.IsMatch(basis)) basis = "_" + basis;
        var file = basis + ".glsl";
        for (var i = 2; occupied.Contains(file); i++) file = $"{basis} ({i}).glsl";
        return file;
    }
    public static void Validate(string name, string code)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new InvalidDataException("Введите название от 1 до 80 символов.");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100000) throw new InvalidDataException("Вставьте GLSL-код, не более 100 000 символов.");
    }
    private HashSet<string> Occupied(string? except = null) => new(Directory.EnumerateFileSystemEntries(ShaderDirectory)
        .Select(Path.GetFileName).Where(x => x != except).Select(x => x!), StringComparer.OrdinalIgnoreCase);
    public static string? ReadCode(string directory, string file)
    {
        if (!SafeFile(file)) throw new InvalidDataException("Некорректное имя файла обоев.");
        var path = Path.Combine(directory, file);
        if (!File.Exists(path)) { if (Directory.Exists(path)) throw new IOException("Вместо GLSL-файла найдена папка."); return null; }
        var info = new FileInfo(path);
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Length > 400000)
            throw new IOException($"Недоступен файл обоев: {file}");
        return File.ReadAllText(path, DataFiles.Utf8);
    }
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
            items.Add(new(id, name, file, version == 1 ? code : ReadCode(shaderDirectory, file) ?? ""));
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
        if (transaction.Revision != revision)
            foreach (var change in transaction.Changes)
            {
                var current = ReadCode(ShaderDirectory, change.File);
                if (current == change.Before) continue;
                if (current != change.After) throw new IOException($"Файл {change.File} изменён другим редактором. Файл и журнал сохранены: {journal}");
                DataFiles.Replace(Path.Combine(ShaderDirectory, change.File), change.Before);
            }
        File.Delete(journal);
    }
    private void Commit(LibraryState next)
    {
        Recover(); next.Settings.Validate();
        var changes = new List<FileChange>();
        foreach (var item in next.Items)
        {
            Validate(item.Name, item.Code);
            var before = ReadCode(ShaderDirectory, item.File);
            if (before != item.Code) changes.Add(new(item.File, before, item.Code));
        }
        foreach (var item in State.Items.Where(x => !next.Items.Any(n => n.File == x.File)))
        {
            var before = ReadCode(ShaderDirectory, item.File);
            if (before is not null) changes.Add(new(item.File, before, null));
        }
        var revision = Guid.NewGuid().ToString();
        File.WriteAllText(Manifest + ".tmp", Serialize(next, revision), DataFiles.Utf8);
        DataFiles.Write(Manifest + ".journal", new FileTransaction(revision, changes.ToArray()));
        try
        {
            foreach (var change in changes)
            {
                if (ReadCode(ShaderDirectory, change.File) != change.Before) throw new IOException($"Файл {change.File} изменён другим редактором. Обновите коллекцию.");
                DataFiles.Replace(Path.Combine(ShaderDirectory, change.File), change.After);
            }
            File.Move(Manifest + ".tmp", Manifest, true);
        }
        catch { Recover(); throw; }
        State = next with { Revision = revision };
        try { File.Delete(Manifest + ".journal"); } catch (IOException) { }
    }
    public LibraryState Refresh()
    {
        Recover(); var items = new List<Wallpaper>(); var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in State.Items)
        {
            var code = ReadCode(ShaderDirectory, item.File);
            if (code is null) continue;
            Validate(item.Name, code); items.Add(item with { Code = code }); known.Add(item.File);
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(ShaderDirectory).OrderBy(x => x, StringComparer.Ordinal))
        {
            var file = Path.GetFileName(path);
            if (!file.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase) || known.Contains(file)) continue;
            var code = ReadCode(ShaderDirectory, file) ?? throw new IOException($"Файл исчез: {file}");
            var name = file[..^5]; if (name.Length > 80) name = name[..80]; Validate(name, code);
            items.Add(new(Guid.NewGuid().ToString(), name, file, code)); known.Add(file);
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
        var file = previous?.Name == name ? previous.File : FileName(name, Occupied(previous?.File));
        if (string.Equals(previous?.File, file, StringComparison.OrdinalIgnoreCase)) file = previous!.File;
        var item = new Wallpaper(previous?.Id ?? Guid.NewGuid().ToString(), name, file, code);
        var items = State.Items.ToList();
        if (previous is null) items.Add(item); else items[items.FindIndex(x => x.Id == id)] = item;
        Commit(State with { Items = items }); return item;
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
