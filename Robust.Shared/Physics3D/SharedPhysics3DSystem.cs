using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;

namespace Robust.Shared.Physics3D;

/// <summary>
/// Owns server-side Bepu simulations and synchronizes their results into authoritative Transform3D state.
/// Content interacts with components and this system; backend handles never cross the engine boundary.
/// </summary>
public sealed class SharedPhysics3DSystem : EntitySystem
{
    public static readonly Vector3 DefaultGravity = new(0f, 0f, -14.5f);
    public const float FixedTimeStep = 1f / 60f;
    private const int MaximumCatchUpSteps = 8;

    [Dependency] private INetManager _network = default!;
    [Dependency] private SharedTransform3DSystem _transform3D = default!;

    private readonly Dictionary<MapId, PhysicsWorld3D> _worlds = new();
    private readonly Dictionary<EntityUid, BodyRegistration> _registrations = new();
    private readonly HashSet<EntityUid> _pending = new();
    private readonly List<EntityUid> _movedBetweenMaps = new();
    private float _accumulator;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PhysicsBody3DComponent, ComponentStartup>(OnBodyStartup);
        SubscribeLocalEvent<PhysicsBody3DComponent, ComponentShutdown>(OnBodyShutdown);
        SubscribeLocalEvent<Collider3DComponent, ComponentStartup>(OnColliderStartup);
        SubscribeLocalEvent<Collider3DComponent, ComponentShutdown>(OnColliderShutdown);
        SubscribeLocalEvent<MapRemovedEvent>(OnMapRemoved);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_network.IsServer)
            return;

        RefreshCrossMapBodies();
        FlushPending();

        _accumulator += Math.Clamp(frameTime, 0f, FixedTimeStep * MaximumCatchUpSteps);
        var steps = 0;
        while (_accumulator >= FixedTimeStep && steps < MaximumCatchUpSteps)
        {
            foreach (var world in _worlds.Values)
                world.Simulation.Timestep(FixedTimeStep);

            _accumulator -= FixedTimeStep;
            steps++;
        }

        if (steps > 0)
            SynchronizeDynamicBodies();
    }

    public override void Shutdown()
    {
        foreach (var world in _worlds.Values)
            world.Dispose();

        _worlds.Clear();
        _registrations.Clear();
        _pending.Clear();
        _movedBetweenMaps.Clear();
        _accumulator = 0f;
        base.Shutdown();
    }

    public void RefreshBody(EntityUid uid)
    {
        if (!_network.IsServer)
            return;

        RemoveBody(uid);
        _pending.Add(uid);
    }

    public bool TryGetBodyPose(EntityUid uid, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.Identity;

        if (!_registrations.TryGetValue(uid, out var registration))
            return false;

        var world = _worlds[registration.MapId];
        RigidPose pose;
        if (registration.IsStatic)
            pose = world.Simulation.Statics[registration.StaticHandle].Pose;
        else
            pose = world.Simulation.Bodies[registration.BodyHandle].Pose;

        rotation = RemoveShapeRotation(pose.Orientation, registration.ShapeRotation);
        position = pose.Position - rotation.Rotate(registration.ShapeOffset);
        return true;
    }

    public bool TryGetVelocity(EntityUid uid, out Vector3 linear, out Vector3 angular)
    {
        linear = default;
        angular = default;
        if (!_registrations.TryGetValue(uid, out var registration) || registration.IsStatic)
            return false;

        var reference = _worlds[registration.MapId].Simulation.Bodies[registration.BodyHandle];
        linear = reference.Velocity.Linear;
        angular = reference.Velocity.Angular;
        return true;
    }

    public bool SetVelocity(EntityUid uid, Vector3 linear, Vector3 angular, bool wake = true)
    {
        if (!SpatialMath.IsFinite(linear) ||
            !SpatialMath.IsFinite(angular) ||
            !_registrations.TryGetValue(uid, out var registration) ||
            registration.IsStatic)
        {
            return false;
        }

        var reference = _worlds[registration.MapId].Simulation.Bodies[registration.BodyHandle];
        reference.Velocity.Linear = linear;
        reference.Velocity.Angular = angular;
        if (wake)
            reference.Awake = true;

        if (TryComp(uid, out PhysicsBody3DComponent? body))
        {
            body.LinearVelocity = linear;
            body.AngularVelocity = angular;
            Dirty(uid, body);
        }

        return true;
    }

    public bool ApplyLinearImpulse(EntityUid uid, Vector3 impulse)
    {
        if (!SpatialMath.IsFinite(impulse) ||
            !_registrations.TryGetValue(uid, out var registration) ||
            registration.IsStatic)
        {
            return false;
        }

        var reference = _worlds[registration.MapId].Simulation.Bodies[registration.BodyHandle];
        if (reference.Kinematic)
            return false;

        reference.Awake = true;
        reference.ApplyLinearImpulse(impulse);
        return true;
    }

    public bool TeleportBody(EntityUid uid, Vector3 position, Quaternion rotation)
    {
        if (!SpatialMath.IsFinite(position) ||
            !SpatialMath.IsFinite(rotation) ||
            rotation.LengthSquared() < 1e-8f ||
            !_registrations.TryGetValue(uid, out var registration))
        {
            return false;
        }

        rotation = SpatialMath.Normalize(rotation);
        var physicsPosition = position + rotation.Rotate(registration.ShapeOffset);
        var physicsRotation = SpatialMath.Compose(registration.ShapeRotation, rotation);
        var world = _worlds[registration.MapId];
        if (registration.IsStatic)
        {
            var reference = world.Simulation.Statics[registration.StaticHandle];
            reference.Pose = new RigidPose(physicsPosition, physicsRotation);
            reference.UpdateBounds();
        }
        else
        {
            var reference = world.Simulation.Bodies[registration.BodyHandle];
            reference.Pose = new RigidPose(physicsPosition, physicsRotation);
            reference.UpdateBounds();
            reference.Awake = true;
        }

        _transform3D.SetWorldPosition3D(uid, position);
        _transform3D.SetWorldRotation3D(uid, rotation);
        return true;
    }

    public bool TryRayCast(
        MapId mapId,
        Ray3D ray,
        float maxDistance,
        int collisionMask,
        EntityUid? ignoredEntity,
        bool includeSensors,
        out PhysicsRayHit3D hit)
    {
        hit = default;
        if (!float.IsFinite(maxDistance) ||
            maxDistance <= 0f ||
            !ray.TryNormalize(out ray) ||
            !_worlds.TryGetValue(mapId, out var world))
        {
            return false;
        }

        var handler = new RaycastHitHandler3D(
            world.CollisionProperties,
            collisionMask,
            ignoredEntity,
            includeSensors);
        world.Simulation.RayCast(ray.Origin, ray.Direction, maxDistance, ref handler);
        if (!handler.Found)
            return false;

        hit = new PhysicsRayHit3D(
            handler.Entity,
            ray.Origin + ray.Direction * handler.Distance,
            handler.Normal,
            handler.Distance,
            handler.Sensor);
        return true;
    }

    public void RequestCharacterJump(EntityUid uid)
    {
        if (TryComp(uid, out CharacterController3DComponent? character))
            character.JumpRequested = true;
    }

    /// <summary>
    /// Converts world-space horizontal input into a dynamic upright character velocity. Call this once per
    /// server simulation update before this system advances its fixed physics worlds.
    /// </summary>
    public bool DriveCharacter(EntityUid uid, Vector2 wishDirection, bool sprinting, float frameTime)
    {
        if (!_network.IsServer ||
            !TryComp(uid, out CharacterController3DComponent? character) ||
            !TryComp(uid, out PhysicsBody3DComponent? body) ||
            body.BodyType != PhysicsBodyType3D.Character ||
            !TryComp(uid, out TransformComponent? transform) ||
            !TryGetVelocity(uid, out var linear, out var angular))
        {
            return false;
        }

        var position = _transform3D.GetWorldPosition3D(uid, transform);
        var probeOrigin = position + Vector3.UnitZ * MathF.Max(0f, character.GroundProbeStart);
        var probeLength = MathF.Max(0.01f, character.GroundProbeStart + character.GroundProbeDistance);
        var grounded = TryRayCast(
                           transform.MapID,
                           new Ray3D(probeOrigin, -Vector3.UnitZ),
                           probeLength,
                           character.GroundCollisionMask,
                           uid,
                           false,
                           out var groundHit) &&
                       Vector3.Dot(groundHit.Normal, Vector3.UnitZ) >=
                       MathF.Cos(Math.Clamp(character.MaximumSlopeDegrees, 0f, 89f) * MathF.PI / 180f);

        EntityUid? newGroundEntity = grounded ? groundHit.Entity : null;
        var newGroundNormal = grounded ? groundHit.Normal : Vector3.UnitZ;
        if (character.Grounded != grounded ||
            character.GroundEntity != newGroundEntity ||
            !character.GroundNormal.Equals(newGroundNormal))
        {
            character.Grounded = grounded;
            character.GroundEntity = newGroundEntity;
            character.GroundNormal = newGroundNormal;
            Dirty(uid, character);
        }

        if (wishDirection.LengthSquared() > 1f)
            wishDirection = Vector2.Normalize(wishDirection);

        var targetSpeed = sprinting ? character.SprintSpeed : character.WalkSpeed;
        var target = wishDirection * MathF.Max(0f, targetSpeed);
        var current = new Vector2(linear.X, linear.Y);
        var acceleration = grounded ? character.GroundAcceleration : character.AirAcceleration;
        current = MoveTowards(current, target, MathF.Max(0f, acceleration) * Math.Clamp(frameTime, 0f, 0.1f));
        linear.X = current.X;
        linear.Y = current.Y;

        if (character.JumpRequested)
        {
            character.JumpRequested = false;
            if (grounded)
            {
                linear.Z = MathF.Max(0f, character.JumpSpeed);
                character.Grounded = false;
                character.GroundEntity = null;
                Dirty(uid, character);
            }
        }

        return SetVelocity(uid, linear, angular);
    }

    private void OnBodyStartup(Entity<PhysicsBody3DComponent> entity, ref ComponentStartup args)
    {
        if (_network.IsServer)
            _pending.Add(entity.Owner);
    }

    private void OnBodyShutdown(Entity<PhysicsBody3DComponent> entity, ref ComponentShutdown args)
    {
        RemoveBody(entity.Owner);
    }

    private void OnColliderStartup(Entity<Collider3DComponent> entity, ref ComponentStartup args)
    {
        if (_network.IsServer)
            _pending.Add(entity.Owner);
    }

    private void OnColliderShutdown(Entity<Collider3DComponent> entity, ref ComponentShutdown args)
    {
        RemoveBody(entity.Owner);
    }

    private void OnMapRemoved(MapRemovedEvent args)
    {
        if (!_worlds.Remove(args.MapId, out var world))
            return;

        _movedBetweenMaps.Clear();
        foreach (var (uid, registration) in _registrations)
        {
            if (registration.MapId == args.MapId)
                _movedBetweenMaps.Add(uid);
        }

        foreach (var uid in _movedBetweenMaps)
        {
            _registrations.Remove(uid);
            if (TryComp(uid, out PhysicsBody3DComponent? body))
            {
                body.BackendHandle = -1;
                body.BackendStatic = false;
            }
        }

        world.Dispose();
        _movedBetweenMaps.Clear();
    }

    private void RefreshCrossMapBodies()
    {
        _movedBetweenMaps.Clear();
        foreach (var (uid, registration) in _registrations)
        {
            if (!TryComp(uid, out TransformComponent? transform) || transform.MapID != registration.MapId)
                _movedBetweenMaps.Add(uid);
        }

        foreach (var uid in _movedBetweenMaps)
            RefreshBody(uid);

        _movedBetweenMaps.Clear();
    }

    private void FlushPending()
    {
        if (_pending.Count == 0)
            return;

        foreach (var uid in _pending)
            TryCreateBody(uid);

        _pending.Clear();
    }

    private bool TryCreateBody(EntityUid uid)
    {
        if (_registrations.ContainsKey(uid) ||
            !TryComp(uid, out PhysicsBody3DComponent? body) ||
            !TryComp(uid, out Collider3DComponent? collider) ||
            !TryComp(uid, out TransformComponent? transform) ||
            transform.MapID == MapId.Nullspace ||
            collider.Shapes.Count == 0)
        {
            return false;
        }

        // Compound construction is the next backend slice. Until then, rejecting unsupported descriptions is
        // safer than silently dropping collision geometry.
        if (collider.Shapes.Count != 1)
            return false;

        _transform3D.SetAuthoritative(uid, true, transform);
        var world = GetOrCreateWorld(transform.MapID);
        var position = _transform3D.GetWorldPosition3D(uid, transform);
        var entityRotation = _transform3D.GetWorldRotation3D(uid, transform);
        var shapeDefinition = collider.Shapes[0];
        var shapeRotation = SpatialMath.Normalize(shapeDefinition.Rotation);
        var shapeOffset = shapeDefinition.Offset;
        if (!SpatialMath.IsFinite(shapeOffset))
            return false;

        var physicsPosition = position + entityRotation.Rotate(shapeOffset);
        var pose = new RigidPose(physicsPosition, SpatialMath.Compose(shapeRotation, entityRotation));
        var velocity = new BodyVelocity
        {
            Linear = body.LinearVelocity,
            Angular = body.AngularVelocity,
        };

        BodyRegistration? registration = shapeDefinition switch
        {
            BoxShape3D box when IsPositive(box.Size) => AddConvex(
                world,
                uid,
                body,
                pose,
                velocity,
                shapeDefinition,
                shapeOffset,
                shapeRotation,
                new Box(box.Size.X, box.Size.Y, box.Size.Z)),
            SphereShape3D sphere when float.IsFinite(sphere.Radius) && sphere.Radius > 0f => AddConvex(
                world,
                uid,
                body,
                pose,
                velocity,
                shapeDefinition,
                shapeOffset,
                shapeRotation,
                new Sphere(sphere.Radius)),
            CapsuleShape3D capsule when IsPositive(capsule.Radius, capsule.Length) => AddConvex(
                world,
                uid,
                body,
                pose,
                velocity,
                shapeDefinition,
                shapeOffset,
                shapeRotation,
                new Capsule(capsule.Radius, capsule.Length)),
            CylinderShape3D cylinder when IsPositive(cylinder.Radius, cylinder.Length) => AddConvex(
                world,
                uid,
                body,
                pose,
                velocity,
                shapeDefinition,
                shapeOffset,
                shapeRotation,
                new Cylinder(cylinder.Radius, cylinder.Length)),
            _ => null,
        };

        if (registration is null)
            return false;

        _registrations.Add(uid, registration);
        body.BackendHandle = registration.IsStatic
            ? registration.StaticHandle.Value
            : registration.BodyHandle.Value;
        body.BackendStatic = registration.IsStatic;
        return true;
    }

    private BodyRegistration AddConvex<TShape>(
        PhysicsWorld3D world,
        EntityUid uid,
        PhysicsBody3DComponent body,
        RigidPose pose,
        BodyVelocity velocity,
        CollisionShape3D shapeDefinition,
        Vector3 shapeOffset,
        Quaternion shapeRotation,
        TShape shape)
        where TShape : unmanaged, IConvexShape
    {
        if (body.BodyType == PhysicsBodyType3D.Static)
        {
            var shapeIndex = world.Simulation.Shapes.Add(shape);
            var handle = world.Simulation.Statics.Add(new StaticDescription(pose, shapeIndex));
            var registration = BodyRegistration.ForStatic(
                uid,
                world.MapId,
                handle,
                shapeIndex,
                shapeOffset,
                shapeRotation);
            world.CollisionProperties.Add(registration.CollidablePacked, uid, body, shapeDefinition);
            return registration;
        }

        BodyDescription description;
        if (body.BodyType == PhysicsBodyType3D.Kinematic)
        {
            description = BodyDescription.CreateConvexKinematic(
                pose,
                velocity,
                world.Simulation.Shapes,
                shape);
        }
        else
        {
            description = BodyDescription.CreateConvexDynamic(
                pose,
                velocity,
                MathF.Max(0.001f, body.Mass),
                world.Simulation.Shapes,
                shape);

            // Upright characters still participate as fully dynamic bodies, but their own capsule cannot
            // topple. External platforms and impulses continue to affect linear motion.
            if (body.BodyType == PhysicsBodyType3D.Character)
                description.LocalInertia.InverseInertiaTensor = default;
        }

        var bodyHandle = world.Simulation.Bodies.Add(description);
        var collidablePacked = world.Simulation.Bodies[bodyHandle].CollidableReference.Packed;
        var registration = BodyRegistration.ForBody(
            uid,
            world.MapId,
            bodyHandle,
            description.Collidable.Shape,
            collidablePacked,
            shapeOffset,
            shapeRotation);
        world.CollisionProperties.Add(registration.CollidablePacked, uid, body, shapeDefinition);
        return registration;
    }

    private PhysicsWorld3D GetOrCreateWorld(MapId mapId)
    {
        if (_worlds.TryGetValue(mapId, out var world))
            return world;

        world = new PhysicsWorld3D(mapId, DefaultGravity);
        _worlds.Add(mapId, world);
        return world;
    }

    private void RemoveBody(EntityUid uid)
    {
        _pending.Remove(uid);
        if (!_registrations.Remove(uid, out var registration) ||
            !_worlds.TryGetValue(registration.MapId, out var world))
        {
            return;
        }

        world.CollisionProperties.Remove(registration.CollidablePacked);
        if (registration.IsStatic)
            world.Simulation.Statics.Remove(registration.StaticHandle);
        else
            world.Simulation.Bodies.Remove(registration.BodyHandle);

        world.Simulation.Shapes.Remove(registration.ShapeIndex);

        if (TryComp(uid, out PhysicsBody3DComponent? body))
        {
            body.BackendHandle = -1;
            body.BackendStatic = false;
        }
    }

    private void SynchronizeDynamicBodies()
    {
        foreach (var (uid, registration) in _registrations)
        {
            if (registration.IsStatic ||
                !TryComp(uid, out PhysicsBody3DComponent? body) ||
                !_worlds.TryGetValue(registration.MapId, out var world))
            {
                continue;
            }

            var reference = world.Simulation.Bodies[registration.BodyHandle];
            var rotation = RemoveShapeRotation(reference.Pose.Orientation, registration.ShapeRotation);
            var position = reference.Pose.Position - rotation.Rotate(registration.ShapeOffset);
            var linearVelocity = reference.Velocity.Linear;
            var angularVelocity = reference.Velocity.Angular;

            _transform3D.SetWorldPosition3D(uid, position);
            _transform3D.SetWorldRotation3D(uid, rotation);

            if (!body.LinearVelocity.Equals(linearVelocity) || !body.AngularVelocity.Equals(angularVelocity))
            {
                body.LinearVelocity = linearVelocity;
                body.AngularVelocity = angularVelocity;
                Dirty(uid, body);
            }
        }
    }

    private static Quaternion RemoveShapeRotation(Quaternion physicsRotation, Quaternion shapeRotation)
    {
        return SpatialMath.Normalize(Quaternion.Multiply(
            physicsRotation,
            Quaternion.Inverse(SpatialMath.Normalize(shapeRotation))));
    }

    private static bool IsPositive(Vector3 value)
    {
        return SpatialMath.IsFinite(value) && value.X > 0f && value.Y > 0f && value.Z > 0f;
    }

    private static bool IsPositive(float first, float second)
    {
        return float.IsFinite(first) && float.IsFinite(second) && first > 0f && second > 0f;
    }

    private static Vector2 MoveTowards(Vector2 current, Vector2 target, float maximumDelta)
    {
        var delta = target - current;
        var distance = delta.Length();
        if (distance <= maximumDelta || distance < 1e-6f)
            return target;

        return current + delta / distance * maximumDelta;
    }

    private sealed class PhysicsWorld3D : IDisposable
    {
        private readonly BufferPool _pool = new();

        public readonly MapId MapId;
        public readonly Simulation Simulation;
        public readonly CollisionPropertiesRegistry CollisionProperties = new();

        public PhysicsWorld3D(MapId mapId, Vector3 gravity)
        {
            MapId = mapId;
            Simulation = Simulation.Create(
                _pool,
                new NarrowPhaseCallbacks3D(CollisionProperties),
                new PoseIntegratorCallbacks3D(gravity),
                new SolveDescription(8, 2));
            Simulation.Deterministic = true;
        }

        public void Dispose()
        {
            Simulation.Dispose();
            _pool.Clear();
        }
    }

    private sealed class BodyRegistration
    {
        public readonly EntityUid Entity;
        public readonly MapId MapId;
        public readonly bool IsStatic;
        public readonly BodyHandle BodyHandle;
        public readonly StaticHandle StaticHandle;
        public readonly TypedIndex ShapeIndex;
        public readonly uint CollidablePacked;
        public readonly Vector3 ShapeOffset;
        public readonly Quaternion ShapeRotation;

        private BodyRegistration(
            EntityUid entity,
            MapId mapId,
            bool isStatic,
            BodyHandle bodyHandle,
            StaticHandle staticHandle,
            TypedIndex shapeIndex,
            uint collidablePacked,
            Vector3 shapeOffset,
            Quaternion shapeRotation)
        {
            Entity = entity;
            MapId = mapId;
            IsStatic = isStatic;
            BodyHandle = bodyHandle;
            StaticHandle = staticHandle;
            ShapeIndex = shapeIndex;
            CollidablePacked = collidablePacked;
            ShapeOffset = shapeOffset;
            ShapeRotation = shapeRotation;
        }

        public static BodyRegistration ForBody(
            EntityUid entity,
            MapId mapId,
            BodyHandle handle,
            TypedIndex shapeIndex,
            uint collidablePacked,
            Vector3 shapeOffset,
            Quaternion shapeRotation)
        {
            return new BodyRegistration(
                entity,
                mapId,
                false,
                handle,
                default,
                shapeIndex,
                collidablePacked,
                shapeOffset,
                shapeRotation);
        }

        public static BodyRegistration ForStatic(
            EntityUid entity,
            MapId mapId,
            StaticHandle handle,
            TypedIndex shapeIndex,
            Vector3 shapeOffset,
            Quaternion shapeRotation)
        {
            var collidable = new CollidableReference(handle);
            return new BodyRegistration(
                entity,
                mapId,
                true,
                default,
                handle,
                shapeIndex,
                collidable.Packed,
                shapeOffset,
                shapeRotation);
        }
    }

    private readonly record struct CollisionProperties3D(
        EntityUid Entity,
        int Layer,
        int Mask,
        bool CanCollide,
        bool Sensor,
        float Friction,
        float Restitution);

    private sealed class CollisionPropertiesRegistry
    {
        private readonly Dictionary<uint, CollisionProperties3D> _properties = new();

        public void Add(
            uint collidable,
            EntityUid uid,
            PhysicsBody3DComponent body,
            CollisionShape3D shape)
        {
            _properties.Add(collidable, new CollisionProperties3D(
                uid,
                shape.CollisionLayer,
                shape.CollisionMask,
                body.CanCollide,
                shape.Sensor,
                MathF.Max(0f, shape.Friction),
                MathF.Max(0f, shape.Restitution)));
        }

        public bool TryGet(CollidableReference collidable, out CollisionProperties3D properties)
        {
            return _properties.TryGetValue(collidable.Packed, out properties);
        }

        public void Remove(uint collidable)
        {
            _properties.Remove(collidable);
        }
    }

    private struct RaycastHitHandler3D : IRayHitHandler
    {
        private readonly CollisionPropertiesRegistry _properties;
        private readonly int _collisionMask;
        private readonly EntityUid? _ignoredEntity;
        private readonly bool _includeSensors;

        public bool Found;
        public EntityUid Entity;
        public Vector3 Normal;
        public float Distance;
        public bool Sensor;

        public RaycastHitHandler3D(
            CollisionPropertiesRegistry properties,
            int collisionMask,
            EntityUid? ignoredEntity,
            bool includeSensors)
        {
            _properties = properties;
            _collisionMask = collisionMask;
            _ignoredEntity = ignoredEntity;
            _includeSensors = includeSensors;
            Found = false;
            Entity = default;
            Normal = default;
            Distance = float.MaxValue;
            Sensor = false;
        }

        public bool AllowTest(CollidableReference collidable)
        {
            return IsCandidate(collidable);
        }

        public bool AllowTest(CollidableReference collidable, int childIndex)
        {
            return IsCandidate(collidable);
        }

        public void OnRayHit(
            in RayData ray,
            ref float maximumT,
            float t,
            Vector3 normal,
            CollidableReference collidable,
            int childIndex)
        {
            if (!_properties.TryGet(collidable, out var properties) || t < 0f || t > maximumT)
                return;

            maximumT = t;
            Found = true;
            Entity = properties.Entity;
            Normal = normal;
            Distance = t;
            Sensor = properties.Sensor;
        }

        private bool IsCandidate(CollidableReference collidable)
        {
            return _properties.TryGet(collidable, out var properties) &&
                   properties.CanCollide &&
                   (_includeSensors || !properties.Sensor) &&
                   properties.Entity != _ignoredEntity &&
                   (properties.Layer & _collisionMask) != 0;
        }
    }

    private struct NarrowPhaseCallbacks3D : INarrowPhaseCallbacks
    {
        private readonly CollisionPropertiesRegistry _properties;

        public NarrowPhaseCallbacks3D(CollisionPropertiesRegistry properties)
        {
            _properties = properties;
        }

        public void Initialize(Simulation simulation)
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(
            int workerIndex,
            CollidableReference first,
            CollidableReference second,
            ref float speculativeMargin)
        {
            if (first.Mobility != CollidableMobility.Dynamic && second.Mobility != CollidableMobility.Dynamic)
                return false;

            return IsCollisionEnabled(first, second);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        {
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(
            int workerIndex,
            CollidablePair pair,
            ref TManifold manifold,
            out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            _properties.TryGet(pair.A, out var first);
            _properties.TryGet(pair.B, out var second);
            pairMaterial = new PairMaterialProperties(
                MathF.Sqrt(first.Friction * second.Friction),
                3f,
                new SpringSettings(30f, 1f));
            return !first.Sensor && !second.Sensor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold(
            int workerIndex,
            CollidablePair pair,
            int childIndexA,
            int childIndexB,
            ref ConvexContactManifold manifold)
        {
            return true;
        }

        public void Dispose()
        {
        }

        private bool IsCollisionEnabled(CollidableReference first, CollidableReference second)
        {
            if (!_properties.TryGet(first, out var firstProperties) ||
                !_properties.TryGet(second, out var secondProperties))
            {
                return false;
            }

            return firstProperties.CanCollide &&
                   secondProperties.CanCollide &&
                   (firstProperties.Layer & secondProperties.Mask) != 0 &&
                   (secondProperties.Layer & firstProperties.Mask) != 0;
        }
    }

    private struct PoseIntegratorCallbacks3D : IPoseIntegratorCallbacks
    {
        private Vector3Wide _gravityWideDt;

        public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public readonly bool AllowSubstepsForUnconstrainedBodies => false;
        public readonly bool IntegrateVelocityForKinematics => false;

        public Vector3 Gravity;

        public PoseIntegratorCallbacks3D(Vector3 gravity) : this()
        {
            Gravity = gravity;
        }

        public void Initialize(Simulation simulation)
        {
        }

        public void PrepareForIntegration(float dt)
        {
            _gravityWideDt = Vector3Wide.Broadcast(Gravity * dt);
        }

        public void IntegrateVelocity(
            Vector<int> bodyIndices,
            Vector3Wide position,
            QuaternionWide orientation,
            BodyInertiaWide localInertia,
            Vector<int> integrationMask,
            int workerIndex,
            Vector<float> dt,
            ref BodyVelocityWide velocity)
        {
            velocity.Linear += _gravityWideDt;
        }
    }
}
