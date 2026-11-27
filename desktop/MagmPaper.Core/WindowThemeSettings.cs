namespace MagmPaper.Core;

public enum WindowMaterial { Solid, Gradient, Glass }
public enum WindowOpeningMotion { Off, Fade, Rise, Scale }
public enum WindowCorner { System, Square, Round, Small }
public enum WindowsWindowBackdrop { System, Mica = 2, Acrylic = 3 }

public sealed record WindowThemeSettings
{
    public const string DefaultFirst = "#52446B", DefaultSecond = "#3A6891";
    public bool Enabled { get; init; }
    public WindowMaterial Material { get; init; } = WindowMaterial.Glass;
    public string First { get; init; } = DefaultFirst;
    public string Second { get; init; } = DefaultSecond;
    public bool MatchWallpaper { get; init; }
    public int Radius { get; init; } = 12;
    public int Opacity { get; init; } = 100;
    public bool GlassOutline { get; init; } = true;
    public bool StyleWindowsFrames { get; init; }
    public WindowCorner WindowsCorners { get; init; }
    public WindowsWindowBackdrop WindowsBackdrop { get; init; }
    public WindowOpeningMotion Motion { get; init; }
    public int Duration { get; init; } = 160;

    public WindowThemeSettings Normalize() => this with
    {
        Material = Enum.IsDefined(Material) ? Material : WindowMaterial.Glass,
        First = IsColor(First) ? First : DefaultFirst,
        Second = IsColor(Second) ? Second : DefaultSecond,
        Radius = Math.Clamp(Radius, 0, 24),
        Opacity = Math.Clamp(60, Opacity, 100),
        WindowsCorners = Enum.IsDefined(WindowsCorners) ? WindowsCorners : WindowCorner.System,
        WindowsBackdrop = Enum.IsDefined(WindowsBackdrop) ? WindowsBackdrop : WindowsWindowBackdrop.System,
        Motion = Enum.IsDefined(Motion) ? Motion : WindowOpeningMotion.Off,
        Duration = Math.Clamp(Duration, 90, 330)
    };

    public (string First, string Second) Colors(WallpaperColors? wallpaper) =>
        MatchWallpaper && wallpaper is not null ? (wallpaper.First, wallpaper.Second) : (First, Second);

    private static bool IsColor(string? color)
    {
        if (color is not { Length: 7 } || color[0] != '#') return false;
        foreach (char digit in color.AsSpan(1))
            if (!char.IsAsciiHexDigit(digit)) return false;
        return true;
    }
}
