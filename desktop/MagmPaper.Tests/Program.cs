using System.Buffers.Binary;
using System.Text.Json;
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

Test("GIF and MP4 join the same collection and preserve imported source files", (library, path) =>
{
    string animated = Path.Combine(path, "Wave.GIF");
    string video = Path.Combine(path, "Wave.mp4");
    byte[] original = "GIF89a-sample"u8.ToArray();
    File.WriteAllBytes(animated, original);
    File.WriteAllBytes(video, "test binary video"u8.ToArray());
    var gif = library.ImportMediaAsync(animated, WallpaperFormat.Gif).GetAwaiter().GetResult();
    var duplicate = library.ImportMediaAsync(animated, WallpaperFormat.Gif).GetAwaiter().GetResult();
    var mp4 = library.ImportMediaAsync(video, WallpaperFormat.Mp4, "  Морская волна  ").GetAwaiter().GetResult();
    var glsl = library.Save(null, "Wave", Code);
    Check(gif.File == "Wave.gif" && duplicate.File == "Wave (2).gif" && mp4.File == "Wave.mp4", "media extension or collision handling failed");
    library.Activate(mp4.Id);
    Check(mp4.Name == "Морская волна" && gif.Name == "Wave", "imported display name or file name suggestion was lost");
    var renamed = library.RenameMedia(mp4.Id, "  Прибой  ");
    Check(renamed == mp4 with { Name = "Прибой" }, "renaming changed the imported file, stamp, or ID");
    var reloaded = new Library(path);
    Check(reloaded.State.Items.Count == 4 && reloaded.State.ActiveId == mp4.Id &&
        reloaded.Get(mp4.Id) == renamed && reloaded.Get(glsl.Id).Code == Code, "mixed collection or renamed active video did not reload");
    Check(File.ReadAllBytes(reloaded.MediaPath(renamed)).SequenceEqual(File.ReadAllBytes(video)), "renaming changed the media bytes");
    Throws(() => reloaded.Save(gif.Id, gif.Name, Code));
    Throws(() => reloaded.RenameMedia(glsl.Id, "Без редактора"));
    Throws(() => reloaded.RenameMedia(gif.Id, "  "));
    Throws(() => reloaded.ImportMediaAsync(animated, WallpaperFormat.Gif, "  ").GetAwaiter().GetResult());
    Throws(() => reloaded.ImportMediaAsync(video, WallpaperFormat.Gif).GetAwaiter().GetResult());
    reloaded.Remove(mp4.Id);
    reloaded.Remove(gif.Id);
    Check(!File.Exists(Path.Combine(library.ShaderDirectory, mp4.File)) && File.Exists(video) && reloaded.State.ActiveId is null,
        "removing media affected the source or left an active choice");
    Check(File.ReadAllBytes(animated).SequenceEqual(original), "original GIF was changed");
});

Test("aborted binary wallpaper deletion restores the file", (library, path) =>
{
    string source = Path.Combine(path, "clip.mp4");
    File.WriteAllBytes(source, "test binary video"u8.ToArray());
    var item = library.ImportMediaAsync(source, WallpaperFormat.Mp4).GetAwaiter().GetResult();
    string stage = ".media-" + Guid.NewGuid().ToString("N") + ".tmp";
    File.Move(library.MediaPath(item), Path.Combine(library.ShaderDirectory, stage));
    DataFiles.Write(library.Manifest + ".journal", new FileTransaction("uncommitted", [], [new(item.File, stage, false)]));
    var reloaded = new Library(path);
    Check(reloaded.Get(item.Id).Format == WallpaperFormat.Mp4 &&
        File.ReadAllBytes(reloaded.MediaPath(item)).SequenceEqual(File.ReadAllBytes(source)), "binary rollback lost media bytes");
});

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
    Throws(() => library.Configure(new() { Fps = 999 })); Check(library.State.Settings.Fps == 60, "invalid preferences persisted");
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
    Check(style.Encode(1) == "1 1 64 40 52 1193046 25 1 8 0 7429332", "native protocol defaults changed");
    Throws(() => (style with { Color = "red" }).Validate()); Throws(() => (style with { Height = 32 }).Validate());
    var a = new MouseMotion(20); a.Target(1, 0, true); a.Advance(20);
    var b = new MouseMotion(20); b.Target(1, 0, true); b.Advance(10); b.Advance(10);
    Check(Math.Abs(a.X - b.X) < 1e-12 && a.X is > .5 and < 1, "smoothing depends on frame rate");
    a.Target(0, 0, false); Check(a.X == .5 && a.Y == .5, "disabled mouse not centered");
    Check(PlaybackPolicy.PauseReason(new(), false, false, false) is null, "visible desktop paused");
    Check(PlaybackPolicy.PauseReason(new(), true, false, false) is not null, "locked desktop running");
    Check(new Area(-1920, 0, 0, 1080).Covers(new(-1920, 0, 0, 1032)), "coverage geometry");
}));

tests.Add(("glass settings preserve legacy data and encode a second tint", () =>
{
    var old = JsonSerializer.Deserialize<AppearanceSettings>("""{"enabled":true,"color":"#123456","opacity":45}""", DataFiles.Json)!;
    Check(old.Validate().Enabled && !old.Glass && old.Color == "#123456" && old.Opacity == 45, "legacy appearance changed");
    var glass = old with { Glass = true, GlassColor = "#ABCDEF" };
    Check(glass.Encode(2).EndsWith(" 1 11259375", StringComparison.Ordinal), "second tint missing from the wire protocol");
    Throws(() => (glass with { GlassColor = "#GGCCAA" }).Validate());
}));

tests.Add(("release notes snap to a nearby edge and respect the work area", () =>
{
    var work = new Area(-1920, 0, 0, 1080);
    var owner = new Area(-1500, 300, -500, 800);
    NotesDock? Find(Area window) => NotesDock.Find(owner, window, work, 10, 27, 52);
    var nearRight = new Area(-476, 366, -196, 566);
    var nearLeft = new Area(-1798, 350, -1518, 550);
    var nearTop = new Area(-1300, 82, -1020, 282);
    var nearBottom = new Area(-1300, 822, -1020, 1022);
    Check(Find(nearRight) == new NotesDock(NotesEdge.Right, 66), "right edge not selected");
    Check(Find(nearLeft) == new NotesDock(NotesEdge.Left, 50), "left edge not selected");
    Check(Find(nearTop) == new NotesDock(NotesEdge.Top, 200), "top edge not selected");
    Check(Find(nearBottom) == new NotesDock(NotesEdge.Bottom, 200), "bottom edge not selected");
    Check(Find(new(-330, 366, -50, 566)) is null, "notes snapped outside the magnetic zone");
    Check(Find(new(-495, 17, -215, 217)) is null, "notes snapped without an overlapping edge");
    Check(NotesDock.Find(owner, nearRight, work with { Right = -250 }, 10, 27, 52) is null,
        "notes snapped outside the work area");
    var moved = new Area(-1400, 400, -400, 900);
    Check(Find(nearRight)!.Value.Place(moved, nearRight, work, 10) == new Area(-390, 466, -110, 666),
        "docked notes lost their offset when the main window moved");
}));

tests.Add(("taskbar palette follows the two sides of a wallpaper", () =>
{
    byte[] rgba =
    [
        180, 41, 55, 255, 174, 38, 52, 255, 14, 180, 43, 255,
        38, 92, 201, 255, 42, 88, 207, 255, 252, 224, 106, 255
    ];
    var colors = WallpaperColors.FromRgba(rgba, 6, 1);
    Check(colors == new WallpaperColors("#B12735", "#285ACC"), "wrong dominant wallpaper colors");
    var manual = new AppearanceSettings { Glass = true, Color = "#123456", GlassColor = "#ABCDEF" };
    var automatic = manual with { MatchWallpaperColors = true };
    var resolved = automatic.WithWallpaperColors(colors);
    Check(resolved.Color == colors.First && resolved.GlassColor == colors.Second, "automatic hue was not applied");
    Check(automatic.Color == manual.Color && automatic.WithWallpaperColors(null) == automatic, "manual fallback colors changed");
    Check(manual.WithWallpaperColors(colors) == manual, "manual mode used the wallpaper");
    Throws(() => WallpaperColors.FromRgba(rgba, 2, 2));
}));

tests.Add(("cursor packs match Windows states and PNG conversion preserves the hotspot", () =>
{
    var names = new[] { "Pointer.cur", "Work.ani", "Dgn1.cur", "Diagonal_2.CUR", "Text.cur", "Zoom-out.cur", "uninstall.bat" };
    var match = CursorFiles.Match(names, name => name);
    Check(match.Count == 6 && match["Arrow"] == "Pointer.cur" && match["AppStarting"] == "Work.ani" &&
        match["SizeNWSE"] == "Dgn1.cur" && match["SizeNESW"] == "Diagonal_2.CUR" && match["Zoom-out"] == "Zoom-out.cur",
        "cursor roles were not recognized");
    byte[] pixels = [0, 0, 255, 255, 0, 0, 0, 0, 255, 0, 0, 128, 0, 255, 0, 255];
    byte[] cursor = CursorFiles.FromBgra(2, 2, 1, 0, pixels);
    Check(CursorFiles.HasCursorHeader(".CUR", cursor) && !CursorFiles.HasCursorHeader(".ani", cursor), "wrong Windows cursor signature");
    Check(BinaryPrimitives.ReadUInt16LittleEndian(cursor.AsSpan(10)) == 1 &&
        BinaryPrimitives.ReadUInt16LittleEndian(cursor.AsSpan(12)) == 0, "wrong hotspot coordinates");
    Check(cursor.AsSpan(62, 8).SequenceEqual(pixels.AsSpan(8, 8)) && cursor[82] == 0x40,
        "cursor rows or the transparency mask were not encoded");
    Throws(() => CursorFiles.FromBgra(2, 2, 2, 0, pixels));
    Throws(() => CursorFiles.PngSize("not a PNG"u8));
}));

tests.Add(("scroll motion preserves velocity and trajectory across display rates", () =>
{
    var sixty = new ScrollMotion(0); var fast = new ScrollMotion(0);
    sixty.MoveTo(600); fast.MoveTo(600);
    for (int i = 0; i < 15; i++) sixty.Advance(1.0 / 60);
    for (int i = 0; i < 36; i++) fast.Advance(1.0 / 144);
    Check(Math.Abs(sixty.Position - fast.Position) < 1e-8 && Math.Abs(sixty.Velocity - fast.Velocity) < 1e-8, "scroll trajectory depends on refresh rate");
    double position = fast.Position, velocity = fast.Velocity;
    fast.MoveTo(900);
    Check(fast.Position == position && fast.Velocity == velocity, "another wheel event restarts the animation");
    for (int i = 0; i < 144; i++) { fast.Advance(1.0 / 144); Check(fast.Position >= position && fast.Position <= 900, "scroll jumps backwards or overshoots"); position = fast.Position; }
    Check(fast.Settled, "scroll never stops at rest");
    fast.MoveTo(0); for (int i = 0; i < 144; i++) fast.Advance(1.0 / 144);
    Check(fast.Settled && Math.Abs(fast.Position) < .1, "reverse scrolling does not settle");
    fast.Reset(120); fast.Advance(1); Check(fast.Position == 120 && fast.Velocity == 0, "cancelled motion keeps moving");
}));

tests.Add(("GLSL extension compatibility preserves conditional semantics and source lines", () =>
{
    string source = "#version 100\nprecision mediump float;\n#extension GL_OES_standard_derivatives : enable /* notes\ncontinued */\nvoid main(){gl_FragColor=vec4(1.0);}";
    string prepared = ShaderSource.Prepare(source);
    Check(prepared.StartsWith("#version 100\n#extension GL_OES_standard_derivatives : enable\n"), "late extension was not moved into the preamble");
    Check(prepared.Contains("#line 1\n") && prepared.Contains("/* notes\ncontinued */"), "source line map or multiline comment lost");
    var guarded = ShaderSource.Prepare("precision mediump float;\n#ifdef GL_OES_standard_derivatives\n#extension GL_OES_standard_derivatives : enable\n#endif\nvoid main(){}");
    Check(guarded.StartsWith("#ifdef GL_OES_standard_derivatives\n#extension"), "built-in extension guard was lost");
    string custom = "#define CUSTOM 1\n#if CUSTOM\n#extension GL_OES_standard_derivatives : enable\n#endif\nvoid main(){}";
    Check(ShaderSource.Prepare(custom) == custom, "user-defined preprocessor state was reordered");
    string commented = "/*\n#extension GL_fake : require\n*/\n" + Code;
    Check(ShaderSource.Prepare(commented) == commented, "comment was treated as an extension");
    foreach (int rate in new[] { 0, 15, 30, 60, 120, 144, 165, 240 }) new PlaybackSettings { Fps = rate }.Validate();
    Throws(() => new PlaybackSettings { Fps = -1 }.Validate());
}));

int failed = 0;
foreach (var test in tests) try { test.Run(); Console.WriteLine("PASS " + test.Name); } catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + error); }
Console.WriteLine($"{tests.Count - failed}/{tests.Count} native core tests passed.");
return failed == 0 ? 0 : 1;
