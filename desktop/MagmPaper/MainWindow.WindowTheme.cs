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
    private readonly SemaphoreSlim themeWrites = new(1);
    private bool fillingWindowsTheme, matchWindowsAccentToWallpaper;
    private int accentRequest;

    private void RefreshWindowsTheme()
    {
        fillingWindowsTheme = true;
        try
        {
            var theme = windowsTheme.Read();
            WindowsAppsDarkInput.IsChecked = !theme.AppsLight;
            WindowsSystemDarkInput.IsChecked = !theme.SystemLight;
            WindowsTransparencyInput.IsChecked = theme.Transparency;
            WindowsWindowAccentInput.IsChecked = theme.WindowAccent;
            WindowsShellAccentInput.IsChecked = theme.ShellAccent;
            WindowsShellAccentInput.IsEnabled = !theme.SystemLight;
            WindowsAccentText.Text = theme.Accent;
            WindowsAccentSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Accent));
            WindowsWallpaperAccentInput.IsChecked = matchWindowsAccentToWallpaper;
            WindowsAccentButton.IsEnabled = !matchWindowsAccentToWallpaper;
            WindowsAccentStatus.Visibility = matchWindowsAccentToWallpaper ? Visibility.Visible : Visibility.Collapsed;

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
        var picker = new Views.ColorPicker(WindowsAccentText.Text, "Цветовой акцент Windows") { Owner = this };
        TrackWindowFrame(picker);
        if (picker.ShowDialog() == true)
            await ApplyWindowsSettingAsync(() => windowsTheme.SetAccent(picker.SelectedColor));
    }

    private async void WindowsWallpaperAccentClick(object sender, RoutedEventArgs e)
    {
        if (!ready || fillingWindowsTheme) return;
        matchWindowsAccentToWallpaper = WindowsWallpaperAccentInput.IsChecked == true;
        SaveWindowPreferences();
        RefreshWindowsTheme();
        await RefreshWindowsWallpaperAccentAsync();
    }

    private async Task RefreshWindowsWallpaperAccentAsync()
    {
        int request = ++accentRequest;
        if (!ready || !matchWindowsAccentToWallpaper) return;
        WindowsAccentStatus.Text = "Подбираем цвет обоев…";
        try
        {
            var colors = await wallpapers.ReadColorsAsync() ?? await Task.Run(WindowsVisualSettings.ReadWallpaperColors);
            if (Quitting || request != accentRequest) return;
            if (colors is null)
            {
                WindowsAccentStatus.Text = "Цвет станет доступен, когда Windows или MagmPaper покажет обои.";
                return;
            }

            await themeWrites.WaitAsync();
            try
            {
                if (Quitting || request != accentRequest) return;
                await Task.Run(() => windowsTheme.SetAccent(colors.First));
            }
            finally { themeWrites.Release(); }

            if (Quitting || request != accentRequest) return;
            WindowsAccentStatus.Text = "Системный акцент из текущих обоев.";
            if (route == "windowTheme") RefreshWindowsTheme();
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (request != accentRequest || Quitting) return;
            WindowsAccentStatus.Text = "Не удалось установить цвет обоев.";
            Status(error.Message, true);
        }
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
            if (route == "windowTheme" && !busy) RefreshWindowsTheme();
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
