using System;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Robust.Client.GameObjects;

/// <summary>
/// Result of a first-person 3D spatial query.
/// </summary>
public readonly record struct World3DRaycastHit(EntityUid Entity, Vector3 Position, float Distance);

public sealed partial class World3DGridRenderingSystem
{
    private const float RaycastWallHeight = 2.6f;
    private const float RaycastObjectHeight = 0.9f;
    private const float RaycastCharacterHeight = 1.7f;
    private const float RaycastEyeHeight = 1.58f;

    [Dependency] private IEyeManager _raycastEyeManager = default!;

    /// <summary>
    /// Gets the exact ray used by the first-person perspective camera.
    /// </summary>
    public bool TryGetFirstPersonRay(out Vector3 origin, out Vector3 direction)
    {
        origin = default;
        direction = default;

        if (_playerManager.LocalEntity is not { Valid: true } player ||
            !TryComp(player, out TransformComponent? playerTransform))
        {
            return false;
        }

        var eye = _raycastEyeManager.CurrentEye;
        var playerPosition = _transform3DSystem.GetWorldPosition3D(player, playerTransform);
        origin = playerPosition + new Vector3(
            0f,
            0f,
            RaycastEyeHeight + _jumpHeight + _cameraBob);

        var forward2 = eye.Rotation.ToWorldVec();
        var horizontalLook = MathF.Cos(_firstPersonPitch);
        direction = new Vector3(
            -forward2.X * horizontalLook,
            -forward2.Y * horizontalLook,
            MathF.Sin(_firstPersonPitch));

        var lengthSquared = direction.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-8f)
            return false;

        direction /= MathF.Sqrt(lengthSquared);
        return true;
    }

    /// <summary>
    /// Returns the nearest physical entity intersected by the center-screen first-person ray.
    /// Existing 2D fixtures are temporarily extruded into volumetric AABBs so interaction targeting
    /// already respects height while the full 3D physics solver is still under construction.
    /// </summary>
    public bool TryRaycastFirstPerson(float maxDistance, out World3DRaycastHit hit)
    {
        hit = default;

        if (!float.IsFinite(maxDistance) || maxDistance <= 0f ||
            _playerManager.LocalEntity is not { Valid: true } player ||
            !TryComp(player, out TransformComponent? playerTransform) ||
            !TryGetFirstPersonRay(out var origin, out var direction))
        {
            return false;
        }

        var mapId = playerTransform.MapID;
        var nearestDistance = maxDistance;
        var found = false;

        var query = AllEntityQuery<TransformComponent, PhysicsComponent, FixturesComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var transform, out var body, out var fixtures, out var sprite))
        {
            if (uid == player ||
                transform.MapID != mapId ||
                !body.CanCollide ||
                !body.Hard ||
                !sprite._visible ||
                (sprite._containerOccluded && !sprite.OverrideContainerOcclusion) ||
                HasComp<MapGridComponent>(uid))
            {
                continue;
            }

            var (worldPosition, worldRotation) = _transformSystem.GetWorldPositionRotation(transform);
            var physicsTransform = new Robust.Shared.Physics.Transform(worldPosition, worldRotation);
            var bounds = default(Box2);
            var hasBounds = false;

            foreach (var fixture in fixtures.Fixtures.Values)
            {
                if (!fixture.Hard)
                    continue;

                for (var child = 0; child < fixture.Shape.ChildCount; child++)
                {
                    var childBounds = fixture.Shape.ComputeAABB(physicsTransform, child);
                    bounds = hasBounds ? bounds.Union(childBounds) : childBounds;
                    hasBounds = true;
                }
            }

            if (!hasBounds || bounds.Width < 0.02f || bounds.Height < 0.02f)
                continue;

            var baseZ = _transform3DSystem.GetWorldZ(uid);
            var topZ = GetRaycastTop(uid, body, bounds, baseZ);
            var minimum = new Vector3(bounds.Left, bounds.Bottom, baseZ + 0.005f);
            var maximum = new Vector3(bounds.Right, bounds.Top, topZ);

            if (!RayIntersectsAabb(origin, direction, minimum, maximum, nearestDistance, out var distance))
                continue;

            nearestDistance = distance;
            hit = new World3DRaycastHit(uid, origin + direction * distance, distance);
            found = true;
        }

        return found;
    }

    private float GetRaycastTop(EntityUid uid, PhysicsComponent body, Box2 bounds, float baseZ)
    {
        if ((body.BodyType & BodyType.KinematicController) != 0)
            return baseZ + RaycastCharacterHeight;

        if (body.BodyType != BodyType.Static)
            return baseZ + RaycastObjectHeight * 0.72f;

        if (TryComp(uid, out OccluderComponent? occluder) && occluder.Enabled)
            return baseZ + RaycastWallHeight;

        var height = bounds.MaxDimension > 1.4f
            ? RaycastObjectHeight * 1.35f
            : RaycastObjectHeight;
        return baseZ + height;
    }

    private static bool RayIntersectsAabb(
        Vector3 origin,
        Vector3 direction,
        Vector3 minimum,
        Vector3 maximum,
        float maxDistance,
        out float distance)
    {
        var near = 0f;
        var far = maxDistance;

        if (!RaySlab(origin.X, direction.X, minimum.X, maximum.X, ref near, ref far) ||
            !RaySlab(origin.Y, direction.Y, minimum.Y, maximum.Y, ref near, ref far) ||
            !RaySlab(origin.Z, direction.Z, minimum.Z, maximum.Z, ref near, ref far))
        {
            distance = 0f;
            return false;
        }

        distance = near;
        return near >= 0f && near <= maxDistance;
    }

    private static bool RaySlab(
        float origin,
        float direction,
        float minimum,
        float maximum,
        ref float near,
        ref float far)
    {
        const float epsilon = 1e-6f;
        if (MathF.Abs(direction) < epsilon)
            return origin >= minimum && origin <= maximum;

        var inverse = 1f / direction;
        var first = (minimum - origin) * inverse;
        var second = (maximum - origin) * inverse;
        if (first > second)
            (first, second) = (second, first);

        near = MathF.Max(near, first);
        far = MathF.Min(far, second);
        return far >= near;
    }
}
