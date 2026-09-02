using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Maths;

namespace Robust.Client.GameObjects;

internal sealed partial class World3DGridOverlay
{
    private const float SpriteFloorOffset = 0.025f;
    private const float SpriteLayerDepthStep = 0.0008f;

    private readonly Dictionary<uint, List<float>> _spriteVertices = new();
    private readonly Dictionary<Texture, uint> _spriteTextureHandles = new();

    private readonly record struct SpriteBatch(uint TextureHandle, float[] Vertices);

    private void ClearSpriteBatches()
    {
        _spriteVertices.Clear();
        _spriteTextureHandles.Clear();

        foreach (var (texture, loaded) in _clyde.GetLoadedTextures())
        {
            var handle = loaded.OpenGLObject.Handle;
            if (handle != 0)
                _spriteTextureHandles[texture] = handle;
        }
    }

    private int GetSpriteVertexCount()
    {
        var count = 0;
        foreach (var vertices in _spriteVertices.Values)
            count += vertices.Count / FloatsPerVertex;
        return count;
    }

    private SpriteBatch[] SnapshotSpriteBatches()
    {
        if (_spriteVertices.Count == 0)
            return Array.Empty<SpriteBatch>();

        var result = new SpriteBatch[_spriteVertices.Count];
        var index = 0;
        foreach (var (textureHandle, vertices) in _spriteVertices)
            result[index++] = new SpriteBatch(textureHandle, vertices.ToArray());
        return result;
    }

    private void DrawSpriteBatches(SpriteBatch[] batches)
    {
        foreach (var batch in batches)
            DrawVertexData(batch.Vertices, true, batch.TextureHandle);
    }

    /// <summary>
    /// Converts the live SS14 sprite of a dynamic world object into a vertical camera-facing 3D billboard.
    /// Every drawn SpriteComponent layer keeps its current RSI direction, animation frame, local transform and tint.
    /// The lowest transformed pixel quad is lifted onto the entity's current Transform3D floor height.
    /// </summary>
    private bool TryAppendSpriteBillboard(
        SpriteComponent sprite,
        Angle worldRotation,
        Angle eyeRotation,
        Vector3 worldPosition,
        Vector2 billboardRight,
        Vector2 billboardForward)
    {
        if (sprite.Layers.Count == 0)
            return false;

        var apparentAngle = (worldRotation + eyeRotation).Reduced().FlipPositive();
        var minimumLocalY = float.PositiveInfinity;
        var hasDrawableLayer = false;

        // First pass finds the real bottom of the composed sprite after its layer/component transforms.
        // This avoids half of a centered 2D icon disappearing through the 3D floor.
        foreach (var layer in sprite.Layers)
        {
            if (!TryGetSpriteLayerFrame(sprite, layer, apparentAngle, out var texture, out var direction))
                continue;

            layer.GetLayerDrawMatrix(direction, out var layerMatrix, sprite.NoRotation);
            var localMatrix = Matrix3x2.Multiply(layerMatrix, sprite.LocalMatrix);
            GetTextureQuad(texture, out var p0, out var p1, out var p2, out var p3);

            minimumLocalY = MathF.Min(minimumLocalY, Vector2.Transform(p0, localMatrix).Y);
            minimumLocalY = MathF.Min(minimumLocalY, Vector2.Transform(p1, localMatrix).Y);
            minimumLocalY = MathF.Min(minimumLocalY, Vector2.Transform(p2, localMatrix).Y);
            minimumLocalY = MathF.Min(minimumLocalY, Vector2.Transform(p3, localMatrix).Y);
            hasDrawableLayer = true;
        }

        if (!hasDrawableLayer || !float.IsFinite(minimumLocalY))
            return false;

        var baseHeight = worldPosition.Z + SpriteFloorOffset - minimumLocalY;
        var drawOrder = 0;
        var appendedAny = false;

        foreach (var layer in sprite.Layers)
        {
            if (!TryGetSpriteLayerFrame(sprite, layer, apparentAngle, out var texture, out var direction) ||
                !TryResolveSpriteTexture(texture, out var textureHandle, out var uvRegion))
            {
                continue;
            }

            var modulation = sprite.color * layer.Color;
            if (modulation.A <= 0.01f)
                continue;

            layer.GetLayerDrawMatrix(direction, out var layerMatrix, sprite.NoRotation);
            var localMatrix = Matrix3x2.Multiply(layerMatrix, sprite.LocalMatrix);
            GetTextureQuad(texture, out var local0, out var local1, out var local2, out var local3);

            local0 = Vector2.Transform(local0, localMatrix);
            local1 = Vector2.Transform(local1, localMatrix);
            local2 = Vector2.Transform(local2, localMatrix);
            local3 = Vector2.Transform(local3, localMatrix);

            // Later 2D layers are fractionally closer to the camera. Depth testing can therefore preserve
            // normal SS14 layer order even when layers live in different texture atlases/batches.
            var towardCamera = -billboardForward * (drawOrder * SpriteLayerDepthStep);
            var p0 = BillboardPoint(worldPosition, local0, baseHeight, billboardRight, towardCamera);
            var p1 = BillboardPoint(worldPosition, local1, baseHeight, billboardRight, towardCamera);
            var p2 = BillboardPoint(worldPosition, local2, baseHeight, billboardRight, towardCamera);
            var p3 = BillboardPoint(worldPosition, local3, baseHeight, billboardRight, towardCamera);

            var color = new Vector3(
                Math.Clamp(modulation.R, 0f, 1f),
                Math.Clamp(modulation.G, 0f, 1f),
                Math.Clamp(modulation.B, 0f, 1f));

            var uv0 = new Vector2(uvRegion.Left, uvRegion.Bottom);
            var uv1 = new Vector2(uvRegion.Right, uvRegion.Bottom);
            var uv2 = new Vector2(uvRegion.Right, uvRegion.Top);
            var uv3 = new Vector2(uvRegion.Left, uvRegion.Top);

            if (!_spriteVertices.TryGetValue(textureHandle, out var vertices))
            {
                vertices = new List<float>(256);
                _spriteVertices.Add(textureHandle, vertices);
            }

            AddVertex(vertices, p0, color, uv0);
            AddVertex(vertices, p1, color, uv1);
            AddVertex(vertices, p2, color, uv2);
            AddVertex(vertices, p0, color, uv0);
            AddVertex(vertices, p2, color, uv2);
            AddVertex(vertices, p3, color, uv3);

            drawOrder++;
            appendedAny = true;
        }

        return appendedAny;
    }

    /// <summary>
    /// Wraps the current live wall sprite around the real occluder prism. This keeps the wall's existing
    /// 3D footprint and height while replacing the debug EntityColor box with the actual RSI frame.
    /// Every visible sprite layer is applied to the vertical faces and the top cap, so damage/variant
    /// overlays can continue to follow the normal SpriteComponent state.
    /// </summary>
    private bool TryAppendTexturedPrism(
        SpriteComponent sprite,
        Angle worldRotation,
        Angle eyeRotation,
        ReadOnlySpan<Vector2> localPolygon,
        Matrix3x2 worldMatrix,
        float bottom,
        float top,
        Vector2 billboardForward)
    {
        if (localPolygon.Length < 3 || sprite.Layers.Count == 0)
            return false;

        var worldPolygon = new Vector2[localPolygon.Length];
        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;

        for (var i = 0; i < localPolygon.Length; i++)
        {
            var local = localPolygon[i];
            worldPolygon[i] = Vector2.Transform(local, worldMatrix);
            minX = MathF.Min(minX, local.X);
            minY = MathF.Min(minY, local.Y);
            maxX = MathF.Max(maxX, local.X);
            maxY = MathF.Max(maxY, local.Y);
        }

        var localWidth = MathF.Max(0.001f, maxX - minX);
        var localHeight = MathF.Max(0.001f, maxY - minY);
        var apparentAngle = (worldRotation + eyeRotation).Reduced().FlipPositive();
        var drawOrder = 0;
        var appendedAny = false;

        foreach (var layer in sprite.Layers)
        {
            if (!TryGetSpriteLayerFrame(sprite, layer, apparentAngle, out var texture, out _) ||
                !TryResolveSpriteTexture(texture, out var textureHandle, out var uvRegion))
            {
                continue;
            }

            var modulation = sprite.color * layer.Color;
            if (modulation.A <= 0.01f)
                continue;

            var color = new Vector3(
                Math.Clamp(modulation.R, 0f, 1f),
                Math.Clamp(modulation.G, 0f, 1f),
                Math.Clamp(modulation.B, 0f, 1f));

            if (!_spriteVertices.TryGetValue(textureHandle, out var vertices))
            {
                vertices = new List<float>(512);
                _spriteVertices.Add(textureHandle, vertices);
            }

            // Pull each later layer a fraction toward the camera so normal SS14 layer order survives
            // depth testing even when layers use different texture atlases.
            var cameraOffset = -billboardForward * ((drawOrder + 1) * SpriteLayerDepthStep);
            var verticalOffset = (drawOrder + 1) * SpriteLayerDepthStep * 0.35f;

            var uvBottomLeft = new Vector2(uvRegion.Left, uvRegion.Bottom);
            var uvBottomRight = new Vector2(uvRegion.Right, uvRegion.Bottom);
            var uvTopRight = new Vector2(uvRegion.Right, uvRegion.Top);
            var uvTopLeft = new Vector2(uvRegion.Left, uvRegion.Top);

            // Repeat the current wall frame on every vertical face. This deliberately preserves the
            // recognizable SS14 artwork while the underlying polygon still supplies true 3D thickness.
            for (var i = 0; i < worldPolygon.Length; i++)
            {
                var next = (i + 1) % worldPolygon.Length;
                var a = worldPolygon[i] + cameraOffset;
                var b = worldPolygon[next] + cameraOffset;
                var bottomA = new Vector3(a, bottom + verticalOffset);
                var bottomB = new Vector3(b, bottom + verticalOffset);
                var topA = new Vector3(a, top + verticalOffset);
                var topB = new Vector3(b, top + verticalOffset);

                AddVertex(vertices, bottomA, color, uvBottomLeft);
                AddVertex(vertices, bottomB, color, uvBottomRight);
                AddVertex(vertices, topB, color, uvTopRight);
                AddVertex(vertices, bottomA, color, uvBottomLeft);
                AddVertex(vertices, topB, color, uvTopRight);
                AddVertex(vertices, topA, color, uvTopLeft);
            }

            Vector2 TopUv(Vector2 local)
            {
                var u = (local.X - minX) / localWidth;
                var v = (local.Y - minY) / localHeight;
                return new Vector2(
                    uvRegion.Left + u * uvRegion.Width,
                    uvRegion.Bottom + v * uvRegion.Height);
            }

            // The top cap uses the sprite as a planar texture. This is especially useful for the normal
            // top-down wall artwork, which remains recognizable when the player looks down from above.
            for (var i = 1; i < worldPolygon.Length - 1; i++)
            {
                var p0 = new Vector3(worldPolygon[0] + cameraOffset, top + verticalOffset);
                var p1 = new Vector3(worldPolygon[i] + cameraOffset, top + verticalOffset);
                var p2 = new Vector3(worldPolygon[i + 1] + cameraOffset, top + verticalOffset);
                AddVertex(vertices, p0, color, TopUv(localPolygon[0]));
                AddVertex(vertices, p1, color, TopUv(localPolygon[i]));
                AddVertex(vertices, p2, color, TopUv(localPolygon[i + 1]));
            }

            drawOrder++;
            appendedAny = true;
        }

        return appendedAny;
    }

    private static bool TryGetSpriteLayerFrame(
        SpriteComponent sprite,
        SpriteComponent.Layer layer,
        Angle apparentAngle,
        out Texture texture,
        out RsiDirection direction)
    {
        texture = null!;
        direction = RsiDirection.South;

        if (!layer.Drawn)
            return false;

        if (layer._actualState is { } state)
        {
            direction = SpriteComponent.Layer.GetDirection(state.RsiDirections, apparentAngle);
            if (sprite.EnableDirectionOverride)
                direction = sprite.DirectionOverride.Convert(state.RsiDirections);
            direction = direction.OffsetRsiDir(layer.DirOffset);
            texture = state.GetAtlasFrame(direction, layer.AnimationFrame);
            return true;
        }

        if (layer.Texture is not { } directTexture)
            return false;

        texture = directTexture;
        return true;
    }

    private bool TryResolveSpriteTexture(Texture texture, out uint handle, out Box2 uv)
    {
        handle = 0;
        uv = new Box2(0f, 0f, 1f, 1f);
        var source = texture;

        // RSI frames are normally AtlasTextures. Preserve their exact sub-region and support nested atlas
        // wrappers so this path does not depend on a particular Clyde packing implementation.
        while (source is AtlasTexture atlas)
        {
            var region = atlas.NormalizedSubRegion;
            uv = new Box2(
                region.Left + uv.Left * region.Width,
                region.Bottom + uv.Bottom * region.Height,
                region.Left + uv.Right * region.Width,
                region.Bottom + uv.Top * region.Height);
            source = atlas.SourceTexture;
        }

        return _spriteTextureHandles.TryGetValue(source, out handle) && handle != 0;
    }

    private static void GetTextureQuad(
        Texture texture,
        out Vector2 p0,
        out Vector2 p1,
        out Vector2 p2,
        out Vector2 p3)
    {
        var size = new Vector2(texture.Width, texture.Height) / EyeManager.PixelsPerMeter;
        var half = size * 0.5f;
        p0 = new Vector2(-half.X, -half.Y);
        p1 = new Vector2(half.X, -half.Y);
        p2 = new Vector2(half.X, half.Y);
        p3 = new Vector2(-half.X, half.Y);
    }

    private static Vector3 BillboardPoint(
        Vector3 worldPosition,
        Vector2 local,
        float baseHeight,
        Vector2 billboardRight,
        Vector2 cameraOffset)
    {
        var xy = new Vector2(worldPosition.X, worldPosition.Y) + billboardRight * local.X + cameraOffset;
        return new Vector3(xy, baseHeight + local.Y);
    }
}
