using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MagmPaper.Windows;

namespace MagmPaper.Views;

public partial class UpdateNotes : Window
{
    private const double Gap = 10;
    private const double SmallWidth = 250;
    private const uint NoSize = 0x1, NoZOrder = 0x4, NoActivate = 0x10;
    private bool placed, dragging, sliding;
    private Window? anchor;
    private NotesDock? dock;
    private (Win32.Point Cursor, Area Window)? dragStart;
    private Point slideFrom, slideTo;
    private readonly Stopwatch slideClock = new();

    internal bool IsDocked => dock is not null;
    private nint Handle => new WindowInteropHelper(this).Handle;

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
        Closed += (_, _) => StopSlide();
    }

    internal void ShowNextToOwner()
    {
        var owner = Owner;
        anchor = owner;
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
            PlaceInitially();
            placed = true;
            Opacity = 1;
            if (owner is MainWindow manager) manager.TrackWindowFrame(this, NotesCard.CornerRadius.TopLeft);
        }, DispatcherPriority.ContextIdle);
    }

    internal void PlaceNextToOwner()
    {
        if (!placed || dock is not { } current || !IsVisible || !TryGetAnchor(out var owner) || !TryGetBounds(out var window)) return;
        var work = WorkArea(owner);
        int gap = AnchorPixels(Gap);
        var destination = current.Place(owner, window, work, gap);
        // A docked pair can be moved to a monitor with less room.
        if (!Fits(work, destination) && current.Edge is NotesEdge.Left or NotesEdge.Right)
        {
            var other = current with { Edge = current.Edge == NotesEdge.Left ? NotesEdge.Right : NotesEdge.Left };
            var opposite = other.Place(owner, window, work, gap);
            if (Fits(work, opposite)) destination = opposite;
        }
        MoveTo(KeepVisible(destination, work));
    }

    private void PlaceInitially()
    {
        if (!TryGetAnchor(out var bounds) || !TryGetBounds(out var popup)) return;
        var owner = anchor!;
        var work = WorkArea(bounds);
        int width = popup.Width;
        int gap = AnchorPixels(Gap);
        int offset = AnchorPixels(50);
        var side = NotesEdge.Right;
        if (bounds.Right + gap + width > work.Right)
        {
            if (bounds.Left - gap - width >= work.Left)
                side = NotesEdge.Left;
            else if (owner.WindowState == WindowState.Normal &&
                work.Width - width - gap >= AnchorPixels(owner.MinWidth))
            {
                int ownerWidth = Math.Min(bounds.Width, work.Width - width - gap);
                int withNotesRight = Math.Clamp(bounds.Left, work.Left, work.Right - ownerWidth - width - gap);
                int withNotesLeft = Math.Clamp(bounds.Left, work.Left + width + gap, work.Right - ownerWidth);
                bool right = Math.Abs(bounds.Left - withNotesRight) <= Math.Abs(bounds.Left - withNotesLeft);
                var ownerHandle = new WindowInteropHelper(owner).Handle;
                if (Win32.SetWindowPos(ownerHandle, 0, right ? withNotesRight : withNotesLeft, bounds.Top,
                    ownerWidth, bounds.Height, NoZOrder | NoActivate) && TryGetAnchor(out var moved))
                {
                    side = right ? NotesEdge.Right : NotesEdge.Left;
                    bounds = moved;
                }
            }
        }
        dock = new(side, offset);
        var destination = dock.Value.Place(bounds, popup, work, gap);
        MoveTo(KeepVisible(destination, work));
    }

    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        if (!placed || !TryGetBounds(out var window) || !Win32.GetCursorPos(out var cursor)) return;
        if (!DragGrip.CaptureMouse()) return;
        dragStart = (cursor, window);
        dragging = false;
        e.Handled = true;
    }

    private void DragMoved(object sender, MouseEventArgs e)
    {
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed ||
            !Win32.GetCursorPos(out var cursor) || !TryGetBounds(out var window)) return;
        int dx = cursor.X - start.Cursor.X, dy = cursor.Y - start.Cursor.Y;
        if (!dragging)
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * dpi &&
                Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * dpi) return;
            StopSlide();
            dragging = true;
            dock = null;
            Owner = null;
        }
        var raw = At(window, start.Window.Left + dx, start.Window.Top + dy);
        var snap = Snap(raw, out var target);
        if (snap is null)
            MoveTo(raw);
        else
        {
            // Pull partway toward the edge while the pointer is held. Release
            // inside the same zone to commit; moving away restores free drag.
            SlideTo(Interpolate(new Point(raw.Left, raw.Top), new Point(target.Left, target.Top), .65));
        }
        e.Handled = true;
    }

    private void FinishDrag(object sender, MouseButtonEventArgs e)
    {
        if (dragStart is not { } start) return;
        bool wasDragging = dragging;
        dragStart = null;
        dragging = false;
        DragGrip.ReleaseMouseCapture();
        if (wasDragging && Win32.GetCursorPos(out var cursor) && TryGetBounds(out var window))
        {
            var raw = At(window, start.Window.Left + cursor.X - start.Cursor.X, start.Window.Top + cursor.Y - start.Cursor.Y);
            if (Snap(raw, out var target) is { } next)
            {
                dock = next;
                Owner = anchor;
                SlideTo(new Point(target.Left, target.Top));
            }
            else MoveTo(KeepVisible(raw, WorkArea(raw)));
        }
        e.Handled = true;
    }

    private void DragCaptureLost(object sender, MouseEventArgs e)
    {
        if (dragStart is null) return;
        dragStart = null;
        if (dragging) StopSlide();
        dragging = false;
    }

    private NotesDock? Snap(Area raw, out Area target)
    {
        target = raw;
        if (!TryGetAnchor(out var owner)) return null;
        var work = WorkArea(raw);
        int gap = AnchorPixels(Gap);
        var candidate = NotesDock.Find(owner, raw, work, gap, AnchorPixels(27), AnchorPixels(52));
        if (candidate is { } match) target = match.Place(owner, raw, work, gap);
        return candidate;
    }

    private bool TryGetBounds(out Area bounds)
    {
        bool valid = Win32.GetWindowRect(Handle, out var rect);
        bounds = rect.Area;
        return valid;
    }

    private bool TryGetAnchor(out Area bounds)
    {
        bounds = default;
        if (anchor is not { IsVisible: true } owner || owner.WindowState == WindowState.Minimized ||
            !Win32.GetWindowRect(new WindowInteropHelper(owner).Handle, out var rect)) return false;
        bounds = rect.Area;
        return true;
    }

    private int AnchorPixels(double dips) => (int)Math.Ceiling(dips * VisualTreeHelper.GetDpi(anchor!).DpiScaleX);
    private static Area WorkArea(Area window)
    {
        var work = System.Windows.Forms.Screen.GetWorkingArea(new System.Drawing.Rectangle(window.Left, window.Top, window.Width, window.Height));
        return new(work.Left, work.Top, work.Right, work.Bottom);
    }
    private static Area At(Area window, int x, int y) => new(x, y, x + window.Width, y + window.Height);
    private static bool Fits(Area work, Area window) =>
        work.Left <= window.Left && work.Top <= window.Top && work.Right >= window.Right && work.Bottom >= window.Bottom;
    private static Area KeepVisible(Area window, Area work) => At(window,
        Math.Clamp(window.Left, work.Left, Math.Max(work.Left, work.Right - window.Width)),
        Math.Clamp(window.Top, work.Top, Math.Max(work.Top, work.Bottom - window.Height)));

    private void MoveTo(Area area)
    {
        StopSlide();
        Move(new Point(area.Left, area.Top));
    }

    private void Move(Point position) =>
        _ = Win32.SetWindowPos(Handle, 0, (int)Math.Round(position.X), (int)Math.Round(position.Y), 0, 0, NoSize | NoZOrder | NoActivate);

    private void SlideTo(Point target)
    {
        if (!SystemParameters.ClientAreaAnimation) { StopSlide(); Move(target); return; }
        if (sliding && target == slideTo) return;
        if (!TryGetBounds(out var area) || target == new Point(area.Left, area.Top)) { StopSlide(); return; }
        slideFrom = new(area.Left, area.Top);
        slideTo = target;
        slideClock.Restart();
        if (!sliding)
        {
            sliding = true;
            CompositionTarget.Rendering += SlideFrame;
        }
    }

    private void SlideFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) { StopSlide(); return; }
        double progress = Math.Min(1, slideClock.Elapsed.TotalMilliseconds / 115);
        Move(Interpolate(slideFrom, slideTo, 1 - Math.Pow(1 - progress, 3)));
        if (progress >= 1) StopSlide();
    }

    private void StopSlide()
    {
        if (!sliding) return;
        CompositionTarget.Rendering -= SlideFrame;
        slideClock.Stop();
        sliding = false;
    }

    private static Point Interpolate(Point from, Point to, double fraction) =>
        new(from.X + (to.X - from.X) * fraction, from.Y + (to.Y - from.Y) * fraction);

    private void DismissClick(object sender, RoutedEventArgs e) => Close();
}
