using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Replicated definition for a deterministic client-rendered volumetric particle emitter.
/// Gameplay effects remain separate authoritative entities; these particles are presentation only.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class ParticleEmitter3DComponent : Component
{
    [DataField, AutoNetworkedField]
    public string Texture = string.Empty;

    [DataField, AutoNetworkedField]
    public float Rate = 8f;

    [DataField, AutoNetworkedField]
    public float Lifetime = 1.5f;

    [DataField, AutoNetworkedField]
    public int MaxParticles = 64;

    [DataField, AutoNetworkedField]
    public Vector3 InitialVelocity = new(0f, 0f, 0.8f);

    [DataField, AutoNetworkedField]
    public Vector3 VelocityRandomness = new(0.25f);

    [DataField, AutoNetworkedField]
    public Vector3 Acceleration = new(0f, 0f, 0.15f);

    [DataField, AutoNetworkedField]
    public float StartSize = 0.12f;

    [DataField, AutoNetworkedField]
    public float EndSize = 0.42f;

    [DataField, AutoNetworkedField]
    public Color StartColor = Color.White;

    [DataField, AutoNetworkedField]
    public Color EndColor = Color.Transparent;

    [DataField, AutoNetworkedField]
    public uint Seed = 1;

    [DataField, AutoNetworkedField]
    public bool Enabled = true;
}
