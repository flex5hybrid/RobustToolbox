using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Native engine primitive used by early 3D maps and as a deterministic fallback when no model asset is present.
/// It is intentionally independent of 2D sprites and fixtures.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class Primitive3DComponent : Component
{
    [DataField, AutoNetworkedField]
    public Vector3 Size = Vector3.One;

    [DataField, AutoNetworkedField]
    public Color Color = Color.Gray;

    [DataField, AutoNetworkedField]
    public bool Visible = true;
}
