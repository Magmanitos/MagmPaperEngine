using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly Views.WindowFrameBlur windowFrames = new();
    private Views.WindowBlurOptions windowBlur = new();
    private bool fillingWindowBlur;
    private readonly DispatcherTimer windowBlurSaveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly bool allowStartupChangelog;
    private bool showUpdateNotesOnStartup = true;
    private Views.UpdateNotes? updateNotes;
    private string route = "overview";
    private string? editingId;
    private bool editing, dirty, filling, selecting, busy, ready;
    private bool fillingBehavior, fillingAppearance;
    private AppearanceSettings? pendingAppearance;
    private Task appearanceUpdates = Task.CompletedTask;
    private Task behaviorUpdate = Task.CompletedTask;
    private Wallpaper? selected;
    private RenderSurface? loadedPreview;
    private string? loadedPreviewCode;
    internal bool Quitting { get; set; }
    internal bool Busy => busy;
    internal Library Library => library;
    internal RenderSurface? PreviewSurface => Preview.Surface;
    internal string CurrentRoute => route;
    private const string Sample = "precision mediump float;\nuniform float time;\nuniform vec2 resolution;\nuniform vec2 mouse;\n\nvoid main() {\n    vec2 uv = gl_FragCoord.xy / resolution;\n    gl_FragColor = vec4(uv, 0.5 + 0.5 * sin(time), 1.0);\n}\n";

    internal MainWindow(Library library, WallpaperService wallpapers, AppearanceService appearance, bool allowStartupChangelog)
    {
        this.library = library; this.wallpapers = wallpapers; this.appearance = appearance;
        this.allowStartupChangelog = allowStartupChangelog;
        uiFile = Path.Combine(library.DirectoryPath, "ui.json");
        InitializeComponent();
        TrackWindowFrame(this);
        windowBlurSaveTimer.Tick += (_, _) => SaveWindowPreferences();
        CursorsPage.Initialize(library.DirectoryPath);
        AppVersionLabel.Text = Views.UpdateNotes.VersionLabel;
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
            try
            {
                if (File.Exists(uiFile))
                {
                    var preferences = JsonNode.Parse(File.ReadAllText(uiFile));
                    saved = preferences?["route"]?.GetValue<string>() ?? saved;
                    showUpdateNotesOnStartup = preferences?["showUpdateNotesOnStartup"]?.GetValue<bool>() ?? true;
                    windowBlur = preferences?["windowBlur"]?.Deserialize<Views.WindowBlurOptions>(DataFiles.Json)?.Normalize() ?? new();
                }
            }
            catch { }
            ShowUpdateNotesInput.IsChecked = showUpdateNotesOnStartup;
            FillWindowBlur();
            Navigate(saved);
            await RunAsync(() => RefreshLibraryAsync());
        };
        ContentRendered += ShowStartupNotes;
        StateChanged += (_, _) => { SyncPreview(); updateNotes?.PlaceNextToOwner(); };
        IsVisibleChanged += (_, _) => { SyncPreview(); if (!IsVisible && !Quitting) updateNotes?.Close(); };
        LocationChanged += (_, _) => updateNotes?.PlaceNextToOwner();
        SizeChanged += (_, _) => updateNotes?.PlaceNextToOwner();
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
        if (busy) return; busy = true; BusyIndicator.IsIndeterminate = true; BusyIndicator.Visibility = Visibility.Visible; PageContainer.IsEnabled = false;
        try { await task(); }
        catch (Exception error) { Status(error.Message, true); }
        finally { busy = false; BusyIndicator.IsIndeterminate = false; PageContainer.IsEnabled = true; BusyIndicator.Visibility = Visibility.Collapsed; Buttons(); }
    }
    private void Buttons()
    {
        ApplyWallpaperButton.IsEnabled = EditWallpaperButton.IsEnabled = DeleteWallpaperButton.IsEnabled = selected is not null && !editing;
    }
    internal void Navigate(string destination)
    {
        if (destination is not ("overview" or "wallpapers" or "taskbar" or "cursors" or "settings")) destination = "overview";
        route = destination; SyncPreview();
        OverviewPage.Visibility = route == "overview" ? Visibility.Visible : Visibility.Collapsed;
        WallpapersPage.Visibility = route == "wallpapers" ? Visibility.Visible : Visibility.Collapsed;
        TaskbarPage.Visibility = route == "taskbar" ? Visibility.Visible : Visibility.Collapsed;
        CursorsPage.Visibility = route == "cursors" ? Visibility.Visible : Visibility.Collapsed;
        NavCursors.IsChecked = route == "cursors";
        SettingsPage.Visibility = route == "settings" ? Visibility.Visible : Visibility.Collapsed;
        NavOverview.IsChecked = route == "overview"; NavWallpapers.IsChecked = route == "wallpapers"; NavTaskbar.IsChecked = route == "taskbar"; NavSettings.IsChecked = route == "settings";
        SaveWindowPreferences();
        if (route == "taskbar") { FillBehavior(); UpdateAppearanceStatus(); }
        if (route == "settings") FillPreferences();
        if (route == "cursors") CursorsPage.Refresh();
        if (route == "wallpapers" && !editing && !busy) _ = RunAsync(() => RefreshLibraryAsync(selected?.Id));
    }
    private void SaveWindowPreferences()
    {
        windowBlurSaveTimer.Stop();
        try { DataFiles.Write(uiFile, new { route, showUpdateNotesOnStartup, windowBlur }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status(error.Message, true); }
    }
    internal void TrackWindowFrame(Window window) => windowFrames.Track(window);
    private void FillWindowBlur()
    {
        fillingWindowBlur = true;
        WindowBlurInput.IsChecked = windowBlur.Enabled;
        WindowBlurColorValue.Text = windowBlur.Color;
        WindowBlurRadius.Value = windowBlur.Radius;
        WindowBlurIntensity.Value = windowBlur.Intensity;
        fillingWindowBlur = false;
        UpdateWindowBlur();
    }
    private void UpdateWindowBlur()
    {
        WindowBlurControls.Visibility = windowBlur.Enabled ? Visibility.Visible : Visibility.Collapsed;
        WindowBlurSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(windowBlur.Color));
        windowFrames.Configure(windowBlur);
    }
    private void WindowBlurChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || fillingWindowBlur) return;
        windowBlur = new(WindowBlurInput.IsChecked == true, WindowBlurColorValue.Text,
            Number(WindowBlurRadius), Number(WindowBlurIntensity));
        UpdateWindowBlur();
        if (sender is Slider)
        {
            windowBlurSaveTimer.Stop();
            windowBlurSaveTimer.Start();
        }
        else SaveWindowPreferences();
    }
    private void WindowBlurColorClick(object sender, RoutedEventArgs e)
    {
        var picker = new Views.ColorPicker(windowBlur.Color, "Цвет контура") { Owner = this };
        TrackWindowFrame(picker);
        if (picker.ShowDialog() != true) return;
        WindowBlurColorValue.Text = picker.SelectedColor;
        WindowBlurChanged(sender, e);
    }
    private void ShowUpdateNotesChanged(object sender, RoutedEventArgs e)
    {
        showUpdateNotesOnStartup = ShowUpdateNotesInput.IsChecked == true;
        SaveWindowPreferences();
        if (!showUpdateNotesOnStartup) updateNotes?.Close();
    }
    private void ShowStartupNotes(object? sender, EventArgs e)
    {
        ContentRendered -= ShowStartupNotes;
        if (!allowStartupChangelog) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (showUpdateNotesOnStartup) OpenUpdateNotes();
        }, DispatcherPriority.ContextIdle);
    }
    private void OpenUpdateNotesClick(object sender, RoutedEventArgs e) => OpenUpdateNotes();
    private void OpenUpdateNotes()
    {
        if (updateNotes is not null || Quitting || !IsVisible) return;
        try
        {
            updateNotes = new Views.UpdateNotes { Owner = this };
            updateNotes.Closed += (_, _) => updateNotes = null;
            updateNotes.ShowNextToOwner();
        }
        catch (Exception error)
        {
            updateNotes?.Close();
            updateNotes = null;
            Status(error.Message, true);
        }
    }
    private void NavigateClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is string page) Navigate(page); }
    private void SyncPreview() => Preview.SetActive(route == "wallpapers" && IsVisible && WindowState != WindowState.Minimized);
    private void ShowPlayback() => PauseText.Text = wallpapers.PauseReason is { } reason ? "Обои на паузе: " + reason : "";
    private void RenderList(string? id)
    {
        selecting = true;
        var rows = library.State.Items.Select(x => new WallpaperRow(x, wallpapers.RunningId == x.Id)).ToArray();
        if (WallpaperList.ItemsSource is WallpaperRow[] previous && previous.SequenceEqual(rows)) rows = previous;
        else WallpaperList.ItemsSource = rows;
        WallpaperList.SelectedItem = rows.FirstOrDefault(x => x.Item.Id == id) ?? rows.FirstOrDefault();
        selected = (WallpaperList.SelectedItem as WallpaperRow)?.Item;
        WallpaperTitle.Text = selected?.Name ?? "Выберите обои"; CollectionCount.Text = $"{rows.Length} в коллекции";
        selecting = false; Buttons();
    }
    private async Task RefreshLibraryAsync(string? id = null)
    {
        library.Refresh(); wallpapers.Refresh(); RenderList(id ?? selected?.Id); ShowPlayback();
        if (selected is not null && route == "wallpapers") await LoadPreviewAsync(selected.Code);
    }
    private async Task LoadPreviewAsync(string code, bool force = false)
    {
        if (Preview.Surface is not { } surface) return;
        if (force || !ReferenceEquals(loadedPreview, surface) || loadedPreviewCode != code)
        {
            await surface.LoadAsync(code); loadedPreview = surface; loadedPreviewCode = code;
        }
        SyncPreview();
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
    private async void PreviewCodeClick(object sender, RoutedEventArgs e) => await RunAsync(async () => { await LoadPreviewAsync(CodeInput.Text, force: true); Status("Шейдер скомпилирован. Проверьте изображение в предпросмотре."); });
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
    private void OpenTarget(string target) { try { SystemSettings.Open(target); } catch (Exception error) { Status(error.Message, true); } }
    internal void OpenCollectionFolder() => OpenTarget(library.ShaderDirectory);
    private void OpenFolderClick(object sender, RoutedEventArgs e) => OpenCollectionFolder();
    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (editing) { Status("Сначала сохраните или отмените изменения в редакторе."); return; }
        await RunAsync(async () => { await RefreshLibraryAsync(selected?.Id); Status("Коллекция обновлена из GLSL-файлов."); });
    }
    private void FillPreferences()
    {
        filling = true; var settings = library.State.Settings;
        MonitorFpsOption.Content = $"По частоте монитора · {Win32.RefreshRate()} Гц";
        MouseInput.IsChecked = settings.MouseEnabled; CoveredInput.IsChecked = settings.PauseWhenCovered; BatteryInput.IsChecked = settings.PauseOnBattery;
        FpsInput.SelectedIndex = Array.IndexOf(new[] { 15, 30, 60, 120, 144, 165, 240, 0 }, settings.Fps); QualityInput.SelectedIndex = Array.IndexOf(new[] { .5, .75, 1 }, settings.RenderScale);
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
                    Fps = new[] { 15, 30, 60, 120, 144, 165, 240, 0 }[Math.Max(0, FpsInput.SelectedIndex)], RenderScale = new[] { .5, .75, 1 }[Math.Max(0, QualityInput.SelectedIndex)] });
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
        fillingBehavior = true;
        try
        {
            var settings = PlatformSettings.ReadTaskbar();
            AlignmentInput.SelectedIndex = settings.Alignment;
            SearchInput.SelectedIndex = settings.Search;
            AutoHideInput.IsChecked = settings.AutoHide;
            TaskViewInput.IsChecked = settings.TaskView;
            LegacyPanel.Visibility = PlatformSettings.LegacyAvailable || PlatformSettings.CanRestoreLegacy ? Visibility.Visible : Visibility.Collapsed;
            if (LegacyPanel.Visibility == Visibility.Visible)
            {
                var legacy = PlatformSettings.ReadLegacy();
                LegacyHeight.Value = legacy.Height;
                LegacyIcon.Value = legacy.Icon;
                LegacyButton.Value = legacy.Button;
                LegacySmallIcon.Value = legacy.SmallIcon;
                LegacySmallButton.Value = legacy.SmallButton;
            }
        }
        catch (Exception error) { Status(error.Message, true); }
        finally { fillingBehavior = false; }
    }
    private void BehaviorChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || fillingBehavior || sender is not FrameworkElement { Tag: string key }) return;
        JsonNode? value = sender switch
        {
            ComboBox { SelectedIndex: >= 0 } choice => JsonValue.Create(choice.SelectedIndex),
            CheckBox toggle when key == "autoHide" => JsonValue.Create(toggle.IsChecked == true),
            CheckBox toggle => JsonValue.Create(toggle.IsChecked == true ? 1 : 0),
            _ => null
        };
        if (value is null) return;
        behaviorUpdate = RunAsync(async () =>
        {
            try { Status(await Task.Run(() => PlatformSettings.Apply(key, value))); }
            finally { FillBehavior(); }
        });
    }
    private async void RestoreBehaviorClick(object sender, RoutedEventArgs e) => await RunAsync(async () => { try { Status(await Task.Run(PlatformSettings.Restore)); } finally { FillBehavior(); } });

    private void UpdateAppearanceStatus()
    {
        AppearanceStatus.Text = pendingAppearance is not null ? "Обновление оформления…"
            : appearance.Error ?? (appearance.Active ? "Оформление MagmPaper включено."
            : appearance.Available ? "Оформление MagmPaper выключено." : "Нужна полная сборка для Windows 11 x64 с модулем панели.");
    }
    private void FillAppearance()
    {
        var value = appearance.Settings;
        fillingAppearance = true;
        AppearanceEnabled.IsChecked = value.Enabled;
        SetAppearanceSizes(value.Height, value.Icon, value.Button);
        ColorValue.Text = value.Color;
        GlassInput.IsChecked = value.Glass;
        GlassColorValue.Text = value.GlassColor;
        OpacityInput.Value = value.Opacity;
        RadiusInput.Value = value.Radius;
        HideWidgetsInput.IsChecked = value.HideWidgets;
        fillingAppearance = false;
        UpdateAppearanceStatus();
        UpdateTaskbarPreview(value);
    }
    private void SetAppearanceSizes(int height, int icon, int button)
    {
        HeightInput.Value = height;
        int maxIcon = Math.Min(64, height - 8);
        icon = Math.Min(icon, maxIcon);
        // Set values as well as constraints so that relaxing a bound does not
        // restore a previously coerced value.
        IconInput.Maximum = maxIcon;
        IconInput.Value = icon;
        ButtonInput.Minimum = Math.Max(28, icon);
        ButtonInput.Value = Math.Max(button, icon);
    }
    private void UpdateTaskbarPreview(AppearanceSettings value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value.Color);
        var end = (Color)ColorConverter.ConvertFromString(value.GlassColor);
        ColorSwatch.Background = new SolidColorBrush(color);
        GlassSwatch.Background = new SolidColorBrush(end);
        GlassColors.Visibility = value.Glass ? Visibility.Visible : Visibility.Collapsed;
        color.A = (byte)((value.Opacity * 255 + 50) / 100);
        if (value.Glass)
        {
            static byte Shine(byte channel) => (byte)((channel * 3 + 255) / 4);
            var highlight = Color.FromArgb(color.A, Shine(color.R), Shine(color.G), Shine(color.B));
            end.A = (byte)(color.A * 2 / 3);
            color.A = (byte)(color.A * 3 / 4);
            TaskbarSample.Background = new LinearGradientBrush(
                new GradientStopCollection { new(highlight, 0), new(color, .38), new(end, 1) },
                new Point(0, 0), new Point(.85, 1));
        }
        else TaskbarSample.Background = new SolidColorBrush(color);
        TaskbarSample.Height = value.Height;
        TaskbarSample.CornerRadius = new CornerRadius(value.Radius);
        WidgetsSample.Visibility = value.HideWidgets ? Visibility.Collapsed : Visibility.Visible;
        foreach (Border button in TaskbarSampleIcons.Children)
        {
            button.Width = value.Button;
            if (button.Child is Border icon) icon.Width = icon.Height = value.Icon;
        }
    }
    private static int Number(Slider input) => (int)Math.Round(input.Value);
    private AppearanceSettings ReadAppearance() => new()
    {
        Enabled = AppearanceEnabled.IsChecked == true,
        Height = Number(HeightInput),
        Icon = Number(IconInput),
        Button = Number(ButtonInput),
        Color = ColorValue.Text,
        Glass = GlassInput.IsChecked == true,
        GlassColor = GlassColorValue.Text,
        Opacity = Number(OpacityInput),
        Radius = Number(RadiusInput),
        HideWidgets = HideWidgetsInput.IsChecked == true
    };
    private void AppearanceChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || fillingAppearance) return;
        if (ReferenceEquals(sender, HeightInput) || ReferenceEquals(sender, IconInput))
        {
            fillingAppearance = true;
            SetAppearanceSizes(Number(HeightInput), Number(IconInput), Number(ButtonInput));
            fillingAppearance = false;
        }
        QueueAppearance();
    }
    private void QueueAppearance()
    {
        var value = ReadAppearance();
        UpdateTaskbarPreview(value);
        pendingAppearance = value;
        UpdateAppearanceStatus();
        if (appearanceUpdates.IsCompleted) appearanceUpdates = ApplyAppearanceUpdatesAsync();
    }
    private async Task ApplyAppearanceUpdatesAsync()
    {
        while (pendingAppearance is not null)
        {
            // Sample the latest value during a drag, with at most one native
            // request in flight. The final value is sent even when dragging stops.
            await Task.Delay(40);
            var next = pendingAppearance!;
            pendingAppearance = null;
            if (next == appearance.Settings && appearance.Error is null && next.Enabled == appearance.Active)
            {
                UpdateAppearanceStatus();
                continue;
            }

            AppearanceStatus.Text = "Обновление оформления…";
            string? previousError = appearance.Error;
            try
            {
                await appearance.ApplyAsync(next);
                if (previousError is not null && StatusText.Text == previousError)
                    Status("Настройки оформления сохранены.");
            }
            catch (Exception error)
            {
                if (pendingAppearance is null)
                {
                    FillAppearance();
                    Status(error.Message, true);
                }
            }
            UpdateAppearanceStatus();
        }
    }
    internal Task FinishTaskbarUpdatesAsync() => Task.WhenAll(appearanceUpdates, behaviorUpdate);
    private void PresetClick(object sender, RoutedEventArgs e)
    {
        var (height, icon, button) = ((Button)sender).Tag switch
        {
            "compact" => (36, 20, 36),
            "large" => (64, 40, 52),
            _ => (48, 24, 44)
        };
        fillingAppearance = true;
        SetAppearanceSizes(height, icon, button);
        fillingAppearance = false;
        QueueAppearance();
    }
    private void ColorClick(object sender, RoutedEventArgs e) => PickTaskbarColor(ColorValue);
    private void GlassColorClick(object sender, RoutedEventArgs e) => PickTaskbarColor(GlassColorValue);
    private void PickTaskbarColor(TextBlock value)
    {
        var dialog = new Views.ColorPicker(value.Text) { Owner = this };
        TrackWindowFrame(dialog);
        if (dialog.ShowDialog() != true) return;
        value.Text = dialog.SelectedColor;
        QueueAppearance();
    }
    private async void ApplyLegacyClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        try { await PlatformSettings.ApplyLegacyAsync(new(Number(LegacyHeight), Number(LegacyIcon), Number(LegacyButton), Number(LegacySmallIcon), Number(LegacySmallButton))); Status("Размеры Windhawk сохранены."); } finally { FillBehavior(); }
    });
    private async void RestoreLegacyClick(object sender, RoutedEventArgs e) => await RunAsync(async () => { try { await PlatformSettings.ApplyLegacyAsync(new(), true); Status("Прежние размеры возвращены."); } finally { FillBehavior(); } });
    private void RefreshTaskbarClick(object sender, RoutedEventArgs e) { FillBehavior(); if (appearanceUpdates.IsCompleted) FillAppearance(); }
    private void LegacyHelpClick(object sender, RoutedEventArgs e) => OpenTarget("https://windhawk.net/mods/taskbar-icon-size");
    private void OpenWindowsClick(object sender, RoutedEventArgs e) => OpenTarget("ms-settings:taskbar");
    private void CopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(wallpapers.DiagnosticReport()); Status("Диагностика скопирована. Код и названия ваших обоев в неё не входят."); }
        catch (Exception error) { Status(error.Message, true); }
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (windowBlurSaveTimer.IsEnabled) SaveWindowPreferences();
        if (!Quitting) { e.Cancel = true; Hide(); }
    }
    internal bool ConfirmExit() => !dirty || MessageBox.Show(this, "В редакторе есть несохранённые изменения. Выйти из приложения?", "MagmPaper", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
