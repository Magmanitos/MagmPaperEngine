using MagmPaper.Core;
using System.Text.Json.Nodes;

const string Code = "precision mediump float; void main(){gl_FragColor=vec4(1.0);}";
var tests = new List<(string Name, Action Run)>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected a rejected operation"); }
void Test(string name, Action<Library, string> action) => tests.Add((name, () =>
{
    string path = Path.Combine(Path.GetTempPath(), "magm-core-" + Guid.NewGuid());
    try { action(new Library(path), path); } finally { Directory.Delete(path, true); }
}));

Test("CRUD, activation, immutable settings and reload", (library, path) =>
{
    var item = library.Save(null, "Мои обои", Code); library.Activate(item.Id);
    library.Configure(new() { Fps = 30, MouseEnabled = false, RenderScale = .75 });
    var reload = new Library(path); Check(reload.State.ActiveId == item.Id && reload.State.Settings.Fps == 30 && !reload.State.Settings.MouseEnabled, "preferences lost");
    var edited = reload.Save(item.Id, "Новое имя", Code + "\n"); Check(edited.Id == item.Id, "id changed");
    Check(!File.Exists(Path.Combine(library.ShaderDirectory, item.File)), "old filename remained");
    reload.Remove(item.Id); Check(new Library(path).State.Items.Count == 0 && reload.State.ActiveId is null, "delete active failed");
});
Test("Electron v1 JSON migrates to raw GLSL with original backups", (_, path) =>
{
    string manifest = Path.Combine(path, "wallpapers.json");
    DataFiles.Write(manifest, new { version = 1, items = new[] { new { id = "old", name = "Обои: тест", code = Code } }, activeId = "old", settings = new { fps = 15 } });
    var original = File.ReadAllText(manifest); var library = new Library(path);
    Check(library.State.Items[0].Id == "old" && library.State.ActiveId == "old" && library.State.Settings.Fps == 15, "migration lost data");
    Check(File.ReadAllText(manifest + ".v1-backup") == original && File.ReadAllText(manifest + ".electron-backup") == original, "backup changed");
    Check(File.ReadAllText(Path.Combine(library.ShaderDirectory, library.State.Items[0].File)) == Code, "shader file is not raw GLSL");
    Check(!File.ReadAllText(manifest).Contains("gl_FragColor"), "metadata contains code");
});
Test("safe filenames, collisions, path traversal and size bounds", (library, path) =>
{
    var a = library.Save(null, "CON", Code); var b = library.Save(null, "con", Code);
    Check(a.File.StartsWith('_') && b.File.Contains("(2)"), "reserved names/collision failed");
    Check(library.Save(null, "A/B:C?", Code).File == "A_B_C_.glsl", "invalid characters");
    Throws(() => Library.ReadCode(library.ShaderDirectory, "../outside.glsl"));
    Throws(() => library.Save(null, "empty", " ")); Throws(() => library.Save(null, "long", new string('a', 100001)));
    Throws(() => library.Configure(new() { Fps = 144 })); Check(library.State.Settings.Fps == 60, "invalid preferences persisted");
});
Test("external editing, adding, renaming and deleting files", (library, _) =>
{
    var item = library.Save(null, "External", Code); library.Activate(item.Id);
    File.WriteAllText(Path.Combine(library.ShaderDirectory, item.File), Code + " // edit");
    Check(library.Get(item.Id).Code.EndsWith("edit"), "external edit missed");
    File.Move(Path.Combine(library.ShaderDirectory, item.File), Path.Combine(library.ShaderDirectory, "Renamed.glsl"));
    library.Refresh(); Check(library.State.ActiveId is null && library.State.Items[0].Name == "Renamed", "external rename not reflected");
    File.Delete(Path.Combine(library.ShaderDirectory, "Renamed.glsl")); Check(library.Refresh().Items.Count == 0, "external delete missed");
});
Test("interrupted Electron journal rolls back without losing code", (library, path) =>
{
    var item = library.Save(null, "Journal", Code); string target = Path.Combine(library.ShaderDirectory, item.File);
    File.WriteAllText(target, "changed"); DataFiles.Write(library.Manifest + ".journal", new FileTransaction("uncommitted", [new(item.File, Code, "changed")]));
    var reload = new Library(path); Check(reload.Get(item.Id).Code == Code, "journal did not recover");
    File.WriteAllText(target, "external edit"); DataFiles.Write(library.Manifest + ".journal", new FileTransaction("uncommitted", [new(item.File, Code, "changed")]));
    Throws(() => new Library(path)); Check(File.ReadAllText(target) == "external edit" && File.Exists(library.Manifest + ".journal"), "conflict destroyed evidence");
});
Test("committed journal never rolls back a successful save", (library, path) =>
{
    var item = library.Save(null, "Committed", Code);
    DataFiles.Write(library.Manifest + ".journal", new FileTransaction(library.State.Revision!, [new(item.File, "old", Code)]));
    Check(new Library(path).Get(item.Id).Code == Code && !File.Exists(library.Manifest + ".journal"), "committed journal lost shader");
});
Test("corrupt metadata remains untouched", (library, path) =>
{
    library.Save(null, "Shader", Code); string source = File.ReadAllText(library.Manifest);
    File.WriteAllText(library.Manifest, "broken"); Throws(() => new Library(path)); Check(File.ReadAllText(library.Manifest) == "broken", "corruption overwritten");
    var root = JsonNode.Parse(source)!; root["items"]![0]!["file"] = "../outside.glsl"; File.WriteAllText(library.Manifest, root.ToJsonString());
    Throws(() => new Library(path));
});
Test("legacy directory migration preserves settings and refuses conflicts", (_, path) =>
{
    var old = new Library(Path.Combine(path, "magmpaper-engine")); var item = old.Save(null, "Legacy", Code); old.Activate(item.Id);
    string target = Library.MigrateDataDirectory(path); var migrated = new Library(target); Check(migrated.State.ActiveId == item.Id, "legacy activation lost");
    migrated.Save(item.Id, item.Name, Code + " // new"); Library.MigrateDataDirectory(path);
    Check(new Library(target).Get(item.Id).Code.EndsWith("new"), "repeat migration overwrote native data");
    Check(old.Get(item.Id).Code == Code, "legacy source modified");
});
tests.Add(("appearance wire protocol and motion are compatible", () =>
{
    var style = new AppearanceSettings { Enabled = true, Color = "#123456", Height = 64, Icon = 40, Button = 52, Opacity = 25, HideWidgets = true, Radius = 8 };
    Check(style.Encode(1) == "1 1 64 40 52 1193046 25 1 8", "native protocol changed");
    Throws(() => (style with { Color = "red" }).Validate()); Throws(() => (style with { Height = 32 }).Validate());
    var a = new MouseMotion(20); a.Target(1, 0, true); a.Advance(20);
    var b = new MouseMotion(20); b.Target(1, 0, true); b.Advance(10); b.Advance(10);
    Check(Math.Abs(a.X - b.X) < 1e-12 && a.X is > .5 and < 1, "smoothing depends on frame rate");
    a.Target(0, 0, false); Check(a.X == .5 && a.Y == .5, "disabled mouse not centered");
    Check(PlaybackPolicy.PauseReason(new(), false, false, false) is null, "visible desktop paused");
    Check(PlaybackPolicy.PauseReason(new(), true, false, false) is not null, "locked desktop running");
    Check(new Area(-1920, 0, 0, 1080).Covers(new(-1920, 0, 0, 1032)), "coverage geometry");
}));

int failed = 0;
foreach (var test in tests) try { test.Run(); Console.WriteLine("PASS " + test.Name); } catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + error); }
Console.WriteLine($"{tests.Count - failed}/{tests.Count} native core tests passed.");
return failed == 0 ? 0 : 1;
