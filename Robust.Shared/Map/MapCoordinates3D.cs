using System;
using System.Numerics;
using JetBrains.Annotations;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Utility;

namespace Robust.Shared.Map;

/// <summary>
/// A position in an authoritative three-dimensional map space.
/// </summary>
[PublicAPI, DataRecord]
[Serializable, NetSerializable]
public readonly partial record struct MapCoordinates3D : ISpanFormattable
{
    public static readonly MapCoordinates3D Nullspace = new(Vector3.Zero, MapId.Nullspace);

    public readonly Vector3 Position;
    public readonly MapId MapId;

    public float X => Position.X;
    public float Y => Position.Y;
    public float Z => Position.Z;

    public MapCoordinates3D(Vector3 position, MapId mapId)
    {
        Position = position;
        MapId = mapId;
    }

    public MapCoordinates3D(float x, float y, float z, MapId mapId)
        : this(new Vector3(x, y, z), mapId)
    {
    }

    public bool IsValid => MapId != Robust.Shared.Map.MapId.Nullspace && IsFinite(Position);

    public bool InRange(MapCoordinates3D other, float range)
    {
        return MapId == other.MapId &&
               float.IsFinite(range) &&
               range >= 0f &&
               Vector3.DistanceSquared(Position, other.Position) < range * range;
    }

    public MapCoordinates3D Offset(Vector3 offset) => new(Position + offset, MapId);

    public override string ToString() => $"Map={MapId}, X={X:N2}, Y={Y:N2}, Z={Z:N2}";

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider)
    {
        return FormatHelpers.TryFormatInto(destination, out charsWritten, $"{ToString()}");
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
