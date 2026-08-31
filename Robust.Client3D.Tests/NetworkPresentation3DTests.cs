using System.Numerics;
using NUnit.Framework;
using Robust.Client3D;
using Robust.Shared3D;

namespace Robust.Client3D.Tests;

[TestFixture]
public sealed class NetworkPresentation3DTests
{
    [Test]
    public void ReconciliationReplaysOnlyUnacknowledgedInput()
    {
        var spawn = DemoWorld3D.SpawnPosition;
        var predictor = new PredictedPlayer3D(spawn);
        var first = predictor.Step(Vector2.UnitY, false, 0.25f, AuthoritativeWorld3D.FixedDelta);
        predictor.Step(Vector2.UnitY, false, 0.5f, AuthoritativeWorld3D.FixedDelta);

        Assert.That(predictor.PendingInputCount, Is.EqualTo(2));

        predictor.Reconcile(new PlayerSnapshot3D
        {
            PlayerId = 1,
            PositionX = spawn.X,
            PositionY = spawn.Y,
            PositionZ = spawn.Z,
            VelocityX = 0f,
            VelocityY = 0f,
            VelocityZ = 0f,
            FacingYaw = 0.25f,
            Grounded = true,
            AcknowledgedInput = first.Sequence,
        });

        Assert.That(predictor.PendingInputCount, Is.EqualTo(1));
        Assert.That(predictor.Position.Y, Is.GreaterThan(spawn.Y));
        Assert.That(predictor.FacingYaw, Is.EqualTo(0.5f).Within(0.0001f));
    }

    [Test]
    public void RemoteSnapshotsInterpolatePositionAndWrappedYaw()
    {
        var buffer = new RemoteSnapshotBuffer3D();
        buffer.Push(100, Snapshot(7, Vector3.Zero, Vector3.Zero, 3f));
        buffer.Push(106, Snapshot(7, new Vector3(6f, 0f, 0f), Vector3.Zero, -3f));

        Assert.That(
            buffer.TrySample(103, AuthoritativeWorld3D.FixedDelta, out var sample),
            Is.True);
        Assert.That(sample.Position.X, Is.EqualTo(3f).Within(0.001f));
        Assert.That(MathF.Abs(MathF.Abs(sample.FacingYaw) - MathF.PI), Is.LessThan(0.05f));
    }

    [Test]
    public void RemoteSnapshotExtrapolationIsCapped()
    {
        var buffer = new RemoteSnapshotBuffer3D();
        buffer.Push(50, Snapshot(3, Vector3.Zero, new Vector3(12f, 0f, 0f), 0f));

        Assert.That(
            buffer.TrySample(500, AuthoritativeWorld3D.FixedDelta, out var sample),
            Is.True);
        Assert.That(
            sample.Position.X,
            Is.EqualTo(12f * AuthoritativeWorld3D.FixedDelta * 3f).Within(0.001f));
    }

    private static PlayerSnapshot3D Snapshot(int id, Vector3 position, Vector3 velocity, float yaw)
    {
        return new PlayerSnapshot3D
        {
            PlayerId = id,
            PositionX = position.X,
            PositionY = position.Y,
            PositionZ = position.Z,
            VelocityX = velocity.X,
            VelocityY = velocity.Y,
            VelocityZ = velocity.Z,
            FacingYaw = yaw,
            Grounded = true,
            AcknowledgedInput = 0,
        };
    }
}
