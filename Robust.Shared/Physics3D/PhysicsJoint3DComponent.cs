using System;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.Physics3D;

[Serializable, NetSerializable]
public enum PhysicsJointType3D : byte
{
    BallSocket,
    DistanceLimit,
    Hinge,
    Weld,
}

/// <summary>
/// A backend-independent two-body 3D constraint. Static world anchors should be represented by a kinematic
/// body; the physics backend deliberately does not leak static/body handle distinctions into Content.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class PhysicsJoint3DComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public EntityUid BodyA;

    [DataField(required: true), AutoNetworkedField]
    public EntityUid BodyB;

    [DataField, AutoNetworkedField]
    public PhysicsJointType3D JointType;

    [DataField, AutoNetworkedField]
    public Vector3 LocalAnchorA;

    [DataField, AutoNetworkedField]
    public Vector3 LocalAnchorB;

    [DataField, AutoNetworkedField]
    public Vector3 LocalAxisA = Vector3.UnitZ;

    [DataField, AutoNetworkedField]
    public Vector3 LocalAxisB = Vector3.UnitZ;

    [DataField, AutoNetworkedField]
    public Quaternion LocalOrientation = Quaternion.Identity;

    [DataField, AutoNetworkedField]
    public float MinimumDistance;

    [DataField, AutoNetworkedField]
    public float MaximumDistance = 1f;

    [DataField, AutoNetworkedField]
    public float SpringFrequency = 30f;

    [DataField, AutoNetworkedField]
    public float DampingRatio = 1f;

    [DataField, AutoNetworkedField]
    public bool Enabled = true;

    [DataField, AutoNetworkedField]
    public bool CollideConnected;

    [ViewVariables]
    internal int BackendHandle = -1;
}
