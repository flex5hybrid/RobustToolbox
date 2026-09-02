using System;
using System.Collections.Generic;
using System.Numerics;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client.Graphics;
using Robust.Client.Graphics.Clyde;
using Robust.Client.Map;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics3D;

namespace Robust.Client.GameObjects;

/// <summary>
/// First bridge between the normal SS14 client and the experimental 3D renderer.
/// The simulation, networking, eye and map grids are all the normal Robust ECS objects;
/// only the final world presentation is replaced by a perspective OpenGL pass.
/// </summary>
public sealed partial class World3DGridRenderingSystem : EntitySystem
{
    public const float DefaultFirstPersonPitch = -0.075f;

    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private TransformSystem _transformSystem = default!;
    [Dependency] private SharedTransform3DSystem _transform3DSystem = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private IClydeTileDefinitionManager _tileDefinitionManager = default!;
    [Dependency] private IClydeInternal _clyde = default!;
    [Dependency] private IPlayerManager _playerManager = default!;

    private World3DGridOverlay? _overlay;
    private EntityUid? _presentationEntity;
    private float _walkBobPhase;
    private float _cameraBob;
    private float _firstPersonYaw;
    private float _firstPersonPitch = DefaultFirstPersonPitch;

    public override void Initialize()
    {
        _overlay = new World3DGridOverlay(
            EntityManager,
            _transformSystem,
            _transform3DSystem,
            _mapSystem,
            _tileDefinitionManager,
            _clyde);
        _overlayManager.AddOverlay(_overlay);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var localEntity = _playerManager.LocalEntity;
        if (localEntity != _presentationEntity)
        {
            _presentationEntity = localEntity;
            _walkBobPhase = 0f;
            _cameraBob = 0f;
        }

        var bobTarget = 0f;
        if (localEntity is { Valid: true } uid &&
            TryComp(uid, out PhysicsBody3DComponent? body) &&
            TryComp(uid, out CharacterController3DComponent? character) &&
            character.Grounded &&
            new Vector2(body.LinearVelocity.X, body.LinearVelocity.Y).LengthSquared() > 0.04f)
        {
            var speed = MathF.Min(new Vector2(body.LinearVelocity.X, body.LinearVelocity.Y).Length(), 7f);
            _walkBobPhase += frameTime * (7.5f + speed * 1.35f);
            bobTarget = MathF.Sin(_walkBobPhase) * 0.035f;
        }

        _cameraBob += (bobTarget - _cameraBob) * MathF.Min(1f, frameTime * 14f);
        _overlay?.SetLocalPlayerPresentation(localEntity, _cameraBob);
    }

    public void SetFirstPersonView(float yaw, float pitch)
    {
        if (!float.IsFinite(yaw) || !float.IsFinite(pitch))
            return;

        _firstPersonYaw = yaw;
        _firstPersonPitch = Math.Clamp(pitch, -1.35f, 1.35f);
        _overlay?.SetFirstPersonView(_firstPersonYaw, _firstPersonPitch);
    }

    /// <summary>
    /// Overrides the set of map spaces rendered by the perspective pass. The active viewport map is
    /// always included as a fallback. This lets content-level spatial systems, such as stacked decks,
    /// expose several legacy MapIds as one 3D world without making Robust depend on content types.
    /// </summary>
    public void SetRenderMaps(IEnumerable<MapId> mapIds)
    {
        _overlay?.SetRenderMaps(mapIds);
    }

    public void ClearRenderMaps()
    {
        _overlay?.ClearRenderMaps();
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

internal sealed partial class World3DGridOverlay : Overlay
{
    private const int FloatsPerVertex = 8;
    private const float FloorBottom = -0.12f;
    private const float RenderRadius = 28f;
    private const float WallHeight = 2.6f;
    private const float ObjectHeight = 0.9f;
    private const float CharacterHeight = 1.7f;
    private const float FirstPersonEyeHeight = 1.58f;
    private static readonly float[] CrosshairVertices = CreateCrosshairVertices();

    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aColor;
        layout(location = 2) in vec2 aUv;
        uniform mat4 uMvp;
        uniform int uClipSpace;
        out vec3 vColor;
        out vec2 vUv;
        void main()
        {
            vColor = aColor;
            vUv = aUv;
            gl_Position = uClipSpace != 0
                ? vec4(aPosition, 1.0)
                : uMvp * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core
        in vec3 vColor;
        in vec2 vUv;
        uniform sampler2D uTexture;
        uniform int uUseTexture;
        out vec4 fragColor;
        void main()
        {
            if (uUseTexture != 0)
            {
                vec4 sampleColor = texture(uTexture, vUv);
                if (sampleColor.a < 0.08)
                    discard;
                fragColor = vec4(vColor * sampleColor.rgb, 1.0);
            }
            else
            {
                fragColor = vec4(vColor, 1.0);
            }
        }
        """;

    private readonly IEntityManager _entityManager;
    private readonly SharedTransformSystem _transformSystem;
    private readonly SharedTransform3DSystem _transform3DSystem;
    private readonly SharedMapSystem _mapSystem;
    private readonly IClydeTileDefinitionManager _tileDefinitionManager;
    private readonly IClydeInternal _clyde;
    private readonly List<float> _vertices = new(256 * 1024);
    private readonly List<float> _tileVertices = new(256 * 1024);
    private readonly HashSet<MapId> _renderMaps = new();

    private uint _vertexArray;
    private uint _vertexBuffer;
    private uint _program;
    private int _mvpLocation = -1;
    private int _clipSpaceLocation = -1;
    private int _textureLocation = -1;
    private int _useTextureLocation = -1;
    private bool _reportedGeometry;
    private bool _reportedRenderTarget;
    private bool _reportedMatrix;
    private bool _initialized;
    private EntityUid? _localPlayer;
    private float _localCameraBob;
    private float _firstPersonYaw;
    private float _firstPersonPitch = World3DGridRenderingSystem.DefaultFirstPersonPitch;

    private readonly DiagnosticStage _diagnosticStage;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public override bool OverwriteTargetFrameBuffer => false;

    public World3DGridOverlay(
        IEntityManager entityManager,
        SharedTransformSystem transformSystem,
        SharedTransform3DSystem transform3DSystem,
        SharedMapSystem mapSystem,
        IClydeTileDefinitionManager tileDefinitionManager,
        IClydeInternal clyde)
    {
        _entityManager = entityManager;
        _transformSystem = transformSystem;
        _transform3DSystem = transform3DSystem;
        _mapSystem = mapSystem;
        _tileDefinitionManager = tileDefinitionManager;
        _clyde = clyde;
        _diagnosticStage = ParseDiagnosticStage(Environment.GetEnvironmentVariable("SS14_3D_DIAGNOSTIC"));
        ZIndex = int.MaxValue;

        System.Console.WriteLine($"[SS14-3D] render stage: {_diagnosticStage}");
    }

    public void SetLocalPlayerPresentation(EntityUid? localPlayer, float cameraBob)
    {
        _localPlayer = localPlayer;
        _localCameraBob = cameraBob;
    }

    public void SetFirstPersonView(float yaw, float pitch)
    {
        _firstPersonYaw = yaw;
        _firstPersonPitch = pitch;
    }

    public void SetRenderMaps(IEnumerable<MapId> mapIds)
    {
        _renderMaps.Clear();
        foreach (var mapId in mapIds)
        {
            if (mapId != MapId.Nullspace)
                _renderMaps.Add(mapId);
        }
    }

    public void ClearRenderMaps()
    {
        _renderMaps.Clear();
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
        _tileVertices.Clear();
        ClearSpriteBatches();

        // X/Y still follow the authoritative SS14 eye/transform. Base Z now comes from the real
        // Transform3D hierarchy, so the camera automatically follows a grid/deck when it receives height.
        var eyeWorld = eye.Position.Position;
        var cameraBase = new Vector3(eyeWorld, 0f);
        if (_localPlayer is { } localPlayer &&
            _entityManager.TryGetComponent(localPlayer, out TransformComponent? localTransform))
        {
            cameraBase = _transform3DSystem.GetWorldPosition3D(localPlayer, localTransform);
        }

        var camera = cameraBase + new Vector3(
            0f,
            0f,
            FirstPersonEyeHeight + _localCameraBob);

        var horizontalLook = MathF.Cos(_firstPersonPitch);
        var lookDirection = new Vector3(
            MathF.Sin(_firstPersonYaw) * horizontalLook,
            MathF.Cos(_firstPersonYaw) * horizontalLook,
            MathF.Sin(_firstPersonPitch));
        var target = camera + lookDirection;

        var billboardForward = new Vector2(lookDirection.X, lookDirection.Y);
        if (billboardForward.LengthSquared() < 1e-6f)
            billboardForward = new Vector2(MathF.Sin(_firstPersonYaw), MathF.Cos(_firstPersonYaw));
        billboardForward = Vector2.Normalize(billboardForward);
        var billboardRight = new Vector2(-billboardForward.Y, billboardForward.X);
        var cameraFacingRotation = new Angle(-_firstPersonYaw);

        // Legacy SS14 still stores separate floors in separate MapIds. Content can now provide a set
        // of those maps that represent one 3D space, and the renderer composes all of them into this pass.
        var gridCount = 0;
        var staticEntityCount = 0;
        var movingEntityCount = 0;
        var characterCount = 0;
        var renderedActiveMap = false;

        if (_renderMaps.Count > 0)
        {
            foreach (var mapId in _renderMaps)
            {
                AppendMap(
                    mapId,
                    eyeWorld,
                    cameraFacingRotation,
                    billboardRight,
                    billboardForward,
                    ref gridCount,
                    ref staticEntityCount,
                    ref movingEntityCount,
                    ref characterCount);
                renderedActiveMap |= mapId == args.MapId;
            }
        }

        if (_renderMaps.Count == 0 || !renderedActiveMap)
        {
            AppendMap(
                args.MapId,
                eyeWorld,
                cameraFacingRotation,
                billboardRight,
                billboardForward,
                ref gridCount,
                ref staticEntityCount,
                ref movingEntityCount,
                ref characterCount);
        }

        var totalVertexCount = (_vertices.Count + _tileVertices.Count) / FloatsPerVertex;
        if (!_reportedGeometry && totalVertexCount > 0)
        {
            var mapCount = _renderMaps.Count == 0 ? 1 : _renderMaps.Count + (renderedActiveMap ? 0 : 1);
            System.Console.WriteLine(
                $"[SS14-3D] map={args.MapId}; maps={mapCount}; eye={eyeWorld.X:F1},{eyeWorld.Y:F1},{cameraBase.Z:F1}; grids={gridCount}; " +
                $"static={staticEntityCount}; moving={movingEntityCount}; characters={characterCount}; " +
                $"camera=first-person; vertices={totalVertexCount}; textured={_tileVertices.Count / FloatsPerVertex}; spriteVertices={GetSpriteVertexCount()}");
            _reportedGeometry = true;
        }

        if (_diagnosticStage == DiagnosticStage.Tiles && totalVertexCount == 0)
            return;

        var view = Matrix4x4.CreateLookAt(camera, target, Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI * 75f / 180f,
            Math.Max(1, args.Viewport.Size.X) / (float) Math.Max(1, args.Viewport.Size.Y),
            0.035f,
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
        var solidVertexData = GetStageVertices(eyeWorld);
        var tileVertexData = _diagnosticStage == DiagnosticStage.Tiles
            ? _tileVertices.ToArray()
            : Array.Empty<float>();
        var spriteBatches = _diagnosticStage == DiagnosticStage.Tiles
            ? SnapshotSpriteBatches()
            : Array.Empty<SpriteBatch>();
        var tileAtlasHandle = GetTileAtlasHandle();

        renderHandle.RenderInRenderTarget(
            renderTarget,
            () => DrawPerspectivePass(
                viewportSize,
                solidVertexData,
                tileVertexData,
                spriteBatches,
                tileAtlasHandle,
                mvp,
                _diagnosticStage),
            null);
    }

    private unsafe void DrawPerspectivePass(
        Vector2i viewportSize,
        float[] solidVertexData,
        float[] tileVertexData,
        SpriteBatch[] spriteBatches,
        uint tileAtlasHandle,
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
        GL.GetInteger(GetPName.ActiveTexture, out var previousActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out var previousTexture0);

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
            GL.Uniform1(_textureLocation, 0);
            GL.BindVertexArray(_vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);

            DrawVertexData(solidVertexData, false, 0);
            if (tileAtlasHandle != 0)
                DrawVertexData(tileVertexData, true, tileAtlasHandle);
            else
                DrawVertexData(tileVertexData, false, 0);

            DrawSpriteBatches(spriteBatches);

            if (stage == DiagnosticStage.Tiles && _localPlayer is not null)
            {
                GL.Disable(EnableCap.DepthTest);
                GL.DepthMask(false);
                GL.Uniform1(_clipSpaceLocation, 1);
                DrawVertexData(CrosshairVertices, false, 0);
            }
        }
        finally
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, (uint) previousTexture0);
            GL.ActiveTexture((TextureUnit) previousActiveTexture);
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

    private unsafe void DrawVertexData(float[] vertexData, bool textured, uint textureHandle)
    {
        if (vertexData.Length == 0)
            return;

        GL.Uniform1(_useTextureLocation, textured ? 1 : 0);
        if (textured)
            GL.BindTexture(TextureTarget.Texture2D, textureHandle);

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

    private uint GetTileAtlasHandle()
    {
        if (_tileDefinitionManager.TileTextureAtlas is not Clyde.ClydeTexture atlas)
            return 0;

        foreach (var (texture, loaded) in _clyde.GetLoadedTextures())
        {
            if (ReferenceEquals(texture, atlas))
                return loaded.OpenGLObject.Handle;
        }

        return 0;
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
                    -0.85f, -0.75f, 0f, 1f, 0.1f, 0.1f, 0f, 0f,
                    0.85f, -0.75f, 0f, 0.1f, 1f, 0.1f, 0f, 0f,
                    0f, 0.85f, 0f, 0.1f, 0.3f, 1f, 0f, 0f,
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

    private void AppendMap(
        MapId mapId,
        Vector2 eyeWorld,
        Angle eyeRotation,
        Vector2 billboardRight,
        Vector2 billboardForward,
        ref int gridCount,
        ref int staticEntityCount,
        ref int movingEntityCount,
        ref int characterCount)
    {
        if (mapId == MapId.Nullspace)
            return;

        foreach (var grid in _mapSystem.GetAllGrids(mapId))
        {
            gridCount++;
            AppendGrid(grid, eyeWorld);
        }

        AppendEntities(
            mapId,
            eyeWorld,
            eyeRotation,
            billboardRight,
            billboardForward,
            out var mapStaticCount,
            out var mapMovingCount,
            out var mapCharacterCount);

        AppendNative3DEntities(mapId, eyeWorld, ref mapStaticCount, ref mapMovingCount);

        staticEntityCount += mapStaticCount;
        movingEntityCount += mapMovingCount;
        characterCount += mapCharacterCount;
    }

    private void AppendNative3DEntities(
        MapId mapId,
        Vector2 eyeWorld,
        ref int staticEntityCount,
        ref int movingEntityCount)
    {
        var query = _entityManager.AllEntityQueryEnumerator<
            TransformComponent,
            Transform3DComponent,
            Primitive3DComponent>();

        while (query.MoveNext(out var uid, out var transform, out var transform3D, out var primitive))
        {
            if (transform.MapID != mapId ||
                !transform3D.Authoritative ||
                !primitive.Visible ||
                uid == _localPlayer ||
                !SpatialMath.IsFinite(primitive.Size) ||
                primitive.Size.X <= 0f ||
                primitive.Size.Y <= 0f ||
                primitive.Size.Z <= 0f)
            {
                continue;
            }

            var position = _transform3DSystem.GetWorldPosition3D(uid, transform);
            if (MathF.Abs(position.X - eyeWorld.X) > RenderRadius ||
                MathF.Abs(position.Y - eyeWorld.Y) > RenderRadius)
            {
                continue;
            }

            var color = new Vector3(primitive.Color.R, primitive.Color.G, primitive.Color.B);
            AddOrientedBox(_transform3DSystem.GetWorldMatrix3D(uid, transform), primitive.Size, color);

            if (_entityManager.TryGetComponent(uid, out PhysicsBody3DComponent? body) &&
                body.BodyType != PhysicsBodyType3D.Static)
            {
                movingEntityCount++;
            }
            else
            {
                staticEntityCount++;
            }
        }
    }

    private void AddOrientedBox(Matrix4x4 worldMatrix, Vector3 size, Vector3 color)
    {
        var half = size * 0.5f;
        Span<Vector3> corners = stackalloc Vector3[8]
        {
            new(-half.X, -half.Y, -half.Z),
            new( half.X, -half.Y, -half.Z),
            new( half.X,  half.Y, -half.Z),
            new(-half.X,  half.Y, -half.Z),
            new(-half.X, -half.Y,  half.Z),
            new( half.X, -half.Y,  half.Z),
            new( half.X,  half.Y,  half.Z),
            new(-half.X,  half.Y,  half.Z),
        };

        for (var i = 0; i < corners.Length; i++)
            corners[i] = Vector3.Transform(corners[i], worldMatrix);

        AddFace(corners[0], corners[3], corners[2], corners[1], color * 0.48f);
        AddFace(corners[4], corners[5], corners[6], corners[7], Lighten(color, 1.18f));
        AddFace(corners[0], corners[1], corners[5], corners[4], color * 0.66f);
        AddFace(corners[1], corners[2], corners[6], corners[5], color * 0.76f);
        AddFace(corners[2], corners[3], corners[7], corners[6], color * 0.86f);
        AddFace(corners[3], corners[0], corners[4], corners[7], color * 0.72f);
    }

    private void AddFace(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 color)
    {
        AddTriangle(p0, p1, p2, color);
        AddTriangle(p0, p2, p3, color);
    }

    private void AppendGrid(Entity<MapGridComponent> grid, Vector2 eyeWorld)
    {
        var worldMatrix = _transformSystem.GetWorldMatrix(grid);
        if (!Matrix3x2.Invert(worldMatrix, out var inverseWorldMatrix))
            return;

        var gridZ = _transform3DSystem.GetWorldZ(grid.Owner);
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

                        var p0 = Vector2.Transform(new Vector2(gridX, gridY), worldMatrix);
                        var p1 = Vector2.Transform(new Vector2(gridX + 1, gridY), worldMatrix);
                        var p2 = Vector2.Transform(new Vector2(gridX + 1, gridY + 1), worldMatrix);
                        var p3 = Vector2.Transform(new Vector2(gridX, gridY + 1), worldMatrix);

                        var color = TileColor(tile.TypeId);
                        AddTexturedTile(p0, p1, p2, p3, tile, gridZ);

                        AddExposedSide(grid.Comp, new Vector2i(gridX, gridY - 1), p0, p1, gridZ, color * 0.62f);
                        AddExposedSide(grid.Comp, new Vector2i(gridX + 1, gridY), p1, p2, gridZ, color * 0.70f);
                        AddExposedSide(grid.Comp, new Vector2i(gridX, gridY + 1), p2, p3, gridZ, color * 0.78f);
                        AddExposedSide(grid.Comp, new Vector2i(gridX - 1, gridY), p3, p0, gridZ, color * 0.66f);
                    }
                }
            }
        }
    }

    private void AppendEntities(
        MapId mapId,
        Vector2 eyeWorld,
        Angle eyeRotation,
        Vector2 billboardRight,
        Vector2 billboardForward,
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
            var worldPosition3D = _transform3DSystem.GetWorldPosition3D(uid, xform);
            var baseZ = worldPosition3D.Z;
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
                if (uid != _localPlayer)
                    AddCharacter(bounds, baseZ, EntityColor(uid, true));
                continue;
            }

            if (body.BodyType != BodyType.Static)
            {
                movingEntityCount++;
                if (!TryAppendSpriteBillboard(
                        sprite,
                        worldRotation,
                        eyeRotation,
                        worldPosition3D,
                        billboardRight,
                        billboardForward))
                {
                    AddBox(bounds, baseZ + 0.02f, baseZ + ObjectHeight * 0.72f, EntityColor(uid, true) * 0.84f);
                }
                continue;
            }

            staticEntityCount++;
            if (_entityManager.TryGetComponent(uid, out OccluderComponent? occluder) && occluder.Enabled)
            {
                var worldMatrix = _transformSystem.GetWorldMatrix(xform);
                AddPrism(
                    occluder.Polygon,
                    worldMatrix,
                    baseZ + 0.01f,
                    baseZ + WallHeight,
                    EntityColor(uid, false));
                continue;
            }

            var height = bounds.MaxDimension > 1.4f ? ObjectHeight * 1.35f : ObjectHeight;
            AddBox(bounds, baseZ + 0.01f, baseZ + height, EntityColor(uid, false) * 0.82f);
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

    private void AddCharacter(Box2 bounds, float baseZ, Vector3 color)
    {
        var radius = Math.Clamp(MathF.Max(bounds.Width, bounds.Height) * 0.48f, 0.22f, 0.46f);
        AddCylinder(
            bounds.Center,
            radius,
            baseZ + 0.02f,
            baseZ + CharacterHeight * 0.72f,
            color,
            8);
        AddCylinder(
            bounds.Center,
            radius * 0.72f,
            baseZ + CharacterHeight * 0.72f,
            baseZ + CharacterHeight,
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
        float z,
        Vector3 color)
    {
        if (_mapSystem.TryGetTile(grid, neighborIndices, out var neighbor) && !neighbor.IsEmpty)
            return;

        AddVerticalQuad(edgeA, edgeB, z, color);
    }

    private void AddVerticalQuad(Vector2 a, Vector2 b, float z, Vector3 color)
    {
        var topA = new Vector3(a.X, a.Y, z);
        var topB = new Vector3(b.X, b.Y, z);
        var bottomA = new Vector3(a.X, a.Y, z + FloorBottom);
        var bottomB = new Vector3(b.X, b.Y, z + FloorBottom);

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

    private void AddTexturedTile(
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        Tile tile,
        float z)
    {
        var regions = _tileDefinitionManager.TileAtlasRegion(tile);
        var region = regions is not null && tile.Variant < regions.Length
            ? regions[tile.Variant]
            : _tileDefinitionManager.ErrorTileRegion;

        var rotationMirroring = _tileDefinitionManager.TryGetDefinition(tile.TypeId, out var definition) &&
                                definition.AllowRotationMirror
            ? tile.RotationMirroring
            : 0;
        GetTileUvs(region, rotationMirroring, out var uv0, out var uv1, out var uv2, out var uv3);

        // The atlas is generated before the regular client begins rendering. Exposed floor sides retain
        // their generated shading, while the top uses the source texture unchanged.
        var color = Vector3.One;
        AddTexturedTriangle(
            new Vector3(p0, z),
            new Vector3(p1, z),
            new Vector3(p2, z),
            uv0,
            uv1,
            uv2,
            color);
        AddTexturedTriangle(
            new Vector3(p0, z),
            new Vector3(p2, z),
            new Vector3(p3, z),
            uv0,
            uv2,
            uv3,
            color);
    }

    private static void GetTileUvs(
        Box2 region,
        int rotationMirroring,
        out Vector2 uv0,
        out Vector2 uv1,
        out Vector2 uv2,
        out Vector2 uv3)
    {
        uv0 = new Vector2(region.Left, region.Bottom);
        uv1 = new Vector2(region.Right, region.Bottom);
        uv2 = new Vector2(region.Right, region.Top);
        uv3 = new Vector2(region.Left, region.Top);

        for (var rotation = 0; rotation < rotationMirroring % 4; rotation++)
            (uv0, uv1, uv2, uv3) = (uv3, uv0, uv1, uv2);

        if (rotationMirroring < 4)
            return;

        if (rotationMirroring % 2 == 0)
        {
            uv0.X = FlipUv(uv0.X, region.Left, region.Right);
            uv1.X = FlipUv(uv1.X, region.Left, region.Right);
            uv2.X = FlipUv(uv2.X, region.Left, region.Right);
            uv3.X = FlipUv(uv3.X, region.Left, region.Right);
        }
        else
        {
            uv0.Y = FlipUv(uv0.Y, region.Bottom, region.Top);
            uv1.Y = FlipUv(uv1.Y, region.Bottom, region.Top);
            uv2.Y = FlipUv(uv2.Y, region.Bottom, region.Top);
            uv3.Y = FlipUv(uv3.Y, region.Bottom, region.Top);
        }
    }

    private static float FlipUv(float value, float minimum, float maximum)
    {
        return MathF.Abs(value - minimum) < 0.00001f ? maximum : minimum;
    }

    private void AddTexturedTriangle(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector2 uvA,
        Vector2 uvB,
        Vector2 uvC,
        Vector3 color)
    {
        AddVertex(_tileVertices, a, color, uvA);
        AddVertex(_tileVertices, b, color, uvB);
        AddVertex(_tileVertices, c, color, uvC);
    }

    private void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 color)
    {
        AddVertex(a, color);
        AddVertex(b, color);
        AddVertex(c, color);
    }

    private void AddVertex(Vector3 position, Vector3 color)
    {
        AddVertex(_vertices, position, color, Vector2.Zero);
    }

    private static void AddVertex(List<float> vertices, Vector3 position, Vector3 color, Vector2 uv)
    {
        vertices.Add(position.X);
        vertices.Add(position.Y);
        vertices.Add(position.Z);
        vertices.Add(color.X);
        vertices.Add(color.Y);
        vertices.Add(color.Z);
        vertices.Add(uv.X);
        vertices.Add(uv.Y);
    }

    private static float[] CreateCrosshairVertices()
    {
        var vertices = new List<float>(24 * FloatsPerVertex);
        var color = new Vector3(0.92f, 0.96f, 1f);
        const float gap = 0.007f;
        const float length = 0.022f;
        const float thickness = 0.0022f;

        AddClipQuad(vertices, -gap - length, -thickness, -gap, thickness, color);
        AddClipQuad(vertices, gap, -thickness, gap + length, thickness, color);
        AddClipQuad(vertices, -thickness, gap, thickness, gap + length, color);
        AddClipQuad(vertices, -thickness, -gap - length, thickness, -gap, color);
        return vertices.ToArray();
    }

    private static void AddClipQuad(
        List<float> vertices,
        float left,
        float bottom,
        float right,
        float top,
        Vector3 color)
    {
        var p0 = new Vector3(left, bottom, 0f);
        var p1 = new Vector3(right, bottom, 0f);
        var p2 = new Vector3(right, top, 0f);
        var p3 = new Vector3(left, top, 0f);
        AddVertex(vertices, p0, color, Vector2.Zero);
        AddVertex(vertices, p1, color, Vector2.Zero);
        AddVertex(vertices, p2, color, Vector2.Zero);
        AddVertex(vertices, p0, color, Vector2.Zero);
        AddVertex(vertices, p2, color, Vector2.Zero);
        AddVertex(vertices, p3, color, Vector2.Zero);
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
        _textureLocation = GL.GetUniformLocation((int) _program, "uTexture");
        _useTextureLocation = GL.GetUniformLocation((int) _program, "uUseTexture");
        if (_mvpLocation < 0)
            throw new InvalidOperationException("SS14 3D grid shader is missing uMvp.");
        if (_clipSpaceLocation < 0)
            throw new InvalidOperationException("SS14 3D grid shader is missing uClipSpace.");
        if (_textureLocation < 0 || _useTextureLocation < 0)
            throw new InvalidOperationException("SS14 3D grid shader is missing texture uniforms.");

        GL.GenVertexArrays(1, out _vertexArray);
        GL.GenBuffers(1, out _vertexBuffer);
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);

        const int stride = FloatsPerVertex * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);
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
