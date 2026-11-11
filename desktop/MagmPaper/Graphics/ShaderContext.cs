using System.Runtime.InteropServices;
using System.Text;

namespace MagmPaper.Graphics;

internal sealed class DeviceLostException(string message) : IOException(message);
internal sealed record PixelFrame(int Width, int Height, byte[] Rgba);

internal sealed class ShaderContext : IDisposable
{
    private const string Vertex = "attribute vec2 position; varying vec2 surfacePosition; void main(){surfacePosition=position;gl_Position=vec4(position,0.0,1.0);}";
    private const string CopyShader = "precision mediump float; uniform sampler2D image; uniform vec2 resolution; void main(){gl_FragColor=texture2D(image,gl_FragCoord.xy/resolution);}";
    private nint display, surface, context;
    private uint program, copy, quad;
    private int time, resolution, mouse, backbuffer, width, height, read, maxTexture;
    private bool history, offscreen;
    private readonly uint[] textures = new uint[2], buffers = new uint[2];
    public int Generation { get; }
    public bool Loaded => program != 0;
    public string Renderer { get; private set; } = "";
    public ShaderContext(nint window)
    {
        var device = AngleDevice.Acquire(); display = device.Display; Generation = device.Generation;
        try
        {
          lock (AngleDevice.GraphicsGate)
          {
            Diagnostics.Trace("EGL create surface and context");
            Gpu.EglBindAPI(0x30a0);
            surface = Gpu.EglCreateWindowSurface(display, device.Config, window, [0x3038]);
            context = Gpu.EglCreateContext(display, device.Config, 0, [0x3098, 2, 0x3038]);
            if (surface == 0 || context == 0 || Gpu.EglMakeCurrent(display, surface, surface, context) == 0) throw new DeviceLostException($"GPU-поверхность недоступна (EGL 0x{Gpu.EglGetError():X}).");
            Gpu.EglSwapInterval(display, 0);
            Diagnostics.Trace("GLES initialized");
            Renderer = Marshal.PtrToStringAnsi(Gpu.GetString(0x1f01)) ?? "ANGLE";
            Gpu.GetIntegerv(0x0d33, out maxTexture); maxTexture = Math.Min(maxTexture, 3840);
            Gpu.GenBuffers(1, out quad); Gpu.BindBuffer(0x8892, quad);
            float[] vertices = [-1,-1, 1,-1, -1,1, -1,1, 1,-1, 1,1];
            Gpu.BufferData(0x8892, vertices.Length * sizeof(float), vertices, 0x88e4);
            Diagnostics.Trace("Compile copy shader");
            copy = Build(CopyShader);
            Diagnostics.Trace("Shader context ready");
            Gpu.EglMakeCurrent(display, 0, 0, 0);
          }
        }
        catch { Dispose(); throw; }
    }
    private static unsafe uint Compile(uint type, string source)
    {
        var shader = Gpu.CreateShader(type); byte[] bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* data = bytes)
        {
            byte* pointer = data; int length = bytes.Length;
            Gpu.ShaderSource(shader, 1, (nint)(&pointer), (nint)(&length));
        }
        Gpu.CompileShader(shader); Gpu.GetShaderiv(shader, 0x8b81, out int status);
        if (status == 0)
        {
            var error = new StringBuilder(16384); Gpu.GetShaderInfoLog(shader, error.Capacity, out _, error); Gpu.DeleteShader(shader);
            throw new InvalidDataException(error.Length > 0 ? error.ToString() : "Ошибка компиляции GLSL.");
        }
        return shader;
    }
    private static uint Build(string source)
    {
        uint vertex = 0, fragment = 0, value = 0;
        try
        {
            vertex = Compile(0x8b31, Vertex); fragment = Compile(0x8b30, source);
            value = Gpu.CreateProgram(); Gpu.AttachShader(value, vertex); Gpu.AttachShader(value, fragment); Gpu.LinkProgram(value);
            Gpu.GetProgramiv(value, 0x8b82, out int status);
            if (status == 0) { var error = new StringBuilder(16384); Gpu.GetProgramInfoLog(value, error.Capacity, out _, error); throw new InvalidDataException(error.ToString()); }
            return value;
        }
        catch { if (value != 0) Gpu.DeleteProgram(value); throw; }
        finally { if (vertex != 0) Gpu.DeleteShader(vertex); if (fragment != 0) Gpu.DeleteShader(fragment); }
    }
    private void Bind()
    {
        if (Gpu.EglMakeCurrent(display, surface, surface, context) == 0)
            throw new DeviceLostException($"Не удалось активировать GPU-контекст (EGL 0x{Gpu.EglGetError():X}).");
    }
    public void Load(string source)
    {
        lock (AngleDevice.GraphicsGate)
        {
            Bind();
            try { LoadCore(source); }
            finally { Gpu.EglMakeCurrent(display, 0, 0, 0); }
        }
    }
    private void LoadCore(string source)
    {
        var next = Build(ShaderSource.Prepare(source)); // Failed compilation preserves the previous program.
        if (program != 0) Gpu.DeleteProgram(program); program = next;
        time = Gpu.GetUniformLocation(program, "time"); resolution = Gpu.GetUniformLocation(program, "resolution");
        mouse = Gpu.GetUniformLocation(program, "mouse"); backbuffer = Gpu.GetUniformLocation(program, "backbuffer");
        history = backbuffer >= 0; ClearTargets();
    }
    private void ClearTargets()
    {
        for (int i = 0; i < 2; i++)
        {
            if (textures[i] != 0) Gpu.DeleteTextures(1, ref textures[i]);
            if (buffers[i] != 0) Gpu.DeleteFramebuffers(1, ref buffers[i]);
            textures[i] = buffers[i] = 0;
        }
        width = height = read = 0;
    }
    private void Resize(int outputWidth, int outputHeight, double quality)
    {
        var scale = Math.Min(1, Math.Min((double)maxTexture / outputWidth, (double)maxTexture / outputHeight)) * quality;
        int w = Math.Max(1, (int)Math.Round(outputWidth * scale)), h = Math.Max(1, (int)Math.Round(outputHeight * scale));
        bool target = history || w != outputWidth || h != outputHeight;
        if (w == width && h == height && target == offscreen) return;
        ClearTargets(); width = w; height = h; offscreen = target;
        if (!offscreen) return;
        for (int i = 0; i < (history ? 2 : 1); i++)
        {
            Gpu.GenTextures(1, out textures[i]); Gpu.BindTexture(0x0de1, textures[i]);
            Gpu.TexParameteri(0x0de1, 0x2801, 0x2601); Gpu.TexParameteri(0x0de1, 0x2800, 0x2601);
            Gpu.TexParameteri(0x0de1, 0x2802, 0x812f); Gpu.TexParameteri(0x0de1, 0x2803, 0x812f);
            Gpu.TexImage2D(0x0de1, 0, 0x1908, w, h, 0, 0x1908, 0x1401, 0);
            Gpu.GenFramebuffers(1, out buffers[i]); Gpu.BindFramebuffer(0x8d40, buffers[i]);
            Gpu.FramebufferTexture2D(0x8d40, 0x8ce0, 0x0de1, textures[i], 0);
            if (Gpu.CheckFramebufferStatus(0x8d40) != 0x8cd5) throw new IOException("Недостаточно ресурсов GPU для обоев.");
            Gpu.ClearColor(0, 0, 0, 1); Gpu.Clear(0x4000);
        }
    }
    private void Quad(uint value)
    {
        Gpu.UseProgram(value); Gpu.BindBuffer(0x8892, quad);
        var location = Gpu.GetAttribLocation(value, "position");
        if (location < 0) throw new InvalidDataException("Вершинный атрибут position отсутствует.");
        Gpu.EnableVertexAttribArray((uint)location); Gpu.VertexAttribPointer((uint)location, 2, 0x1406, 0, 0, 0);
        Gpu.DrawArrays(4, 0, 6);
        Gpu.DisableVertexAttribArray((uint)location);
    }
    public PixelFrame? Draw(int outputWidth, int outputHeight, double quality, double seconds, double x, double y, bool capture = false)
    {
        lock (AngleDevice.GraphicsGate)
        {
            // Explicitly hand off the shared D3D11 device between EGL contexts.
            // No context stays current after releasing the submission gate.
            Bind();
            try { return DrawCore(outputWidth, outputHeight, quality, seconds, x, y, capture); }
            finally { Gpu.EglMakeCurrent(display, 0, 0, 0); }
        }
    }
    private PixelFrame? DrawCore(int outputWidth, int outputHeight, double quality, double seconds, double x, double y, bool capture)
    {
        if (program == 0) return null;
        Resize(Math.Max(1, outputWidth), Math.Max(1, outputHeight), quality);
        int write = history ? 1 - read : 0;
        Gpu.Viewport(0, 0, width, height); Gpu.BindFramebuffer(0x8d40, offscreen ? buffers[write] : 0);
        Gpu.ActiveTexture(0x84c0); Gpu.BindTexture(0x0de1, history ? textures[read] : 0);
        Gpu.UseProgram(program); Gpu.Uniform1f(time, (float)seconds); Gpu.Uniform2f(resolution, width, height);
        Gpu.Uniform2f(mouse, (float)x, (float)y); Gpu.Uniform1i(backbuffer, 0); Quad(program);
        if (offscreen)
        {
            Gpu.BindFramebuffer(0x8d40, 0); Gpu.Viewport(0, 0, outputWidth, outputHeight);
            Gpu.BindTexture(0x0de1, textures[write]); Gpu.UseProgram(copy);
            Gpu.Uniform2f(Gpu.GetUniformLocation(copy, "resolution"), outputWidth, outputHeight);
            Gpu.Uniform1i(Gpu.GetUniformLocation(copy, "image"), 0); Quad(copy); read = write;
        }
        PixelFrame? pixels = null;
        if (capture) { var bytes = new byte[checked(outputWidth * outputHeight * 4)]; Gpu.ReadPixels(0, 0, outputWidth, outputHeight, 0x1908, 0x1401, bytes); pixels = new(outputWidth, outputHeight, bytes); }
        var error = Gpu.GetError();
        if (error == 0x507) throw new DeviceLostException("Видеодрайвер восстанавливает устройство.");
        if (error != 0) throw new IOException($"Ошибка рендера OpenGL ES: 0x{error:X}.");
        if (Gpu.EglSwapBuffers(display, surface) == 0) throw new DeviceLostException($"Поверхность обоев требует восстановления (EGL 0x{Gpu.EglGetError():X}).");
        return pixels;
    }
    public void Dispose() { lock (AngleDevice.GraphicsGate) DisposeCore(); }
    private void DisposeCore()
    {
        if (display == 0) return;
        Diagnostics.Trace("Dispose shader context start");
        if (context != 0)
        {
            Gpu.EglMakeCurrent(display, surface, surface, context);
            ClearTargets(); if (program != 0) Gpu.DeleteProgram(program); if (copy != 0) Gpu.DeleteProgram(copy); if (quad != 0) Gpu.DeleteBuffers(1, ref quad);
            Gpu.EglMakeCurrent(display, 0, 0, 0); Gpu.EglDestroyContext(display, context);
        }
        if (surface != 0) Gpu.EglDestroySurface(display, surface);
        display = surface = context = 0; program = copy = quad = 0;
        Gpu.EglReleaseThread(); AngleDevice.Release(); Diagnostics.Trace("Dispose shader context done");
    }
}
