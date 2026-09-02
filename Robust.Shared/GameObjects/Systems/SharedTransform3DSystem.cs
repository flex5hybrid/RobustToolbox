using System;
using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Authoritative three-dimensional transform service.
/// Existing entities remain in compatibility mode until explicitly migrated, while authoritative entities
/// use their complete XYZ position and quaternion orientation as the source of truth.
/// </summary>
public sealed class SharedTransform3DSystem : EntitySystem
{
    private const int MaxHierarchyDepth = 256;
    private const float MinimumScale = 1e-6f;

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

    public bool IsAuthoritative(EntityUid uid)
    {
        return _transform3DQuery.TryGetComponent(uid, out var transform3D) && transform3D.Authoritative;
    }

    /// <summary>
    /// Promotes or demotes an entity at the 2D/3D migration boundary. Promotion preserves the current
    /// legacy local pose, after which all writes through this system update the 3D pose first.
    /// </summary>
    public void SetAuthoritative(EntityUid uid, bool authoritative, TransformComponent? transform = null)
    {
        if (!_transformQuery.Resolve(uid, ref transform, false))
            return;

        var transform3D = EnsureComp<Transform3DComponent>(uid);
        if (transform3D.Authoritative == authoritative)
            return;

        if (authoritative)
        {
            transform3D.LocalPosition3D = new Vector3(transform.LocalPosition, transform3D.LocalPosition3D.Z);
            var legacyYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float) transform.LocalRotation.Theta);
            transform3D.LocalRotation3D = NormalizeOrIdentity(
                Quaternion.Concatenate(legacyYaw, transform3D.LocalRotation3D));
        }
        else
        {
            _transform.SetLocalPosition(uid, new Vector2(
                transform3D.LocalPosition3D.X,
                transform3D.LocalPosition3D.Y), transform);
            _transform.SetLocalRotation(uid, GetYaw(transform3D.LocalRotation3D), transform);
        }

        transform3D.Authoritative = authoritative;
        Dirty(uid, transform3D);
    }

    public Vector3 GetLocalPosition3D(EntityUid uid, TransformComponent? transform = null)
    {
        if (!_transformQuery.Resolve(uid, ref transform, false))
            return Vector3.Zero;

        if (_transform3DQuery.TryGetComponent(uid, out var transform3D))
        {
            if (transform3D.Authoritative)
                return transform3D.LocalPosition3D;

            return new Vector3(transform.LocalPosition, transform3D.LocalPosition3D.Z);
        }

        return new Vector3(transform.LocalPosition, 0f);
    }

    public Quaternion GetLocalRotation3D(EntityUid uid, TransformComponent? transform = null)
    {
        if (!_transformQuery.Resolve(uid, ref transform, false))
            return Quaternion.Identity;

        if (_transform3DQuery.TryGetComponent(uid, out var transform3D) && transform3D.Authoritative)
            return NormalizeOrIdentity(transform3D.LocalRotation3D);

        var legacyYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float) transform.LocalRotation.Theta);
        if (!_transform3DQuery.TryGetComponent(uid, out transform3D))
            return legacyYaw;

        return NormalizeOrIdentity(Quaternion.Concatenate(legacyYaw, transform3D.LocalRotation3D));
    }

    public Vector3 GetLocalScale3D(EntityUid uid)
    {
        return _transform3DQuery.TryGetComponent(uid, out var transform3D)
            ? transform3D.LocalScale3D
            : Vector3.One;
    }

    public Matrix4x4 GetLocalMatrix3D(EntityUid uid, TransformComponent? transform = null)
    {
        if (!_transformQuery.Resolve(uid, ref transform, false))
            return Matrix4x4.Identity;

        return Matrix4x4.CreateScale(GetLocalScale3D(uid)) *
               Matrix4x4.CreateFromQuaternion(GetLocalRotation3D(uid, transform)) *
               Matrix4x4.CreateTranslation(GetLocalPosition3D(uid, transform));
    }

    /// <summary>
    /// Builds the complete parent-composed matrix using System.Numerics row-vector conventions.
    /// The hierarchy is shared during migration, but every authoritative node contributes a genuine 3D pose.
    /// </summary>
    public Matrix4x4 GetWorldMatrix3D(EntityUid uid, TransformComponent? transform = null)
    {
        if (!_transformQuery.Resolve(uid, ref transform, false))
            return Matrix4x4.Identity;

        var matrix = Matrix4x4.Identity;
        var currentUid = uid;
        var current = transform;

        for (var depth = 0; depth < MaxHierarchyDepth; depth++)
        {
            matrix *= GetLocalMatrix3D(currentUid, current);

            var parent = current.ParentUid;
            if (!parent.IsValid() || parent == currentUid || !_transformQuery.TryGetComponent(parent, out current))
                break;

            currentUid = parent;
        }

        return matrix;
    }

    public Vector3 GetWorldPosition3D(EntityUid uid, TransformComponent? transform = null)
    {
        return Vector3.Transform(Vector3.Zero, GetWorldMatrix3D(uid, transform));
    }

    public Quaternion GetWorldRotation3D(EntityUid uid, TransformComponent? transform = null)
    {
        var matrix = GetWorldMatrix3D(uid, transform);
        return Matrix4x4.Decompose(matrix, out _, out var rotation, out _)
            ? NormalizeOrIdentity(rotation)
            : Quaternion.Identity;
    }

    public float GetWorldZ(EntityUid uid, TransformComponent? transform = null)
    {
        return GetWorldPosition3D(uid, transform).Z;
    }

    public void SetPosition3D(EntityUid uid, Vector3 position, TransformComponent? transform = null)
    {
        SetWorldPosition3D(uid, position, transform);
    }

    public void SetWorldPosition3D(EntityUid uid, Vector3 position, TransformComponent? transform = null)
    {
        if (!IsFinite(position) || !_transformQuery.Resolve(uid, ref transform, false))
            return;

        if (!IsAuthoritative(uid))
        {
            _transform.SetWorldPosition(uid, new Vector2(position.X, position.Y));
            transform = Transform(uid);
            var parentZ = transform.ParentUid.IsValid() ? GetWorldZ(transform.ParentUid) : 0f;
            SetLocalZ(uid, position.Z - parentZ);
            return;
        }

        // Keep the old transform as a derived XY projection while legacy PVS and map ownership are being replaced.
        // SetWorldPosition may change the parent grid, so derive the authoritative local position afterwards.
        _transform.SetWorldPosition(uid, new Vector2(position.X, position.Y));
        transform = Transform(uid);

        var local = position;
        if (transform.ParentUid.IsValid())
        {
            var parentMatrix = GetWorldMatrix3D(transform.ParentUid);
            if (Matrix4x4.Invert(parentMatrix, out var inverseParent))
                local = Vector3.Transform(position, inverseParent);
        }

        SetLocalPositionCore(uid, local);
    }

    public void SetLocalPosition3D(EntityUid uid, Vector3 position, TransformComponent? transform = null)
    {
        if (!IsFinite(position) || !_transformQuery.Resolve(uid, ref transform, false))
            return;

        if (!IsAuthoritative(uid))
        {
            _transform.SetLocalPosition(uid, new Vector2(position.X, position.Y), transform);
            SetLocalZ(uid, position.Z);
            return;
        }

        SetLocalPositionCore(uid, position);

        // Derived legacy projection only. The authoritative value above remains untouched if the projection
        // cannot represent parent pitch, roll or Z.
        var world = GetWorldPosition3D(uid, transform);
        _transform.SetWorldPosition(uid, new Vector2(world.X, world.Y));
    }

    public void SetWorldZ(EntityUid uid, float worldZ, TransformComponent? transform = null)
    {
        if (!float.IsFinite(worldZ) || !_transformQuery.Resolve(uid, ref transform, false))
            return;

        var world = GetWorldPosition3D(uid, transform);
        SetWorldPosition3D(uid, new Vector3(world.X, world.Y, worldZ), transform);
    }

    public void SetLocalZ(EntityUid uid, float localZ)
    {
        if (!float.IsFinite(localZ))
            return;

        var local = GetLocalPosition3D(uid);
        SetLocalPositionCore(uid, new Vector3(local.X, local.Y, localZ));
    }

    public Quaternion GetRotation3D(EntityUid uid)
    {
        return GetLocalRotation3D(uid);
    }

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

        if (transform3D.Authoritative && _transformQuery.TryGetComponent(uid, out var transform))
            _transform.SetLocalRotation(uid, GetYaw(rotation), transform);
    }

    public void SetWorldRotation3D(EntityUid uid, Quaternion rotation, TransformComponent? transform = null)
    {
        if (!IsFinite(rotation) ||
            rotation.LengthSquared() < 1e-8f ||
            !_transformQuery.Resolve(uid, ref transform, false))
        {
            return;
        }

        rotation = Quaternion.Normalize(rotation);
        if (!IsAuthoritative(uid))
        {
            _transform.SetWorldRotation(uid, GetYaw(rotation));
            return;
        }

        var local = rotation;
        if (transform.ParentUid.IsValid())
            local = SpatialMath.RelativeTo(rotation, GetWorldRotation3D(transform.ParentUid));

        SetRotation3D(uid, local);
    }

    public Vector3 GetScale3D(EntityUid uid)
    {
        return GetLocalScale3D(uid);
    }

    public void SetScale3D(EntityUid uid, Vector3 scale)
    {
        if (!IsUsableScale(scale))
            return;

        var transform3D = EnsureComp<Transform3DComponent>(uid);
        if (transform3D.LocalScale3D.Equals(scale))
            return;

        transform3D.LocalScale3D = scale;
        Dirty(uid, transform3D);
    }

    public MapCoordinates3D ToMapCoordinates(EntityCoordinates3D coordinates)
    {
        if (!coordinates.IsValid(EntityManager) ||
            !_transformQuery.TryGetComponent(coordinates.EntityId, out var parent))
        {
            return MapCoordinates3D.Nullspace;
        }

        var world = Vector3.Transform(coordinates.Position, GetWorldMatrix3D(coordinates.EntityId, parent));
        return new MapCoordinates3D(world, parent.MapID);
    }

    public EntityCoordinates3D ToCoordinates(EntityUid parentUid, MapCoordinates3D coordinates)
    {
        if (!coordinates.IsValid ||
            !_transformQuery.TryGetComponent(parentUid, out var parent) ||
            parent.MapID != coordinates.MapId ||
            !Matrix4x4.Invert(GetWorldMatrix3D(parentUid, parent), out var inverseParent))
        {
            return EntityCoordinates3D.Invalid;
        }

        return new EntityCoordinates3D(parentUid, Vector3.Transform(coordinates.Position, inverseParent));
    }

    public NetCoordinates3D GetNetCoordinates(EntityCoordinates3D coordinates)
    {
        if (!coordinates.IsValid(EntityManager))
            return NetCoordinates3D.Invalid;

        return new NetCoordinates3D(GetNetEntity(coordinates.EntityId), coordinates.Position);
    }

    public EntityCoordinates3D GetCoordinates(NetCoordinates3D coordinates)
    {
        if (!coordinates.NetEntity.Valid)
            return EntityCoordinates3D.Invalid;

        return new EntityCoordinates3D(GetEntity(coordinates.NetEntity), coordinates.Position);
    }

    private void SetLocalPositionCore(EntityUid uid, Vector3 position)
    {
        if (!IsFinite(position))
            return;

        var transform3D = EnsureComp<Transform3DComponent>(uid);
        if (transform3D.LocalPosition3D.Equals(position))
            return;

        var oldPosition = transform3D.LocalPosition3D;
        transform3D.LocalPosition3D = position;
        Dirty(uid, transform3D);
        var moveEvent = new Transform3DPositionChangedEvent(oldPosition, position);
        RaiseLocalEvent(uid, ref moveEvent);
    }

    private void OnGetState(Entity<Transform3DComponent> entity, ref ComponentGetState args)
    {
        var position = entity.Comp.LocalPosition3D;
        var rotation = entity.Comp.LocalRotation3D;
        var scale = entity.Comp.LocalScale3D;
        args.State = new Transform3DComponentState
        {
            Authoritative = entity.Comp.Authoritative,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
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

        var oldPosition = entity.Comp.LocalPosition3D;
        var position = new Vector3(state.X, state.Y, state.Z);
        entity.Comp.Authoritative = state.Authoritative;
        entity.Comp.LocalPosition3D = IsFinite(position) ? position : Vector3.Zero;

        var rotation = new Quaternion(
            state.RotationX,
            state.RotationY,
            state.RotationZ,
            state.RotationW);
        entity.Comp.LocalRotation3D = NormalizeOrIdentity(rotation);

        var scale = new Vector3(state.ScaleX, state.ScaleY, state.ScaleZ);
        entity.Comp.LocalScale3D = IsUsableScale(scale) ? scale : Vector3.One;

        if (oldPosition != entity.Comp.LocalPosition3D)
        {
            var moveEvent = new Transform3DPositionChangedEvent(oldPosition, entity.Comp.LocalPosition3D);
            RaiseLocalEvent(entity.Owner, ref moveEvent);
        }
    }

    private static Angle GetYaw(Quaternion rotation)
    {
        rotation = NormalizeOrIdentity(rotation);
        var sinYaw = 2f * (rotation.W * rotation.Z + rotation.X * rotation.Y);
        var cosYaw = 1f - 2f * (rotation.Y * rotation.Y + rotation.Z * rotation.Z);
        return new Angle(MathF.Atan2(sinYaw, cosYaw));
    }

    private static Quaternion NormalizeOrIdentity(Quaternion rotation)
    {
        return IsFinite(rotation) && rotation.LengthSquared() >= 1e-8f
            ? Quaternion.Normalize(rotation)
            : Quaternion.Identity;
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

    private static bool IsUsableScale(Vector3 value)
    {
        return IsFinite(value) &&
               MathF.Abs(value.X) >= MinimumScale &&
               MathF.Abs(value.Y) >= MinimumScale &&
               MathF.Abs(value.Z) >= MinimumScale;
    }
}
