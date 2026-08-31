using System.Numerics;
using NUnit.Framework;
using Robust.Shared3D;

namespace Robust.Client3D.Tests;

[TestFixture]
public sealed class WorldDefinition3DTests
{
    [Test]
    public void LoadsSpawnsModelsTransformsAndColliders()
    {
        const string json = """
        {
          "version": 1,
          "name": "Test world",
          "spawns": [
            [1, 2, 3],
            [4, 5, 6]
          ],
          "objects": [
            {
              "id": "crate",
              "model": "Assets\\Models\\crate.gltf",
              "position": [2, 0, 0],
              "rotationDegrees": [0, 0, 90],
              "scale": [2, 1, 1],
              "collider": {
                "size": [1, 1, 1]
              }
            }
          ]
        }
        """;

        var world = WorldDefinition3DLoader.Load(json);

        Assert.That(world.Name, Is.EqualTo("Test world"));
        Assert.That(world.GetPlayerSpawnPosition(1), Is.EqualTo(new Vector3(1, 2, 3)));
        Assert.That(world.GetPlayerSpawnPosition(2), Is.EqualTo(new Vector3(4, 5, 6)));
        Assert.That(world.GetPlayerSpawnPosition(3), Is.EqualTo(new Vector3(1, 2, 3)));
        Assert.That(world.Objects, Has.Count.EqualTo(1));
        Assert.That(world.Objects[0].ModelPath, Is.EqualTo("Assets/Models/crate.gltf"));
        Assert.That(world.CollisionBounds, Has.Count.EqualTo(1));
        Assert.That(world.CollisionBounds[0].Center.X, Is.EqualTo(2f).Within(0.001f));
        Assert.That(world.CollisionBounds[0].Size.X, Is.EqualTo(1f).Within(0.001f));
        Assert.That(world.CollisionBounds[0].Size.Y, Is.EqualTo(2f).Within(0.001f));
    }

    [Test]
    public void RejectsDuplicateObjectIds()
    {
        const string json = """
        {
          "version": 1,
          "spawns": [[0, 0, 1]],
          "objects": [
            { "id": "door" },
            { "id": "DOOR" }
          ]
        }
        """;

        Assert.That(
            () => WorldDefinition3DLoader.Load(json),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void RejectsInvalidColliderSize()
    {
        const string json = """
        {
          "version": 1,
          "spawns": [[0, 0, 1]],
          "objects": [
            {
              "id": "bad",
              "collider": { "size": [1, 0, 1] }
            }
          ]
        }
        """;

        Assert.That(
            () => WorldDefinition3DLoader.Load(json),
            Throws.TypeOf<InvalidOperationException>());
    }
}
