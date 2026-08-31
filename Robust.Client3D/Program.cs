using Robust.Client3D;
using Robust.Server3D;

var host = "127.0.0.1";
var port = 12123;
var name = $"client-{Environment.ProcessId}";
var autoplay = false;
var runSeconds = 0;

foreach (var argument in args)
{
    if (argument.StartsWith("--host=", StringComparison.OrdinalIgnoreCase))
        host = argument[7..];
    else if (argument.StartsWith("--port=", StringComparison.OrdinalIgnoreCase) && int.TryParse(argument[7..], out var parsedPort))
        port = parsedPort;
    else if (argument.StartsWith("--name=", StringComparison.OrdinalIgnoreCase))
        name = argument[7..];
    else if (argument.Equals("--autoplay", StringComparison.OrdinalIgnoreCase))
        autoplay = true;
    else if (argument.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase) && int.TryParse(argument[10..], out var parsedSeconds))
        runSeconds = parsedSeconds;
}

await using var client = new NetworkClient3D();
await client.ConnectAsync(host, port, name);
Console.WriteLine($"Connected as player {client.PlayerId}. Server tick {client.ServerTick}.");

var fixedDelta = AuthoritativeServer3D.FixedDelta;
var started = DateTime.UtcNow;
var tick = 0;

while (runSeconds <= 0 || DateTime.UtcNow - started < TimeSpan.FromSeconds(runSeconds))
{
    var moveX = 0f;
    var moveY = 0f;
    var jump = false;
    var yaw = 0f;

    if (autoplay)
    {
        moveY = tick < AuthoritativeServer3D.SimulationRate * 2 ? 1f : 0f;
        moveX = tick >= AuthoritativeServer3D.SimulationRate * 2 ? 1f : 0f;
        jump = tick == AuthoritativeServer3D.SimulationRate / 2;
        yaw = tick >= AuthoritativeServer3D.SimulationRate * 2 ? MathF.PI / 2f : 0f;
    }

    await client.StepLocalAsync(moveX, moveY, yaw, jump, fixedDelta);
    tick++;

    if (tick % 30 == 0)
    {
        var player = client.GetPredictedLocalPlayer();
        Console.WriteLine(
            $"tick={client.ServerTick} players={client.GetAuthoritativePlayers().Count} " +
            $"pos=({player.Position.X:F2},{player.Position.Y:F2},{player.Position.Z:F2}) grounded={player.Grounded}");
    }

    await Task.Delay(TimeSpan.FromSeconds(fixedDelta));
}
