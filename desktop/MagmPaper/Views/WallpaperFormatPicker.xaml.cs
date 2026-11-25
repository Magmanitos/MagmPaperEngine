using System.Windows;
using System.Windows.Controls;

namespace MagmPaper.Views;

public partial class WallpaperFormatPicker : Window
{
    public WallpaperFormat Format { get; private set; }
    public WallpaperFormatPicker() => InitializeComponent();

    private void Choose(object sender, RoutedEventArgs e)
    {
        Format = Enum.Parse<WallpaperFormat>((string)((Button)sender).Tag);
        DialogResult = true;
    }
}
