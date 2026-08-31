using System.Numerics;
using NUnit.Framework;
using Robust.Shared3D;

namespace Robust.Client3D.Tests;

[TestFixture]
public sealed class KinematicCharacter3DTests
{
    [Test]
    public void CharacterLandsOnFloorAndStopsAtWall()
    {
        var character = new KinematicCharacter3D(new Vector3(0f, -4f, 2f));
        const float dt = 1f / 120f;

        for (var i = 0; i < 240; i++)
            character.Step(new CharacterInput3D(0f, 0f, 0f, false), dt, DemoWorld3D.Colliders);

        Assert.That(character.Grounded, Is.True);
        Assert.That(character.Position.Z, Is.EqualTo(0f).Within(0.001f));

        for (var i = 0; i < 300; i++)
            character.Step(new CharacterInput3D(0f, -1f, 0f, false), dt, DemoWorld3D.Colliders);

        Assert.That(character.Position.Y, Is.GreaterThanOrEqualTo(-5.66f));
        Assert.That(character.Position.Y, Is.LessThan(-5.5f));
    }

    [Test]
    public void JumpReturnsToGround()
    {
        var character = new KinematicCharacter3D(new Vector3(3.5f, -3.5f, 0f));
        const float dt = 1f / 120f;
        var peak = 0f;

        for (var i = 0; i < 240; i++)
        {
            character.Step(new CharacterInput3D(0f, 0f, 0f, i == 0), dt, DemoWorld3D.Colliders);
            peak = Math.Max(peak, character.Position.Z);
        }

        Assert.That(peak, Is.GreaterThan(0.7f));
        Assert.That(character.Grounded, Is.True);
        Assert.That(character.Position.Z, Is.EqualTo(0f).Within(0.001f));
    }
}
