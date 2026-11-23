using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MagmPaper.Windows;

namespace MagmPaper.Views;

public partial class UpdateNotes : Window
{
    internal static string VersionLabel => $"MagmPaper {typeof(UpdateNotes).Assembly.GetName().Version!.ToString(3)}";

    public UpdateNotes()
    {
        InitializeComponent();
        VersionText.Text = VersionLabel;
        using var text = typeof(UpdateNotes).Assembly.GetManifestResourceStream("MagmPaper.Assets.ReleaseNotes.md")
            ?? throw new InvalidOperationException("В сборке отсутствует список изменений.");
        using var reader = new StreamReader(text);
        var items = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.StartsWith("- ", StringComparison.Ordinal)) items.Add(line[2..].Replace("**", ""));
        Changes.ItemsSource = items;
        ContentRendered += (_, _) => PlaceNextToOwner();
    }

    internal void PlaceNextToOwner()
    {
        if (!IsVisible || Owner is null || Owner.WindowState == WindowState.Minimized) return;
        nint ownerHandle = new WindowInteropHelper(Owner).Handle;
        nint popupHandle = new WindowInteropHelper(this).Handle;
        if (ownerHandle == 0 || popupHandle == 0 || !Win32.GetWindowRect(ownerHandle, out var bounds) ||
            !Win32.GetWindowRect(popupHandle, out var popup)) return;
        var area = System.Windows.Forms.Screen.FromHandle(ownerHandle).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(Owner);
        int width = popup.Right - popup.Left;
        int height = popup.Bottom - popup.Top;
        int gap = (int)Math.Ceiling(10 * scale.DpiScaleX);
        int x = bounds.Right + gap;
        if (x + width > area.Right)
            x = bounds.Left - gap - width >= area.Left ? bounds.Left - gap - width : bounds.Right - gap - width;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        int y = Math.Clamp(bounds.Top + (int)(50 * scale.DpiScaleY), area.Top, Math.Max(area.Top, area.Bottom - height));
        const uint NoSize = 0x1, NoZOrder = 0x4, NoActivate = 0x10;
        _ = Win32.SetWindowPos(popupHandle, 0, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    private void DismissClick(object sender, RoutedEventArgs e) => Close();
}
