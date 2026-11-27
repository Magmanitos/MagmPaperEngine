using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using MagmPaper.Windows;

namespace MagmPaper;

public partial class MainWindow
{
    private readonly WindowsThemeSettings windowsTheme = new();
    private readonly WindowsWindowPalette windowPalette;
    private readonly SemaphoreSlim themeWrites = new(1);
    private bool fillingWindowsTheme, matchWindowsAccentToWallpaper;
    private string themeBackground = "#27253B", systemAccent = "#0078D4";
    private string? themeAccent;
    private WallpaperColors? themeWallpaperColors;
    private int accentRequest, themeTaskbarMode;

    private WallpaperColors ThemeColors => matchWindowsAccentToWallpaper && themeWallpaperColors is { } colors
        ? colors : new(themeBackground, themeAccent ?? systemAccent);

    private static string? ThemeHex(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit) ? value : null;

    private void RefreshWindowsTheme()
    {
        fillingWindowsTheme = true;
        try
        {
            var theme = windowsTheme.Read();
            systemAccent = theme.Accent;
            bool custom = windowPalette.IsApplied();
            int mode = custom ? 2 : WindowsWindowPalette.ContrastEnabled() ? 3 : theme.AppsLight ? 0 : 1;
            CurrentWindowsThemeOption.Visibility = mode == 3 ? Visibility.Visible : Visibility.Collapsed;
            WindowsBaseThemeInput.SelectedIndex = mode;
            WindowsCustomThemeHint.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            RestoreWindowsPaletteButton.Visibility = windowPalette.HasBackup ? Visibility.Visible : Visibility.Collapsed;
            WindowsPaletteSourceInput.SelectedIndex = matchWindowsAccentToWallpaper ? 1 : 0;
            WindowsTaskbarMaterialInput.SelectedIndex = themeTaskbarMode;
            WindowsSystemDarkInput.IsChecked = !theme.SystemLight;
            WindowsTransparencyInput.IsChecked = theme.Transparency;
            WindowsWindowAccentInput.IsChecked = theme.WindowAccent;
            WindowsShellAccentInput.IsChecked = theme.ShellAccent;
            WindowsShellAccentInput.IsEnabled = !theme.SystemLight;
            WindowsBackgroundRow.Visibility = custom || themeTaskbarMode != 0 ? Visibility.Visible : Visibility.Collapsed;
            WindowsAccentStatus.Visibility = matchWindowsAccentToWallpaper ? Visibility.Visible : Visibility.Collapsed;
            WindowsBackgroundButton.IsEnabled = WindowsAccentButton.IsEnabled = !matchWindowsAccentToWallpaper;
            UpdateWindowsThemePreview(custom, theme.AppsLight);

            var animation = WindowsVisualSettings.ReadAnimations();
            WindowsAnimationsInput.IsChecked = animation.ClientArea;
            WindowsMinimizeAnimationsInput.IsChecked = animation.Minimize;
            WindowsMenuMotionInput.SelectedIndex = (int)animation.Motion;
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Status(error.Message, true);
        }
        finally { fillingWindowsTheme = false; }
    }

    private void UpdateWindowsThemePreview(bool custom, bool light)
    {
        static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
        var colors = ThemeColors;
        WindowsBackgroundText.Text = colors.First;
        WindowsAccentText.Text = colors.Second;
        WindowsBackgroundSwatch.Background = Brush(colors.First);
        WindowsAccentSwatch.Background = Brush(colors.Second);
        var preview = WindowsWindowPalette.Preview(custom ? colors : colors with { First = light ? "#F2F2F2" : "#202020" });
        WindowsPaletteSample.Background = Brush(WindowsWindowPalette.Hex(preview.Background));
        WindowsPaletteSampleText.Foreground = Brush(WindowsWindowPalette.Hex(preview.Text));
        WindowsPaletteSampleSelection.Background = Brush(WindowsWindowPalette.Hex(preview.Accent));
        WindowsPaletteSampleAccentText.Foreground = Brush(WindowsWindowPalette.Hex(preview.AccentText));
    }

    private async void WindowsBaseThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        int mode = WindowsBaseThemeInput.SelectedIndex;
        if (!ready || fillingWindowsTheme || mode is < 0 or > 2) return;
        var colors = ThemeColors;
        await ApplyWindowsSettingAsync(() =>
        {
            if (mode == 2) windowPalette.Apply(colors);
            else
            {
                windowPalette.Restore();
                WindowsWindowPalette.UseStandardTheme();
                windowsTheme.Set(WindowsThemeSettings.Option.AppsLight, mode == 0);
            }
        });
    }

    private async void RestoreWindowsPaletteClick(object sender, RoutedEventArgs e) =>
        await ApplyWindowsSettingAsync(windowPalette.Restore);

    private async void WindowsThemeOptionClick(object sender, RoutedEventArgs e)
    {
        if (!ready || fillingWindowsTheme || sender is not CheckBox toggle ||
            toggle.Tag is not string tag || !Enum.TryParse(tag, out WindowsThemeSettings.Option option)) return;
        bool value = toggle.IsChecked == true;
        if (option is WindowsThemeSettings.Option.AppsLight or WindowsThemeSettings.Option.SystemLight) value = !value;
        await ApplyWindowsSettingAsync(() => windowsTheme.Set(option, value));
    }

    private async void WindowsAnimationClick(object sender, RoutedEventArgs e)
    {
        if (!ready || fillingWindowsTheme || sender is not CheckBox toggle) return;
        bool enabled = toggle.IsChecked == true;
        await ApplyWindowsSettingAsync(toggle == WindowsAnimationsInput
            ? () => WindowsVisualSettings.SetClientAnimations(enabled)
            : () => WindowsVisualSettings.SetMinimizeAnimations(enabled));
    }

    private async void WindowsMenuMotionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || fillingWindowsTheme || WindowsMenuMotionInput.SelectedIndex < 0) return;
        var motion = (WindowsVisualSettings.MenuMotion)WindowsMenuMotionInput.SelectedIndex;
        await ApplyWindowsSettingAsync(() => WindowsVisualSettings.SetMenuMotion(motion));
    }

    private async Task ApplyWindowsSettingAsync(Action change) => await RunAsync(async () =>
    {
        await themeWrites.WaitAsync();
        try
        {
            await Task.Run(change);
            Status("Настройка Windows сохранена.");
        }
        finally
        {
            themeWrites.Release();
            if (route == "windowTheme") RefreshWindowsTheme();
        }
    });

    private async void WindowsAccentClick(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || matchWindowsAccentToWallpaper) return;
        bool background = sender is FrameworkElement { Tag: "background" };
        var colors = ThemeColors;
        var picker = new Views.ColorPicker(background ? colors.First : colors.Second,
            background ? "Основной цвет темы" : "Цвет выделения темы") { Owner = this };
        TrackWindowFrame(picker);
        if (picker.ShowDialog() != true) return;
        if (background) themeBackground = picker.SelectedColor;
        else themeAccent = picker.SelectedColor;
        SaveWindowPreferences();
        await ApplyCurrentColorsAsync();
        ApplyLinkedTaskbarPalette();
    }

    private async void WindowsPaletteSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || fillingWindowsTheme || WindowsPaletteSourceInput.SelectedIndex < 0) return;
        matchWindowsAccentToWallpaper = WindowsPaletteSourceInput.SelectedIndex == 1;
        SaveWindowPreferences();
        RefreshWindowsTheme();
        if (matchWindowsAccentToWallpaper) await RefreshWindowsWallpaperAccentAsync();
        else
        {
            ++accentRequest;
            await ApplyCurrentColorsAsync();
            ApplyLinkedTaskbarPalette();
        }
    }

    private Task ApplyCurrentColorsAsync()
    {
        var colors = ThemeColors;
        return ApplyWindowsSettingAsync(() => ApplyThemeColors(colors));
    }

    private void ApplyThemeColors(WallpaperColors colors)
    {
        if (windowPalette.IsApplied()) windowPalette.Apply(colors);
        else windowsTheme.SetAccent(colors.Second);
    }

    private async Task RefreshWindowsWallpaperAccentAsync()
    {
        int request = ++accentRequest;
        if (!ready || !matchWindowsAccentToWallpaper) return;
        WindowsAccentStatus.Text = "Подбираем цвета обоев…";
        try
        {
            var colors = await wallpapers.ReadColorsAsync() ?? await Task.Run(WindowsVisualSettings.ReadWallpaperColors);
            if (Quitting || request != accentRequest) return;
            themeWallpaperColors = colors;
            WindowsAccentStatus.Text = colors is null ? "Используются ручные цвета. Палитра обоев пока недоступна." : "Цвета из текущих обоев.";
            await themeWrites.WaitAsync();
            try
            {
                if (Quitting || request != accentRequest) return;
                var palette = ThemeColors;
                await Task.Run(() => ApplyThemeColors(palette));
            }
            finally { themeWrites.Release(); }

            if (Quitting || request != accentRequest) return;
            if (route == "windowTheme") RefreshWindowsTheme();
            ApplyLinkedTaskbarPalette();
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (request != accentRequest || Quitting) return;
            WindowsAccentStatus.Text = "Не удалось применить цвета обоев.";
            Status(error.Message, true);
        }
    }

    private void WindowsThemeTaskbarChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || fillingWindowsTheme || WindowsTaskbarMaterialInput.SelectedIndex < 0) return;
        themeTaskbarMode = WindowsTaskbarMaterialInput.SelectedIndex;
        SaveWindowPreferences();
        RefreshWindowsTheme();
        ApplyLinkedTaskbarPalette();
    }

    private void ApplyLinkedTaskbarPalette()
    {
        if (themeTaskbarMode == 0) return;
        var colors = ThemeColors;
        fillingAppearance = true;
        AppearanceEnabled.IsChecked = true;
        TaskbarMaterialInput.SelectedIndex = themeTaskbarMode;
        WallpaperColorsInput.IsChecked = false;
        taskbarColor = colors.First;
        glassColor = colors.Second;
        fillingAppearance = false;
        QueueAppearance();
    }

    private void UnlinkThemeTaskbar()
    {
        if (themeTaskbarMode == 0) return;
        themeTaskbarMode = 0;
        SaveWindowPreferences();
    }

    private void WindowSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.Desktop or UserPreferenceCategory.General or
            UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (Quitting) return;
            if (e.Category == UserPreferenceCategory.Desktop && matchWindowsAccentToWallpaper)
                _ = RefreshWindowsWallpaperAccentAsync();
            if (route == "windowTheme" && !busy && themeWrites.CurrentCount != 0) RefreshWindowsTheme();
        });
    }

    private void OpenWindowsThemeSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string target }) OpenTarget(target);
    }

    private void OpenWindowsStartClick(object sender, RoutedEventArgs e)
    {
        if (Win32.FindWindow("Shell_TrayWnd", null) == 0)
        {
            Status("Меню «Пуск» недоступно.", true);
            return;
        }
        const uint SystemCommand = 0x0112;
        const uint OpenStart = 0xf130;
        var hwnd = new WindowInteropHelper(this).Handle;
        _ = Win32.SendMessageTimeout(hwnd, SystemCommand, OpenStart, 0, 2, 300, out _);
    }
}
