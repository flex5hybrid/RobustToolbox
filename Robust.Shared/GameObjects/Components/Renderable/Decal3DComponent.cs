using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Robust.Shared.GameObjects;

/// <summary>
/// A thin textured surface in native 3D space. Decals inherit their entity's full transform and can therefore
/// be attached to floors, walls, ceilings, vehicles, and moving station grids without planar projection.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class Decal3DComponent : Component
{
    [DataField, AutoNetworkedField]
    public string Texture = string.Empty;

    [DataField, AutoNetworkedField]
    public Vector2 Size = Vector2.One;

    [DataField, AutoNetworkedField]
    public Vector3 Offset = new(0f, 0f, 0.002f);

    [DataField, AutoNetworkedField]
    public Color Color = Color.White;

    [DataField, AutoNetworkedField]
    public bool Visible = true;
}
