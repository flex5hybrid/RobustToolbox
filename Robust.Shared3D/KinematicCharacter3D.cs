using System.Numerics;

namespace Robust.Shared3D;

public readonly record struct CharacterInput3D(float MoveX, float MoveY, float Yaw, bool Jump);

public sealed class KinematicCharacter3D
{
    public const float MoveSpeed = 4.5f;
    public const float JumpSpeed = 5.6f;
    public const float Gravity = -16f;

    public Vector3 Position { get; private set; }
    public Vector3 Velocity { get; private set; }
    public float Yaw { get; private set; }
    public bool Grounded { get; private set; }

    public KinematicCharacter3D(Vector3 spawn)
    {
        Position = spawn;
        Grounded = spawn.Z <= 0.001f;
    }

    public void SetState(Vector3 position, Vector3 velocity, float yaw, bool grounded)
    {
        Position = position;
        Velocity = velocity;
        Yaw = yaw;
        Grounded = grounded;
    }

    public void Step(in CharacterInput3D input, float dt, IReadOnlyList<Aabb3> colliders)
    {
        if (dt <= 0f)
            return;

        Yaw = input.Yaw;

        var localMove = new Vector2(input.MoveX, input.MoveY);
        if (localMove.LengthSquared() > 1f)
            localMove = Vector2.Normalize(localMove);

        var sin = MathF.Sin(Yaw);
        var cos = MathF.Cos(Yaw);
        var forward = new Vector3(sin, cos, 0f);
        var right = new Vector3(cos, -sin, 0f);
        var worldMove = right * localMove.X + forward * localMove.Y;

        Velocity = new Vector3(worldMove.X * MoveSpeed, worldMove.Y * MoveSpeed, Velocity.Z);

        if (input.Jump && Grounded)
        {
            Velocity = new Vector3(Velocity.X, Velocity.Y, JumpSpeed);
            Grounded = false;
        }

        Velocity = new Vector3(Velocity.X, Velocity.Y, Velocity.Z + Gravity * dt);

        MoveAxis(0, Velocity.X * dt, colliders);
        MoveAxis(1, Velocity.Y * dt, colliders);

        Grounded = false;
        var hitZ = MoveAxis(2, Velocity.Z * dt, colliders);
        if (hitZ)
        {
            if (Velocity.Z <= 0f)
                Grounded = true;

            Velocity = new Vector3(Velocity.X, Velocity.Y, 0f);
        }
    }

    private bool MoveAxis(int axis, float delta, IReadOnlyList<Aabb3> colliders)
    {
        if (MathF.Abs(delta) < 0.000001f)
            return false;

        var candidate = Position;
        SetAxis(ref candidate, axis, GetAxis(candidate, axis) + delta);
        var bounds = DemoWorld3D.CharacterBounds(candidate);
        var hit = false;

        foreach (var collider in colliders)
        {
            if (!bounds.Intersects(collider))
                continue;

            hit = true;
            var corrected = GetAxis(candidate, axis);

            if (axis == 0)
            {
                corrected = delta > 0f
                    ? collider.Min.X - DemoWorld3D.PlayerRadius
                    : collider.Max.X + DemoWorld3D.PlayerRadius;
            }
            else if (axis == 1)
            {
                corrected = delta > 0f
                    ? collider.Min.Y - DemoWorld3D.PlayerRadius
                    : collider.Max.Y + DemoWorld3D.PlayerRadius;
            }
            else
            {
                corrected = delta > 0f
                    ? collider.Min.Z - DemoWorld3D.PlayerHeight
                    : collider.Max.Z;
            }

            SetAxis(ref candidate, axis, corrected);
            bounds = DemoWorld3D.CharacterBounds(candidate);
        }

        Position = candidate;
        return hit;
    }

    private static float GetAxis(Vector3 value, int axis)
    {
        return axis switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }

    private static void SetAxis(ref Vector3 value, int axis, float component)
    {
        value = axis switch
        {
            0 => new Vector3(component, value.Y, value.Z),
            1 => new Vector3(value.X, component, value.Z),
            2 => new Vector3(value.X, value.Y, component),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }
}
