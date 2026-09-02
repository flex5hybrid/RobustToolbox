using System;
using System.Numerics;

namespace Robust.Shared.Maths;

/// <summary>
/// An axis-aligned bounding box in three-dimensional space.
/// </summary>
public readonly record struct Box3
{
    public readonly Vector3 Min;
    public readonly Vector3 Max;

    public Vector3 Size => Max - Min;
    public Vector3 Center => (Min + Max) * 0.5f;
    public float Volume => MathF.Abs(Size.X * Size.Y * Size.Z);
    public bool IsFinite =>
        float.IsFinite(Min.X) && float.IsFinite(Min.Y) && float.IsFinite(Min.Z) &&
        float.IsFinite(Max.X) && float.IsFinite(Max.Y) && float.IsFinite(Max.Z);
    public bool IsValid => IsFinite && Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z;

    public Box3(Vector3 min, Vector3 max)
    {
        Min = Vector3.Min(min, max);
        Max = Vector3.Max(min, max);
    }

    public static Box3 FromDimensions(Vector3 position, Vector3 size) => new(position, position + size);

    public static Box3 CenteredAround(Vector3 center, Vector3 size)
    {
        var halfSize = size * 0.5f;
        return new Box3(center - halfSize, center + halfSize);
    }

    public bool Contains(Vector3 point)
    {
        return point.X >= Min.X && point.X <= Max.X &&
               point.Y >= Min.Y && point.Y <= Max.Y &&
               point.Z >= Min.Z && point.Z <= Max.Z;
    }

    public bool Intersects(Box3 other)
    {
        return Min.X <= other.Max.X && Max.X >= other.Min.X &&
               Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
               Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
    }

    public Box3 Union(Box3 other) => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    public Box3 TransformedBounds(Matrix4x4 matrix)
    {
        var transformedMin = new Vector3(float.PositiveInfinity);
        var transformedMax = new Vector3(float.NegativeInfinity);

        for (var corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) == 0 ? Min.X : Max.X,
                (corner & 2) == 0 ? Min.Y : Max.Y,
                (corner & 4) == 0 ? Min.Z : Max.Z);
            point = Vector3.Transform(point, matrix);
            transformedMin = Vector3.Min(transformedMin, point);
            transformedMax = Vector3.Max(transformedMax, point);
        }

        return new Box3(transformedMin, transformedMax);
    }
}
