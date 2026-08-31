# Robust 3D multiplayer checkpoint

This branch contains an intentionally isolated 3D vertical slice while the legacy 2D engine is being replaced.

## Run the authoritative server

```powershell
dotnet run --project Robust.Server3D/Robust.Server3D.csproj -- --port=12123
```

## Run clients

```powershell
dotnet run --project Robust.Client3D/Robust.Client3D.csproj -- --name=alpha --autoplay
dotnet run --project Robust.Client3D/Robust.Client3D.csproj -- --name=bravo --autoplay
```

The server simulates movement and collision at 120 Hz and sends world snapshots at 20 Hz. Clients send input commands, predict their local player immediately, reconcile against authoritative snapshots, and keep the latest authoritative state for remote players.

The next rendering step consumes `GetPredictedLocalPlayer()` for the local body and `GetAuthoritativePlayers()` for all remote bodies.
