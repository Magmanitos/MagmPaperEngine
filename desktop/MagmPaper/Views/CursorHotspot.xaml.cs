using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MagmPaper.Views;

public partial class CursorHotspot : Window
{
    private readonly int width, height;
    private readonly FormatConvertedBitmap bitmap;

    internal CursorHotspot(byte[] png)
    {
        (width, height) = CursorFiles.PngSize(png);
        using var stream = new MemoryStream(png, writable: false);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth != width || frame.PixelHeight != height)
            throw new InvalidDataException("Размер декодированного PNG не совпадает с заголовком.");
        bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        InitializeComponent();
        Picture.Source = bitmap;
        double scale = 200.0 / Math.Max(width, height);
        Surface.Width = width * scale;
        Surface.Height = height * scale;
        XInput.Maximum = width - 1;
        YInput.Maximum = height - 1;
        DrawMarker();
    }

    internal byte[] CursorBytes()
    {
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return CursorFiles.FromBgra(width, height, (int)XInput.Value, (int)YInput.Value, pixels);
    }

    private void PickPixel(object sender, MouseButtonEventArgs e)
    {
        Point point = e.GetPosition(Surface);
        XInput.Value = Math.Clamp((int)(point.X / Surface.Width * width), 0, width - 1);
        YInput.Value = Math.Clamp((int)(point.Y / Surface.Height * height), 0, height - 1);
    }

    private void HotspotChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (XInput is not null && YInput is not null && Marker is not null) DrawMarker();
    }

    private void DrawMarker()
    {
        Canvas.SetLeft(Marker, (XInput.Value + .5) / width * Surface.Width - Marker.Width / 2);
        Canvas.SetTop(Marker, (YInput.Value + .5) / height * Surface.Height - Marker.Height / 2);
    }

    private void AcceptClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
