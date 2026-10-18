# SupermarketWithMoreFriends

A BepInEx 6 (IL2CPP) mod for **Supermarket Simulator** that raises the co-op lobby
from 4 players up to 8 (configurable 1–255). Players #5–#8 spawn as runtime-cloned
player bodies with fresh Photon view IDs.

## Status

- ✅ 4-player baseline intact
- ✅ Room capacity 8 (Photon + Steam lobby), config-driven `MaxPlayers`
- ✅ Loading-screen gate bypasses for 5+ player sessions
- ✅ Runtime player-body pool grows 4 → N (clones of scene bodies)
- ✅ Fresh PhotonView ID assignment for cloned bodies (headless 6-body autotest passes:
  clones keep distinct IDs `10001`, `10002`, ...)
- ⚠️ End-to-end 5-player *playtest* (movement/vision on both machines) still pending —
  needs a real session with friends on this exact build

## Requirements

- Windows
- Supermarket Simulator (Unity 6000.3.6f1, IL2CPP)
- .NET 6 runtime shipped with the game (`<install>\dotnet\`)

## How it works

The game scene contains exactly **4 `PlayerInstance` bodies** and has no code path that
instantiates an additional player, and no player prefab exists in the assets. The mod:

1. Raises the Photon room + Steam lobby caps (`MaxPlayers` in the config).
2. When a 5th+ player joins and no unowned body exists, **clones** a scene player
   body at runtime and grows `PlayerManager.PlayerInstances`.
3. Replaces the clone's inherited PhotonView with a **brand-new** component that gets a
   fresh runtime ID (`AllocateViewID`) — the scene's hardwired IDs can't be reused.
4. Bypasses the game's loading gate (`_InitialLoadingUIRoutine_b__36_0` /
   `_InitialLoadingWarningRoutine_b__37_0`), which never completes past 4 players.

Headless verification: pass `-autotest` to the game (with
`Diagnostics.SelfTestPhotonRealloc = true` in the config) — it auto-connects to Photon,
creates a room, grows the pool, prints `AUTOTEST6: ... PASS/FAIL`, and exits. No friends
needed for the automated check.

## Build

Requires a .NET 6 SDK. The csproj resolves the game's BepInEx core/interop DLLs from
`BepInExDir` (defaults to the Steam install path; override with `-D:BepInExDir=...`).

```powershell
dotnet build -c Release
```

Deploy `bin\Release\net6.0\SupermarketMorePlayers.dll` to:

```
<game>\BepInEx\plugins\SupermarketMorePlayers.dll
```

## Config

`BepInEx\config\dev.codex.supermarket.moreplayers.cfg`

```ini
[General]
MaxPlayers = 8            # lobby capacity, host controls the room (1-255)

[Diagnostics]
ForceSyncTargetToZero = true    # lets loading finish with 5+ players
ForceMinPlayerInstances = 0     # solo-testing only; keep 0 in real multiplayer
SelfTestPhotonRealloc = false   # set true for the headless -autotest, then back to false
```

> **Every player in a session must run the same DLL build.** Mixed versions produce
> baffling desync bugs. Only the host's `MaxPlayers` matters for the room cap.

## Friend pack

`pack\` (or the prebuilt `SupermarketMorePlayers-FriendPack.zip`) contains the full
runtime needed by friends: `BepInEx\core|interop|unity-libs|plugins|config`,
`dotnet\`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`.
Extract over the game folder, then launch. See `README-INSTALL.txt` in the pack.

## Repo contents

```
Plugin.cs                  # the entire mod (single source file)
MorePlayersBepInEx.csproj  # builds SupermarketMorePlayers.dll (follows upstream file name)
build.ps1                  # optional convenience build invoker
scripts/AutoTest.ps1       # headless -autotest launcher + verdict parser
```