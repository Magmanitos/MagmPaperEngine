using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MagmPaper.Windows;

internal sealed class WindowsWindowPalette(string directory)
{
    private const string ColorsKey = @"Control Panel\Colors";
    private const uint GetHighContrast = 0x0042, SetHighContrast = 0x0043, HighContrastOn = 1;
    private readonly string backupPath = Path.Combine(directory, "windows-window-palette.json");
    private static readonly (int Index, string Name)[] Colors =
    [
        (4, "Menu"), (5, "Window"), (7, "MenuText"), (8, "WindowText"), (9, "TitleText"),
        (12, "AppWorkspace"), (13, "Hilight"), (14, "HilightText"), (15, "ButtonFace"),
        (17, "GrayText"), (18, "ButtonText"), (19, "InactiveTitleText"), (23, "InfoText"),
        (24, "InfoWindow"), (26, "HotTrackingColor")
    ];
    private static readonly int[] Indices = Colors.Select(color => color.Index).ToArray();

    private sealed record Backup(bool Contrast, int[] RuntimeColors, string?[] RegistryColors, int[] LastColors);
    internal readonly record struct Palette(int Background, int Text, int Accent, int AccentText);

    internal bool IsApplied()
    {
        var backup = Load();
        return backup is not null && ContrastEnabled() && GetSysColor(5) == backup.LastColors[1]
            && GetSysColor(8) == backup.LastColors[3] && GetSysColor(13) == backup.LastColors[6];
    }

    internal bool HasBackup => File.Exists(backupPath);

    internal void Apply(WallpaperColors colors)
    {
        var palette = Preview(colors);
        var desired = PaletteColors(palette);
        var previous = Load();
        bool enabled = ContrastEnabled();
        if (previous is not null && !IsApplied())
        {
            Restore();
            previous = null;
        }
        if (previous is not null && previous.LastColors.AsSpan().SequenceEqual(desired)) return;
        using var key = Registry.CurrentUser.CreateSubKey(ColorsKey);
        var runtime = Indices.Select(GetSysColor).ToArray();
        var persisted = Colors.Select(color => key.GetValue(color.Name) as string).ToArray();
        var original = previous ?? new Backup(enabled, runtime, persisted, desired);
        var next = original with { LastColors = desired };
        DataFiles.Write(backupPath, next);
        try
        {
            if (!enabled) ToggleContrast(true);
            SetColors(desired);
            for (int index = 0; index < Colors.Length; index++)
                key.SetValue(Colors[index].Name, Rgb(desired[index]), RegistryValueKind.String);
            if (!IsApplied()) throw new IOException("Windows не подтвердила цвета фона и текста.");
        }
        catch
        {
            try
            {
                if (ContrastEnabled() != enabled) ToggleContrast(enabled);
                for (int index = 0; index < Colors.Length; index++)
                    Put(key, Colors[index].Name, persisted[index]);
                SetColors(runtime);
                if (previous is null) File.Delete(backupPath);
                else DataFiles.Write(backupPath, previous);
            }
            catch (Exception rollback) when (rollback is Win32Exception or IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal void Restore()
    {
        var backup = Load();
        if (backup is null) return;
        bool ours = IsApplied();
        using var key = Registry.CurrentUser.CreateSubKey(ColorsKey);
        for (int index = 0; index < Colors.Length; index++)
        {
            var name = Colors[index].Name;
            if (Equals(key.GetValue(name), Rgb(backup.LastColors[index])))
                Put(key, name, backup.RegistryColors[index]);
        }
        if (ours)
        {
            if (ContrastEnabled() != backup.Contrast) ToggleContrast(backup.Contrast);
            SetColors(backup.RuntimeColors);
        }
        File.Delete(backupPath);
    }

    private Backup? Load()
    {
        if (!File.Exists(backupPath)) return null;
        var saved = DataFiles.Read<Backup>(backupPath);
        if (saved.RuntimeColors is null || saved.RuntimeColors.Length != Colors.Length ||
            saved.RegistryColors is null || saved.RegistryColors.Length != Colors.Length ||
            saved.LastColors is null || saved.LastColors.Length != Colors.Length ||
            saved.RuntimeColors.Concat(saved.LastColors).Any(color => color < 0 || color > 0xffffff))
            throw new InvalidDataException("Резервная копия цветов Windows повреждена. Исходный файл сохранён.");
        return saved;
    }

    internal static Palette Preview(WallpaperColors colors)
    {
        var background = FromHex(colors.First);
        var accent = FromHex(colors.Second);
        // Keep small links and focus outlines legible even if both wallpaper samples are close in color.
        int text = TextOn(background);
        if (Ratio(background, accent) < 4.5)
        {
            for (int step = 1; step <= 10; step++)
            {
                var mixed = Mix(accent, text, step / 10.0);
                if (Ratio(background, mixed) >= 4.5) { accent = mixed; break; }
            }
        }
        return new(background, text, accent, TextOn(accent));
    }

    internal static string Hex(int color) => $"#{color & 255:X2}{(color >> 8) & 255:X2}{(color >> 16) & 255:X2}";
    private static int FromHex(string color)
    {
        if (color.Length != 7 || color[0] != '#' || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            throw new ArgumentException("Выберите цвет в формате #RRGGBB.", nameof(color));
        return (rgb >> 16) | (rgb & 0xff00) | ((rgb & 255) << 16);
    }
    private static string Rgb(int color) => FormattableString.Invariant($"{color & 255} {(color >> 8) & 255} {(color >> 16) & 255}");
    private static int TextOn(int color) => Ratio(color, 0xffffff) >= Ratio(color, 0) ? 0xffffff : 0;
    private static int Mix(int a, int b, double fraction)
    {
        int Channel(int shift) => (int)Math.Round(((a >> shift) & 255) * (1 - fraction) + ((b >> shift) & 255) * fraction);
        return Channel(0) | Channel(8) << 8 | Channel(16) << 16;
    }

    private static double Ratio(int a, int b)
    {
        static double Value(int color)
        {
            static double Linear(int channel)
            {
                double s = channel / 255.0;
                return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
            }
            return .2126 * Linear(color & 255) + .7152 * Linear((color >> 8) & 255) + .0722 * Linear((color >> 16) & 255);
        }
        var first = Value(a);
        var second = Value(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static int[] PaletteColors(Palette colors)
    {
        var (bg, text, accent, accentText) = colors;
        return [bg, bg, text, text, text, bg, accent, accentText, bg, Mix(bg, text, .58), text, text, text, bg, accent];
    }

    private static void Put(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void SetColors(int[] colors)
    {
        if (!SetSysColors(Indices.Length, Indices, colors)) throw new Win32Exception();
    }

    internal static bool ContrastEnabled() => (ReadContrast().Flags & HighContrastOn) != 0;
    internal static void UseStandardTheme()
    {
        if (ContrastEnabled()) ToggleContrast(false);
    }

    private static HighContrast ReadContrast()
    {
        var data = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        if (!SystemParametersInfo(GetHighContrast, data.Size, ref data, 0)) throw new Win32Exception();
        return data;
    }

    private static void ToggleContrast(bool enabled)
    {
        var data = ReadContrast();
        data.Flags = enabled ? data.Flags | HighContrastOn : data.Flags & ~HighContrastOn;
        if (!SystemParametersInfo(SetHighContrast, data.Size, ref data, 3)) throw new Win32Exception();
        if (ContrastEnabled() != enabled) throw new IOException("Windows не подтвердила смену контрастной темы.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size, Flags;
        public nint Scheme;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint size, ref HighContrast value, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSysColors(int count, int[] elements, int[] colors);
    [DllImport("user32.dll")]
    internal static extern int GetSysColor(int index);
}
