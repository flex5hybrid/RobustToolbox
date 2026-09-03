using Robust.Shared.GameObjects;

namespace Robust.Shared.Physics3D;

/// <summary>
/// Redirects a legacy collision-enable request to an authoritative native 3D body.
/// The planar body remains disabled and is retained only as compatibility state.
/// </summary>
[ByRefEvent]
public readonly record struct Physics3DCollisionChangeRequestedEvent(
    EntityUid Entity,
    bool CanCollide);
