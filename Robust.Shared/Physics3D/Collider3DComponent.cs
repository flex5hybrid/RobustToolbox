using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.Physics3D;

/// <summary>
/// One or more local-space shapes owned by a 3D physics body. Multiple entries form a compound collider.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class Collider3DComponent : Component
{
    [DataField, AutoNetworkedField]
    public List<CollisionShape3D> Shapes = new();

    [ViewVariables]
    internal uint BackendRevision;
}
