using Microsoft.Win32;

namespace MagmPaper.Windows;

internal sealed record WindowsThemeState(bool AppsLight, bool SystemLight, bool Transparency, bool WindowAccent,
    bool ShellAccent, string Accent);

internal sealed class WindowsThemeSettings(RegistryKey? workspace = null)
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Dwm = @"Software\Microsoft\Windows\DWM";
    private const string ExplorerAccent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string Desktop = @"Control Panel\Desktop";
    private readonly RegistryKey profile = workspace ?? Registry.CurrentUser;

    internal enum Option { AppsLight, SystemLight, Transparency, WindowAccent, ShellAccent }

    internal WindowsThemeState Read()
    {
        using var personalize = profile.OpenSubKey(Personalize);
        using var dwm = profile.OpenSubKey(Dwm);
        using var accent = profile.OpenSubKey(ExplorerAccent);
        var palette = accent?.GetValue("AccentPalette") as byte[];
        string color = palette is { Length: >= 16 }
            ? "#" + Convert.ToHexString(palette.AsSpan(12, 3))
            : dwm?.GetValue("ColorizationColor") is int value ? $"#{value & 0xffffff:X6}" : "#0078D4";
        return new(Flag(personalize, "AppsUseLightTheme", true), Flag(personalize, "SystemUsesLightTheme", true),
            Flag(personalize, "EnableTransparency", true), Flag(dwm, "ColorPrevalence", false),
            Flag(personalize, "ColorPrevalence", false), color);
    }

    internal void Set(Option option, bool value)
    {
        int enabled = value ? 1 : 0;
        var change = option switch
        {
            Option.AppsLight => new Change(Personalize, "AppsUseLightTheme", enabled),
            Option.SystemLight => new Change(Personalize, "SystemUsesLightTheme", enabled),
            Option.Transparency => new Change(Personalize, "EnableTransparency", enabled),
            Option.WindowAccent => new Change(Dwm, "ColorPrevalence", enabled),
            Option.ShellAccent => new Change(Personalize, "ColorPrevalence", enabled),
            _ => throw new ArgumentOutOfRangeException(nameof(option))
        };
        if (Apply(change) && workspace is null) Notify();
    }

    internal void SetAccent(string hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#' || !hex.Skip(1).All(char.IsAsciiHexDigit))
            throw new ArgumentException("Выберите цвет в формате #RRGGBB.", nameof(hex));

        var rgb = Convert.FromHexString(hex[1..]);
        using var key = profile.OpenSubKey(ExplorerAccent);
        var oldPalette = key?.GetValue("AccentPalette") as byte[];
        var palette = BuildPalette(rgb, oldPalette);
        int argb = unchecked((int)0xff000000) | rgb[0] << 16 | rgb[1] << 8 | rgb[2];
        int abgr = Pack(palette.AsSpan(12, 3));
        bool changed = Apply(new(Desktop, "AutoColorization", 0),
            new(ExplorerAccent, "AccentPalette", palette, RegistryValueKind.Binary),
            new(ExplorerAccent, "AccentColorMenu", abgr),
            new(ExplorerAccent, "StartColorMenu", Pack(palette.AsSpan(16, 3))),
            new(Dwm, "AccentColor", abgr), new(Dwm, "ColorizationColor", argb));
        if (!changed || workspace is not null) return;
        Notify();
        const uint DwmColorizationChanged = 0x0320;
        _ = Win32.SendMessageTimeout(0xffff, DwmColorizationChanged, unchecked((uint)argb), 0, 2, 200, out _);
    }

    private static int Pack(ReadOnlySpan<byte> rgb) => unchecked((int)0xff000000) | rgb[2] << 16 | rgb[1] << 8 | rgb[0];

    private static byte[] BuildPalette(byte[] rgb, byte[]? previous)
    {
        // Explorer stores AccentLight3 … AccentDark3 as RGB plus one reserved byte.
        double[] shades = [.66, .46, .24, 0, -.2, -.4, -.58];
        byte[] palette = previous is { Length: >= 32 } ? (byte[])previous.Clone() : new byte[32];
        for (int index = 0; index < shades.Length; index++)
        {
            double shade = shades[index];
            for (int channel = 0; channel < rgb.Length; channel++)
            {
                int edge = shade >= 0 ? 255 : 0;
                palette[index * 4 + channel] = (byte)Math.Round(rgb[channel] + (edge - rgb[channel]) * Math.Abs(shade));
            }
        }
        return palette;
    }

    private static bool Flag(RegistryKey? key, string name, bool fallback) =>
        key?.GetValue(name) is int number ? number != 0 : fallback;

    private sealed record Change(string Path, string Name, object Value, RegistryValueKind Kind = RegistryValueKind.DWord);
    private sealed record Original(Change Change, object? Value, RegistryValueKind Kind);

    private bool Apply(params Change[] changes)
    {
        var applied = new List<Original>(changes.Length);
        try
        {
            foreach (var change in changes)
            {
                using var key = profile.CreateSubKey(change.Path, writable: true);
                var old = key.GetValue(change.Name);
                if (SameValue(old, change.Value)) continue;
                var kind = old is null ? RegistryValueKind.None : key.GetValueKind(change.Name);
                key.SetValue(change.Name, change.Value, change.Kind);
                applied.Add(new(change, old, kind));
                if (!SameValue(key.GetValue(change.Name), change.Value))
                    throw new IOException("Windows не подтвердила изменение темы.");
            }
            return applied.Count != 0;
        }
        catch
        {
            for (int index = applied.Count - 1; index >= 0; index--)
            {
                var old = applied[index];
                try
                {
                    using var key = profile.OpenSubKey(old.Change.Path, writable: true);
                    if (key is null || !SameValue(key.GetValue(old.Change.Name), old.Change.Value)) continue;
                    if (old.Value is null) key.DeleteValue(old.Change.Name, false);
                    else key.SetValue(old.Change.Name, old.Value, old.Kind);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
            throw;
        }
    }

    private static bool SameValue(object? first, object? second) =>
        first is byte[] left && second is byte[] right ? left.AsSpan().SequenceEqual(right) : Equals(first, second);

    private static void Notify() =>
        _ = Win32.Broadcast(0xffff, 0x001a, 0, "ImmersiveColorSet", 2, 200, out _);
}
