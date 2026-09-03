using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Robust.Shared.Physics3D;

/// <summary>
/// Temporary event-compatibility metadata for content promoted from fixture-based 2D collision.
/// Shape indices map back to legacy fixture IDs while listeners are migrated to native 3D events.
/// </summary>
[RegisterComponent]
public sealed partial class LegacyPhysics3DBridgeComponent : Component
{
    [DataField]
    public List<string> ShapeFixtureIds = new();

    [DataField]
    public bool RaiseLegacyEvents = true;

    /// <summary>
    /// Collision state requested through the retained 2D API. This remains independent of temporary absence of
    /// shapes so that adding a fixture later restores the intended native collision state.
    /// </summary>
    [DataField]
    public bool RequestedCanCollide = true;
}
