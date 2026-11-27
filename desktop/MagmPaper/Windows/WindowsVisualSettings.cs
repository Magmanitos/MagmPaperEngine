using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MagmPaper.Windows;

internal static class WindowsVisualSettings
{
    private const uint GetClientAreaAnimation = 0x1042, SetClientAreaAnimation = 0x1043;
    private const uint UpdateProfile = 1, BroadcastChange = 2;
    private const uint GetWallpaper = 0x0073;

    internal static bool AnimationsEnabled
    {
        get
        {
            if (!Read(GetClientAreaAnimation, 0, out int enabled, 0)) throw new Win32Exception();
            return enabled != 0;
        }
        set
        {
            if (!Write(SetClientAreaAnimation, 0, value ? 1 : 0, UpdateProfile | BroadcastChange))
                throw new Win32Exception();
        }
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
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode)]
    private static extern bool ReadPath(uint action, uint parameter, StringBuilder value, uint flags);
}
