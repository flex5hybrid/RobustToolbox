using System;
using System.Numerics;
using JetBrains.Annotations;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Robust.Shared.Map;

/// <summary>
/// Network-safe representation of <see cref="EntityCoordinates3D"/>.
/// </summary>
[PublicAPI]
[Serializable, NetSerializable]
public readonly record struct NetCoordinates3D(NetEntity NetEntity, Vector3 Position) : ISpanFormattable
{
    public static readonly NetCoordinates3D Invalid = new(NetEntity.Invalid, Vector3.Zero);

    public float X => Position.X;
    public float Y => Position.Y;
    public float Z => Position.Z;

    public NetCoordinates3D(NetEntity netEntity, float x, float y, float z)
        : this(netEntity, new Vector3(x, y, z))
    {
    }

    public override string ToString() => $"NetEntity={NetEntity}, X={X:N2}, Y={Y:N2}, Z={Z:N2}";

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider)
    {
        return FormatHelpers.TryFormatInto(destination, out charsWritten, ToString());
    }
}
