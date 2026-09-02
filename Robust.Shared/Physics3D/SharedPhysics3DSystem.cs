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
            {
                world.Contacts.BeginStep();
                world.Simulation.Timestep(FixedTimeStep);
                world.Contacts.EndStep();
            }

            _accumulator -= FixedTimeStep;
            steps++;
        }

        if (steps > 0)
        {
            SynchronizeDynamicBodies();
            DispatchContactEvents();
        }
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

    public bool SweepBox(
        MapId mapId,
        Vector3 size,
        Vector3 position,
        Quaternion rotation,
        Vector3 displacement,
        int collisionMask,
        EntityUid? ignoredEntity,
        bool includeSensors,
        out PhysicsSweepHit3D hit)
    {
        hit = default;
        if (!IsPositive(size))
            return false;

        return SweepConvex(
            mapId,
            new Box(size.X, size.Y, size.Z),
            position,
            rotation,
            displacement,
            collisionMask,
            ignoredEntity,
            includeSensors,
            out hit);
    }

    public bool SweepSphere(
        MapId mapId,
        float radius,
        Vector3 position,
        Vector3 displacement,
        int collisionMask,
        EntityUid? ignoredEntity,
        bool includeSensors,
        out PhysicsSweepHit3D hit)
    {
        hit = default;
        if (!float.IsFinite(radius) || radius <= 0f)
            return false;

        return SweepConvex(
            mapId,
            new Sphere(radius),
            position,
            Quaternion.Identity,
            displacement,
            collisionMask,
            ignoredEntity,
            includeSensors,
            out hit);
    }

    public bool SweepCapsule(
        MapId mapId,
        float radius,
        float length,
        Vector3 position,
        Quaternion rotation,
        Vector3 displacement,
        int collisionMask,
        EntityUid? ignoredEntity,
        bool includeSensors,
        out PhysicsSweepHit3D hit)
    {
        hit = default;
        if (!IsPositive(radius, length))
            return false;

        return SweepConvex(
            mapId,
            new Capsule(radius, length),
            position,
            rotation,
            displacement,
            collisionMask,
            ignoredEntity,
            includeSensors,
            out hit);
    }

    /// <summary>
    /// Returns broad-phase candidates intersecting an axis-aligned world volume. This is intentionally named
    /// as an AABB query: callers requiring contact-level precision should use a convex sweep.
    /// </summary>
    public int GetAabbOverlaps(
        MapId mapId,
        Box3 bounds,
        int collisionMask,
        EntityUid? ignoredEntity,
        bool includeSensors,
        List<PhysicsOverlap3D> results)
    {
        if (!_worlds.TryGetValue(mapId, out var world) || !bounds.IsValid)
            return 0;

        var start = results.Count;
        var handler = new OverlapHandler3D(
            world,
            collisionMask,
            ignoredEntity,
            includeSensors,
            results);
        world.Simulation.BroadPhase.GetOverlaps(bounds.Min, bounds.Max, ref handler);
        return results.Count - start;
    }

    private bool SweepConvex<TShape>(
        MapId mapId,
        TShape shape,
        Vector3 position,
        Quaternion rotation,
        Vector3 displacement,
        int collisionMask,
        EntityUid? ignoredEntity,
        bool includeSensors,
        out PhysicsSweepHit3D hit)
        where TShape : unmanaged, IConvexShape
    {
        hit = default;
        if (!SpatialMath.IsFinite(position) ||
            !SpatialMath.IsFinite(rotation) ||
            !SpatialMath.IsFinite(displacement) ||
            rotation.LengthSquared() < 1e-8f ||
            displacement.LengthSquared() < 1e-12f ||
            !_worlds.TryGetValue(mapId, out var world))
        {
            return false;
        }

        var handler = new SweepHitHandler3D(
            world.CollisionProperties,
            collisionMask,
            ignoredEntity,
            includeSensors,
            position);
        world.Simulation.Sweep(
            shape,
            new RigidPose(position, SpatialMath.Normalize(rotation)),
            new BodyVelocity(displacement, Vector3.Zero),
            1f,
            world.Pool,
            ref handler);
        if (!handler.Found)
            return false;

        hit = new PhysicsSweepHit3D(
            handler.Entity,
            handler.Position,
            handler.Normal,
            handler.Time * displacement.Length(),
            handler.Sensor);
        return true;
    }

    public void RequestCharacterJump(EntityUid uid)
    {
        if (TryComp(uid, out CharacterController3DComponent? character))
            character.JumpRequested = true;
    }

    private void DispatchContactEvents()
    {
        foreach (var world in _worlds.Values)
        {
            foreach (var transition in world.Contacts.Transitions)
            {
                var contact = transition.Contact;
                var firstExists = EntityManager.EntityExists(contact.First);
                var secondExists = EntityManager.EntityExists(contact.Second);
                switch (transition.Kind)
                {
                    case ContactTransitionKind3D.Started:
                    {
                        var first = new StartCollide3DEvent(
                            contact.First,
                            contact.Second,
                            contact.Position,
                            contact.Normal,
                            contact.Penetration,
                            contact.Sensor);
                        var second = new StartCollide3DEvent(
                            contact.Second,
                            contact.First,
                            contact.Position,
                            -contact.Normal,
                            contact.Penetration,
                            contact.Sensor);
                        if (firstExists)
                            RaiseLocalEvent(contact.First, ref first);
                        if (secondExists)
                            RaiseLocalEvent(contact.Second, ref second);
                        break;
                    }
                    case ContactTransitionKind3D.Touching:
                    {
                        var first = new Collide3DEvent(
                            contact.First,
                            contact.Second,
                            contact.Position,
                            contact.Normal,
                            contact.Penetration,
                            contact.Sensor);
                        var second = new Collide3DEvent(
                            contact.Second,
                            contact.First,
                            contact.Position,
                            -contact.Normal,
                            contact.Penetration,
                            contact.Sensor);
                        if (firstExists)
                            RaiseLocalEvent(contact.First, ref first);
                        if (secondExists)
                            RaiseLocalEvent(contact.Second, ref second);
                        break;
                    }
                    case ContactTransitionKind3D.Ended:
                    {
                        var first = new EndCollide3DEvent(contact.First, contact.Second, contact.Sensor);
                        var second = new EndCollide3DEvent(contact.Second, contact.First, contact.Sensor);
                        if (firstExists)
                            RaiseLocalEvent(contact.First, ref first);
                        if (secondExists)
                            RaiseLocalEvent(contact.Second, ref second);
                        break;
                    }
                }
            }

            world.Contacts.Transitions.Clear();
        }
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

        _transform3D.SetAuthoritative(uid, true, transform);
        var world = GetOrCreateWorld(transform.MapID);
        var position = _transform3D.GetWorldPosition3D(uid, transform);
        var entityRotation = _transform3D.GetWorldRotation3D(uid, transform);

        if (collider.Shapes.Count > 1)
        {
            var compound = AddCompound(world, uid, body, collider, position, entityRotation);
            if (compound is null)
                return false;

            RegisterBody(uid, body, compound);
            return true;
        }

        var shapeDefinition = collider.Shapes[0];
        var shapeRotation = SpatialMath.Normalize(shapeDefinition.Rotation);
        var shapeOffset = shapeDefinition.Offset;
        if (!SpatialMath.IsFinite(shapeOffset) || !SpatialMath.IsFinite(shapeRotation))
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
            ConvexHullShape3D hull when IsValidHull(hull.Points) => AddConvexHull(
                world,
                uid,
                body,
                velocity,
                hull,
                position,
                entityRotation),
            TriangleMeshShape3D mesh when IsValidMesh(mesh) &&
                                               body.BodyType is PhysicsBodyType3D.Static or PhysicsBodyType3D.Kinematic => AddTriangleMesh(
                world,
                uid,
                body,
                velocity,
                mesh,
                pose,
                shapeOffset,
                shapeRotation),
            _ => null,
        };

        if (registration is null)
            return false;

        RegisterBody(uid, body, registration);
        return true;
    }

    private void RegisterBody(EntityUid uid, PhysicsBody3DComponent body, BodyRegistration registration)
    {
        _registrations.Add(uid, registration);
        body.BackendHandle = registration.IsStatic
            ? registration.StaticHandle.Value
            : registration.BodyHandle.Value;
        body.BackendStatic = registration.IsStatic;
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
        var shapeIndex = world.Simulation.Shapes.Add(shape);
        var inertia = shape.ComputeInertia(MathF.Max(0.001f, body.Mass));
        return AddCachedShape(
            world,
            uid,
            body,
            pose,
            velocity,
            shapeIndex,
            inertia,
            new[] { shapeDefinition },
            shapeOffset,
            shapeRotation);
    }

    private BodyRegistration? AddConvexHull(
        PhysicsWorld3D world,
        EntityUid uid,
        PhysicsBody3DComponent body,
        BodyVelocity velocity,
        ConvexHullShape3D definition,
        Vector3 entityPosition,
        Quaternion entityRotation)
    {
        var points = definition.Points.ToArray();
        var shape = new ConvexHull(points.AsSpan(), world.Pool, out var center);
        var shapeRotation = SpatialMath.Normalize(definition.Rotation);
        var effectiveOffset = definition.Offset + shapeRotation.Rotate(center);
        var pose = new RigidPose(
            entityPosition + entityRotation.Rotate(effectiveOffset),
            SpatialMath.Compose(shapeRotation, entityRotation));
        return AddConvex(
            world,
            uid,
            body,
            pose,
            velocity,
            definition,
            effectiveOffset,
            shapeRotation,
            shape);
    }

    private BodyRegistration? AddTriangleMesh(
        PhysicsWorld3D world,
        EntityUid uid,
        PhysicsBody3DComponent body,
        BodyVelocity velocity,
        TriangleMeshShape3D definition,
        RigidPose pose,
        Vector3 shapeOffset,
        Quaternion shapeRotation)
    {
        var triangleCount = definition.Indices.Count / 3;
        world.Pool.Take<Triangle>(triangleCount, out var triangles);
        for (var i = 0; i < triangleCount; i++)
        {
            triangles[i] = new Triangle(
                definition.Vertices[definition.Indices[i * 3]],
                definition.Vertices[definition.Indices[i * 3 + 1]],
                definition.Vertices[definition.Indices[i * 3 + 2]]);
        }

        var shape = new Mesh(triangles, Vector3.One, world.Pool);
        var shapeIndex = world.Simulation.Shapes.Add(shape);
        var inertia = body.BodyType is PhysicsBodyType3D.Dynamic or PhysicsBodyType3D.Character
            ? shape.ComputeOpenInertia(MathF.Max(0.001f, body.Mass))
            : default;
        return AddCachedShape(
            world,
            uid,
            body,
            pose,
            velocity,
            shapeIndex,
            inertia,
            new CollisionShape3D[] { definition },
            shapeOffset,
            shapeRotation);
    }

    private BodyRegistration? AddCompound(
        PhysicsWorld3D world,
        EntityUid uid,
        PhysicsBody3DComponent body,
        Collider3DComponent collider,
        Vector3 position,
        Quaternion rotation)
    {
        foreach (var shape in collider.Shapes)
        {
            if (!IsSupportedCompoundShape(shape))
                return null;
        }

        using var builder = new CompoundBuilder(world.Pool, world.Simulation.Shapes, collider.Shapes.Count);
        var childMass = MathF.Max(0.001f, body.Mass) / collider.Shapes.Count;
        foreach (var shape in collider.Shapes)
        {
            if (!TryAddCompoundChild(world, ref builder, shape, childMass, body.BodyType))
                return null;
        }

        BepuUtilities.Memory.Buffer<CompoundChild> children;
        BodyInertia inertia;
        if (body.BodyType is PhysicsBodyType3D.Dynamic or PhysicsBodyType3D.Character)
            builder.BuildDynamicCompound(out children, out inertia);
        else
        {
            builder.BuildKinematicCompound(out children);
            inertia = default;
        }

        var compound = new Compound(children);
        var shapeIndex = world.Simulation.Shapes.Add(compound);
        return AddCachedShape(
            world,
            uid,
            body,
            new RigidPose(position, rotation),
            new BodyVelocity(body.LinearVelocity, body.AngularVelocity),
            shapeIndex,
            inertia,
            collider.Shapes,
            Vector3.Zero,
            Quaternion.Identity);
    }

    private bool TryAddCompoundChild(
        PhysicsWorld3D world,
        ref CompoundBuilder builder,
        CollisionShape3D definition,
        float mass,
        PhysicsBodyType3D bodyType)
    {
        var rotation = SpatialMath.Normalize(definition.Rotation);
        var pose = new RigidPose(definition.Offset, rotation);
        var dynamic = bodyType is PhysicsBodyType3D.Dynamic or PhysicsBodyType3D.Character;

        switch (definition)
        {
            case BoxShape3D box:
                AddCompoundChild(ref builder, new Box(box.Size.X, box.Size.Y, box.Size.Z), pose, mass, dynamic);
                return true;
            case SphereShape3D sphere:
                AddCompoundChild(ref builder, new Sphere(sphere.Radius), pose, mass, dynamic);
                return true;
            case CapsuleShape3D capsule:
                AddCompoundChild(ref builder, new Capsule(capsule.Radius, capsule.Length), pose, mass, dynamic);
                return true;
            case CylinderShape3D cylinder:
                AddCompoundChild(ref builder, new Cylinder(cylinder.Radius, cylinder.Length), pose, mass, dynamic);
                return true;
            case ConvexHullShape3D hull:
            {
                var points = hull.Points.ToArray();
                var shape = new ConvexHull(points.AsSpan(), world.Pool, out var center);
                pose.Position += rotation.Rotate(center);
                AddCompoundChild(ref builder, shape, pose, mass, dynamic);
                return true;
            }
            default:
                return false;
        }
    }

    private static void AddCompoundChild<TShape>(
        ref CompoundBuilder builder,
        TShape shape,
        RigidPose pose,
        float mass,
        bool dynamic)
        where TShape : unmanaged, IConvexShape
    {
        if (dynamic)
            builder.Add(shape, pose, mass);
        else
            builder.AddForKinematic(shape, pose, 1f);
    }

    private BodyRegistration AddCachedShape(
        PhysicsWorld3D world,
        EntityUid uid,
        PhysicsBody3DComponent body,
        RigidPose pose,
        BodyVelocity velocity,
        TypedIndex shapeIndex,
        BodyInertia inertia,
        IReadOnlyList<CollisionShape3D> shapeDefinitions,
        Vector3 shapeOffset,
        Quaternion shapeRotation)
    {
        if (body.BodyType == PhysicsBodyType3D.Static)
        {
            var handle = world.Simulation.Statics.Add(new StaticDescription(pose, shapeIndex));
            var registration = BodyRegistration.ForStatic(
                uid,
                world.MapId,
                handle,
                shapeIndex,
                shapeOffset,
                shapeRotation);
            world.CollisionProperties.Add(registration.CollidablePacked, uid, body, shapeDefinitions);
            return registration;
        }

        var collidable = new CollidableDescription(
            shapeIndex,
            0f,
            GetMaximumSpeculativeMargin(body.ContinuousDetection),
            GetContinuousDetection(body.ContinuousDetection));
        var activity = new BodyActivityDescription(body.SleepingAllowed ? 0.01f : -1f);
        BodyDescription description;
        if (body.BodyType == PhysicsBodyType3D.Kinematic)
            description = BodyDescription.CreateKinematic(pose, velocity, collidable, activity);
        else
        {
            description = BodyDescription.CreateDynamic(pose, velocity, inertia, collidable, activity);
            if (body.BodyType == PhysicsBodyType3D.Character)
                description.LocalInertia.InverseInertiaTensor = default;
        }

        var bodyHandle = world.Simulation.Bodies.Add(description);
        var collidablePacked = world.Simulation.Bodies[bodyHandle].CollidableReference.Packed;
        var registration = BodyRegistration.ForBody(
            uid,
            world.MapId,
            bodyHandle,
            shapeIndex,
            collidablePacked,
            shapeOffset,
            shapeRotation);
        world.CollisionProperties.Add(registration.CollidablePacked, uid, body, shapeDefinitions);
        world.Dynamics.Add(bodyHandle, body);
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
        {
            world.Dynamics.Remove(registration.BodyHandle);
            world.Simulation.Bodies.Remove(registration.BodyHandle);
        }

        world.Simulation.Shapes.RecursivelyRemoveAndDispose(registration.ShapeIndex, world.Pool);

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

    private static bool IsValidHull(IReadOnlyList<Vector3> points)
    {
        if (points.Count < 4)
            return false;

        foreach (var point in points)
        {
            if (!SpatialMath.IsFinite(point))
                return false;
        }

        var origin = points[0];
        var line = Vector3.Zero;
        foreach (var point in points)
        {
            var candidate = point - origin;
            if (candidate.LengthSquared() > line.LengthSquared())
                line = candidate;
        }

        if (line.LengthSquared() < 1e-10f)
            return false;

        var planeNormal = Vector3.Zero;
        foreach (var point in points)
        {
            var candidate = Vector3.Cross(line, point - origin);
            if (candidate.LengthSquared() > planeNormal.LengthSquared())
                planeNormal = candidate;
        }

        if (planeNormal.LengthSquared() < 1e-10f)
            return false;

        foreach (var point in points)
        {
            if (MathF.Abs(Vector3.Dot(planeNormal, point - origin)) > 1e-6f)
                return true;
        }

        return false;
    }

    private static bool IsValidMesh(TriangleMeshShape3D mesh)
    {
        if (mesh.Vertices.Count < 3 || mesh.Indices.Count < 3 || mesh.Indices.Count % 3 != 0)
            return false;

        foreach (var vertex in mesh.Vertices)
        {
            if (!SpatialMath.IsFinite(vertex))
                return false;
        }

        foreach (var index in mesh.Indices)
        {
            if (index < 0 || index >= mesh.Vertices.Count)
                return false;
        }

        for (var i = 0; i < mesh.Indices.Count; i += 3)
        {
            var a = mesh.Vertices[mesh.Indices[i]];
            var b = mesh.Vertices[mesh.Indices[i + 1]];
            var c = mesh.Vertices[mesh.Indices[i + 2]];
            if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-12f)
                return false;
        }

        return true;
    }

    private static bool IsSupportedCompoundShape(CollisionShape3D shape)
    {
        if (!SpatialMath.IsFinite(shape.Offset) ||
            !SpatialMath.IsFinite(shape.Rotation) ||
            shape.Rotation.LengthSquared() < 1e-8f)
        {
            return false;
        }

        return shape switch
        {
            BoxShape3D box => IsPositive(box.Size),
            SphereShape3D sphere => float.IsFinite(sphere.Radius) && sphere.Radius > 0f,
            CapsuleShape3D capsule => IsPositive(capsule.Radius, capsule.Length),
            CylinderShape3D cylinder => IsPositive(cylinder.Radius, cylinder.Length),
            ConvexHullShape3D hull => IsValidHull(hull.Points),
            _ => false,
        };
    }

    private static ContinuousDetection GetContinuousDetection(ContinuousDetectionMode3D mode)
    {
        return mode switch
        {
            ContinuousDetectionMode3D.Passive => ContinuousDetection.Passive,
            ContinuousDetectionMode3D.Continuous => ContinuousDetection.Continuous(),
            _ => ContinuousDetection.Discrete,
        };
    }

    private static float GetMaximumSpeculativeMargin(ContinuousDetectionMode3D mode)
    {
        return mode == ContinuousDetectionMode3D.Continuous ? 0.1f : float.MaxValue;
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
        public readonly BufferPool Pool = new();

        public readonly MapId MapId;
        public readonly Simulation Simulation;
        public readonly CollisionPropertiesRegistry CollisionProperties = new();
        public readonly BodyDynamicsRegistry Dynamics = new();
        public readonly ContactTracker3D Contacts;

        public PhysicsWorld3D(MapId mapId, Vector3 gravity)
        {
            MapId = mapId;
            Contacts = new ContactTracker3D(CollisionProperties);
            Simulation = Simulation.Create(
                Pool,
                new NarrowPhaseCallbacks3D(CollisionProperties, Contacts),
                new PoseIntegratorCallbacks3D(gravity, Dynamics),
                new SolveDescription(8, 2));
            Simulation.Deterministic = true;
        }

        public void Dispose()
        {
            Simulation.Dispose();
            Pool.Clear();
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

    private readonly record struct ShapeCollisionProperties3D(
        int Layer,
        int Mask,
        bool Sensor,
        float Friction,
        float Restitution);

    private sealed class BodyCollisionProperties3D
    {
        public readonly EntityUid Entity;
        public readonly bool CanCollide;
        public readonly ShapeCollisionProperties3D[] Shapes;
        public readonly int CombinedLayer;
        public readonly int CombinedMask;
        public readonly bool AllSensors;
        public readonly float Friction;
        public readonly float Restitution;

        public BodyCollisionProperties3D(
            EntityUid entity,
            bool canCollide,
            IReadOnlyList<CollisionShape3D> shapes)
        {
            Entity = entity;
            CanCollide = canCollide;
            Shapes = new ShapeCollisionProperties3D[shapes.Count];
            AllSensors = true;
            var friction = 0f;
            var restitution = 0f;
            for (var i = 0; i < shapes.Count; i++)
            {
                var shape = shapes[i];
                Shapes[i] = new ShapeCollisionProperties3D(
                    shape.CollisionLayer,
                    shape.CollisionMask,
                    shape.Sensor,
                    MathF.Max(0f, shape.Friction),
                    MathF.Max(0f, shape.Restitution));
                CombinedLayer |= shape.CollisionLayer;
                CombinedMask |= shape.CollisionMask;
                AllSensors &= shape.Sensor;
                friction += MathF.Max(0f, shape.Friction);
                restitution += MathF.Max(0f, shape.Restitution);
            }

            Friction = friction / shapes.Count;
            Restitution = restitution / shapes.Count;
        }

        public ShapeCollisionProperties3D GetShape(int childIndex)
        {
            if (Shapes.Length == 1 || childIndex < 0 || childIndex >= Shapes.Length)
                return Shapes[0];

            return Shapes[childIndex];
        }
    }

    private sealed class CollisionPropertiesRegistry
    {
        private readonly Dictionary<uint, BodyCollisionProperties3D> _properties = new();

        public void Add(
            uint collidable,
            EntityUid uid,
            PhysicsBody3DComponent body,
            IReadOnlyList<CollisionShape3D> shapes)
        {
            _properties.Add(collidable, new BodyCollisionProperties3D(uid, body.CanCollide, shapes));
        }

        public bool TryGet(CollidableReference collidable, out BodyCollisionProperties3D properties)
        {
            return _properties.TryGetValue(collidable.Packed, out properties!);
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
            return IsCandidate(collidable, childIndex);
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
            Sensor = properties.GetShape(childIndex).Sensor;
        }

        private bool IsCandidate(CollidableReference collidable, int childIndex = -1)
        {
            if (!_properties.TryGet(collidable, out var properties) ||
                !properties.CanCollide ||
                properties.Entity == _ignoredEntity)
            {
                return false;
            }

            if (childIndex < 0)
            {
                return (_includeSensors || !properties.AllSensors) &&
                       (properties.CombinedLayer & _collisionMask) != 0;
            }

            var shape = properties.GetShape(childIndex);
            return (_includeSensors || !shape.Sensor) && (shape.Layer & _collisionMask) != 0;
        }
    }

    private struct SweepHitHandler3D : ISweepHitHandler
    {
        private readonly CollisionPropertiesRegistry _properties;
        private readonly int _collisionMask;
        private readonly EntityUid? _ignoredEntity;
        private readonly bool _includeSensors;
        private readonly Vector3 _start;

        public bool Found;
        public EntityUid Entity;
        public Vector3 Position;
        public Vector3 Normal;
        public float Time;
        public bool Sensor;

        public SweepHitHandler3D(
            CollisionPropertiesRegistry properties,
            int collisionMask,
            EntityUid? ignoredEntity,
            bool includeSensors,
            Vector3 start)
        {
            _properties = properties;
            _collisionMask = collisionMask;
            _ignoredEntity = ignoredEntity;
            _includeSensors = includeSensors;
            _start = start;
            Found = false;
            Entity = default;
            Position = default;
            Normal = default;
            Time = float.MaxValue;
            Sensor = false;
        }

        public bool AllowTest(CollidableReference collidable)
        {
            return IsCandidate(collidable, -1, out _);
        }

        public bool AllowTest(CollidableReference collidable, int child)
        {
            return IsCandidate(collidable, child, out _);
        }

        public void OnHit(
            ref float maximumT,
            float t,
            in Vector3 hitLocation,
            in Vector3 hitNormal,
            CollidableReference collidable)
        {
            if (t < 0f || t > Time || !IsCandidate(collidable, -1, out var sensor))
                return;

            maximumT = t;
            Time = t;
            Position = hitLocation;
            Normal = hitNormal;
            Sensor = sensor;
            Found = true;
            Entity = _properties.TryGet(collidable, out var properties) ? properties.Entity : default;
        }

        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
        {
            if (!IsCandidate(collidable, -1, out var sensor))
                return;

            maximumT = 0f;
            Time = 0f;
            Position = _start;
            Normal = Vector3.Zero;
            Sensor = sensor;
            Found = true;
            Entity = _properties.TryGet(collidable, out var properties) ? properties.Entity : default;
        }

        private bool IsCandidate(CollidableReference collidable, int child, out bool sensor)
        {
            sensor = false;
            if (!_properties.TryGet(collidable, out var properties) ||
                !properties.CanCollide ||
                properties.Entity == _ignoredEntity)
            {
                return false;
            }

            if (child >= 0)
            {
                var shape = properties.GetShape(child);
                sensor = shape.Sensor;
                return (_includeSensors || !sensor) && (shape.Layer & _collisionMask) != 0;
            }

            foreach (var shape in properties.Shapes)
            {
                if ((_includeSensors || !shape.Sensor) && (shape.Layer & _collisionMask) != 0)
                {
                    sensor = shape.Sensor;
                    return true;
                }
            }

            return false;
        }
    }

    private struct OverlapHandler3D : IBreakableForEach<CollidableReference>
    {
        private readonly PhysicsWorld3D _world;
        private readonly int _collisionMask;
        private readonly EntityUid? _ignoredEntity;
        private readonly bool _includeSensors;
        private readonly List<PhysicsOverlap3D> _results;

        public OverlapHandler3D(
            PhysicsWorld3D world,
            int collisionMask,
            EntityUid? ignoredEntity,
            bool includeSensors,
            List<PhysicsOverlap3D> results)
        {
            _world = world;
            _collisionMask = collisionMask;
            _ignoredEntity = ignoredEntity;
            _includeSensors = includeSensors;
            _results = results;
        }

        public bool LoopBody(CollidableReference collidable)
        {
            if (!_world.CollisionProperties.TryGet(collidable, out var properties) ||
                !properties.CanCollide ||
                properties.Entity == _ignoredEntity)
            {
                return true;
            }

            var found = false;
            var sensor = false;
            foreach (var shape in properties.Shapes)
            {
                if ((_includeSensors || !shape.Sensor) && (shape.Layer & _collisionMask) != 0)
                {
                    found = true;
                    sensor = shape.Sensor;
                    break;
                }
            }

            if (!found)
                return true;

            var bounds = collidable.Mobility == CollidableMobility.Static
                ? _world.Simulation.Statics[collidable.StaticHandle].BoundingBox
                : _world.Simulation.Bodies[collidable.BodyHandle].BoundingBox;
            _results.Add(new PhysicsOverlap3D(
                properties.Entity,
                new Box3(bounds.Min, bounds.Max),
                sensor));
            return true;
        }
    }

    private enum ContactTransitionKind3D : byte
    {
        Started,
        Touching,
        Ended,
    }

    private readonly record struct ContactPairKey3D(uint First, uint Second);
    private readonly record struct ContactTransition3D(ContactTransitionKind3D Kind, PhysicsContact3D Contact);

    private sealed class ContactTracker3D
    {
        private readonly CollisionPropertiesRegistry _properties;
        private readonly Dictionary<ContactPairKey3D, PhysicsContact3D> _active = new();
        private readonly Dictionary<ContactPairKey3D, PhysicsContact3D> _observed = new();
        private Simulation? _simulation;

        public readonly List<ContactTransition3D> Transitions = new();

        public ContactTracker3D(CollisionPropertiesRegistry properties)
        {
            _properties = properties;
        }

        public void Initialize(Simulation simulation)
        {
            _simulation = simulation;
        }

        public void BeginStep()
        {
            _observed.Clear();
        }

        public void EndStep()
        {
            foreach (var (key, contact) in _observed)
            {
                if (!_active.ContainsKey(key))
                    Transitions.Add(new ContactTransition3D(ContactTransitionKind3D.Started, contact));

                Transitions.Add(new ContactTransition3D(ContactTransitionKind3D.Touching, contact));
            }

            foreach (var (key, contact) in _active)
            {
                if (!_observed.ContainsKey(key))
                    Transitions.Add(new ContactTransition3D(ContactTransitionKind3D.Ended, contact));
            }

            _active.Clear();
            foreach (var (key, contact) in _observed)
                _active.Add(key, contact);
        }

        public void Record<TManifold>(
            CollidablePair pair,
            ref TManifold manifold,
            int childIndexA,
            int childIndexB)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            if (_simulation is null ||
                manifold.Count == 0 ||
                !_properties.TryGet(pair.A, out var firstProperties) ||
                !_properties.TryGet(pair.B, out var secondProperties))
            {
                return;
            }

            var bestIndex = -1;
            var bestDepth = float.NegativeInfinity;
            for (var i = 0; i < manifold.Count; i++)
            {
                var depth = manifold.GetDepth(ref manifold, i);
                if (float.IsFinite(depth) && depth >= 0f && depth > bestDepth)
                {
                    bestDepth = depth;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                return;

            var offset = manifold.GetOffset(ref manifold, bestIndex);
            var normal = manifold.GetNormal(ref manifold, bestIndex);
            if (!SpatialMath.IsFinite(offset) || !SpatialMath.IsFinite(normal))
                return;

            var firstPose = pair.A.Mobility == CollidableMobility.Static
                ? _simulation.Statics[pair.A.StaticHandle].Pose
                : _simulation.Bodies[pair.A.BodyHandle].Pose;
            var sensor = firstProperties.GetShape(childIndexA).Sensor ||
                         secondProperties.GetShape(childIndexB).Sensor;
            var contact = new PhysicsContact3D(
                firstProperties.Entity,
                secondProperties.Entity,
                firstPose.Position + offset,
                normal,
                bestDepth,
                sensor);

            var firstPacked = pair.A.Packed;
            var secondPacked = pair.B.Packed;
            ContactPairKey3D key;
            if (firstPacked <= secondPacked)
                key = new ContactPairKey3D(firstPacked, secondPacked);
            else
            {
                key = new ContactPairKey3D(secondPacked, firstPacked);
                contact = new PhysicsContact3D(
                    contact.Second,
                    contact.First,
                    contact.Position,
                    -contact.Normal,
                    contact.Penetration,
                    contact.Sensor);
            }

            if (!_observed.TryGetValue(key, out var previous) || contact.Penetration > previous.Penetration)
                _observed[key] = contact;
        }
    }

    private struct NarrowPhaseCallbacks3D : INarrowPhaseCallbacks
    {
        private readonly CollisionPropertiesRegistry _properties;
        private readonly ContactTracker3D _contacts;

        public NarrowPhaseCallbacks3D(CollisionPropertiesRegistry properties, ContactTracker3D contacts)
        {
            _properties = properties;
            _contacts = contacts;
        }

        public void Initialize(Simulation simulation)
        {
            _contacts.Initialize(simulation);
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
            if (!_properties.TryGet(pair.A, out var first) || !_properties.TryGet(pair.B, out var second))
                return false;

            return IsCollisionEnabled(first.GetShape(childIndexA), second.GetShape(childIndexB));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(
            int workerIndex,
            CollidablePair pair,
            ref TManifold manifold,
            out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            if (!_properties.TryGet(pair.A, out var first) || !_properties.TryGet(pair.B, out var second))
            {
                pairMaterial = default;
                return false;
            }

            // Bepu v2 models bounce through contact spring recovery rather than a classical restitution
            // impulse. Preserve the engine-facing coefficient by mapping it onto damping and recovery.
            var restitution = Math.Clamp(MathF.Max(first.Restitution, second.Restitution), 0f, 1f);
            pairMaterial = new PairMaterialProperties(
                MathF.Sqrt(first.Friction * second.Friction),
                restitution > 0f ? float.MaxValue : 3f,
                new SpringSettings(30f, 1f - restitution));
            _contacts.Record(pair, ref manifold, -1, -1);
            return !first.AllSensors && !second.AllSensors;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold(
            int workerIndex,
            CollidablePair pair,
            int childIndexA,
            int childIndexB,
            ref ConvexContactManifold manifold)
        {
            if (!_properties.TryGet(pair.A, out var first) || !_properties.TryGet(pair.B, out var second))
                return false;

            var firstShape = first.GetShape(childIndexA);
            var secondShape = second.GetShape(childIndexB);
            _contacts.Record(pair, ref manifold, childIndexA, childIndexB);
            return !firstShape.Sensor && !secondShape.Sensor && IsCollisionEnabled(firstShape, secondShape);
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
                   (firstProperties.CombinedLayer & secondProperties.CombinedMask) != 0 &&
                   (secondProperties.CombinedLayer & firstProperties.CombinedMask) != 0;
        }

        private static bool IsCollisionEnabled(
            ShapeCollisionProperties3D first,
            ShapeCollisionProperties3D second)
        {
            return (first.Layer & second.Mask) != 0 && (second.Layer & first.Mask) != 0;
        }
    }

    private readonly record struct BodyDynamics3D(
        float GravityScale,
        float LinearDamping,
        float AngularDamping);

    private sealed class BodyDynamicsRegistry
    {
        private readonly Dictionary<int, BodyDynamics3D> _properties = new();

        public void Add(BodyHandle handle, PhysicsBody3DComponent body)
        {
            _properties[handle.Value] = new BodyDynamics3D(
                float.IsFinite(body.GravityScale) ? body.GravityScale : 1f,
                Math.Clamp(body.LinearDamping, 0f, 1f),
                Math.Clamp(body.AngularDamping, 0f, 1f));
        }

        public bool TryGet(BodyHandle handle, out BodyDynamics3D properties)
        {
            return _properties.TryGetValue(handle.Value, out properties);
        }

        public void Remove(BodyHandle handle)
        {
            _properties.Remove(handle.Value);
        }
    }

    private struct PoseIntegratorCallbacks3D : IPoseIntegratorCallbacks
    {
        private readonly BodyDynamicsRegistry _dynamics;
        private Simulation? _simulation;

        public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public readonly bool AllowSubstepsForUnconstrainedBodies => false;
        public readonly bool IntegrateVelocityForKinematics => false;

        public Vector3 Gravity;

        public PoseIntegratorCallbacks3D(Vector3 gravity, BodyDynamicsRegistry dynamics) : this()
        {
            Gravity = gravity;
            _dynamics = dynamics;
        }

        public void Initialize(Simulation simulation)
        {
            _simulation = simulation;
        }

        public void PrepareForIntegration(float dt)
        {
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
            if (_simulation is null)
                return;

            for (var lane = 0; lane < Vector<float>.Count; lane++)
            {
                if (integrationMask[lane] == 0)
                    continue;

                var bodyIndex = bodyIndices[lane];
                if (bodyIndex < 0 || bodyIndex >= _simulation.Bodies.ActiveSet.Count)
                    continue;

                var handle = _simulation.Bodies.ActiveSet.IndexToHandle[bodyIndex];
                if (!_dynamics.TryGet(handle, out var properties))
                    properties = new BodyDynamics3D(1f, 0f, 0f);

                Vector3Wide.ReadSlot(ref velocity.Linear, lane, out var linear);
                Vector3Wide.ReadSlot(ref velocity.Angular, lane, out var angular);
                var laneDt = dt[lane];
                linear += Gravity * properties.GravityScale * laneDt;
                linear *= MathF.Pow(1f - properties.LinearDamping, laneDt);
                angular *= MathF.Pow(1f - properties.AngularDamping, laneDt);
                Vector3Wide.WriteSlot(linear, lane, ref velocity.Linear);
                Vector3Wide.WriteSlot(angular, lane, ref velocity.Angular);
            }
        }
    }
}
