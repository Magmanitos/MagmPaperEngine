using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagmPaper.Windows;

namespace MagmPaper.Graphics;

internal static class MediaThumbnails
{
    public static async Task<BitmapSource?> ReadAsync(string path)
    {
        if (Library.FormatOf(path) == WallpaperFormat.Gif)
            return (await GifClip.ReadAsync(path, GalleryRenderer.Width, animate: false)).First;
        return await Task.Run(() => ReadVideo(path));
    }

    private static BitmapSource? ReadVideo(string path)
    {
        int com = CoInitializeEx(0, 0); // Thumbnail providers run in the worker's COM apartment.
        try { return ReadVideoShellItem(path); }
        finally { if (com >= 0) CoUninitialize(); }
    }

    private static BitmapSource? ReadVideoShellItem(string path)
    {
        Guid iid = typeof(IImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, 0, ref iid, out var item) < 0) return null;
        nint bitmap = 0;
        try
        {
            const uint ThumbnailOnly = 8;
            if (item.GetImage(new(GalleryRenderer.Width, GalleryRenderer.Height), ThumbnailOnly, out bitmap) < 0 || bitmap == 0) return null;
            var thumbnail = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var opaque = new FormatConvertedBitmap(thumbnail, PixelFormats.Bgr32, null, 0);
            opaque.Freeze();
            return opaque;
        }
        finally
        {
            if (bitmap != 0) Win32.DeleteObject(bitmap);
            Marshal.ReleaseComObject(item);
        }
    }

    public static async Task<WallpaperColors?> ColorsAsync(string path)
    {
        var image = await ReadAsync(path);
        if (image is null) return null;
        var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = pixels.PixelWidth, height = pixels.PixelHeight;
        int rows = Math.Min(12, height);
        var data = new byte[width * rows * 4];
        pixels.CopyPixels(new Int32Rect(0, (height - rows) * 5 / 6, width, rows), data, width * 4, 0);
        // The picker only uses red, green and blue channels. Convert BGR in place.
        for (int i = 0; i < data.Length; i += 4) (data[i], data[i + 2]) = (data[i + 2], data[i]);
        return WallpaperColors.FromRgba(data, width, rows);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PixelSize(int x, int y)
    {
        public readonly int X = x, Y = y;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageFactory
    {
        [PreserveSig] int GetImage(PixelSize size, uint flags, out nint bitmap);
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint binding, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IImageFactory item);
}
