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
}
