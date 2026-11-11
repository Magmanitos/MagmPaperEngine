namespace MagmPaper;

// Enabled only by the isolated Windows validation entry point. No production
// logging or disk writes in the rendering loop.
internal static class Diagnostics
{
    private static readonly object Gate = new();
    internal static string? Output { get; set; }
    internal static void Trace(string message)
    {
        if (Output is null) return;
        lock (Gate)
        {
            Directory.CreateDirectory(Output);
            File.AppendAllText(Path.Combine(Output, "trace.txt"), $"{DateTime.UtcNow:O} [{Environment.CurrentManagedThreadId}] {message}\n");
        }
    }
}
