using System.Collections;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using RoboFactory.Logistics;
using RoboFactory.Sim;

namespace RoboFactory.Integration
{
    /// <summary>
    /// Automated pick &amp; place for the Warcool warehouse robot.
    ///
    /// The robot keeps its own locomotion model: it drives on a 0.25 m grid, turns in 45° steps and
    /// must be exactly docked (position + heading) before it can take or release a carton. This
    /// pilot plans an A* route on that grid, drives it cell by cell, aligns on the dock point and
    /// then uses the asset's own interaction (RobotCargoInteractor.TryInteract) so the load/unload
    /// animations and the physical attachment stay exactly as authored.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WarehouseRobotAutopilot : MonoBehaviour
    {
        [Header("Robot")]
        public GridRobotController Controller;
        public Animator Animator;
        public RobotCargoInteractor Interactor;

        [Header("Grille (doit correspondre au GridRobotController)")]
        public float Cell = .25f;
        public float RotationStep = 45f;
        [Tooltip("Colliders ignorés par le calcul d'itinéraire. Vide = tous les layers.")]
        public LayerMask ObstacleMask = ~0;
        public Vector3 CheckHalfExtents = new Vector3(.18f, .28f, .18f);
        public Vector3 CheckOffset = new Vector3(0f, .28f, 0f);

        [Header("Conduite")]
        public float MoveSpeed = 1.8f;
        public float TurnSpeed = 260f;
        [Tooltip("Petite pause à chaque case: le robot avance bien case par case.")]
        public float StepPause = 0f;
        public float ArriveTolerance = .01f;
        [Tooltip("Aucun trajet trouvé : le robot attend sur place et réessaie après ce délai.")]
        public float ReplanInterval = .6f;

        [Header("Mission")]
        public bool AutoStart = true;
        public float IdlePause = .6f;
        public bool LogActions = true;

        public ShelfInteractionZone CurrentTarget { get; private set; }
        public string Status { get; private set; } = "arrêt";
        public int Delivered { get; private set; }
        public int SortedCount => sorted.Count;
        private readonly HashSet<CargoBox> sorted = new HashSet<CargoBox>();
        private readonly HashSet<CargoBox> blocked = new HashSet<CargoBox>();
        private readonly HashSet<ShelfInteractionZone> blockedZones = new HashSet<ShelfInteractionZone>();
        private bool legOk;
        private bool warnedNoSource;
        private Vector3 homePosition;
        private Quaternion homeRotation;
        private bool homeCaptured;
        private readonly int[] orderRemaining = new int[3];
        private bool orderActive;
        public bool OrderActive => orderActive;
        public int OrderRemaining(CargoKind kind)
        {
            int index = KindIndex(kind);
            return index >= 0 ? orderRemaining[index] : 0;
        }
        private ShelfInteractionZone lastSource;
        private ShelfInteractionZone lastTarget;
        private Transform carryRoot;
        private CargoBox carried;
        private readonly Dictionary<ShelfInteractionZone, List<CargoBox>> inventory =
            new Dictionary<ShelfInteractionZone, List<CargoBox>>();
        private float clearance = 1f;
        private readonly Dictionary<CargoKind, List<ShelfInteractionZone>> reserved =
            new Dictionary<CargoKind, List<ShelfInteractionZone>>();
        public int Picked { get; private set; }
        public bool Running { get; private set; }
        public static bool Verbose = true;
        private readonly List<Vector3> displayPath = new List<Vector3>();
        /// <summary>Itineraire courant, conserve pendant les recalculs (affichage stable).</summary>
        public IReadOnlyList<Vector3> CurrentPath => displayPath;
        private bool replanRequested;
        /// <summary>Demande un nouveau calcul d'itineraire (obstacle detecte sur le trajet).</summary>
        public void RequestReplan() { replanRequested = true; }
        [Tooltip("Trie par famille : caisses bois ensemble, gros cartons ensemble, cartons moyens ensemble.")]
        public bool SortByKind = true;
        [Tooltip("Autorise à poser un colis dans une colonne d'une autre famille quand il n'y a plus de place. Désactivé = sécurité de tri.")]
        public bool AllowMixing = false;
        [Tooltip("Force la destination sur ce rangement (banc). Vide = le banc libre le plus proche de la palette en cours.")]
        public ShelfStorage ForcedBench;

        [Header("Commande client")]
        [Tooltip("A = caisses bois, B = gros cartons, C = cartons moyens. Exemple : A x2, B x3, C x1.")]
        public string OrderText = "A x2, B x3, C x1";
        [Tooltip("Zone de dechargement ou la commande est livree. Vide = le banc libre le plus proche.")]
        public ShelfStorage DeliveryZone;
        [Tooltip("Execute la commande saisie au demarrage. Sinon le robot trie tout le stock.")]
        public bool RunOrderOnStart = true;

        [Header("Planification")]
        [Tooltip("Demi-portée de la zone navigable en mètres. 0 = automatique (grille du GridRobotController).")]
        public float MapHalfExtent = 0f;

        [Header("Affichage")]
        [Tooltip("Dessine l'itinéraire dans la Game view et dans le build (les gizmos ne marchent que dans l'éditeur).")]
        public bool ShowPathInGame = true;
        public float PathLineWidth = .03f;

        [Header("Debug grille")]
        [Tooltip("Colore les cases de la grille : vert = franchissable, rouge = bloquée.")]
        public bool ShowGridDebug = true;
        [Tooltip("Délai entre deux rafraîchissements de la grille de debug, en secondes.")]
        public float GridRefresh = .5f;
        public Color FreeCellColor = new Color(0f, .9f, .3f, .22f);
        public Color BlockedCellColor = new Color(1f, .15f, .1f, .35f);

        private readonly List<ShelfInteractionZone> zones = new List<ShelfInteractionZone>();
        private readonly List<ShelfInteractionZone> orderedZones = new List<ShelfInteractionZone>();
        private readonly List<Vector3> path = new List<Vector3>();
        private int pathCursor; // nombre de cellules déjà parcourues dans path
        private float halfExtent;
        private bool ready;

        private void Awake()
        {
            if (Controller == null) Controller = GetComponent<GridRobotController>();
            if (Animator == null) Animator = GetComponent<Animator>();
            if (Interactor == null) Interactor = GetComponent<RobotCargoInteractor>();
        }

        private void Start()
        {
            Debug.Log("[Autopilot] Start sur " + name + " (AutoStart=" + AutoStart + ", controller=" +
                      (Controller != null) + ", actif=" + gameObject.activeInHierarchy + ")");
            if (AutoStart) Begin();
        }

        public void Begin()
        {
            if (Running) return;
            if (Controller == null)
            {
                Debug.LogError("[Autopilot] GridRobotController introuvable sur " + name);
                return;
            }
            // The pilot owns the motion: the keyboard controller must not fight it.
            Controller.controlsLocked = true;
            halfExtent = MapHalfExtent > 0f ? MapHalfExtent
                : Mathf.Max(1f, Mathf.Max(Controller.gridCellsX, Controller.gridCellsZ) * Controller.cellSize * .5f);
            RefreshZones();
            SyncStorages();
            Running = true;
            StartCoroutine(Mission());
        }

        public void Stop()
        {
            Running = false;
            StopAllCoroutines();
            Status = "arrêt";
        }

        /// <summary>
        /// Démarre en mode dirigé : le robot est prêt (grille, inventaire, contrôles
        /// verrouillés) mais la boucle autonome Mission() ne tourne PAS. Seules les
        /// missions PickMission envoyées par l'orchestrateur s'exécutent.
        /// </summary>
        public void BeginDirected()
        {
            if (Running) return;
            if (Controller == null)
            {
                Debug.LogError("[Autopilot] GridRobotController introuvable sur " + name);
                return;
            }
            Controller.controlsLocked = true;
            halfExtent = MapHalfExtent > 0f ? MapHalfExtent
                : Mathf.Max(1f, Mathf.Max(Controller.gridCellsX, Controller.gridCellsZ) * Controller.cellSize * .5f);
            RefreshZones();
            SyncStorages();
            if (!homeCaptured)
            {
                homePosition = transform.position;
                homeRotation = transform.rotation;
                homeCaptured = true;
            }
            Running = true;
            Status = "en attente d'une mission dirigée";
            Debug.Log("[Autopilot] mode dirigé sur " + name + " : " + zones.Count + " zones, en attente de l'orchestrateur");
        }

        private void RefreshZones()
        {
            zones.Clear();
            zones.AddRange(FindObjectsByType<ShelfInteractionZone>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            int found = zones.Count;
            int removed = zones.RemoveAll(z => z == null || z.shelf == null || z.dockPoint == null);
            SortZones();
            Debug.Log("[Autopilot] scan: trouves=" + found + " retires=" + removed + " retenus=" + zones.Count +
                      " ordonnes=" + orderedZones.Count + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        }

        // ------------------------------------------------------------------ mission

        /// <summary>Famille de colis, déduite du prefab (les CargoBox n'ont pas de type sérialisé).</summary>
        public enum CargoKind { Unknown, WoodenCrate, BigCardboard, MediumCardboard, Mixed }

        public static CargoKind KindOf(CargoBox box)
        {
            if (box == null) return CargoKind.Unknown;
            string n = box.name.ToLowerInvariant();
            if (n.Contains("wooden")) return CargoKind.WoodenCrate;
            if (n.Contains("big")) return CargoKind.BigCardboard;
            if (n.Contains("cardboard") || n.Contains("carton")) return CargoKind.MediumCardboard;
            return CargoKind.Unknown;
        }

        /// <summary>Famille déjà rangée dans une colonne (Unknown = colonne vide).</summary>
        public static CargoKind ZoneKind(ShelfInteractionZone zone)
        {
            if (zone == null || zone.shelf == null || zone.shelf.columns == null) return CargoKind.Unknown;
            if (zone.columnIndex < 0 || zone.columnIndex >= zone.shelf.columns.Length) return CargoKind.Unknown;
            ShelfStorage.StorageColumn column = zone.shelf.columns[zone.columnIndex];
            if (column == null || column.levels == null) return CargoKind.Unknown;
            CargoKind result = CargoKind.Unknown;
            foreach (Transform level in column.levels)
            {
                if (level == null) continue;
                CargoBox box = level.GetComponentInChildren<CargoBox>(true);
                if (box == null) continue;
                CargoKind kind = KindOf(box);
                if (result == CargoKind.Unknown) result = kind;
                else if (result != kind) return CargoKind.Mixed; // colonne déjà mélangée : jamais réutilisée
            }
            return result;
        }

        /// <summary>
        /// Re-registers every box found in the scene into its ShelfStorage column/level.
        /// ShelfStorage fills its private table in Awake, which does not run for inactive objects:
        /// without this the asset answers "no box here" and the pilot has no source to pick.
        /// </summary>
        private void SyncStorages()
        {
            // The pilot keeps its own inventory read straight from the scene. ShelfStorage builds its
            // private table in Awake, which never runs on a disabled component: relying on it made
            // the asset answer "no box" and "no room" for every column.
            int total = 0;
            var initialised = new HashSet<ShelfStorage>();
            foreach (ShelfInteractionZone zone in zones)
            {
                Transform[] levels = ColumnLevels(zone);
                if (levels == null) continue;
                if (initialised.Add(zone.shelf)) EnsureStorageInitialized(zone.shelf);
                foreach (Transform level in levels)
                {
                    if (level == null) continue;
                    if (level.GetComponentInChildren<CargoBox>(true) != null) total++;
                }
            }
            if (carryRoot == null) BuildCarryRoot();
            if (LogActions) Debug.Log("[Autopilot] inventaire : " + total + " colis dans " + zones.Count + " zones.");
        }

        /// <summary>ShelfStorage builds its table in Awake; a disabled component never runs it.</summary>
        private static void EnsureStorageInitialized(ShelfStorage shelf)
        {
            if (shelf == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ShelfStorage).GetMethod("InitializeStorage", flags)?.Invoke(shelf, null);
            typeof(ShelfStorage).GetMethod("AutoFindExistingBoxes", flags)?.Invoke(shelf, null);
        }

        /// <summary>The carry anchor the asset itself uses (created in RobotCargoInteractor.Awake).</summary>
        private Transform AssetCarryRoot()
        {
            if (Interactor == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            return typeof(RobotCargoInteractor).GetField("activeCarryRoot", flags)?.GetValue(Interactor) as Transform;
        }

        private void BuildCarryRoot()
        {
            var root = new GameObject("Autopilot Carry Root");
            carryRoot = root.transform;
            carryRoot.SetParent(transform, false);
            // Same anchor the asset uses (RobotCargoInteractor.cargoAttachPoint): the carton rides
            // on the robot's arms above its head, not inside its body.
            carryRoot.localPosition = Interactor != null && Interactor.cargoAttachPoint != null
                ? transform.InverseTransformPoint(Interactor.cargoAttachPoint.position)
                : new Vector3(0f, .75f, 0f);
            carryRoot.localRotation = Quaternion.identity;
        }

        private int LevelOf(ShelfInteractionZone zone, CargoBox box)
        {
            Transform[] levels = ColumnLevels(zone);
            if (levels == null) return 0;
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] != null && levels[i].GetComponentInChildren<CargoBox>(true) == box) return i;
            return 0;
        }

        /// <summary>A=0 caisses bois, B=1 gros cartons, C=2 cartons moyens.</summary>
        private static int KindIndex(CargoKind kind)
        {
            switch (kind)
            {
                case CargoKind.WoodenCrate: return 0;
                case CargoKind.BigCardboard: return 1;
                case CargoKind.MediumCardboard: return 2;
                default: return -1;
            }
        }

        private static CargoKind KindFromIndex(int index)
        {
            switch (index)
            {
                case 0: return CargoKind.WoodenCrate;
                case 1: return CargoKind.BigCardboard;
                case 2: return CargoKind.MediumCardboard;
                default: return CargoKind.Unknown;
            }
        }

        /// <summary>Lit une commande du type "A x2, B x3, C x1".</summary>
        public bool ParseOrder(string text)
        {
            orderRemaining[0] = orderRemaining[1] = orderRemaining[2] = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.ToUpperInvariant().Split(new[] { ',', ';', '+' }, System.StringSplitOptions.RemoveEmptyEntries);
            int parsed = 0;
            foreach (string part in parts)
            {
                char letter = '?';
                foreach (char c in part) if (c >= 'A' && c <= 'C') { letter = c; break; }
                if (letter == '?') continue;
                string digits = string.Empty;
                foreach (char c in part) if (char.IsDigit(c)) digits += c;
                if (!int.TryParse(digits, out int count)) continue;
                orderRemaining[letter - 'A'] = Mathf.Max(0, count);
                parsed += count;
            }
            orderActive = parsed > 0;
            return orderActive;
        }

        /// <summary>Carton le plus haut de la colonne, lu directement dans la scène.</summary>
        private CargoBox TopBox(ShelfInteractionZone zone)
        {
            Transform[] levels = ColumnLevels(zone);
            if (levels == null) return null;
            for (int i = levels.Length - 1; i >= 0; i--)
            {
                if (levels[i] == null) continue;
                CargoBox box = levels[i].GetComponentInChildren<CargoBox>(true);
                if (box != null) return box;
            }
            return null;
        }

        private bool HasRoom(ShelfInteractionZone zone)
        {
            Transform[] levels = ColumnLevels(zone);
            if (levels == null) return false;
            // Colonne dont le carton du dessus est réservé (Robot 2 vient le chercher) :
            // déposer par-dessus l'enterrerait — on choisit une autre colonne.
            var registry = RoboFactory.Logistics.CargoRegistry.Instance;
            if (registry != null)
            {
                for (int i = levels.Length - 1; i >= 0; i--)
                {
                    if (levels[i] == null) continue;
                    CargoBox top = levels[i].GetComponentInChildren<CargoBox>(true);
                    if (top == null) continue;
                    if (registry.IsReserved(top)) return false;
                    break;
                }
            }
            foreach (Transform level in levels)
                if (level == null || level.GetComponentInChildren<CargoBox>(true) == null) return true;
            return false;
        }

        /// <summary>Numero entre parentheses du nom (pallet (2) -> 2, pallet -> 0).</summary>
        private static int NaturalIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int open = name.LastIndexOf('(');
            if (open >= 0 && name.EndsWith(")"))
            {
                string inner = name.Substring(open + 1, name.Length - open - 2);
                if (int.TryParse(inner, out int number)) return number;
            }
            return 0;
        }

        /// <summary>
        /// Ordre de travail deterministe : palettes d'abord (palette 1, puis 2, ...), bancs ensuite.
        /// Le pilote vide donc entierement la palette 1 avant de toucher a la suivante.
        /// </summary>
        private void SortZones()
        {
            orderedZones.Clear();
            orderedZones.AddRange(zones);
            orderedZones.Sort((a, b) =>
            {
                int benchA = IsBench(a) ? 1 : 0, benchB = IsBench(b) ? 1 : 0;
                if (benchA != benchB) return benchA.CompareTo(benchB);
                int indexA = NaturalIndex(a.shelf.name), indexB = NaturalIndex(b.shelf.name);
                if (indexA != indexB) return indexA.CompareTo(indexB);
                int byName = string.CompareOrdinal(a.shelf.name, b.shelf.name);
                if (byName != 0) return byName;
                return a.columnIndex.CompareTo(b.columnIndex);
            });
        }

        /// <summary>Niveaux réels de la colonne, ou null si la zone ne pointe pas sur une colonne valide.</summary>
        public static Transform[] ColumnLevels(ShelfInteractionZone zone)
        {
            if (zone == null || zone.shelf == null || zone.shelf.columns == null) return null;
            if (zone.columnIndex < 0 || zone.columnIndex >= zone.shelf.columns.Length) return null;
            ShelfStorage.StorageColumn column = zone.shelf.columns[zone.columnIndex];
            return column != null ? column.levels : null;
        }

        /// <summary>Vrai si aucun colis n'occupe la colonne (lecture directe de la scène).</summary>
        public static bool IsEmptyColumn(ShelfInteractionZone zone)
        {
            Transform[] levels = ColumnLevels(zone);
            if (levels == null) return false;
            foreach (Transform level in levels)
                if (level != null && level.GetComponentInChildren<CargoBox>(true) != null) return false;
            return true;
        }

        /// <summary>Banc de tri (shelving) ou zone de stockage (palette).</summary>
        public static bool IsBench(ShelfInteractionZone zone)
        {
            return zone != null && zone.shelf != null && zone.shelf.name.ToLowerInvariant().Contains("shelving");
        }

        /// <summary>Zone montée sur le camion — jamais une source pour le robot 1.</summary>
        private static bool IsTruck(ShelfInteractionZone zone)
        {
            return zone != null && zone.shelf != null &&
                   zone.shelf.GetComponentInParent<TruckBay>() != null;
        }

        public static string KindName(CargoKind kind)
        {
            switch (kind)
            {
                case CargoKind.WoodenCrate: return "caisse bois";
                case CargoKind.BigCardboard: return "gros carton";
                case CargoKind.MediumCardboard: return "carton moyen";
                case CargoKind.Mixed: return "colonne mélangée";
                default: return "inconnu";
            }
        }

        private IEnumerator Mission()
        {
            yield return null; // let GridRobotController.Start snap the robot on the grid
            if (RunOrderOnStart && ParseOrder(OrderText))
                Debug.Log("[Autopilot] commande : " + OrderText + " (A=" + orderRemaining[0] +
                          " B=" + orderRemaining[1] + " C=" + orderRemaining[2] + ")");
            if (!homeCaptured)
            {
                homePosition = transform.position;
                homeRotation = transform.rotation;
                homeCaptured = true;
            }
            while (Running)
            {
                CargoBox box;
                ShelfInteractionZone source = FindSource(out box);
                if (source == null)
                {
                    if (homeCaptured && Vector3.Distance(transform.position, homePosition) > .35f)
                        yield return GoHome();
                    Status = SortedCount + " colis triés - en attente d'ordres";
                    yield return new WaitForSeconds(2f);
                    RefreshZones();
                    continue;
                }

                lastSource = source;
                CargoKind kind = KindOf(box);
                Status = "prise " + KindName(kind);
                yield return DriveToZone(source);
                if (!legOk) { blocked.Add(box); yield return new WaitForSeconds(.2f); continue; }
                yield return Interact(true, box);
                if (carried != box)
                {
                    blocked.Add(box);
                    yield return new WaitForSeconds(.3f);
                    continue;
                }

                ShelfInteractionZone target = FindTarget(source, kind);
                lastTarget = target;
                if (target == null)
                {
                    // No room for this family: skip that box instead of fetching it forever.
                    blocked.Add(box);
                    Status = "aucune place pour " + KindName(kind);
                    yield return new WaitForSeconds(.3f);
                    RefreshZones();
                    continue;
                }

                Status = "dépose " + KindName(kind) + " sur " + target.name;
                if (LogActions)
                    Debug.Log("[Autopilot] " + KindName(kind) + " -> " + target.name + " (colonne " +
                              target.columnIndex + ", famille actuelle " + KindName(ZoneKind(target)) + ")");
                yield return DriveToZone(target);
                if (!legOk) { blockedZones.Add(target); yield return new WaitForSeconds(.2f); continue; }
                yield return Interact(false, box);
                if (carried == null)
                {
                    CargoKind landed = ZoneKind(target);
                    if (landed != kind)
                        Debug.LogError("[Autopilot] TRI INVALIDE : " + KindName(kind) + " posé dans une colonne " +
                                       KindName(landed) + " (" + target.name + "). Colonne à vérifier.");
                    sorted.Add(box);
                    Delivered++;
                }
                if (orderActive)
                {
                    int index = KindIndex(kind);
                    if (index >= 0 && orderRemaining[index] > 0) orderRemaining[index]--;
                    Debug.Log("[Autopilot] commande : " + KindName(kind) + " livre, reste A=" + orderRemaining[0] +
                              " B=" + orderRemaining[1] + " C=" + orderRemaining[2]);
                    if (orderRemaining[0] + orderRemaining[1] + orderRemaining[2] == 0)
                    {
                        orderActive = false;
                        Status = "commande terminee";
                        Debug.Log("[Autopilot] commande terminee : retour au poste.");
                        yield return GoHome();
                        Running = false;           // la mission s'arrete : plus de tri automatique
                        Status = "en attente d'ordres";
                        yield break;
                    }
                }
                Status = KindName(kind) + " livre (" + SortedCount + ")";
                yield return new WaitForSeconds(IdlePause);
            }
        }

        /// <summary>
        /// Takes a box that is not sorted yet. Boxes already delivered by the pilot are skipped, which
        /// is what previously made it pick up the box it had just put down.
        /// </summary>
        private ShelfInteractionZone FindSource(out CargoBox box)
        {
            box = null;
            // The storage objects can still be inactive when the scene starts: re-scan on demand.
            if (orderedZones.Count == 0) RefreshZones();
            foreach (ShelfInteractionZone zone in orderedZones)
            {
                if (blockedZones.Contains(zone)) continue;
                // Sources = palettes uniquement : les bancs appartiennent au robot 2,
                // et le robot 1 ne décharge jamais le camion.
                if (IsBench(zone) || IsTruck(zone)) continue;
                CargoBox candidate = TopBox(zone);
                if (candidate == null || sorted.Contains(candidate) || blocked.Contains(candidate)) continue;
                if (orderActive)
                {
                    int index = KindIndex(KindOf(candidate));
                    if (index < 0 || orderRemaining[index] <= 0) continue; // pas dans la commande
                }
                box = candidate;
                return zone; // ordre impose : on finit la palette en cours avant la suivante
            }
            if (!warnedNoSource)
            {
                warnedNoSource = true;
                ShelfInteractionZone first = orderedZones.Count > 0 ? orderedZones[0] : null;
                Debug.LogWarning("[Autopilot] aucune source : zones=" + orderedZones.Count +
                                 " premier=" + (first != null ? first.shelf.name + " col " + first.columnIndex : "-") +
                                 " niveaux=" + (first != null && ColumnLevels(first) != null ? ColumnLevels(first).Length : -1) +
                                 " carton=" + (first != null && TopBox(first) != null) +
                                 " bloques=" + blocked.Count);
            }
            return null;
        }

        /// <summary>
        /// Choix deterministe : colonne deja reservee a la famille, sinon la colonne vide la PLUS
        /// PROCHE du point de prise, sur un banc. Aucun melange possible, aucun choix au hasard.
        /// Si ForcedBench est renseigne, seul ce rangement est utilise.
        /// </summary>
        private ShelfInteractionZone FindTarget(ShelfInteractionZone source, CargoKind kind)
        {
            if (kind == CargoKind.Unknown || kind == CargoKind.Mixed) return null;
            if (!reserved.TryGetValue(kind, out List<ShelfInteractionZone> owned))
            {
                owned = new List<ShelfInteractionZone>();
                reserved[kind] = owned;
            }

            Vector3 from = source.dockPoint != null ? source.dockPoint.position : transform.position;

            // Order mode: everything goes to the unloading zone.
            if (orderActive && DeliveryZone != null)
            {
                ShelfInteractionZone drop = DeliveryColumn(from, kind);
                if (drop != null) return drop;
            }

            // 1) colonne deja reservee a cette famille, encore libre : la plus proche
            ShelfInteractionZone best = Nearest(owned, from);
            if (best != null) return best;

            // 2) nouvelle colonne totalement vide sur un banc, la plus proche
            best = NearestEmpty(true, from, source);
            if (best != null) { owned.Add(best); return best; }

            // 3) a defaut, n'importe quelle colonne vide
            best = NearestEmpty(false, from, source);
            if (best != null) { owned.Add(best); return best; }

            return null; // plus de place pour cette famille : le colis est mis de cote
        }

        /// <summary>
        /// Livraison dans la zone de dechargement, mais SANS jamais melanger : colonne deja dediee
        /// a cette famille, sinon colonne vide. Une colonne melangee est exclue.
        /// </summary>
        private ShelfInteractionZone DeliveryColumn(Vector3 from, CargoKind kind)
        {
            ShelfInteractionZone sameKind = null, empty = null;
            float sameDistance = float.PositiveInfinity, emptyDistance = float.PositiveInfinity;
            foreach (ShelfInteractionZone zone in orderedZones)
            {
                if (zone == null || zone.shelf != DeliveryZone || blockedZones.Contains(zone)) continue;
                if (IsTruck(zone)) continue;
                if (!HasRoom(zone)) continue;
                CargoKind content = ZoneKind(zone);
                float distance = Vector3.Distance(from, zone.dockPoint.position);
                if (content == kind && distance < sameDistance) { sameDistance = distance; sameKind = zone; }
                else if (content == CargoKind.Unknown && distance < emptyDistance) { emptyDistance = distance; empty = zone; }
            }
            return sameKind != null ? sameKind : empty;
        }

        private ShelfInteractionZone Nearest(List<ShelfInteractionZone> candidates, Vector3 from)
        {
            ShelfInteractionZone best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (ShelfInteractionZone zone in candidates)
            {
                if (zone == null || blockedZones.Contains(zone) || IsTruck(zone)) continue;
                if (!HasRoom(zone)) continue;
                float distance = Vector3.Distance(from, zone.dockPoint.position);
                if (distance < bestDistance) { bestDistance = distance; best = zone; }
            }
            return best;
        }

        private ShelfInteractionZone NearestEmpty(bool benchesOnly, Vector3 from, ShelfInteractionZone source)
        {
            ShelfInteractionZone best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (ShelfInteractionZone zone in orderedZones)
            {
                if (zone == source || blockedZones.Contains(zone) || IsTruck(zone)) continue;
                if (benchesOnly != IsBench(zone)) continue;
                if (ForcedBench != null && zone.shelf != ForcedBench) continue;
                if (!IsEmptyColumn(zone)) continue;
                if (!HasRoom(zone)) continue;
                bool promised = false;
                foreach (List<ShelfInteractionZone> list in reserved.Values)
                    if (list.Contains(zone)) { promised = true; break; }
                if (promised) continue;
                float distance = Vector3.Distance(from, zone.dockPoint.position);
                if (distance < bestDistance) { bestDistance = distance; best = zone; }
            }
            return best;
        }

        // ------------------------------------------------------------------ interaction

        private IEnumerator Interact(bool load, CargoBox box)
        {
            CurrentTarget = null;
            Status = load ? "chargement" : "déchargement";

            // First choice: the asset's own interaction (its carry anchor, its animations, its
            // placement code). The pilot only falls back if the asset refuses to react.
            if (Interactor != null)
            {
                Interactor.SendMessage("TryInteract", SendMessageOptions.DontRequireReceiver);
                float deadline = Time.time + 3f;
                while (Time.time < deadline && Interactor.IsBusy) yield return null;
                while (Time.time < deadline && Interactor.HasBox != load) yield return null;
                if (Interactor.HasBox == load)
                {
                    carried = load ? box : null;
                    if (load) Picked++;
                    yield break;
                }
                Debug.LogWarning("[Autopilot] l'interaction Warcool n'a pas répondu, transfert manuel.");
            }

            if (load)
            {
                int level = Mathf.Clamp(LevelOf(lastSource, box), 0, 2);
                PlayTrigger("Load_Level_" + level);
                yield return new WaitForSeconds(.45f);
                Attach(box);
                yield return new WaitForSeconds(.25f);
                Picked++;
            }
            else
            {
                int level = Mathf.Clamp(FreeLevel(lastTarget), 0, 2);
                PlayTrigger("Unload_Level_" + level);
                yield return new WaitForSeconds(.55f);
                Place(box, lastTarget, level);
                yield return new WaitForSeconds(.25f);
            }
        }

        private void PlayTrigger(string name)
        {
            if (Animator == null) return;
            foreach (AnimatorControllerParameter parameter in Animator.parameters)
                if (parameter.name == name && parameter.type == AnimatorControllerParameterType.Trigger)
                {
                    Animator.SetTrigger(name);
                    return;
                }
        }

        private void Attach(CargoBox box)
        {
            if (box == null) return;
            Transform anchor = AssetCarryRoot();
            if (anchor == null) { if (carryRoot == null) BuildCarryRoot(); anchor = carryRoot; }
            Quaternion rotation = box.transform.rotation;
            box.transform.SetParent(anchor, true);
            box.transform.position = anchor.position;
            box.transform.rotation = rotation;
            box.isCarried = true;
            carried = box;
            if (Animator != null) Animator.SetBool("HasBox", true);
        }

        private int FreeLevel(ShelfInteractionZone zone)
        {
            Transform[] levels = ColumnLevels(zone);
            if (levels == null) return 0;
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] != null && levels[i].GetComponentInChildren<CargoBox>(true) == null) return i;
            return 0;
        }

        private void Place(CargoBox box, ShelfInteractionZone zone, int level)
        {
            if (box == null || zone == null) return;
            Transform[] levels = ColumnLevels(zone);
            Transform point = levels != null && level < levels.Length ? levels[level] : null;
            if (point == null) return;
            // Exactly like RobotCargoInteractor.DetachBoxToShelf: the carton keeps the orientation
            // it had while carried, only its position snaps to the storage point.
            Quaternion rotation = box.transform.rotation;
            box.transform.SetParent(point, true);
            box.transform.position = point.position;
            box.transform.rotation = rotation;
            box.isCarried = false;
            carried = null;
            if (Animator != null) Animator.SetBool("HasBox", false);
        }

        /// <summary>Returns to the post and waits there, ready for a new order.</summary>
        private IEnumerator GoHome()
        {
            Status = "retour au poste";
            if (Plan(Snap(transform.position), Snap(homePosition)))
                yield return FollowPath();

            Vector3 flat = new Vector3(homePosition.x, transform.position.y, homePosition.z);
            while (Vector3.Distance(transform.position, flat) > .02f && Running)
            {
                transform.position = Vector3.MoveTowards(transform.position, flat, MoveSpeed * Time.deltaTime);
                yield return null;
            }
            transform.position = flat;

            while (Quaternion.Angle(transform.rotation, homeRotation) > .5f && Running)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, homeRotation, TurnSpeed * Time.deltaTime);
                yield return null;
            }
            transform.rotation = homeRotation;
            Status = "en attente d'ordres";
        }

        // ------------------------------------------------------------------ navigation

        private IEnumerator DriveToZone(ShelfInteractionZone zone)
        {
            CurrentTarget = zone;
            legOk = false;
            Status = "vers " + zone.name;
            Vector3 dock = zone.dockPoint.position;
            Vector3 heading = zone.dockPoint.forward;
            heading.y = 0f;
            if (heading.sqrMagnitude < .001f) heading = transform.forward;
            heading.Normalize();

            // 1) grid route to the cell nearest the dock, then 2) a short fine approach that lands
            //    exactly on the dock point with the exact heading the zone expects.
            Vector3 startCell = Snap(transform.position);
            Vector3 goalCell = Snap(dock);
            // Déjà sur la cellule du dock (ex. suspendu devant cette palette) : pas d'itinéraire
            // grille. Sinon TryPlan visait une voisine souvent DANS le rack -> replan infini.
            if (ToCell(startCell) == ToCell(goalCell))
            {
                path.Clear();
                pathCursor = 0;
                displayPath.Clear();
                replanRequested = false;
                yield return TurnTo(heading);
                yield return FineApproach(dock, heading);
                yield return new WaitForSeconds(.1f);
                legOk = true;
                yield break;
            }
            bool planned = TryPlan(startCell, goalCell);
            // Aucun itinéraire : idle sur place et nouvel essai périodique — l'obstacle est
            // souvent temporaire (ouvrier, carton), le robot repart dès que le passage se libère.
            if (!planned || path.Count == 0)
            {
                Debug.LogWarning("[Autopilot] aucun itinéraire vers " + goalCell.ToString("F2") +
                                 " : attente sur place, nouvel essai toutes les " + ReplanInterval + " s.");
                while (Running)
                {
                    Status = "attente : trajet bloqué vers " + zone.name;
                    yield return new WaitForSeconds(ReplanInterval);
                    if (!Running) yield break;
                    planned = TryPlan(Snap(transform.position), goalCell);
                    if (planned && path.Count > 0) break;
                }
            }
            if (!planned || path.Count == 0)
            {
                path.Clear();
                pathCursor = 0;
                displayPath.Clear();
                yield break;
            }
            displayPath.Clear();
            displayPath.AddRange(path);
            while (true)
            {
                replanRequested = false;
                Vector3 before = transform.position;
                yield return FollowPath();
                if (!replanRequested) break;
                Status = "recalcul du trajet";
                if ((transform.position - before).sqrMagnitude < 1e-4f)
                    yield return new WaitForSeconds(ReplanInterval); // aucun progrès : attendre
                planned = TryPlan(Snap(transform.position), goalCell);
                // Plus de trajet non plus : attente sur place, pareil qu'au départ.
                while ((!planned || path.Count == 0) && Running)
                {
                    Status = "attente : trajet bloqué vers " + zone.name;
                    yield return new WaitForSeconds(ReplanInterval);
                    if (!Running) yield break;
                    planned = TryPlan(Snap(transform.position), goalCell);
                }
                if (!Running) yield break;
                displayPath.Clear();
                displayPath.AddRange(path);
            }

            // Fine approach only from close range: it must never become a cross-map shortcut.
            if (Vector3.Distance(transform.position, dock) > 1.2f)
            {
                Status = "arrêt hors portée de " + zone.name;
                Debug.LogWarning("[Autopilot] arrivée à " + Vector3.Distance(transform.position, dock).ToString("F2") +
                                 " m de " + zone.name + " : arrimage abandonné.");
                blockedZones.Add(zone);
                path.Clear();
                pathCursor = 0;
                displayPath.Clear();
                yield break;
            }

            replanRequested = false; // arrimage engagé : les demandes de replan ne concernent que le trajet grille
            yield return TurnTo(heading);
            yield return FineApproach(dock, heading);
            yield return new WaitForSeconds(.1f);
            displayPath.Clear();

            if (!zone.IsRobotCorrectlyDocked(transform))
                Debug.LogWarning("[Autopilot] arrimage approximatif à " + zone.name +
                                 " (position " + Vector3.Distance(transform.position, dock).ToString("3") + " m)");
            legOk = true;
        }

        /// <summary>
        /// Drives the grid route as continuous straight runs: one turn per direction change and one
        /// uninterrupted move through all the cells of that run, so the robot no longer stutters.
        /// </summary>
        private IEnumerator FollowPath()
        {
            int index = 0;
            while (index < path.Count && Running)
            {
                if (replanRequested) yield break;
                // Extend the run while the next cell keeps the same heading.
                int end = index;
                while (end + 1 < path.Count)
                {
                    Vector3 current = path[end] - transform.position;
                    current.y = 0f;
                    Vector3 next = path[end + 1] - path[end];
                    next.y = 0f;
                    if (current.sqrMagnitude > 1e-6f && next.sqrMagnitude > 1e-6f &&
                        Vector3.Dot(current.normalized, next.normalized) < .999f) break;
                    end++;
                }

                Vector3 head = path[end] - transform.position;
                head.y = 0f;
                if (head.sqrMagnitude > 1e-6f) yield return TurnTo(head.normalized);
                if (replanRequested) yield break;

                for (int i = index; i <= end && Running; i++)
                {
                    // Re-check live avant chaque cellule : un collider apparu sur la route
                    // déclenche un replan au lieu d'être traversé.
                    // Dernière cellule = dock, acceptée par conception par le planificateur.
                    if (i < path.Count - 1 && !IsFree(path[i]))
                    {
                        RequestReplan();
                        yield break;
                    }
                    yield return MoveTo(path[i]);
                    if (replanRequested) yield break; // cellule interrompue : pas consommée
                    pathCursor = i + 1; // cellule consommée : elle disparaît du tracé
                    if (displayPath.Count > 0) displayPath.RemoveAt(0);
                    if (StepPause > 0f && i < end) yield return new WaitForSeconds(StepPause);
                }

                index = end + 1;
            }
        }

        private IEnumerator MoveTo(Vector3 target)
        {
            target.y = transform.position.y;
            while (Vector3.Distance(transform.position, target) > ArriveTolerance && Running && !replanRequested)
            {
                transform.position = Vector3.MoveTowards(transform.position, target, MoveSpeed * Time.deltaTime);
                yield return null;
            }
            transform.position = target;
        }

        /// <summary>Turns in 45° steps, like the asset's own controller.</summary>
        private IEnumerator TurnTo(Vector3 worldDirection)
        {
            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude < 1e-6f) yield break;
            float wanted = Mathf.Round(Mathf.Atan2(worldDirection.x, worldDirection.z) * Mathf.Rad2Deg / RotationStep) * RotationStep;
            Quaternion target = Quaternion.Euler(0f, wanted, 0f);
            while (Quaternion.Angle(transform.rotation, target) > .5f && Running && !replanRequested)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, target, TurnSpeed * Time.deltaTime);
                yield return null;
            }
            transform.rotation = target;
        }

        /// <summary>Last few centimetres: exact position and heading so the zone accepts the robot.</summary>
        private IEnumerator FineApproach(Vector3 dock, Vector3 heading)
        {
            Quaternion target = Quaternion.LookRotation(heading, Vector3.up);
            while (Quaternion.Angle(transform.rotation, target) > .4f && Running)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, target, TurnSpeed * Time.deltaTime);
                yield return null;
            }
            transform.rotation = target;

            Vector3 flatDock = new Vector3(dock.x, transform.position.y, dock.z);
            while (Vector3.Distance(transform.position, flatDock) > .005f && Running)
            {
                transform.position = Vector3.MoveTowards(transform.position, flatDock, MoveSpeed * .5f * Time.deltaTime);
                yield return null;
            }
            transform.position = flatDock;
        }

        // ------------------------------------------------------------------ grille + A*

        private Vector3 Snap(Vector3 world)
        {
            return new Vector3(Mathf.Round(world.x / Cell) * Cell, world.y, Mathf.Round(world.z / Cell) * Cell);
        }

        /// <summary>
        /// Route vers le but, ou vers une cellule voisine quand la cellule du dock est occupée
        /// par le collider de l'étagère (l'approche fine couvre les derniers centimètres).
        /// </summary>
        private bool TryPlan(Vector3 start, Vector3 goal)
        {
            if (Plan(start, goal) && path.Count > 0) return true;
            var neighbours = new[]
            {
                goal + new Vector3(Cell, 0f, 0f), goal + new Vector3(-Cell, 0f, 0f),
                goal + new Vector3(0f, 0f, Cell), goal + new Vector3(0f, 0f, -Cell)
            };
            foreach (Vector3 candidate in neighbours)
            {
                Vector3 c = Snap(candidate);
                if (!IsFree(c)) continue; // jamais une cellule dans le rack
                if (Plan(start, c) && path.Count > 0) return true;
            }
            return false;
        }

        private bool Plan(Vector3 start, Vector3 goal)
        {
            // The warehouse aisles are tight: retry with a smaller clearance before declaring the
            // zone unreachable, otherwise a single wide shelf collider blocks the whole map.
            float[] clearances = { 1f, .75f, .5f };
            foreach (float scale in clearances)
            {
                if (PlanWith(start, goal, scale))
                {
                    if (scale < 1f && LogActions)
                        Debug.Log("[Autopilot] itinéraire trouvé avec une marge réduite (" + (scale * 100f).ToString("F0") + "%).");
                    return true;
                }
            }
            path.Clear();
            pathCursor = 0;
            return false;
        }

        private bool PlanWith(Vector3 start, Vector3 goal, float clearanceScale)
        {
            clearance = clearanceScale;
            path.Clear();
            pathCursor = 0;
            Vector2Int from = ToCell(start);
            Vector2Int to = ToCell(goal);
            if (from == to) return true;
            // The dock cell sits right against the shelf: it is a legal standing position even
            // though the shelf collider overlaps it. Blocking it would make every dock unreachable.

            var came = new Dictionary<Vector2Int, Vector2Int> { [from] = from };
            var open = new Queue<Vector2Int>();
            open.Enqueue(from);
            var steps = new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };
            int guard = 0;
            while (open.Count > 0 && guard++ < 40000)
            {
                Vector2Int node = open.Dequeue();
                if (node == to) break;
                foreach (Vector2Int step in steps)
                {
                    Vector2Int next = node + step;
                    if (came.ContainsKey(next)) continue;
                    Vector3 world = ToWorld(next);
                    if (Mathf.Abs(world.x) > halfExtent || Mathf.Abs(world.z) > halfExtent) continue;
                    // The goal cell is always accepted: it is the dock, by design flush with the rack.
                    if (next != to && !IsFree(world)) continue;
                    came[next] = node;
                    open.Enqueue(next);
                }
            }
            if (!came.ContainsKey(to)) return false;

            for (Vector2Int cell = to; cell != from; cell = came[cell]) path.Add(ToWorld(cell));
            path.Reverse();
            return true;
        }

        private Vector2Int ToCell(Vector3 world)
        {
            return new Vector2Int(Mathf.RoundToInt(world.x / Cell), Mathf.RoundToInt(world.z / Cell));
        }

        private Vector3 ToWorld(Vector2Int cell)
        {
            return new Vector3(cell.x * Cell, transform.position.y, cell.y * Cell);
        }

        /// <summary>Test de franchissabilité d'une cellule avec les mêmes règles que
        /// le planificateur (ObstacleMask + clearance) — utilisé par le capteur
        /// "vision" pour ne jamais réclamer un itinéraire que le planificateur ne
        /// peut pas produire.</summary>
        public bool PathCellFree(Vector3 world) { return IsFree(world); }

        private bool IsFree(Vector3 world)
        {
            Vector3 center = new Vector3(world.x, transform.position.y, world.z) + CheckOffset;
            Collider[] hits = Physics.OverlapBox(center, CheckHalfExtents * clearance, Quaternion.identity, ObstacleMask, QueryTriggerInteraction.Ignore);
            float floorCut = transform.position.y + .06f;
            foreach (Collider hit in hits)
            {
                if (hit == null || hit.isTrigger) continue;
                if (hit.bounds.max.y <= floorCut) continue; // sol / marquages au ras du plancher
                // IsChildOf plutôt qu'une liste figée : couvre aussi ce qui s'attache plus tard (charge).
                if (hit.transform.IsChildOf(transform)) continue;
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ animation

        private void LateUpdate()
        {
            if (!ready) ready = true;
            UpdatePathViz();
            UpdateGridDebug();
            if (Animator == null) return;
            // GridRobotController forces IsMoving=false while controlsLocked, so the pilot drives it.
            bool moving = Running && (path.Count > 0 || Vector3.Distance(transform.position, lastPosition) > .0005f);
            Animator.SetBool("IsMoving", moving);
            lastPosition = transform.position;
        }

        private Vector3 lastPosition;

        // ------------------------------------------------------------------ affichage runtime
        // Les gizmos n'existent que dans l'éditeur : ce rendu reste visible dans un build.

        private Transform pathVizRoot;
        private LineRenderer pathLine;
        private Material pathMaterial;
        private int pathHash = -1;

        // ------------------------------------------------------------------ debug grille
        // Overlay au sol : vert = cellule franchissable pour le planificateur, rouge = bloquée.
        // Positions en monde : l'objet n'est PAS parenté au robot (sinon il suivrait son déplacement).

        private Transform gridRoot;
        private Mesh gridMesh;
        private float nextGridRefresh;
        private readonly Collider[] gridHits = new Collider[24];
        private readonly List<Vector3> gridVerts = new List<Vector3>();
        private readonly List<int> gridFree = new List<int>();
        private readonly List<int> gridBlocked = new List<int>();

        private void OnDestroy()
        {
            if (gridRoot != null) Destroy(gridRoot.gameObject);
        }

        private void UpdateGridDebug()
        {
            if (ShowGridDebug)
            {
                EnsureGridDebug();
                if (Time.time >= nextGridRefresh)
                {
                    nextGridRefresh = Time.time + GridRefresh;
                    RebuildGridDebug();
                }
            }
            if (gridRoot != null && gridRoot.gameObject.activeSelf != ShowGridDebug)
                gridRoot.gameObject.SetActive(ShowGridDebug);
        }

        private void EnsureGridDebug()
        {
            if (gridRoot != null) return;
            var go = new GameObject("Autopilot Grid Debug");
            gridRoot = go.transform; // racine de scène : le mesh stocke des positions monde
            gridMesh = new Mesh { name = "Autopilot grid", indexFormat = IndexFormat.UInt32 };
            go.AddComponent<MeshFilter>().sharedMesh = gridMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.sharedMaterials = new[] { TransparentMaterial(FreeCellColor), TransparentMaterial(BlockedCellColor) };
        }

        private static Material TransparentMaterial(Color color)
        {
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
                mat.renderQueue = (int)RenderQueue.Transparent;
            }
            return mat;
        }

        /// <summary>Même test que IsFree mais sans allocation, pour scanner toute la grille.</summary>
        private bool CellFreeDebug(Vector3 world)
        {
            Vector3 center = new Vector3(world.x, transform.position.y, world.z) + CheckOffset;
            int count = Physics.OverlapBoxNonAlloc(center, CheckHalfExtents, gridHits, Quaternion.identity,
                                                   ObstacleMask, QueryTriggerInteraction.Ignore);
            float floorCut = transform.position.y + .06f;
            for (int i = 0; i < count; i++)
            {
                Collider hit = gridHits[i];
                if (hit == null || hit.isTrigger) continue;
                if (hit.bounds.max.y <= floorCut) continue;
                if (hit.transform.IsChildOf(transform)) continue;
                return false;
            }
            return true;
        }

        private void RebuildGridDebug()
        {
            float range = halfExtent > .1f ? halfExtent
                        : MapHalfExtent > 0f ? MapHalfExtent
                        : Controller != null ? Mathf.Max(1f, Mathf.Max(Controller.gridCellsX, Controller.gridCellsZ) * Controller.cellSize * .5f) : 8f;
            int n = Mathf.FloorToInt(range / Cell);
            float y = transform.position.y + .015f;
            float h = Cell * .42f;
            gridVerts.Clear();
            gridFree.Clear();
            gridBlocked.Clear();
            for (int ix = -n; ix <= n; ix++)
            {
                for (int iz = -n; iz <= n; iz++)
                {
                    Vector3 c = new Vector3(ix * Cell, y, iz * Cell);
                    int v = gridVerts.Count;
                    gridVerts.Add(c + new Vector3(-h, 0f, -h));
                    gridVerts.Add(c + new Vector3( h, 0f, -h));
                    gridVerts.Add(c + new Vector3( h, 0f,  h));
                    gridVerts.Add(c + new Vector3(-h, 0f,  h));
                    List<int> idx = CellFreeDebug(c) ? gridFree : gridBlocked;
                    idx.Add(v); idx.Add(v + 2); idx.Add(v + 1); // double face
                    idx.Add(v); idx.Add(v + 3); idx.Add(v + 2);
                    idx.Add(v); idx.Add(v + 1); idx.Add(v + 2);
                    idx.Add(v); idx.Add(v + 2); idx.Add(v + 3);
                }
            }
            gridMesh.Clear();
            gridMesh.SetVertices(gridVerts);
            gridMesh.subMeshCount = 2;
            gridMesh.SetIndices(gridFree, MeshTopology.Triangles, 0);
            gridMesh.SetIndices(gridBlocked, MeshTopology.Triangles, 1);
            gridMesh.RecalculateBounds();
        }

        private Material PathMaterial
        {
            get
            {
                if (pathMaterial == null)
                {
                    Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader == null) shader = Shader.Find("Unlit/Color");
                    if (shader == null) shader = Shader.Find("Sprites/Default");
                    if (shader == null) shader = Shader.Find("Standard");
                    if (shader != null) pathMaterial = new Material(shader) { color = Color.cyan };
                }
                return pathMaterial;
            }
        }

        private void UpdatePathViz()
        {
            bool show = ShowPathInGame && path.Count > pathCursor;
            if (show)
            {
                EnsurePathViz();
                int hash = PathHash();
                if (hash != pathHash)
                {
                    pathHash = hash;
                    RebuildPathViz();
                }
                // Le premier point suit le robot en direct, même sans changement de trajet.
                if (pathLine.positionCount > 0)
                    pathLine.SetPosition(0, transform.position + Vector3.up * .02f);
            }
            else pathHash = -1;
            if (pathVizRoot != null && pathVizRoot.gameObject.activeSelf != show)
                pathVizRoot.gameObject.SetActive(show);
        }

        private void EnsurePathViz()
        {
            if (pathVizRoot != null) return;
            var root = new GameObject("Autopilot Path Viz");
            pathVizRoot = root.transform;
            pathVizRoot.SetParent(transform, false);

            pathLine = root.AddComponent<LineRenderer>();
            pathLine.useWorldSpace = true;
            pathLine.shadowCastingMode = ShadowCastingMode.Off;
            pathLine.numCornerVertices = pathLine.numCapVertices = 2;
            pathLine.startWidth = pathLine.endWidth = PathLineWidth;
            pathLine.startColor = pathLine.endColor = Color.cyan;
            pathLine.sharedMaterial = PathMaterial;
        }

        private int PathHash()
        {
            unchecked
            {
                int h = path.Count * 31 + pathCursor;
                for (int i = pathCursor; i < path.Count; i++) h = h * 397 + path[i].GetHashCode();
                return h;
            }
        }

        /// <summary>Ligne au sol : position du robot puis cellules restantes du trajet.</summary>
        private void RebuildPathViz()
        {
            int n = path.Count - pathCursor;
            pathLine.positionCount = n + 1;
            pathLine.SetPosition(0, transform.position + Vector3.up * .02f);
            for (int i = 0; i < n; i++) pathLine.SetPosition(i + 1, path[pathCursor + i] + Vector3.up * .02f);
        }

        private void OnDrawGizmos()
        {
            if (path == null) return;
            Gizmos.color = Color.cyan;
            for (int i = pathCursor + 1; i < path.Count; i++)
                Gizmos.DrawLine(path[i - 1], path[i]);
        }

        // ------------------------------------------------------------------ mission dirigée (Muse Wonder)
        //
        // L'orchestrateur découpe le plan Muse en étapes : (palette, type, quantité).
        // Le robot exécute les étapes dans l'ordre, ne prend que le carton réellement
        // au-dessus d'une colonne, marque chaque carton avec l'orderId, puis le dépose
        // sur le banc imposé. En cas de rupture sur une palette, il continue sur les
        // palettes de secours déjà approuvées par Muse — sans attendre une nouvelle
        // décision de l'API. Rapport détaillé en fin de mission.

        /// <summary>Une étape de prélèvement : palette + type + quantité.</summary>
        public sealed class PickLeg
        {
            public ShelfStorage Pallet;
            public string PalletId;
            public CargoKind Kind;
            public int Planned;
            public int Picked;
        }

        /// <summary>Rapport de fin de mission dirigée.</summary>
        public sealed class PickMissionReport
        {
            public int OrderId;
            public readonly List<PickLeg> Legs = new List<PickLeg>();
            public int TotalPlanned;
            public int TotalPicked;
            public bool Aborted;
            public string AbortReason;
            /// <summary>Quantité non prélevée par type (A,B,C) — rupture totale.</summary>
            public readonly int[] Missing = new int[3];
        }

        /// <summary>Mission dirigée en cours.</summary>
        public bool MissionActive { get; private set; }
        /// <summary>Dernier rapport (null avant la première mission).</summary>
        public PickMissionReport LastReport { get; private set; }
        /// <summary>Le robot vient de finir une étape (prévue ou écourtée).</summary>
        public event System.Action<PickLeg> LegFinished;
        /// <summary>Changement d'itinéraire suite à une rupture ("P01 -> P02 (A)").</summary>
        public event System.Action<string> RouteChanged;
        /// <summary>Chaque carton réellement prélevé (après marquage orderId).</summary>
        public event System.Action<CargoBox, PickLeg> BoxPicked;
        /// <summary>Chaque carton réellement posé sur le banc.</summary>
        public event System.Action<CargoBox> BoxStaged;

        private bool missionAbort;
        private bool missionSuspend;
        private PickLeg activeLeg;
        /// <summary>Étape en cours (pour le HUD : palette + reste).</summary>
        public PickLeg ActiveLeg => activeLeg;
        [Tooltip("Après la mission dirigée, le robot rentre se garer à sa position initiale.")]
        public bool ReturnHomeAfterMission = true;
        /// <summary>Missions en file derrière la courante : >0 -> enchaîner sans retour base.</summary>
        public int QueuedPicks;
        /// <summary>L'orchestrateur peut demander à R1 de rester sur place après une
        /// collecte (décision Muse en vol, commandes en pause à reprendre) : le
        /// retour base est différé tant que cela renvoie true et qu'aucune collecte
        /// n'est arrivée en file (même principe que TruckLoaderAgent.StayOut).</summary>
        public System.Func<bool> StayOut;
        [Tooltip("Attente max sur place (s) avant de rentrer quand aucune collecte n'arrive.")]
        public float StayOutMaxWait = 100f;   // > 45 s Wonder + 45 s Spark : R1 ne rentre plus pendant un repli

        /// <summary>Annule la mission dirigée en cours (le robot dépose ce qu'il porte).</summary>
        public void AbortMission() { missionAbort = true; }

        /// <summary>
        /// Suspension sûre (commande urgente) : finit l'opération physique en cours —
        /// un carton en main est TOUJOURS déposé sur le banc de la commande — puis
        /// termine la mission sans démarrer de nouveau prélèvement.
        /// </summary>
        public void SuspendMission() { missionSuspend = true; }

        /// <summary>
        /// Exécute les étapes dans l'ordre puis dépose sur le banc imposé.
        /// fallbacks : palettes de secours approuvées par Muse, dans l'ordre de préférence.
        /// Le rapport final arrive dans done (toujours appelé, même en échec).
        /// </summary>
        public IEnumerator PickMission(List<PickLeg> legs, List<ShelfStorage> fallbacks,
                                       ShelfStorage bench, int orderId,
                                       System.Action<PickMissionReport> done)
        {
            var report = new PickMissionReport { OrderId = orderId };
            LastReport = report;
            MissionActive = true;
            missionAbort = false;
            missionSuspend = false;
            foreach (PickLeg l in legs) { report.Legs.Add(l); report.TotalPlanned += l.Planned; }

            var queue = new Queue<PickLeg>(legs);
            var usedFallbacks = new HashSet<ShelfStorage>();

            while (Running && !missionAbort && !missionSuspend && queue.Count > 0)
            {
                PickLeg leg = queue.Dequeue();
                activeLeg = leg;
                int remaining = leg.Planned - leg.Picked;

                while (remaining > 0 && Running && !missionAbort)
                {
                    // Suspension : on ne commence un nouveau prélèvement que mains libres ;
                    // un carton en main poursuit jusqu'au banc (jamais relâché au sol).
                    if (missionSuspend && carried == null) break;
                    // Carton réellement accessible : le dessus d'une colonne du bon type,
                    // non réservé, et appartenant au stock libre ou à cette commande.
                    ShelfInteractionZone zone = TopZoneWithKind(leg.Pallet, leg.Kind, orderId);
                    if (zone == null)
                    {
                        // Rupture sur cette palette : chaîne de secours approuvée par Muse.
                        ShelfStorage fallback = NextFallback(fallbacks, usedFallbacks, leg.Kind, orderId);
                        if (fallback != null)
                        {
                            var next = new PickLeg
                            {
                                Pallet = fallback,
                                PalletId = fallback.name,
                                Kind = leg.Kind,
                                Planned = remaining,
                                Picked = 0
                            };
                            report.Legs.Add(next);
                            report.TotalPlanned += 0; // même quantité, source différente
                            usedFallbacks.Add(fallback);
                            queue = Requeue(next, queue);
                            string msg = leg.PalletId + " -> " + next.PalletId + " (" + KindName(leg.Kind) +
                                         ", reste " + remaining + ")";
                            Debug.Log("[Autopilot] rupture : " + msg);
                            RouteChanged?.Invoke(msg);
                            leg.Planned = leg.Picked; // l'étape se termine à ce qui a été pris
                            break;
                        }
                        int idx = KindIndex(leg.Kind);
                        if (idx >= 0) report.Missing[idx] += remaining;
                        Debug.LogWarning("[Autopilot] " + leg.PalletId + " : plus de " + KindName(leg.Kind) +
                                         " accessible, reste " + remaining + " non prélevé(s)");
                        break;
                    }

                    // ---- aller chercher le carton
                    lastSource = zone;
                    yield return DriveToZone(zone);
                    int attempts = 0;
                    while (!legOk && attempts++ < 3 && Running && !missionAbort)
                    {
                        Status = "dock " + zone.name + " occupé : nouvel essai";
                        yield return new WaitForSeconds(1f);
                        yield return DriveToZone(zone);
                    }
                    if (!legOk) // dock inatteignable : la palette est de fait indisponible
                    {
                        Debug.LogWarning("[Autopilot] dock " + zone.name + " inatteignable : palette écartée");
                        blockedZones.Add(zone);
                        continue; // TopZoneWithKind trouvera une autre colonne, sinon rupture
                    }
                    if (!Running || missionAbort || missionSuspend) break; // encore mains libres ici

                    // Relecture après trajet : le dessus a pu changer pendant le trajet.
                    CargoBox box = TopBox(zone);
                    if (box == null || KindOf(box) != leg.Kind || !CanTakeForOrder(box, orderId))
                        continue; // ce carton n'est plus là : on relit la scène au tour suivant

                    Status = "prise " + box.name + " (commande #" + orderId + ")";
                    yield return Interact(true, box);
                    if (carried != box)
                    {
                        blocked.Add(box);
                        yield return new WaitForSeconds(.3f);
                        continue;
                    }
                    box.orderId = orderId; // le carton appartient désormais à cette commande
                    BoxPicked?.Invoke(box, leg);

                    // ---- dépôt sur le banc imposé (attente si plein : R2 peut le vider)
                    ShelfInteractionZone target = null;
                    while (target == null && Running && !missionAbort)
                    {
                        target = BenchZoneWithRoom(bench, transform.position);
                        if (target == null)
                        {
                            Status = "banc plein : attente d'un niveau libre";
                            yield return new WaitForSeconds(.8f);
                        }
                    }
                    if (!Running || missionAbort) break;
                    lastTarget = target;
                    yield return DriveToZone(target);
                    while (!legOk && Running && !missionAbort) // le banc DOIT être atteint
                    {
                        Status = "dock banc bloqué : nouvel essai";
                        yield return new WaitForSeconds(1f);
                        yield return DriveToZone(target);
                    }
                    if (!Running || missionAbort) break;

                    yield return Interact(false, box);
                    if (carried == null)
                    {
                        leg.Picked++;
                        remaining--;
                        Delivered++;
                        BoxStaged?.Invoke(box);
                        Status = leg.Kind + " posé (" + leg.Picked + "/" + leg.Planned + " sur " + leg.PalletId + ")";
                    }
                    else
                    {
                        // Le carton n'a pas pu être posé : jamais un robot figé avec sa charge.
                        Debug.LogError("[Autopilot] dépose impossible de " + box.name + " : mission abandonnée");
                        report.Aborted = true;
                        report.AbortReason = "dépose impossible sur " + bench.name;
                        DropCarried();
                        break;
                    }
                }
                activeLeg = null;
                LegFinished?.Invoke(leg);
            }

            if (missionAbort || missionSuspend)
            {
                report.Aborted = true;
                report.AbortReason = missionSuspend ? "suspendue pour commande urgente"
                                                    : "annulée par l'orchestrateur";
            }
            if (carried != null) DropCarried();
            report.TotalPicked = 0;
            foreach (PickLeg l in report.Legs) report.TotalPicked += l.Picked;
            Status = report.Aborted ? (missionSuspend ? "mission suspendue" : "mission abandonnée")
                     : report.TotalPicked >= report.TotalPlanned ? "collecte terminée"
                     : "collecte incomplète (rupture)";
            done?.Invoke(report);
            // Rentrer au poste initial : le robot ne reste pas garé devant le banc.
            // MissionActive reste vrai pendant le trajet : pas de nouvelle mission avant le parking.
            // Sauf si une collecte est déjà en file : on enchaîne directement dessus.
            // Mission SUSPENDUE : arrêt sûr sur place (mains libres) — l'urgente
            // reprend R1 immédiatement, le retour base n'aurait fait que la retarder.
            if (ReturnHomeAfterMission && Running && QueuedPicks <= 0 && !missionSuspend)
            {
                // Travail imminent (décision Muse en vol, reprise après urgente) :
                // attendre sur place plutôt que faire l'aller-retour à la base. La
                // collecte suivante (QueuedPicks > 0) libère R1 immédiatement.
                if (StayOut != null && StayOut())
                {
                    Status = "attente sur place : décision Muse en cours";
                    float until = Time.time + StayOutMaxWait;
                    while (Running && QueuedPicks <= 0 && StayOut() && Time.time < until)
                        yield return null;
                }
                if (Running && QueuedPicks <= 0) yield return GoHome();
            }
            MissionActive = false;
        }

        private static Queue<PickLeg> Requeue(PickLeg first, Queue<PickLeg> rest)
        {
            var q = new Queue<PickLeg>();
            q.Enqueue(first);
            foreach (PickLeg l in rest) q.Enqueue(l);
            return q;
        }

        /// <summary>
        /// Colonne de la palette dont le dessus porte le bon type : le seul carton
        /// physiquement accessible. Stock libre (orderId -1) ou déjà à cette commande.
        /// </summary>
        private ShelfInteractionZone TopZoneWithKind(ShelfStorage pallet, CargoKind kind, int orderId)
        {
            if (pallet == null) return null;
            foreach (ShelfInteractionZone zone in zones)
            {
                if (zone == null || zone.shelf != pallet || blockedZones.Contains(zone)) continue;
                CargoBox top = TopBox(zone);
                if (top == null || KindOf(top) != kind) continue;
                if (!CanTakeForOrder(top, orderId)) continue;
                return zone;
            }
            return null;
        }

        /// <summary>Carton prélevable pour cette commande : stock libre ou déjà réservé à elle.</summary>
        private bool CanTakeForOrder(CargoBox box, int orderId)
        {
            if (box == null || box.isCarried) return false;
            if (box.orderId >= 0 && box.orderId != orderId) return false; // autre commande
            var registry = CargoRegistry.Instance;
            if (registry != null && registry.IsReserved(box) &&
                !ReferenceEquals(registry.ReservedBy(box), this)) return false;
            return true;
        }

        /// <summary>Première palette de secours approuvée portant encore le bon type au sommet.</summary>
        private ShelfStorage NextFallback(List<ShelfStorage> fallbacks, HashSet<ShelfStorage> used,
                                          CargoKind kind, int orderId)
        {
            if (fallbacks == null) return null;
            foreach (ShelfStorage pallet in fallbacks)
            {
                if (pallet == null || used.Contains(pallet)) continue;
                if (TopZoneWithKind(pallet, kind, orderId) != null) return pallet;
            }
            return null;
        }

        /// <summary>Colonne du banc imposé avec un niveau libre (la plus proche).</summary>
        private ShelfInteractionZone BenchZoneWithRoom(ShelfStorage bench, Vector3 from)
        {
            ShelfInteractionZone best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (ShelfInteractionZone zone in zones)
            {
                if (zone == null || zone.shelf != bench || zone.dockPoint == null) continue;
                if (blockedZones.Contains(zone) || IsTruck(zone)) continue;
                if (!HasRoom(zone)) continue;
                float distance = Vector3.Distance(from, zone.dockPoint.position);
                if (distance < bestDistance) { bestDistance = distance; best = zone; }
            }
            return best;
        }

        /// <summary>Pose le carton porté au sol devant le robot (dernier recours).</summary>
        private void DropCarried()
        {
            if (carried == null) return;
            carried.isCarried = false;
            carried.transform.SetParent(null, true);
            Vector3 pos = transform.position + transform.forward * .45f;
            carried.transform.position = new Vector3(pos.x, transform.position.y, pos.z);
            carried = null;
            if (Animator != null) Animator.SetBool("HasBox", false);
        }
    }
}
