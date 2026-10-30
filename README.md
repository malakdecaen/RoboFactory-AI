# RoboFactory AI

**An autonomous warehouse orchestrated by Meta's Muse Wonder.** Two robots (R1 prepares, R2 loads), pallets, staging benches and trucks; a Meta model decides every order in real time, handles urgent orders spoken by voice, and frees a loading bay when everything is saturated.

---

## What it does

- **Muse Wonder decisions**: for each order, the model picks the pallets to collect from, the staging bench, the truck and the task order. Every plan is validated in Unity against the real simulated state (stock, reservations, capacity) before the robots execute it. No decision is ever invented locally.
- **Muse Spark fallback**: if Wonder does not answer in time (or returns 404 / 408 / 429 / 5xx), the same request is sent to Spark. The model actually used is always displayed.
- **Urgent voice orders** (Muse Voice, real-time transcription): "urgent order, two B boxes" safely pauses the orders in progress (no box is lost or dropped on the floor), runs the urgent order, then resumes the others in order.
- **FINISH TO FREE**: when all 3 benches and all 3 trucks are busy, Muse chooses which order to finish first to free a bay as fast as possible (ETA computed from real travel distances).
- **Real-time HUD**: Muse decisions, orders, robot and resource states, orbit camera, and a flow log that flags wasted steps.
- **Meta gateway built into the game** (C#): no Python, no external executable; the API key never leaves the process.

## Run the public version (simplified shapes)

This version replaces the commercial store assets with cubes and cylinders: **the behaviour is the same** (same positions, same docking zones, same settings).

1. Unity **6000.3.10f1** (URP).
2. Open the repository folder as a Unity project.
3. Open `Assets/RoboFactorySim/Scenes/warehouse_simplified.unity` (or rebuild it with **RoboFactory → Public → Build simplified scene**).
4. Meta API key: environment variable `MODEL_API_KEY`, or the file `%USERPROFILE%\.meta\model_api_key`.
5. Press Play. Create orders from the HUD; use the microphone for urgent orders.

Windows build: **RoboFactory → Build → Build Windows** (output in `Builds/`).

## Full version (Release build)

The GitHub Release contains the Windows build with the original art. To rebuild the full version yourself, import these assets into the project (they cannot be redistributed in this repository under the Asset Store license):

- **Warehouse Robot Controller** (robots, pallets, benches, boxes) — https://assetstore.unity.com/packages/3d/characters/robots/warehouse-robot-controller-389700
- **Unity Factory** (factory environment) — https://assetstore.unity.com/packages/3d/environments/industrial/unity-factory-276400
- **Factory Training — Industry Sample** (trucks, workers, props) — https://assetstore.unity.com/packages/templates/packs/factory-training-industry-sample-344832

## Architecture

| Folder | Role |
|---|---|
| `Assets/RoboFactory/Logistics` | Muse orchestrator (`MuseOrchestrator`), warehouse world (reservations, stock), R2 loader and trucks, urgent orders, FINISH TO FREE, session logs |
| `Assets/RoboFactory/Integration` | R1 autopilot (pallet → bench picking) |
| `Assets/RoboFactory/MetaServer` | In-game Meta gateway (Wonder / Spark / Voice / SAM 3.1 / vision), same local HTTP API as the original proxy |
| `Assets/RoboFactory/HUD` | Real-time interface built in code |
| `Assets/RoboFactory/Scripts` | Orchestration contracts (plan validation, resume after an urgent order), Meta client |
| `Assets/RoboFactorySim` | Public version: replacement components for the store assets + the simplified scene builder |

Meta models used: `muse-wonder-1.0` (orchestration), `muse-spark-1.3` (fallback, vision), `muse-voice-transcribe-1.0` (voice), `sam-3.1` (segmentation).

## License

Code: MIT (see `LICENSE`). The store assets used by the full version remain the property of their authors and are not included.
