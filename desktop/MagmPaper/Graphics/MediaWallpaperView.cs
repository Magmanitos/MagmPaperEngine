using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MagmPaper.Windows;

namespace MagmPaper.Graphics;

public sealed class MediaWallpaperView : FrameworkElement, IDisposable
{
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render);
    private readonly Stopwatch clock = new();
    private CancellationTokenSource? loading;
    private RenderOptions options = new();
    private MediaPlayer? player;
    private GifClip? gif;
    private BitmapSource? frame;
    private bool videoReady, playing;

    internal event Action<string>? Error;
    internal long Frames { get; private set; }
    internal bool Playing => playing;
    internal TimeSpan PlaybackPosition => player?.Position ?? clock.Elapsed;
    internal bool MediaReady => gif is not null || videoReady;
    public MediaWallpaperView()
    {
        ClipToBounds = true;
        timer.Tick += (_, _) => ShowGifFrame();
    }

    internal async Task LoadAsync(string path, int maxDimension = 1920)
    {
        Clear();
        using var current = new CancellationTokenSource();
        loading = current;
        var token = current.Token;
        try
        {
            if (Library.FormatOf(path) == WallpaperFormat.Gif)
            {
                var clip = await GifClip.ReadAsync(path, maxDimension, animate: true, token);
                token.ThrowIfCancellationRequested();
                gif = clip;
                frame = clip.First;
            }
            else
            {
                await OpenVideoAsync(path, token);
                token.ThrowIfCancellationRequested();
            }
            InvalidateVisual();
            UpdatePlayback();
        }
        catch
        {
            if (ReferenceEquals(loading, current)) Clear();
            throw;
        }
        finally { if (ReferenceEquals(loading, current)) loading = null; }
    }

    private async Task OpenVideoAsync(string path, CancellationToken token)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new MediaPlayer { IsMuted = true, Volume = 0, ScrubbingEnabled = true };
        player = next;
        next.MediaOpened += (_, _) => opened.TrySetResult();
        next.MediaFailed += (_, e) =>
        {
            var error = new InvalidDataException("Windows не смогла открыть MP4. Используйте MP4 с видео H.264.", e.ErrorException);
            if (!opened.TrySetException(error) && ReferenceEquals(player, next)) Error?.Invoke(error.Message);
        };
        next.MediaEnded += (_, _) =>
        {
            if (!ReferenceEquals(player, next)) return;
            next.Position = TimeSpan.Zero;
            if (playing) next.Play();
        };
        next.Open(new Uri(Path.GetFullPath(path)));
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
        token.ThrowIfCancellationRequested();
        if (!next.HasVideo || next.NaturalVideoWidth <= 0 || next.NaturalVideoHeight <= 0)
            throw new InvalidDataException("MP4 не содержит доступной видеодорожки.");
        videoReady = true;
        next.Position = TimeSpan.FromMilliseconds(1);
    }

    internal void Configure(RenderOptions value)
    {
        if (options == value) return;
        if (options.Quality != value.Quality)
        {
            // WPF downscales the composited layer at reduced quality. MP4
            // decoding and timing are still handled by the Windows media stack.
            CacheMode = value.Quality < 1 ? new BitmapCache(value.Quality) : null;
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(this, value.Quality < 1 ? BitmapScalingMode.Linear : BitmapScalingMode.HighQuality);
        }
        bool fpsChanged = options.Fps != value.Fps;
        options = value;
        UpdatePlayback();
        if (playing && fpsChanged && gif is not null) ShowGifFrame();
    }

    private void UpdatePlayback()
    {
        bool run = MediaReady && !options.Paused;
        if (playing == run) return;
        playing = run;
        if (gif is not null)
        {
            if (run) { clock.Start(); ShowGifFrame(); timer.Start(); }
            else { timer.Stop(); clock.Stop(); }
        }
        if (player is not null && videoReady)
        {
            if (run) player.Play();
            else player.Pause();
        }
    }

    private void ShowGifFrame()
    {
        if (gif is null || !playing) return;
        var next = gif.FrameAt(clock.ElapsedMilliseconds, out var untilNext);
        if (!ReferenceEquals(frame, next)) { frame = next; Frames++; InvalidateVisual(); }
        int fps = options.Fps == 0 ? Win32.RefreshRate() : options.Fps;
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(untilNext, 1000.0 / fps));
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var bounds = new Rect(RenderSize);
        drawing.DrawRectangle(Brushes.Black, null, bounds);
        if (bounds.Width == 0 || bounds.Height == 0) return;
        if (frame is { } bitmap)
            drawing.DrawImage(bitmap, Cover(bitmap.PixelWidth, bitmap.PixelHeight, bounds));
        else if (player is { } video && videoReady)
            drawing.DrawVideo(video, Cover(video.NaturalVideoWidth, video.NaturalVideoHeight, bounds));
    }

    private static Rect Cover(int width, int height, Rect bounds)
    {
        double scale = Math.Max(bounds.Width / width, bounds.Height / height);
        double w = width * scale, h = height * scale;
        return new((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
    }

    internal void Clear()
    {
        loading?.Cancel(); loading = null;
        timer.Stop(); clock.Reset(); playing = videoReady = false;
        player?.Close(); player = null;
        gif = null; frame = null;
        InvalidateVisual();
    }

    public void Dispose() => Clear();
}
