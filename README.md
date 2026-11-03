# RoboFactory AI

**An autonomous warehouse run by Meta's models, from the production line to the truck.** A packing cell inspects every product with Muse Spark and packs the good ones into cartons; a third robot (R3) brings the cartons to the stock pallets; Muse Wonder plans every order for R1 (picking) and R2 (loading); Muse Spark and Muse Image write and draw the shipping label of each order; SAM 3.1 checks what the robot cameras see; Muse Voice takes urgent orders spoken aloud.

---

## What it does

### Packing line (upstream)

- **Products on a conveyor loop** (bottles, mugs, balls) come out of a black box. A **scan camera** sends each product's picture to **Muse Spark**, which answers the kind and any visible defect (cracked, dented, stained, broken) with a confidence. The cell decides from that answer only: defect → the arm drops it in the reject bin; good and needed → into its carton (A: 4 bottles, B: 4 mugs, C: 4 balls); unsure → rescanned on the next lap. The simulation's own truth is only logged afterwards to measure the scan (36 of 38 scans agreed with it in our last test run).
- **The belt never stops**: the arm catches each product on the fly, following it as it comes down on it.
- **Full cartons** close into the same boxes the warehouse uses; **robot R3** takes them from the trays with the robot's own load / unload gestures and stacks them on pallets P07 / P08, which become stock for R1.
- **HUD**: the very picture sent to Spark, its answer and the cell's verdict (OK → CARTON B, DEFECT → BIN…).

### Warehouse (orders → trucks)

- **Muse Wonder decisions**: for each order, the model picks the pallets to collect from, the staging bench, the truck and the task order. Every plan is validated in Unity against the real simulated state (stock, reservations, capacity) before the robots execute it. No decision is ever invented locally.
- **Muse Spark fallback**: if Wonder does not answer in time (or returns 404 / 408 / 429 / 5xx), the same request is sent to Spark. The model actually used is always displayed.
- **Urgent voice orders** (Muse Voice, real-time transcription): "urgent order, two B boxes" safely pauses the orders in progress (no box is lost or dropped on the floor), runs the urgent order, then resumes the others in order.
- **FINISH TO FREE**: when all 3 benches and all 3 trucks are busy, Muse chooses which order to finish first to free a bay as fast as possible (ETA computed from real travel distances).
- **SAM 3.1 on the robot cameras**: R1 and R2 send what their on-board camera sees to Meta SAM 3.1 when R1 takes a box from a pallet or puts one on a bench, when R2 reaches a bench and after it drops a box in a truck ("box"), and when an employee is in front of a moving robot ("person"). The word sent to SAM follows the boxes expected there ("cardboard box", "wooden crate") and only the objects inside the work zone (the bench / truck / pallet slots projected into the image) are counted. The boxes SAM returns are drawn in the ROBOT VISION windows with their confidence, next to what the simulation knows at that place ("scene"). **Order check**: after each box R2 drops in a truck, SAM counts the boxes in the trailer; when the order is complete and SAM sees exactly the boxes the truck holds, the HUD shows "ORDER #n VERIFIED", otherwise "CHECK" with both counts. SAM never drives the robots: the emergency stop stays local, because a ~2 s answer is too slow for safety.
- **Shipping labels (Muse Spark + Muse Image)**: as soon as an order is planned, Muse Spark invents its fictional consignee (name, street, town) and a short description of the place; **Muse Image** draws the label's vignette from it. Every box R1 puts on the bench gets the label (SHIP TO, order, truck, parcel n/N, barcode) before R2 loads it. If Meta does not answer, a printed text-only label is used.
- **Real-time HUD**: Muse decisions, orders, robot and resource states, orbit camera, and a flow log that flags wasted steps.
- **Meta gateway built into the game** (C#): no Python, no external executable; the API key never leaves the process.

## Run the public version (simplified shapes)

This version replaces the commercial store assets with cubes and cylinders: **the behaviour is the same** (same positions, same docking zones, same settings).

1. Unity **6000.3.10f1** (URP).
2. Open the repository folder as a Unity project.
3. Open `Assets/RoboFactorySim/Scenes/warehouse_simplified.unity` (or rebuild it with **RoboFactory → Public → Build simplified scene**).
4. Press Play: the start screen asks for a mode (see below).

Windows build: **RoboFactory → Build → Build Windows** (output in `Builds/`).

## Two ways to run: RECORDED DEMO and LIVE AI

At launch a start screen offers two modes; the HUD always shows which one is running.

| | RECORDED DEMO (default) | LIVE AI |
|---|---|---|
| API key / Internet | none | your own Meta Model API key |
| Muse decisions | real Meta answers recorded earlier, replayed unchanged (model, plan, reason and latency as recorded) | real calls to Muse Wonder (Spark fallback) |
| Robots, boxes, doors, trucks | really simulated in Unity, every plan is still validated against the live state | same |
| Voice | disabled | microphone for urgent orders, free typed orders |

**RECORDED DEMO** plays one scenario, *Full saturation*: 5 orders fill the 3 benches and 3 trucks, then 1 spoken urgent order makes Muse free a bay (FINISH TO FREE) and the suspended orders resume. The packing line runs at the same time: its product scans and the shipping labels (consignees and Muse Image vignettes) are replayed from the same recording. It starts directly. The scene is reloaded before each run, and a cargo audit at the end checks that no box was lost or duplicated.

**LIVE AI** asks for your key, checks it with real requests to Muse Wonder and Muse Spark, and keeps it **in memory only** (never written to a file, a log or a recording). Without typing it you can also use the `MODEL_API_KEY` environment variable or the file `%USERPROFILE%\.meta\model_api_key`.

### Recording the demos yourself

Recordings are only made from real Meta answers: in LIVE AI choose *Record a scenario* and let it run to the end. In the editor a complete recording replaces the demo at once (`Assets/StreamingAssets/RecordedDemos/saturation.json`, shipped with the build; the previous one is overwritten). **RoboFactory → Recorded Demo → Promote captured recordings** does the same from the captures folder. Without it, RECORDED DEMO shows *NOT RECORDED YET*. Press **F5** during a run to return to the start screen.

## Full version (Release build)

The GitHub Release contains the Windows build with the original art. To rebuild the full version yourself, import these assets into the project (they cannot be redistributed in this repository under the Asset Store license):

- **Warehouse Robot Controller** (robots, pallets, benches, boxes) — https://assetstore.unity.com/packages/3d/characters/robots/warehouse-robot-controller-389700
- **Unity Factory** (factory environment) — https://assetstore.unity.com/packages/3d/environments/industrial/unity-factory-276400
- **Factory Training — Industry Sample** (trucks, workers, props) — https://assetstore.unity.com/packages/templates/packs/factory-training-industry-sample-344832

## Architecture

| Folder | Role |
|---|---|
| `Assets/RoboFactory/Logistics` | Muse orchestrator (`MuseOrchestrator`), warehouse world (reservations, stock), R2 loader and trucks, urgent orders, FINISH TO FREE, shipping labels, session logs |
| `Assets/RoboFactory/Packing` | Packing line: conveyor, products and defects, scan (Muse Spark), arm (IK of the full project / procedural arm), cartons, transport robot R3, stock pallets P07 / P08 |
| `Assets/RoboFactory/Integration` | R1 autopilot (pallet → bench picking) |
| `Assets/RoboFactory/MetaServer` | In-game Meta gateway (Wonder / Spark / Voice / SAM 3.1 / vision), same local HTTP API as the original proxy |
| `Assets/RoboFactory/HUD` | Real-time interface built in code |
| `Assets/RoboFactory/Launcher` | Start screen (RECORDED DEMO / LIVE AI), scenario player, cargo audit, recording promotion menu |
| `Assets/RoboFactory/Scripts` | Orchestration contracts (plan validation, resume after an urgent order), Meta client |
| `Assets/RoboFactorySim` | Public version: replacement components for the store assets + the simplified scene builder |

Meta models used:

| Model | Role |
|---|---|
| `muse-wonder-1.0` | orchestration of every order, urgent orders, FINISH TO FREE |
| `muse-spark-1.3` | fallback of Wonder; product inspection on the packing line; consignee of each order |
| `muse-image-1.0` | vignette of the shipping labels |
| `sam-3.1` | segmentation of the robot cameras (boxes, people, confidence; masks requested in the compact encoding, not decoded) |
| `muse-voice-transcribe-1.0` | real-time transcription of urgent orders spoken aloud |

**Muse Glimmer (optional, not included)**: Glimmer is an open-weight, text-only model that teams can run on their own hardware. The gateway reads the API base URL (`META_BASE_URL`) and the fallback model name (`META_ORCHESTRATOR_FALLBACK`) from environment variables, so a self-hosted Glimmer behind a compatible endpoint can be tried for the text decisions. The project does not ship, require or test it: Muse Spark remains the fallback.

## License

Code: MIT (see `LICENSE`). The store assets used by the full version remain the property of their authors and are not included.
