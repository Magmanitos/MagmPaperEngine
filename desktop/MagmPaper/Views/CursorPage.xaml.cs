using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MagmPaper.Windows;

namespace MagmPaper.Views;

public partial class CursorPage : UserControl
{
    private CursorSettings? settings;
    private Dictionary<string, string?> files = new();
    private Cursor? preview;
    private Task update = Task.CompletedTask;

    public CursorPage()
    {
        InitializeComponent();
        Packs.ItemsSource = CursorSettings.Packs;
        Packs.SelectedIndex = 0;
        Roles.ItemsSource = CursorFiles.Roles;
        Roles.SelectedIndex = 0;
    }

    internal void Initialize(string directory) => settings = new(directory);

    internal void Refresh()
    {
        if (settings is null || !update.IsCompleted) return;
        try { ReadCurrent(); }
        catch (Exception error) { ShowFeedback(error.Message, true); }
    }

    internal Task FinishUpdatesAsync() => update;

    private void ReadCurrent()
    {
        files = settings!.Read();
        RestoreButton.IsEnabled = settings.CanRestore;
        UpdatePreview();
    }

    private void ShowFeedback(string message, bool error = false)
    {
        if (Window.GetWindow(this) is MainWindow owner) owner.Status(message, error);
    }

    private void Begin(Func<Task<string>> operation)
    {
        if (settings is not null && update.IsCompleted) update = RunAsync(operation);
    }

    private async Task RunAsync(Func<Task<string>> operation)
    {
        IsEnabled = false;
        try
        {
            ShowFeedback("Обновляем схему курсоров…");
            string message = await operation();
            ReadCurrent();
            ShowFeedback(message);
        }
        catch (Exception error) { ShowFeedback(error.Message, true); }
        finally { IsEnabled = true; }
    }

    private void InstallPackClick(object sender, RoutedEventArgs e)
    {
        if (Packs.SelectedItem is not CursorPack pack) return;
        Begin(async () =>
        {
            var assets = await settings!.DownloadAsync(pack);
            int count = await Task.Run(() => settings.Apply(assets));
            return $"{pack.Name} · обновлено состояний: {count} из {CursorFiles.Roles.Count}.";
        });
    }

    private void SourceClick(object sender, RoutedEventArgs e)
    {
        if (Packs.SelectedItem is not CursorPack pack) return;
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

    private void ChooseFileClick(object sender, RoutedEventArgs e)
    {
        if (Roles.SelectedItem is not CursorRole role) return;
        var owner = Window.GetWindow(this);
        var dialog = new OpenFileDialog { Title = role.Label, Filter = "Курсоры и PNG (*.cur;*.ani;*.png)|*.cur;*.ani;*.png" };
        if (dialog.ShowDialog(owner) != true) return;
        Begin(async () =>
        {
            string name = Path.GetFileName(dialog.FileName);
            byte[] bytes = await Task.Run(() => CursorSettings.ReadFile(dialog.FileName));
            if (Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                var picker = new CursorHotspot(bytes) { Owner = owner };
                if (picker.ShowDialog() != true) return "Назначение курсора отменено.";
                bytes = picker.CursorBytes();
                name = Path.ChangeExtension(name, ".cur");
            }
            await Task.Run(() => settings!.Apply(new() { [role.Key] = new(name, bytes) }));
            return $"Обновлено состояние «{role.Label}».";
        });
    }

    private void RestoreClick(object sender, RoutedEventArgs e) => Begin(async () =>
    {
        await Task.Run(settings!.Restore);
        return "Прежняя схема курсоров восстановлена.";
    });

    private void ResetClick(object sender, RoutedEventArgs e)
    {
        if (Roles.SelectedItem is not CursorRole role) return;
        Begin(async () =>
        {
            await Task.Run(() => settings!.Reset(role.Key));
            return $"Возвращён стандартный курсор для состояния «{role.Label}».";
        });
    }

    private void RoleChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (Roles.SelectedItem is not CursorRole role) return;
        HoverArea.Cursor = null;
        preview?.Dispose();
        preview = null;
        files.TryGetValue(role.Key, out var path);
        RoleTitle.Text = role.Label;
        bool custom = !string.IsNullOrEmpty(path);
        ResetButton.IsEnabled = custom;
        RoleFile.Text = custom ? Path.GetFileName(path) : "Стандартный курсор Windows";
        RoleFile.ToolTip = path;
        HoverText.Text = custom ? "Наведите сюда курсор для проверки" : "Используется стандартный курсор для этого состояния";
        if (!custom) return;
        try
        {
            if (!CursorFiles.IsCursor(path!)) return;
            preview = new Cursor(path!, scaleWithDpi: true);
            HoverArea.Cursor = preview;
        }
        catch { HoverText.Text = "Файл курсора недоступен. Выберите другой файл."; }
    }
}
