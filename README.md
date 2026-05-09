# Supermarket More Players

BepInEx IL2CPP plugin source for testing higher co-op limits in Supermarket Simulator.

## What the game analysis found

This game build is IL2CPP. The managed assembly names are listed in `Supermarket Simulator_Data/ScriptingAssemblies.json`, and the metadata confirms Photon PUN plus Facepunch Steamworks:

- `PhotonRealtime.dll`
- `PhotonUnityNetworking.dll`
- `Photon3Unity3D.dll`
- `Facepunch.Steamworks.Win64.dll`

The important game-side multiplayer types exposed by `global-metadata.dat` are:

- `LobbyManager`
  - Source path in metadata: `Assets/- Project -/Scripts/Multiplayer/LobbyManager.cs`
  - Relevant methods: `CreateRoom`, `JoinTargetRoom`, `ExternalJoinTargetRoom`, `ConnectToServer`, `ConnectToRegion`, `OnConnectedToMaster`
  - Relevant field: `m_MaxPlayersPerRoom`
- `NetworkManager`
  - Source path in metadata: `Assets/- Project -/Scripts/Multiplayer/Managers/NetworkManager.cs`
  - Relevant methods/fields: `OnLoadingDone`, `InitialLoadingUIRoutine`, `InitialLoadingWarningRoutine`, `UpdatePlayerList`, `SetupNewPlayerVisuals`, `m_MaxWaitTimeForJoiningPlayers`
- `RoomSettingsManager`
  - Source path in metadata: `Assets/- Project -/Scripts/Multiplayer/RoomSettingsManager.cs`
  - Relevant methods/fields: `UpdateList`, `ClearPlayerPrefabList`, `RemoveFromUIList`, `_roomInfoPlayers`

That means the likely cap is not just a Photon constant. The mod patches both:

- Photon room creation methods, by forcing `RoomOptions.MaxPlayers`.
- Facepunch/Steamworks lobby creation methods, by forcing max member arguments when those APIs are present.

It also sets `LobbyManager.m_MaxPlayersPerRoom` before the game's own room methods run, because the game already has a private field for this value.

## Behavior

Default config:

```ini
[General]
MaxPlayers = 8
PatchSteamLobby = true
ShrinkPhotonRoomWhenClosed = true

[Diagnostics]
VerboseLogging = true
```

The shrink option follows the same idea used by the R.E.P.O. MorePlayers mod: allow a larger lobby before the run starts, then when the host closes the Photon room, reduce `CurrentRoom.MaxPlayers` to the actual connected player count. This is meant to avoid loading flows that wait for empty player slots.

## Build on Windows

1. Install the .NET SDK.

   Check it from PowerShell:

   ```powershell
   dotnet --version
   ```

2. Install BepInEx 6 IL2CPP into the Supermarket Simulator game folder.

   The folder should look like this:

   ```text
   Supermarket Simulator/
     BepInEx/
       core/
     Supermarket Simulator.exe
     GameAssembly.dll
     Supermarket Simulator_Data/
   ```

3. Launch the game once with BepInEx installed.

   This is required because BepInEx generates the interop assemblies the mod builds against:

   ```text
   BepInEx/interop/
   ```

4. Build the mod from this source folder:

   ```powershell
   cd "C:\path\to\Supermarket Simulator\MorePlayersBepInEx"
   dotnet build -c Release
   ```

   By default, the project expects BepInEx at:

   ```text
   ..\BepInEx
   ```

   If your BepInEx folder is somewhere else, pass it explicitly:

   ```powershell
   dotnet build -c Release -p:BepInExDir="C:\path\to\Supermarket Simulator\BepInEx"
   ```

5. Install the built plugin.

   Create this folder if needed:

   ```text
   BepInEx\plugins\SupermarketMorePlayers\
   ```

   Then copy:

   ```text
   MorePlayersBepInEx\bin\Release\net6.0\SupermarketMorePlayers.dll
   ```

   into:

   ```text
   BepInEx\plugins\SupermarketMorePlayers\SupermarketMorePlayers.dll
   ```

6. Start the game and edit the generated config after the first successful load:

```powershell
notepad "BepInEx\config\dev.codex.supermarket.moreplayers.cfg"
```

Start with `MaxPlayers = 5` for the first test.

## Test plan

Start with `MaxPlayers = 5`, then test 6 and 8. Watch the BepInEx log for:

- Patched Photon room creation methods.
- Patched Steamworks lobby creation methods.
- `LobbyManager.m_MaxPlayersPerRoom forced`.
- `CurrentRoom: PlayerCount=..., MaxPlayers=..., IsOpen=...`.
- `Room closed; Photon CurrentRoom.MaxPlayers shrunk`.

If a fifth player can enter but loading or spawning breaks, the next likely fixes are in `NetworkManager`, `PlayerManager.GetAvailableSkinIndex`, spawn positions, and `RoomSettingsManager` UI assumptions.
