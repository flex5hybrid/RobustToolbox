using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.IoC;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Physics3D;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Generates server-authoritative compound collision from solid MapGrid3D voxels. Contiguous cells are greedily
/// merged into boxes inside each chunk to keep the Bepu shape count bounded without losing exact voxel topology.
/// </summary>
public sealed class SharedMapGrid3DPhysicsSystem : EntitySystem
{
    [Dependency] private INetManager _network = default!;
    [Dependency] private SharedPhysics3DSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MapGrid3DComponent, MapGrid3DStartedEvent>(OnGridStartup);
        SubscribeLocalEvent<MapGrid3DPhysicsComponent, ComponentStartup>(OnPhysicsStartup);
        SubscribeLocalEvent<MapGrid3DPhysicsComponent, ComponentShutdown>(OnPhysicsShutdown);
        SubscribeLocalEvent<MapGrid3DComponent, GridChunkChanged3DEvent>(OnChunkChanged);
    }

    private void OnGridStartup(Entity<MapGrid3DComponent> grid, ref MapGrid3DStartedEvent args)
    {
        if (!_network.IsServer || !grid.Comp.CollisionEnabled)
            return;

        EnsureComp<MapGrid3DPhysicsComponent>(grid.Owner);
    }

    private void OnPhysicsStartup(Entity<MapGrid3DPhysicsComponent> grid, ref ComponentStartup args)
    {
        if (!_network.IsServer || !TryComp(grid.Owner, out MapGrid3DComponent? mapGrid) || !mapGrid.CollisionEnabled)
            return;

        RebuildAll((grid.Owner, mapGrid, grid.Comp));
    }

    private void OnPhysicsShutdown(Entity<MapGrid3DPhysicsComponent> grid, ref ComponentShutdown args)
    {
        if (!_network.IsServer)
            return;

        grid.Comp.ChunkShapes.Clear();
        if (TryComp(grid.Owner, out Collider3DComponent? collider))
        {
            collider.Shapes.Clear();
            Dirty(grid.Owner, collider);
            _physics.RefreshBody(grid.Owner);
        }

        if (grid.Comp.OwnsCollider)
            RemComp<Collider3DComponent>(grid.Owner);
        if (grid.Comp.OwnsBody)
            RemComp<PhysicsBody3DComponent>(grid.Owner);
    }

    private void OnChunkChanged(Entity<MapGrid3DComponent> grid, ref GridChunkChanged3DEvent args)
    {
        if (!_network.IsServer ||
            !grid.Comp.CollisionEnabled ||
            !TryComp(grid.Owner, out MapGrid3DPhysicsComponent? generated))
        {
            return;
        }

        if (args.Deleted || !grid.Comp.Chunks.TryGetValue(args.Chunk, out var cells))
            generated.ChunkShapes.Remove(args.Chunk);
        else
            generated.ChunkShapes[args.Chunk] = BuildChunk(grid.Comp, args.Chunk, cells, generated);

        ApplyCollider((grid.Owner, grid.Comp, generated));
    }

    private void RebuildAll(Entity<MapGrid3DComponent, MapGrid3DPhysicsComponent> grid)
    {
        grid.Comp2.ChunkShapes.Clear();
        foreach (var (indices, cells) in grid.Comp1.Chunks)
            grid.Comp2.ChunkShapes[indices] = BuildChunk(grid.Comp1, indices, cells, grid.Comp2);

        ApplyCollider(grid);
    }

    private void ApplyCollider(Entity<MapGrid3DComponent, MapGrid3DPhysicsComponent> grid)
    {
        grid.Comp2.OwnsBody |= !EnsureComp<PhysicsBody3DComponent>(grid.Owner, out var body);
        grid.Comp2.OwnsCollider |= !EnsureComp<Collider3DComponent>(grid.Owner, out var collider);

        body.BodyType = grid.Comp2.BodyType;
        body.CanCollide = grid.Comp1.CollisionEnabled;
        body.GravityScale = 0f;
        body.LinearVelocity = Vector3.Zero;
        body.AngularVelocity = Vector3.Zero;

        collider.Shapes.Clear();
        foreach (var shapes in grid.Comp2.ChunkShapes.Values)
            collider.Shapes.AddRange(shapes);

        Dirty(grid.Owner, body);
        Dirty(grid.Owner, collider);
        _physics.RefreshBody(grid.Owner);
    }

    private static List<BoxShape3D> BuildChunk(
        MapGrid3DComponent grid,
        Vector3i chunkIndices,
        Voxel3D[] cells,
        MapGrid3DPhysicsComponent physics)
    {
        var size = grid.ChunkSize;
        var consumed = new bool[cells.Length];
        var shapes = new List<BoxShape3D>();

        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var start = new Vector3i(x, y, z);
            var startIndex = Flatten(start, size);
            if (consumed[startIndex] || !IsSolid(cells[startIndex]))
                continue;

            var endX = x + 1;
            while (endX < size && CanConsume(cells, consumed, endX, y, z, size))
                endX++;

            var endY = y + 1;
            while (endY < size && CanConsumeLayer(cells, consumed, x, endX, endY, z, z + 1, size))
                endY++;

            var endZ = z + 1;
            while (endZ < size && CanConsumeLayer(cells, consumed, x, endX, y, endY, endZ, size))
                endZ++;

            MarkConsumed(consumed, x, endX, y, endY, z, endZ, size);
            var cellMin = chunkIndices * size + new Vector3i(x, y, z);
            var cellSize = new Vector3(endX - x, endY - y, endZ - z) * grid.CellSize;
            shapes.Add(new BoxShape3D
            {
                Size = cellSize,
                Offset = (Vector3) cellMin * grid.CellSize + cellSize * 0.5f,
                CollisionLayer = physics.CollisionLayer,
                CollisionMask = physics.CollisionMask,
                Friction = physics.Friction,
                Restitution = physics.Restitution,
            });
        }

        return shapes;
    }

    private static bool CanConsume(
        Voxel3D[] cells,
        bool[] consumed,
        int x,
        int y,
        int z,
        ushort size)
    {
        var index = Flatten(new Vector3i(x, y, z), size);
        return !consumed[index] && IsSolid(cells[index]);
    }

    private static bool CanConsumeLayer(
        Voxel3D[] cells,
        bool[] consumed,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int z,
        ushort size)
    {
        for (var y = minY; y < maxY; y++)
        for (var x = minX; x < maxX; x++)
        {
            if (!CanConsume(cells, consumed, x, y, z, size))
                return false;
        }

        return true;
    }

    private static void MarkConsumed(
        bool[] consumed,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int minZ,
        int maxZ,
        ushort size)
    {
        for (var z = minZ; z < maxZ; z++)
        for (var y = minY; y < maxY; y++)
        for (var x = minX; x < maxX; x++)
            consumed[Flatten(new Vector3i(x, y, z), size)] = true;
    }

    private static int Flatten(Vector3i local, ushort size)
    {
        return local.X + size * (local.Y + size * local.Z);
    }

    private static bool IsSolid(Voxel3D voxel)
    {
        return !voxel.IsEmpty && (voxel.Flags & VoxelFlags3D.Solid) != 0;
    }
}
