using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Truck loading bay.
    ///
    /// Two modes:
    ///   - Existing station: if the trailer already contains a ShelfStorage with
    ///     ShelfInteractionZones (e.g. a "pallet depot"), the component uses it as is —
    ///     counting, validation and indicators rely on its levels.
    ///   - Auto: otherwise it generates slots + ShelfStorage + a dock zone right behind the door.
    ///
    /// In both cases it adds thin colliders on the walls (door = the end of the
    /// bounds closest to the scene origin, it stays open), a signal
    /// light and slot markers (green = occupied).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TruckBay : MonoBehaviour
    {
        [Header("Existing station (optional)")]
        [Tooltip("Storage already present in the trailer (e.g. pallet depot). " +
                 "Empty = automatic detection of child ShelfStorages, otherwise auto-generated slots.")]
        public ShelfStorage CustomStorage;

        [Header("Slot layout (auto mode)")]
        public int SlotsDepth = 4;
        public int SlotsWide = 2;
        public int StackLevels = 1;
        public Vector3 SlotSpacing = new Vector3(.75f, .7f, .85f);
        public float SlotLift = .12f;

        [Header("Dock (mode auto)")]
        public float DockOffset = .8f;
        public Vector3 DockZoneSize = new Vector3(1.4f, 1.6f, 1.4f);

        [Header("Wall colliders")]
        public bool AutoWallColliders = true;
        public float WallThickness = .12f;

        [Header("Signal")]
        public bool ShowIndicators = true;
        public Color SlotFreeColor = new Color(1f, .65f, .1f, .3f);
        public Color SlotUsedColor = new Color(.15f, 1f, .35f, .5f);

        [Header("Door (order finished)")]
        [Tooltip("Door transform. Empty = automatic search for 'gate_01_GRP' in the trailer.")]
        public Transform Gate;
        public string GateName = "gate_01_GRP";
        [Tooltip("Local Y of the door once closed.")]
        public float GateClosedY = .08f;
        [Tooltip("Door sliding speed (m/s).")]
        public float GateSpeed = 1.5f;
        [Tooltip("At start the door goes down to the closed position (required for Muse orchestration).")]
        public bool StartClosed = true;
        [Tooltip("Without an orchestrator in the scene, the doors open by themselves at start (historical demo behaviour).")]
        public bool AutoOpenIfUnmanaged = true;
        [Tooltip("Once the door is closed on a loaded order, the boxes are destroyed (shipping).")]
        public bool ClearAfterLoad = true;
        [Tooltip("When the order is complete: the door closes, the dropped boxes are destroyed, then the door reopens.")]
        public bool CloseWhenComplete = true;
        [Tooltip("Seconds without work in progress (empty benches + robot without a box) to consider the order finished.")]
        public float IdleCloseDelay = 5f;
        public bool ReopenAfterClear = true;

        /// <summary>A box has just been validated in the truck.</summary>
        public event System.Action<CargoBox> Stored;

        public ShelfStorage Storage { get; private set; }
        public ShelfInteractionZone DockZone { get; private set; }
        public Transform DockPoint { get; private set; }
        public int Capacity { get; private set; }
        public int LoadedCount { get; private set; }
        public int FreeSlots => Capacity - LoadedCount;
        public bool IsFull => Capacity > 0 && LoadedCount >= Capacity;
        /// <summary>True if the bay reuses an existing storage instead of its generated slots.</summary>
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

        // ---- driven door (Muse orchestration) ---------------------------------
        /// <summary>Rear door FSM. Closed at start; the orchestrator drives it.</summary>
        public enum TruckDoorState { Closed, Opening, Open, Ready, Loading, Closing, Loaded }
        public TruckDoorState Door { get; private set; } = TruckDoorState.Closed;
        /// <summary>Order owning the bay (-1 = no reservation).</summary>
        public int ReservedOrderId { get; private set; } = -1;
        /// <summary>The door is physically open: a robot can enter.</summary>
        public bool IsDoorOpen => Door == TruckDoorState.Open || Door == TruckDoorState.Ready ||
                                  Door == TruckDoorState.Loading;
        /// <summary>Count of boxes loaded per order (survives the destruction of the objects).</summary>
        public int LastLoadedOrderId { get; private set; } = -1;
        public int LastLoadedCount { get; private set; }
        private NavBlocker doorBlocker;
        private bool managed;         // an orchestrator drives this bay
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
        /// Open end of the trailer: the edge of the bounds closest to the origin
        /// (the warehouse). Works for trailers facing +x or -x.
        /// </summary>
        private float DoorX()
        {
            Bounds b = InteriorBounds();
            return Mathf.Abs(b.min.x) <= Mathf.Abs(b.max.x) ? b.min.x : b.max.x;
        }

        [ContextMenu("Rebuild the bay")]
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
            float dir = Mathf.Sign(farX - doorX); // fill direction: door -> back
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
                Debug.Log("[Truck] existing station detected: " + Storage.name +
                          " (" + Capacity + " levels), dock " + (DockZone != null ? DockZone.name : "?"));
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

        /// <summary>Trailer door: reuses Gate if assigned, otherwise looks for gate_01_GRP.</summary>
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
                    Gate.localPosition = lp;   // door physically closed from the start
                    Door = TruckDoorState.Closed;
                }
                else
                {
                    Door = TruckDoorState.Open;
                }
            }
            BuildDoorBlocker();
        }

        /// <summary>No-go zone in front of the dock while the door is closed.</summary>
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
            blocker.ShowVolume = false;   // the door itself shows the obstacle
            doorBlocker = blocker;
            blocker.enabled = !IsDoorOpen;
        }

        // ------------------------------------------------------------------ door API (orchestrator)

        /// <summary>The orchestrator announces itself: the bay no longer opens by itself.</summary>
        public void SetManaged(bool value) { managed = value; }

        /// <summary>Opens the door for a given order (reservation included).</summary>
        public void OpenDoor(int orderId)
        {
            managed = true;
            ReservedOrderId = orderId;
            if (Door == TruckDoorState.Closed || Door == TruckDoorState.Loaded || Door == TruckDoorState.Closing)
                Door = TruckDoorState.Opening;
            Debug.Log("[Truck] " + name + ": opening door for order #" + orderId);
            ApplyDoorBlocker();
        }

        /// <summary>R2 starts loading: Open/Ready -> Loading.</summary>
        public void BeginLoading()
        {
            if (Door == TruckDoorState.Open || Door == TruckDoorState.Ready)
                Door = TruckDoorState.Loading;
        }

        /// <summary>
        /// Closes the door. complete=true -> Loaded (order delivered); false -> Closed
        /// (cancellation). The animation waits for the robot to leave the trailer.
        /// keepReservation: the bay stays reserved for the order (suspension for an
        /// urgent order — the boxes already loaded stay its own).
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

        /// <summary>Releases the reservation without touching the door.</summary>
        public void ReleaseReservation() { ReservedOrderId = -1; }

        /// <summary>Boxes of the order currently sitting in this truck.</summary>
        public int CountBoxesForOrder(int orderId)
        {
            int count = 0;
            foreach (CargoBox box in GetComponentsInChildren<CargoBox>(true))
                if (box != null && !box.isCarried && box.orderId == orderId) count++;
            return count;
        }

        /// <summary>Per-type counters (A,B,C) of the order's boxes in the truck.</summary>
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
            // The dock can only be crossed when the door is fully open.
            if (doorBlocker != null) doorBlocker.enabled = !IsDoorOpen;
        }

        // ------------------------------------------------------------------ order door

        /// <summary>
        /// Door driven by the FSM (orchestrator): Opening/Closing animate the transform.
        /// Without an orchestrator (managed=false), historical behaviour: automatic
        /// opening, then a close/reopen cycle when the order is finished.
        /// </summary>
        private void UpdateGate()
        {
            if (Gate == null) return;

            // Demo fallback: no orchestrator -> the door opens by itself at start.
            if (!managed && AutoOpenIfUnmanaged && Door == TruckDoorState.Closed)
            {
                if (unmanagedOpenAt <= 0f) unmanagedOpenAt = Time.time + 1.5f;
                else if (Time.time >= unmanagedOpenAt)
                {
                    Door = TruckDoorState.Opening;
                    ApplyDoorBlocker();
                    Debug.Log("[Truck] " + name + ": no orchestrator, door open (demo mode)");
                }
            }

            switch (Door)
            {
                case TruckDoorState.Opening:
                    if (MoveGateY(gateOpenY))
                    {
                        Door = ReservedOrderId >= 0 ? TruckDoorState.Ready : TruckDoorState.Open;
                        Debug.Log("[Truck] " + name + ": door open" +
                                  (ReservedOrderId >= 0 ? " (order #" + ReservedOrderId + ")" : ""));
                        ApplyDoorBlocker();
                    }
                    break;

                case TruckDoorState.Closing:
                    // The loader robot must be out before the door goes down.
                    if (!LoaderOutside()) break;
                    if (MoveGateY(GateClosedY))
                    {
                        if (pendingLoaded)
                        {
                            LastLoadedOrderId = ReservedOrderId;
                            LastLoadedCount = LoadedCount;
                            Debug.Log("[Truck] " + name + ": order #" + ReservedOrderId +
                                      " shipped (" + LoadedCount + " boxes), door closed");
                            if (ClearAfterLoad) ClearStoredBoxes();
                            Door = TruckDoorState.Loaded;
                            ReservedOrderId = -1;
                        }
                        else
                        {
                            Door = TruckDoorState.Closed;
                            if (keepReservationOnClose)
                                Debug.Log("[Truck] " + name + ": door closed, bay kept " +
                                          "for order #" + ReservedOrderId);
                            else
                            {
                                ReservedOrderId = -1;
                                Debug.Log("[Truck] " + name + ": door closed");
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
                    // Historical behaviour only outside orchestration.
                    if (!managed && CloseWhenComplete && LoadedCount > 0 &&
                        (IsFull || OrderComplete()) && LoaderOutside())
                    {
                        pendingLoaded = true;
                        Door = TruckDoorState.Closing;
                        Debug.Log("[Truck] order finished (" + LoadedCount + "/" + Capacity + "): closing the door");
                    }
                    break;

                case TruckDoorState.Loaded:
                    // Without an orchestrator, reopen after shipping (historical demo cycle).
                    if (!managed && ReopenAfterClear)
                    {
                        Door = TruckDoorState.Opening;
                        Debug.Log("[Truck] door reopened: ready for the next order");
                    }
                    break;
            }
        }

        /// <summary>
        /// Nothing left to deliver: benches empty, robot without a box and not
        /// loading — stable for IdleCloseDelay seconds.
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
                if (s == TruckLoaderAgent.R2State.ToBench || s == TruckLoaderAgent.R2State.Picking ||
                    s == TruckLoaderAgent.R2State.ToTruck || s == TruckLoaderAgent.R2State.Dropping ||
                    s == TruckLoaderAgent.R2State.WaitingForTruck) { quietSince = 0f; return false; }
            }
            if (quietSince <= 0f) quietSince = Time.time;
            return Time.time - quietSince >= IdleCloseDelay;
        }

        /// <summary>The loader robot has left the trailer (door clear).</summary>
        private bool LoaderOutside()
        {
            // The loader was only resolved by the non-orchestrated auto close:
            // in Muse mode it stayed null and the door closed on R2.
            if (loader == null && Time.time > nextLoaderFind)
            {
                nextLoaderFind = Time.time + 1f;
                loader = FindFirstObjectByType<TruckLoaderAgent>();
            }
            if (loader == null || !loader.isActiveAndEnabled) return true;
            return !IsInside(loader.transform.position);
        }

        /// <summary>Is the loader robot in the trailer or the doorway (trace/diagnostics).</summary>
        public bool LoaderInside => !LoaderOutside();

        /// <summary>Position in the trailer or in the doorway.</summary>
        public bool IsInside(Vector3 p)
        {
            Bounds b = InteriorBounds();
            float doorX = DoorX();
            bool doorIsMin = Mathf.Abs(b.min.x) <= Mathf.Abs(b.max.x);
            const float clearance = .8f; // margin to fully clear the doorway
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

        /// <summary>Destroys all the boxes stored on the pallet (order shipped).</summary>
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
                    Debug.Log("[Truck] box dispatched: " + box.name);
                    if (Application.isPlaying) Destroy(box.gameObject);
                    else DestroyImmediate(box.gameObject);
                }
            }
            Recount();
            RefreshIndicators(true);
            Debug.Log("[Truck] " + name + ": pallet emptied, order shipped");
        }

        /// <summary>First child ShelfStorage that is not the auto-generated storage.</summary>
        private ShelfStorage DetectExternalStorage()
        {
            foreach (ShelfStorage shelf in GetComponentsInChildren<ShelfStorage>(true))
                if (shelf != null && shelf.name != "truck_storage") return shelf;
            return null;
        }

        /// <summary>Every level point of the existing storage becomes a "slot".
        /// If the storage has no filled columns, they are generated: one column
        /// per zone (columnIndex), levels stacked above the pallet surface.</summary>
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

        /// <summary>Height of the drop surface: top of the storage's "pallet" renderer.</summary>
        private float PalletTopY()
        {
            Renderer best = PalletRenderer();
            return Mathf.Max(0f, best != null ? best.bounds.max.y : InteriorBounds().min.y + .15f);
        }

        /// <summary>Bounds of the storage's "pallet" renderer (the physical support of the boxes).</summary>
        private Bounds PalletBounds()
        {
            Renderer best = PalletRenderer();
            // No "pallet" renderer: fall back to the inside of the trailer.
            return best != null ? best.bounds : InteriorBounds();
        }

        /// <summary>The highest renderer with "pallet" in its name.</summary>
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

        /// <summary>Checks whether at least one real level point is assigned.</summary>
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

        /// <summary>Fills in the missing level references of the existing storage:
        /// keeps the defined number of columns/levels, places each point under its zone.</summary>
        private void EnsureExternalColumns()
        {
            if (HasAnyLevel(Storage)) return; // already wired by the user — touch nothing

            ShelfInteractionZone[] zones = Storage.GetComponentsInChildren<ShelfInteractionZone>(true);
            int columnCount = Storage.columns != null ? Mathf.Max(1, Storage.columns.Length) : 1;
            foreach (ShelfInteractionZone zone in zones)
                if (zone != null) columnCount = Mathf.Max(columnCount, zone.columnIndex + 1);

            Transform root = ChildOf(Storage.transform, "truck_levels");
            for (int i = root.childCount - 1; i >= 0; i--) // purge levels generated previously
                SafeDestroy(root.GetChild(i).gameObject);
            float topY = PalletTopY();

            // Drop anchors: the "marker" pads placed by the user on the pallet
            // (intended positions). Fallback: 25 %/75 % quadrants of the pallet bounds.
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
                    levelCount = Storage.columns[c].levels.Length; // keeps the user's shape

                // The zone's dock (otherwise the zone itself) tells the side of the column.
                Vector3 side = Storage.transform.position;
                foreach (ShelfInteractionZone zone in zones)
                    if (zone != null && zone.columnIndex == c)
                    {
                        side = zone.dockPoint != null ? zone.dockPoint.position : zone.transform.position;
                        break;
                    }
                Vector3 anchor = new Vector3(side.x < cx ? xLo : xHi, topY, side.z < cz ? zLo : zHi);
                // A marker pad exists on this side: it is the intended drop position.
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
                    // Initial estimate — UpdateLevelHeights then aligns it with the real stack.
                    level.position = new Vector3(anchor.x, topY + .02f + l * .55f, anchor.z);
                    levels[l] = level;
                }
                newColumns[c] = new ShelfStorage.StorageColumn { levels = levels };
            }
            Storage.columns = newColumns;
            ReinitStorage();
            Debug.Log("[Truck] levels generated on " + Storage.name + ": " + columnCount + " columns");
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

        /// <summary>Replays the ShelfStorage's internal initialization (boxes table + scan).</summary>
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

        /// <summary>Station zone whose dock is closest to the door.</summary>
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
        /// Dock zone to use for the next drop: the least filled column
        /// (the boxes spread over the pallet instead of piling up in the same corner).
        /// On a tie, the dock closest to the given position.
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
                if (free <= 0) continue; // column full
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
            // Side walls (z) + back wall (x). The door stays open.
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
            float xStart = doorX + dir * .7f; // first slot right behind the door

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

            // ShelfStorage fills its internal table in Awake — which has already run (or never,
            // if the object was inactive). We rebuild it the way the autopilot does.
            ReinitStorage();
        }

        private void BuildDockZone(float doorX, float dir, float centerZ, float floorY)
        {
            Transform zoneT = Child("truck_dock_zone");
            zoneT.position = new Vector3(doorX - dir * DockOffset, floorY, centerZ);
            zoneT.rotation = Quaternion.LookRotation(Vector3.right * dir, Vector3.up); // facing the truck

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
            ApplyBeaconColor(new Color(1f, .65f, .1f)); // orange = ready
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

        // ------------------------------------------------------------------ state

        /// <summary>Box sitting on the station (child of the storage or of a slot).</summary>
        public bool OwnsBox(CargoBox box)
        {
            if (box == null) return false;
            if (ExternalMode) return Storage != null && box.transform.IsChildOf(Storage.transform);
            return slotsRoot != null && box.transform.IsChildOf(slotsRoot);
        }

        /// <summary>First free slot/level, least filled column first (spreading).</summary>
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
        /// Logical validation of a delivery: the box must end up under the station.
        /// Returns true + updates counters/indicators, false otherwise (with a warning).
        /// </summary>
        public bool ValidateStored(CargoBox box)
        {
            if (box == null) return false;
            if (!OwnsBox(box))
            {
                Debug.LogWarning("[Truck] " + box.name + " reported delivered but is not under the station.");
                return false;
            }
            ClampInsidePallet(box);
            NotifyStored(box);
            return true;
        }

        /// <summary>
        /// Repositions the box so that its whole footprint stays on the pallet:
        /// the pads can be close to the edge, a big box must not stick out.
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

        /// <summary>Counting + indicators + event, idempotent.</summary>
        public void NotifyStored(CargoBox box)
        {
            Recount();
            beaconGreen = true;
            beaconUntil = Time.time + 1.2f;
            Debug.Log("[Truck] delivery validated: " + box.name + " (" + LoadedCount + "/" + Capacity + ")" +
                      (IsFull ? " — TRUCK FULL" : ""));
            Stored?.Invoke(box);
            RefreshIndicators(true);
        }

        /// <summary>Manual drop (fallback when the Warcool interaction does not answer).</summary>
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
                // Keeps the ShelfStorage's internal table consistent with the scene.
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
        /// Each empty level aligns with the real top of the stack below
        /// (measured through the renderers' bounds): the stacking stays tight
        /// whatever the height of the boxes.
        /// </summary>
        private void UpdateLevelHeights()
        {
            if (Storage == null || Storage.columns == null) return;
            const float gap = .005f;
            foreach (ShelfStorage.StorageColumn column in Storage.columns)
            {
                if (column == null || column.levels == null) continue;
                // A level = the support surface (top of the pallet, then top of the box below).
                float surface = baseSurfaceY + gap;
                foreach (Transform level in column.levels)
                {
                    if (level == null) continue;
                    Vector3 p = level.position;
                    if (Mathf.Abs(p.y - surface) > .001f) level.position = new Vector3(p.x, surface, p.z);

                    CargoBox box = level.GetComponentInChildren<CargoBox>(true);
                    if (box == null || box.isCarried) continue;
                    // The pivot of the boxes is at the center: we align the REAL BOTTOM of the box
                    // (renderer bounds) with the surface, centered on the level point.
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
