using System;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics3D;

namespace Robust.Client.GameObjects;

internal sealed partial class World3DGridOverlay
{
    private void AppendNative3DGrids(MapId mapId, Vector2 eyeWorld, ref int gridCount)
    {
        AppendLegacyLights3D(mapId);

        var query = _entityManager.AllEntityQueryEnumerator<TransformComponent, Transform3DComponent, MapGrid3DComponent>();
        while (query.MoveNext(out var uid, out var transform, out var transform3D, out var grid))
        {
            if (transform.MapID != mapId || !transform3D.IsAuthoritative || grid.CellSize <= 0f)
                continue;

            gridCount++;
            var worldMatrix = _transform3DSystem.GetWorldMatrix3D(uid, transform);
            foreach (var (indices, voxel) in _mapGrid3D.GetOccupiedVoxels((uid, grid)))
            {
                var localMinimum = (Vector3) indices * grid.CellSize;
                var center = Vector3.Transform(localMinimum + new Vector3(grid.CellSize * 0.5f), worldMatrix);
                if (MathF.Abs(center.X - eyeWorld.X) > RenderRadius ||
                    MathF.Abs(center.Y - eyeWorld.Y) > RenderRadius)
                    continue;

                AppendVoxelFaces(uid, transform.MapID, grid, indices, voxel, localMinimum, worldMatrix);
            }
        }
    }

    /// <summary>
    /// Legacy map lights do not necessarily have Transform3DComponent because many lights are decorative and have
    /// no physics body to migrate. Feed those lights into the same 3D shading buffer using their composed 3D world
    /// position. Lights already collected by the native Transform3D pass are skipped by entity id.
    /// </summary>
    private void AppendLegacyLights3D(MapId mapId)
    {
        var query = _entityManager.AllEntityQueryEnumerator<TransformComponent, PointLightComponent>();
        while (query.MoveNext(out var uid, out var transform, out var light))
        {
            if (transform.MapID != mapId ||
                !light.Enabled ||
                light.ContainerOccluded ||
                light.Radius <= 0f)
            {
                continue;
            }

            var alreadyCollected = false;
            foreach (var existing in _lights3D)
            {
                if (existing.Entity != uid)
                    continue;

                alreadyCollected = true;
                break;
            }

            if (alreadyCollected)
                continue;

            var worldRotation = _transform3DSystem.GetWorldRotation3D(uid, transform);
            var position = _transform3DSystem.GetWorldPosition3D(uid, transform) +
                           Vector3.Transform(new Vector3(light.Offset, 0f), worldRotation);

            _lights3D.Add(new RenderLight3D(
                uid,
                mapId,
                position,
                Vector3.UnitY,
                new Vector3(light.Color.R, light.Color.G, light.Color.B),
                light.Radius,
                light.Energy,
                light.Falloff,
                LightKind3D.Point,
                22f,
                35f,
                light.CastShadows));
        }
    }

    private void AppendVoxelFaces(
        EntityUid gridUid,
        MapId mapId,
        MapGrid3DComponent grid,
        Vector3i indices,
        Voxel3D voxel,
        Vector3 minimum,
        Matrix4x4 worldMatrix)
    {
        var maximum = minimum + new Vector3(grid.CellSize);
        Span<Vector3> corners = stackalloc Vector3[8]
        {
            new(minimum.X, minimum.Y, minimum.Z),
            new(maximum.X, minimum.Y, minimum.Z),
            new(maximum.X, maximum.Y, minimum.Z),
            new(minimum.X, maximum.Y, minimum.Z),
            new(minimum.X, minimum.Y, maximum.Z),
            new(maximum.X, minimum.Y, maximum.Z),
            new(maximum.X, maximum.Y, maximum.Z),
            new(minimum.X, maximum.Y, maximum.Z),
        };

        for (var i = 0; i < corners.Length; i++)
            corners[i] = Vector3.Transform(corners[i], worldMatrix);

        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.Down).IsEmpty)
            AddTexturedVoxelFace(gridUid, mapId, corners[0], corners[3], corners[2], corners[1], voxel);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.Up).IsEmpty)
            AddTexturedVoxelFace(gridUid, mapId, corners[4], corners[5], corners[6], corners[7], voxel);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.South).IsEmpty)
            AddTexturedVoxelFace(gridUid, mapId, corners[0], corners[1], corners[5], corners[4], voxel);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.East).IsEmpty)
            AddTexturedVoxelFace(gridUid, mapId, corners[1], corners[2], corners[6], corners[5], voxel);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.North).IsEmpty)
            AddTexturedVoxelFace(gridUid, mapId, corners[2], corners[3], corners[7], corners[6], voxel);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.West).IsEmpty)
            AddTexturedVoxelFace(gridUid, mapId, corners[3], corners[0], corners[4], corners[7], voxel);
    }

    private void AddTexturedVoxelFace(
        EntityUid gridUid,
        MapId mapId,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Voxel3D voxel)
    {
        var tile = new Tile(voxel.TypeId, variant: voxel.Variant, rotationMirroring: (byte) (voxel.Orientation % 8));
        var regions = _tileDefinitionManager.TileAtlasRegion(tile);
        var region = regions is not null && tile.Variant < regions.Length
            ? regions[tile.Variant]
            : _tileDefinitionManager.ErrorTileRegion;
        GetTileUvs(region, tile.RotationMirroring, out var uv0, out var uv1, out var uv2, out var uv3);

        var normal = Vector3.Cross(p1 - p0, p2 - p0);
        normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
        var illumination = ShadeSurface3D(gridUid, mapId, (p0 + p1 + p2 + p3) * 0.25f, normal);
        var color = Vector3.Min(Vector3.Multiply(VoxelColor(voxel), illumination), Vector3.One);
        AddTexturedTriangle(p0, p1, p2, uv0, uv1, uv2, color);
        AddTexturedTriangle(p0, p2, p3, uv0, uv2, uv3, color);
    }

    private static Vector3 VoxelColor(Voxel3D voxel)
    {
        var hash = unchecked((uint) voxel.TypeId * 2654435761u + voxel.Variant * 2246822519u);
        var baseValue = new Vector3(
            0.38f + ((hash >> 16) & 0xFF) / 255f * 0.28f,
            0.42f + ((hash >> 8) & 0xFF) / 255f * 0.25f,
            0.47f + (hash & 0xFF) / 255f * 0.26f);

        if ((voxel.Flags & VoxelFlags3D.Conductive) != 0)
            baseValue = Vector3.Lerp(baseValue, new Vector3(0.55f, 0.61f, 0.68f), 0.45f);
        if ((voxel.Flags & VoxelFlags3D.Flammable) != 0)
            baseValue = Vector3.Lerp(baseValue, new Vector3(0.48f, 0.31f, 0.18f), 0.5f);
        return Vector3.Min(baseValue, Vector3.One);
    }
}
