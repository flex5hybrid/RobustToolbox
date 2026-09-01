using System;
using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Transitional 3D spatial layer built on top of Robust's existing 2D transform hierarchy.
/// X/Y continue to come from <see cref="TransformComponent"/> so existing maps, physics and PVS stay intact.
/// Z is hierarchical and networked through <see cref="Transform3DComponent"/>.
/// </summary>
public sealed class SharedTransform3DSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _transform = default!;

    private EntityQuery<TransformComponent> _transformQuery;
    private EntityQuery<Transform3DComponent> _transform3DQuery;

    public override void Initialize()
    {
        base.Initialize();

        _transformQuery = GetEntityQuery<TransformComponent>();
        _transform3DQuery = GetEntityQuery<Transform3DComponent>();

        SubscribeLocalEvent<Transform3DComponent, ComponentGetState>(OnGetState);
        SubscribeLocalEvent<Transform3DComponent, ComponentHandleState>(OnHandleState);
    }

    /// <summary>
    /// Gets an entity's local 3D position. X/Y are read live from the legacy transform and Z from the 3D layer.
    /// </summary>
    public Vector3 GetLocalPosition3D(EntityUid uid, TransformComponent? transform = null)
    {
        if (!_transformQuery.Resolve(uid, ref transform, false))
            return Vector3.Zero;

        var z = _transform3DQuery.TryGetComponent(uid, out var transform3D)
            ? transform3D.LocalZ
            : 0f;

        return new Vector3(transform.LocalPosition.X, transform.LocalPosition.Y, z);
    }

    /// <summary>
    /// Gets the real 3D world position exposed to 3D presentation/simulation code.
    /// The existing transform hierarchy supplies world X/Y; local Z values are accumulated through the same hierarchy.
    /// </summary>
    public Vector3 GetWorldPosition3D(EntityUid uid, TransformComponent? transform = null)
    {
        if (!_transformQuery.Resolve(uid, ref transform, false))
            return Vector3.Zero;

        var xy = _transform.GetWorldPosition(transform);
        return new Vector3(xy.X, xy.Y, GetWorldZ(uid, transform));
    }

    /// <summary>
    /// Gets world Z by accumulating local Z through the existing TransformComponent parent chain.
    /// This means assigning Z to a grid/map automatically raises all of its children into that deck.
    /// </summary>
    public float GetWorldZ(EntityUid uid, TransformComponent? transform = null)
    {
        if (!_transformQuery.Resolve(uid, ref transform, false))
            return 0f;

        var z = 0f;
        var currentUid = uid;
        var current = transform;

        // Normal Robust transform hierarchies are acyclic. Keep a defensive ceiling because client state
        // application can temporarily observe malformed parent chains while entities are being reconciled.
        for (var depth = 0; depth < 256; depth++)
        {
            if (_transform3DQuery.TryGetComponent(currentUid, out var current3D))
                z += current3D.LocalZ;

            var parent = current.ParentUid;
            if (!parent.IsValid() || parent == currentUid || !_transformQuery.TryGetComponent(parent, out current))
                break;

            currentUid = parent;
        }

        return z;
    }

    /// <summary>
    /// Sets world X/Y through the existing Robust transform and world Z through the new hierarchical 3D layer.
    /// Existing 2D physics therefore remains authoritative for horizontal movement during migration.
    /// </summary>
    public void SetPosition3D(EntityUid uid, Vector3 position, TransformComponent? transform = null)
    {
        SetWorldPosition3D(uid, position, transform);
    }

    public void SetWorldPosition3D(EntityUid uid, Vector3 position, TransformComponent? transform = null)
    {
        if (!IsFinite(position) || !_transformQuery.Resolve(uid, ref transform, false))
            return;

        _transform.SetWorldPosition(uid, new Vector2(position.X, position.Y));

        // SetWorldPosition may re-parent the entity to another grid, so calculate the parent's Z afterwards.
        var parentZ = transform.ParentUid.IsValid() ? GetWorldZ(transform.ParentUid) : 0f;
        SetLocalZ(uid, position.Z - parentZ);
    }

    public void SetLocalPosition3D(EntityUid uid, Vector3 position, TransformComponent? transform = null)
    {
        if (!IsFinite(position) || !_transformQuery.Resolve(uid, ref transform, false))
            return;

        _transform.SetLocalPosition(uid, new Vector2(position.X, position.Y), transform);
        SetLocalZ(uid, position.Z);
    }

    public void SetWorldZ(EntityUid uid, float worldZ, TransformComponent? transform = null)
    {
        if (!float.IsFinite(worldZ) || !_transformQuery.Resolve(uid, ref transform, false))
            return;

        var parentZ = transform.ParentUid.IsValid() ? GetWorldZ(transform.ParentUid) : 0f;
        SetLocalZ(uid, worldZ - parentZ);
    }

    public void SetLocalZ(EntityUid uid, float localZ)
    {
        if (!float.IsFinite(localZ))
            return;

        var transform3D = EnsureComp<Transform3DComponent>(uid);
        if (transform3D.LocalZ.Equals(localZ))
            return;

        transform3D.LocalZ = localZ;
        Dirty(uid, transform3D);
    }

    public Quaternion GetRotation3D(EntityUid uid)
    {
        return _transform3DQuery.TryGetComponent(uid, out var transform3D)
            ? transform3D.LocalRotation3D
            : Quaternion.Identity;
    }

    /// <summary>
    /// Sets the additional local 3D rotation. Legacy 2D yaw remains separate until the physics migration.
    /// </summary>
    public void SetRotation3D(EntityUid uid, Quaternion rotation)
    {
        if (!IsFinite(rotation) || rotation.LengthSquared() < 1e-8f)
            return;

        rotation = Quaternion.Normalize(rotation);
        var transform3D = EnsureComp<Transform3DComponent>(uid);
        if (transform3D.LocalRotation3D.Equals(rotation))
            return;

        transform3D.LocalRotation3D = rotation;
        Dirty(uid, transform3D);
    }

    public Vector3 GetScale3D(EntityUid uid)
    {
        return _transform3DQuery.TryGetComponent(uid, out var transform3D)
            ? transform3D.LocalScale3D
            : Vector3.One;
    }

    public void SetScale3D(EntityUid uid, Vector3 scale)
    {
        if (!IsFinite(scale))
            return;

        var transform3D = EnsureComp<Transform3DComponent>(uid);
        if (transform3D.LocalScale3D.Equals(scale))
            return;

        transform3D.LocalScale3D = scale;
        Dirty(uid, transform3D);
    }

    private void OnGetState(Entity<Transform3DComponent> entity, ref ComponentGetState args)
    {
        var rotation = entity.Comp.LocalRotation3D;
        var scale = entity.Comp.LocalScale3D;
        args.State = new Transform3DComponentState
        {
            Z = entity.Comp.LocalZ,
            RotationX = rotation.X,
            RotationY = rotation.Y,
            RotationZ = rotation.Z,
            RotationW = rotation.W,
            ScaleX = scale.X,
            ScaleY = scale.Y,
            ScaleZ = scale.Z,
        };
    }

    private void OnHandleState(Entity<Transform3DComponent> entity, ref ComponentHandleState args)
    {
        if (args.Current is not Transform3DComponentState state)
            return;

        entity.Comp.LocalZ = float.IsFinite(state.Z) ? state.Z : 0f;

        var rotation = new Quaternion(
            state.RotationX,
            state.RotationY,
            state.RotationZ,
            state.RotationW);
        entity.Comp.LocalRotation3D = IsFinite(rotation) && rotation.LengthSquared() >= 1e-8f
            ? Quaternion.Normalize(rotation)
            : Quaternion.Identity;

        var scale = new Vector3(state.ScaleX, state.ScaleY, state.ScaleZ);
        entity.Comp.LocalScale3D = IsFinite(scale) ? scale : Vector3.One;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return float.IsFinite(value.X) &&
               float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) &&
               float.IsFinite(value.W);
    }
}
