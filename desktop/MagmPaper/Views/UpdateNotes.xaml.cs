using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MagmPaper.Windows;

namespace MagmPaper.Views;

public partial class UpdateNotes : Window
{
    private const double Gap = 10;
    private const double SmallWidth = 250;
    private const uint NoSize = 0x1, NoZOrder = 0x4, NoActivate = 0x10;
    private bool placed, arranging;

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
    }

    internal void ShowNextToOwner()
    {
        var owner = Owner;
        var handle = new WindowInteropHelper(owner).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(owner).DpiScaleX;
        // Leave room at the owner's minimum width on displays that can show both windows.
        if (owner.WindowState == WindowState.Normal)
            Width = Math.Clamp(area.Width / scale - owner.MinWidth - 2 * Gap, Math.Min(SmallWidth, Width), Width);

        Opacity = 0;
        Show();
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible) return;
            UpdateLayout();
            Place(reserveSpace: true);
            placed = true;
            Opacity = 1;
            if (owner is MainWindow manager) manager.TrackWindowFrame(this, NotesCard.CornerRadius.TopLeft);
        }, DispatcherPriority.ContextIdle);
    }

    internal void PlaceNextToOwner()
    {
        if (placed) Place(reserveSpace: false);
    }

    private void Place(bool reserveSpace)
    {
        if (arranging || !IsVisible || Owner is not { } owner || owner.WindowState == WindowState.Minimized) return;
        nint ownerHandle = new WindowInteropHelper(owner).Handle;
        nint popupHandle = new WindowInteropHelper(this).Handle;
        if (ownerHandle == 0 || popupHandle == 0 || !Win32.GetWindowRect(ownerHandle, out var bounds) ||
            !Win32.GetWindowRect(popupHandle, out var popup)) return;
        var area = System.Windows.Forms.Screen.FromHandle(ownerHandle).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(owner);
        int width = popup.Right - popup.Left;
        int height = popup.Bottom - popup.Top;
        int gap = (int)Math.Ceiling(Gap * scale.DpiScaleX);
        int x;
        if (bounds.Right + gap + width <= area.Right)
            x = bounds.Right + gap;
        else if (bounds.Left - gap - width >= area.Left)
            x = bounds.Left - gap - width;
        else
        {
            x = bounds.Right - gap - width;
            if (reserveSpace && owner.WindowState == WindowState.Normal &&
                area.Width - width - gap >= (int)Math.Ceiling(owner.MinWidth * scale.DpiScaleX))
            {
                int ownerWidth = Math.Min(bounds.Right - bounds.Left, area.Width - width - gap);
                int withNotesRight = Math.Clamp(bounds.Left, area.Left, area.Right - ownerWidth - width - gap);
                int withNotesLeft = Math.Clamp(bounds.Left, area.Left + width + gap, area.Right - ownerWidth);
                bool notesOnRight = Math.Abs(bounds.Left - withNotesRight) <= Math.Abs(bounds.Left - withNotesLeft);
                arranging = true;
                try
                {
                    if (Win32.SetWindowPos(ownerHandle, 0, notesOnRight ? withNotesRight : withNotesLeft, bounds.Top,
                        ownerWidth, bounds.Bottom - bounds.Top, NoZOrder | NoActivate) && Win32.GetWindowRect(ownerHandle, out var moved))
                    {
                        x = notesOnRight ? moved.Right + gap : moved.Left - gap - width;
                        bounds = moved;
                    }
                }
                finally { arranging = false; }
            }
        }
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        int y = Math.Clamp(bounds.Top + (int)(50 * scale.DpiScaleY), area.Top, Math.Max(area.Top, area.Bottom - height));
        _ = Win32.SetWindowPos(popupHandle, 0, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    private void DismissClick(object sender, RoutedEventArgs e) => Close();
}
