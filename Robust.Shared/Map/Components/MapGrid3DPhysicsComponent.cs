using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Physics3D;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.Map.Components;

/// <summary>
/// Configures the generated compound collider for a native 3D grid. The runtime cache stores one greedy box
/// decomposition per chunk so a voxel edit only rebuilds its own chunk.
/// </summary>
[RegisterComponent]
public sealed partial class MapGrid3DPhysicsComponent : Component
{
    [DataField]
    public PhysicsBodyType3D BodyType = PhysicsBodyType3D.Static;

    [DataField]
    public int CollisionLayer = 1;

    [DataField]
    public int CollisionMask = int.MaxValue;

    [DataField]
    public float Friction = 0.8f;

    [DataField]
    public float Restitution;

    [ViewVariables]
    internal readonly Dictionary<Vector3i, List<BoxShape3D>> ChunkShapes = new();

    [ViewVariables]
    internal bool OwnsBody;

    [ViewVariables]
    internal bool OwnsCollider;
}
