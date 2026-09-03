using Robust.Shared.GameStates;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Engine-level, server-visible first-person view state. Gameplay requests carry intent only; authoritative systems
/// reconstruct their rays from this state instead of trusting a client-provided cursor or world coordinate.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class View3DComponent : Component
{
    [DataField, AutoNetworkedField, ViewVariables]
    public bool Enabled;

    [DataField, AutoNetworkedField, ViewVariables]
    public float Yaw;

    [DataField, AutoNetworkedField, ViewVariables]
    public float Pitch = -0.075f;

    [DataField, AutoNetworkedField, ViewVariables]
    public float EyeHeight = 1.58f;
}
