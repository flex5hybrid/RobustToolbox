using System;
using System.Numerics;

namespace Robust.Shared.Maths;

/// <summary>
/// Shared conventions and operations for the engine's right-handed 3D coordinate system:
/// +X east, +Y north and +Z up. Matrices use System.Numerics row-vector conventions.
/// </summary>
public static class SpatialMath
{
    public const float QuaternionEqualityTolerance = 1e-6f;

    public static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    public static bool IsFinite(Quaternion value)
    {
        return float.IsFinite(value.X) &&
               float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) &&
               float.IsFinite(value.W);
    }

    public static Quaternion Normalize(Quaternion rotation)
    {
        return IsFinite(rotation) && rotation.LengthSquared() >= 1e-8f
            ? Quaternion.Normalize(rotation)
            : Quaternion.Identity;
    }

    public static Quaternion FromYaw(Angle yaw)
    {
        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float) yaw.Theta);
    }

    public static Angle Yaw(this Quaternion rotation)
    {
        rotation = Normalize(rotation);
        var sin = 2f * (rotation.W * rotation.Z + rotation.X * rotation.Y);
        var cos = 1f - 2f * (rotation.Y * rotation.Y + rotation.Z * rotation.Z);
        return new Angle(MathF.Atan2(sin, cos));
    }

    public static bool EqualsApprox(
        this Quaternion left,
        Quaternion right,
        float tolerance = QuaternionEqualityTolerance)
    {
        left = Normalize(left);
        right = Normalize(right);
        return 1f - MathF.Abs(Quaternion.Dot(left, right)) <= tolerance;
    }

    public static bool EqualsApprox(this Vector3 left, Vector3 right, float tolerance = 1e-5f)
    {
        return Vector3.DistanceSquared(left, right) <= tolerance * tolerance;
    }

    public static Vector3 Rotate(this Quaternion rotation, Vector3 vector)
    {
        return Vector3.Transform(vector, Normalize(rotation));
    }

    public static Vector2 XY(this Vector3 vector) => new(vector.X, vector.Y);

    public static Vector3 WithZ(this Vector2 vector, float z = 0f) => new(vector.X, vector.Y, z);

    public static Quaternion Compose(Quaternion local, Quaternion parent)
    {
        return Normalize(Quaternion.Concatenate(local, parent));
    }

    public static Quaternion RelativeTo(Quaternion world, Quaternion parent)
    {
        return Normalize(Quaternion.Concatenate(world, Quaternion.Inverse(Normalize(parent))));
    }

    public static Matrix4x4 CreateTransform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        return Matrix4x4.CreateScale(scale) *
               Matrix4x4.CreateFromQuaternion(Normalize(rotation)) *
               Matrix4x4.CreateTranslation(position);
    }

    public static bool TryCreateInverseTransform(
        Vector3 position,
        Quaternion rotation,
        Vector3 scale,
        out Matrix4x4 inverse)
    {
        return Matrix4x4.Invert(CreateTransform(position, rotation, scale), out inverse);
    }
}
