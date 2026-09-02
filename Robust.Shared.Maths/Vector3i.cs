using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Robust.Shared.Utility;

namespace Robust.Shared.Maths;

/// <summary>
/// Integer coordinate in the engine's +X east, +Y north, +Z up convention.
/// </summary>
[Serializable, StructLayout(LayoutKind.Sequential)]
public struct Vector3i : IEquatable<Vector3i>, ISpanFormattable
{
    public static readonly Vector3i Zero = new(0, 0, 0);
    public static readonly Vector3i One = new(1, 1, 1);
    public static readonly Vector3i East = new(1, 0, 0);
    public static readonly Vector3i West = new(-1, 0, 0);
    public static readonly Vector3i North = new(0, 1, 0);
    public static readonly Vector3i South = new(0, -1, 0);
    public static readonly Vector3i Up = new(0, 0, 1);
    public static readonly Vector3i Down = new(0, 0, -1);

    [JsonInclude] public int X;
    [JsonInclude] public int Y;
    [JsonInclude] public int Z;

    public Vector3i(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public readonly long LengthSquared => (long) X * X + (long) Y * Y + (long) Z * Z;

    public static Vector3i ComponentMin(Vector3i first, Vector3i second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Min(first.Z, second.Z));

    public static Vector3i ComponentMax(Vector3i first, Vector3i second) => new(
        Math.Max(first.X, second.X),
        Math.Max(first.Y, second.Y),
        Math.Max(first.Z, second.Z));

    public readonly bool Equals(Vector3i other) => X == other.X && Y == other.Y && Z == other.Z;
    public readonly override bool Equals(object? obj) => obj is Vector3i other && Equals(other);
    public readonly override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public readonly void Deconstruct(out int x, out int y, out int z) => (x, y, z) = (X, Y, Z);

    public static Vector3i operator +(Vector3i first, Vector3i second) =>
        new(first.X + second.X, first.Y + second.Y, first.Z + second.Z);
    public static Vector3i operator -(Vector3i first, Vector3i second) =>
        new(first.X - second.X, first.Y - second.Y, first.Z - second.Z);
    public static Vector3i operator -(Vector3i value) => new(-value.X, -value.Y, -value.Z);
    public static Vector3i operator *(Vector3i value, int scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    public static Vector3 operator *(Vector3i value, float scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    public static Vector3i operator /(Vector3i value, int divisor) => new(value.X / divisor, value.Y / divisor, value.Z / divisor);
    public static Vector3 operator /(Vector3i value, float divisor) => new(value.X / divisor, value.Y / divisor, value.Z / divisor);
    public static bool operator ==(Vector3i first, Vector3i second) => first.Equals(second);
    public static bool operator !=(Vector3i first, Vector3i second) => !first.Equals(second);
    public static implicit operator Vector3(Vector3i value) => new(value.X, value.Y, value.Z);
    public static explicit operator Vector3i(Vector3 value) => new((int) value.X, (int) value.Y, (int) value.Z);
    public static implicit operator Vector3i((int x, int y, int z) value) => new(value.x, value.y, value.z);

    public readonly override string ToString() => $"({X}, {Y}, {Z})";
    public readonly string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public readonly bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider)
    {
        return FormatHelpers.TryFormatInto(destination, out charsWritten, $"({X}, {Y}, {Z})");
    }
}
