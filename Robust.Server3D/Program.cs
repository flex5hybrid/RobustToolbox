using Robust.Server3D;

var port = 12123;
var runSeconds = 0;

foreach (var argument in args)
{
    if (argument.StartsWith("--port=", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(argument[7..], out var parsedPort))
    {
        port = parsedPort;
    }
    else if (argument.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase) &&
             int.TryParse(argument[10..], out var parsedSeconds))
    {
        runSeconds = parsedSeconds;
    }
}

await using var server = new AuthoritativeServer3D(port);
await server.StartAsync();
Console.WriteLine($"Robust.Server3D listening on 127.0.0.1:{server.Port} at {AuthoritativeServer3D.SimulationRate} Hz.");

if (runSeconds > 0)
{
    await Task.Delay(TimeSpan.FromSeconds(runSeconds));
}
else
{
    Console.WriteLine("Press Ctrl+C to stop.");
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        completion.TrySetResult();
    };
    await completion.Task;
}
