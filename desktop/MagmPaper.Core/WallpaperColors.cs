namespace MagmPaper.Core;

public sealed record WallpaperColors(string First, string Second)
{
    public static WallpaperColors FromRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || 4L * width * height != rgba.Length)
            throw new ArgumentException("Некорректный кадр обоев.", nameof(rgba));

        int middle = Math.Max(1, width / 2);
        string first = Dominant(rgba, width, height, 0, middle);
        string second = width == 1 ? first : Dominant(rgba, width, height, middle, width);
        return new(first, second);
    }

    private static string Dominant(ReadOnlySpan<byte> rgba, int width, int height, int start, int end)
    {
        // Nearby shades vote together. Take the average of the winning group instead of
        // mixing unrelated colors across the wallpaper.
        Span<int> count = stackalloc int[512];
        Span<int> red = stackalloc int[512];
        Span<int> green = stackalloc int[512];
        Span<int> blue = stackalloc int[512];
        count.Clear();
        red.Clear();
        green.Clear();
        blue.Clear();
        int stepX = Math.Max(1, (end - start) / 160);
        int stepY = Math.Max(1, height / 12);
        int winner = 0;
        for (int y = 0; y < height; y += stepY)
        for (int x = start; x < end; x += stepX)
        {
            int offset = (y * width + x) * 4;
            int r = rgba[offset], g = rgba[offset + 1], b = rgba[offset + 2];
            int bucket = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
            red[bucket] += r;
            green[bucket] += g;
            blue[bucket] += b;
            count[bucket]++;
            if (count[bucket] > count[winner]) winner = bucket;
        }
        int n = count[winner];
        return $"#{red[winner] / n:X2}{green[winner] / n:X2}{blue[winner] / n:X2}";
    }
}
