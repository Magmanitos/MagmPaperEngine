using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MagmPaper.Views;

public partial class WallpaperDialog : Window
{
    internal double FrameRadius => DialogCard.CornerRadius.TopLeft;
    internal WallpaperFormat Format { get; private set; }
    internal string? MediaFile { get; private set; }
    internal string WallpaperName => NameInput.Text.Trim();

    public WallpaperDialog() => InitializeComponent();

    internal WallpaperDialog(string name) : this()
    {
        Title = "Переименовать обои";
        ShowName(name, "Сохранить");
    }

    private void Choose(object sender, RoutedEventArgs e)
    {
        var format = Enum.Parse<WallpaperFormat>((string)((Button)sender).Tag);
        if (format == WallpaperFormat.Glsl)
        {
            Format = format;
            DialogResult = true;
            return;
        }

        string extension = format.ToString().ToLowerInvariant();
        var file = new OpenFileDialog
        {
            Title = "Выберите файл обоев",
            Filter = $"{extension.ToUpperInvariant()} (*.{extension})|*.{extension}",
            CheckFileExists = true
        };
        if (file.ShowDialog(this) == true) ChooseMedia(file.FileName, format);
    }

    internal void ChooseMedia(string path, WallpaperFormat format)
    {
        MediaFile = path;
        Format = format;
        ShowName(Library.SuggestedMediaName(path), "Добавить");
    }

    private void ShowName(string name, string action)
    {
        FormatPanel.Visibility = Visibility.Collapsed;
        NamePanel.Visibility = AcceptName.Visibility = Visibility.Visible;
        AcceptName.Content = action;
        NameInput.Text = name;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible) return;
            NameInput.Focus();
            NameInput.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void NameChanged(object sender, TextChangedEventArgs e) => AcceptName.IsEnabled = WallpaperName.Length > 0;
    private void Accept(object sender, RoutedEventArgs e) => DialogResult = true;
    private void DragTitle(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
