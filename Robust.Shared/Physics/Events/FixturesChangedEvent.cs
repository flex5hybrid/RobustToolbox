using Robust.Shared.GameObjects;
using Robust.Shared.Physics.Components;

namespace Robust.Shared.Physics.Events;

/// <summary>
/// Directed event raised after fixture geometry or collision properties have been recomputed.
/// Consumers may rebuild derived collision representations from the final fixture state.
/// </summary>
[ByRefEvent]
public readonly record struct FixturesChangedEvent(
    Entity<PhysicsComponent, FixturesComponent> Entity);
