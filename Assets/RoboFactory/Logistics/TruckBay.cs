using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Baie de chargement du camion.
    ///
    /// Deux modes :
    ///   - Station existante : si la remorque contient déjà un ShelfStorage avec des
    ///     ShelfInteractionZone (ex. un "pallet depot"), le composant l'utilise tel quel —
    ///     comptage, validation et indicateurs se basent sur ses niveaux.
    ///   - Auto : sinon il génère slots + ShelfStorage + zone de dock juste derrière la porte.
    ///
    /// Dans les deux cas il ajoute des colliders fins sur les parois (porte = extrémité
    /// des bounds la plus proche de l'origine de la scène, elle reste ouverte), un feu de
    /// signalisation et des marqueurs de slot (vert = occupé).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TruckBay : MonoBehaviour
    {
        [Header("Station existante (optionnel)")]
        [Tooltip("Stockage déjà présent dans la remorque (ex. palette depot). " +
                 "Vide = détection automatique des ShelfStorage enfants, sinon slots auto-générés.")]
        public ShelfStorage CustomStorage;

        [Header("Disposition des slots (mode auto)")]
        public int SlotsDepth = 4;
        public int SlotsWide = 2;
        public int StackLevels = 1;
        public Vector3 SlotSpacing = new Vector3(.75f, .7f, .85f);
        public float SlotLift = .12f;

        [Header("Dock (mode auto)")]
        public float DockOffset = .8f;
        public Vector3 DockZoneSize = new Vector3(1.4f, 1.6f, 1.4f);

        [Header("Colliders de paroi")]
        public bool AutoWallColliders = true;
        public float WallThickness = .12f;

        [Header("Signal")]
        public bool ShowIndicators = true;
        public Color SlotFreeColor = new Color(1f, .65f, .1f, .3f);
        public Color SlotUsedColor = new Color(.15f, 1f, .35f, .5f);

        [Header("Porte (commande terminée)")]
        [Tooltip("Transform de la porte. Vide = recherche automatique de 'gate_01_GRP' dans la remorque.")]
        public Transform Gate;
        public string GateName = "gate_01_GRP";
        [Tooltip("Y local de la porte une fois fermée.")]
        public float GateClosedY = .08f;
        [Tooltip("Vitesse de coulissement de la porte (m/s).")]
        public float GateSpeed = 1.5f;
        [Tooltip("Au démarrage la porte descend en position fermée (obligatoire pour l'orchestration Muse).")]
        public bool StartClosed = true;
        [Tooltip("Sans orchestrateur dans la scène, les portes s'ouvrent seules au démarrage (comportement démo historique).")]
        public bool AutoOpenIfUnmanaged = true;
        [Tooltip("Une fois la porte refermée sur une commande chargée, les cartons sont détruits (expédition).")]
        public bool ClearAfterLoad = true;
        [Tooltip("Quand la commande est complète : la porte ferme, les cartons déposés sont détruits, puis la porte rouvre.")]
        public bool CloseWhenComplete = true;
        [Tooltip("Secondes sans travail en cours (bancs vides + robot sans carton) pour considérer la commande terminée.")]
        public float IdleCloseDelay = 5f;
        public bool ReopenAfterClear = true;

        /// <summary>Un carton vient d'être validé dans le camion.</summary>
        public event System.Action<CargoBox> Stored;

        public ShelfStorage Storage { get; private set; }
        public ShelfInteractionZone DockZone { get; private set; }
        public Transform DockPoint { get; private set; }
        public int Capacity { get; private set; }
        public int LoadedCount { get; private set; }
        public int FreeSlots => Capacity - LoadedCount;
        public bool IsFull => Capacity > 0 && LoadedCount >= Capacity;
        /// <summary>Vrai si la baie réutilise un stockage existant au lieu de ses slots générés.</summary>
        public bool ExternalMode { get; private set; }

        private readonly List<Transform> slots = new List<Transform>();
        private readonly List<Renderer> slotMarkers = new List<Renderer>();
        private Transform slotsRoot;
        private float baseSurfaceY;
        private Light beacon;
        private Renderer beaconBulb;
        private bool built;
        private float beaconUntil;
        private bool beaconGreen;
        private float gateOpenY;
        private TruckLoaderAgent loader;
        private float nextLoaderFind;
        private float quietSince;

        // ---- porte pilotée (orchestration Muse) ---------------------------------
        /// <summary>FSM de la porte arrière. Closed au départ ; l'orchestrateur la pilote.</summary>
        public enum TruckDoorState { Closed, Opening, Open, Ready, Loading, Closing, Loaded }
        public TruckDoorState Door { get; private set; } = TruckDoorState.Closed;
        /// <summary>Commande propriétaire de la baie (-1 = aucune réservation).</summary>
        public int ReservedOrderId { get; private set; } = -1;
        /// <summary>La porte est physiquement ouverte : un robot peut entrer.</summary>
        public bool IsDoorOpen => Door == TruckDoorState.Open || Door == TruckDoorState.Ready ||
                                  Door == TruckDoorState.Loading;
        /// <summary>Compte des cartons chargés par commande (survit à la destruction des objets).</summary>
        public int LastLoadedOrderId { get; private set; } = -1;
        public int LastLoadedCount { get; private set; }
        private NavBlocker doorBlocker;
        private bool managed;         // un orchestrateur pilote cette baie
        private float unmanagedOpenAt;

        private void Awake()
        {
            EnsureBuilt();
        }

        private void Update()
        {
            if (!built) EnsureBuilt();
            UpdateLevelHeights();
            RefreshIndicators();
            UpdateBeacon();
            UpdateGate();
        }

        // ------------------------------------------------------------------ construction

        private Bounds InteriorBounds()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(transform.position, Vector3.one);
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }

        /// <summary>
        /// Extrémité ouverte de la remorque : le bord des bounds le plus proche de l'origine
        /// (l'entrepôt). Marche pour les remorques orientées +x ou -x.
        /// </summary>
        private float DoorX()
        {
            Bounds b = InteriorBounds();
            return Mathf.Abs(b.min.x) <= Mathf.Abs(b.max.x) ? b.min.x : b.max.x;
        }

        [ContextMenu("Reconstruire la baie")]
        public void Rebuild()
        {
            built = false;
            EnsureBuilt();
        }

        private void EnsureBuilt()
        {
            if (built) return;
            built = true;

            Bounds bounds = InteriorBounds();
            float doorX = Mathf.Abs(bounds.min.x) <= Mathf.Abs(bounds.max.x) ? bounds.min.x : bounds.max.x;
            float farX = Mathf.Approximately(doorX, bounds.min.x) ? bounds.max.x : bounds.min.x;
            float dir = Mathf.Sign(farX - doorX); // sens de remplissage : porte -> fond
            float centerZ = (bounds.min.z + bounds.max.z) * .5f;
            float floorY = Mathf.Max(0f, bounds.min.y);
            float depth = bounds.max.x - bounds.min.x;
            float width = bounds.max.z - bounds.min.z;

            BuildWallColliders(bounds, doorX, farX, centerZ, depth, width);

            Storage = CustomStorage != null ? CustomStorage : DetectExternalStorage();
            ExternalMode = Storage != null;
            if (ExternalMode)
            {
                baseSurfaceY = PalletTopY();
                CollectExternalSlots();
                ChooseExternalDockZone(doorX);
                Debug.Log("[Camion] station existante détectée : " + Storage.name +
                          " (" + Capacity + " niveaux), dock " + (DockZone != null ? DockZone.name : "?"));
            }
            else
            {
                baseSurfaceY = floorY + SlotLift;
                BuildSlots(doorX, dir, centerZ, floorY, width);
                BuildStorage();
                BuildDockZone(doorX, dir, centerZ, floorY);
            }
            if (ShowIndicators) BuildBeacon(doorX, dir, centerZ);
            SetupGate();
            RefreshIndicators(true);
        }

        /// <summary>Porte de la remorque : réutilise Gate si assigné, sinon cherche gate_01_GRP.</summary>
        private void SetupGate()
        {
            if (Gate == null && !string.IsNullOrEmpty(GateName))
                foreach (Transform t in GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == GateName) { Gate = t; break; }
            if (Gate != null)
            {
                gateOpenY = Gate.localPosition.y;
                if (StartClosed)
                {
                    Vector3 lp = Gate.localPosition;
                    lp.y = GateClosedY;
                    Gate.localPosition = lp;   // porte physiquement fermée dès le départ
                    Door = TruckDoorState.Closed;
                }
                else
                {
                    Door = TruckDoorState.Open;
                }
            }
            BuildDoorBlocker();
        }

        /// <summary>Zone interdite devant le dock tant que la porte est fermée.</summary>
        private void BuildDoorBlocker()
        {
            if (DockPoint == null) return;
            Transform t = transform.Find("door_nav_blocker");
            GameObject go = t != null ? t.gameObject : new GameObject("door_nav_blocker");
            if (go.transform.parent != transform) go.transform.SetParent(transform, false);
            go.transform.position = DockPoint.position + Vector3.up * .6f;
            var col = go.GetComponent<BoxCollider>();
            if (col == null) col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = DockZoneSize + new Vector3(.6f, 0f, .6f);
            var blocker = go.GetComponent<NavBlocker>();
            if (blocker == null) blocker = go.AddComponent<NavBlocker>();
            blocker.ShowVolume = false;   // la porte elle-même montre l'obstacle
            doorBlocker = blocker;
            blocker.enabled = !IsDoorOpen;
        }

        // ------------------------------------------------------------------ API porte (orchestrateur)

        /// <summary>L'orchestrateur s'annonce : la baie n'ouvre plus toute seule.</summary>
        public void SetManaged(bool value) { managed = value; }

        /// <summary>Ouvre la porte pour une commande donnée (réservation incluse).</summary>
        public void OpenDoor(int orderId)
        {
            managed = true;
            ReservedOrderId = orderId;
            if (Door == TruckDoorState.Closed || Door == TruckDoorState.Loaded || Door == TruckDoorState.Closing)
                Door = TruckDoorState.Opening;
            Debug.Log("[Camion] " + name + " : ouverture porte pour commande #" + orderId);
            ApplyDoorBlocker();
        }

        /// <summary>R2 commence le chargement : Open/Ready -> Loading.</summary>
        public void BeginLoading()
        {
            if (Door == TruckDoorState.Open || Door == TruckDoorState.Ready)
                Door = TruckDoorState.Loading;
        }

        /// <summary>
        /// Ferme la porte. complete=true -> Loaded (commande livrée) ; false -> Closed
        /// (annulation). L'animation attend que le robot soit sorti de la remorque.
        /// keepReservation : la baie reste réservée à la commande (suspension sur
        /// commande urgente — les cartons déjà chargés restent à elle).
        /// </summary>
        public void CloseDoor(bool orderComplete, bool keepReservation = false)
        {
            managed = true;
            keepReservationOnClose = keepReservation;
            if (Door == TruckDoorState.Open || Door == TruckDoorState.Ready ||
                Door == TruckDoorState.Loading || Door == TruckDoorState.Opening)
                Door = TruckDoorState.Closing;
            pendingLoaded = orderComplete;
        }

        /// <summary>Libère la réservation sans toucher la porte.</summary>
        public void ReleaseReservation() { ReservedOrderId = -1; }

        /// <summary>Cartons de la commande actuellement posés dans ce camion.</summary>
        public int CountBoxesForOrder(int orderId)
        {
            int count = 0;
            foreach (CargoBox box in GetComponentsInChildren<CargoBox>(true))
                if (box != null && !box.isCarried && box.orderId == orderId) count++;
            return count;
        }

        /// <summary>Compteurs par type (A,B,C) des cartons de la commande dans le camion.</summary>
        public int[] CountByTypeForOrder(int orderId)
        {
            var counts = new int[3];
            foreach (CargoBox box in GetComponentsInChildren<CargoBox>(true))
            {
                if (box == null || box.isCarried || box.orderId != orderId) continue;
                switch (box.TypeLetter)
                {
                    case "A": counts[0]++; break;
                    case "B": counts[1]++; break;
                    case "C": counts[2]++; break;
                }
            }
            return counts;
        }

        private bool pendingLoaded;
        private bool keepReservationOnClose;
        private void ApplyDoorBlocker()
        {
            // Le dock n'est franchissable que porte complètement ouverte.
            if (doorBlocker != null) doorBlocker.enabled = !IsDoorOpen;
        }

        // ------------------------------------------------------------------ porte de commande

        /// <summary>
        /// Porte pilotée par la FSM (orchestrateur) : Opening/Closing animent le transform.
        /// Sans orchestrateur (managed=false), comportement historique : ouverture
        /// automatique puis cycle fermeture/réouverture quand la commande est terminée.
        /// </summary>
        private void UpdateGate()
        {
            if (Gate == null) return;

            // Repli démo : pas d'orchestrateur -> la porte s'ouvre toute seule au départ.
            if (!managed && AutoOpenIfUnmanaged && Door == TruckDoorState.Closed)
            {
                if (unmanagedOpenAt <= 0f) unmanagedOpenAt = Time.time + 1.5f;
                else if (Time.time >= unmanagedOpenAt)
                {
                    Door = TruckDoorState.Opening;
                    ApplyDoorBlocker();
                    Debug.Log("[Camion] " + name + " : pas d'orchestrateur, porte ouverte (mode démo)");
                }
            }

            switch (Door)
            {
                case TruckDoorState.Opening:
                    if (MoveGateY(gateOpenY))
                    {
                        Door = ReservedOrderId >= 0 ? TruckDoorState.Ready : TruckDoorState.Open;
                        Debug.Log("[Camion] " + name + " : porte ouverte" +
                                  (ReservedOrderId >= 0 ? " (commande #" + ReservedOrderId + ")" : ""));
                        ApplyDoorBlocker();
                    }
                    break;

                case TruckDoorState.Closing:
                    // Le robot chargeur doit être sorti avant que la porte descende.
                    if (!LoaderOutside()) break;
                    if (MoveGateY(GateClosedY))
                    {
                        if (pendingLoaded)
                        {
                            LastLoadedOrderId = ReservedOrderId;
                            LastLoadedCount = LoadedCount;
                            Debug.Log("[Camion] " + name + " : commande #" + ReservedOrderId +
                                      " expédiée (" + LoadedCount + " cartons), porte fermée");
                            if (ClearAfterLoad) ClearStoredBoxes();
                            Door = TruckDoorState.Loaded;
                            ReservedOrderId = -1;
                        }
                        else
                        {
                            Door = TruckDoorState.Closed;
                            if (keepReservationOnClose)
                                Debug.Log("[Camion] " + name + " : porte fermée, baie conservée " +
                                          "pour commande #" + ReservedOrderId);
                            else
                            {
                                ReservedOrderId = -1;
                                Debug.Log("[Camion] " + name + " : porte fermée");
                            }
                        }
                        pendingLoaded = false;
                        keepReservationOnClose = false;
                        ApplyDoorBlocker();
                    }
                    break;

                case TruckDoorState.Open:
                case TruckDoorState.Ready:
                case TruckDoorState.Loading:
                    // Comportement historique uniquement hors orchestration.
                    if (!managed && CloseWhenComplete && LoadedCount > 0 &&
                        (IsFull || OrderComplete()) && LoaderOutside())
                    {
                        pendingLoaded = true;
                        Door = TruckDoorState.Closing;
                        Debug.Log("[Camion] commande terminée (" + LoadedCount + "/" + Capacity + ") : fermeture de la porte");
                    }
                    break;

                case TruckDoorState.Loaded:
                    // Sans orchestrateur, rouvrir après expédition (cycle démo historique).
                    if (!managed && ReopenAfterClear)
                    {
                        Door = TruckDoorState.Opening;
                        Debug.Log("[Camion] porte rouverte : prêt pour la prochaine commande");
                    }
                    break;
            }
        }

        /// <summary>
        /// Plus rien à livrer : bancs vides, robot sans carton et pas en train de
        /// charger — stable depuis IdleCloseDelay secondes.
        /// </summary>
        private bool OrderComplete()
        {
            CargoRegistry reg = CargoRegistry.Instance;
            if (reg == null) return false;
            if (loader == null && Time.time > nextLoaderFind)
            {
                nextLoaderFind = Time.time + 2f;
                loader = FindFirstObjectByType<TruckLoaderAgent>();
            }
            if (reg.OnBenchCount > 0) { quietSince = 0f; return false; }
            if (loader != null)
            {
                if (loader.Carried != null) { quietSince = 0f; return false; }
                TruckLoaderAgent.R2State s = loader.State;
                if (s == TruckLoaderAgent.R2State.VersBanc || s == TruckLoaderAgent.R2State.Prise ||
                    s == TruckLoaderAgent.R2State.VersCamion || s == TruckLoaderAgent.R2State.Depose ||
                    s == TruckLoaderAgent.R2State.AttenteCamion) { quietSince = 0f; return false; }
            }
            if (quietSince <= 0f) quietSince = Time.time;
            return Time.time - quietSince >= IdleCloseDelay;
        }

        /// <summary>Le robot chargeur est sorti de la remorque (porte dégagée).</summary>
        private bool LoaderOutside()
        {
            // Le chargeur était résolu uniquement par la fermeture auto non orchestrée :
            // en mode Muse il restait null et la porte se refermait sur R2.
            if (loader == null && Time.time > nextLoaderFind)
            {
                nextLoaderFind = Time.time + 1f;
                loader = FindFirstObjectByType<TruckLoaderAgent>();
            }
            if (loader == null || !loader.isActiveAndEnabled) return true;
            return !IsInside(loader.transform.position);
        }

        /// <summary>Le robot chargeur est-il dans la remorque ou l'embrasure (trace/diagnostic).</summary>
        public bool LoaderInside => !LoaderOutside();

        /// <summary>Position dans la remorque ou dans l'embrasure de la porte.</summary>
        public bool IsInside(Vector3 p)
        {
            Bounds b = InteriorBounds();
            float doorX = DoorX();
            bool doorIsMin = Mathf.Abs(b.min.x) <= Mathf.Abs(b.max.x);
            const float clearance = .8f; // marge pour dégager complètement l'embrasure
            if (doorIsMin && p.x < doorX - clearance) return false;
            if (!doorIsMin && p.x > doorX + clearance) return false;
            return !(p.x < b.min.x - .15f || p.x > b.max.x + .15f ||
                     p.z < b.min.z - .15f || p.z > b.max.z + .15f);
        }

        private bool MoveGateY(float targetY)
        {
            Vector3 lp = Gate.localPosition;
            lp.y = Mathf.MoveTowards(lp.y, targetY, GateSpeed * Time.deltaTime);
            Gate.localPosition = lp;
            return Mathf.Abs(lp.y - targetY) < .001f;
        }

        /// <summary>Détruit tous les cartons stockés sur la palette (commande expédiée).</summary>
        private void ClearStoredBoxes()
        {
            if (Storage == null || Storage.columns == null) return;
            for (int c = 0; c < Storage.columns.Length; c++)
            {
                Transform[] levels = Storage.columns[c] != null ? Storage.columns[c].levels : null;
                if (levels == null) continue;
                for (int l = 0; l < levels.Length; l++)
                {
                    CargoBox box = levels[l] != null ? levels[l].GetComponentInChildren<CargoBox>(true) : null;
                    if (box == null || box.isCarried) continue;
                    Storage.RemoveBox(c, l);
                    Debug.Log("[Camion] carton expédié : " + box.name);
                    if (Application.isPlaying) Destroy(box.gameObject);
                    else DestroyImmediate(box.gameObject);
                }
            }
            Recount();
            RefreshIndicators(true);
            Debug.Log("[Camion] " + name + " : palette vidée, commande expédiée");
        }

        /// <summary>Premier ShelfStorage enfant qui n'est pas le stockage auto-généré.</summary>
        private ShelfStorage DetectExternalStorage()
        {
            foreach (ShelfStorage shelf in GetComponentsInChildren<ShelfStorage>(true))
                if (shelf != null && shelf.name != "truck_storage") return shelf;
            return null;
        }

        /// <summary>Tous les points de niveau du stockage existant deviennent des "slots".
        /// Si le stockage n'a pas de colonnes remplies, elles sont générées : une colonne
        /// par zone (columnIndex), niveaux empilés au-dessus de la surface de la palette.</summary>
        private void CollectExternalSlots()
        {
            slots.Clear();
            slotMarkers.Clear();
            slotsRoot = Storage != null ? Storage.transform : null;
            if (Storage == null) { Capacity = 0; return; }
            EnsureExternalColumns();
            if (Storage.columns == null) { Capacity = 0; return; }
            foreach (ShelfStorage.StorageColumn column in Storage.columns)
            {
                if (column == null || column.levels == null) continue;
                for (int l = 0; l < column.levels.Length; l++)
                {
                    Transform level = column.levels[l];
                    if (level == null) continue;
                    slots.Add(level);
                    if (ShowIndicators && l == 0) BuildSlotMarker(level);
                    else slotMarkers.Add(null);
                }
            }
            Capacity = slots.Count;
        }

        /// <summary>Hauteur de la surface de dépose : dessus du renderer "pallet" du stockage.</summary>
        private float PalletTopY()
        {
            Renderer best = PalletRenderer();
            return Mathf.Max(0f, best != null ? best.bounds.max.y : InteriorBounds().min.y + .15f);
        }

        /// <summary>Bounds du renderer "pallet" du stockage (le support physique des cartons).</summary>
        private Bounds PalletBounds()
        {
            Renderer best = PalletRenderer();
            // Pas de renderer "pallet" : repli sur l'intérieur de la remorque.
            return best != null ? best.bounds : InteriorBounds();
        }

        /// <summary>Le renderer le plus haut portant "pallet" dans son nom.</summary>
        private Renderer PalletRenderer()
        {
            Renderer best = null;
            foreach (Renderer r in Storage.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.name.ToLowerInvariant().Contains("pallet")) continue;
                if (best == null || r.bounds.max.y > best.bounds.max.y) best = r;
            }
            return best;
        }

        /// <summary>Regarde si au moins un vrai point de niveau est assigné.</summary>
        private static bool HasAnyLevel(ShelfStorage storage)
        {
            if (storage.columns == null) return false;
            foreach (ShelfStorage.StorageColumn column in storage.columns)
            {
                if (column == null || column.levels == null) continue;
                foreach (Transform level in column.levels)
                    if (level != null) return true;
            }
            return false;
        }

        /// <summary>Remplit les références de niveaux manquantes du stockage existant :
        /// conserve le nombre de colonnes/niveaux défini, place chaque point sous sa zone.</summary>
        private void EnsureExternalColumns()
        {
            if (HasAnyLevel(Storage)) return; // déjà câblé par l'utilisateur — ne rien toucher

            ShelfInteractionZone[] zones = Storage.GetComponentsInChildren<ShelfInteractionZone>(true);
            int columnCount = Storage.columns != null ? Mathf.Max(1, Storage.columns.Length) : 1;
            foreach (ShelfInteractionZone zone in zones)
                if (zone != null) columnCount = Mathf.Max(columnCount, zone.columnIndex + 1);

            Transform root = ChildOf(Storage.transform, "truck_levels");
            for (int i = root.childCount - 1; i >= 0; i--) // purge des niveaux générés précédemment
                SafeDestroy(root.GetChild(i).gameObject);
            float topY = PalletTopY();

            // Ancres de dépose : les pads "marker" placés par l'utilisateur sur la palette
            // (positions voulues). Fallback : quadrants 25 %/75 % des bounds de la palette.
            var markers = new List<Vector3>();
            foreach (Renderer r in Storage.GetComponentsInChildren<Renderer>(true))
                if (r != null && r.name.StartsWith("marker", System.StringComparison.OrdinalIgnoreCase))
                    markers.Add(r.bounds.center);
            Bounds pb = PalletBounds();
            float xLo = Mathf.Lerp(pb.min.x, pb.max.x, .25f);
            float xHi = Mathf.Lerp(pb.min.x, pb.max.x, .75f);
            float zLo = Mathf.Lerp(pb.min.z, pb.max.z, .25f);
            float zHi = Mathf.Lerp(pb.min.z, pb.max.z, .75f);
            float cx = (pb.min.x + pb.max.x) * .5f;
            float cz = (pb.min.z + pb.max.z) * .5f;

            var newColumns = new ShelfStorage.StorageColumn[columnCount];
            for (int c = 0; c < columnCount; c++)
            {
                int levelCount = Mathf.Max(1, StackLevels);
                if (Storage.columns != null && c < Storage.columns.Length &&
                    Storage.columns[c] != null && Storage.columns[c].levels != null &&
                    Storage.columns[c].levels.Length > 0)
                    levelCount = Storage.columns[c].levels.Length; // conserve le shape utilisateur

                // Le dock de la zone (sinon la zone elle-même) indique le côté de la colonne.
                Vector3 side = Storage.transform.position;
                foreach (ShelfInteractionZone zone in zones)
                    if (zone != null && zone.columnIndex == c)
                    {
                        side = zone.dockPoint != null ? zone.dockPoint.position : zone.transform.position;
                        break;
                    }
                Vector3 anchor = new Vector3(side.x < cx ? xLo : xHi, topY, side.z < cz ? zLo : zHi);
                // Un pad marker existe de ce côté : c'est la position de dépose voulue.
                float bestDist = float.PositiveInfinity;
                foreach (Vector3 marker in markers)
                {
                    if ((marker.x < cx) != (side.x < cx) || (marker.z < cz) != (side.z < cz)) continue;
                    float d = Vector2.Distance(new Vector2(marker.x, marker.z), new Vector2(side.x, side.z));
                    if (d < bestDist) { bestDist = d; anchor = new Vector3(marker.x, topY, marker.z); }
                }

                var levels = new Transform[levelCount];
                for (int l = 0; l < levelCount; l++)
                {
                    var level = new GameObject("level_" + c + "_" + l).transform;
                    level.SetParent(root, false);
                    // Estimation initiale — UpdateLevelHeights recale ensuite sur la pile réelle.
                    level.position = new Vector3(anchor.x, topY + .02f + l * .55f, anchor.z);
                    levels[l] = level;
                }
                newColumns[c] = new ShelfStorage.StorageColumn { levels = levels };
            }
            Storage.columns = newColumns;
            ReinitStorage();
            Debug.Log("[Camion] niveaux générés sur " + Storage.name + " : " + columnCount + " colonnes");
        }

        private static Transform ChildOf(Transform parent, string childName)
        {
            Transform t = parent.Find(childName);
            if (t == null)
            {
                t = new GameObject(childName).transform;
                t.SetParent(parent, false);
            }
            return t;
        }

        /// <summary>Rejoue l'initialisation interne du ShelfStorage (table boxes + scan).</summary>
        private void ReinitStorage()
        {
            if (Storage == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ShelfStorage).GetMethod("InitializeStorage", flags)?.Invoke(Storage, null);
            typeof(ShelfStorage).GetMethod("AutoFindExistingBoxes", flags)?.Invoke(Storage, null);
        }

        private static void SafeDestroy(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj);
            else Object.DestroyImmediate(obj);
        }

        /// <summary>Zone de la station dont le dock est le plus proche de la porte.</summary>
        private void ChooseExternalDockZone(float doorX)
        {
            DockZone = null;
            float best = float.PositiveInfinity;
            foreach (ShelfInteractionZone zone in Storage.GetComponentsInChildren<ShelfInteractionZone>(true))
            {
                if (zone == null || zone.dockPoint == null) continue;
                float distance = Mathf.Abs(zone.dockPoint.position.x - doorX);
                if (distance < best) { best = distance; DockZone = zone; }
            }
            DockPoint = DockZone != null ? DockZone.dockPoint : null;
        }

        /// <summary>
        /// Zone de dock à utiliser pour la prochaine dépose : la colonne la moins remplie
        /// (les cartons se répartissent sur la palette au lieu de s'entasser au même coin).
        /// À égalité, le dock le plus proche de la position donnée.
        /// </summary>
        public ShelfInteractionZone ChooseDockForLoad(Vector3 from)
        {
            if (!ExternalMode || Storage == null || Storage.columns == null) return DockZone;
            ShelfInteractionZone best = DockZone;
            int bestFill = int.MaxValue;
            float bestDist = float.PositiveInfinity;
            foreach (ShelfInteractionZone zone in Storage.GetComponentsInChildren<ShelfInteractionZone>(true))
            {
                if (zone == null || zone.dockPoint == null) continue;
                Transform[] levels = CargoRegistry.ColumnLevels(zone);
                if (levels == null || levels.Length == 0) continue;
                int fill = 0, free = 0;
                foreach (Transform level in levels)
                {
                    if (level == null) continue;
                    if (level.GetComponentInChildren<CargoBox>(true) != null) fill++;
                    else free++;
                }
                if (free <= 0) continue; // colonne pleine
                float d = Vector3.Distance(from, zone.dockPoint.position);
                if (fill < bestFill || (fill == bestFill && d < bestDist))
                {
                    bestFill = fill;
                    bestDist = d;
                    best = zone;
                }
            }
            return best;
        }

        private Transform Child(string childName)
        {
            Transform t = transform.Find(childName);
            if (t == null)
            {
                t = new GameObject(childName).transform;
                t.SetParent(transform, false);
            }
            return t;
        }

        private void BuildWallColliders(Bounds b, float doorX, float farX, float centerZ, float depth, float width)
        {
            if (!AutoWallColliders) return;
            Transform walls = Child("truck_walls");
            float h = b.max.y - Mathf.Max(0f, b.min.y);
            float midY = Mathf.Max(0f, b.min.y) + h * .5f;
            float side = Mathf.Sign(farX - doorX);
            // Parois latérales (z) + paroi du fond (x). La porte reste ouverte.
            Wall(walls, "wall_z_min", new Vector3((b.min.x + b.max.x) * .5f, midY, b.min.z - WallThickness * .5f),
                 new Vector3(depth + WallThickness * 2f, h, WallThickness));
            Wall(walls, "wall_z_max", new Vector3((b.min.x + b.max.x) * .5f, midY, b.max.z + WallThickness * .5f),
                 new Vector3(depth + WallThickness * 2f, h, WallThickness));
            Wall(walls, "wall_back", new Vector3(farX + side * WallThickness * .5f, midY, centerZ),
                 new Vector3(WallThickness, h, width));
        }

        private static void Wall(Transform parent, string wallName, Vector3 center, Vector3 size)
        {
            Transform t = parent.Find(wallName);
            if (t == null)
            {
                t = new GameObject(wallName).transform;
                t.SetParent(parent, false);
            }
            t.position = center;
            BoxCollider col = t.GetComponent<BoxCollider>();
            if (col == null) col = t.gameObject.AddComponent<BoxCollider>();
            col.size = size;
            col.isTrigger = false;
        }

        private void BuildSlots(float doorX, float dir, float centerZ, float floorY, float width)
        {
            slotsRoot = Child("truck_slots");
            slots.Clear();
            slotMarkers.Clear();

            float margin = .45f;
            float usable = Mathf.Max(.3f, width - margin * 2f);
            float zStart = centerZ - usable * .5f;
            float zStep = SlotsWide > 1 ? usable / (SlotsWide - 1) : 0f;
            float xStart = doorX + dir * .7f; // premier slot juste derrière la porte

            for (int d = 0; d < SlotsDepth; d++)
            {
                for (int w = 0; w < SlotsWide; w++)
                {
                    for (int l = 0; l < StackLevels; l++)
                    {
                        Transform slot = new GameObject("slot_" + slots.Count).transform;
                        slot.SetParent(slotsRoot, false);
                        slot.position = new Vector3(xStart + dir * d * SlotSpacing.x,
                                                    floorY + SlotLift + l * SlotSpacing.y,
                                                    zStart + w * zStep);
                        slots.Add(slot);
                        if (ShowIndicators) BuildSlotMarker(slot);
                    }
                }
            }
            Capacity = slots.Count;
        }

        private void BuildSlotMarker(Transform slot)
        {
            Transform existing = slot.Find("marker");
            GameObject marker = existing != null ? existing.gameObject : GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "marker";
            SafeDestroy(marker.GetComponent<Collider>());
            if (marker.transform.parent != slot) marker.transform.SetParent(slot, false);
            marker.transform.localRotation = Quaternion.identity;
            marker.transform.localPosition = new Vector3(0f, -.08f, 0f);
            marker.transform.localScale = new Vector3(.5f, .02f, .65f);
            var renderer = marker.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.sharedMaterial = FlatMaterial(SlotFreeColor);
            slotMarkers.Add(renderer);
        }

        private void BuildStorage()
        {
            Transform holder = Child("truck_storage");
            Storage = holder.GetComponent<ShelfStorage>();
            if (Storage == null) Storage = holder.gameObject.AddComponent<ShelfStorage>();

            var column = new ShelfStorage.StorageColumn { levels = slots.ToArray() };
            Storage.columns = new[] { column };

            // ShelfStorage remplit sa table interne dans Awake — qui a déjà tourné (ou jamais,
            // si l'objet était inactif). On la reconstruit comme le fait l'autopilot.
            ReinitStorage();
        }

        private void BuildDockZone(float doorX, float dir, float centerZ, float floorY)
        {
            Transform zoneT = Child("truck_dock_zone");
            zoneT.position = new Vector3(doorX - dir * DockOffset, floorY, centerZ);
            zoneT.rotation = Quaternion.LookRotation(Vector3.right * dir, Vector3.up); // face au camion

            BoxCollider trigger = zoneT.GetComponent<BoxCollider>();
            if (trigger == null) trigger = zoneT.gameObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = DockZoneSize;
            trigger.center = new Vector3(0f, DockZoneSize.y * .4f, 0f);

            Transform dock = zoneT.Find("dock");
            if (dock == null)
            {
                dock = new GameObject("dock").transform;
                dock.SetParent(zoneT, false);
            }
            dock.localPosition = Vector3.zero;
            dock.localRotation = Quaternion.identity;
            DockPoint = dock;

            DockZone = zoneT.GetComponent<ShelfInteractionZone>();
            if (DockZone == null) DockZone = zoneT.gameObject.AddComponent<ShelfInteractionZone>();
            DockZone.shelf = Storage;
            DockZone.columnIndex = 0;
            DockZone.accessSide = ShelfInteractionZone.AccessSide.Front;
            DockZone.dockPoint = dock;
            DockZone.positionTolerance = .08f;
            DockZone.angleTolerance = 8f;
        }

        private void BuildBeacon(float doorX, float dir, float centerZ)
        {
            Transform b = Child("truck_beacon");
            Vector3 pos = new Vector3(doorX - dir * .4f, 2.4f, centerZ);
            if (ExternalMode && DockPoint != null) pos = DockPoint.position + Vector3.up * 2.4f;
            b.position = pos;
            var visual = b.Find("bulb") != null ? b.Find("bulb").gameObject
                         : GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = "bulb";
            if (visual.transform.parent != b) visual.transform.SetParent(b, false);
            visual.transform.localScale = Vector3.one * .22f;
            SafeDestroy(visual.GetComponent<Collider>());
            beaconBulb = visual.GetComponent<Renderer>();
            beaconBulb.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            beacon = b.GetComponent<Light>();
            if (beacon == null) beacon = b.gameObject.AddComponent<Light>();
            beacon.type = LightType.Point;
            beacon.range = 4f;
            beacon.intensity = 2f;
            ApplyBeaconColor(new Color(1f, .65f, .1f)); // orange = prêt
        }

        private void ApplyBeaconColor(Color color)
        {
            beacon.color = color;
            beacon.enabled = true;
            if (beaconBulb != null) beaconBulb.sharedMaterial = FlatMaterial(color);
        }

        private void UpdateBeacon()
        {
            if (beacon == null) return;
            if (Time.time < beaconUntil)
            {
                if (beaconGreen) ApplyBeaconColor(Color.green);
                return;
            }
            ApplyBeaconColor(IsFull ? Color.red : new Color(1f, .65f, .1f));
        }

        // ------------------------------------------------------------------ état

        /// <summary>Carton posé sur la station (enfant du stockage ou d'un slot).</summary>
        public bool OwnsBox(CargoBox box)
        {
            if (box == null) return false;
            if (ExternalMode) return Storage != null && box.transform.IsChildOf(Storage.transform);
            return slotsRoot != null && box.transform.IsChildOf(slotsRoot);
        }

        /// <summary>Premier slot/niveau libre, colonne la moins remplie d'abord (répartition).</summary>
        public Transform NextFreeSlot()
        {
            if (!ExternalMode || Storage == null || Storage.columns == null)
            {
                foreach (Transform slot in slots)
                    if (slot != null && slot.GetComponentInChildren<CargoBox>(true) == null) return slot;
                return null;
            }
            Transform best = null;
            int bestFill = int.MaxValue;
            foreach (ShelfStorage.StorageColumn column in Storage.columns)
            {
                if (column == null || column.levels == null) continue;
                int fill = 0;
                Transform firstFree = null;
                foreach (Transform level in column.levels)
                {
                    if (level == null) continue;
                    if (level.GetComponentInChildren<CargoBox>(true) != null) { fill++; continue; }
                    if (firstFree == null) firstFree = level;
                }
                if (firstFree != null && fill < bestFill) { bestFill = fill; best = firstFree; }
            }
            return best;
        }

        /// <summary>
        /// Validation logique d'une livraison : le carton doit finir sous la station.
        /// Renvoie vrai + met à jour compteurs/indicateurs, faux sinon (avec warning).
        /// </summary>
        public bool ValidateStored(CargoBox box)
        {
            if (box == null) return false;
            if (!OwnsBox(box))
            {
                Debug.LogWarning("[Camion] " + box.name + " annoncé livré mais n'est pas sous la station.");
                return false;
            }
            ClampInsidePallet(box);
            NotifyStored(box);
            return true;
        }

        /// <summary>
        /// Replace le carton pour que toute sa empreinte reste sur la palette :
        /// les pads peuvent être proches du bord, un gros carton ne doit pas dépasser.
        /// </summary>
        public void ClampInsidePallet(CargoBox box)
        {
            if (box == null) return;
            Bounds pb = PalletBounds();
            if (pb.size.x <= 0f || pb.size.z <= 0f) return;
            if (!TryBoxBounds(box, out Bounds bb)) return;
            float minX = pb.min.x + bb.extents.x, maxX = pb.max.x - bb.extents.x;
            float minZ = pb.min.z + bb.extents.z, maxZ = pb.max.z - bb.extents.z;
            float cx = minX <= maxX ? Mathf.Clamp(bb.center.x, minX, maxX) : (pb.min.x + pb.max.x) * .5f;
            float cz = minZ <= maxZ ? Mathf.Clamp(bb.center.z, minZ, maxZ) : (pb.min.z + pb.max.z) * .5f;
            Vector3 delta = new Vector3(cx - bb.center.x, 0f, cz - bb.center.z);
            if (delta.sqrMagnitude > 1e-6f) box.transform.position += delta;
        }

        /// <summary>Comptage + indicateurs + événement, idempotent.</summary>
        public void NotifyStored(CargoBox box)
        {
            Recount();
            beaconGreen = true;
            beaconUntil = Time.time + 1.2f;
            Debug.Log("[Camion] livraison validée : " + box.name + " (" + LoadedCount + "/" + Capacity + ")" +
                      (IsFull ? " — CAMION PLEIN" : ""));
            Stored?.Invoke(box);
            RefreshIndicators(true);
        }

        /// <summary>Dépose manuelle (fallback quand l'interaction Warcool ne répond pas).</summary>
        public bool PlaceBox(CargoBox box)
        {
            Transform slot = NextFreeSlot();
            if (slot == null || box == null) return false;
            Quaternion rotation = box.transform.rotation;
            box.transform.SetParent(slot, true);
            box.transform.position = slot.position;
            box.transform.rotation = rotation;
            box.isCarried = false;
            ClampInsidePallet(box);
            if (Storage != null)
            {
                // Garde la table interne du ShelfStorage cohérente avec la scène.
                for (int c = 0; c < Storage.columns.Length; c++)
                {
                    Transform[] levels = Storage.columns[c] != null ? Storage.columns[c].levels : null;
                    if (levels == null) continue;
                    for (int l = 0; l < levels.Length; l++)
                        if (levels[l] == slot) { Storage.PutBox(c, l, box, rotation); goto done; }
                }
            }
            done:
            NotifyStored(box);
            return true;
        }

        /// <summary>
        /// Chaque niveau vide se cale sur le dessus réel de la pile en dessous
        /// (mesuré via les bounds des renderers) : l'empilement reste ajusté
        /// quelle que soit la hauteur des cartons.
        /// </summary>
        private void UpdateLevelHeights()
        {
            if (Storage == null || Storage.columns == null) return;
            const float gap = .005f;
            foreach (ShelfStorage.StorageColumn column in Storage.columns)
            {
                if (column == null || column.levels == null) continue;
                // Un niveau = la surface d'appui (dessus palette, puis dessus du carton dessous).
                float surface = baseSurfaceY + gap;
                foreach (Transform level in column.levels)
                {
                    if (level == null) continue;
                    Vector3 p = level.position;
                    if (Mathf.Abs(p.y - surface) > .001f) level.position = new Vector3(p.x, surface, p.z);

                    CargoBox box = level.GetComponentInChildren<CargoBox>(true);
                    if (box == null || box.isCarried) continue;
                    // Le pivot des cartons est au centre : on cale le BAS réel du carton
                    // (bounds renderer) sur la surface, centré sur le point du niveau.
                    if (TryBoxBounds(box, out Bounds b))
                    {
                        Vector3 delta = new Vector3(level.position.x - b.center.x, surface - b.min.y, level.position.z - b.center.z);
                        if (delta.sqrMagnitude > 1e-6f) box.transform.position += delta;
                        ClampInsidePallet(box);
                        surface += b.size.y + gap;
                    }
                    else surface += .5f;
                }
            }
        }

        private static bool TryBoxBounds(CargoBox box, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer r in box.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        private void Recount()
        {
            int count = 0;
            foreach (Transform slot in slots)
                if (slot != null && slot.GetComponentInChildren<CargoBox>(true) != null) count++;
            LoadedCount = count;
        }

        private void RefreshIndicators(bool force = false)
        {
            for (int i = 0; i < slots.Count && i < slotMarkers.Count; i++)
            {
                if (slots[i] == null || slotMarkers[i] == null) continue;
                bool used = slots[i].GetComponentInChildren<CargoBox>(true) != null;
                Color wanted = used ? SlotUsedColor : SlotFreeColor;
                if (force || slotMarkers[i].sharedMaterial.color != wanted)
                    slotMarkers[i].sharedMaterial = FlatMaterial(wanted);
            }
            Recount();
        }

        private static readonly Dictionary<Color, Material> materialCache = new Dictionary<Color, Material>();

        private static Material FlatMaterial(Color color)
        {
            if (materialCache.TryGetValue(color, out Material cached) && cached != null) return cached;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader) { color = color };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            materialCache[color] = mat;
            return mat;
        }
    }
}
