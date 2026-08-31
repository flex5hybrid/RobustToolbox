using System.Numerics;
using Robust.Shared3D;

namespace Robust.Client3D;

public sealed class PredictedPlayer3D
{
    private readonly KinematicCharacter3D _character;
    private readonly List<PendingInput3D> _pendingInputs = new();
    private ulong _nextSequence;

    public Vector3 Position => _character.Position;
    public Vector3 Velocity => _character.Velocity;
    public bool Grounded => _character.IsGrounded;
    public float FacingYaw { get; private set; }
    public int PendingInputCount => _pendingInputs.Count;

    public PredictedPlayer3D(Vector3 spawnPosition)
    {
        _character = new KinematicCharacter3D(spawnPosition, DemoWorld3D.CollisionBounds);
    }

    public InputMessage3D Step(Vector2 movement, bool jump, float facingYaw, float deltaTime)
    {
        var input = new InputMessage3D
        {
            Sequence = ++_nextSequence,
            MovementX = movement.X,
            MovementY = movement.Y,
            Jump = jump,
            FacingYaw = facingYaw,
        };

        _character.Step(new CharacterInput3D(movement, jump), deltaTime);
        FacingYaw = facingYaw;
        _pendingInputs.Add(new PendingInput3D(input, deltaTime));
        return input;
    }

    public void Reconcile(PlayerSnapshot3D authoritative)
    {
        _character.ApplyAuthoritativeState(
            new Vector3(authoritative.PositionX, authoritative.PositionY, authoritative.PositionZ),
            new Vector3(authoritative.VelocityX, authoritative.VelocityY, authoritative.VelocityZ),
            authoritative.Grounded);
        FacingYaw = authoritative.FacingYaw;

        _pendingInputs.RemoveAll(pending => pending.Input.Sequence <= authoritative.AcknowledgedInput);

        foreach (var pending in _pendingInputs)
        {
            var input = pending.Input;
            _character.Step(
                new CharacterInput3D(new Vector2(input.MovementX, input.MovementY), input.Jump),
                pending.DeltaTime);
            FacingYaw = input.FacingYaw;
        }
    }

    private readonly record struct PendingInput3D(InputMessage3D Input, float DeltaTime);
}
