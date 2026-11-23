using System.Buffers.Binary;
using System.Text;

namespace MagmPaper.Core;

public sealed record CursorRole(string Key, string Label, params string[] FileNames);

public static class CursorFiles
{
    public const int MaxFileBytes = 8 * 1024 * 1024;
    public static IReadOnlyList<CursorRole> Roles { get; } = Array.AsReadOnly<CursorRole>(
    [
        new("Arrow", "Основной указатель", "Pointer", "Default", "Normal", "Normal Select"),
        new("Help", "Выбор справки", "Help Select", "Question"),
        new("AppStarting", "Фоновая загрузка", "Work", "Working In Background", "Progress"),
        new("Wait", "Ожидание", "Busy", "Loading"),
        new("Crosshair", "Точное выделение", "Cross", "Precision", "Precision Select"),
        new("IBeam", "Выделение текста", "Text", "Text Select", "Beam"),
        new("NWPen", "Рукописный ввод", "Handwriting", "Pen", "Pencil"),
        new("No", "Недоступно", "Unavailable", "Unavailiable", "Forbidden", "Not Allowed"),
        new("SizeNS", "Размер по вертикали", "Vert", "Vertical", "Vertical Resize", "NS Resize"),
        new("SizeWE", "Размер по горизонтали", "Horz", "Horizontal", "Horizontal Resize", "EW Resize"),
        new("SizeNWSE", "Диагональ ↘", "Dgn1", "Diagonal 1", "Diagonal Resize 1", "NWSE Resize"),
        new("SizeNESW", "Диагональ ↙", "Dgn2", "Diagonal 2", "Diagonal Resize 2", "NESW Resize"),
        new("SizeAll", "Перемещение", "Move", "All Scroll", "Fleur"),
        new("UpArrow", "Дополнительное выделение", "Alternate", "Alternate Select"),
        new("Hand", "Выбор ссылки", "Link", "Link Select", "Hand2"),
        new("Pin", "Выбор местоположения", "Location", "Location Select"),
        new("Person", "Выбор человека", "Person Select"),
        new("Pan", "Панорамирование", "Open Hand", "Grab"),
        new("Grabbing", "Перетаскивание", "Closed Hand"),
        new("Zoom-in", "Увеличение", "Magnify"),
        new("Zoom-out", "Уменьшение")
    ]);
    private static string Normalize(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    public static bool IsCursor(string name) => Path.GetExtension(name).ToLowerInvariant() is ".cur" or ".ani";

    public static Dictionary<string, T> Match<T>(IEnumerable<T> entries, Func<T, string> name)
    {
        var lookup = entries.Where(entry => IsCursor(name(entry)))
            .GroupBy(entry => Normalize(Path.GetFileNameWithoutExtension(name(entry))))
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => name(entry), StringComparer.OrdinalIgnoreCase).First());
        var result = new Dictionary<string, T>();
        foreach (var role in Roles)
        {
            foreach (string candidate in role.FileNames.Prepend(role.Key))
            {
                if (!lookup.TryGetValue(Normalize(candidate), out var entry)) continue;
                result.Add(role.Key, entry);
                break;
            }
        }
        return result;
    }

    public static bool HasCursorHeader(string extension, ReadOnlySpan<byte> bytes) =>
        extension.Equals(".cur", StringComparison.OrdinalIgnoreCase)
            ? bytes.Length >= 22 && bytes[..4].SequenceEqual(new byte[] { 0, 0, 2, 0 }) && BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 0
            : extension.Equals(".ani", StringComparison.OrdinalIgnoreCase) && bytes.Length >= 12 &&
                bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("ACON"u8);

    public static (int Width, int Height) PngSize(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(signature) || !bytes[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Выбранный файл не является PNG.");
        int width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..]);
        if (width is < 1 or > 256 || height is < 1 or > 256)
            throw new InvalidDataException("Размер изображения PNG должен быть от 1×1 до 256×256 пикселей.");
        return (width, height);
    }

    public static byte[] FromBgra(int width, int height, int x, int y, ReadOnlySpan<byte> pixels)
    {
        if (width is < 1 or > 256 || height is < 1 or > 256 || x < 0 || x >= width || y < 0 || y >= height ||
            pixels.Length != width * height * 4)
            throw new ArgumentException("Некорректное изображение или точка клика.");
        int imageBytes = width * height * 4;
        int maskStride = (width + 31) / 32 * 4;
        int dibBytes = 40 + imageBytes + maskStride * height;
        using var result = new MemoryStream(22 + dibBytes);
        using var writer = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write((ushort)2); // CUR
        writer.Write((ushort)1);
        writer.Write((byte)(width % 256));
        writer.Write((byte)(height % 256));
        writer.Write((ushort)0);
        writer.Write((ushort)x);
        writer.Write((ushort)y);
        writer.Write(dibBytes);
        writer.Write(22);
        writer.Write(40); // BITMAPINFOHEADER
        writer.Write(width);
        writer.Write(height * 2); // The color bitmap and monochrome mask.
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(imageBytes);
        for (int i = 0; i < 4; i++) writer.Write(0);
        for (int row = height - 1; row >= 0; row--) writer.Write(pixels.Slice(row * width * 4, width * 4));
        var mask = new byte[maskStride];
        for (int row = height - 1; row >= 0; row--)
        {
            Array.Clear(mask);
            for (int column = 0; column < width; column++)
                if (pixels[(row * width + column) * 4 + 3] == 0)
                    mask[column / 8] |= (byte)(0x80 >> (column % 8));
            writer.Write(mask);
        }
        return result.ToArray();
    }
}
