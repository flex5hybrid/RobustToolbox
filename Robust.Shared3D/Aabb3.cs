using System.Numerics;

namespace Robust.Shared3D;

public readonly record struct Aabb3(Vector3 Min, Vector3 Max)
{
    public Vector3 Size => Max - Min;
    public Vector3 Center => (Min + Max) * 0.5f;

    public bool Intersects(in Aabb3 other)
    {
        return Min.X < other.Max.X && Max.X > other.Min.X &&
               Min.Y < other.Max.Y && Max.Y > other.Min.Y &&
               Min.Z < other.Max.Z && Max.Z > other.Min.Z;
    }
}
