using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Maths;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Timing;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.Map.Components;

[DataDefinition, Serializable, NetSerializable]
public partial struct Voxel3D : IEquatable<Voxel3D>
{
    public static readonly Voxel3D Empty = new(0);

    [DataField]
    public int TypeId;

    [DataField]
    public byte Flags;

    [DataField]
    public byte Variant;

    /// <summary>
    /// One of the 24 right-angle cube orientations. Interpretation belongs to the voxel definition.
    /// </summary>
    [DataField]
    public byte Orientation;

    public bool IsEmpty => TypeId == 0;

    public Voxel3D(int typeId, byte flags = 0, byte variant = 0, byte orientation = 0)
    {
        TypeId = typeId;
        Flags = flags;
        Variant = variant;
        Orientation = orientation;
    }

    public bool Equals(Voxel3D other)
    {
        return TypeId == other.TypeId &&
               Flags == other.Flags &&
               Variant == other.Variant &&
               Orientation == other.Orientation;
    }

    public override bool Equals(object? obj) => obj is Voxel3D other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(TypeId, Flags, Variant, Orientation);
    public static bool operator ==(Voxel3D first, Voxel3D second) => first.Equals(second);
    public static bool operator !=(Voxel3D first, Voxel3D second) => !first.Equals(second);
}

/// <summary>
/// Sparse native three-dimensional map grid. Chunks are serialized directly and replicated through explicit
/// full/delta component states; an absent chunk is entirely empty.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class MapGrid3DComponent : Component
{
    public const ushort DefaultChunkSize = 8;
    public const int CurrentFormatVersion = 1;

    [DataField]
    public int FormatVersion = CurrentFormatVersion;

    [DataField]
    public ushort ChunkSize = DefaultChunkSize;

    [DataField]
    public float CellSize = 1f;

    [DataField("chunks")]
    internal Dictionary<Vector3i, Voxel3D[]> Chunks = new();

    [DataField]
    public bool CanSplit = true;

    [ViewVariables]
    public int ChunkCount => Chunks.Count;

    [ViewVariables]
    public Box3 LocalAabb { get; internal set; }

    [ViewVariables]
    public GameTick LastVoxelModifiedTick { get; internal set; }

    internal readonly Dictionary<Vector3i, GameTick> ChunkModifiedTicks = new();
    internal readonly List<(GameTick Tick, Vector3i Indices)> ChunkDeletionHistory = new();

    public bool HasChunk(Vector3i indices) => Chunks.ContainsKey(indices);
}

[Serializable, NetSerializable]
internal sealed class MapGrid3DComponentState(
    int formatVersion,
    ushort chunkSize,
    float cellSize,
    Dictionary<Vector3i, Voxel3D[]> chunks,
    GameTick lastModifiedTick) : ComponentState
{
    public int FormatVersion = formatVersion;
    public ushort ChunkSize = chunkSize;
    public float CellSize = cellSize;
    public Dictionary<Vector3i, Voxel3D[]> Chunks = chunks;
    public GameTick LastModifiedTick = lastModifiedTick;
}

[Serializable, NetSerializable]
internal sealed class MapGrid3DComponentDeltaState(
    int formatVersion,
    ushort chunkSize,
    float cellSize,
    Dictionary<Vector3i, Voxel3D[]?>? chunks,
    GameTick lastModifiedTick)
    : ComponentState, IComponentDeltaState<MapGrid3DComponentState>
{
    public readonly int FormatVersion = formatVersion;
    public readonly ushort ChunkSize = chunkSize;
    public readonly float CellSize = cellSize;
    public readonly Dictionary<Vector3i, Voxel3D[]?>? Chunks = chunks;
    public readonly GameTick LastModifiedTick = lastModifiedTick;

    public void ApplyToFullState(MapGrid3DComponentState state)
    {
        state.FormatVersion = FormatVersion;
        state.ChunkSize = ChunkSize;
        state.CellSize = CellSize;
        state.LastModifiedTick = LastModifiedTick;
        if (Chunks is null)
            return;

        foreach (var (indices, data) in Chunks)
        {
            if (data is null)
                state.Chunks.Remove(indices);
            else
                state.Chunks[indices] = data;
        }
    }

    public MapGrid3DComponentState CreateNewFullState(MapGrid3DComponentState state)
    {
        var chunks = new Dictionary<Vector3i, Voxel3D[]>(state.Chunks.Count);
        foreach (var (indices, data) in state.Chunks)
            chunks.Add(indices, (Voxel3D[]) data.Clone());

        var copy = new MapGrid3DComponentState(
            FormatVersion,
            ChunkSize,
            CellSize,
            chunks,
            LastModifiedTick);
        ApplyToFullState(copy);
        return copy;
    }
}

[ByRefEvent]
public readonly record struct VoxelChanged3DEvent(
    EntityUid Grid,
    Vector3i Indices,
    Voxel3D OldVoxel,
    Voxel3D NewVoxel);

[ByRefEvent]
public readonly record struct GridChunkChanged3DEvent(EntityUid Grid, Vector3i Chunk, bool Deleted);
