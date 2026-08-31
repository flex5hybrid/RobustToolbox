using System.Numerics;
using Robust.Shared3D;

namespace Robust.Client3D;

public readonly record struct InterpolatedPlayer3D(
    int PlayerId,
    Vector3 Position,
    Vector3 Velocity,
    float FacingYaw,
    bool Grounded);

public sealed class RemoteSnapshotBuffer3D
{
    private const int Capacity = 32;
    private readonly List<SnapshotSample3D> _samples = new();

    public int Count => _samples.Count;
    public long LatestTick => _samples.Count == 0 ? 0 : _samples[^1].ServerTick;

    public void Push(long serverTick, PlayerSnapshot3D snapshot)
    {
        if (_samples.Count > 0 && serverTick < _samples[^1].ServerTick)
            return;

        if (_samples.Count > 0 && serverTick == _samples[^1].ServerTick)
            _samples[^1] = new SnapshotSample3D(serverTick, snapshot);
        else
            _samples.Add(new SnapshotSample3D(serverTick, snapshot));

        if (_samples.Count > Capacity)
            _samples.RemoveRange(0, _samples.Count - Capacity);
    }

    public bool TrySample(double renderTick, float fixedDelta, out InterpolatedPlayer3D player)
    {
        if (_samples.Count == 0)
        {
            player = default;
            return false;
        }

        if (_samples.Count == 1 || renderTick <= _samples[0].ServerTick)
        {
            player = Convert(_samples[0].Snapshot);
            return true;
        }

        for (var i = 1; i < _samples.Count; i++)
        {
            var after = _samples[i];
            if (renderTick > after.ServerTick)
                continue;

            var before = _samples[i - 1];
            var tickSpan = after.ServerTick - before.ServerTick;
            var amount = tickSpan <= 0
                ? 1f
                : (float) Math.Clamp((renderTick - before.ServerTick) / tickSpan, 0d, 1d);
            player = Interpolate(before.Snapshot, after.Snapshot, amount);
            return true;
        }

        var latest = _samples[^1].Snapshot;
        var extraTicks = Math.Clamp(renderTick - _samples[^1].ServerTick, 0d, 3d);
        var position = Position(latest) + Velocity(latest) * (float) (extraTicks * fixedDelta);
        player = new InterpolatedPlayer3D(
            latest.PlayerId,
            position,
            Velocity(latest),
            latest.FacingYaw,
            latest.Grounded);
        return true;
    }

    private static InterpolatedPlayer3D Interpolate(PlayerSnapshot3D before, PlayerSnapshot3D after, float amount)
    {
        return new InterpolatedPlayer3D(
            after.PlayerId,
            Vector3.Lerp(Position(before), Position(after), amount),
            Vector3.Lerp(Velocity(before), Velocity(after), amount),
            LerpAngle(before.FacingYaw, after.FacingYaw, amount),
            amount < 0.5f ? before.Grounded : after.Grounded);
    }

    private static InterpolatedPlayer3D Convert(PlayerSnapshot3D snapshot)
    {
        return new InterpolatedPlayer3D(
            snapshot.PlayerId,
            Position(snapshot),
            Velocity(snapshot),
            snapshot.FacingYaw,
            snapshot.Grounded);
    }

    private static Vector3 Position(PlayerSnapshot3D snapshot)
    {
        return new Vector3(snapshot.PositionX, snapshot.PositionY, snapshot.PositionZ);
    }

    private static Vector3 Velocity(PlayerSnapshot3D snapshot)
    {
        return new Vector3(snapshot.VelocityX, snapshot.VelocityY, snapshot.VelocityZ);
    }

    private static float LerpAngle(float from, float to, float amount)
    {
        var difference = MathF.IEEERemainder(to - from, MathF.Tau);
        return from + difference * amount;
    }

    private readonly record struct SnapshotSample3D(long ServerTick, PlayerSnapshot3D Snapshot);
}
