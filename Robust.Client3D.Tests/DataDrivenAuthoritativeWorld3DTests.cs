using System.Numerics;
using NUnit.Framework;
using Robust.Shared3D;

namespace Robust.Client3D.Tests;

[TestFixture]
public sealed class DataDrivenAuthoritativeWorld3DTests
{
    [Test]
    public void PlayerUsesDefinitionSpawnAndCollisionBounds()
    {
        const string json = """
        {
          "version": 1,
          "spawns": [[1, 2, 2]],
          "objects": [
            {
              "id": "floor",
              "position": [0, 0, -0.5],
              "scale": [10, 10, 1],
              "collider": { "size": [1, 1, 1] }
            }
          ]
        }
        """;

        var definition = WorldDefinition3DLoader.Load(json);
        var world = new AuthoritativeWorld3D(definition);
        var player = world.AddPlayer(1);

        Assert.That(player.Character.Position, Is.EqualTo(new Vector3(1, 2, 2)));
        Assert.That(world.CollisionBounds, Has.Count.EqualTo(1));

        for (var i = 0; i < 240; i++)
            world.Step();

        Assert.That(player.Character.IsGrounded, Is.True);
        Assert.That(player.Character.Position.Z, Is.EqualTo(0.9f).Within(0.002f));
    }
}
