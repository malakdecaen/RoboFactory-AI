# RoboFactory AI

**Un entrepôt autonome orchestré par Muse Wonder.** Deux robots (R1 prépare, R2 charge), des palettes, des bancs de préparation et des camions ; un modèle Meta décide en temps réel de chaque commande, des commandes urgentes dictées à la voix et de la libération des quais quand tout est saturé.

*An autonomous warehouse orchestrated by Meta's Muse Wonder — English summary below.*

---

## Ce que fait le projet

- **Décisions Muse Wonder** : pour chaque commande, le modèle choisit les palettes à prélever, le banc de préparation, le camion et l'ordre des tâches. Chaque plan est validé côté Unity (stock réel, réservations, capacité) avant exécution — aucune décision n'est inventée localement.
- **Repli Muse Spark** : si Wonder ne répond pas à temps (ou erreur 404 / 408 / 429 / 5xx), la même demande part sur Spark ; le modèle réellement utilisé est toujours affiché.
- **Commandes urgentes à la voix** (Muse Voice, transcription temps réel) : « commande urgente, deux cartons B » interrompt proprement les commandes en cours (aucun carton perdu ni posé au sol), exécute l'urgente, puis reprend les autres dans l'ordre.
- **FINISH TO FREE** : quand les 3 bancs et les 3 camions sont pris, Muse choisit la commande à terminer en premier pour libérer la place le plus vite (ETA calculée sur les distances réelles).
- **HUD temps réel** : décisions Muse, commandes, état des robots et des ressources, vue orbite, journal de flux qui repère les étapes inutiles.
- **Serveur Meta intégré au jeu** (C#) : aucun Python ni exécutable externe ; la clé API reste dans le processus.

## Lancer la version publique (formes simplifiées)

Cette version remplace les assets du commerce par des cubes et des cylindres : **le comportement est le même** (mêmes positions, mêmes zones d'arrimage, mêmes réglages).

1. Unity **6000.3.10f1** (URP).
2. Ouvrir le dossier du dépôt comme projet Unity.
3. Menu **RoboFactory → Public → Créer la scène simplifiée** (génère `Assets/RoboFactorySim/Scenes/warehouse_simplified.unity`).
4. Clé Meta API : variable d'environnement `MODEL_API_KEY`, ou fichier `%USERPROFILE%\.meta\model_api_key`.
5. Play. Les commandes se créent depuis le HUD ; le micro sert aux commandes urgentes.

Build Windows : menu **RoboFactory → Build → Build Windows (jury)** (sortie dans `Builds/`).

## Version complète (build de la Release)

La Release GitHub contient le build Windows avec les assets d'origine. Pour recompiler la version complète, importer ces assets dans le projet (non redistribuables dans ce dépôt, licence de l'Asset Store) :

- **Warehouse Robot Controller** (robots, palettes, bancs, cartons) — https://assetstore.unity.com/packages/3d/characters/robots/warehouse-robot-controller-389700
- **Unity Factory** (environnement de l'usine) — https://assetstore.unity.com/packages/3d/environments/industrial/unity-factory-276400
- **Factory Training — Industry Sample** (camions, employés, décor) — https://assetstore.unity.com/packages/templates/packs/factory-training-industry-sample-344832

## Architecture

| Dossier | Rôle |
|---|---|
| `Assets/RoboFactory/Logistics` | Orchestrateur Muse (`MuseOrchestrator`), monde de l'entrepôt (réservations, stocks), robots R2 / camions, urgences, FINISH TO FREE, journaux |
| `Assets/RoboFactory/Integration` | Pilote de R1 (prélèvement palette → banc) |
| `Assets/RoboFactory/MetaServer` | Serveur Meta intégré (Wonder / Spark / Voice / SAM 3.1 / vision), même API HTTP locale que l'ancien proxy |
| `Assets/RoboFactory/HUD` | Interface temps réel construite en code |
| `Assets/RoboFactory/Scripts` | Contrats d'orchestration (validation des plans, reprise après urgence), client Meta |
| `Assets/RoboFactorySim` | Version publique : composants de remplacement des assets du commerce + constructeur de la scène en cubes |

Modèles Meta utilisés : `muse-wonder-1.0` (orchestration), `muse-spark-1.3` (repli, vision), `muse-voice-transcribe-1.0` (voix), `sam-3.1` (segmentation).

---

## English summary

RoboFactory AI is a Unity 6 (URP) warehouse simulation where **Muse Wonder** plans every order (pallet picks, staging bench, truck, task order), **Muse Spark** takes over when Wonder is unavailable, **Muse Voice** turns spoken urgent orders into prioritized missions, and a **FINISH TO FREE** policy frees a loading bay when all three are busy. Every plan is validated against the real simulated state before robots execute it. The Meta gateway runs inside the game (C#), no external server.

This public repository replaces third-party store assets with simple shapes (same behaviour). Open the project in Unity 6000.3.10f1, run **RoboFactory → Public → Créer la scène simplifiée**, set `MODEL_API_KEY`, press Play. The full-art Windows build is attached to the GitHub Release; the store assets it uses are listed above (Warehouse Robot Controller, Unity Factory, Factory Training — Industry Sample).

## Licence

Code : MIT (voir `LICENSE`). Les assets du commerce utilisés par la version complète restent la propriété de leurs auteurs et ne sont pas inclus.
