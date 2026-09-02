using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Robust.Shared.Maths;

namespace Robust.Shared.Map.Enumerators;

/// <summary>
/// Allocation-free iterator over all cubic chunk indices intersected by a world-space cube or local AABB.
/// </summary>
public struct ChunkIndicesEnumerator3D
{
    private readonly Vector3i _minimum;
    private readonly Vector3i _maximum;
    private int _x;
    private int _y;
    private int _z;

    public ChunkIndicesEnumerator3D(Vector3 viewPosition, float range, float chunkSize)
        : this(Box3.CenteredAround(viewPosition, new Vector3(range * 2f)), chunkSize)
    {
    }

    public ChunkIndicesEnumerator3D(Box3 bounds, float chunkSize)
    {
        if (!float.IsFinite(chunkSize) || chunkSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));

        _minimum = Floor(bounds.Min / chunkSize);
        _maximum = Floor(bounds.Max / chunkSize);
        _x = _minimum.X;
        _y = _minimum.Y;
        _z = _minimum.Z;
    }

    public bool MoveNext([NotNullWhen(true)] out Vector3i? indices)
    {
        if (_z > _maximum.Z)
        {
            _z = _minimum.Z;
            _y++;
        }

        if (_y > _maximum.Y)
        {
            _y = _minimum.Y;
            _x++;
        }

        if (_x > _maximum.X)
        {
            indices = null;
            return false;
        }

        indices = new Vector3i(_x, _y, _z);
        _z++;
        return true;
    }

    private static Vector3i Floor(Vector3 value)
    {
        return new Vector3i(
            (int) MathF.Floor(value.X),
            (int) MathF.Floor(value.Y),
            (int) MathF.Floor(value.Z));
    }
}
