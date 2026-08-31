using System.Diagnostics;
using System.Numerics;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Shared.Maths;
using Robust.Shared3D;
using SDL3;

namespace Robust.Client3D;

internal static class NetworkedProgram
{
    private const int InterpolationDelayTicks = 12;

    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        uniform mat4 uMvp;
        uniform vec3 uTint;
        out vec3 vColor;
        void main()
        {
            vColor = uTint;
            gl_Position = uMvp * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core
        in vec3 vColor;
        out vec4 fragColor;
        void main()
        {
            fragColor = vec4(vColor, 1.0);
        }
        """;

    [STAThread]
    public static unsafe int Main(string[] args)
    {
        if (HasArgument(args, "--offline"))
            return Program.Main(args);

        var host = ReadString(args, "--host=", "127.0.0.1");
        var port = ReadInteger(args, "--port=", NetworkProtocol3D.DefaultPort);
        var frameLimit = ReadNullableInteger(args, "--frames=");
        var autoPlay = HasArgument(args, "--autoplay");
        var screenshotPath = ReadString(args, "--screenshot=", string.Empty);

        using var connectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var network = NetworkClient3D.ConnectAsync(host, port, connectCancellation.Token)
            .GetAwaiter()
            .GetResult();

        Console.WriteLine(
            $"Connected to Server3D at {host}:{port} as player {network.PlayerId}; " +
            $"fixedDelta={network.FixedDelta:F6}");

        if (!SDL.SDL_Init(SDL.SDL_InitFlags.SDL_INIT_VIDEO | SDL.SDL_InitFlags.SDL_INIT_EVENTS))
        {
            Console.Error.WriteLine($"SDL initialization failed: {SDL.SDL_GetError()}");
            return 1;
        }

        IntPtr window = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        uint vertexArray = 0;
        uint vertexBuffer = 0;
        uint program = 0;

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
                $"RussianCM 3D multiplayer - player {network.PlayerId}",
                1280,
                720,
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

            program = CreateProgram(VertexShaderSource, FragmentShaderSource);
            var mvpLocation = GL.GetUniformLocation((int) program, "uMvp");
            var tintLocation = GL.GetUniformLocation((int) program, "uTint");
            if (mvpLocation < 0 || tintLocation < 0)
                throw new InvalidOperationException("Networked 3D shader uniforms are missing.");

            var vertices = CreateCubeVertices();
            GL.GenVertexArrays(1, out vertexArray);
            GL.GenBuffers(1, out vertexBuffer);
            GL.BindVertexArray(vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
            fixed (float* vertexPointer = vertices)
            {
                GL.BufferData(
                    BufferTarget.ArrayBuffer,
                    vertices.Length * sizeof(float),
                    (IntPtr) vertexPointer,
                    BufferUsageHint.StaticDraw);
            }

            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.ClearColor(0.025f, 0.035f, 0.065f, 1f);

            var interactive = frameLimit is null && !autoPlay;
            if (interactive && !SDL.SDL_SetWindowRelativeMouseMode(window, true))
                Console.Error.WriteLine($"Relative mouse mode unavailable: {SDL.SDL_GetError()}");

            var predictor = new PredictedPlayer3D(DemoWorld3D.GetPlayerSpawnPosition(network.PlayerId));
            var remotePlayers = new Dictionary<int, RemoteSnapshotBuffer3D>();
            long latestServerTick = 0;

            var yaw = MathF.PI;
            var pitch = -0.34f;
            var moveForward = false;
            var moveBackward = false;
            var moveLeft = false;
            var moveRight = false;
            var jumpRequested = false;
            var autoPlayTime = 0f;
            var autoJumpSent = false;
            var previousTimestamp = Stopwatch.GetTimestamp();
            var simulationAccumulator = 0f;
            var frame = 0;
            var running = true;

            Console.WriteLine("Controls: WASD move, mouse look, Space jump, Escape quit");

            while (running && network.Connected)
            {
                while (SDL.SDL_PollEvent(out var ev))
                {
                    var type = (SDL.SDL_EventType) ev.type;
                    if (type is SDL.SDL_EventType.SDL_EVENT_QUIT or
                        SDL.SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                    {
                        running = false;
                    }

                    if (type == SDL.SDL_EventType.SDL_EVENT_MOUSE_MOTION && interactive)
                    {
                        yaw += ev.motion.xrel * 0.0025f;
                        pitch = Math.Clamp(pitch - ev.motion.yrel * 0.0025f, -1.15f, 0.45f);
                    }

                    if (type is SDL.SDL_EventType.SDL_EVENT_KEY_DOWN or SDL.SDL_EventType.SDL_EVENT_KEY_UP)
                    {
                        var pressed = type == SDL.SDL_EventType.SDL_EVENT_KEY_DOWN;
                        switch (ev.key.scancode)
                        {
                            case SDL.SDL_Scancode.SDL_SCANCODE_W:
                                moveForward = pressed;
                                break;
                            case SDL.SDL_Scancode.SDL_SCANCODE_S:
                                moveBackward = pressed;
                                break;
                            case SDL.SDL_Scancode.SDL_SCANCODE_A:
                                moveLeft = pressed;
                                break;
                            case SDL.SDL_Scancode.SDL_SCANCODE_D:
                                moveRight = pressed;
                                break;
                            case SDL.SDL_Scancode.SDL_SCANCODE_SPACE when pressed && !ev.key.repeat:
                                jumpRequested = true;
                                break;
                            case SDL.SDL_Scancode.SDL_SCANCODE_ESCAPE when pressed:
                                running = false;
                                break;
                        }
                    }
                }

                while (network.TryReadSnapshot(out var snapshot))
                {
                    latestServerTick = Math.Max(latestServerTick, snapshot.ServerTick);
                    var activeRemotePlayers = new HashSet<int>();

                    foreach (var player in snapshot.Players)
                    {
                        if (player.PlayerId == network.PlayerId)
                        {
                            predictor.Reconcile(player);
                            continue;
                        }

                        activeRemotePlayers.Add(player.PlayerId);
                        if (!remotePlayers.TryGetValue(player.PlayerId, out var buffer))
                        {
                            buffer = new RemoteSnapshotBuffer3D();
                            remotePlayers.Add(player.PlayerId, buffer);
                        }

                        buffer.Push(snapshot.ServerTick, player);
                    }

                    foreach (var staleId in remotePlayers.Keys.Where(id => !activeRemotePlayers.Contains(id)).ToArray())
                        remotePlayers.Remove(staleId);
                }

                var currentTimestamp = Stopwatch.GetTimestamp();
                var frameTime = Math.Min(
                    (currentTimestamp - previousTimestamp) / (float) Stopwatch.Frequency,
                    0.1f);
                previousTimestamp = currentTimestamp;
                simulationAccumulator += frameTime;

                var forward = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
                var right = new Vector2(forward.Y, -forward.X);
                var movement = forward * ((moveForward ? 1f : 0f) - (moveBackward ? 1f : 0f)) +
                               right * ((moveRight ? 1f : 0f) - (moveLeft ? 1f : 0f));

                while (simulationAccumulator >= network.FixedDelta)
                {
                    var stepMovement = movement;
                    var stepJump = jumpRequested;
                    if (autoPlay)
                    {
                        autoPlayTime += network.FixedDelta;
                        stepMovement = autoPlayTime switch
                        {
                            < 1.2f => forward,
                            < 2.2f => right,
                            _ => Vector2.Zero,
                        };
                        stepJump = !autoJumpSent && autoPlayTime >= 0.35f;
                        autoJumpSent |= stepJump;
                    }

                    var input = predictor.Step(stepMovement, stepJump, yaw, network.FixedDelta);
                    network.QueueInput(input);
                    jumpRequested = false;
                    simulationAccumulator -= network.FixedDelta;
                }

                SDL.SDL_GetWindowSizeInPixels(window, out var width, out var height);
                if (width <= 0 || height <= 0)
                    continue;

                GL.Viewport(0, 0, width, height);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                var horizontalLook = MathF.Cos(pitch);
                var lookDirection = Vector3.Normalize(new Vector3(
                    MathF.Sin(yaw) * horizontalLook,
                    MathF.Cos(yaw) * horizontalLook,
                    MathF.Sin(pitch)));
                var cameraTarget = predictor.Position + Vector3.UnitZ * 0.35f;
                var cameraDirection = -lookDirection;
                var cameraDistance = ResolveCameraDistance(cameraTarget, cameraDirection, 3.5f);
                var camera = cameraTarget + cameraDirection * cameraDistance;
                var view = Matrix4x4.CreateLookAt(camera, cameraTarget, Vector3.UnitZ);
                var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                    MathF.PI / 3f,
                    width / (float) height,
                    0.05f,
                    100f);

                GL.UseProgram(program);
                GL.BindVertexArray(vertexArray);
                DrawWorld(mvpLocation, tintLocation, view, projection);
                DrawPlayer(
                    predictor.Position,
                    predictor.FacingYaw,
                    new Vector3(1f, 0.25f, 0.08f),
                    new Vector3(1f, 0.58f, 0.22f),
                    mvpLocation,
                    tintLocation,
                    view,
                    projection);

                var renderTick = Math.Max(0d, latestServerTick - InterpolationDelayTicks);
                foreach (var buffer in remotePlayers.Values)
                {
                    if (!buffer.TrySample(renderTick, network.FixedDelta, out var remote))
                        continue;

                    DrawPlayer(
                        remote.Position,
                        remote.FacingYaw,
                        new Vector3(0.08f, 0.65f, 1f),
                        new Vector3(0.35f, 0.9f, 1f),
                        mvpLocation,
                        tintLocation,
                        view,
                        projection);
                }

                if (!string.IsNullOrEmpty(screenshotPath) &&
                    (frameLimit is null ? frame == 0 : frame + 1 >= frameLimit.Value))
                {
                    SaveFramebuffer(Path.GetFullPath(screenshotPath), width, height);
                    screenshotPath = string.Empty;
                }

                SDL.SDL_GL_SwapWindow(window);
                frame++;
                if (frameLimit is not null && frame >= frameLimit.Value)
                    running = false;
            }

            Console.WriteLine(
                $"Network player {network.PlayerId}: {predictor.Position.X:F3}, {predictor.Position.Y:F3}, " +
                $"{predictor.Position.Z:F3}; pending={predictor.PendingInputCount}; serverTick={latestServerTick}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (vertexBuffer != 0)
                GL.DeleteBuffer(vertexBuffer);
            if (vertexArray != 0)
                GL.DeleteVertexArray(vertexArray);
            if (program != 0)
                GL.DeleteProgram(program);
            if (context != IntPtr.Zero)
                SDL.SDL_GL_DestroyContext(context);
            if (window != IntPtr.Zero)
                SDL.SDL_DestroyWindow(window);
            SDL.SDL_Quit();
        }
    }

    private static float ResolveCameraDistance(Vector3 target, Vector3 direction, float desiredDistance)
    {
        var ray = new Ray3(target, direction);
        var distance = desiredDistance;
        foreach (var bounds in DemoWorld3D.CollisionBounds)
        {
            if (ray.TryIntersect(bounds, out var hitDistance) && hitDistance <= desiredDistance)
                distance = MathF.Min(distance, MathF.Max(0.4f, hitDistance - 0.08f));
        }

        return distance;
    }

    private static unsafe void DrawWorld(
        int mvpLocation,
        int tintLocation,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        foreach (var worldObject in DemoWorld3D.Objects)
        {
            DrawCube(
                worldObject.Transform,
                new Vector3(0.25f, 0.55f, 0.75f),
                mvpLocation,
                tintLocation,
                view,
                projection);
        }
    }

    private static unsafe void DrawPlayer(
        Vector3 position,
        float yaw,
        Vector3 bodyTint,
        Vector3 headTint,
        int mvpLocation,
        int tintLocation,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -yaw);
        DrawCube(
            new SpatialTransform(position - Vector3.UnitZ * 0.2f, rotation, new Vector3(0.62f, 0.42f, 1.2f)),
            bodyTint,
            mvpLocation,
            tintLocation,
            view,
            projection);
        DrawCube(
            new SpatialTransform(position + Vector3.UnitZ * 0.56f, rotation, new Vector3(0.55f)),
            headTint,
            mvpLocation,
            tintLocation,
            view,
            projection);
    }

    private static unsafe void DrawCube(
        SpatialTransform transform,
        Vector3 tint,
        int mvpLocation,
        int tintLocation,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        var mvp = transform.Matrix * view * projection;
        GL.UniformMatrix4(mvpLocation, 1, false, (float*) &mvp);
        GL.Uniform3(tintLocation, tint.X, tint.Y, tint.Z);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 36);
    }

    private static uint CreateProgram(string vertexSource, string fragmentSource)
    {
        var vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);
        var program = GL.CreateProgram();
        GL.AttachShader((int) program, vertexShader);
        GL.AttachShader((int) program, fragmentShader);
        GL.LinkProgram((int) program);
        GL.GetProgram((int) program, GetProgramParameterName.LinkStatus, out var linked);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
        if (linked == 0)
            throw new InvalidOperationException($"Shader link failed: {GL.GetProgramInfoLog((int) program)}");
        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
        if (compiled == 0)
            throw new InvalidOperationException($"Shader compile failed: {GL.GetShaderInfoLog(shader)}");
        return shader;
    }

    private static float[] CreateCubeVertices()
    {
        return
        [
            -0.5f,-0.5f,-0.5f,  0.5f,-0.5f,-0.5f,  0.5f, 0.5f,-0.5f,
             0.5f, 0.5f,-0.5f, -0.5f, 0.5f,-0.5f, -0.5f,-0.5f,-0.5f,
            -0.5f,-0.5f, 0.5f,  0.5f, 0.5f, 0.5f,  0.5f,-0.5f, 0.5f,
             0.5f, 0.5f, 0.5f, -0.5f,-0.5f, 0.5f, -0.5f, 0.5f, 0.5f,
            -0.5f, 0.5f, 0.5f, -0.5f,-0.5f, 0.5f, -0.5f,-0.5f,-0.5f,
            -0.5f,-0.5f,-0.5f, -0.5f, 0.5f,-0.5f, -0.5f, 0.5f, 0.5f,
             0.5f, 0.5f, 0.5f,  0.5f, 0.5f,-0.5f,  0.5f,-0.5f,-0.5f,
             0.5f,-0.5f,-0.5f,  0.5f,-0.5f, 0.5f,  0.5f, 0.5f, 0.5f,
            -0.5f,-0.5f,-0.5f,  0.5f,-0.5f,-0.5f,  0.5f,-0.5f, 0.5f,
             0.5f,-0.5f, 0.5f, -0.5f,-0.5f, 0.5f, -0.5f,-0.5f,-0.5f,
            -0.5f, 0.5f,-0.5f,  0.5f, 0.5f, 0.5f,  0.5f, 0.5f,-0.5f,
             0.5f, 0.5f, 0.5f, -0.5f, 0.5f,-0.5f, -0.5f, 0.5f, 0.5f,
        ];
    }

    private static unsafe void SaveFramebuffer(string path, int width, int height)
    {
        var stride = (width * 3 + 3) & ~3;
        var pixels = new byte[stride * height];
        GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
        fixed (byte* pixelPointer = pixels)
        {
            GL.ReadPixels(0, 0, width, height, PixelFormat.Bgr, PixelType.UnsignedByte, (IntPtr) pixelPointer);
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
        Console.WriteLine($"Rendered network frame: {path}");
    }

    private static bool HasArgument(string[] args, string expected)
    {
        return args.Any(argument => string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadString(string[] args, string prefix, string fallback)
    {
        foreach (var argument in args)
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return argument[prefix.Length..];
        }

        return fallback;
    }

    private static int ReadInteger(string[] args, string prefix, int fallback)
    {
        var value = ReadNullableInteger(args, prefix);
        return value ?? fallback;
    }

    private static int? ReadNullableInteger(string[] args, string prefix)
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
}
