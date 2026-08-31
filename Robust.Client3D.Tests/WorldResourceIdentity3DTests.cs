using System;
using NUnit.Framework;
using Robust.Shared3D;

namespace Robust.Client3D.Tests;

[TestFixture]
public sealed class WorldResourceIdentity3DTests
{
    [Test]
    public void NormalizesSafeRelativeResourcePath()
    {
        Assert.That(
            WorldResourceIdentity3D.NormalizeResourcePath("Worlds\\bootstrap-world3d.json"),
            Is.EqualTo("Worlds/bootstrap-world3d.json"));
    }

    [TestCase("../world.json")]
    [TestCase("Worlds/../world.json")]
    [TestCase("/tmp/world.json")]
    [TestCase("C:\\world.json")]
    [TestCase("\\\\server\\share\\world.json")]
    public void RejectsEscapingOrRootedResourcePath(string path)
    {
        Assert.That(
            () => WorldResourceIdentity3D.NormalizeResourcePath(path),
            Throws.TypeOf<ArgumentException>());
    }
}
