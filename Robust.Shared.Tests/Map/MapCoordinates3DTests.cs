using System.Numerics;
using NUnit.Framework;
using Robust.Shared.Map;

namespace Robust.UnitTesting.Shared.Map;

[TestFixture]
[Parallelizable]
public sealed class MapCoordinates3DTests
{
    [Test]
    public void RangeIncludesVerticalDistance()
    {
        var map = new MapId(1);
        var origin = new MapCoordinates3D(Vector3.Zero, map);
        var above = new MapCoordinates3D(new Vector3(0f, 0f, 2f), map);

        Assert.That(origin.InRange(above, 1.9f), Is.False);
        Assert.That(origin.InRange(above, 2.1f), Is.True);
    }

    [Test]
    public void DifferentMapsAreNeverInRange()
    {
        var first = new MapCoordinates3D(Vector3.Zero, new MapId(1));
        var second = new MapCoordinates3D(Vector3.Zero, new MapId(2));

        Assert.That(first.InRange(second, 100f), Is.False);
    }

    [Test]
    public void NullspaceAndNonFiniteCoordinatesAreInvalid()
    {
        Assert.That(MapCoordinates3D.Nullspace.IsValid, Is.False);
        Assert.That(
            new MapCoordinates3D(new Vector3(float.NaN, 0f, 0f), new MapId(1)).IsValid,
            Is.False);
    }
}
