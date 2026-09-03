using System.Numerics;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Utility;

namespace Robust.Shared.Physics3D;

public sealed partial class SharedPhysics3DSystem
{
    private void OnLegacyStartCollide3D(
        Entity<LegacyPhysics3DBridgeComponent> entity,
        ref StartCollide3DEvent args)
    {
        if (!entity.Comp.RaiseLegacyEvents ||
            !TryResolveLegacyContact(entity.Owner, args.OtherEntity, args.OurShape, args.OtherShape,
                out var ourFixtureId, out var otherFixtureId,
                out var ourFixture, out var otherFixture,
                out var ourBody, out var otherBody))
            return;

        var points = new FixedArray2<Vector2>(new Vector2(args.Position.X, args.Position.Y), default);
        var normal = new Vector2(args.Normal.X, args.Normal.Y);
        if (normal.LengthSquared() > 1e-8f)
            normal = Vector2.Normalize(normal);
        var legacy = new StartCollideEvent(
            entity.Owner,
            args.OtherEntity,
            ourFixtureId,
            otherFixtureId,
            ourFixture,
            otherFixture,
            ourBody,
            otherBody,
            points,
            1,
            normal);
        RaiseLocalEvent(entity.Owner, ref legacy, true);
    }

    private void OnLegacyEndCollide3D(
        Entity<LegacyPhysics3DBridgeComponent> entity,
        ref EndCollide3DEvent args)
    {
        if (!entity.Comp.RaiseLegacyEvents ||
            !TryResolveLegacyContact(entity.Owner, args.OtherEntity, args.OurShape, args.OtherShape,
                out var ourFixtureId, out var otherFixtureId,
                out var ourFixture, out var otherFixture,
                out var ourBody, out var otherBody))
            return;

        var legacy = new EndCollideEvent(
            entity.Owner,
            args.OtherEntity,
            ourFixtureId,
            otherFixtureId,
            ourFixture,
            otherFixture,
            ourBody,
            otherBody);
        RaiseLocalEvent(entity.Owner, ref legacy, true);
    }

    private bool TryResolveLegacyContact(
        EntityUid ours,
        EntityUid other,
        int ourShape,
        int otherShape,
        out string ourFixtureId,
        out string otherFixtureId,
        out Fixture ourFixture,
        out Fixture otherFixture,
        out PhysicsComponent ourBody,
        out PhysicsComponent otherBody)
    {
        ourFixtureId = string.Empty;
        otherFixtureId = string.Empty;
        ourFixture = null!;
        otherFixture = null!;
        ourBody = null!;
        otherBody = null!;
        if (!TryComp(ours, out LegacyPhysics3DBridgeComponent? ourBridge) ||
            !TryComp(other, out LegacyPhysics3DBridgeComponent? otherBridge) ||
            !TryComp(ours, out FixturesComponent? ourFixtures) ||
            !TryComp(other, out FixturesComponent? otherFixtures) ||
            !TryComp(ours, out ourBody) ||
            !TryComp(other, out otherBody) ||
            !TryGetFixture(ourBridge, ourFixtures, ourShape, out ourFixtureId, out ourFixture) ||
            !TryGetFixture(otherBridge, otherFixtures, otherShape, out otherFixtureId, out otherFixture))
            return false;
        return true;
    }

    private static bool TryGetFixture(
        LegacyPhysics3DBridgeComponent bridge,
        FixturesComponent fixtures,
        int shape,
        out string fixtureId,
        out Fixture fixture)
    {
        fixtureId = string.Empty;
        fixture = null!;
        if ((uint) shape < (uint) bridge.ShapeFixtureIds.Count)
            fixtureId = bridge.ShapeFixtureIds[shape];
        if (!string.IsNullOrEmpty(fixtureId) && fixtures.Fixtures.TryGetValue(fixtureId, out fixture!))
            return true;
        foreach (var pair in fixtures.Fixtures)
        {
            fixtureId = pair.Key;
            fixture = pair.Value;
            return true;
        }
        return false;
    }
}
