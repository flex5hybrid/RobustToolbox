using System.Net.Sockets;
using Robust.Shared3D;

namespace Robust.Client3D;

public sealed class NetworkClient3D : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly List<PredictedInput> _pendingInputs = new();
    private readonly Dictionary<int, PlayerSnapshot3D> _players = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _receiveTask;
    private KinematicCharacter3D? _localCharacter;
    private int _nextSequence;
    private int _acknowledgedSequence;
    private long _serverTick;

    public int PlayerId { get; private set; }
    public long ServerTick => Interlocked.Read(ref _serverTick);

    public async Task ConnectAsync(string host, int port, string name, CancellationToken cancellationToken = default)
    {
        if (_client is not null)
            throw new InvalidOperationException("Client is already connected.");

        var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);

        var stream = client.GetStream();
        var reader = new StreamReader(stream, leaveOpen: true);
        var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(NetworkProtocol3D.Serialize(ClientHello3D.Create(name)).AsMemory(), cancellationToken);
        var welcomeLine = await reader.ReadLineAsync(cancellationToken)
                          ?? throw new IOException("Server closed the connection before welcome.");

        if (NetworkProtocol3D.ReadType(welcomeLine) != NetworkProtocol3D.Welcome)
            throw new InvalidDataException("Expected welcome message.");

        var welcome = NetworkProtocol3D.Deserialize<ServerWelcome3D>(welcomeLine);

        _client = client;
        _reader = reader;
        _writer = writer;
        PlayerId = welcome.PlayerId;
        Interlocked.Exchange(ref _serverTick, welcome.ServerTick);

        lock (_sync)
        {
            _localCharacter = new KinematicCharacter3D(welcome.Player.Position);
            _localCharacter.SetState(
                welcome.Player.Position,
                welcome.Player.Velocity,
                welcome.Player.Yaw,
                welcome.Player.Grounded);
            _players[PlayerId] = welcome.Player;
        }

        _receiveTask = ReceiveLoopAsync(_shutdown.Token);
    }

    public async Task StepLocalAsync(
        float moveX,
        float moveY,
        float yaw,
        bool jump,
        float dt,
        CancellationToken cancellationToken = default)
    {
        var writer = _writer ?? throw new InvalidOperationException("Client is not connected.");
        InputCommand3D command;

        lock (_sync)
        {
            var sequence = ++_nextSequence;
            command = InputCommand3D.Create(sequence, moveX, moveY, yaw, jump);
            _localCharacter!.Step(command.ToCharacterInput(jump), dt, DemoWorld3D.Colliders);
            _pendingInputs.Add(new PredictedInput(command, dt));
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(NetworkProtocol3D.Serialize(command).AsMemory(), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public PlayerSnapshot3D GetPredictedLocalPlayer()
    {
        lock (_sync)
        {
            var character = _localCharacter ?? throw new InvalidOperationException("Client is not connected.");
            var name = _players.TryGetValue(PlayerId, out var snapshot) ? snapshot.Name : $"Player {PlayerId}";
            return new PlayerSnapshot3D(
                PlayerId,
                name,
                character.Position,
                character.Velocity,
                character.Yaw,
                character.Grounded);
        }
    }

    public IReadOnlyDictionary<int, PlayerSnapshot3D> GetAuthoritativePlayers()
    {
        lock (_sync)
            return _players.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    public async Task WaitForPlayerCountAsync(int expected, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_players.Count >= expected)
                    return;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for {expected} players.");
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _client?.Dispose();

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException) when (_shutdown.IsCancellationRequested)
            {
            }
        }

        _reader?.Dispose();
        _writer?.Dispose();
        _writeLock.Dispose();
        _shutdown.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _reader!;
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (line is null)
                break;

            if (NetworkProtocol3D.ReadType(line) != NetworkProtocol3D.Snapshot)
                continue;

            Reconcile(NetworkProtocol3D.Deserialize<WorldSnapshot3D>(line));
        }
    }

    private void Reconcile(WorldSnapshot3D snapshot)
    {
        lock (_sync)
        {
            Interlocked.Exchange(ref _serverTick, snapshot.ServerTick);
            _players.Clear();
            foreach (var player in snapshot.Players)
                _players[player.PlayerId] = player;

            if (!_players.TryGetValue(PlayerId, out var authoritative) || _localCharacter is null)
                return;

            _acknowledgedSequence = Math.Max(_acknowledgedSequence, snapshot.AcknowledgedSequence);
            _pendingInputs.RemoveAll(input => input.Command.Sequence <= _acknowledgedSequence);

            _localCharacter.SetState(
                authoritative.Position,
                authoritative.Velocity,
                authoritative.Yaw,
                authoritative.Grounded);

            foreach (var pending in _pendingInputs)
                _localCharacter.Step(pending.Command.ToCharacterInput(pending.Command.Jump), pending.Delta, DemoWorld3D.Colliders);
        }
    }

    private readonly record struct PredictedInput(InputCommand3D Command, float Delta);
}
