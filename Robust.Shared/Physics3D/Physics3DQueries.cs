using System;
using System.Numerics;
using Robust.Shared.GameObjects;

namespace Robust.Shared.Physics3D;

public readonly record struct Ray3D(Vector3 Origin, Vector3 Direction)
{
    public bool TryNormalize(out Ray3D ray)
    {
        var lengthSquared = Direction.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-8f || !IsFinite(Origin))
        {
            ray = default;
            return false;
        }

        ray = new Ray3D(Origin, Direction / MathF.Sqrt(lengthSquared));
        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}

public readonly record struct PhysicsRayHit3D(
    EntityUid Entity,
    Vector3 Position,
    Vector3 Normal,
    float Distance,
    bool Sensor);

public readonly record struct PhysicsSweepHit3D(
    EntityUid Entity,
    Vector3 Position,
    Vector3 Normal,
    float Distance,
    bool Sensor);

public readonly record struct PhysicsContact3D(
    EntityUid First,
    EntityUid Second,
    Vector3 Position,
    Vector3 Normal,
    float Penetration,
    bool Sensor);
