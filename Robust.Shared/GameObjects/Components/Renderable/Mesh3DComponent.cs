using Robust.Shared.GameStates;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Networked reference to a native 3D mesh and its physically meaningful surface properties.
/// Asset decoding remains client-side; the server only replicates stable resource paths and material state.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class Mesh3DComponent : Component
{
    /// <summary>
    /// Content-root path to an OBJ or glTF mesh resource.
    /// </summary>
    [DataField(required: true), AutoNetworkedField]
    public string Mesh = string.Empty;

    /// <summary>
    /// Optional content-root path to an albedo texture. An empty path uses only <see cref="Tint"/>.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string AlbedoTexture = string.Empty;

    [DataField, AutoNetworkedField]
    public Color Tint = Color.White;

    [DataField, AutoNetworkedField]
    public Color Emissive = Color.Transparent;

    [DataField, AutoNetworkedField]
    public float Roughness = 0.7f;

    [DataField, AutoNetworkedField]
    public float Metallic;

    [DataField, AutoNetworkedField]
    public bool DoubleSided;

    [DataField, AutoNetworkedField]
    public bool CastShadows = true;

    [DataField, AutoNetworkedField]
    public bool ReceiveLights = true;

    [DataField, AutoNetworkedField]
    public bool Visible = true;
}
