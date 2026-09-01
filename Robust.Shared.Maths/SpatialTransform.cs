using System.Numerics;

namespace Robust.Shared.Maths;

/// <summary>
/// A complete local three-dimensional pose.
/// </summary>
public readonly record struct SpatialTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static readonly SpatialTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public Matrix4x4 Matrix => SpatialMath.CreateTransform(Position, Rotation, Scale);

    public bool TryGetInverseMatrix(out Matrix4x4 inverse)
    {
        return SpatialMath.TryCreateInverseTransform(Position, Rotation, Scale, out inverse);
    }

    public Vector3 TransformPoint(Vector3 point) => Vector3.Transform(point, Matrix);

    public bool TryInverseTransformPoint(Vector3 point, out Vector3 localPoint)
    {
        if (!TryGetInverseMatrix(out var inverse))
        {
            localPoint = default;
            return false;
        }

        localPoint = Vector3.Transform(point, inverse);
        return true;
    }
}
