using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagmPaper.Windows;

namespace MagmPaper.Views;

public sealed record CursorPreviewFrame(string Label, BitmapSource Picture);

internal static class CursorArtwork
{
    private const int Cell = 64;
    private static readonly Dictionary<string, int> SystemCursors = new()
    {
        ["Arrow"] = 32512, ["IBeam"] = 32513, ["Wait"] = 32514, ["Crosshair"] = 32515, ["UpArrow"] = 32516,
        ["SizeNWSE"] = 32642, ["SizeNESW"] = 32643, ["SizeWE"] = 32644, ["SizeNS"] = 32645, ["SizeAll"] = 32646,
        ["No"] = 32648, ["Hand"] = 32649, ["AppStarting"] = 32650, ["Help"] = 32651
    };

    internal static IReadOnlyList<CursorPreviewFrame> Pack(CursorPack pack)
    {
        using var stream = typeof(CursorArtwork).Assembly.GetManifestResourceStream($"MagmPaper.CursorPreviews.{pack.File}.png")
            ?? throw new InvalidDataException($"Предпросмотр набора {pack.Name} недоступен.");
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (frame.PixelWidth != Cell || frame.PixelHeight != Cell * CursorFiles.Roles.Count)
            throw new InvalidDataException($"Неверный размер предпросмотра набора {pack.Name}.");
        var atlas = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        atlas.Freeze();
        var result = new List<CursorPreviewFrame>();
        var pixels = new byte[Cell * Cell * 4];
        for (int index = 0; index < CursorFiles.Roles.Count; index++)
        {
            var image = new CroppedBitmap(atlas, new Int32Rect(0, index * Cell, Cell, Cell));
            image.CopyPixels(pixels, Cell * 4, 0);
            if (!HasVisiblePixel(pixels)) continue;
            image.Freeze();
            result.Add(new(CursorFiles.Roles[index].Label, image));
        }
        return result;
    }

    private static bool HasVisiblePixel(byte[] pixels)
    {
        for (int index = 3; index < pixels.Length; index += 4)
            if (pixels[index] != 0) return true;
        return false;
    }

    internal static IReadOnlyList<CursorPreviewFrame> Current(IReadOnlyDictionary<string, string?> files)
    {
        var frames = new List<CursorPreviewFrame>();
        foreach (var role in CursorFiles.Roles)
        {
            files.TryGetValue(role.Key, out var path);
            // LR_LOADFROMFILE owns the cursor handle. Shared system cursors are owned by Windows.
            nint custom = !string.IsNullOrEmpty(path) && CursorFiles.IsCursor(path) ? LoadImage(0, path, 2, 48, 48, 0x10) : 0;
            try
            {
                nint handle = custom != 0 ? custom : (SystemCursors.TryGetValue(role.Key, out int id) ? LoadCursor(0, id) : 0);
                if (handle == 0) continue;
                var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                frames.Add(new(role.Label, image));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException) { }
            finally { if (custom != 0) DestroyCursor(custom); }
        }
        return frames;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
    private static extern nint LoadImage(nint instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern nint LoadCursor(nint instance, nint id);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(nint cursor);
}
