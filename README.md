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

## Automated testing

Two headless harnesses live in `scripts/`. Both build/deploy the mod and parse machine-
readable `PASS`/`FAIL` verdict lines from the BepInEx log, so you can test → fix → re-test
until green. No friends needed for the automated checks.

**1. Fabricated-player matrix** (`scripts/RunMatrixTest.ps1 -Players 6 -Loop 3`)
One game instance, solo room. The mod fabricates real `Photon.Realtime.Player`s
(`il2cpp_object_new`) and drives the game's actual `NetworkManager.OnPlayerEnteredRoom` for
fake players #5+, grading:
- `POOL` — pool grows to the target count
- `DOUBLE` — no double-clone (exact grow count) [concern #6]
- `UNIQUE` — every body has a distinct fresh ViewID [host side of #1/#2]
- `DRIVE` — `OnPlayerEnteredRoom` ran for fabricated players with no exceptions [#5]

```powershell
powershell -ExecutionPolicy Bypass -File scripts\RunMatrixTest.ps1 -Players 6 -Loop 3
# -> MATRIX RESULT: POOL=PASS DOUBLE=PASS UNIQUE=PASS DRIVE=PASS
```

**2. Two-instance real-peer harness** (`scripts/RunTwoPlayerTest.ps1 -Players 6 -Loop 2`)
Runs TWO full game instances on one machine in the same Photon room: the host creates the
room and the client joins as a real 2nd player. Cross-checks both logs:
- Host: `POOL / DOUBLE / UNIQUE / DRIVE` (same checks as the matrix, but with a real peer present)
- Client: `JOINED` (really in room), `LOCAL_BODY` (acquired a body owned/flagged local by its
  own actor), `FRESH_VIEW` (a fresh-range view is present). This is the cross-client check for
  concern #1/#2.

To run two instances on one machine, the harness needs a second full game copy with a
**renamed exe and `_Data` folder** (bypasses the game's single-instance guard):

```
SMP-Client\SupermarketSimulatorClient.exe      (renamed from "Supermarket Simulator.exe")
SMP-Client\SupermarketSimulatorClient_Data\    (renamed from "Supermarket Simulator_Data\")
```

Adjust the paths at the top of `RunTwoPlayerTest.ps1` to match your second copy.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\RunTwoPlayerTest.ps1 -Players 6 -Loop 2
# -> HOST  : POOL=PASS DOUBLE=PASS UNIQUE=PASS DRIVE=PASS
# -> CLIENT: JOINED=PASS LOCAL_BODY=PASS FRESH_VIEW=PASS
# -> TWO-PLAYER RESULT: PASS
```

Deterministic-ID note: a client can reliably create + register its own body whose ViewID is
`50000 + actorNumber` (every client computes the same ID for the same connection, and Photon
never reuses an actor number within a session, so it is rejoin-safe). This is the intended
fallback for the cross-client body when dynamic `AllocateViewID` IDs cannot be mirrored.

**3. Leave/rejoin scenarios** (`scripts/RunRejoinTest.ps1 -Scenario 1|2|0` and
`scripts/RunRejoinRealClients.ps1`)
Two ways to test that leave + rejoin never produce a stale/duplicate deterministic ID:

- `RunRejoinTest.ps1` (solo host, fabricated actors): drives the real `OnPlayerEnteredRoom` /
  `OnPlayerLeftRoom` with deterministic IDs keyed on actor number. Covers:
  - S1: P5 enters, P6 enters, P5 leaves, P5 rejoins
  - S2: P5 enters, P6 enters, P5 leaves, P6 leaves, P6 enters, P5 enters
  - asserts no stale/duplicate ViewID after each leave/rejoin.
- `RunRejoinRealClients.ps1` (real clients): host + 2 client copies on one machine, real
  Photon connect → kill (leave) → relaunch (rejoin, gets a NEW actor). Asserts the host
  assigned a deterministic body (`50000+actor`) on every enter AND reclaimed it on every
  leave (honest criterion: every assigned view must be reclaimed). Needs 3 game copies
  (host + two renamed `_Data` clients). PASS shown as:
  ```
  ensured deterministic body for actor 2 -> view 50002
  ReclaimBodyForActor: freeing view 50002
  ```

Both validate the invariant: a rejoin gets a NEW actor number, so its deterministic ID never
collides with a body still owned by another player.

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