# MUSE WONDER HUD — RoboFactory AI

New command-centre HUD, built **next to** the existing interface.
No logistics script (MuseOrchestrator, robots, trucks, gateway, safety) was modified.

## Installation
Nothing to do: `MuseHudBootstrap` installs the HUD when any scene that contains
a `MuseOrchestrator` starts (e.g. `warehouse_robot_demo`). To tune the options in the inspector,
place a GameObject with `MuseHudRoot` in the scene (the bootstrap will not duplicate it).
To disable it: `MuseHudBootstrap.AutoInstall = false` or delete the `HUD` folder.

## Controls
| Key | Action |
|---|---|
| F2 / F3 / F4 | CINEMATIC / OPERATIONS (jury default) / DEBUG |
| H | hide / show the whole HUD |
| M | microphone (same button as the voice dock) |
| N | mute / unmute the sounds (remembered), also through the speaker icon |
| F1 | old IMGUI debug panel (unchanged, hidden at start) |
| F5 | back to the start screen (RECORDED DEMO / LIVE AI), scene reloaded clean |
| click an order card | details + highlight of its resources in the scene |
| click the vision window | enlarge / shrink |

## Architecture (folder `Assets/RoboFactory/HUD`, Assembly-CSharp)
- `Core/` — design system: `HudTheme` (colours, type, grid), `HudFonts` (system Bahnschrift),
  `HudSprites` (anti-aliased procedural sprites), `HudFactory` (components), `HudInput`,
  `LegacyUiController` (hides the old HUD through public flags, restored in DEBUG).
- `Data/` — `HudDataSource` (read-only access to the existing systems → ViewModels), `HudModel`
  (ViewModels), `MicLevelProbe` (real microphone level).
- `Graphics/` — vector uGUI Graphics: arcs, ticks, polylines, corner brackets, segments.
- `Views/` — presentation and animations: Muse circle, Orders, AI Decisions, robot vision,
  trucks, voice dock, banners, status bar, top bar, DEBUG console.
- `World/` — 3D markers on reserved resources, real paths, anchored labels, robot cameras.
- `MuseHudRoot` — canvas, responsive layout, modes, data ↔ views binding.

uGUI + TextMeshPro was chosen: the project only had IMGUI, uGUI is already present
(com.unity.ugui 2.0) and allows custom vector Graphics and simple RenderTextures.

## Data sources (nothing is invented)
| Element | Source |
|---|---|
| circle state | `MuseOrchestrator.Stage / Pending / UrgentPending / ActiveCount`, `VoiceCommandController.State` |
| orders | `MuseOrchestrator.OrdersSnapshot` |
| decision, reason, model, latency, verdict | `MuseOrchestrator.LastDecision / History` (`MuseDecisionRecord`) |
| pallets/bench/truck of the decision | `PlanSummary` produced by `MuseOrchestrator.Describe` |
| 3D markers | real reservations `WarehouseWorld.ReservedBy` + `TruckBay.ReservedOrderId` |
| paths | `WarehouseRobotAutopilot.CurrentPath`, `TruckLoaderAgent.Mover.CurrentPath` |
| robots | `Status`, `MissionActive`, `ActiveLeg`, `CurrentTarget`, `R2State`, carried box |
| truck doors | `TruckBay.Door`, `LoadedCount / Capacity` |
| safety | `MuseOrchestrator.MinRobotWorkerDistance` vs `WorkerProximityAlert` |
| Meta link | `ModelUsed`, `LastLatencyMs`, `GET /health` of the gateway (read-only) |
| DEBUG logs | `Application.logMessageReceived` |
| robot vision | on-board camera → RenderTexture (10 fps / 384×216, 20 fps / 768×432 enlarged) |

Visual detection: no detector is connected to R1/R2 → the window shows
« DETECTION NOT CONNECTED » and no box. The target shown is the real navigation target.

## Layout
Reference 1920×1080 (CanvasScaler, match height). Beyond 21:9 (e.g. 32:9) the interface stays
in a central 21:9 area; on multi-screen (12288×2160) in a central 16:9 area = 3840×2160.

## Sounds (folder `Audio/`)
Synthesised at start by `HudSoundSynth` (no imported audio file), played in 2D by
`HudAudio` (adjustable volume: `MuseHudRoot.SoundVolume`). They only follow real events:

| Sound | Real trigger |
|---|---|
| click | HUD button |
| sweep | mode change |
| rising / falling beep | microphone opened / closed (`VoiceCommandController`) |
| data ticks | final transcription received |
| ping | request sent to the Meta gateway (`Pending`) |
| 3-note bell | Muse decision accepted |
| two falling notes | decision refused by the validator |
| impact + two-tone alarm | entering urgent mode (`Stage` Suspending / UrgentActive) |
| rising arpeggio | operations resuming (`Resuming` / end of urgent order) |
| low tones | urgent pipeline failure (`Failed`) |
| success chord | order switched to `loaded` |
| mechanical breath | truck door `Opening` / `Closing` |
| double beep | safety switching to ALERT |
