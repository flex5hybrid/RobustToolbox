using System.Numerics;

namespace Robust.Shared3D;

public static class DemoWorld3D
{
    public const float PlayerRadius = 0.35f;
    public const float PlayerHeight = 1.8f;

    private static readonly Aabb3[] StaticColliders =
    [
        // Floor.
        new(new Vector3(-6f, -6f, -0.5f), new Vector3(6f, 6f, 0f)),

        // Four room walls.
        new(new Vector3(-6.25f, -6.25f, 0f), new Vector3(-6f, 6.25f, 3.5f)),
        new(new Vector3(6f, -6.25f, 0f), new Vector3(6.25f, 6.25f, 3.5f)),
        new(new Vector3(-6f, -6.25f, 0f), new Vector3(6f, -6f, 3.5f)),
        new(new Vector3(-6f, 6f, 0f), new Vector3(6f, 6.25f, 3.5f)),

        // Obstacles that make collision and remote motion obvious.
        new(new Vector3(-1.2f, -0.8f, 0f), new Vector3(1.2f, 0.8f, 1.4f)),
        new(new Vector3(2.2f, 1.8f, 0f), new Vector3(3.4f, 3f, 2.2f)),
        new(new Vector3(-3.5f, 2.4f, 0f), new Vector3(-2.3f, 3.6f, 0.8f)),
    ];

    public static IReadOnlyList<Aabb3> Colliders => StaticColliders;

    public static Vector3 SpawnFor(int playerId)
    {
        var lane = (playerId - 1) % 4;
        return lane switch
        {
            0 => new Vector3(-3.5f, -3.5f, 0f),
            1 => new Vector3(3.5f, -3.5f, 0f),
            2 => new Vector3(-3.5f, 3.5f, 0f),
            _ => new Vector3(3.5f, 3.5f, 0f),
        };
    }

    public static Aabb3 CharacterBounds(Vector3 feetPosition)
    {
        return new Aabb3(
            feetPosition + new Vector3(-PlayerRadius, -PlayerRadius, 0f),
            feetPosition + new Vector3(PlayerRadius, PlayerRadius, PlayerHeight));
    }
}
