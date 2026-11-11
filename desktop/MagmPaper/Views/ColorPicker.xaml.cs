using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MagmPaper.Views;

public partial class ColorPicker : Window
{
    private double saturation, brightness;
    private bool ready, filling;
    public string SelectedColor { get; private set; } = "#171923";
    public ColorPicker(string initial)
    {
        InitializeComponent(); ready = true;
        foreach (string color in new[] { "#171923", "#303343", "#BCA6FF", "#5B8DEF", "#57C9AB", "#E6B85C", "#D47094", "#EAEAF1" })
        {
            var button = new Button { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), Margin = new Thickness(0, 0, 6, 0), Height = 25, Padding = new Thickness(0), ToolTip = color };
            button.Click += (_, _) => SetColor(color); Presets.Children.Add(button);
        }
        SetColor(initial);
    }
    internal void SetColor(string text)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$")) text = "#171923";
        var color = (Color)ColorConverter.ConvertFromString(text);
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double high = Math.Max(r, Math.Max(g, b)), low = Math.Min(r, Math.Min(g, b)), delta = high - low;
        double hue = delta == 0 ? Hue.Value : high == r ? 60 * ((g - b) / delta % 6) : high == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        filling = true; Hue.Value = (hue + 360) % 360; filling = false;
        saturation = high == 0 ? 0 : delta / high; brightness = high; UpdateColor();
    }
    private static Color FromHsv(double hue, double saturation, double value)
    {
        double chroma = value * saturation, x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - chroma;
        (double r, double g, double b) = hue switch { < 60 => (chroma, x, 0.0), < 120 => (x, chroma, 0.0), < 180 => (0.0, chroma, x), < 240 => (0.0, x, chroma), < 300 => (x, 0.0, chroma), _ => (chroma, 0.0, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
    private void UpdateColor()
    {
        if (!ready) return;
        var color = FromHsv(Hue.Value % 360, saturation, brightness);
        SelectedColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        HueBackground.Background = new SolidColorBrush(FromHsv(Hue.Value % 360, 1, 1)); Swatch.Background = new SolidColorBrush(color);
        Canvas.SetLeft(Marker, saturation * Spectrum.ActualWidth - 7); Canvas.SetTop(Marker, (1 - brightness) * Spectrum.ActualHeight - 7);
        filling = true; Hex.Text = SelectedColor; filling = false; Accept.IsEnabled = true; Hint.Text = "Непрозрачность настраивается отдельно.";
    }
    private void Pick(MouseEventArgs args)
    {
        var point = args.GetPosition(Spectrum);
        saturation = Math.Clamp(point.X / Math.Max(1, Spectrum.ActualWidth), 0, 1); brightness = 1 - Math.Clamp(point.Y / Math.Max(1, Spectrum.ActualHeight), 0, 1); UpdateColor();
    }
    private void SpectrumDown(object sender, MouseButtonEventArgs args) { Spectrum.Focus(); Spectrum.CaptureMouse(); Pick(args); args.Handled = true; }
    private void SpectrumMove(object sender, MouseEventArgs args) { if (Spectrum.IsMouseCaptured && args.LeftButton == MouseButtonState.Pressed) Pick(args); }
    private void SpectrumUp(object sender, MouseButtonEventArgs args) { Spectrum.ReleaseMouseCapture(); args.Handled = true; }
    private void SpectrumResize(object sender, SizeChangedEventArgs args) => UpdateColor();
    private void SpectrumKey(object sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        saturation = Math.Clamp(saturation + (args.Key == Key.Right ? .01 : args.Key == Key.Left ? -.01 : 0), 0, 1);
        brightness = Math.Clamp(brightness + (args.Key == Key.Up ? .01 : args.Key == Key.Down ? -.01 : 0), 0, 1); UpdateColor(); args.Handled = true;
    }
    private void HueChanged(object sender, RoutedPropertyChangedEventArgs<double> args) { if (!filling) UpdateColor(); }
    private void HexChanged(object sender, TextChangedEventArgs args)
    {
        if (!ready || filling) return;
        if (System.Text.RegularExpressions.Regex.IsMatch(Hex.Text, "^#[0-9a-fA-F]{6}$")) SetColor(Hex.Text);
        else { Accept.IsEnabled = false; Hint.Text = "Введите цвет в формате #RRGGBB."; }
    }
    private void AcceptClick(object sender, RoutedEventArgs args) => DialogResult = true;
    private void DragTitle(object sender, MouseButtonEventArgs args) { if (args.OriginalSource is not Button) DragMove(); }
}
