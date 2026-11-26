using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MagmPaper.Windows;

namespace MagmPaper.Views;

public partial class CursorDetails : Window
{
    private readonly CursorSettings settings;
    private readonly CursorPage.Tile tile;
    private readonly Func<Func<Task<string>>, Action<string, bool>, Task> runUpdate;
    private readonly Func<Task<Dictionary<string, CursorAsset>>>? loadPack;
    private IReadOnlyDictionary<string, string?> files;
    private Dictionary<string, CursorAsset>? assets;
    private Cursor? cursor;
    private bool loading, closed;

    internal CursorDetails(CursorPage.Tile tile, CursorSettings settings,
        Func<Func<Task<string>>, Action<string, bool>, Task> runUpdate,
        Func<Task<Dictionary<string, CursorAsset>>>? loadPack)
    {
        this.tile = tile;
        this.settings = settings;
        this.runUpdate = runUpdate;
        this.loadPack = loadPack;
        files = loadPack is null ? settings.Read() : new Dictionary<string, string?>();
        InitializeComponent();
        Title = tile.Name;
        if (loadPack is not null)
        {
            ChooseFileButton.Visibility = ResetButton.Visibility = FormatHint.Visibility = Visibility.Collapsed;
            HoverArea.MouseEnter += LoadPackPreview;
        }
        Roles.ItemsSource = CursorFiles.Roles;
        Roles.SelectedIndex = 0;
        Closed += (_, _) => { closed = true; ReleaseCursor(); };
    }

    private async void LoadPackPreview(object sender, MouseEventArgs e)
    {
        if (loadPack is null || loading || assets is not null || RoleImage.Source is null) return;
        loading = true;
        ShowRole();
        try { assets = await loadPack(); }
        catch (Exception error) { ShowFeedback(error.Message, true); }
        finally { loading = false; }
        if (!closed) ShowRole();
    }

    private void ReleaseCursor()
    {
        HoverArea.Cursor = null;
        cursor?.Dispose();
        cursor = null;
    }

    private void ShowRole()
    {
        if (Roles.SelectedItem is not CursorRole role) return;
        ReleaseCursor();
        RoleTitle.Text = role.Label;
        if (loadPack is not null)
        {
            var frame = tile.Frames.FirstOrDefault(item => item.Label == role.Label);
            RoleImage.Source = frame?.Picture;
            RoleImage.Visibility = frame is null ? Visibility.Collapsed : Visibility.Visible;
            RoleFile.Text = tile.Name;
            HoverText.Text = frame is null ? "В наборе нет этого состояния" : loading ? "Загружаем указатель…" : "Наведите сюда курсор для проверки";
            if (assets is not null && assets.TryGetValue(role.Key, out var asset))
            {
                RoleFile.Text = asset.Name;
                try
                {
                    using var stream = new MemoryStream(asset.Bytes, writable: false);
                    HoverArea.Cursor = cursor = new Cursor(stream, scaleWithDpi: true);
                }
                catch { HoverText.Text = "Файл курсора недоступен."; }
            }
            return;
        }

        files.TryGetValue(role.Key, out var path);
        bool custom = !string.IsNullOrEmpty(path);
        ResetButton.IsEnabled = custom;
        RoleFile.Text = custom ? Path.GetFileName(path) : "Стандартный курсор Windows";
        RoleFile.ToolTip = path;
        HoverText.Text = custom ? "Наведите сюда курсор для проверки" : "Используется стандартный курсор для этого состояния";
        if (!custom) return;
        try { HoverArea.Cursor = cursor = new Cursor(path!, scaleWithDpi: true); }
        catch { HoverText.Text = "Файл курсора недоступен. Выберите другой файл."; }
    }

    private async Task ChangeAsync(Func<Task<string>> operation)
    {
        Editor.IsEnabled = false;
        try
        {
            await runUpdate(operation, ShowFeedback);
            if (!closed) { files = settings.Read(); ShowRole(); }
        }
        catch (Exception error) { ShowFeedback(error.Message, true); }
        finally { if (!closed) Editor.IsEnabled = true; }
    }

    private async void ChooseFileClick(object sender, RoutedEventArgs e)
    {
        if (Roles.SelectedItem is not CursorRole role) return;
        var dialog = new OpenFileDialog { Title = role.Label, Filter = "Курсоры и PNG (*.cur;*.ani;*.png)|*.cur;*.ani;*.png" };
        if (dialog.ShowDialog(this) != true) return;
        await ChangeAsync(async () =>
        {
            string name = Path.GetFileName(dialog.FileName);
            byte[] bytes = await Task.Run(() => CursorSettings.ReadFile(dialog.FileName));
            if (closed) return "Назначение курсора отменено.";
            if (Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                var picker = new CursorHotspot(bytes) { Owner = this };
                if (Owner is MainWindow main) main.TrackWindowFrame(picker);
                if (picker.ShowDialog() != true) return "Назначение курсора отменено.";
                bytes = picker.CursorBytes();
                name = Path.ChangeExtension(name, ".cur");
            }
            await Task.Run(() => settings.Apply(new() { [role.Key] = new(name, bytes) }));
            return $"Обновлено состояние «{role.Label}».";
        });
    }

    private async void ResetClick(object sender, RoutedEventArgs e)
    {
        if (Roles.SelectedItem is not CursorRole role) return;
        await ChangeAsync(async () =>
        {
            await Task.Run(() => settings.Reset(role.Key));
            return $"Возвращён стандартный курсор для состояния «{role.Label}».";
        });
    }

    private void ShowFeedback(string message, bool error)
    {
        if (closed) return;
        Feedback.Text = message;
        Feedback.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedBrush");
        Feedback.Visibility = Visibility.Visible;
    }

    private void RoleChanged(object sender, SelectionChangedEventArgs e) => ShowRole();
    private void DragTitle(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
