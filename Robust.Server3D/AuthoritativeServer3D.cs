using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Robust.Shared3D;

namespace Robust.Server3D;

public sealed class AuthoritativeServer3D : IAsyncDisposable
{
    public const int SimulationRate = 120;
    public const int SnapshotRate = 20;
    public const float FixedDelta = 1f / SimulationRate;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
    private readonly AuthoritativeWorld3D _world = new();
    private Task? _acceptTask;
    private Task? _simulationTask;
    private long _serverTick;

    public AuthoritativeServer3D(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => _listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0;
    public long ServerTick => Interlocked.Read(ref _serverTick);
    public int PlayerCount => _world.PlayerCount;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_shutdown.Token);
        _simulationTask = SimulationLoopAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_shutdown.IsCancellationRequested)
            return;

        _shutdown.Cancel();
        _listener.Stop();

        foreach (var session in _sessions.Values)
            session.Dispose();

        var tasks = new[] { _acceptTask, _simulationTask }.Where(task => task is not null).Cast<Task>().ToArray();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, leaveOpen: true))
        using (var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true })
        {
            var helloLine = await reader.ReadLineAsync(cancellationToken);
            if (helloLine is null || NetworkProtocol3D.ReadType(helloLine) != NetworkProtocol3D.Hello)
                return;

            var hello = NetworkProtocol3D.Deserialize<ClientHello3D>(helloLine);
            var player = _world.AddPlayer(hello.Name);
            var session = new ClientSession(player.PlayerId, client, writer);

            try
            {
                await session.SendAsync(
                    NetworkProtocol3D.Serialize(ServerWelcome3D.Create(player.PlayerId, ServerTick, player)),
                    cancellationToken);

                _sessions[player.PlayerId] = session;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                        break;

                    if (NetworkProtocol3D.ReadType(line) != NetworkProtocol3D.Input)
                        continue;

                    var input = NetworkProtocol3D.Deserialize<InputCommand3D>(line);
                    _world.SubmitInput(player.PlayerId, input);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested || !client.Connected)
            {
            }
            finally
            {
                _sessions.TryRemove(player.PlayerId, out _);
                _world.RemovePlayer(player.PlayerId);
                session.Dispose();
            }
        }
    }

    private async Task SimulationLoopAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var previous = stopwatch.Elapsed.TotalSeconds;
        var accumulator = 0d;
        var snapshotsEvery = SimulationRate / SnapshotRate;

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = stopwatch.Elapsed.TotalSeconds;
            accumulator += Math.Min(now - previous, 0.25d);
            previous = now;

            while (accumulator >= FixedDelta)
            {
                _world.Step(FixedDelta);
                var tick = Interlocked.Increment(ref _serverTick);
                accumulator -= FixedDelta;

                if (tick % snapshotsEvery == 0)
                    await BroadcastSnapshotsAsync(tick, cancellationToken);
            }

            await Task.Delay(1, cancellationToken);
        }
    }

    private async Task BroadcastSnapshotsAsync(long tick, CancellationToken cancellationToken)
    {
        foreach (var session in _sessions.Values)
        {
            var snapshot = _world.CreateSnapshot(session.PlayerId, tick);
            try
            {
                await session.SendAsync(NetworkProtocol3D.Serialize(snapshot), cancellationToken);
            }
            catch (IOException)
            {
                // The receive loop will clean the disconnected session up.
            }
            catch (SocketException)
            {
                // The receive loop will clean the disconnected session up.
            }
        }
    }

    private sealed class ClientSession : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public ClientSession(int playerId, TcpClient client, StreamWriter writer)
        {
            PlayerId = playerId;
            _client = client;
            _writer = writer;
        }

        public int PlayerId { get; }

        public async Task SendAsync(string line, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            _client.Dispose();
            _writeLock.Dispose();
        }
    }
}
