using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MagmPaper.Windows;

namespace MagmPaper.Views;

public partial class CursorPage : UserControl
{
    internal sealed record Tile(string Name, CursorPack? Pack, IReadOnlyList<CursorPreviewFrame> Frames);
    private readonly ObservableCollection<Tile> tiles = [new("Текущая схема", null, [])];
    private CursorSettings? settings;
    private Dictionary<string, string?> files = new();
    private readonly Dictionary<CursorPack, Task<Dictionary<string, CursorAsset>>> packs = new();
    private Task update = Task.CompletedTask;
    private CursorTilePreview? hovered;
    internal IReadOnlyList<Tile> Tiles => tiles;

    public CursorPage()
    {
        InitializeComponent();
        Gallery.Tag = 3;
        Gallery.ItemsSource = tiles;
    }

    internal void Initialize(string directory) => settings = new(directory);

    internal void Refresh()
    {
        if (settings is null || !update.IsCompleted) return;
        try
        {
            if (tiles.Count == 1)
                foreach (var pack in CursorSettings.Packs) tiles.Add(new(pack.Name, pack, CursorArtwork.Pack(pack)));
            ReadCurrent();
        }
        catch (Exception error) { ShowFeedback(error.Message, true); }
    }

    internal Task FinishUpdatesAsync() => update;

    private void ReadCurrent()
    {
        var snapshot = settings!.Read();
        if (!files.SequenceEqual(snapshot))
        {
            files = snapshot;
            _ = LoadCurrentPreviewAsync(snapshot);
        }
        RestoreButton.IsEnabled = settings.CanRestore;
    }

    private async Task LoadCurrentPreviewAsync(Dictionary<string, string?> snapshot)
    {
        try
        {
            var frames = await Task.Run(() => CursorArtwork.Current(snapshot));
            if (ReferenceEquals(files, snapshot)) tiles[0] = tiles[0] with { Frames = frames };
        }
        catch (Exception error) { ShowFeedback(error.Message, true); }
    }

    private void GalleryResized(object sender, SizeChangedEventArgs e) => Gallery.Tag = e.NewSize.Width >= 745 ? 3 : 2;

    private static CursorTilePreview? PreviewOf(object sender) =>
        sender is Border { Child: Grid tile } ? tile.Children[0] as CursorTilePreview : null;

    private void TileEntered(object sender, MouseEventArgs e)
    {
        var next = PreviewOf(sender);
        if (ReferenceEquals(hovered, next)) return;
        hovered?.Stop();
        hovered = next;
        hovered?.Start();
    }

    private void TileLeft(object sender, MouseEventArgs e)
    {
        if (!ReferenceEquals(hovered, PreviewOf(sender))) return;
        hovered?.Stop();
        hovered = null;
    }

    private void GalleryScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PageScroll) || e.VerticalChange == 0) return;
        hovered?.Stop();
        hovered = null;
    }

    private void OpenTileClick(object sender, RoutedEventArgs e)
    {
        if (settings is null || !update.IsCompleted || sender is not FrameworkElement { DataContext: Tile tile }) return;
        hovered?.Stop();
        hovered = null;
        Func<Task<Dictionary<string, CursorAsset>>>? load = tile.Pack is { } pack ? () => LoadPackAsync(pack) : null;
        try
        {
            var details = new CursorDetails(tile, settings, Begin, load);
            details.BackRequested += () =>
            {
                details.CloseDetails();
                DetailsHost.Content = null;
                DetailsHost.Visibility = Visibility.Collapsed;
                PageScroll.Visibility = Visibility.Visible;
                if (sender is FrameworkElement opener) opener.Focus();
            };
            DetailsHost.Content = details;
            PageScroll.Visibility = Visibility.Collapsed;
            DetailsHost.Visibility = Visibility.Visible;
            details.BackButton.Focus();
        }
        catch (Exception error) { ShowFeedback(error.Message, true); }
    }

    private Task<Dictionary<string, CursorAsset>> LoadPackAsync(CursorPack pack)
    {
        if (!packs.TryGetValue(pack, out var pending) || pending.IsCompleted)
            packs[pack] = pending = Task.Run(() => settings!.DownloadAsync(pack));
        return pending;
    }

    private void ShowFeedback(string message, bool error = false)
    {
        if (Window.GetWindow(this) is MainWindow owner) owner.Status(message, error);
    }

    private Task Begin(Func<Task<string>> operation, Action<string, bool>? feedback = null)
    {
        if (settings is not null && update.IsCompleted) update = RunAsync(operation, feedback);
        return update;
    }

    private async Task RunAsync(Func<Task<string>> operation, Action<string, bool>? feedback)
    {
        IsEnabled = false;
        void Report(string message, bool error = false)
        {
            ShowFeedback(message, error);
            feedback?.Invoke(message, error);
        }
        try
        {
            Report("Обновляем схему курсоров…");
            string message = await operation();
            ReadCurrent();
            Report(message);
        }
        catch (Exception error) { Report(error.Message, true); }
        finally { IsEnabled = true; }
    }

    private void InstallPackClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Tile { Pack: { } pack } }) return;
        Begin(async () =>
        {
            var assets = await LoadPackAsync(pack);
            int count = await Task.Run(() => settings!.Apply(assets));
            return $"{pack.Name} · обновлено состояний: {count} из {CursorFiles.Roles.Count}.";
        });
    }

    private void SourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Tile { Pack: { } pack } }) return;
        try { SystemSettings.Open(pack.Source); }
        catch (Exception error) { ShowFeedback(error.Message, true); }
    }

    private void ImportZipClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите ZIP с курсорами", Filter = "Архивы (*.zip)|*.zip" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Import(() => CursorSettings.ReadZipFile(dialog.FileName));
    }

    private void ImportFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку с курсорами" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Import(() => CursorSettings.ReadFolder(dialog.FolderName));
    }

    private void Import(Func<Dictionary<string, CursorAsset>> read) => Begin(async () =>
    {
        int count = await Task.Run(() => settings!.Apply(read()));
        return $"Импорт завершён. Обновлено состояний: {count} из {CursorFiles.Roles.Count}.";
    });

    private void RestoreClick(object sender, RoutedEventArgs e) => Begin(async () =>
    {
        await Task.Run(settings!.Restore);
        return "Прежняя схема курсоров восстановлена.";
    });

}
