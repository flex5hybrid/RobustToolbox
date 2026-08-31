using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client3D.Assets;
using Robust.Client3D.Graphics;
using SDL3;

namespace Robust.Client3D;

internal static class AssetPreviewProgram
{
    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aTexCoord;

        uniform mat4 uMvp;

        out vec3 vNormal;
        out vec2 vTexCoord;

        void main()
        {
            vNormal = aNormal;
            vTexCoord = aTexCoord;
            gl_Position = uMvp * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vTexCoord;
        out vec4 fragColor;

        void main()
        {
            vec3 normal = normalize(vNormal);
            vec3 lightDirection = normalize(vec3(0.4, -0.7, 0.8));
            float diffuse = max(dot(normal, lightDirection), 0.0);
            float checker = mod(floor(vTexCoord.x * 8.0) + floor(vTexCoord.y * 8.0), 2.0);
            vec3 base = mix(vec3(0.13, 0.48, 0.72), vec3(0.65, 0.83, 0.95), checker * 0.18);
            fragColor = vec4(base * (0.28 + diffuse * 0.72), 1.0);
        }
        """;

    [STAThread]
    public static unsafe int Main(string[] args)
    {
        var frameLimit = ReadInteger(args, "--frames=");
        var screenshotPath = ReadString(args, "--screenshot=");
        var requestedModel = ReadString(args, "--model=");
        var modelPath = string.IsNullOrWhiteSpace(requestedModel)
            ? Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "bootstrap-pyramid.gltf")
            : Path.GetFullPath(requestedModel);

        if (!File.Exists(modelPath))
        {
            Console.Error.WriteLine($"3D model not found: {modelPath}");
            return 1;
        }

        if (!SDL.SDL_Init(SDL.SDL_InitFlags.SDL_INIT_VIDEO | SDL.SDL_InitFlags.SDL_INIT_EVENTS))
        {
            Console.Error.WriteLine($"SDL initialization failed: {SDL.SDL_GetError()}");
            return 1;
        }

        IntPtr window = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        uint program = 0;
        GpuMesh3D? gpuMesh = null;

        try
        {
            SDL.SDL_GL_SetAttribute(SDL.SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
            SDL.SDL_GL_SetAttribute(SDL.SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
            SDL.SDL_GL_SetAttribute(
                SDL.SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK,
                SDL.SDL_GL_CONTEXT_PROFILE_CORE);
            SDL.SDL_GL_SetAttribute(
                SDL.SDL_GLAttr.SDL_GL_CONTEXT_FLAGS,
                SDL.SDL_GL_CONTEXT_FORWARD_COMPATIBLE_FLAG);
            SDL.SDL_GL_SetAttribute(SDL.SDL_GLAttr.SDL_GL_DOUBLEBUFFER, 1);
            SDL.SDL_GL_SetAttribute(SDL.SDL_GLAttr.SDL_GL_DEPTH_SIZE, 24);

            window = SDL.SDL_CreateWindow(
                "RussianCM 3D asset preview",
                1024,
                768,
                SDL.SDL_WindowFlags.SDL_WINDOW_OPENGL | SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == IntPtr.Zero)
                throw new InvalidOperationException($"Window creation failed: {SDL.SDL_GetError()}");

            context = SDL.SDL_GL_CreateContext(window);
            if (context == IntPtr.Zero)
                throw new InvalidOperationException($"OpenGL context creation failed: {SDL.SDL_GetError()}");
            if (!SDL.SDL_GL_MakeCurrent(window, context))
                throw new InvalidOperationException($"OpenGL context activation failed: {SDL.SDL_GetError()}");

            GL.LoadBindings(new SdlBindingsContext());
            SDL.SDL_GL_SetSwapInterval(1);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.ClearColor(0.018f, 0.025f, 0.045f, 1f);

            var modelDirectory = Path.GetDirectoryName(modelPath) ?? AppContext.BaseDirectory;
            var meshData = GltfStaticMeshLoader3D.Load(
                File.ReadAllBytes(modelPath),
                uri => File.ReadAllBytes(Path.Combine(modelDirectory, uri.Replace('/', Path.DirectorySeparatorChar))));
            gpuMesh = new GpuMesh3D(meshData);

            program = CreateProgram(VertexShaderSource, FragmentShaderSource);
            var mvpLocation = GL.GetUniformLocation((int) program, "uMvp");
            if (mvpLocation < 0)
                throw new InvalidOperationException("Asset preview shader has no uMvp uniform.");

            Console.WriteLine(
                $"Loaded glTF mesh: {Path.GetFileName(modelPath)}; " +
                $"vertices={meshData.Vertices.Length}; triangles={meshData.Indices.Length / 3}");
            Console.WriteLine("Asset preview: Escape closes the window. Use --model=<path> to inspect another .gltf file.");

            var stopwatch = Stopwatch.StartNew();
            var frame = 0;
            var running = true;

            while (running)
            {
                while (SDL.SDL_PollEvent(out var ev))
                {
                    var type = (SDL.SDL_EventType) ev.type;
                    if (type is SDL.SDL_EventType.SDL_EVENT_QUIT or
                        SDL.SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                    {
                        running = false;
                    }
                    else if (type == SDL.SDL_EventType.SDL_EVENT_KEY_DOWN &&
                             ev.key.scancode == SDL.SDL_Scancode.SDL_SCANCODE_ESCAPE)
                    {
                        running = false;
                    }
                }

                SDL.SDL_GetWindowSizeInPixels(window, out var width, out var height);
                if (width <= 0 || height <= 0)
                    continue;

                GL.Viewport(0, 0, width, height);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                var angle = (float) stopwatch.Elapsed.TotalSeconds * 0.45f;
                var camera = new Vector3(MathF.Sin(angle) * 3.4f, MathF.Cos(angle) * 3.4f, 2.25f);
                var target = new Vector3(0f, 0f, 0.45f);
                var view = Matrix4x4.CreateLookAt(camera, target, Vector3.UnitZ);
                var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                    MathF.PI / 3f,
                    width / (float) height,
                    0.05f,
                    100f);
                var model = Matrix4x4.CreateRotationZ(-0.18f);
                var mvp = model * view * projection;

                GL.UseProgram(program);
                GL.UniformMatrix4(mvpLocation, 1, false, (float*) &mvp);
                gpuMesh.Draw();

                if (!string.IsNullOrWhiteSpace(screenshotPath) &&
                    (frameLimit is null ? frame == 0 : frame + 1 >= frameLimit.Value))
                {
                    SaveFramebuffer(Path.GetFullPath(screenshotPath), width, height);
                    screenshotPath = null;
                }

                SDL.SDL_GL_SwapWindow(window);
                frame++;
                if (frameLimit is not null && frame >= frameLimit.Value)
                    running = false;
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            gpuMesh?.Dispose();
            if (program != 0)
                GL.DeleteProgram(program);
            if (context != IntPtr.Zero)
                SDL.SDL_GL_DestroyContext(context);
            if (window != IntPtr.Zero)
                SDL.SDL_DestroyWindow(window);
            SDL.SDL_Quit();
        }
    }

    private static uint CreateProgram(string vertexSource, string fragmentSource)
    {
        var vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);
        var shaderProgram = (uint) GL.CreateProgram();

        try
        {
            GL.AttachShader(shaderProgram, vertexShader);
            GL.AttachShader(shaderProgram, fragmentShader);
            GL.LinkProgram(shaderProgram);
            GL.GetProgram(shaderProgram, GetProgramParameterName.LinkStatus, out var linked);
            if (linked != 1)
            {
                throw new InvalidOperationException(
                    $"Asset preview shader link failed: {GL.GetProgramInfoLog((int) shaderProgram)}");
            }

            return shaderProgram;
        }
        catch
        {
            GL.DeleteProgram(shaderProgram);
            throw;
        }
        finally
        {
            GL.DetachShader(shaderProgram, vertexShader);
            GL.DetachShader(shaderProgram, fragmentShader);
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);
        }
    }

    private static uint CompileShader(ShaderType type, string source)
    {
        var shader = (uint) GL.CreateShader(type);
        GL.ShaderSource((int) shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
        if (compiled == 1)
            return shader;

        var message = GL.GetShaderInfoLog((int) shader);
        GL.DeleteShader(shader);
        throw new InvalidOperationException($"Asset preview shader compilation failed: {message}");
    }

    private static int? ReadInteger(string[] args, string prefix)
    {
        foreach (var argument in args)
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(argument[prefix.Length..], out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadString(string[] args, string prefix)
    {
        foreach (var argument in args)
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return argument[prefix.Length..];
        }

        return null;
    }

    private static unsafe void SaveFramebuffer(string path, int width, int height)
    {
        var stride = (width * 3 + 3) & ~3;
        var pixels = new byte[stride * height];
        GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
        fixed (byte* pixelPointer = pixels)
        {
            GL.ReadPixels(
                0,
                0,
                width,
                height,
                PixelFormat.Bgr,
                PixelType.UnsignedByte,
                (IntPtr) pixelPointer);
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        const int headerSize = 54;
        writer.Write((byte) 'B');
        writer.Write((byte) 'M');
        writer.Write(headerSize + pixels.Length);
        writer.Write(0);
        writer.Write(headerSize);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short) 1);
        writer.Write((short) 24);
        writer.Write(0);
        writer.Write(pixels.Length);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        writer.Write(pixels);
        Console.WriteLine($"Rendered glTF preview: {path}");
    }
}
