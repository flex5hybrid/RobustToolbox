# Robust.Client3D

`Robust.Client3D` is the visual client for the server-authoritative 3D multiplayer slice.
The normal network path is now data-driven: `Robust.Server3D` loads a world JSON, sends
its resource path and SHA-256 during the protocol v2 handshake, and every client verifies
and loads the same world before entering the render loop.

The world definition provides player spawn points, static object transforms, model resource
paths and collision boxes. The server simulation, client prediction and third-person camera
all use the same collision bounds. Objects with `.gltf` or `.glb` model resources are uploaded
to OpenGL and rendered as indexed meshes; objects without a model remain procedural cubes.

Build and test from the RobustToolbox directory:

```powershell
dotnet build Robust.Server3D\Robust.Server3D.csproj
dotnet build Robust.Client3D\Robust.Client3D.csproj
dotnet test Robust.Client3D.Tests\Robust.Client3D.Tests.csproj
```

Start the authoritative server:

```powershell
bin\Server3D\Robust.Server3D.exe
```

The default resource is `Worlds/bootstrap-world3d.json`. A different packaged world can be
selected with `--world=Worlds/<name>.json`.

Then start one or more clients:

```powershell
bin\Client3D\Robust.Client3D.exe
```

The client connects to `127.0.0.1:12133` by default. Override the endpoint with
`--host=<address>` and `--port=<port>`.

Controls:

- `WASD` moves relative to the camera.
- Mouse movement rotates the third-person camera.
- `Space` jumps.
- `Escape` exits.

The local player is orange and client-predicted. Remote players are blue and use delayed
snapshot interpolation with capped extrapolation. The bootstrap glTF pyramid is rendered
in gold so it is visually obvious that model geometry came from an asset rather than the
procedural cube path.

For an automated network smoke run, keep `Robust.Server3D` running and use:

```powershell
bin\Client3D\Robust.Client3D.exe --autoplay --frames=180 --screenshot=bin\Client3D\network-smoke.bmp
```

The previous hard-coded local room is still available for regression checks:

```powershell
bin\Client3D\Robust.Client3D.exe --offline
```
