using System;
using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Adds the spatial data that does not exist in the legacy 2D <see cref="TransformComponent"/>.
/// During the 2D-to-3D transition, X/Y and the parent hierarchy remain authoritative in
/// <see cref="TransformComponent"/>, while this component contributes local Z, 3D rotation and scale.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class Transform3DComponent : Component
{
    [ViewVariables]
    internal float LocalZ;

    /// <summary>
    /// Additional local 3D rotation. The existing 2D transform yaw remains authoritative until
    /// the spatial simulation itself is migrated to 3D.
    /// </summary>
    [ViewVariables]
    internal Quaternion LocalRotation3D = Quaternion.Identity;

    [ViewVariables]
    internal Vector3 LocalScale3D = Vector3.One;
}

/// <summary>
/// Network state for <see cref="Transform3DComponent"/>. Primitive fields are used deliberately so
/// this foundation does not depend on any new Vector3/Quaternion serializer behavior.
/// </summary>
[Serializable, NetSerializable]
public sealed class Transform3DComponentState : ComponentState
{
    public float Z;
    public float RotationX;
    public float RotationY;
    public float RotationZ;
    public float RotationW;
    public float ScaleX;
    public float ScaleY;
    public float ScaleZ;
}
