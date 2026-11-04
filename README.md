# Easy Red 2 — Headless Dedicated Multiplayer Server (`v0.1.0`)

> [!IMPORTANT]
> **Disclosure — Vibecoded Project:**
> This mod was **vibecoded** (designed, reverse-engineered, and written collaboratively with an AI coding agent). If you are averse to AI-assisted or AI-generated software/mods, please be aware of this upfront before downloading or using this repository.

A **BepInEx 6 (IL2CPP)** Headless / Dedicated Multiplayer Server plugin (**supporting both PvP and Co-op**) and zero-duplication staging launcher for **Easy Red 2** (companion project to **[Easy Red 2 — Humanized AI & Veteran Tactics (`easy-red-2-ai`)](https://github.com/sudravirodhin/easy-red-2-ai)**).

Instead of forcing one player's game client to shoulder both **high-FPS client rendering** and **100% of the MasterClient AI/Physics simulation (`BattleManager`, `SpawnManager`, `SoldierAI`, `TurretAI`, `AIVehicle`, `Physics.Raycast`)**, this project runs a **stripped-down background instance of `Easy Red 2.exe`** on its own CPU cores with **near-zero GPU load**, acting as a dedicated Photon `MasterClient` so **every human player (across both Invader and Defender teams in PvP, or together in Co-op) connects as a pure client**.

---

## Compatibility & Target Environment

| Property | Details |
| :--- | :--- |
| **Target Branch** | Steam `public` (Default Branch) |
| **Confirmed Game Version** | **Easy Red 2 — Game Build `921`** (Steam Build ID `26375879`, Unity `2022.3.62f3` IL2CPP x64) |
| **Mod Loader** | **[BepInEx 6 Bleeding Edge (`BepInEx-Unity.IL2CPP-win-x64`, build `#791`+)](https://builds.bepinex.dev/projects/bepinex_be)** |
| **Sister AI Mod Support** | Native compatibility with **[`EasyRed2HumanAI.dll` (`v0.4.0`+)](https://github.com/sudravirodhin/easy-red-2-ai)** running on the headless host |

---

## Why Run a Headless Multiplayer / Co-op Host?

In *Easy Red 2*, Unity's `Transform`, `Physics.Raycast` (PhysX), `CharacterController`, and `SoldierAI` updates are all bound to the **Unity Main Thread**:
1. **Eliminates Host Client FPS Drops**: When you host a match normally, your client's single main thread must prepare every rendered frame (camera culling, skinning, animations, IK, particles, audio, UI) *and* simulate 100+ AI bots. Offloading the `MasterClient` role to a separate headless process frees your playing client to only interpolate network snapshots and render graphics.
2. **Eliminates Frame-Jitter Multiplayer Desyncs**: Photon PUN (`SyncSoldier`, `SyncVehicle`) packet pacing depends on the `MasterClient`'s update loop. Because the headless server disables cameras (`Camera.enabled = false`), mutes audio (`AudioListener.pause = true`), and runs at a locked tick rate (`45–60 Hz`) with `~0%` GPU load, connected players in both **PvP** and **Co-op** receive rock-steady network updates even during heavy artillery barrages.
3. **Full PvP & Co-op Team Slot Control**: Supports configuring `MatchData.mp_max_players_invaders` and `MatchData.mp_max_players_defenders` (e.g., balanced PvP across both factions, Asymmetric PvP, or pure Co-op on one side) plus friendly-fire/teamkill rules (`mp_allow_teamkillers`, `mp_kick_teamkillers`) and realism locks (`mp_allow_crosshair`, `mp_allow_hitmarker`, `mp_allow_tps`).
4. **Keeps Your Steam Game Folder 100% Pure Vanilla**: Using Windows Directory Junctions (`mklink /J`), the headless server runs from an isolated `< 150 MB` `server_instance\` folder that links read-only data (`Easy Red 2_Data`, `MonoBleedingEdge`) from your Steam installation while keeping `BepInEx`, `dotnet`, `winhttp.dll`, and server plugins completely separate from your normal Steam client.

---

## Core Architecture & Planned Features

### 1. Zero-Duplication Isolated Staging (`server_instance\`)
* **`build.ps1 -SetupServerInstance`**:
  * Creates an isolated `server_instance\` directory without duplicating the 15 GB game installation.
  * Links `Easy Red 2_Data` and `MonoBleedingEdge` via Windows Directory Junctions (`mklink /J`).
  * Installs BepInEx 6 BE (`#791`), `EasyRed2HeadlessServer.dll`, and optionally `EasyRed2HumanAI.dll` (`v0.4.0`) strictly inside `server_instance\`, leaving `Steam\steamapps\common\Easy Red 2` 100% untouched vanilla.

### 2. Dual-Instance Photon Cloud Authentication (`MultiplayerAuthenticator` & `MatchData`)
* Allows launching the Headless Server and your normal Steam client **on the same PC simultaneously**:
  * Configures a distinct Photon `AuthenticationValues` / `UserId` for the headless process so it never collides with your personal Steam session.
  * Automatically enables `MatchData.mp_allow_unverified_clients = true`, `MatchData.mp_voting_enabled = true`, and `MatchData.mp_visible = true`.

### 3. Extended Server Names (`> 8` Characters) & Automated Mission Rotation
* **Bypasses the 8-Character UI Name Cap (`ServerListMenu.maxServerNameLength` / `CampaignsTree.SanitizeRoomNameCr`)**:
  * Vanilla's "Create Match" UI restricts the text input box to 8 characters, even though `MatchData.room_name` is transmitted as a full C# string in Photon `RoomOptions.CustomRoomProperties` and rendered directly by `ServerListItem.AssignServer`.
  * `EasyRed2HeadlessServer` sets `MatchData.room_name` directly from `BepInEx\config\com.antigravity.easyred2.headless.cfg`, allowing descriptive server names in the vanilla Server Browser.
* **In-Game Map Voting & Mission Playlist**:
  * Automatically populates `MatchData.battles` with your configured mission rotation and enables native in-game Map Voting (`mp_voting_enabled = true`) so you and your friends can switch missions from inside your normal game clients.

### 4. "Ghost / Spectator" Host State & Zero-Render Loop
* Once the match starts (`BattleManager.instance`), the headless server:
  * Remains cleanly unspawned in `RespawnPanel.InRespawningView` so no AI squad or bot slot is occupied by an AFK host character while `BattleManager` and `SpawnManager` run the battle normally.
  * Disables all active `Camera` components, pauses `AudioListener`, shrinks to a minimal `160x90` window (or `-batchmode`), and locks `Application.targetFrameRate` to a steady `45–60` ticks per second.

---

## Building From Source

Run [`build.ps1`](build.ps1) from PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

---

## Related Projects
* **[Easy Red 2 — Humanized AI & Veteran Tactics (`easy-red-2-ai`)](https://github.com/sudravirodhin/easy-red-2-ai)** — Humanized vision cones, stance stealth, 15-minute veteran progression, 30 role personalities, smart wall-side cover, sheltered tank repairs, point-blank bayonet/melee CQB, and death screen kill attribution (`v0.4.0` Co-op & performance optimized).
