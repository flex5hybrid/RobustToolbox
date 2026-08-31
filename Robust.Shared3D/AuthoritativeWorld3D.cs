namespace Robust.Shared3D;

public sealed class AuthoritativeWorld3D
{
    private sealed class PlayerRuntime
    {
        public required int PlayerId { get; init; }
        public required string Name { get; init; }
        public required KinematicCharacter3D Character { get; init; }
        public InputCommand3D Input { get; set; } = InputCommand3D.Create(0, 0f, 0f, 0f, false);
        public int LastProcessedSequence { get; set; }
        public bool PendingJump { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<int, PlayerRuntime> _players = new();
    private int _nextPlayerId = 1;

    public int PlayerCount
    {
        get
        {
            lock (_sync)
                return _players.Count;
        }
    }

    public PlayerSnapshot3D AddPlayer(string name)
    {
        lock (_sync)
        {
            var playerId = _nextPlayerId++;
            var runtime = new PlayerRuntime
            {
                PlayerId = playerId,
                Name = string.IsNullOrWhiteSpace(name) ? $"Player {playerId}" : name.Trim(),
                Character = new KinematicCharacter3D(DemoWorld3D.SpawnFor(playerId)),
            };

            _players.Add(playerId, runtime);
            return Snapshot(runtime);
        }
    }

    public void RemovePlayer(int playerId)
    {
        lock (_sync)
            _players.Remove(playerId);
    }

    public void SubmitInput(int playerId, InputCommand3D input)
    {
        lock (_sync)
        {
            if (!_players.TryGetValue(playerId, out var player))
                return;

            if (input.Sequence <= player.Input.Sequence)
                return;

            player.Input = input;
            player.PendingJump |= input.Jump;
        }
    }

    public void Step(float dt)
    {
        lock (_sync)
        {
            foreach (var player in _players.Values)
            {
                var input = player.Input.ToCharacterInput(player.PendingJump);
                player.PendingJump = false;
                player.Character.Step(input, dt, DemoWorld3D.Colliders);
                player.LastProcessedSequence = player.Input.Sequence;
            }
        }
    }

    public WorldSnapshot3D CreateSnapshot(int receiverPlayerId, long serverTick)
    {
        lock (_sync)
        {
            var ack = _players.TryGetValue(receiverPlayerId, out var receiver)
                ? receiver.LastProcessedSequence
                : 0;

            var snapshots = _players.Values
                .OrderBy(player => player.PlayerId)
                .Select(Snapshot)
                .ToArray();

            return WorldSnapshot3D.Create(serverTick, ack, snapshots);
        }
    }

    public PlayerSnapshot3D? GetPlayer(int playerId)
    {
        lock (_sync)
            return _players.TryGetValue(playerId, out var player) ? Snapshot(player) : null;
    }

    private static PlayerSnapshot3D Snapshot(PlayerRuntime player)
    {
        return new PlayerSnapshot3D(
            player.PlayerId,
            player.Name,
            player.Character.Position,
            player.Character.Velocity,
            player.Character.Yaw,
            player.Character.Grounded);
    }
}
