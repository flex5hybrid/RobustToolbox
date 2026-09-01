using System;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.Physics3D;

[Serializable, NetSerializable]
public enum PhysicsBodyType3D : byte
{
    Static,
    Dynamic,
    Kinematic,
    Character,
}

[Serializable, NetSerializable]
public enum ContinuousDetectionMode3D : byte
{
    Discrete,
    Passive,
    Continuous,
}

/// <summary>
/// Networked gameplay-facing state for an authoritative 3D rigid body. Backend handles are deliberately kept
/// private to the engine so Content never depends on a particular physics library.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class PhysicsBody3DComponent : Component
{
    [DataField, AutoNetworkedField]
    public PhysicsBodyType3D BodyType = PhysicsBodyType3D.Static;

    [DataField, AutoNetworkedField]
    public Vector3 LinearVelocity;

    [DataField, AutoNetworkedField]
    public Vector3 AngularVelocity;

    [DataField, AutoNetworkedField]
    public float Mass = 1f;

    [DataField, AutoNetworkedField]
    public float GravityScale = 1f;

    [DataField, AutoNetworkedField]
    public float LinearDamping = 0.08f;

    [DataField, AutoNetworkedField]
    public float AngularDamping = 0.08f;

    [DataField, AutoNetworkedField]
    public bool CanCollide = true;

    [DataField, AutoNetworkedField]
    public bool SleepingAllowed = true;

    [DataField, AutoNetworkedField]
    public ContinuousDetectionMode3D ContinuousDetection = ContinuousDetectionMode3D.Discrete;

    [ViewVariables]
    internal int BackendHandle = -1;

    [ViewVariables]
    internal bool BackendStatic;
}
