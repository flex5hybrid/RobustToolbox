using System;
using System.Numerics;

namespace Robust.Shared.Maths;

/// <summary>
/// A normalized ray in three-dimensional space.
/// </summary>
public readonly record struct Ray3
{
    public readonly Vector3 Origin;
    public readonly Vector3 Direction;

    public Ray3(Vector3 origin, Vector3 direction)
    {
        if (!SpatialMath.IsFinite(origin))
            throw new ArgumentOutOfRangeException(nameof(origin));
        if (!SpatialMath.IsFinite(direction) || direction.LengthSquared() < 1e-8f)
            throw new ArgumentOutOfRangeException(nameof(direction));

        Origin = origin;
        Direction = Vector3.Normalize(direction);
    }

    public Vector3 GetPoint(float distance) => Origin + Direction * distance;

    public bool TryIntersect(Box3 bounds, out float distance)
    {
        var near = 0f;
        var far = float.PositiveInfinity;

        if (!IntersectAxis(Origin.X, Direction.X, bounds.Min.X, bounds.Max.X, ref near, ref far) ||
            !IntersectAxis(Origin.Y, Direction.Y, bounds.Min.Y, bounds.Max.Y, ref near, ref far) ||
            !IntersectAxis(Origin.Z, Direction.Z, bounds.Min.Z, bounds.Max.Z, ref near, ref far))
        {
            distance = 0f;
            return false;
        }

        distance = near;
        return true;
    }

    private static bool IntersectAxis(
        float origin,
        float direction,
        float minimum,
        float maximum,
        ref float near,
        ref float far)
    {
        if (MathF.Abs(direction) < 1e-7f)
            return origin >= minimum && origin <= maximum;

        var inverse = 1f / direction;
        var first = (minimum - origin) * inverse;
        var second = (maximum - origin) * inverse;
        if (first > second)
            (first, second) = (second, first);

        near = MathF.Max(near, first);
        far = MathF.Min(far, second);
        return near <= far;
    }
}
