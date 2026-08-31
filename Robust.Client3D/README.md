# Robust.Client3D

`Robust.Client3D` is now the visual client for the first server-authoritative 3D
multiplayer slice. The legacy standalone room is still available with
`--offline`, but the default entry point connects to `Robust.Server3D` and uses
client-side prediction for the local player plus delayed interpolation for
remote players.

Build the 3D server and client from the RobustToolbox directory:

```powershell
dotnet build Robust.Server3D\Robust.Server3D.csproj
dotnet build Robust.Client3D\Robust.Client3D.csproj
```

Start the authoritative server:

```powershell
bin\Server3D\Robust.Server3D.exe
```

Then start two clients in separate terminals:

```powershell
bin\Client3D\Robust.Client3D.exe
bin\Client3D\Robust.Client3D.exe
```

The client connects to `127.0.0.1:12133` by default. Override the endpoint with
`--host=<address>` and `--port=<port>`.

Controls:

- `WASD` moves relative to the camera.
- Mouse movement rotates the third-person camera.
- `Space` jumps.
- `Escape` exits.

The local player is rendered in orange and is predicted immediately at the
server fixed timestep. Server snapshots reconcile that prediction and replay
inputs that have not yet been acknowledged. Other players are rendered in blue
from a snapshot buffer delayed by 12 server ticks (100 ms at 120 Hz), with
short extrapolation capped to three ticks when a snapshot is late.

For an automated network smoke run, keep `Robust.Server3D` running and use:

```powershell
bin\Client3D\Robust.Client3D.exe --autoplay --frames=180 --screenshot=bin\Client3D\network-smoke.bmp
```

The previous local-only 3D room can still be started with:

```powershell
bin\Client3D\Robust.Client3D.exe --offline
```
