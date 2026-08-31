using NUnit.Framework;
using Robust.Shared3D;

namespace Robust.Client3D.Tests;

[TestFixture]
public sealed class AuthoritativeInput3DTests
{
    [Test]
    public void ServerAcknowledgesOnlyInputsAppliedBySimulation()
    {
        var player = new PlayerEntity3D(1, DemoWorld3D.SpawnPosition);
        var first = new InputMessage3D
        {
            Sequence = 1,
            MovementY = 1f,
        };
        var second = new InputMessage3D
        {
            Sequence = 2,
            MovementX = 1f,
        };

        Assert.That(player.ApplyInput(first), Is.True);
        Assert.That(player.ApplyInput(second), Is.True);
        Assert.That(player.PendingInputCount, Is.EqualTo(2));
        Assert.That(player.AcknowledgedInput, Is.Zero);

        player.Step(AuthoritativeWorld3D.FixedDelta);
        Assert.That(player.AcknowledgedInput, Is.EqualTo(1));
        Assert.That(player.PendingInputCount, Is.EqualTo(1));

        player.Step(AuthoritativeWorld3D.FixedDelta);
        Assert.That(player.AcknowledgedInput, Is.EqualTo(2));
        Assert.That(player.PendingInputCount, Is.Zero);
    }

    [Test]
    public void ServerRejectsDuplicateOrOlderInput()
    {
        var player = new PlayerEntity3D(1, DemoWorld3D.SpawnPosition);
        var input = new InputMessage3D { Sequence = 5 };

        Assert.That(player.ApplyInput(input), Is.True);
        Assert.That(player.ApplyInput(input), Is.False);
        Assert.That(player.ApplyInput(new InputMessage3D { Sequence = 4 }), Is.False);
        Assert.That(player.PendingInputCount, Is.EqualTo(1));
    }
}
