# Incompatible 3D engine migration

The long-term target is a real 3D RobustToolbox fork: XYZ transforms, quaternion orientation, volumetric physics and maps, perspective rendering, models, materials, and server-authoritative multiplayer.

## Recovery note

The first experimental 3D commits existed only in a local checkout and were not published to this GitHub fork. The remote branch therefore reconstructs the vertical slice from the current `master` without changing legacy 2D APIs yet. This keeps the work recoverable on GitHub while the deeper engine-core migration is re-applied deliberately.

## Current checkpoint

- `Robust.Shared3D`: common room geometry, deterministic kinematic character controller, network messages, authoritative world state.
- `Robust.Server3D`: headless TCP server, 120 Hz fixed simulation, 20 Hz per-client authoritative snapshots.
- `Robust.Client3D`: input transport, immediate local prediction, acknowledged-input reconciliation, remote-player snapshot state.
- `Robust.Client3D.Tests`: deterministic physics tests and a real TCP test with one server and two clients.

## Next checkpoint

1. Restore the OpenGL 3.3 perspective renderer and draw every replicated player in the same 3D room.
2. Re-apply the incompatible `Vector3` / quaternion engine transform core from the local prototype.
3. Replace component trees and entity lookup with a `Box3` broadphase.
4. Move the standalone multiplayer entities into the normal ECS/network stack.
5. Replace the demo room with volumetric map chunks.
