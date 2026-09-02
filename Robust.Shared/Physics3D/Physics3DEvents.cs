using System.Numerics;
using Robust.Shared.GameObjects;

namespace Robust.Shared.Physics3D;

/// <summary>
/// Raised once for both participants when a new authoritative 3D contact begins.
/// The normal always points from <see cref="OtherEntity"/> toward <see cref="OurEntity"/>.
/// </summary>
[ByRefEvent]
public readonly record struct StartCollide3DEvent(
    EntityUid OurEntity,
    EntityUid OtherEntity,
    Vector3 Position,
    Vector3 Normal,
    float Penetration,
    bool Sensor);

/// <summary>
/// Raised for every fixed step in which an authoritative 3D contact exists.
/// </summary>
[ByRefEvent]
public readonly record struct Collide3DEvent(
    EntityUid OurEntity,
    EntityUid OtherEntity,
    Vector3 Position,
    Vector3 Normal,
    float Penetration,
    bool Sensor);

/// <summary>
/// Raised once for both participants when an authoritative 3D contact ends.
/// </summary>
[ByRefEvent]
public readonly record struct EndCollide3DEvent(
    EntityUid OurEntity,
    EntityUid OtherEntity,
    bool Sensor);
