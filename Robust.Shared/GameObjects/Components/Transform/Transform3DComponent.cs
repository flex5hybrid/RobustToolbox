using System;
using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Stores an entity's local three-dimensional transform.
/// Entities can opt into authoritative 3D positioning independently, allowing the engine to migrate
/// complete gameplay slices without silently projecting their simulation back into two dimensions.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class Transform3DComponent : Component
{
    /// <summary>
    /// When true, <see cref="LocalPosition3D"/> and <see cref="LocalRotation3D"/> are the source of truth.
    /// When false, X/Y and yaw are read from the legacy transform while this component only supplies
    /// the missing Z, pitch, roll and scale during the migration.
    /// </summary>
    [DataField("authoritative"), ViewVariables]
    internal bool Authoritative;

    [DataField("position"), ViewVariables]
    internal Vector3 LocalPosition3D;

    /// <summary>
    /// Local orientation. In compatibility mode the legacy yaw is composed with this value.
    /// </summary>
    [DataField("rotation"), ViewVariables]
    internal Quaternion LocalRotation3D = Quaternion.Identity;

    [DataField("scale"), ViewVariables]
    internal Vector3 LocalScale3D = Vector3.One;

    public bool IsAuthoritative => Authoritative;
    public Vector3 LocalPosition => LocalPosition3D;
    public Quaternion LocalRotation => LocalRotation3D;
    public Vector3 LocalScale => LocalScale3D;
}

/// <summary>
/// Network state for <see cref="Transform3DComponent"/>. Primitive fields are used deliberately so
/// this foundation does not depend on any new Vector3/Quaternion serializer behavior.
/// </summary>
[Serializable, NetSerializable]
public sealed class Transform3DComponentState : ComponentState
{
    public bool Authoritative;
    public float X;
    public float Y;
    public float Z;
    public float RotationX;
    public float RotationY;
    public float RotationZ;
    public float RotationW;
    public float ScaleX;
    public float ScaleY;
    public float ScaleZ;
}

/// <summary>
/// Raised whenever the authoritative local XYZ position changes. Systems that partition space must listen to this
/// event instead of relying on the legacy XY move event.
/// </summary>
[ByRefEvent]
public readonly record struct Transform3DPositionChangedEvent(Vector3 OldPosition, Vector3 NewPosition);

/// <summary>
/// Raised after a network state replaces a 3D pose, allowing prediction backends to rebuild from the server snapshot.
/// </summary>
[ByRefEvent]
public readonly record struct Transform3DStateAppliedEvent;
