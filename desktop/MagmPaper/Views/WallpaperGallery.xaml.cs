using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MagmPaper.Graphics;

namespace MagmPaper.Views;

public partial class WallpaperGallery : UserControl, IDisposable
{
    internal sealed class Tile(Wallpaper item) : INotifyPropertyChanged
    {
        public Wallpaper Item { get; } = item;
        private bool active;
        private ImageSource? image;
        private string status = "Загрузка предпросмотра…";
        public bool Active
        {
            get => active;
            set
            {
                if (active == value) return;
                active = value;
                Changed(nameof(Active));
                Changed(nameof(Subtitle));
            }
        }
        public string Subtitle => Active ? "● На рабочем столе" : Item.Format.ToString().ToUpperInvariant() + " · " + Item.File;
        public ImageSource? Image { get => image; set { image = value; Changed(nameof(Image)); } }
        public string PreviewStatus { get => status; set { status = value; Changed(nameof(PreviewStatus)); } }
        public ImageSource? Still { get; set; }
        public bool Loaded { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    }

    private sealed record Hover(Tile Tile, FrameworkElement Stage);
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(WallpaperGallery), new PropertyMetadata(3));
    public static readonly DependencyProperty PreviewHeightProperty = DependencyProperty.Register(
        nameof(PreviewHeight), typeof(double), typeof(WallpaperGallery), new PropertyMetadata(156.0));
    public int Columns { get => (int)GetValue(ColumnsProperty); private set => SetValue(ColumnsProperty, value); }
    public double PreviewHeight { get => (double)GetValue(PreviewHeightProperty); private set => SetValue(PreviewHeightProperty, value); }

    private Tile[] tiles = [];
    private readonly Dictionary<string, Tile> byId = new(StringComparer.Ordinal);
    private string? cacheDirectory;
    private Library? library;
    private MediaWallpaperView? mediaHover;
    private GalleryRenderer? stillRenderer, liveRenderer;
    private Hover? hover;
    private Task thumbs = Task.CompletedTask, live = Task.CompletedTask;
    private bool active, scheduled, disposed;

    internal event Func<Wallpaper, Task>? OpenRequested, ApplyRequested, StopRequested, DeleteRequested;
    internal IReadOnlyList<Tile> Cards => tiles;

    public WallpaperGallery() => InitializeComponent();
    internal void Initialize(Library source)
    {
        library = source;
        cacheDirectory = Path.Combine(source.DirectoryPath, "GalleryPreview-v1");
    }

    internal void UpdateItems(IReadOnlyList<Wallpaper> items, string? runningId)
    {
        if (!tiles.Select(tile => tile.Item).SequenceEqual(items))
        {
            tiles = items.Select(item => byId.TryGetValue(item.Id, out var tile) && tile.Item == item ? tile : new Tile(item)).ToArray();
            if (hover is { } current && !tiles.Contains(current.Tile)) ClearHover();
            byId.Clear();
            foreach (var tile in tiles) byId.Add(tile.Item.Id, tile);
            Tiles.ItemsSource = tiles;
        }
        foreach (var tile in tiles) tile.Active = tile.Item.Id == runningId;
        Empty.Visibility = tiles.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleThumbnails();
    }

    internal void SetActive(bool value)
    {
        active = value && !disposed;
        if (!active) ClearHover();
        else ScheduleThumbnails();
    }

    private void ViewportResized(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(1, Scroller.ActualWidth - 14);
        Columns = width >= 1360 ? 4 : width >= 830 ? 3 : 2;
        PreviewHeight = Math.Max(90, (width / Columns - 32) * 9 / 16);
        ScheduleThumbnails();
    }

    private void ViewportChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Scroller)) return;
        ScheduleThumbnails();
    }

    private void ScheduleThumbnails()
    {
        if (!active || scheduled || !thumbs.IsCompleted) return;
        scheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            scheduled = false;
            if (active && thumbs.IsCompleted) thumbs = LoadThumbnailsAsync();
        }));
    }

    private bool InViewport(Tile tile)
    {
        if (Tiles.ItemContainerGenerator.ContainerFromItem(tile) is not FrameworkElement { IsVisible: true } element) return false;
        var bounds = element.TransformToAncestor(Scroller).TransformBounds(new Rect(element.RenderSize));
        return bounds.Height > 0 && bounds.Bottom > 0 && bounds.Top < Scroller.ViewportHeight;
    }

    private async Task LoadThumbnailsAsync()
    {
        while (active && tiles.FirstOrDefault(tile => !tile.Loaded && InViewport(tile)) is { } tile)
        {
            tile.Loaded = true;
            try
            {
                if (tile.Item.Format != WallpaperFormat.Glsl)
                {
                    tile.Still = await MediaThumbnails.ReadAsync(library!.MediaPath(tile.Item));
                    if (!ReferenceEquals(hover?.Tile, tile) || tile.Image is null) tile.Image = tile.Still;
                    if (tile.Still is null) tile.PreviewStatus = "▶ " + tile.Item.Format.ToString().ToUpperInvariant();
                    continue;
                }
                var path = CacheFile(tile.Item, out var hash);
                var pixels = await ReadCacheAsync(path, hash);
                if (pixels is null)
                {
                    if (!active || !InViewport(tile)) { tile.Loaded = false; continue; }
                    stillRenderer ??= new GalleryRenderer();
                    await stillRenderer.LoadAsync(tile.Item.Code);
                    if (!active || !InViewport(tile)) { tile.Loaded = false; continue; }
                    _ = await stillRenderer.FrameAsync(.7);
                    pixels = await stillRenderer.FrameAsync(1.4);
                    if (byId.TryGetValue(tile.Item.Id, out var current) && ReferenceEquals(current, tile))
                        await SaveCacheAsync(path, hash, pixels);
                }
                tile.Still = GalleryRenderer.Thumbnail(pixels);
                if (!ReferenceEquals(hover?.Tile, tile) || tile.Image is not WriteableBitmap) tile.Image = tile.Still;
            }
            catch
            {
                tile.PreviewStatus = "Предпросмотр недоступен";
            }
        }
    }

    private string? CacheFile(Wallpaper item, out byte[] hash)
    {
        hash = SHA256.HashData(Encoding.UTF8.GetBytes(item.Code));
        return cacheDirectory is null ? null : Path.Combine(cacheDirectory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Id))) + ".bin");
    }

    private static async Task<byte[]?> ReadCacheAsync(string? path, byte[] hash)
    {
        if (path is null) return null;
        try
        {
            if (new FileInfo(path).Length != hash.Length + GalleryRenderer.Stride * GalleryRenderer.Height) return null;
            byte[] data = await File.ReadAllBytesAsync(path);
            return data.Length == hash.Length + GalleryRenderer.Stride * GalleryRenderer.Height &&
                data.AsSpan(0, hash.Length).SequenceEqual(hash) ? data[hash.Length..] : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task SaveCacheAsync(string? path, byte[] hash, byte[] pixels)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path + ".tmp", [.. hash, .. pixels]);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void TileEnter(object sender, MouseEventArgs e)
    {
        if (!active || sender is not FrameworkElement { DataContext: Tile tile } ||
            Tiles.ItemContainerGenerator.ContainerFromItem(tile) is not ContentPresenter container ||
            container.ContentTemplate?.FindName("Screen", container) is not FrameworkElement stage) return;
        StartHover(tile, stage);
    }

    internal void StartHover(Tile tile, FrameworkElement stage)
    {
        ClearHover();
        hover = new(tile, stage);
        if (live.IsCompleted) live = AnimateHoverAsync();
    }

    private void TileLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element && ReferenceEquals(element.DataContext, hover?.Tile)) ClearHover();
    }

    internal void ClearHover()
    {
        if (hover is not { } old) return;
        hover = null;
        if (mediaHover is { } view)
        {
            view.Dispose();
            if (view.Parent is Grid layer) layer.Children.Remove(view);
            mediaHover = null;
        }
        old.Tile.Image = old.Tile.Still;
    }

    private async Task AnimateHoverAsync()
    {
        while (hover is { } request && active)
        {
            await Task.Delay(120);
            if (!ReferenceEquals(hover, request) || !active) continue;
            try
            {
                if (request.Tile.Item.Format != WallpaperFormat.Glsl)
                {
                    await AnimateMediaAsync(request);
                    if (ReferenceEquals(hover, request)) ClearHover();
                    continue;
                }
                liveRenderer ??= new GalleryRenderer();
                await liveRenderer.LoadAsync(request.Tile.Item.Code);
                var bitmap = GalleryRenderer.LiveBitmap();
                var clock = Stopwatch.StartNew();
                while (active && ReferenceEquals(hover, request) && InViewport(request.Tile))
                {
                    var point = Mouse.GetPosition(request.Stage);
                    double x = Math.Clamp(point.X / Math.Max(1, request.Stage.ActualWidth), 0, 1);
                    double y = 1 - Math.Clamp(point.Y / Math.Max(1, request.Stage.ActualHeight), 0, 1);
                    byte[] pixels = await liveRenderer.FrameAsync(clock.Elapsed.TotalSeconds + 1.4, x, y);
                    if (!active || !ReferenceEquals(hover, request)) break;
                    GalleryRenderer.Update(bitmap, pixels);
                    if (!ReferenceEquals(request.Tile.Image, bitmap)) request.Tile.Image = bitmap;
                    await Task.Delay(32);
                }
            }
            catch { /* Keep the still image if the shader cannot animate. */ }
            if (ReferenceEquals(hover, request)) ClearHover();
        }
    }

    private async Task AnimateMediaAsync(Hover request)
    {
        if (request.Stage is not Border { Child: Grid layer }) return;
        using var view = new MediaWallpaperView { IsHitTestVisible = false };
        mediaHover = view;
        layer.Children.Add(view);
        await view.LoadAsync(library!.MediaPath(request.Tile.Item), maxDimension: GalleryRenderer.Width);
        if (!active || !ReferenceEquals(hover, request)) return;
        view.Configure(new(Paused: false, Fps: 30));
        while (active && ReferenceEquals(hover, request) && InViewport(request.Tile)) await Task.Delay(100);
    }

    private static Task Request(object sender, Func<Wallpaper, Task>? action) =>
        sender is FrameworkElement { DataContext: Tile tile } && action is not null ? action(tile.Item) : Task.CompletedTask;

    private async void OpenClick(object sender, RoutedEventArgs e) => await Request(sender, OpenRequested);
    private async void ApplyClick(object sender, RoutedEventArgs e) => await Request(sender, ApplyRequested);
    private async void StopClick(object sender, RoutedEventArgs e) => await Request(sender, StopRequested);
    private async void DeleteClick(object sender, RoutedEventArgs e) => await Request(sender, DeleteRequested);

    public void Dispose()
    {
        if (disposed) return;
        SetActive(false);
        disposed = true;
        stillRenderer?.Dispose();
        liveRenderer?.Dispose();
    }
}
