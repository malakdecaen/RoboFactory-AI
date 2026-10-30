using System.Collections.Generic;
using RoboFactory.Logistics;
using UnityEngine;
using UnityEngine.EventSystems;
using RoboFactory.Sim;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Vues caméra du HUD : vue d'origine de la scène (WarehouseCameraController, inchangé) et
    /// vue ORBITE. En orbite : glisser la souris pour tourner, molette pour un zoom / dézoom
    /// limité, rotation lente automatique sans interaction. Flèche gauche / droite : la caméra
    /// suit R1 / R2 (orbite autour du robot) ; haut / bas : retour à la vue d'ensemble.
    /// La hauteur reste sous le plafond (détecté par un rayon vers le haut, sinon 6,5 m).
    /// Le script caméra de la scène n'est que mis en pause ; la pose d'origine (position,
    /// rotation, FOV) est restaurée à l'identique au retour, avec un fondu de trajectoire.
    /// </summary>
    public sealed class HudCameraRig
    {
        public enum View { Default, Orbit }
        public enum Focus { Warehouse, R1, R2 }

        public View Current { get; private set; } = View.Default;
        public bool IsOrbit => Current == View.Orbit;
        public Focus CurrentFocus { get; private set; } = Focus.Warehouse;
        /// <summary>Libellé court du bouton caméra : SCENE / ORBIT / R1 / R2.</summary>
        public string Label => Current == View.Default ? "SCENE"
                             : CurrentFocus == Focus.R1 ? "R1" : CurrentFocus == Focus.R2 ? "R2" : "ORBIT";

        public float ZoomRange = .35f;        // ±35 % autour de la distance de base
        public float AutoSpeed = 4f;          // °/s
        public float IdleBeforeAuto = 4f;     // s sans interaction avant la rotation auto
        public float DragYaw = .25f, DragPitch = .18f;
        public float MinPitch = 14f, MaxPitch = 65f;
        public float BlendTime = .9f;
        public float OverviewMinDist = 6f, OverviewMaxDist = 11f;
        public float RobotDist = 4.2f;
        public float DefaultCeiling = 6.5f;   // hauteur max au-dessus du sol si aucun plafond détecté
        public float CeilingMargin = .7f;

        private Camera cam;
        private readonly List<Behaviour> paused = new List<Behaviour>();
        private Vector3 savedPos;
        private Quaternion savedRot;
        private float savedFov;

        private Vector3 overviewPivot, pivot;
        private float overviewDist;
        private Transform follow;
        private float ceilingY;
        private float yaw, pitch, dist, baseDist;
        private float targetYaw, targetPitch, targetDist;
        private float idle;
        private bool dragging;
        private Vector2 lastMouse;

        private float blend = 1f;
        private bool leaving;
        private Vector3 fromPos;
        private Quaternion fromRot;

        public void Toggle(WarehouseWorld world)
        {
            if (Current == View.Default) EnterOrbit(world);
            else if (!leaving) ExitOrbit();
        }

        /// <summary>Flèches : suivre R1 / R2, ou revenir à la vue d'ensemble (entre en orbite si besoin).</summary>
        public void FocusOn(Focus focus, WarehouseWorld world, Transform r1, Transform r2)
        {
            if (Current == View.Default) EnterOrbit(world);
            if (Current != View.Orbit || leaving) return;
            Transform target = focus == Focus.R1 ? r1 : focus == Focus.R2 ? r2 : null;
            if (focus != Focus.Warehouse && target == null) return;
            CurrentFocus = focus;
            follow = target;
            baseDist = focus == Focus.Warehouse ? overviewDist : RobotDist;
            targetDist = baseDist;
            targetPitch = focus == Focus.Warehouse ? 38f : 30f;
            idle = 0f;
        }

        private void EnterOrbit(WarehouseWorld world)
        {
            cam = Camera.main;
            if (cam == null) return;
            Transform t = cam.transform;
            savedPos = t.position;
            savedRot = t.rotation;
            savedFov = cam.fieldOfView;

            // le script caméra de la scène est mis en pause (pas modifié) pendant l'orbite
            paused.Clear();
            foreach (WarehouseCameraController c in Object.FindObjectsByType<WarehouseCameraController>(FindObjectsSortMode.None))
                if (c.enabled && (c.targetCamera == cam || c.transform == t || t.IsChildOf(c.transform)))
                {
                    c.enabled = false;
                    paused.Add(c);
                }

            overviewPivot = WorldCenter(world, out float radius);
            if (radius <= 0f) overviewPivot = GroundHit(t, out radius);
            // plus près qu'avant : une vue d'ensemble serrée, jamais au-dessus du toit
            overviewDist = Mathf.Clamp(radius * .55f, OverviewMinDist, OverviewMaxDist);
            ceilingY = DetectCeiling(overviewPivot);
            pivot = overviewPivot;
            follow = null;
            CurrentFocus = Focus.Warehouse;

            Vector3 off = t.position - pivot;
            Vector3 flat = new Vector3(off.x, 0f, off.z);
            yaw = flat.sqrMagnitude > .001f ? Mathf.Atan2(-flat.x, -flat.z) * Mathf.Rad2Deg : t.eulerAngles.y;
            pitch = 36f;
            dist = baseDist = overviewDist;
            targetYaw = yaw;
            targetPitch = pitch;
            targetDist = dist;
            idle = IdleBeforeAuto;
            dragging = false;

            fromPos = t.position;
            fromRot = t.rotation;
            blend = 0f;
            leaving = false;
            Current = View.Orbit;
        }

        private void ExitOrbit()
        {
            if (cam == null) { Restore(); return; }
            fromPos = cam.transform.position;
            fromRot = cam.transform.rotation;
            blend = 0f;
            leaving = true;
        }

        private void Restore()
        {
            if (cam != null)
            {
                cam.transform.SetPositionAndRotation(savedPos, savedRot);
                cam.fieldOfView = savedFov;
            }
            foreach (Behaviour b in paused) if (b != null) b.enabled = true;
            paused.Clear();
            leaving = false;
            follow = null;
            CurrentFocus = Focus.Warehouse;
            Current = View.Default;
        }

        /// <summary>Appelé en LateUpdate (après les scripts caméra de la scène).</summary>
        public void LateTick(float dt)
        {
            if (Current != View.Orbit) return;
            if (cam == null) { Restore(); return; }
            Transform t = cam.transform;

            if (leaving)
            {
                blend = Mathf.Clamp01(blend + dt / BlendTime);
                float k = Smooth(blend);
                t.SetPositionAndRotation(Vector3.Lerp(fromPos, savedPos, k), Quaternion.Slerp(fromRot, savedRot, k));
                if (blend >= 1f) Restore();
                return;
            }

            ReadInput(dt);
            // pivot : centre de l'entrepôt, ou robot suivi (lissé)
            Vector3 goal = follow != null ? new Vector3(follow.position.x, overviewPivot.y + .6f, follow.position.z) : overviewPivot;
            pivot = Vector3.Lerp(pivot, goal, 1f - Mathf.Exp(-4f * dt));

            float s = 1f - Mathf.Exp(-8f * dt);
            yaw = Mathf.LerpAngle(yaw, targetYaw, s);
            pitch = Mathf.Lerp(pitch, targetPitch, s);
            dist = Mathf.Lerp(dist, targetDist, s);
            // jamais au-dessus du plafond : l'angle est plafonné selon la distance
            float room = Mathf.Max(1f, ceilingY - CeilingMargin - pivot.y);
            float roofPitch = Mathf.Asin(Mathf.Clamp01(room / Mathf.Max(.1f, dist))) * Mathf.Rad2Deg;
            float usePitch = Mathf.Clamp(Mathf.Min(pitch, roofPitch), 6f, MaxPitch);

            Quaternion orbit = Quaternion.Euler(usePitch, yaw, 0f);
            Vector3 pos = pivot + orbit * new Vector3(0f, 0f, -dist);
            Quaternion rot = Quaternion.LookRotation(pivot - pos, Vector3.up);
            if (blend < 1f)
            {
                blend = Mathf.Clamp01(blend + dt / BlendTime);
                float k = Smooth(blend);
                pos = Vector3.Lerp(fromPos, pos, k);
                rot = Quaternion.Slerp(fromRot, rot, k);
            }
            t.SetPositionAndRotation(pos, rot);
        }

        private void ReadInput(float dt)
        {
            Vector2 mouse = HudInput.MousePosition();
            bool held = HudInput.MouseHeld();
            if (held && !dragging && !PointerOverUi()) { dragging = true; lastMouse = mouse; }
            if (!held) dragging = false;
            bool touched = false;
            if (dragging)
            {
                Vector2 d = mouse - lastMouse;
                lastMouse = mouse;
                if (d.sqrMagnitude > .01f)
                {
                    targetYaw += d.x * DragYaw;
                    targetPitch = Mathf.Clamp(targetPitch - d.y * DragPitch, MinPitch, MaxPitch);
                    touched = true;
                }
            }
            float scroll = HudInput.ScrollY();
            if (Mathf.Abs(scroll) > .01f && !PointerOverUi())
            {
                targetDist = Mathf.Clamp(targetDist * (1f - Mathf.Sign(scroll) * .08f),
                                         baseDist * (1f - ZoomRange), baseDist * (1f + ZoomRange));
                touched = true;
            }
            idle = touched || dragging ? 0f : idle + dt;
            if (idle >= IdleBeforeAuto) targetYaw += AutoSpeed * dt;
        }

        private static bool PointerOverUi()
        {
            EventSystem es = EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        }

        /// <summary>Hauteur du plafond au-dessus du point (premier collider vers le haut), sinon défaut.</summary>
        private float DetectCeiling(Vector3 at)
        {
            float fallback = at.y + DefaultCeiling;
            // un obstacle bas (étagère, lampe) n'est pas un plafond : on ne retient qu'un toit > 3,5 m
            if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.up, out RaycastHit hit, 40f, ~0, QueryTriggerInteraction.Ignore) &&
                hit.point.y > at.y + 3.5f)
                return Mathf.Min(hit.point.y, at.y + 12f);
            return fallback;
        }

        /// <summary>Centre des palettes, bancs et camions réels (sol) et rayon englobant.</summary>
        private static Vector3 WorldCenter(WarehouseWorld world, out float radius)
        {
            radius = 0f;
            if (world == null) return Vector3.zero;
            bool any = false;
            var b = new Bounds();
            void Add(Component c)
            {
                if (c == null) return;
                if (!any) { b = new Bounds(c.transform.position, Vector3.zero); any = true; }
                else b.Encapsulate(c.transform.position);
            }
            foreach (string id in world.PalletIds) Add(world.Pallet(id));
            foreach (string id in world.BenchIds) Add(world.Bench(id));
            foreach (string id in world.TruckIds) Add(world.Truck(id));
            if (!any) return Vector3.zero;
            radius = new Vector2(b.extents.x, b.extents.z).magnitude;
            return new Vector3(b.center.x, b.min.y, b.center.z);
        }

        /// <summary>Repli : point du sol (y = 0) visé par la caméra.</summary>
        private static Vector3 GroundHit(Transform t, out float radius)
        {
            radius = 10f;
            Vector3 f = t.forward;
            if (f.y < -.05f)
            {
                float k = -t.position.y / f.y;
                return t.position + f * k;
            }
            return t.position + new Vector3(f.x, 0f, f.z).normalized * 10f;
        }

        private static float Smooth(float x) { return x * x * (3f - 2f * x); }
    }
}
