using Robust.Shared.GameObjects;

namespace Robust.Shared.Physics3D;

/// <summary>
/// Client-local marker selecting a dynamic body for prediction. It is never networked and never grants authority:
/// every authoritative snapshot rebuilds the backend body before pending input is replayed.
/// </summary>
[RegisterComponent]
public sealed partial class PredictedPhysics3DComponent : Component
{
}
