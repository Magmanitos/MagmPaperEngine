using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagmPaper.Windows;

namespace MagmPaper.Graphics;

internal sealed class GalleryRenderer : IDisposable
{
    public const int Width = 320, Height = 180, Stride = Width * 4;
    private readonly nint window;
    private readonly RenderSurface surface;
    private readonly byte[] rgba = new byte[Stride * Height], bgr = new byte[Stride * Height];

    public GalleryRenderer()
    {
        window = Win32.CreateSurface(0);
        try
        {
            if (!Win32.SetWindowPos(window, 0, 0, 0, Width, Height, 0x14)) throw new Win32Exception();
            surface = new RenderSurface(window);
        }
        catch { Win32.DestroyWindow(window); throw; }
    }

    public Task LoadAsync(string code) => surface.LoadAsync(code);

    public async Task<byte[]> FrameAsync(double seconds, double x = .5, double y = .5)
    {
        var frame = await surface.PreviewFrameAsync(seconds, x, y, rgba).ConfigureAwait(false);
        if (frame.Width != Width || frame.Height != Height) throw new IOException("Изменился размер предпросмотра галереи.");
        // OpenGL returns RGBA rows from the bottom. WPF's Bgr32 starts at the top.
        for (int row = 0; row < Height; row++)
        {
            int src = (Height - row - 1) * Stride, dst = row * Stride;
            for (int xPixel = 0; xPixel < Width; xPixel++, src += 4, dst += 4)
            {
                bgr[dst] = frame.Rgba[src + 2];
                bgr[dst + 1] = frame.Rgba[src + 1];
                bgr[dst + 2] = frame.Rgba[src];
                bgr[dst + 3] = 255;
            }
        }
        return bgr;
    }

    public static BitmapSource Thumbnail(byte[] pixels)
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, pixels, Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static WriteableBitmap LiveBitmap() => new(Width, Height, 96, 96, PixelFormats.Bgr32, null);
    public static void Update(WriteableBitmap bitmap, byte[] pixels) =>
        bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), pixels, Stride, 0);

    public void Dispose()
    {
        surface.Dispose();
        Win32.DestroyWindow(window);
    }
}
