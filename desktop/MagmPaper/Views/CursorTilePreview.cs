using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MagmPaper.Views;

public sealed partial class CursorTilePreview : UserControl
{
    public static readonly DependencyProperty FramesProperty = DependencyProperty.Register(nameof(Frames),
        typeof(IReadOnlyList<CursorPreviewFrame>), typeof(CursorTilePreview), new PropertyMetadata(null, (element, _) => ((CursorTilePreview)element).Stop()));

    public IReadOnlyList<CursorPreviewFrame>? Frames
    {
        get => (IReadOnlyList<CursorPreviewFrame>?)GetValue(FramesProperty);
        set => SetValue(FramesProperty, value);
    }

    private readonly DispatcherTimer timer = new(DispatcherPriority.Background);
    private int selected;
    internal bool Cycling => timer.IsEnabled;

    public CursorTilePreview()
    {
        InitializeComponent();
        timer.Tick += (_, _) => Advance();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) Stop(); };
        Unloaded += (_, _) => Stop();
    }

    internal void Start()
    {
        if (timer.IsEnabled || !IsEnabled || !IsVisible || Frames is not { Count: > 1 }) return;
        timer.Interval = TimeSpan.FromMilliseconds(800);
        timer.Start();
    }

    internal void Stop()
    {
        timer.Stop();
        selected = 0;
        Show(animate: false);
    }

    private void Advance()
    {
        if (!IsEnabled || !IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized || Frames is not { Count: > 1 } frames)
        {
            Stop();
            return;
        }
        selected = (selected + 1) % frames.Count;
        Show(SystemParameters.ClientAreaAnimation);
        timer.Interval = TimeSpan.FromMilliseconds(1350);
    }

    private void Show(bool animate)
    {
        var frame = Frames is { Count: > 0 } frames ? frames[selected] : null;
        Outgoing.Source = animate ? Incoming.Source : null;
        Incoming.Source = frame?.Picture;
        Caption.Text = frame?.Label ?? "Предпросмотр недоступен";
        var shift = (TranslateTransform)Incoming.RenderTransform;
        Incoming.BeginAnimation(OpacityProperty, animate ? Fade(0, 1) : null);
        Outgoing.BeginAnimation(OpacityProperty, animate ? Fade(1, 0) : null);
        shift.BeginAnimation(TranslateTransform.XProperty, animate ? Fade(7, 0) : null);
    }

    private static DoubleAnimation Fade(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(220))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };
}
