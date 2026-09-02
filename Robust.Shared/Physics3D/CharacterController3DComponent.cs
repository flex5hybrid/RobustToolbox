using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Robust.Shared.Physics3D;

/// <summary>
/// Gameplay-facing parameters and replicated support state for an upright first-person character.
/// Input policy remains in Content; collision, grounding and velocity application remain engine-owned.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class CharacterController3DComponent : Component
{
    [DataField, AutoNetworkedField]
    public float WalkSpeed = 2.5f;

    [DataField, AutoNetworkedField]
    public float SprintSpeed = 4.5f;

    [DataField, AutoNetworkedField]
    public float GroundAcceleration = 35f;

    [DataField, AutoNetworkedField]
    public float AirAcceleration = 8f;

    [DataField, AutoNetworkedField]
    public float JumpSpeed = 5.2f;

    [DataField, AutoNetworkedField]
    public float GroundProbeDistance = 0.12f;

    [DataField, AutoNetworkedField]
    public float GroundProbeStart = 0.08f;

    [DataField, AutoNetworkedField]
    public float MaximumSlopeDegrees = 50f;

    [DataField, AutoNetworkedField]
    public int GroundCollisionMask = int.MaxValue;

    [DataField, AutoNetworkedField]
    public bool Grounded;

    [DataField, AutoNetworkedField]
    public System.Numerics.Vector3 GroundNormal = System.Numerics.Vector3.UnitZ;

    [DataField, AutoNetworkedField]
    public EntityUid? GroundEntity;

    internal bool JumpRequested;
}
