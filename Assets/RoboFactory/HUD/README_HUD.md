# HUD MUSE WONDER — RoboFactory AI

Nouveau HUD du centre de commandement, construit **à côté** de l'interface existante.
Aucun script logistique (MuseOrchestrator, robots, camions, proxy, sécurité) n'a été modifié.

## Installation
Rien à faire : `MuseHudBootstrap` installe le HUD au lancement de toute scène qui contient
un `MuseOrchestrator` (ex. `warehouse_robot_demo`). Pour régler les options dans l'inspecteur,
placer un GameObject avec `MuseHudRoot` dans la scène (le bootstrap ne le dupliquera pas).
Pour le désactiver : `MuseHudBootstrap.AutoInstall = false` ou supprimer le dossier `HUD`.

## Commandes
| Touche | Action |
|---|---|
| F2 / F3 / F4 | CINEMATIC / OPERATIONS (défaut jury) / DEBUG |
| H | masquer / afficher tout le HUD |
| M | micro (même bouton que le dock vocal) |
| N | couper / rétablir les sons (mémorisé), aussi via l'icône haut-parleur |
| F1 | ancien panneau de debug IMGUI (inchangé, masqué au démarrage) |
| clic carte commande | détails + mise en avant de ses ressources dans la scène |
| clic fenêtre vision | agrandir / réduire |

## Architecture (dossier `Assets/RoboFactory/HUD`, Assembly-CSharp)
- `Core/` — design system : `HudTheme` (couleurs, typo, grille), `HudFonts` (Bahnschrift système),
  `HudSprites` (sprites procéduraux anti-aliasés), `HudFactory` (composants), `HudInput`,
  `LegacyUiController` (masquage de l'ancien HUD par drapeaux publics, restaurés en DEBUG).
- `Data/` — `HudDataSource` (lecture seule des systèmes existants → ViewModels), `HudModel`
  (ViewModels), `MicLevelProbe` (vrai niveau micro).
- `Graphics/` — Graphics uGUI vectoriels : arcs, graduations, polylignes, équerres, segments.
- `Views/` — présentation et animations : cercle Muse, Orders, AI Decisions, vision robots,
  camions, dock vocal, bandeaux, barre d'état, barre haute, console DEBUG.
- `World/` — repères 3D sur ressources réservées, chemins réels, étiquettes ancrées, caméras robots.
- `MuseHudRoot` — canvas, mise en page responsive, modes, liaison données ↔ vues.

uGUI + TextMeshPro a été retenu : le projet n'avait que de l'IMGUI, uGUI est déjà présent
(com.unity.ugui 2.0) et permet des Graphics vectoriels sur mesure et des RenderTextures simples.

## Origine des données (rien n'est inventé)
| Élément | Source |
|---|---|
| état du cercle | `MuseOrchestrator.Stage / Pending / UrgentPending / ActiveCount`, `VoiceCommandController.State` |
| commandes | `MuseOrchestrator.OrdersSnapshot` |
| décision, raison, modèle, latence, verdict | `MuseOrchestrator.LastDecision / History` (`MuseDecisionRecord`) |
| palettes/banc/camion de la décision | `PlanSummary` produit par `MuseOrchestrator.Describe` |
| repères 3D | réservations réelles `WarehouseWorld.ReservedBy` + `TruckBay.ReservedOrderId` |
| chemins | `WarehouseRobotAutopilot.CurrentPath`, `TruckLoaderAgent.Mover.CurrentPath` |
| robots | `Status`, `MissionActive`, `ActiveLeg`, `CurrentTarget`, `R2State`, carton porté |
| portes camions | `TruckBay.Door`, `LoadedCount / Capacity` |
| sécurité | `MuseOrchestrator.MinRobotWorkerDistance` vs `WorkerProximityAlert` |
| liaison Meta | `ModelUsed`, `LastLatencyMs`, `GET /health` du proxy (lecture seule) |
| logs DEBUG | `Application.logMessageReceived` |
| vision robots | caméra embarquée → RenderTexture (10 fps / 384×216, 20 fps / 768×432 agrandie) |

Détection visuelle : aucun détecteur n'est branché sur R1/R2 → la fenêtre affiche
« DETECTION NOT CONNECTED » et aucune boîte. La cible affichée est la cible de navigation réelle.

## Mise en page
Référence 1920×1080 (CanvasScaler, match hauteur). Au-delà de 21:9 (ex. 32:9) l'interface reste
dans une zone centrale 21:9 ; en multi-écran (12288×2160) dans une zone centrale 16:9 = 3840×2160.

## Sons (dossier `Audio/`)
Synthétisés au démarrage par `HudSoundSynth` (aucun fichier audio importé), joués en 2D par
`HudAudio` (volume réglable : `MuseHudRoot.SoundVolume`). Ils ne suivent que des événements réels :

| Son | Déclencheur réel |
|---|---|
| clic | bouton du HUD |
| balayage | changement de mode |
| bip montant / descendant | micro ouvert / fermé (`VoiceCommandController`) |
| ticks de données | transcription finale reçue |
| ping | requête envoyée au proxy Meta (`Pending`) |
| cloche à 3 notes | décision Muse acceptée |
| deux notes descendantes | décision refusée par le validateur |
| impact + alarme bi-tonale | entrée en urgence (`Stage` Suspending / UrgentActive) |
| arpège montant | reprise des opérations (`Resuming` / fin d'urgence) |
| tons graves | échec du pipeline urgent (`Failed`) |
| accord de réussite | commande passée à `loaded` |
| souffle mécanique | porte de camion en `Opening` / `Closing` |
| double bip | sécurité qui passe en ALERT |
