using System;
using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Robust.Shared.GameObjects;

[Serializable, NetSerializable]
public enum LightKind3D : byte
{
    Point,
    Spot,
}

/// <summary>
/// Volumetric extension for a normal point light. Colour, energy, radius and enabled state remain on
/// <see cref="SharedPointLightComponent"/> so existing powered-light gameplay continues to drive the source.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class PointLight3DComponent : Component
{
    [DataField, AutoNetworkedField]
    public LightKind3D Kind;

    [DataField, AutoNetworkedField]
    public Vector3 Offset;

    [DataField, AutoNetworkedField]
    public Vector3 Direction = Vector3.UnitY;

    [DataField, AutoNetworkedField]
    public float InnerConeDegrees = 22f;

    [DataField, AutoNetworkedField]
    public float OuterConeDegrees = 35f;
}
