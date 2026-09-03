using Robust.Shared.GameStates;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Authoritative playback state for a named animation clip in a native 3D asset.
/// Clients derive presentation time from the replicated start tick instead of advancing shared simulation state.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class AnimationPlayer3DComponent : Component
{
    [DataField, AutoNetworkedField]
    public string Clip = string.Empty;

    [DataField, AutoNetworkedField]
    public float PlaybackRate = 1f;

    [DataField, AutoNetworkedField]
    public bool Loop = true;

    [DataField, AutoNetworkedField]
    public bool Playing = true;

    [DataField, AutoNetworkedField]
    public uint StartTick;
}
