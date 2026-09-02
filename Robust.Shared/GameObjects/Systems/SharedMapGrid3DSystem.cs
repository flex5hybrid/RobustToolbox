using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Mutation, coordinate and replication API for sparse native 3D grids.
/// </summary>
public sealed class SharedMapGrid3DSystem : EntitySystem
{
    public static readonly Vector3i[] CardinalNeighbors =
    {
        Vector3i.East,
        Vector3i.West,
        Vector3i.North,
        Vector3i.South,
        Vector3i.Up,
        Vector3i.Down,
    };

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransform3DSystem _transform3D = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MapGrid3DComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<MapGrid3DComponent, ComponentGetState>(OnGetState);
        SubscribeLocalEvent<MapGrid3DComponent, ComponentHandleState>(OnHandleState);
    }

    public Voxel3D GetVoxel(Entity<MapGrid3DComponent?> grid, Vector3i indices)
    {
        if (!Resolve(grid, ref grid.Comp, false))
            return Voxel3D.Empty;

        var chunkIndices = CellToChunk(indices, grid.Comp.ChunkSize);
        if (!grid.Comp.Chunks.TryGetValue(chunkIndices, out var chunk))
            return Voxel3D.Empty;

        var local = CellToChunkLocal(indices, grid.Comp.ChunkSize);
        return chunk[Flatten(local, grid.Comp.ChunkSize)];
    }

    public bool SetVoxel(Entity<MapGrid3DComponent?> grid, Vector3i indices, Voxel3D voxel)
    {
        if (!Resolve(grid, ref grid.Comp, false))
            return false;

        ValidateGrid(grid.Comp);
        var chunkIndices = CellToChunk(indices, grid.Comp.ChunkSize);
        if (!grid.Comp.Chunks.TryGetValue(chunkIndices, out var chunk))
        {
            if (voxel.IsEmpty)
                return false;

            chunk = new Voxel3D[GetChunkVolume(grid.Comp.ChunkSize)];
            grid.Comp.Chunks.Add(chunkIndices, chunk);
        }

        var local = CellToChunkLocal(indices, grid.Comp.ChunkSize);
        var flat = Flatten(local, grid.Comp.ChunkSize);
        var oldVoxel = chunk[flat];
        if (oldVoxel == voxel)
            return false;

        chunk[flat] = voxel;
        var tick = _timing.CurTick;
        grid.Comp.LastVoxelModifiedTick = tick;
        grid.Comp.ChunkModifiedTicks[chunkIndices] = tick;

        var deleted = voxel.IsEmpty && IsEmpty(chunk);
        if (deleted)
        {
            grid.Comp.Chunks.Remove(chunkIndices);
            grid.Comp.ChunkModifiedTicks.Remove(chunkIndices);
            grid.Comp.ChunkDeletionHistory.Add((tick, chunkIndices));
        }

        RegenerateBounds(grid.Comp);
        Dirty(grid.Owner, grid.Comp);
        var voxelEvent = new VoxelChanged3DEvent(grid.Owner, indices, oldVoxel, voxel);
        RaiseLocalEvent(grid.Owner, ref voxelEvent, true);
        var chunkEvent = new GridChunkChanged3DEvent(grid.Owner, chunkIndices, deleted);
        RaiseLocalEvent(grid.Owner, ref chunkEvent, true);
        return true;
    }

    public IEnumerable<(Vector3i Indices, Voxel3D Voxel)> GetVoxels(
        Entity<MapGrid3DComponent?> grid,
        Vector3i min,
        Vector3i max,
        bool includeEmpty = false)
    {
        if (!Resolve(grid, ref grid.Comp, false))
            yield break;

        var lower = Vector3i.ComponentMin(min, max);
        var upper = Vector3i.ComponentMax(min, max);
        for (var z = lower.Z; z <= upper.Z; z++)
        for (var y = lower.Y; y <= upper.Y; y++)
        for (var x = lower.X; x <= upper.X; x++)
        {
            var indices = new Vector3i(x, y, z);
            var voxel = GetVoxel(grid, indices);
            if (includeEmpty || !voxel.IsEmpty)
                yield return (indices, voxel);
        }
    }

    public Vector3 CellToLocal(Entity<MapGrid3DComponent?> grid, Vector3i indices, bool center = true)
    {
        if (!Resolve(grid, ref grid.Comp, false))
            return Vector3.Zero;

        var offset = center ? new Vector3(0.5f) : Vector3.Zero;
        return ((Vector3) indices + offset) * grid.Comp.CellSize;
    }

    public Vector3i LocalToCell(Entity<MapGrid3DComponent?> grid, Vector3 local)
    {
        if (!Resolve(grid, ref grid.Comp, false) || grid.Comp.CellSize <= 0f)
            return Vector3i.Zero;

        var scaled = local / grid.Comp.CellSize;
        return new Vector3i(
            (int) MathF.Floor(scaled.X),
            (int) MathF.Floor(scaled.Y),
            (int) MathF.Floor(scaled.Z));
    }

    public Vector3 CellToWorld(Entity<MapGrid3DComponent?> grid, Vector3i indices, bool center = true)
    {
        return Vector3.Transform(CellToLocal(grid, indices, center), _transform3D.GetWorldMatrix3D(grid.Owner));
    }

    public Vector3i WorldToCell(Entity<MapGrid3DComponent?> grid, Vector3 world)
    {
        if (!Matrix4x4.Invert(_transform3D.GetWorldMatrix3D(grid.Owner), out var inverse))
            return Vector3i.Zero;

        return LocalToCell(grid, Vector3.Transform(world, inverse));
    }

    public static Vector3i CellToChunk(Vector3i indices, ushort chunkSize)
    {
        return new Vector3i(
            FloorDiv(indices.X, chunkSize),
            FloorDiv(indices.Y, chunkSize),
            FloorDiv(indices.Z, chunkSize));
    }

    public static Vector3i CellToChunkLocal(Vector3i indices, ushort chunkSize)
    {
        return new Vector3i(
            Mod(indices.X, chunkSize),
            Mod(indices.Y, chunkSize),
            Mod(indices.Z, chunkSize));
    }

    private void OnStartup(Entity<MapGrid3DComponent> grid, ref ComponentStartup args)
    {
        ValidateGrid(grid.Comp);
        foreach (var (indices, data) in grid.Comp.Chunks)
        {
            ValidateChunk(grid.Comp, indices, data);
            grid.Comp.ChunkModifiedTicks[indices] = grid.Comp.CreationTick;
        }

        RegenerateBounds(grid.Comp);
    }

    private void OnGetState(EntityUid uid, MapGrid3DComponent component, ref ComponentGetState args)
    {
        if (args.FromTick <= component.CreationTick)
        {
            args.State = new MapGrid3DComponentState(
                component.FormatVersion,
                component.ChunkSize,
                component.CellSize,
                component.CanSplit,
                component.CollisionEnabled,
                CloneChunks(component.Chunks),
                component.LastVoxelModifiedTick);
            return;
        }

        Dictionary<Vector3i, Voxel3D[]?>? changes = null;
        if (component.LastVoxelModifiedTick >= args.FromTick)
        {
            changes = new Dictionary<Vector3i, Voxel3D[]?>();
            foreach (var (tick, indices) in component.ChunkDeletionHistory)
            {
                if (tick >= args.FromTick && !component.Chunks.ContainsKey(indices))
                    changes[indices] = null;
            }

            foreach (var (indices, tick) in component.ChunkModifiedTicks)
            {
                if (tick >= args.FromTick && component.Chunks.TryGetValue(indices, out var data))
                    changes[indices] = (Voxel3D[]) data.Clone();
            }
        }

        args.State = new MapGrid3DComponentDeltaState(
            component.FormatVersion,
            component.ChunkSize,
            component.CellSize,
            component.CanSplit,
            component.CollisionEnabled,
            changes,
            component.LastVoxelModifiedTick);
    }

    private void OnHandleState(EntityUid uid, MapGrid3DComponent component, ref ComponentHandleState args)
    {
        switch (args.Current)
        {
            case MapGrid3DComponentState full:
                ValidateState(component, full.FormatVersion, full.ChunkSize, full.CellSize);
                var previousChunks = new HashSet<Vector3i>(component.Chunks.Keys);
                component.FormatVersion = full.FormatVersion;
                component.ChunkSize = full.ChunkSize;
                component.CellSize = full.CellSize;
                component.CanSplit = full.CanSplit;
                component.CollisionEnabled = full.CollisionEnabled;
                component.Chunks.Clear();
                foreach (var (indices, data) in full.Chunks)
                {
                    ValidateChunk(component, indices, data);
                    component.Chunks.Add(indices, (Voxel3D[]) data.Clone());
                    previousChunks.Remove(indices);
                    var chunkEvent = new GridChunkChanged3DEvent(uid, indices, false);
                    RaiseLocalEvent(uid, ref chunkEvent, true);
                }

                foreach (var indices in previousChunks)
                {
                    var chunkEvent = new GridChunkChanged3DEvent(uid, indices, true);
                    RaiseLocalEvent(uid, ref chunkEvent, true);
                }

                component.LastVoxelModifiedTick = full.LastModifiedTick;
                break;
            case MapGrid3DComponentDeltaState delta:
                ValidateState(component, delta.FormatVersion, delta.ChunkSize, delta.CellSize);
                component.FormatVersion = delta.FormatVersion;
                component.ChunkSize = delta.ChunkSize;
                component.CellSize = delta.CellSize;
                component.CanSplit = delta.CanSplit;
                component.CollisionEnabled = delta.CollisionEnabled;
                if (delta.Chunks is not null)
                {
                    foreach (var (indices, data) in delta.Chunks)
                    {
                        if (data is null)
                            component.Chunks.Remove(indices);
                        else
                        {
                            ValidateChunk(component, indices, data);
                            component.Chunks[indices] = (Voxel3D[]) data.Clone();
                        }

                        var chunkEvent = new GridChunkChanged3DEvent(uid, indices, data is null);
                        RaiseLocalEvent(uid, ref chunkEvent, true);
                    }
                }
                component.LastVoxelModifiedTick = delta.LastModifiedTick;
                break;
            default:
                return;
        }

        RegenerateBounds(component);
    }

    private static void ValidateState(MapGrid3DComponent component, int version, ushort chunkSize, float cellSize)
    {
        if (version != MapGrid3DComponent.CurrentFormatVersion)
            throw new InvalidOperationException($"Unsupported MapGrid3D format version {version}.");
        if (chunkSize == 0 || chunkSize > 32)
            throw new InvalidOperationException($"Invalid MapGrid3D chunk size {chunkSize}.");
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
            throw new InvalidOperationException($"Invalid MapGrid3D cell size {cellSize}.");
        if (component.Chunks.Count > 0 && component.ChunkSize != chunkSize)
            throw new InvalidOperationException("Cannot change MapGrid3D chunk size after chunks exist.");
    }

    private static void ValidateGrid(MapGrid3DComponent component)
    {
        ValidateState(component, component.FormatVersion, component.ChunkSize, component.CellSize);
    }

    private static void ValidateChunk(MapGrid3DComponent component, Vector3i indices, Voxel3D[] data)
    {
        if (data.Length != GetChunkVolume(component.ChunkSize))
            throw new InvalidOperationException($"MapGrid3D chunk {indices} has {data.Length} cells; expected {GetChunkVolume(component.ChunkSize)}.");
        if (IsEmpty(data))
            throw new InvalidOperationException($"MapGrid3D chunk {indices} is empty and must be omitted.");
    }

    private static Dictionary<Vector3i, Voxel3D[]> CloneChunks(Dictionary<Vector3i, Voxel3D[]> chunks)
    {
        var clone = new Dictionary<Vector3i, Voxel3D[]>(chunks.Count);
        foreach (var (indices, data) in chunks)
            clone.Add(indices, (Voxel3D[]) data.Clone());
        return clone;
    }

    private static void RegenerateBounds(MapGrid3DComponent component)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var size = component.ChunkSize;
        foreach (var (chunkIndices, chunk) in component.Chunks)
        {
            for (var z = 0; z < size; z++)
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                if (chunk[Flatten(new Vector3i(x, y, z), size)].IsEmpty)
                    continue;

                var cell = chunkIndices * size + new Vector3i(x, y, z);
                minimum = Vector3.Min(minimum, (Vector3) cell * component.CellSize);
                maximum = Vector3.Max(maximum, ((Vector3) cell + Vector3.One) * component.CellSize);
            }
        }

        component.LocalAabb = float.IsPositiveInfinity(minimum.X)
            ? new Box3(Vector3.Zero, Vector3.Zero)
            : new Box3(minimum, maximum);
    }

    private static int Flatten(Vector3i local, ushort size)
    {
        return local.X + size * (local.Y + size * local.Z);
    }

    private static int GetChunkVolume(ushort size) => checked(size * size * size);
    private static bool IsEmpty(Voxel3D[] chunk) => Array.TrueForAll(chunk, voxel => voxel.IsEmpty);
    private static int FloorDiv(int value, int divisor) => (int) Math.Floor((double) value / divisor);
    private static int Mod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }
}
