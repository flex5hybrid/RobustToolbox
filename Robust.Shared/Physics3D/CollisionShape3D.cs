using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Robust.Shared.Physics3D;

[ImplicitDataDefinitionForInheritors, Serializable, NetSerializable]
public abstract partial class CollisionShape3D
{
    [DataField]
    public Vector3 Offset;

    [DataField]
    public Quaternion Rotation = Quaternion.Identity;

    [DataField]
    public bool Sensor;

    [DataField]
    public int CollisionLayer = 1;

    [DataField]
    public int CollisionMask = int.MaxValue;

    [DataField]
    public float Friction = 0.6f;

    [DataField]
    public float Restitution;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class BoxShape3D : CollisionShape3D
{
    [DataField(required: true)]
    public Vector3 Size = Vector3.One;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class SphereShape3D : CollisionShape3D
{
    [DataField(required: true)]
    public float Radius = 0.5f;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class CapsuleShape3D : CollisionShape3D
{
    [DataField(required: true)]
    public float Radius = 0.35f;

    /// <summary>
    /// Length of the cylindrical section, excluding both hemispheres.
    /// </summary>
    [DataField(required: true)]
    public float Length = 1f;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class CylinderShape3D : CollisionShape3D
{
    [DataField(required: true)]
    public float Radius = 0.5f;

    [DataField(required: true)]
    public float Length = 1f;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class ConvexHullShape3D : CollisionShape3D
{
    [DataField(required: true)]
    public List<Vector3> Points = new();
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class TriangleMeshShape3D : CollisionShape3D
{
    /// <summary>
    /// Triangle meshes are concave, one-sided world geometry and are accepted only on static or kinematic
    /// bodies. Moving props must use primitives, convex hulls, or compounds of convex shapes.
    /// </summary>
    [DataField(required: true)]
    public List<Vector3> Vertices = new();

    /// <summary>
    /// Consecutive triples index <see cref="Vertices"/> and form triangles.
    /// </summary>
    [DataField(required: true)]
    public List<int> Indices = new();
}
