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
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

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
        _overlay = new World3DGridOverlay(EntityManager, _transformSystem, _mapSystem);
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
    private const float RenderRadius = 28f;
    private const float WallHeight = 2.6f;
    private const float ObjectHeight = 0.9f;
    private const float CharacterHeight = 1.7f;

    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMvp;
        uniform int uClipSpace;
        out vec3 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = uClipSpace != 0
                ? vec4(aPosition, 1.0)
                : uMvp * vec4(aPosition, 1.0);
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

    private readonly IEntityManager _entityManager;
    private readonly SharedTransformSystem _transformSystem;
    private readonly SharedMapSystem _mapSystem;
    private readonly List<float> _vertices = new(256 * 1024);

    private uint _vertexArray;
    private uint _vertexBuffer;
    private uint _program;
    private int _mvpLocation = -1;
    private int _clipSpaceLocation = -1;
    private bool _reportedGeometry;
    private bool _reportedRenderTarget;
    private bool _reportedMatrix;
    private bool _initialized;

    private readonly DiagnosticStage _diagnosticStage;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public override bool OverwriteTargetFrameBuffer => false;

    public World3DGridOverlay(
        IEntityManager entityManager,
        SharedTransformSystem transformSystem,
        SharedMapSystem mapSystem)
    {
        _entityManager = entityManager;
        _transformSystem = transformSystem;
        _mapSystem = mapSystem;
        _diagnosticStage = ParseDiagnosticStage(Environment.GetEnvironmentVariable("SS14_3D_DIAGNOSTIC"));
        ZIndex = int.MaxValue;

        System.Console.WriteLine($"[SS14-3D] render stage: {_diagnosticStage}");
    }

    protected internal override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return args.MapId != MapId.Nullspace && args.Viewport.Eye is not null;
    }

    protected internal override void Draw(in OverlayDrawArgs args)
    {
        var eye = args.Viewport.Eye;
        if (eye is null)
            return;

        _vertices.Clear();

        // Do not use the legacy 2D viewport bounds to decide what exists in our perspective view.
        // Enumerate the actual live grids on the eye's map, then select chunks in grid-local space
        // around the real IEye position.
        var eyeWorld = eye.Position.Position + eye.Offset;
        var gridCount = 0;
        foreach (var grid in _mapSystem.GetAllGrids(args.MapId))
        {
            gridCount++;
            AppendGrid(grid, eyeWorld);
        }

        AppendEntities(
            args.MapId,
            eyeWorld,
            out var staticEntityCount,
            out var movingEntityCount,
            out var characterCount);

        if (!_reportedGeometry && _vertices.Count > 0)
        {
            System.Console.WriteLine(
                $"[SS14-3D] map={args.MapId}; eye={eyeWorld.X:F1},{eyeWorld.Y:F1}; grids={gridCount}; " +
                $"static={staticEntityCount}; moving={movingEntityCount}; characters={characterCount}; " +
                $"vertices={_vertices.Count / FloatsPerVertex}");
            _reportedGeometry = true;
        }

        if (_diagnosticStage == DiagnosticStage.Tiles && _vertices.Count == 0)
            return;

        var target = new Vector3(eyeWorld.X, eyeWorld.Y, 0f);
        var forward2 = eye.Rotation.ToWorldVec();
        var camera = target + new Vector3(-forward2.X * 9f, -forward2.Y * 9f, 7f);
        var view = Matrix4x4.CreateLookAt(camera, target, Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            Math.Max(1, args.Viewport.Size.X) / (float) Math.Max(1, args.Viewport.Size.Y),
            0.05f,
            200f);
        var mvp = view * projection;

        if (!_reportedMatrix && _diagnosticStage == DiagnosticStage.WorldQuad)
        {
            var clip = Vector4.Transform(new Vector4(target, 1f), mvp);
            var ndc = clip / clip.W;
            System.Console.WriteLine(
                $"[SS14-3D] target clip={clip.X:F3},{clip.Y:F3},{clip.Z:F3},{clip.W:F3}; ndc={ndc.X:F3},{ndc.Y:F3},{ndc.Z:F3}");
            _reportedMatrix = true;
        }

        // Copy everything needed out of OverlayDrawArgs before entering the callback. OverlayDrawArgs is
        // a ref struct and cannot be captured by a lambda. Going through RenderInRenderTarget is important:
        // Clyde flushes its queued 2D work and binds the viewport's actual framebuffer before our raw GL pass.
        var renderTarget = args.Viewport.RenderTarget;
        var renderHandle = args.RenderHandle;
        var viewportSize = args.Viewport.Size;
        var vertexData = GetStageVertices(eyeWorld);

        renderHandle.RenderInRenderTarget(
            renderTarget,
            () => DrawPerspectivePass(viewportSize, vertexData, mvp, _diagnosticStage),
            null);
    }

    private unsafe void DrawPerspectivePass(
        Vector2i viewportSize,
        float[] vertexData,
        Matrix4x4 mvp,
        DiagnosticStage stage)
    {
        GL.GetInteger(GetPName.CurrentProgram, out var previousProgram);
        GL.GetInteger(GetPName.VertexArrayBinding, out var previousVertexArray);
        GL.GetInteger(GetPName.ArrayBufferBinding, out var previousArrayBuffer);
        GL.GetInteger(GetPName.DepthFunc, out var previousDepthFunc);
        var previousDepthMask = GL.GetBoolean(GetPName.DepthWritemask);
        var previousDepthTest = GL.IsEnabled(EnableCap.DepthTest);
        var previousCullFace = GL.IsEnabled(EnableCap.CullFace);
        var previousScissorTest = GL.IsEnabled(EnableCap.ScissorTest);
        var previousStencilTest = GL.IsEnabled(EnableCap.StencilTest);
        var previousBlend = GL.IsEnabled(EnableCap.Blend);

        try
        {
            EnsureInitialized();

            if (!_reportedRenderTarget)
            {
                GL.GetInteger(GetPName.DrawFramebufferBinding, out var framebuffer);
                var framebufferStatus = GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer);
                System.Console.WriteLine(
                    $"[SS14-3D] framebuffer={framebuffer}; status={framebufferStatus}; viewport={viewportSize.X}x{viewportSize.Y}");
                _reportedRenderTarget = true;
            }

            GL.Viewport(0, 0, viewportSize.X, viewportSize.Y);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.StencilTest);
            GL.Disable(EnableCap.Blend);

            if (stage == DiagnosticStage.Clear)
                GL.ClearColor(1f, 0f, 0.75f, 1f);
            else
                GL.ClearColor(0.025f, 0.035f, 0.055f, 1f);

            // Clyde's preceding lighting passes can leave depth writes disabled. A masked depth clear
            // is a no-op, so restore writes before clearing for conventional Less depth testing.
            GL.ClearDepth(1d);
            GL.DepthMask(true);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (stage is DiagnosticStage.WorldQuad or DiagnosticStage.Tiles)
            {
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc(DepthFunction.Less);
                GL.DepthMask(true);
            }
            else
            {
                GL.Disable(EnableCap.DepthTest);
                GL.DepthMask(false);
            }

            if (stage == DiagnosticStage.Clear)
                return;

            GL.UseProgram(_program);
            GL.UniformMatrix4(_mvpLocation, 1, false, (float*) &mvp);
            GL.Uniform1(_clipSpaceLocation, stage == DiagnosticStage.ClipTriangle ? 1 : 0);
            GL.BindVertexArray(_vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);

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
            GL.DepthMask(previousDepthMask);

            if (previousDepthTest)
                GL.Enable(EnableCap.DepthTest);
            else
                GL.Disable(EnableCap.DepthTest);

            if (previousCullFace)
                GL.Enable(EnableCap.CullFace);
            else
                GL.Disable(EnableCap.CullFace);

            if (previousScissorTest)
                GL.Enable(EnableCap.ScissorTest);
            else
                GL.Disable(EnableCap.ScissorTest);

            if (previousStencilTest)
                GL.Enable(EnableCap.StencilTest);
            else
                GL.Disable(EnableCap.StencilTest);

            if (previousBlend)
                GL.Enable(EnableCap.Blend);
            else
                GL.Disable(EnableCap.Blend);
        }
    }

    private float[] GetStageVertices(Vector2 eyeWorld)
    {
        switch (_diagnosticStage)
        {
            case DiagnosticStage.Clear:
                return Array.Empty<float>();
            case DiagnosticStage.ClipTriangle:
                return
                [
                    -0.85f, -0.75f, 0f, 1f, 0.1f, 0.1f,
                    0.85f, -0.75f, 0f, 0.1f, 1f, 0.1f,
                    0f, 0.85f, 0f, 0.1f, 0.3f, 1f,
                ];
            case DiagnosticStage.WorldQuad:
                _vertices.Clear();
                var center = new Vector2(eyeWorld.X, eyeWorld.Y);
                AddQuad(
                    center + new Vector2(-3f, -3f),
                    center + new Vector2(3f, -3f),
                    center + new Vector2(3f, 3f),
                    center + new Vector2(-3f, 3f),
                    0f,
                    new Vector3(1f, 0.12f, 0.72f));
                return _vertices.ToArray();
            default:
                return _vertices.ToArray();
        }
    }

    private static DiagnosticStage ParseDiagnosticStage(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "clear" => DiagnosticStage.Clear,
            "clip" => DiagnosticStage.ClipTriangle,
            "quad" => DiagnosticStage.WorldQuad,
            _ => DiagnosticStage.Tiles,
        };
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

    private void AppendGrid(Entity<MapGridComponent> grid, Vector2 eyeWorld)
    {
        var worldMatrix = _transformSystem.GetWorldMatrix(grid);
        if (!Matrix3x2.Invert(worldMatrix, out var inverseWorldMatrix))
            return;

        var eyeLocal = Vector2.Transform(eyeWorld, inverseWorldMatrix);
        var chunkSize = grid.Comp.ChunkSize;
        var radius = new Vector2(RenderRadius, RenderRadius);
        var minChunk = SharedMapSystem.GetChunkIndices(eyeLocal - radius, chunkSize);
        var maxChunk = SharedMapSystem.GetChunkIndices(eyeLocal + radius, chunkSize);

        for (var chunkX = minChunk.X; chunkX <= maxChunk.X; chunkX++)
        {
            for (var chunkY = minChunk.Y; chunkY <= maxChunk.Y; chunkY++)
            {
                if (!grid.Comp.Chunks.TryGetValue(new Vector2i(chunkX, chunkY), out var chunk))
                    continue;

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
                        var localCenter = new Vector2(gridX + 0.5f, gridY + 0.5f);
                        if (MathF.Abs(localCenter.X - eyeLocal.X) > RenderRadius ||
                            MathF.Abs(localCenter.Y - eyeLocal.Y) > RenderRadius)
                            continue;

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
    }

    private void AppendEntities(
        MapId mapId,
        Vector2 eyeWorld,
        out int staticEntityCount,
        out int movingEntityCount,
        out int characterCount)
    {
        staticEntityCount = 0;
        movingEntityCount = 0;
        characterCount = 0;

        var query = _entityManager.AllEntityQueryEnumerator<
            TransformComponent,
            PhysicsComponent,
            FixturesComponent,
            SpriteComponent>();

        while (query.MoveNext(out var uid, out var xform, out var body, out var fixtures, out var sprite))
        {
            if (xform.MapID != mapId ||
                !body.CanCollide ||
                !body.Hard ||
                !sprite._visible ||
                (sprite._containerOccluded && !sprite.OverrideContainerOcclusion) ||
                _entityManager.HasComponent<MapGridComponent>(uid))
            {
                continue;
            }

            var (worldPosition, worldRotation) = _transformSystem.GetWorldPositionRotation(xform);
            if (MathF.Abs(worldPosition.X - eyeWorld.X) > RenderRadius ||
                MathF.Abs(worldPosition.Y - eyeWorld.Y) > RenderRadius)
            {
                continue;
            }

            var physicsTransform = new Robust.Shared.Physics.Transform(worldPosition, worldRotation);
            var bounds = default(Box2);
            var hasBounds = false;

            foreach (var fixture in fixtures.Fixtures.Values)
            {
                if (!fixture.Hard)
                    continue;

                for (var child = 0; child < fixture.Shape.ChildCount; child++)
                {
                    var childBounds = fixture.Shape.ComputeAABB(physicsTransform, child);
                    bounds = hasBounds ? bounds.Union(childBounds) : childBounds;
                    hasBounds = true;
                }
            }

            if (!hasBounds || bounds.Width < 0.04f || bounds.Height < 0.04f)
                continue;

            if ((body.BodyType & BodyType.KinematicController) != 0)
            {
                characterCount++;
                AddCharacter(bounds, EntityColor(uid, true));
                continue;
            }

            if (body.BodyType != BodyType.Static)
            {
                movingEntityCount++;
                AddBox(bounds, 0.02f, ObjectHeight * 0.72f, EntityColor(uid, true) * 0.84f);
                continue;
            }

            staticEntityCount++;
            if (_entityManager.TryGetComponent(uid, out OccluderComponent? occluder) && occluder.Enabled)
            {
                AddPrism(
                    occluder.Polygon,
                    _transformSystem.GetWorldMatrix(xform),
                    0.01f,
                    WallHeight,
                    EntityColor(uid, false));
                continue;
            }

            var height = bounds.MaxDimension > 1.4f ? ObjectHeight * 1.35f : ObjectHeight;
            AddBox(bounds, 0.01f, height, EntityColor(uid, false) * 0.82f);
        }
    }

    private void AddBox(Box2 bounds, float bottom, float top, Vector3 color)
    {
        var p0 = bounds.BottomLeft;
        var p1 = bounds.BottomRight;
        var p2 = bounds.TopRight;
        var p3 = bounds.TopLeft;

        AddQuad(p0, p1, p2, p3, top, Lighten(color, 1.18f));
        AddWallSide(p0, p1, bottom, top, color * 0.66f);
        AddWallSide(p1, p2, bottom, top, color * 0.76f);
        AddWallSide(p2, p3, bottom, top, color * 0.86f);
        AddWallSide(p3, p0, bottom, top, color * 0.72f);
    }

    private void AddCharacter(Box2 bounds, Vector3 color)
    {
        var radius = Math.Clamp(MathF.Max(bounds.Width, bounds.Height) * 0.48f, 0.22f, 0.46f);
        AddCylinder(bounds.Center, radius, 0.02f, CharacterHeight * 0.72f, color, 8);
        AddCylinder(
            bounds.Center,
            radius * 0.72f,
            CharacterHeight * 0.72f,
            CharacterHeight,
            Lighten(color, 1.12f),
            8);
    }

    private void AddCylinder(
        Vector2 center,
        float radius,
        float bottom,
        float top,
        Vector3 color,
        int segments)
    {
        var ring = new Vector2[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = MathF.Tau * i / segments;
            ring[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }

        var topCenter = new Vector3(center, top);
        var topColor = Lighten(color, 1.16f);
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            AddTriangle(topCenter, new Vector3(ring[i], top), new Vector3(ring[next], top), topColor);
            AddWallSide(ring[i], ring[next], bottom, top, color * (0.68f + i % 4 * 0.06f));
        }
    }

    private void AddPrism(
        ReadOnlySpan<Vector2> localPolygon,
        Matrix3x2 worldMatrix,
        float bottom,
        float top,
        Vector3 color)
    {
        if (localPolygon.Length < 3)
            return;

        var worldPolygon = new Vector2[localPolygon.Length];
        for (var i = 0; i < localPolygon.Length; i++)
            worldPolygon[i] = Vector2.Transform(localPolygon[i], worldMatrix);

        var topColor = Lighten(color, 1.18f);
        for (var i = 1; i < worldPolygon.Length - 1; i++)
        {
            AddTriangle(
                new Vector3(worldPolygon[0], top),
                new Vector3(worldPolygon[i], top),
                new Vector3(worldPolygon[i + 1], top),
                topColor);
        }

        for (var i = 0; i < worldPolygon.Length; i++)
        {
            var next = (i + 1) % worldPolygon.Length;
            AddWallSide(worldPolygon[i], worldPolygon[next], bottom, top, color * (0.66f + i % 3 * 0.08f));
        }
    }

    private void AddWallSide(Vector2 a, Vector2 b, float bottom, float top, Vector3 color)
    {
        var bottomA = new Vector3(a, bottom);
        var bottomB = new Vector3(b, bottom);
        var topA = new Vector3(a, top);
        var topB = new Vector3(b, top);

        AddTriangle(bottomA, bottomB, topB, color);
        AddTriangle(bottomA, topB, topA, color);
    }

    private static Vector3 EntityColor(EntityUid uid, bool dynamic)
    {
        var hash = unchecked((uint) uid.GetHashCode() * 2246822519u + 3266489917u);
        if (dynamic)
        {
            return new Vector3(
                0.78f + ((hash >> 16) & 0xFF) / 255f * 0.18f,
                0.34f + ((hash >> 8) & 0xFF) / 255f * 0.24f,
                0.16f + (hash & 0xFF) / 255f * 0.18f);
        }

        return new Vector3(
            0.30f + ((hash >> 16) & 0xFF) / 255f * 0.16f,
            0.43f + ((hash >> 8) & 0xFF) / 255f * 0.18f,
            0.52f + (hash & 0xFF) / 255f * 0.20f);
    }

    private static Vector3 Lighten(Vector3 color, float factor)
    {
        return Vector3.Min(color * factor, Vector3.One);
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
        _clipSpaceLocation = GL.GetUniformLocation((int) _program, "uClipSpace");
        if (_mvpLocation < 0)
            throw new InvalidOperationException("SS14 3D grid shader is missing uMvp.");
        if (_clipSpaceLocation < 0)
            throw new InvalidOperationException("SS14 3D grid shader is missing uClipSpace.");

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

    private enum DiagnosticStage
    {
        Clear,
        ClipTriangle,
        WorldQuad,
        Tiles,
    }
}
