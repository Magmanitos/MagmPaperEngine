using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MagmPaper.Views;
using MagmPaper.Windows;

namespace MagmPaper;

public partial class MainWindow
{
    private readonly WindowThemeFrames themedFrames = new();
    private readonly WindowsFrameTheme windowsFrames;
    private WindowThemeSettings windowTheme = new();
    private WallpaperColors? windowColors;
    private int windowPaletteRequest;
    private bool fillingWindowTheme;

    private void FillWindowTheme()
    {
        fillingWindowTheme = true;
        WindowThemeEnabled.IsChecked = windowTheme.Enabled;
        WindowMaterialInput.SelectedIndex = (int)windowTheme.Material;
        WindowWallpaperInput.IsChecked = windowTheme.MatchWallpaper;
        WindowRadiusInput.Value = windowTheme.Radius;
        WindowOpacityInput.Value = windowTheme.Opacity;
        WindowGlassOutlineInput.IsChecked = windowTheme.GlassOutline;
        WindowsFramesInput.IsChecked = windowTheme.StyleWindowsFrames;
        WindowsCornersInput.SelectedIndex = (int)windowTheme.WindowsCorners;
        WindowsBackdropInput.SelectedIndex = windowTheme.WindowsBackdrop switch
        {
            WindowsWindowBackdrop.Mica => 1, WindowsWindowBackdrop.Acrylic => 2, _ => 0
        };
        WindowMotionInput.SelectedIndex = (int)windowTheme.Motion;
        WindowMotionDurationInput.Value = windowTheme.Duration;
        fillingWindowTheme = false;
        SystemEvents.UserPreferenceChanged += WindowSystemPreferenceChanged;
        UpdateWindowTheme();
    }

    private void WindowThemeChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || fillingWindowTheme || WindowMaterialInput.SelectedIndex < 0 ||
            WindowMotionInput.SelectedIndex < 0 || WindowsCornersInput.SelectedIndex < 0 || WindowsBackdropInput.SelectedIndex < 0) return;

        bool hadWallpaper = windowTheme.MatchWallpaper;
        windowTheme = windowTheme with
        {
            Enabled = WindowThemeEnabled.IsChecked == true,
            Material = (WindowMaterial)WindowMaterialInput.SelectedIndex,
            MatchWallpaper = WindowWallpaperInput.IsChecked == true,
            Radius = Number(WindowRadiusInput),
            Opacity = Number(WindowOpacityInput),
            GlassOutline = WindowGlassOutlineInput.IsChecked == true,
            StyleWindowsFrames = WindowsFramesInput.IsChecked == true,
            WindowsCorners = (WindowCorner)WindowsCornersInput.SelectedIndex,
            WindowsBackdrop = WindowsBackdropInput.SelectedIndex switch
            {
                1 => WindowsWindowBackdrop.Mica, 2 => WindowsWindowBackdrop.Acrylic, _ => WindowsWindowBackdrop.System
            },
            Motion = (WindowOpeningMotion)WindowMotionInput.SelectedIndex,
            Duration = Number(WindowMotionDurationInput)
        };
        UpdateWindowTheme();
        if (hadWallpaper != windowTheme.MatchWallpaper) _ = RefreshWindowThemeColorsAsync();
        if (sender is Slider) { windowBlurSaveTimer.Stop(); windowBlurSaveTimer.Start(); }
        else SaveWindowPreferences();
    }

    private void UpdateWindowTheme()
    {
        var (first, second) = windowTheme.Colors(windowColors);
        var a = (Color)ColorConverter.ConvertFromString(first);
        var b = (Color)ColorConverter.ConvertFromString(second);
        WindowFirstText.Text = first;
        WindowSecondText.Text = second;
        WindowFirstSwatch.Background = new SolidColorBrush(a);
        WindowSecondSwatch.Background = new SolidColorBrush(b);
        bool matched = windowTheme.MatchWallpaper && windowColors is not null;
        WindowFirstButton.IsEnabled = WindowSecondButton.IsEnabled = !matched;
        WindowPaletteStatus.Visibility = windowTheme.MatchWallpaper ? Visibility.Visible : Visibility.Collapsed;
        WindowPaletteStatus.Text = matched ? "Цвета из текущих обоев." : "Используются ручные цвета, пока палитра обоев недоступна.";
        WindowSecondRow.Visibility = windowTheme.Material == WindowMaterial.Solid ? Visibility.Collapsed : Visibility.Visible;
        WindowGlassOutlineInput.Visibility = windowTheme.Material == WindowMaterial.Glass ? Visibility.Visible : Visibility.Collapsed;
        WindowMotionDurationInput.IsEnabled = windowTheme.Motion != WindowOpeningMotion.Off;
        WindowMotionPreviewButton.IsEnabled = WindowMotionDurationInput.IsEnabled && SystemParameters.ClientAreaAnimation;
        WindowsCornersInput.IsEnabled = windowTheme.StyleWindowsFrames;
        WindowsBackdropInput.IsEnabled = windowTheme.StyleWindowsFrames;
        WindowsFramesStatus.Visibility = windowTheme.StyleWindowsFrames ? Visibility.Visible : Visibility.Collapsed;

        var background = ((SolidColorBrush)FindResource("ChromeBrush")).Color;
        var material = WindowThemePaint.Create(windowTheme, a, b, background);
        WindowThemeSample.Background = material;
        WindowThemeSample.CornerRadius = new(windowTheme.Radius);
        WindowThemeSample.Opacity = windowTheme.Opacity / 100d;
        var applied = SystemParameters.HighContrast ? windowTheme with { Enabled = false, Motion = WindowOpeningMotion.Off } : windowTheme;
        themedFrames.Configure(applied, material);
        windowFrames.Configure(windowBlur, applied.Enabled ? windowTheme.Radius : null);

        static uint ColorRef(Color color) => (uint)(color.R | color.G << 8 | color.B << 16);
        bool darkText = WindowThemePaint.Luminance(a) > .18;
        windowsFrames.Configure(windowTheme.StyleWindowsFrames && !SystemParameters.HighContrast
            ? new(ColorRef(a), darkText ? 0x101010u : 0xffffffu, windowTheme.WindowsCorners, windowTheme.WindowsBackdrop) : null);
    }

    private async Task RefreshWindowThemeColorsAsync()
    {
        int request = ++windowPaletteRequest;
        WallpaperColors? resolved = null;
        if (windowTheme.MatchWallpaper)
        {
            resolved = await wallpapers.ReadColorsAsync();
            resolved ??= await Task.Run(WindowsVisualSettings.ReadWallpaperColors);
        }
        if (request != windowPaletteRequest || Quitting || resolved == windowColors) return;
        windowColors = resolved;
        UpdateWindowTheme();
    }

    private void WindowSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.Desktop or UserPreferenceCategory.General or
            UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (Quitting) return;
            if (e.Category is UserPreferenceCategory.Desktop && windowTheme.MatchWallpaper) _ = RefreshWindowThemeColorsAsync();
            UpdateWindowTheme();
            if (route == "windowTheme") ReadWindowsAnimations();
        });
    }

    private void WindowColorClick(object sender, RoutedEventArgs e)
    {
        bool second = sender is FrameworkElement { Tag: "second" };
        var picker = new Views.ColorPicker(second ? windowTheme.Second : windowTheme.First,
            second ? "Второй цвет окон" : "Основной цвет окон") { Owner = this };
        TrackWindowFrame(picker);
        if (picker.ShowDialog() != true) return;
        windowTheme = second ? windowTheme with { Second = picker.SelectedColor } : windowTheme with { First = picker.SelectedColor };
        UpdateWindowTheme();
        SaveWindowPreferences();
    }

    private void WindowMotionPreviewClick(object sender, RoutedEventArgs e) => WindowThemePaint.Play(WindowThemeSample, windowTheme);
    private void WindowsColorsClick(object sender, RoutedEventArgs e) => OpenTarget("ms-settings:colors");

    private void ReadWindowsAnimations()
    {
        try
        {
            bool enabled = WindowsVisualSettings.AnimationsEnabled;
            WindowsAnimationsInput.IsEnabled = true;
            WindowsAnimationsInput.IsChecked = enabled;
            WindowMotionPreviewButton.IsEnabled = enabled && windowTheme.Motion != WindowOpeningMotion.Off;
            WindowsMotionHint.Text = "Настройка Windows также действует на меню «Пуск» и другие системные окна.";
        }
        catch (Win32Exception)
        {
            WindowsAnimationsInput.IsEnabled = false;
            WindowsMotionHint.Text = "Windows не предоставила доступ к системному переключателю.";
        }
    }

    private void WindowsAnimationsClick(object sender, RoutedEventArgs e)
    {
        if (!ready || !WindowsAnimationsInput.IsEnabled) return;
        try { WindowsVisualSettings.AnimationsEnabled = WindowsAnimationsInput.IsChecked == true; }
        catch (Win32Exception error) { Status(error.Message, true); }
        finally { ReadWindowsAnimations(); }
    }

    private void DisposeWindowTheme()
    {
        SystemEvents.UserPreferenceChanged -= WindowSystemPreferenceChanged;
        windowsFrames.Dispose();
    }
}
