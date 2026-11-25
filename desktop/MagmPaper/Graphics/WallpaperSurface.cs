namespace MagmPaper.Graphics;

internal interface WallpaperSurface : IDisposable
{
    event Action<string>? Error;
    string Renderer { get; }
    long Frames { get; }
    Task LoadAsync(string source);
    void Configure(RenderOptions options);
    Task<WallpaperColors?> ReadColorsAsync();
    void Fit() { }
}
