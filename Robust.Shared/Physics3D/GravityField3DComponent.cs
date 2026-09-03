using System;
using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Robust.Shared.Physics3D;

/// <summary>
/// Root-local gravity field for a native map or grid. Direction rotates with the station or vehicle.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class GravityField3DComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Enabled = true;

    [DataField, AutoNetworkedField]
    public Vector3 Direction = -Vector3.UnitZ;

    [DataField, AutoNetworkedField]
    public float Acceleration = SharedPhysics3DSystem.DefaultGravity.Length();
}
