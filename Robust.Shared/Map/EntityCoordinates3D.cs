using System;
using System.Numerics;
using JetBrains.Annotations;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Utility;

namespace Robust.Shared.Map;

/// <summary>
/// A three-dimensional position relative to another entity.
/// </summary>
[PublicAPI, DataRecord]
public readonly partial record struct EntityCoordinates3D : ISpanFormattable
{
    public static readonly EntityCoordinates3D Invalid = new(EntityUid.Invalid, Vector3.Zero);

    public readonly EntityUid EntityId;
    public readonly Vector3 Position;

    public float X => Position.X;
    public float Y => Position.Y;
    public float Z => Position.Z;

    public EntityCoordinates3D(EntityUid entityId, Vector3 position)
    {
        EntityId = entityId;
        Position = position;
    }

    public EntityCoordinates3D(EntityUid entityId, float x, float y, float z)
        : this(entityId, new Vector3(x, y, z))
    {
    }

    public bool IsValid(IEntityManager entityManager)
    {
        return EntityId.IsValid() &&
               entityManager.EntityExists(EntityId) &&
               float.IsFinite(X) &&
               float.IsFinite(Y) &&
               float.IsFinite(Z);
    }

    public EntityCoordinates3D WithPosition(Vector3 position) => new(EntityId, position);

    public override string ToString() => $"Entity={EntityId}, X={X:N2}, Y={Y:N2}, Z={Z:N2}";

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider)
    {
        var strValue = ToString();

        if (strValue.AsSpan().TryCopyTo(destination))
        {
            charsWritten = strValue.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }
}
