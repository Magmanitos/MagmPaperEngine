using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace MagmPaper.Graphics;

internal sealed class GifClip(BitmapSource[] frames, long[] ends)
{
    public BitmapSource First => frames[0];

    public BitmapSource FrameAt(long elapsed, out long untilNext)
    {
        long position = elapsed % ends[^1];
        int index = Array.BinarySearch(ends, position + 1);
        if (index < 0) index = ~index;
        untilNext = ends[index] - position;
        return frames[index];
    }

    public static Task<GifClip> ReadAsync(string path, int maxDimension, bool animate, CancellationToken token = default) =>
        Task.Run(() => Read(path, maxDimension, animate, token), token);

    private static GifClip Read(string path, int maxDimension, bool animate, CancellationToken token)
    {
        using var input = File.OpenRead(path);
        // Guard decoded dimensions before asking GDI+ to allocate a canvas.
        Span<byte> header = stackalloc byte[10];
        input.ReadExactly(header);
        if (!header[..6].SequenceEqual("GIF87a"u8) && !header[..6].SequenceEqual("GIF89a"u8))
            throw new InvalidDataException("Выберите корректный GIF-файл.");
        int width = header[6] | header[7] << 8, height = header[8] | header[9] << 8;
        if (width == 0 || height == 0 || (long)width * height > 16_777_216)
            throw new InvalidDataException("Слишком большой размер GIF.");
        input.Position = 0;
        using var image = Drawing.Image.FromStream(input);
        var dimension = new FrameDimension(image.FrameDimensionsList[0]);
        int count = animate ? image.GetFrameCount(dimension) : 1;
        if (count is < 1 or > 4096) throw new InvalidDataException("Слишком много кадров GIF.");

        // Cache composited frames once so playback never decodes on the UI thread.
        // The cap also allows long GIFs to play without storing full-size frames.
        const double frameBudget = 96 * 1024 * 1024;
        double scale = Math.Min(1, Math.Min((double)maxDimension / Math.Max(width, height),
            Math.Sqrt(frameBudget / (4.0 * width * height * count))));
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
        byte[]? delays = image.PropertyIdList.Contains(0x5100) ? image.GetPropertyItem(0x5100)?.Value : null;
        var frames = new BitmapSource[count];
        var ends = new long[count];
        using var canvas = new Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Drawing.Graphics.FromImage(canvas);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = scale < .99 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
        var rect = new Drawing.Rectangle(0, 0, w, h);
        long end = 0;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            image.SelectActiveFrame(dimension, i);
            graphics.DrawImage(image, rect, 0, 0, width, height, Drawing.GraphicsUnit.Pixel);
            var data = canvas.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var frame = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, data.Scan0, data.Stride * h, data.Stride);
                frame.Freeze(); frames[i] = frame;
            }
            finally { canvas.UnlockBits(data); }
            int delay = delays is not null && delays.Length >= 4 * (i + 1) ? BitConverter.ToInt32(delays, 4 * i) : 0;
            end += delay > 0 ? Math.Min(60_000, 10L * delay) : 100;
            ends[i] = end;
        }
        return new(frames, ends);
    }
}
