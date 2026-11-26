using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MagmPaper.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlaybackSettings
{
    public bool MouseEnabled { get; init; } = true;
    public bool PauseWhenCovered { get; init; } = true;
    public bool PauseOnBattery { get; init; }
    public int Fps { get; init; } = 60;
    public double RenderScale { get; init; } = 1;
    public PlaybackSettings Validate()
    {
        if (Fps is not (0 or 15 or 30 or 60 or 120 or 144 or 165 or 240) || RenderScale is not (0.5 or 0.75 or 1))
            throw new InvalidDataException("Некорректные параметры производительности.");
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppearanceSettings
{
    public bool Enabled { get; init; }
    public int Height { get; init; } = 48;
    public int Icon { get; init; } = 24;
    public int Button { get; init; } = 44;
    public string Color { get; init; } = "#171923";
    public int Opacity { get; init; } = 80;
    public bool HideWidgets { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AnimateIcons { get; init; }
    public int Radius { get; init; }
    public bool Glass { get; init; }
    public string GlassColor { get; init; } = "#715CD4";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MatchWallpaperColors { get; init; }
    public bool UsesWallpaperColors() => Glass && MatchWallpaperColors;
    public AppearanceSettings WithWallpaperColors(WallpaperColors? palette) =>
        UsesWallpaperColors() && palette is not null ? this with { Color = palette.First, GlassColor = palette.Second } : this;
    public AppearanceSettings Validate()
    {
        if (Height is < 32 or > 96 || Icon is < 16 or > 64 || Button is < 28 or > 100 ||
            Icon + 8 > Height || Button < Icon || Opacity is < 0 or > 100 || Radius is < 0 or > 24 ||
            !ValidColor(Color) || !ValidColor(GlassColor))
            throw new InvalidDataException("Проверьте размеры, цвет и прозрачность панели. Значки должны помещаться в панель и кнопки.");
        return this;
    }
    private static bool ValidColor(string? value) => value is not null && Regex.IsMatch(value, @"\A#[0-9a-fA-F]{6}\z");
    public string Encode(int sequence)
    {
        Validate();
        return FormattableString.Invariant($"{sequence} {(Enabled ? 1 : 0)} {Height} {Icon} {Button} {Convert.ToInt32(Color[1..], 16)} {Opacity} {(HideWidgets ? 1 : 0)} {Radius} {(Glass ? 1 : 0)} {Convert.ToInt32(GlassColor[1..], 16)} {(AnimateIcons ? 1 : 0)}");
    }
}

public static class DataFiles
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static readonly System.Text.UTF8Encoding Utf8 = new(false, true);
    public static void Replace(string file, string? contents)
    {
        if (contents is null) { File.Delete(file); return; }
        File.WriteAllText(file + ".tmp", contents, Utf8);
        File.Move(file + ".tmp", file, true);
    }
    public static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(file, Utf8), Json)
        ?? throw new InvalidDataException($"Пустой файл настроек: {file}");
    public static void Write<T>(string file, T value) => Replace(file, JsonSerializer.Serialize(value, Json));
}

public sealed class MouseMotion(double smoothingMilliseconds)
{
    public double X { get; private set; } = .5;
    public double Y { get; private set; } = .5;
    private double targetX = .5, targetY = .5;
    public void Target(double x, double y, bool enabled)
    {
        if (!enabled) { X = Y = targetX = targetY = .5; return; }
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        targetX = Math.Clamp(x, 0, 1); targetY = Math.Clamp(y, 0, 1);
    }
    public void Advance(double elapsedMilliseconds)
    {
        var alpha = smoothingMilliseconds <= 0 ? 1 : 1 - Math.Exp(-Math.Max(0, elapsedMilliseconds) / smoothingMilliseconds);
        X += (targetX - X) * alpha; Y += (targetY - Y) * alpha;
    }
}

public readonly record struct Area(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Covers(Area target) => Left <= target.Left + 2 && Top <= target.Top + 2 && Right >= target.Right - 2 && Bottom >= target.Bottom - 2;
}
public static class PlaybackPolicy
{
    public static string? PauseReason(PlaybackSettings settings, bool locked, bool battery, bool covered) =>
        locked ? "Windows заблокирована" : settings.PauseOnBattery && battery ? "работа от батареи" :
        settings.PauseWhenCovered && covered ? "рабочий стол закрыт окном" : null;
}
