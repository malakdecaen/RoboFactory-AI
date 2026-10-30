using System.Collections;
using System.Reflection;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Robot 2 — chargeur de camion.
    ///
    /// State machine explicite :
    ///   AttenteColis -> VersBanc -> Prise -> VersCamion -> Depose -> (reboucle)
    ///                                        (AttenteCamion si la baie est pleine)
    ///
    /// Le robot demande un job au CargoRegistry (carton sur banc + slot camion libre),
    /// réserve le carton, navigue avec GridMover (attente/retry intégrés) et utilise
    /// RobotCargoInteractor pour les animations de prise/dépose.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TruckLoaderAgent : MonoBehaviour
    {
        public enum R2State
        {
            Arret,
            AttenteColis,
            VersBanc,
            Prise,
            VersCamion,
            Depose,
            AttenteCamion,
            RetourBase
        }

        [Header("Robot")]
        public GridRobotController Controller;
        public Animator Animator;
        public RobotCargoInteractor Interactor;
        public GridMover Mover;

        [Header("Mission")]
        public bool AutoStart = true;
        [Tooltip("Délai de rescan quand aucun carton n'attend sur les bancs.")]
        public float PollInterval = 1.5f;
        [Tooltip("Délai de nouvel essai quand la baie camion est pleine.")]
        public float FullRetryInterval = 2f;
        [Tooltip("Quand il n'y a plus rien à faire, le robot rentre se garer à sa position initiale.")]
        public bool ReturnHomeWhenIdle = true;
        [Tooltip("Point de parking (optionnel). Vide = position du robot au démarrage.")]
        public Transform HomeAnchor;
        [Tooltip("Distance au point de parking considérée comme 'à la base'.")]
        public float HomeTolerance = .35f;
        public bool LogActions = true;

        public R2State State { get; private set; } = R2State.Arret;
        public string Status { get; private set; } = "arrêt";
        public int Delivered { get; private set; }
        public bool Running { get; private set; }
        /// <summary>Carton actuellement transporté par ce robot (lu après prise).</summary>
        public CargoBox Carried { get; private set; }

        private CargoRegistry registry;
        private Transform carryRoot;
        private CargoRegistry.DeliveryJob job;
        private bool hasJob;
        private Vector3 homePos;
        private Vector3 homeForward;

        private void Awake()
        {
            if (Controller == null) Controller = GetComponent<GridRobotController>();
            if (Animator == null) Animator = GetComponent<Animator>();
            if (Interactor == null) Interactor = GetComponent<RobotCargoInteractor>();
            if (Mover == null) Mover = GetComponent<GridMover>();
        }

        private void Start()
        {
            if (AutoStart) Begin();
        }

        public void Begin()
        {
            if (Running) return;
            if (Mover == null)
            {
                Debug.LogError("[Loader:" + name + "] GridMover introuvable.");
                return;
            }
            if (Controller != null) Controller.controlsLocked = true;
            // Position initiale = point de parking (sauf si un anchor explicite est donné).
            homePos = HomeAnchor != null ? HomeAnchor.position : transform.position;
            homeForward = HomeAnchor != null ? HomeAnchor.forward : transform.forward;
            Running = true;
            StartCoroutine(Run());
            Log("démarré");
        }

        public void Stop()
        {
            Running = false;
            if (Mover != null) Mover.StopNavigation();
            ReleaseJob();
            StopAllCoroutines();
            SetState(R2State.Arret, "arrêt");
        }

        // ------------------------------------------------------------------ boucle FSM

        private IEnumerator Run()
        {
            yield return null; // laisse le controller se caler sur la grille
            while (Running)
            {
                registry = CargoRegistry.Instance != null ? CargoRegistry.Instance
                                                          : FindAnyObjectByType<CargoRegistry>();
                if (registry == null)
                {
                    SetState(R2State.AttenteColis, "attente du registre logistique");
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                if (!registry.TryAcquireDelivery(this, transform.position, out job))
                {
                    // Rien à faire : le robot rentre se garer à sa position initiale.
                    // Le trajet s'interrompt dès qu'un carton apparaît sur un banc.
                    if (ReturnHomeWhenIdle && !NearHome())
                    {
                        SetState(R2State.RetourBase, "plus de travail : retour à la base");
                        yield return Mover.NavigateTo(homePos, homeForward,
                            () => registry != null && registry.OnBenchCount > 0);
                    }
                    SetState(R2State.AttenteColis, NearHome()
                        ? "à la base : aucun carton sur les bancs"
                        : "aucun carton sur les bancs (ou camion plein)");
                    yield return new WaitForSeconds(PollInterval);
                    continue;
                }
                hasJob = true;
                Log("job : " + job.Box.name + " sur " + job.Zone.name + " -> camion");

                // ------ aller au banc
                SetState(R2State.VersBanc, "vers " + job.Zone.name);
                yield return Mover.NavigateTo(job.Zone.dockPoint.position, job.Zone.dockPoint.forward);
                if (Mover.LastResult != GridMover.NavResult.Arrive)
                {
                    FailJob("pas arrivé au banc");
                    continue;
                }

                // Le carton a pu disparaître pendant le trajet (déplacé à la main, autre agent).
                if (CargoRegistry.Classify(job.Box) != CargoStatus.OnBench)
                {
                    FailJob("le carton n'est plus sur le banc");
                    continue;
                }

                // Sécurité pile : on prend toujours le carton du dessus. Si le carton
                // réservé a été enterré entre-temps, la réservation passe au vrai dessus.
                CargoBox top = CargoRegistry.TopBox(job.Zone);
                if (top != null && top != job.Box && registry.TryReserve(top, this))
                {
                    Log("le réservé " + job.Box.name + " est sous " + top.name + " : prise du dessus");
                    registry.Release(job.Box, this);
                    job.Box = top;
                }

                // ------ prise
                SetState(R2State.Prise, "prise " + job.Box.name);
                yield return Interact(true, job.Box, job.Zone);
                if (Carried != job.Box)
                {
                    FailJob("prise échouée");
                    yield return new WaitForSeconds(.3f);
                    continue;
                }

                // ------ aller au camion (le robot porte : il attend tant que besoin)
                // Dock = zone de la colonne la moins remplie : répartition sur la palette.
                ShelfInteractionZone dockZone = job.Bay.ChooseDockForLoad(transform.position);
                Transform dockPoint = dockZone != null && dockZone.dockPoint != null
                    ? dockZone.dockPoint : job.Bay.DockPoint;
                if (dockPoint == null)
                {
                    FailJob("aucun dock camion");
                    continue;
                }
                SetState(R2State.VersCamion, "vers camion (" + dockPoint.name + ")");
                yield return Mover.NavigateTo(dockPoint.position, dockPoint.forward);
                if (Mover.LastResult != GridMover.NavResult.Arrive)
                {
                    // Enabled=false seulement si arrêté : ne devrait pas arriver en cours de mission.
                    FailJob("trajet camion interrompu");
                    continue;
                }

                // ------ dépose (attente si la baie s'est remplie entre-temps)
                while (Running && job.Bay.FreeSlots <= 0)
                {
                    SetState(R2State.AttenteCamion, "camion plein : attente d'un slot");
                    yield return new WaitForSeconds(FullRetryInterval);
                }
                if (!Running) yield break;

                SetState(R2State.Depose, "dépose dans le camion");
                yield return Interact(false, Carried, job.Zone, job.Bay);
                if (Carried == null)
                {
                    Delivered++;
                    registry.Release(job.Box, this);
                    hasJob = false;
                    if (Animator != null) Animator.SetBool("HasBox", false);
                    Status = Delivered + " carton(s) livré(s) au camion";
                }
                else
                {
                    // La dépose a échoué alors que le carton est encore en main :
                    // on tente un slot libre, sinon on le pose au sol — jamais
                    // un robot figé avec une charge indéposable.
                    if (job.Bay == null || !job.Bay.PlaceBox(Carried)) DropAtFeet(Carried);
                    Carried = null;
                    if (Animator != null) Animator.SetBool("HasBox", false);
                    FailJob("dépose échouée (carton relâché)");
                }
            }
        }

        private bool NearHome()
        {
            return Vector3.Distance(transform.position, homePos) <= HomeTolerance;
        }

        /// <summary>Détache le carton et le pose au sol devant le robot (statut Loose).</summary>
        private void DropAtFeet(CargoBox box)
        {
            if (box == null) return;
            box.isCarried = false;
            box.transform.SetParent(null, true);
            Vector3 pos = transform.position + transform.forward * .45f;
            box.transform.position = new Vector3(pos.x, transform.position.y, pos.z);
            Log("carton posé au sol (fallback)");
        }

        private void FailJob(string reason)
        {
            Log("job abandonné : " + reason);
            ReleaseJob();
        }

        private void ReleaseJob()
        {
            if (hasJob && registry != null && job.Box != null) registry.Release(job.Box, this);
            hasJob = false;
        }

        private void SetState(R2State state, string status)
        {
            if (State != state)
            {
                State = state;
                Log("état: " + state);
            }
            Status = status;
        }

        // ------------------------------------------------------------------ interaction

        /// <summary>
        /// Prise/dépose via l'interaction Warcool (zones trigger + animations).
        /// Fallback manuel si l'asset ne répond pas, comme l'autopilot.
        /// </summary>
        private IEnumerator Interact(bool load, CargoBox box, ShelfInteractionZone zone, TruckBay bay = null)
        {
            if (Interactor != null)
            {
                Interactor.SendMessage("TryInteract", SendMessageOptions.DontRequireReceiver);
                float deadline = Time.time + 3f;
                while (Time.time < deadline && Interactor.IsBusy) yield return null;
                while (Time.time < deadline && Interactor.HasBox != load) yield return null;
                if (Interactor.HasBox == load)
                {
                    Carried = load ? box : null;
                    if (!load && bay != null && box != null) bay.ValidateStored(box);
                    yield break;
                }
                Debug.LogWarning("[Loader:" + name + "] l'interaction Warcool n'a pas répondu, transfert manuel.");
            }

            if (load)
            {
                int level = Mathf.Clamp(LevelOf(zone, box), 0, 2);
                PlayTrigger("Load_Level_" + level);
                yield return new WaitForSeconds(.45f);
                Attach(box, zone);
                yield return new WaitForSeconds(.25f);
            }
            else
            {
                PlayTrigger("Unload_Level_0");
                yield return new WaitForSeconds(.55f);
                bool placed = bay != null && bay.PlaceBox(box);
                if (placed) Carried = null;
                else Debug.LogWarning("[Loader:" + name + "] aucun slot libre dans le camion.");
                yield return new WaitForSeconds(.25f);
            }
        }

        private void Attach(CargoBox box, ShelfInteractionZone zone)
        {
            if (box == null) return;
            Transform anchor = AssetCarryRoot();
            if (anchor == null)
            {
                if (carryRoot == null)
                {
                    var root = new GameObject("Loader Carry Root");
                    carryRoot = root.transform;
                    carryRoot.SetParent(transform, false);
                    carryRoot.localPosition = Interactor != null && Interactor.cargoAttachPoint != null
                        ? transform.InverseTransformPoint(Interactor.cargoAttachPoint.position)
                        : new Vector3(0f, .75f, 0f);
                }
                anchor = carryRoot;
            }
            // Sortir le carton de la table du ShelfStorage (cohérence pour les autres agents).
            RemoveFromStorage(zone, box);
            Quaternion rotation = box.transform.rotation;
            box.transform.SetParent(anchor, true);
            box.transform.position = anchor.position;
            box.transform.rotation = rotation;
            box.isCarried = true;
            Carried = box;
            if (Animator != null) Animator.SetBool("HasBox", true);
        }

        private static void RemoveFromStorage(ShelfInteractionZone zone, CargoBox box)
        {
            if (zone == null || zone.shelf == null || zone.shelf.columns == null) return;
            for (int c = 0; c < zone.shelf.columns.Length; c++)
            {
                Transform[] levels = zone.shelf.columns[c] != null ? zone.shelf.columns[c].levels : null;
                if (levels == null) continue;
                for (int l = 0; l < levels.Length; l++)
                    if (zone.shelf.GetBox(c, l) == box) { zone.shelf.RemoveBox(c, l); return; }
            }
        }

        private Transform AssetCarryRoot()
        {
            if (Interactor == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            return typeof(RobotCargoInteractor).GetField("activeCarryRoot", flags)?.GetValue(Interactor) as Transform;
        }

        private static int LevelOf(ShelfInteractionZone zone, CargoBox box)
        {
            Transform[] levels = CargoRegistry.ColumnLevels(zone);
            if (levels == null) return 0;
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] != null && levels[i].GetComponentInChildren<CargoBox>(true) == box) return i;
            return 0;
        }

        private void PlayTrigger(string triggerName)
        {
            if (Animator == null) return;
            foreach (AnimatorControllerParameter parameter in Animator.parameters)
                if (parameter.name == triggerName && parameter.type == AnimatorControllerParameterType.Trigger)
                {
                    Animator.SetTrigger(triggerName);
                    return;
                }
        }

        private void Log(string message)
        {
            if (LogActions) Debug.Log("[Loader:" + name + "] " + message);
        }

        // ------------------------------------------------------------------ mission dirigée (Muse Wonder)
        //
        // L'orchestrateur impose le camion et la commande : le robot ne prend QUE les
        // cartons marqués orderId, sur le banc imposé, et ne les charge que dans la
        // baie imposée — en attendant que sa porte soit réellement ouverte.

        public sealed class LoadMissionReport
        {
            public int OrderId;
            public int Loaded;
            public bool Aborted;
            public string AbortReason;
        }

        /// <summary>Mission dirigée en cours.</summary>
        /// <summary>Missions de chargement déjà en file : le retour base est sauté
        /// pour enchaîner directement (repos seulement quand plus rien à faire).</summary>
        public int QueuedLoads;
        /// <summary>L'orchestrateur peut demander à R2 de rester dehors après une
        /// mission (des chargements repris vont arriver dans quelques secondes) :
        /// retourne true tant que du travail en pause attend le camion.</summary>
        public System.Func<bool> StayOut;
        public bool MissionActive { get; private set; }
        /// <summary>Commande chargée par la mission en cours (-1 = aucune).</summary>
        public int DirectedOrderId { get; private set; } = -1;
        public LoadMissionReport LastReport { get; private set; }
        /// <summary>Chaque carton réellement chargé dans le camion.</summary>
        public event System.Action<CargoBox> BoxLoaded;
        /// <summary>Secondes sans carton trouvé avant abandon (0 = attend indéfiniment).</summary>
        public float IdleAbortDelay = 0f;

        private bool missionAbort;
        private bool missionSuspend;

        /// <summary>Démarre en mode dirigé : contrôles verrouillés mais PAS de boucle libre.</summary>
        public void BeginDirected()
        {
            if (Running) return;
            if (Mover == null)
            {
                Debug.LogError("[Loader:" + name + "] GridMover introuvable.");
                return;
            }
            if (Controller != null) Controller.controlsLocked = true;
            homePos = HomeAnchor != null ? HomeAnchor.position : transform.position;
            homeForward = HomeAnchor != null ? HomeAnchor.forward : transform.forward;
            Running = true;
            SetState(R2State.AttenteColis, "en attente d'une mission dirigée");
            Log("démarré (mode dirigé)");
        }

        /// <summary>Annule la mission en cours (le carton porté est posé au sol).</summary>
        public void AbortMission() { missionAbort = true; }

        /// <summary>
        /// Suspension sûre (commande urgente) : un carton en main est TOUJOURS chargé
        /// dans le camion (opération indivisible), puis la mission se termine et le
        /// robot sort du quai — aucun nouveau cycle n'est démarré.
        /// </summary>
        public void SuspendMission() { missionSuspend = true; }

        /// <summary>
        /// Charge dans bay tous les cartons de orderId trouvés sur bench, jusqu'à ce que
        /// expectedCount soit atteint ou qu'il n'y ait plus rien (R1 a fini sa collecte).
        /// N'entre jamais dans un camion dont la porte n'est pas complètement ouverte.
        /// </summary>
        public IEnumerator LoadMission(TruckBay bay, ShelfStorage bench, int orderId,
                                       int expectedCount, System.Action<LoadMissionReport> done)
        {
            var report = new LoadMissionReport { OrderId = orderId };
            LastReport = report;
            MissionActive = true;
            missionAbort = false;
            missionSuspend = false;
            DirectedOrderId = orderId;
            float idleSince = Time.time;

            // Carried est toujours null en haut de boucle : la suspension peut y
            // arrêter le cycle sans qu'un carton reste en l'air.
            while (Running && !missionAbort && !missionSuspend && report.Loaded < expectedCount)
            {
                // ---- porte : on n'approche jamais un camion fermé
                while (Running && !missionAbort && !missionSuspend && !bay.IsDoorOpen)
                {
                    SetState(R2State.AttenteCamion, "attente ouverture porte " + bay.name);
                    yield return new WaitForSeconds(.4f);
                }
                if (!Running || missionAbort || missionSuspend) break;

                // ---- carton de la commande sur le banc imposé (toujours le dessus)
                CargoBox box = FindOrderBox(bench, orderId, out ShelfInteractionZone zone);
                if (box == null)
                {
                    SetState(R2State.AttenteColis, "attente cartons commande #" + orderId +
                           " (" + report.Loaded + "/" + expectedCount + ")");
                    if (IdleAbortDelay > 0f && Time.time - idleSince > IdleAbortDelay)
                    {
                        report.AbortReason = "plus de cartons de la commande sur le banc";
                        break;
                    }
                    yield return new WaitForSeconds(PollInterval);
                    continue;
                }
                idleSince = Time.time;

                if (registry == null) registry = CargoRegistry.Instance;
                if (registry != null && !registry.TryReserve(box, this))
                {
                    yield return new WaitForSeconds(.3f);
                    continue;
                }

                // ---- aller au banc
                SetState(R2State.VersBanc, "vers " + zone.name);
                yield return Mover.NavigateTo(zone.dockPoint.position, zone.dockPoint.forward);
                if (Mover.LastResult != GridMover.NavResult.Arrive)
                {
                    if (registry != null) registry.Release(box, this);
                    FailJob("pas arrivé au banc");
                    yield return new WaitForSeconds(.5f);
                    continue;
                }

                // Le carton du dessus a pu changer : on prend le vrai dessus s'il
                // appartient à la commande, sinon on relance la recherche.
                CargoBox top = CargoRegistry.TopBox(zone);
                if (top != box)
                {
                    if (registry != null) registry.Release(box, this);
                    if (top != null && top.orderId == orderId &&
                        (registry == null || registry.TryReserve(top, this)))
                        box = top;
                    else continue;
                }

                // Suspension : pas de nouveau cycle — la prise suivante ne démarre pas.
                if (missionSuspend)
                {
                    if (registry != null) registry.Release(box, this);
                    break;
                }

                SetState(R2State.Prise, "prise " + box.name + " (commande #" + orderId + ")");
                yield return Interact(true, box, zone);
                if (Carried != box)
                {
                    if (registry != null) registry.Release(box, this);
                    yield return new WaitForSeconds(.3f);
                    continue;
                }

                // ---- aller au camion choisi par Muse (porte ouverte exigée)
                while (Running && !missionAbort && !missionSuspend && !bay.IsDoorOpen)
                {
                    SetState(R2State.AttenteCamion, "porte refermée : attente " + bay.name);
                    yield return new WaitForSeconds(.4f);
                }
                if (!Running || missionAbort) break;
                // Suspension avec carton en main et porte fermée : remise au banc
                // de la commande, jamais au sol et jamais mélangé.
                if (missionSuspend && !bay.IsDoorOpen)
                {
                    yield return PutBackOnBench(Carried, bench, zone);
                    break;
                }
                bay.BeginLoading();

                ShelfInteractionZone dockZone = bay.ChooseDockForLoad(transform.position);
                Transform dockPoint = dockZone != null && dockZone.dockPoint != null
                    ? dockZone.dockPoint : bay.DockPoint;
                if (dockPoint == null)
                {
                    FailJob("aucun dock camion");
                    break;
                }
                SetState(R2State.VersCamion, "vers " + bay.name + " (commande #" + orderId + ")");
                yield return Mover.NavigateTo(dockPoint.position, dockPoint.forward);
                if (Mover.LastResult != GridMover.NavResult.Arrive)
                {
                    FailJob("trajet camion interrompu");
                    yield return new WaitForSeconds(.5f);
                    continue;
                }

                while (Running && !missionAbort && !missionSuspend && bay.FreeSlots <= 0)
                {
                    SetState(R2State.AttenteCamion, "camion plein : attente d'un slot");
                    yield return new WaitForSeconds(FullRetryInterval);
                }
                if (!Running || missionAbort) break;
                // Suspension avec carton en main et camion plein : remise au banc.
                if (missionSuspend && bay.FreeSlots <= 0)
                {
                    yield return PutBackOnBench(Carried, bench, zone);
                    break;
                }

                SetState(R2State.Depose, "dépose dans " + bay.name);
                yield return Interact(false, Carried, zone, bay);
                if (Carried == null)
                {
                    Delivered++;
                    report.Loaded++;
                    if (registry != null) registry.Release(box, this);
                    if (Animator != null) Animator.SetBool("HasBox", false);
                    Status = report.Loaded + "/" + expectedCount + " cartons commande #" + orderId;
                    BoxLoaded?.Invoke(box);
                }
                else
                {
                    if (bay == null || !bay.PlaceBox(Carried)) DropAtFeet(Carried);
                    Carried = null;
                    if (Animator != null) Animator.SetBool("HasBox", false);
                    FailJob("dépose échouée (carton relâché)");
                }
            }

            if (missionAbort || missionSuspend)
            {
                report.Aborted = true;
                report.AbortReason = missionSuspend ? "suspendue pour commande urgente"
                                                    : "annulée par l'orchestrateur";
            }
            if (Carried != null) DropAtFeet(Carried);
            // Sortir du camion et rentrer à la base AVANT de rendre le rapport :
            // la porte ne se refermera que quand le robot est physiquement dehors,
            // et la mission reste active jusqu'au parking pour éviter tout chevauchement.
            // Repos seulement quand il n'y a vraiment plus de travail : file de
            // chargement en attente, mission suspendue ou commandes en pause dont
            // la reprise est imminente. Dans TOUS les cas R2 sort d'abord de la
            // remorque : la porte n'attend que ça pour se fermer (LoaderOutside)
            // et un robot garé dans le camion la bloquerait indéfiniment.
            bool stayOut = QueuedLoads > 0 || missionSuspend ||
                           (StayOut != null && StayOut());
            if (stayOut)
            {
                yield return ExitTrailer(bay);
            }
            else if (ReturnHomeWhenIdle && Running && !NearHome())
            {
                SetState(R2State.RetourBase, "mission terminée : retour à la base");
                yield return Mover.NavigateTo(homePos, homeForward);
            }
            MissionActive = false;
            DirectedOrderId = -1;
            done?.Invoke(report);
        }

        /// <summary>
        /// Faire sortir R2 de la remorque/embrasure sans rentrer à la base :
        /// TruckBay.IsInside dit exactement où s'arrête le volume intérieur —
        /// on sonde les deux côtés de la porte et on marche juste au-delà.
        /// </summary>
        private IEnumerator ExitTrailer(TruckBay bay)
        {
            if (bay == null || !bay.IsInside(transform.position)) yield break;
            Vector3 p = transform.position;
            float dir = 0f;
            for (float d = 1f; d <= 14f && dir == 0f; d += 1f)
            {
                if (!bay.IsInside(new Vector3(p.x - d, p.y, p.z))) dir = -1f;
                else if (!bay.IsInside(new Vector3(p.x + d, p.y, p.z))) dir = +1f;
            }
            if (dir == 0f) yield break; // géométrie inconnue : la porte se protège seule
            Vector3 target = p;
            for (int i = 0; i < 18 && bay.IsInside(target); i++)
                target.x += dir * 1f;
            target.x += dir * .8f; // marge au-delà du volume intérieur
            SetState(R2State.VersCamion, "sortie de " + bay.name);
            yield return Mover.NavigateTo(target, transform.forward);
        }

        /// <summary>
        /// Suspension avec carton en main impossible à charger (porte fermée ou
        /// camion plein) : le carton est REMIS sur le banc de sa commande —
        /// jamais posé au sol, jamais mélangé à une autre commande.
        /// </summary>
        private IEnumerator PutBackOnBench(CargoBox box, ShelfStorage bench,
                                           ShelfInteractionZone zone)
        {
            if (box == null) yield break;
            if (bench == null || zone == null || zone.shelf != bench || zone.dockPoint == null)
            {
                DropAtFeet(box);
                Carried = null;
                if (Animator != null) Animator.SetBool("HasBox", false);
                yield break;
            }
            SetState(R2State.VersBanc, "suspension : remise de " + box.name + " sur le banc");
            yield return Mover.NavigateTo(zone.dockPoint.position, zone.dockPoint.forward);
            int column = zone.columnIndex;
            int level = -1;
            if (!bench.HasFreePlaceToPut(column, out level))
            {
                level = -1; // colonne pleine : tout autre niveau libre du banc convient
                for (int c = 0; c < bench.columns.Length; c++)
                {
                    if (bench.HasFreePlaceToPut(c, out level)) { column = c; break; }
                }
            }
            if (level >= 0)
            {
                bench.PutBox(column, level, box);
                Carried = null;
                if (Animator != null) Animator.SetBool("HasBox", false);
                Status = "carton remis sur le banc (suspension)";
            }
            else
            {
                DropAtFeet(box);
                Carried = null;
                if (Animator != null) Animator.SetBool("HasBox", false);
            }
            if (registry != null && box != null) registry.Release(box, this);
        }

        /// <summary>Carton du dessus d'une colonne du banc, appartenant à la commande.</summary>
        private CargoBox FindOrderBox(ShelfStorage bench, int orderId, out ShelfInteractionZone zone)
        {
            zone = null;
            if (bench == null) return null;
            CargoBox best = null;
            ShelfInteractionZone bestZone = null;
            float bestDistance = float.PositiveInfinity;
            foreach (ShelfInteractionZone z in bench.GetComponentsInChildren<ShelfInteractionZone>(true))
            {
                if (z == null || z.dockPoint == null) continue;
                if (NavBlocker.BlocksPoint(z.dockPoint.position)) continue;
                CargoBox top = CargoRegistry.TopBox(z);
                if (top == null || top.orderId != orderId || top.isCarried) continue;
                if (registry != null && registry.IsReserved(top) &&
                    !ReferenceEquals(registry.ReservedBy(top), this)) continue;
                float d = Vector3.Distance(transform.position, z.dockPoint.position);
                if (d < bestDistance) { bestDistance = d; best = top; bestZone = z; }
            }
            zone = bestZone;
            return best;
        }
    }
}
