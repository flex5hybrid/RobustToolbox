using System;
using System.Collections.Generic;
using System.Numerics;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Robust.Client.GameObjects;

/// <summary>
/// First bridge between the normal SS14 client and the experimental 3D renderer.
/// The simulation, networking, eye and map grids are all the normal Robust ECS objects;
/// only the final world presentation is replaced by a perspective OpenGL pass.
/// </summary>
internal sealed class World3DGridRenderingSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private TransformSystem _transformSystem = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;

    private World3DGridOverlay? _overlay;

    public override void Initialize()
    {
        _overlay = new World3DGridOverlay(_transformSystem, _mapSystem);
        _overlayManager.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        if (_overlay is null)
            return;

        _overlayManager.RemoveOverlay(_overlay);
        _overlay.Dispose();
        _overlay = null;
    }
}

internal sealed class World3DGridOverlay : Overlay
{
    private const int FloatsPerVertex = 6;
    private const float FloorBottom = -0.12f;

    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMvp;
        out vec3 vColor;
        void main()
        {
            vColor = aColor;
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

    private readonly SharedTransformSystem _transformSystem;
    private readonly SharedMapSystem _mapSystem;
    private List<Entity<MapGridComponent>> _grids = new();
    private readonly List<float> _vertices = new(64 * 1024);

    private uint _vertexArray;
    private uint _vertexBuffer;
    private uint _program;
    private int _mvpLocation = -1;
    private bool _initialized;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public override bool OverwriteTargetFrameBuffer => true;

    public World3DGridOverlay(SharedTransformSystem transformSystem, SharedMapSystem mapSystem)
    {
        _transformSystem = transformSystem;
        _mapSystem = mapSystem;
        ZIndex = int.MaxValue;
    }

    protected internal override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return args.MapId != MapId.Nullspace && args.Viewport.Eye is not null;
    }

    protected internal override unsafe void Draw(in OverlayDrawArgs args)
    {
        var eye = args.Viewport.Eye;
        if (eye is null)
            return;

        // This overlay uses raw OpenGL while Clyde keeps its own GL state cache.
        // Preserve the actual bindings Clyde had on entry so our pass is transparent
        // to everything that renders after the world overlay.
        GL.GetInteger(GetPName.CurrentProgram, out var previousProgram);
        GL.GetInteger(GetPName.VertexArrayBinding, out var previousVertexArray);
        GL.GetInteger(GetPName.ArrayBufferBinding, out var previousArrayBuffer);
        GL.GetInteger(GetPName.DepthFunc, out var previousDepthFunc);
        var previousDepthTest = GL.IsEnabled(EnableCap.DepthTest);

        try
        {
            EnsureInitialized();

            _vertices.Clear();
            _grids.Clear();
            _mapSystem.FindGridsIntersecting(args.MapId, args.WorldBounds, ref _grids);

            foreach (var grid in _grids)
                AppendGrid(grid, args.WorldBounds);

            GL.Viewport(0, 0, args.Viewport.Size.X, args.Viewport.Size.Y);
            GL.ClearColor(0.025f, 0.035f, 0.055f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.DepthMask(true);

            if (_vertices.Count == 0)
                return;

            var target2 = eye.Position.Position + eye.Offset;
            var target = new Vector3(target2.X, target2.Y, 0f);
            var forward2 = eye.Rotation.ToWorldVec();
            var camera = target + new Vector3(-forward2.X * 9f, -forward2.Y * 9f, 7f);
            var view = Matrix4x4.CreateLookAt(camera, target, Vector3.UnitZ);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 3f,
                Math.Max(1, args.Viewport.Size.X) / (float) Math.Max(1, args.Viewport.Size.Y),
                0.05f,
                200f);
            var mvp = view * projection;

            GL.UseProgram(_program);
            GL.UniformMatrix4(_mvpLocation, 1, false, (float*) &mvp);
            GL.BindVertexArray(_vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);

            var vertexData = _vertices.ToArray();
            fixed (float* vertexPointer = vertexData)
            {
                GL.BufferData(
                    BufferTarget.ArrayBuffer,
                    vertexData.Length * sizeof(float),
                    (IntPtr) vertexPointer,
                    BufferUsageHint.StreamDraw);
            }

            GL.DrawArrays(PrimitiveType.Triangles, 0, vertexData.Length / FloatsPerVertex);
        }
        finally
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, (uint) previousArrayBuffer);
            GL.BindVertexArray((uint) previousVertexArray);
            GL.UseProgram((uint) previousProgram);
            GL.DepthFunc((DepthFunction) previousDepthFunc);

            if (previousDepthTest)
                GL.Enable(EnableCap.DepthTest);
            else
                GL.Disable(EnableCap.DepthTest);
        }
    }

    protected override void DisposeBehavior()
    {
        if (_initialized)
        {
            if (_vertexBuffer != 0)
                GL.DeleteBuffer(_vertexBuffer);
            if (_vertexArray != 0)
                GL.DeleteVertexArray(_vertexArray);
            if (_program != 0)
                GL.DeleteProgram(_program);
        }

        base.DisposeBehavior();
    }

    private void AppendGrid(Entity<MapGridComponent> grid, Box2Rotated worldBounds)
    {
        var worldMatrix = _transformSystem.GetWorldMatrix(grid);
        var chunks = _mapSystem.GetMapChunks(grid.Owner, grid.Comp, worldBounds);

        while (chunks.MoveNext(out var chunk))
        {
            var chunkSize = grid.Comp.ChunkSize;
            var chunkOrigin = chunk.Indices * chunkSize;

            for (ushort x = 0; x < chunkSize; x++)
            {
                for (ushort y = 0; y < chunkSize; y++)
                {
                    var tile = chunk.GetTile(x, y);
                    if (tile.IsEmpty)
                        continue;

                    var gridX = x + chunkOrigin.X;
                    var gridY = y + chunkOrigin.Y;
                    var color = TileColor(tile.TypeId);

                    var p0 = Vector2.Transform(new Vector2(gridX, gridY), worldMatrix);
                    var p1 = Vector2.Transform(new Vector2(gridX + 1, gridY), worldMatrix);
                    var p2 = Vector2.Transform(new Vector2(gridX + 1, gridY + 1), worldMatrix);
                    var p3 = Vector2.Transform(new Vector2(gridX, gridY + 1), worldMatrix);

                    AddQuad(p0, p1, p2, p3, 0f, color);

                    AddExposedSide(grid.Comp, new Vector2i(gridX, gridY - 1), p0, p1, color * 0.62f);
                    AddExposedSide(grid.Comp, new Vector2i(gridX + 1, gridY), p1, p2, color * 0.70f);
                    AddExposedSide(grid.Comp, new Vector2i(gridX, gridY + 1), p2, p3, color * 0.78f);
                    AddExposedSide(grid.Comp, new Vector2i(gridX - 1, gridY), p3, p0, color * 0.66f);
                }
            }
        }
    }

    private void AddExposedSide(
        MapGridComponent grid,
        Vector2i neighborIndices,
        Vector2 edgeA,
        Vector2 edgeB,
        Vector3 color)
    {
        if (_mapSystem.TryGetTile(grid, neighborIndices, out var neighbor) && !neighbor.IsEmpty)
            return;

        AddVerticalQuad(edgeA, edgeB, color);
    }

    private void AddVerticalQuad(Vector2 a, Vector2 b, Vector3 color)
    {
        var topA = new Vector3(a.X, a.Y, 0f);
        var topB = new Vector3(b.X, b.Y, 0f);
        var bottomA = new Vector3(a.X, a.Y, FloorBottom);
        var bottomB = new Vector3(b.X, b.Y, FloorBottom);

        AddTriangle(topA, topB, bottomB, color);
        AddTriangle(topA, bottomB, bottomA, color);
    }

    private void AddQuad(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float z, Vector3 color)
    {
        AddTriangle(
            new Vector3(p0.X, p0.Y, z),
            new Vector3(p1.X, p1.Y, z),
            new Vector3(p2.X, p2.Y, z),
            color);
        AddTriangle(
            new Vector3(p0.X, p0.Y, z),
            new Vector3(p2.X, p2.Y, z),
            new Vector3(p3.X, p3.Y, z),
            color);
    }

    private void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 color)
    {
        AddVertex(a, color);
        AddVertex(b, color);
        AddVertex(c, color);
    }

    private void AddVertex(Vector3 position, Vector3 color)
    {
        _vertices.Add(position.X);
        _vertices.Add(position.Y);
        _vertices.Add(position.Z);
        _vertices.Add(color.X);
        _vertices.Add(color.Y);
        _vertices.Add(color.Z);
    }

    private static Vector3 TileColor(int typeId)
    {
        var hash = unchecked((uint) typeId * 2654435761u + 0x9E3779B9u);
        var red = 0.22f + ((hash >> 16) & 0xFF) / 255f * 0.38f;
        var green = 0.28f + ((hash >> 8) & 0xFF) / 255f * 0.38f;
        var blue = 0.34f + (hash & 0xFF) / 255f * 0.42f;
        return new Vector3(red, green, blue);
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        _program = CreateProgram(VertexShaderSource, FragmentShaderSource);
        _mvpLocation = GL.GetUniformLocation((int) _program, "uMvp");
        if (_mvpLocation < 0)
            throw new InvalidOperationException("SS14 3D grid shader is missing uMvp.");

        GL.GenVertexArrays(1, out _vertexArray);
        GL.GenBuffers(1, out _vertexBuffer);
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);

        const int stride = FloatsPerVertex * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);

        _initialized = true;
    }

    private static uint CreateProgram(string vertexSource, string fragmentSource)
    {
        var vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);
        var program = (uint) GL.CreateProgram();

        try
        {
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
            if (linked != 1)
                throw new InvalidOperationException($"SS14 3D grid shader link failed: {GL.GetProgramInfoLog((int) program)}");
            return program;
        }
        catch
        {
            GL.DeleteProgram(program);
            throw;
        }
        finally
        {
            GL.DetachShader(program, vertexShader);
            GL.DetachShader(program, fragmentShader);
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

        var log = GL.GetShaderInfoLog((int) shader);
        GL.DeleteShader(shader);
        throw new InvalidOperationException($"SS14 3D grid shader compilation failed: {log}");
    }
}
