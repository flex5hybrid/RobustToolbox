using NUnit.Framework;
using Robust.Client3D;
using Robust.Server3D;

namespace Robust.Client3D.Tests;

[TestFixture]
public sealed class NetworkMultiplayer3DTests
{
    [Test]
    [Timeout(15000)]
    public async Task TwoClientsReceiveSameAuthoritativeWorld()
    {
        await using var server = new AuthoritativeServer3D();
        await server.StartAsync();

        await using var first = new NetworkClient3D();
        await using var second = new NetworkClient3D();
        await first.ConnectAsync("127.0.0.1", server.Port, "alpha");
        await second.ConnectAsync("127.0.0.1", server.Port, "bravo");

        await first.WaitForPlayerCountAsync(2, TimeSpan.FromSeconds(3));
        await second.WaitForPlayerCountAsync(2, TimeSpan.FromSeconds(3));

        var maxSecondHeight = 0f;
        for (var i = 0; i < 120; i++)
        {
            await first.StepLocalAsync(0f, 1f, 0f, false, AuthoritativeServer3D.FixedDelta);
            await second.StepLocalAsync(1f, 0f, 0f, i == 12, AuthoritativeServer3D.FixedDelta);
            maxSecondHeight = Math.Max(maxSecondHeight, second.GetPredictedLocalPlayer().Position.Z);
            await Task.Delay(8);
        }

        await Task.Delay(250);

        var firstView = first.GetAuthoritativePlayers();
        var secondView = second.GetAuthoritativePlayers();

        Assert.That(firstView.Count, Is.EqualTo(2));
        Assert.That(secondView.Count, Is.EqualTo(2));
        Assert.That(maxSecondHeight, Is.GreaterThan(0.5f));

        foreach (var playerId in firstView.Keys)
        {
            Assert.That(secondView.ContainsKey(playerId), Is.True);
            var distance = Vector3.Distance(firstView[playerId].Position, secondView[playerId].Position);
            Assert.That(distance, Is.LessThan(0.35f), $"Player {playerId} diverged between client snapshots.");
        }

        Assert.That(firstView[first.PlayerId].Position.Y, Is.GreaterThan(-3.2f));
        Assert.That(secondView[second.PlayerId].Position.X, Is.GreaterThan(3.7f));
    }
}
