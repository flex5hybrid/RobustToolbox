using System;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Robust.Client.GameObjects;

internal sealed partial class World3DGridOverlay
{
    private void AppendNative3DGrids(MapId mapId, Vector2 eyeWorld, ref int gridCount)
    {
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

        var baseColor = VoxelColor(voxel);
        var albedo = new Vector4(baseColor, 1f);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.Down).IsEmpty)
            AddLitFace(gridUid, mapId, corners[0], corners[3], corners[2], corners[1], albedo);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.Up).IsEmpty)
            AddLitFace(gridUid, mapId, corners[4], corners[5], corners[6], corners[7], albedo);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.South).IsEmpty)
            AddLitFace(gridUid, mapId, corners[0], corners[1], corners[5], corners[4], albedo);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.East).IsEmpty)
            AddLitFace(gridUid, mapId, corners[1], corners[2], corners[6], corners[5], albedo);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.North).IsEmpty)
            AddLitFace(gridUid, mapId, corners[2], corners[3], corners[7], corners[6], albedo);
        if (_mapGrid3D.GetVoxel((gridUid, grid), indices + Vector3i.West).IsEmpty)
            AddLitFace(gridUid, mapId, corners[3], corners[0], corners[4], corners[7], albedo);
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
