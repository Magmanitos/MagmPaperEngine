using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MagmPaper.Graphics;
using MagmPaper.Services;
using MagmPaper.Windows;

namespace MagmPaper;

public partial class MainWindow : Window
{
    internal sealed record WallpaperRow(Wallpaper Item, bool Active)
    {
        public string Name => Item.Name;
        public string Subtitle => Active ? "● На рабочем столе" : Item.File;
    }
    private readonly Library library;
    private readonly WallpaperService wallpapers;
    private readonly AppearanceService appearance;
    private readonly SystemSettings? system;
    private readonly string uiFile;
    private string route = "overview";
    private string? editingId;
    private bool editing, dirty, filling, selecting, busy, ready;
    private Wallpaper? selected;
    internal bool Quitting { get; set; }
    internal bool Busy => busy;
    internal Library Library => library;
    internal RenderSurface? PreviewSurface => Preview.Surface;
    internal string CurrentRoute => route;
    private const string Sample = "precision mediump float;\nuniform float time;\nuniform vec2 resolution;\nuniform vec2 mouse;\n\nvoid main() {\n    vec2 uv = gl_FragCoord.xy / resolution;\n    gl_FragColor = vec4(uv, 0.5 + 0.5 * sin(time), 1.0);\n}\n";

    internal MainWindow(Library library, WallpaperService wallpapers, AppearanceService appearance)
    {
        this.library = library; this.wallpapers = wallpapers; this.appearance = appearance;
        uiFile = Path.Combine(library.DirectoryPath, "ui.json");
        InitializeComponent();
        foreach (var field in new[] { HeightInput, IconInput, ButtonInput, ColorInput, OpacityInput, RadiusInput })
            field.TextChanged += (_, _) => UpdateTaskbarPreview();
        HideWidgetsInput.Click += (_, _) => UpdateTaskbarPreview();
        try { system = new SystemSettings(library.DirectoryPath); } catch (Exception error) { Status(error.Message, true); }
        Preview.Ready += () =>
        {
            Preview.Surface!.Error += message => Dispatcher.BeginInvoke(() => Status(message, true));
            _ = RunAsync(async () => { if (selected is not null) await LoadPreviewAsync(selected.Code); }); SyncPreview();
        };
        Loaded += async (_, _) =>
        {
            if (ready) return; ready = true;
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24); Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
            FillPreferences(); FillAppearance();
            string saved = "overview";
            try { if (File.Exists(uiFile)) saved = JsonNode.Parse(File.ReadAllText(uiFile))?["route"]?.GetValue<string>() ?? saved; } catch { }
            Navigate(saved); await RunAsync(() => RefreshLibraryAsync());
        };
        StateChanged += (_, _) => SyncPreview(); IsVisibleChanged += (_, _) => SyncPreview();
        Activated += async (_, _) => { if (ready && !busy && !editing && route == "wallpapers") await RunAsync(() => RefreshLibraryAsync(selected?.Id)); };
        Closing += OnClosing;
        wallpapers.Status += (message, error) => Status(message, error);
        wallpapers.Changed += () => { ShowPlayback(); RenderList(selected?.Id); };
        appearance.Changed += () => Dispatcher.BeginInvoke(UpdateAppearanceStatus);
    }
    internal void Status(string message, bool error = false)
    {
        StatusText.Text = message; StatusText.Foreground = (Brush)FindResource(error ? "ErrorBrush" : "MutedBrush");
    }
    private async Task RunAsync(Func<Task> task)
    {
        if (busy) return; busy = true; BusyIndicator.Visibility = Visibility.Visible; PageContainer.IsEnabled = false;
        try { await task(); }
        catch (Exception error) { Status(error.Message, true); }
        finally { busy = false; PageContainer.IsEnabled = true; BusyIndicator.Visibility = Visibility.Collapsed; Buttons(); }
    }
    private void Buttons()
    {
        ApplyWallpaperButton.IsEnabled = EditWallpaperButton.IsEnabled = DeleteWallpaperButton.IsEnabled = selected is not null && !editing;
    }
    internal void Navigate(string destination)
    {
        if (destination is not ("overview" or "wallpapers" or "taskbar" or "settings")) destination = "overview";
        route = destination; SyncPreview();
        OverviewPage.Visibility = route == "overview" ? Visibility.Visible : Visibility.Collapsed;
        WallpapersPage.Visibility = route == "wallpapers" ? Visibility.Visible : Visibility.Collapsed;
        TaskbarPage.Visibility = route == "taskbar" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = route == "settings" ? Visibility.Visible : Visibility.Collapsed;
        NavOverview.IsChecked = route == "overview"; NavWallpapers.IsChecked = route == "wallpapers"; NavTaskbar.IsChecked = route == "taskbar"; NavSettings.IsChecked = route == "settings";
        try { DataFiles.Write(uiFile, new { route }); } catch (IOException error) { Status(error.Message, true); }
        if (route == "taskbar") { FillBehavior(); UpdateAppearanceStatus(); }
        if (route == "settings") FillPreferences();
        if (route == "wallpapers" && !editing && !busy) _ = RunAsync(() => RefreshLibraryAsync(selected?.Id));
    }
    private void NavigateClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is string page) Navigate(page); }
    private void SyncPreview() => Preview.Surface?.Configure(new(route != "wallpapers" || !IsVisible || WindowState == WindowState.Minimized));
    private void ShowPlayback() => PauseText.Text = wallpapers.PauseReason is { } reason ? "Обои на паузе: " + reason : "";
    private void RenderList(string? id)
    {
        selecting = true;
        var rows = library.State.Items.Select(x => new WallpaperRow(x, wallpapers.RunningId == x.Id)).ToArray();
        WallpaperList.ItemsSource = rows; WallpaperList.SelectedItem = rows.FirstOrDefault(x => x.Item.Id == id) ?? rows.FirstOrDefault();
        selected = (WallpaperList.SelectedItem as WallpaperRow)?.Item;
        WallpaperTitle.Text = selected?.Name ?? "Выберите обои"; CollectionCount.Text = $"{rows.Length} в коллекции";
        selecting = false; Buttons();
    }
    private async Task RefreshLibraryAsync(string? id = null)
    {
        library.Refresh(); wallpapers.Refresh(); RenderList(id ?? selected?.Id); ShowPlayback();
        if (selected is not null && route == "wallpapers") await LoadPreviewAsync(selected.Code);
    }
    private async Task LoadPreviewAsync(string code)
    {
        if (Preview.Surface is not { } surface) return;
        await surface.LoadAsync(code); SyncPreview();
    }
    private async void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (selecting || !ready || busy || WallpaperList.SelectedItem is not WallpaperRow row) return;
        if (!LeaveEditor()) { RenderList(selected?.Id); return; }
        selected = row.Item; WallpaperTitle.Text = row.Name; Buttons();
        await RunAsync(async () => { await LoadPreviewAsync(row.Item.Code); Status("Предпросмотр готов."); });
    }
    private bool LeaveEditor()
    {
        if (dirty && MessageBox.Show(this, "Отменить несохранённые изменения?", "Редактор обоев", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        editing = dirty = false; editingId = null; EditorPanel.Visibility = Visibility.Collapsed; Buttons(); return true;
    }
    private void OpenEditor(Wallpaper? item)
    {
        if (!LeaveEditor()) return;
        filling = true; editing = true; editingId = item?.Id; NameInput.Text = item?.Name ?? ""; CodeInput.Text = item?.Code ?? Sample;
        EditorTitle.Text = item is null ? "Новые обои" : "Редактирование обоев";
        EditorPanel.Visibility = Visibility.Visible; filling = false; dirty = false; Buttons(); NameInput.Focus();
    }
    private void AddWallpaperClick(object sender, RoutedEventArgs e) => OpenEditor(null);
    private void EditWallpaperClick(object sender, RoutedEventArgs e) { if (selected is not null) OpenEditor(selected); }
    private void EditorChanged(object sender, TextChangedEventArgs e) { if (!filling && editing) dirty = true; }
    private async void CancelEditorClick(object sender, RoutedEventArgs e) { if (LeaveEditor() && selected is not null) await RunAsync(() => LoadPreviewAsync(selected.Code)); }
    private async void PreviewCodeClick(object sender, RoutedEventArgs e) => await RunAsync(async () => { await LoadPreviewAsync(CodeInput.Text); Status("Шейдер скомпилирован. Проверьте изображение в предпросмотре."); });
    private async void SaveWallpaperClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        Library.Validate(NameInput.Text, CodeInput.Text);
        if (Preview.Surface is null) throw new IOException("Предпросмотр ещё не готов.");
        await LoadPreviewAsync(CodeInput.Text);
        var item = library.Save(editingId, NameInput.Text, CodeInput.Text); dirty = false; LeaveEditor(); await RefreshLibraryAsync(item.Id);
        Status("Обои сохранены. Для применения изменений нажмите «На рабочий стол».");
    });
    private async void ApplyWallpaperClick(object sender, RoutedEventArgs e) => await RunAsync(async () => { if (selected is not null) { await wallpapers.ApplyAsync(selected.Id); RenderList(selected.Id); } });
    private async void StopWallpaperClick(object sender, RoutedEventArgs e) => await RunAsync(() => { wallpapers.Stop(); RenderList(selected?.Id); return Task.CompletedTask; });
    private async void DeleteWallpaperClick(object sender, RoutedEventArgs e)
    {
        if (selected is null || MessageBox.Show(this, $"Удалить обои «{selected.Name}» и их GLSL-файл?", "Удаление обоев", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async () => { var id = selected.Id; if (library.State.ActiveId == id) wallpapers.Stop(); library.Remove(id); await RefreshLibraryAsync(); Status("Обои удалены."); });
    }
    private void OpenFolderClick(object sender, RoutedEventArgs e) { try { SystemSettings.Open(library.ShaderDirectory); } catch (Exception error) { Status(error.Message, true); } }
    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (editing) { Status("Сначала сохраните или отмените изменения в редакторе."); return; }
        await RunAsync(async () => { await RefreshLibraryAsync(selected?.Id); Status("Коллекция обновлена из GLSL-файлов."); });
    }
    private void FillPreferences()
    {
        filling = true; var settings = library.State.Settings;
        MouseInput.IsChecked = settings.MouseEnabled; CoveredInput.IsChecked = settings.PauseWhenCovered; BatteryInput.IsChecked = settings.PauseOnBattery;
        FpsInput.SelectedIndex = Array.IndexOf(new[] { 15, 30, 60 }, settings.Fps); QualityInput.SelectedIndex = Array.IndexOf(new[] { .5, .75, 1 }, settings.RenderScale);
        try { StartupInput.IsChecked = SystemSettings.StartupEnabled; } catch (Exception error) { Status(error.Message, true); }
        filling = false;
    }
    private async void PlaybackChanged(object sender, RoutedEventArgs e)
    {
        if (filling || !ready) return;
        await RunAsync(() =>
        {
            try
            {
                library.Configure(new PlaybackSettings { MouseEnabled = MouseInput.IsChecked == true, PauseWhenCovered = CoveredInput.IsChecked == true, PauseOnBattery = BatteryInput.IsChecked == true,
                    Fps = new[] { 15, 30, 60 }[Math.Max(0, FpsInput.SelectedIndex)], RenderScale = new[] { .5, .75, 1 }[Math.Max(0, QualityInput.SelectedIndex)] });
                wallpapers.Refresh(); Status("Параметры сохранены."); return Task.CompletedTask;
            }
            finally { FillPreferences(); }
        });
    }
    private async void StartupClick(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        try { SystemSettings.StartupEnabled = StartupInput.IsChecked == true; Status("Настройка автозапуска сохранена."); }
        finally { FillPreferences(); } return Task.CompletedTask;
    });
    private SystemSettings PlatformSettings => system ?? throw new IOException("Не удалось прочитать резервные настройки панели. Исходный файл сохранён.");
    private void FillBehavior()
    {
        try
        {
            var settings = PlatformSettings.ReadTaskbar(); AlignmentInput.SelectedIndex = settings.Alignment; SearchInput.SelectedIndex = settings.Search;
            AutoHideInput.IsChecked = settings.AutoHide; TaskViewInput.IsChecked = settings.TaskView;
            LegacyPanel.Visibility = PlatformSettings.LegacyAvailable || PlatformSettings.CanRestoreLegacy ? Visibility.Visible : Visibility.Collapsed;
            var legacy = PlatformSettings.ReadLegacy(); LegacyHeight.Text = legacy.Height.ToString(); LegacyIcon.Text = legacy.Icon.ToString(); LegacyButton.Text = legacy.Button.ToString(); LegacySmallIcon.Text = legacy.SmallIcon.ToString(); LegacySmallButton.Text = legacy.SmallButton.ToString();
        }
        catch (Exception error) { Status(error.Message, true); }
    }
    private async void ApplyBehaviorClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var current = PlatformSettings.ReadTaskbar(); var next = current with { Alignment = AlignmentInput.SelectedIndex, Search = SearchInput.SelectedIndex, AutoHide = AutoHideInput.IsChecked == true, TaskView = TaskViewInput.IsChecked == true };
        try { Status(await Task.Run(() => PlatformSettings.Apply(next))); } finally { FillBehavior(); }
    });
    private async void RestoreBehaviorClick(object sender, RoutedEventArgs e) => await RunAsync(async () => { try { Status(await Task.Run(PlatformSettings.Restore)); } finally { FillBehavior(); } });
    private void UpdateAppearanceStatus() => AppearanceStatus.Text = appearance.Error ?? (appearance.Active ? "Оформление MagmPaper работает." : appearance.Available ? "Модуль установлен вместе с приложением." : "Нужна полная сборка для Windows 11 x64 с модулем панели.");
    private void FillAppearance()
    {
        var value = appearance.Settings; AppearanceEnabled.IsChecked = value.Enabled; HeightInput.Text = value.Height.ToString(); IconInput.Text = value.Icon.ToString(); ButtonInput.Text = value.Button.ToString();
        ColorInput.Text = value.Color; OpacityInput.Text = value.Opacity.ToString(); RadiusInput.Text = value.Radius.ToString(); HideWidgetsInput.IsChecked = value.HideWidgets; UpdateAppearanceStatus(); UpdateTaskbarPreview();
    }
    private void UpdateTaskbarPreview()
    {
        try
        {
            var value = ReadAppearance();
            var color = (Color)ColorConverter.ConvertFromString(value.Color);
            color.A = (byte)Math.Round(value.Opacity * 2.55);
            TaskbarSample.Background = new SolidColorBrush(color); TaskbarSample.Height = value.Height;
            TaskbarSample.CornerRadius = new CornerRadius(value.Radius);
            WidgetsSample.Visibility = value.HideWidgets ? Visibility.Collapsed : Visibility.Visible;
            TaskbarSampleIcons.Children.Clear();
            foreach (string tint in new[] { "#53A6FF", "#EABB58", "#6CC9B0", "#C295EB" })
                TaskbarSampleIcons.Children.Add(new Border { Width = value.Button, Height = value.Height, Child = new Border
                { Width = value.Icon, Height = value.Icon, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tint)) } });
        }
        catch (Exception error) when (error is InvalidDataException or FormatException) { /* Keep the last valid draft while typing. */ }
    }
    private static int Number(TextBox input) => int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : throw new InvalidDataException("Введите целое число в поле размера или прозрачности.");
    private AppearanceSettings ReadAppearance() => new AppearanceSettings { Enabled = AppearanceEnabled.IsChecked == true, Height = Number(HeightInput), Icon = Number(IconInput), Button = Number(ButtonInput),
        Color = ColorInput.Text.Trim(), Opacity = Number(OpacityInput), Radius = Number(RadiusInput), HideWidgets = HideWidgetsInput.IsChecked == true }.Validate();
    private async void ApplyAppearanceClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var next = ReadAppearance(); Status("Применение оформления…");
        try { await appearance.ApplyAsync(next); Status("Оформление панели сохранено."); } finally { FillAppearance(); }
    });
    private async void DisableAppearanceClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        try { await appearance.ApplyAsync(appearance.Settings with { Enabled = false }); Status("Исходное оформление Windows возвращено."); } finally { FillAppearance(); }
    });
    private void PresetClick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag as string; int[] sizes = tag == "compact" ? [36, 20, 36] : tag == "large" ? [64, 40, 52] : [48, 24, 44];
        HeightInput.Text = sizes[0].ToString(); IconInput.Text = sizes[1].ToString(); ButtonInput.Text = sizes[2].ToString();
    }
    private void ColorClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try { dialog.Color = global::System.Drawing.ColorTranslator.FromHtml(ColorInput.Text); } catch { }
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) ColorInput.Text = "#" + (dialog.Color.ToArgb() & 0xffffff).ToString("X6");
    }
    private async void ApplyLegacyClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        try { await PlatformSettings.ApplyLegacyAsync(new(Number(LegacyHeight), Number(LegacyIcon), Number(LegacyButton), Number(LegacySmallIcon), Number(LegacySmallButton))); Status("Размеры Windhawk сохранены."); } finally { FillBehavior(); }
    });
    private async void RestoreLegacyClick(object sender, RoutedEventArgs e) => await RunAsync(async () => { try { await PlatformSettings.ApplyLegacyAsync(new(), true); Status("Прежние размеры возвращены."); } finally { FillBehavior(); } });
    private void RefreshTaskbarClick(object sender, RoutedEventArgs e) { FillBehavior(); FillAppearance(); }
    private void LegacyHelpClick(object sender, RoutedEventArgs e) => SystemSettings.Open("https://windhawk.net/mods/taskbar-icon-size");
    private void OpenWindowsClick(object sender, RoutedEventArgs e) => SystemSettings.Open("ms-settings:taskbar");
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private void OnClosing(object? sender, CancelEventArgs e) { if (!Quitting) { e.Cancel = true; Hide(); } }
    internal bool ConfirmExit() => !dirty || MessageBox.Show(this, "В редакторе есть несохранённые изменения. Выйти из приложения?", "MagmPaper", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
