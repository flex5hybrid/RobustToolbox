using System.Numerics;
using NUnit.Framework;

namespace Robust.Shared.Maths.Tests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class SpatialMath3DTests
{
    [Test]
    public void ChildMatrixComposesWithParentMatrix()
    {
        var child = new SpatialTransform(
            new Vector3(1f, 0f, 2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f),
            Vector3.One);
        var parent = new SpatialTransform(
            new Vector3(10f, -2f, 4f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f),
            Vector3.One);
        var point = new Vector3(2f, 3f, 4f);

        var composed = Vector3.Transform(point, child.Matrix * parent.Matrix);
        var sequential = parent.TransformPoint(child.TransformPoint(point));

        Assert.That(composed.X, Is.EqualTo(sequential.X).Within(0.0001f));
        Assert.That(composed.Y, Is.EqualTo(sequential.Y).Within(0.0001f));
        Assert.That(composed.Z, Is.EqualTo(sequential.Z).Within(0.0001f));
    }

    [Test]
    public void TransformAndInverseRoundTripPoint()
    {
        var transform = new SpatialTransform(
            new Vector3(12f, -3f, 7f),
            Quaternion.CreateFromYawPitchRoll(0.7f, -0.2f, 0.4f),
            new Vector3(2f, 3f, 0.5f));
        var point = new Vector3(-4f, 8f, 1.5f);

        Assert.That(transform.TryInverseTransformPoint(transform.TransformPoint(point), out var roundTrip), Is.True);
        Assert.That(roundTrip.EqualsApprox(point, 0.0001f), Is.True);
    }

    [Test]
    public void SpatialIndexSeparatesVerticalVolumes()
    {
        var index = new LinearSpatialIndex3<string>();
        index.Add("floor", Box3.FromDimensions(Vector3.Zero, new Vector3(10f, 10f, 0.25f)));
        index.Add("ceiling", Box3.FromDimensions(new Vector3(0f, 0f, 4f), new Vector3(10f, 10f, 0.25f)));
        var results = new List<string>();

        index.Query(Box3.CenteredAround(new Vector3(5f, 5f, 4f), Vector3.One), results);

        Assert.That(results, Is.EquivalentTo(new[] { "ceiling" }));
    }

    [Test]
    public void RayIntersectsVolumeAlongVerticalAxis()
    {
        var ray = new Ray3(new Vector3(2f, 2f, 10f), -Vector3.UnitZ);
        var volume = Box3.FromDimensions(new Vector3(0f, 0f, 2f), new Vector3(4f, 4f, 3f));

        Assert.That(ray.TryIntersect(volume, out var distance), Is.True);
        Assert.That(distance, Is.EqualTo(5f).Within(0.00001f));
    }
}
