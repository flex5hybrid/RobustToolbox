using System;
using System.Numerics;
using NUnit.Framework;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.GameObjects.Systems;

[TestFixture]
[Parallelizable]
public sealed class Transform3DSystemTests
{
    [Test]
    public void AuthoritativeHierarchyComposesPositionAndRotation()
    {
        var simulation = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = simulation.Resolve<IEntityManager>();
        var transforms = entities.System<SharedTransformSystem>();
        var transforms3D = entities.System<SharedTransform3DSystem>();
        var mapId = simulation.CreateMap().MapId;

        var parent = entities.SpawnEntity(null, new MapCoordinates(new Vector2(10f, 20f), mapId));
        var child = entities.SpawnEntity(null, new MapCoordinates(new Vector2(11f, 20f), mapId));
        transforms.SetParent(child, parent);

        transforms3D.SetAuthoritative(parent, true);
        transforms3D.SetAuthoritative(child, true);
        transforms3D.SetLocalPosition3D(parent, new Vector3(10f, 20f, 5f));
        transforms3D.SetRotation3D(parent, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));
        transforms3D.SetLocalPosition3D(child, new Vector3(1f, 0f, 2f));

        var world = transforms3D.GetWorldPosition3D(child);
        Assert.That(world.X, Is.EqualTo(10f).Within(0.0001f));
        Assert.That(world.Y, Is.EqualTo(21f).Within(0.0001f));
        Assert.That(world.Z, Is.EqualTo(7f).Within(0.0001f));
    }

    [Test]
    public void MapAndEntityCoordinatesRoundTripWithoutDroppingZ()
    {
        var simulation = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = simulation.Resolve<IEntityManager>();
        var transforms3D = entities.System<SharedTransform3DSystem>();
        var mapId = simulation.CreateMap().MapId;
        var parent = entities.SpawnEntity(null, new MapCoordinates(new Vector2(4f, -2f), mapId));

        transforms3D.SetAuthoritative(parent, true);
        transforms3D.SetLocalPosition3D(parent, new Vector3(4f, -2f, 3f));

        var local = new EntityCoordinates3D(parent, new Vector3(1f, 2f, 4f));
        var map = transforms3D.ToMapCoordinates(local);
        var roundTrip = transforms3D.ToCoordinates(parent, map);

        Assert.That(map.Position.X, Is.EqualTo(5f).Within(0.0001f));
        Assert.That(map.Position.Y, Is.EqualTo(0f).Within(0.0001f));
        Assert.That(map.Position.Z, Is.EqualTo(7f).Within(0.0001f));
        Assert.That(roundTrip.Position.X, Is.EqualTo(local.Position.X).Within(0.0001f));
        Assert.That(roundTrip.Position.Y, Is.EqualTo(local.Position.Y).Within(0.0001f));
        Assert.That(roundTrip.Position.Z, Is.EqualTo(local.Position.Z).Within(0.0001f));
    }
}
