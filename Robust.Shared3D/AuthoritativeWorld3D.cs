using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Robust.Shared.Maths;

namespace Robust.Shared3D;

/// <summary>
/// Minimal server-owned entity world used to establish the first networked 3D authority boundary.
/// </summary>
public sealed class AuthoritativeWorld3D
{
    public const float FixedDelta = 1f / 120f;

    private readonly Dictionary<int, PlayerEntity3D> _players = new();
    private readonly Func<int, Vector3> _spawnResolver;
    private readonly IReadOnlyList<Box3> _collisionBounds;

    public long Tick { get; private set; }
    public IReadOnlyCollection<PlayerEntity3D> Players => _players.Values;
    public WorldDefinition3D? Definition { get; }
    public IReadOnlyList<Box3> CollisionBounds => _collisionBounds;

    public AuthoritativeWorld3D(WorldDefinition3D? definition = null)
    {
        Definition = definition;
        if (definition is null)
        {
            _spawnResolver = DemoWorld3D.GetPlayerSpawnPosition;
            _collisionBounds = DemoWorld3D.CollisionBounds;
        }
        else
        {
            _spawnResolver = definition.GetPlayerSpawnPosition;
            _collisionBounds = definition.CollisionBounds;
        }
    }

    public PlayerEntity3D AddPlayer(int playerId)
    {
        var player = new PlayerEntity3D(
            playerId,
            _spawnResolver(playerId),
            _collisionBounds);
        _players.Add(playerId, player);
        return player;
    }

    public bool RemovePlayer(int playerId)
    {
        return _players.Remove(playerId);
    }

    public bool ApplyInput(int playerId, InputMessage3D input)
    {
        return _players.TryGetValue(playerId, out var player) && player.ApplyInput(input);
    }

    public void Step()
    {
        foreach (var player in _players.Values)
            player.Step(FixedDelta);

        Tick++;
    }

    public SnapshotMessage3D CreateSnapshot()
    {
        return new SnapshotMessage3D
        {
            ServerTick = Tick,
            Players = _players.Values
                .OrderBy(static player => player.PlayerId)
                .Select(static player => player.CreateSnapshot())
                .ToArray(),
        };
    }
}

public sealed class PlayerEntity3D
{
    private readonly Queue<InputMessage3D> _pendingInputs = new();
    private Vector2 _movement;
    private ulong _lastReceivedInput;

    public int PlayerId { get; }
    public KinematicCharacter3D Character { get; }
    public float FacingYaw { get; private set; }
    public ulong AcknowledgedInput { get; private set; }
    public int PendingInputCount => _pendingInputs.Count;

    public PlayerEntity3D(int playerId, Vector3 spawnPosition)
        : this(playerId, spawnPosition, DemoWorld3D.CollisionBounds)
    {
    }

    public PlayerEntity3D(
        int playerId,
        Vector3 spawnPosition,
        IReadOnlyList<Box3> collisionBounds)
    {
        ArgumentNullException.ThrowIfNull(collisionBounds);
        PlayerId = playerId;
        Character = new KinematicCharacter3D(spawnPosition, collisionBounds);
    }

    public bool ApplyInput(InputMessage3D input)
    {
        if (input.Sequence <= _lastReceivedInput)
            return false;

        var movement = new Vector2(input.MovementX, input.MovementY);
        if (!float.IsFinite(movement.X) || !float.IsFinite(movement.Y))
            movement = Vector2.Zero;
        if (movement.LengthSquared() > 1f)
            movement = Vector2.Normalize(movement);

        _pendingInputs.Enqueue(input with
        {
            MovementX = movement.X,
            MovementY = movement.Y,
            FacingYaw = float.IsFinite(input.FacingYaw) ? input.FacingYaw : 0f,
        });
        _lastReceivedInput = input.Sequence;
        return true;
    }

    public void Step(float deltaTime)
    {
        var jump = false;
        if (_pendingInputs.TryDequeue(out var input))
        {
            _movement = new Vector2(input.MovementX, input.MovementY);
            jump = input.Jump;
            FacingYaw = input.FacingYaw;
            AcknowledgedInput = input.Sequence;
        }

        Character.Step(new CharacterInput3D(_movement, jump), deltaTime);
    }

    public PlayerSnapshot3D CreateSnapshot()
    {
        var position = Character.Position;
        var velocity = Character.Velocity;
        return new PlayerSnapshot3D
        {
            PlayerId = PlayerId,
            PositionX = position.X,
            PositionY = position.Y,
            PositionZ = position.Z,
            VelocityX = velocity.X,
            VelocityY = velocity.Y,
            VelocityZ = velocity.Z,
            FacingYaw = FacingYaw,
            Grounded = Character.IsGrounded,
            AcknowledgedInput = AcknowledgedInput,
        };
    }
}
