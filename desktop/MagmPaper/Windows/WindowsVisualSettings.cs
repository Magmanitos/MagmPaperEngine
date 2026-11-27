using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MagmPaper.Windows;

internal static class WindowsVisualSettings
{
    private const uint GetClientAreaAnimation = 0x1042, SetClientAreaAnimation = 0x1043;
    private const uint GetMinimize = 0x0048, SetMinimize = 0x0049;
    private const uint GetMenu = 0x1002, SetMenu = 0x1003, GetFade = 0x1012, SetFade = 0x1013;
    private const uint UpdateProfile = 1, BroadcastChange = 2;
    private const uint GetWallpaper = 0x0073;

    internal enum MenuMotion { Off, Slide, Fade }
    internal readonly record struct Animations(bool ClientArea, bool Minimize, bool MenuEnabled, bool MenuFade)
    {
        internal MenuMotion Motion => MenuEnabled ? (MenuFade ? MenuMotion.Fade : MenuMotion.Slide) : MenuMotion.Off;
    }

    internal static Animations ReadAnimations() =>
        new(ReadFlag(GetClientAreaAnimation), ReadMinimize(), ReadFlag(GetMenu), ReadFlag(GetFade));

    internal static void SetClientAnimations(bool enabled) =>
        ChangeFlag(GetClientAreaAnimation, SetClientAreaAnimation, enabled);

    internal static void SetMinimizeAnimations(bool enabled)
    {
        if (ReadMinimize() == enabled) return;
        var data = new AnimationInfo { Size = 8, Minimize = enabled ? 1 : 0 };
        if (!Animation(SetMinimize, data.Size, ref data, UpdateProfile | BroadcastChange)) throw new Win32Exception();
        if (ReadMinimize() != enabled) throw new IOException("Windows не подтвердила анимацию сворачивания.");
    }

    internal static void SetMenuMotion(MenuMotion motion) =>
        SetMenuAnimations(motion != MenuMotion.Off, motion == MenuMotion.Off ? ReadFlag(GetFade) : motion == MenuMotion.Fade);

    internal static void SetMenuAnimations(bool enabled, bool fade)
    {
        bool previousFade = ReadFlag(GetFade);
        try
        {
            ChangeFlag(GetFade, SetFade, fade);
            ChangeFlag(GetMenu, SetMenu, enabled);
        }
        catch
        {
            try { ChangeFlag(GetFade, SetFade, previousFade); }
            catch (Exception error) when (error is Win32Exception or IOException) { }
            throw;
        }
    }

    private static bool ReadFlag(uint action)
    {
        if (!Read(action, 0, out int enabled, 0)) throw new Win32Exception();
        return enabled != 0;
    }

    private static void ChangeFlag(uint read, uint write, bool enabled)
    {
        if (ReadFlag(read) == enabled) return;
        if (!Write(write, 0, enabled ? 1 : 0, UpdateProfile | BroadcastChange)) throw new Win32Exception();
        if (ReadFlag(read) != enabled) throw new IOException("Windows не подтвердила изменение анимации.");
    }

    private static bool ReadMinimize()
    {
        var data = new AnimationInfo { Size = 8 };
        if (!Animation(GetMinimize, data.Size, ref data, 0)) throw new Win32Exception();
        return data.Minimize != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int Minimize;
    }

    internal static WallpaperColors? ReadWallpaperColors()
    {
        var path = new StringBuilder(32768);
        if (!ReadPath(GetWallpaper, (uint)path.Capacity, path, 0) || path.Length == 0) return null;
        try
        {
            using var input = File.OpenRead(path.ToString());
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = input;
            image.DecodePixelWidth = 96;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            return WallpaperColors.FromRgba(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ExternalException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool Read(uint action, uint parameter, out int value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool Write(uint action, uint parameter, nint value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool Animation(uint action, uint parameter, ref AnimationInfo value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode)]
    private static extern bool ReadPath(uint action, uint parameter, StringBuilder value, uint flags);
}
