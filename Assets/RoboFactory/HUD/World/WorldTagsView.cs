using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Étiquettes holographiques (V2) ancrées aux VRAIS Transform : robots (activité réelle,
    /// cible, charge, état), ressources sélectionnées (orange) et camions. Projetées en écran
    /// chaque frame : texte net, taille constante. Les cartouches des robots restent dans la
    /// zone libre entre les panneaux (SafeArea) ; seul le trait de rappel rejoint le robot.
    /// </summary>
    public sealed class WorldTagsView
    {
        public RectTransform Root { get; private set; }
        public bool ShowRobots = true;
        public bool ShowResources = true;
        /// <summary>Zone libre (repère local de Root) où les cartouches robots peuvent se placer.</summary>
        public Rect SafeArea;
        /// <summary>Zone du cercle Muse (repère local de Root) que les cartouches doivent éviter.</summary>
        public Rect AvoidArea;

        private RobotTag r1Tag, r2Tag;

        // ---- survol souris : les bulles n'apparaissent que sur l'objet survolé (DEBUG : toutes)
        /// <summary>Position souris dans le repère des étiquettes (valide si MouseOk).</summary>
        public Vector2 Mouse;
        public bool MouseOk;
        /// <summary>Mode DEBUG : bulles robots + ressources réservées toujours visibles (ancien comportement).</summary>
        public bool ShowAll;
        private const float HoverLinger = .35f;
        private readonly Dictionary<Transform, Renderer[]> renderers = new Dictionary<Transform, Renderer[]>();
        private readonly Dictionary<Transform, float> renderersAt = new Dictionary<Transform, float>();
        private readonly Dictionary<string, float> hoverUntil = new Dictionary<string, float>();
        private readonly Vector3[] corners8 = new Vector3[8];
        private readonly Dictionary<string, SmallTag> smallTags = new Dictionary<string, SmallTag>();
        private readonly HashSet<string> seen = new HashSet<string>();
        private readonly List<string> stale = new List<string>();
        private readonly Dictionary<Transform, float> heights = new Dictionary<Transform, float>();
        private float time;

        private const float TagW = 252f, TagH = 94f;

        private sealed class RobotTag
        {
            public RectTransform Box;
            public CanvasGroup Group;
            public SegmentGraphic Leader;
            public Image Anchor, AnchorRing, Header, Accent;
            public TextMeshProUGUI Id, Role, Activity, Load, Cargo;
            public Chip State;
            public Vector2 Smoothed, BoxPos;
            public bool Init;
        }

        private sealed class SmallTag
        {
            public RectTransform Box;
            public CanvasGroup Group;
            public SegmentGraphic Leader;
            public Image Stroke, Dot;
            public TextMeshProUGUI Text;
            public Vector2 Smoothed;
            public bool Init;
            public float Alpha;
        }

        public void Build(RectTransform parent)
        {
            Root = HudFactory.Stretch(HudFactory.Rect("World Tags", parent));
            r1Tag = BuildRobotTag("R1");
            r2Tag = BuildRobotTag("R2");
        }

        private RobotTag BuildRobotTag(string id)
        {
            var t = new RobotTag();
            t.Leader = HudFactory.Add<SegmentGraphic>(Root, id + " Leader");
            t.Leader.Width = 1.6f;
            HudFactory.Place(t.Leader.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), Vector2.zero, new Vector2(10f, 10f));
            t.Anchor = HudFactory.Image(Root, id + " Anchor", HudSprites.Circle(), HudTheme.Cyan, false);
            t.Anchor.rectTransform.sizeDelta = new Vector2(8f, 8f);
            t.AnchorRing = HudFactory.Image(Root, id + " Anchor Ring", HudSprites.Rounded(9, 1.5f), HudTheme.Cyan);
            t.AnchorRing.rectTransform.sizeDelta = new Vector2(20f, 20f);

            t.Box = HudFactory.GlassPanel(Root, id + " Tag", HudTheme.GlassStrong, null, 9, false);
            t.Box.anchorMin = t.Box.anchorMax = new Vector2(.5f, .5f);
            t.Box.pivot = new Vector2(0f, 0f);
            t.Box.sizeDelta = new Vector2(TagW, TagH);
            t.Group = t.Box.gameObject.AddComponent<CanvasGroup>();
            t.Group.blocksRaycasts = false;
            t.Header = HudFactory.Image(t.Box, "Header", null, HudTheme.A(HudTheme.Cyan, .14f));
            HudFactory.TopStrip(t.Header.rectTransform, 2f, 30f, 2f, 2f);
            t.Accent = HudFactory.Image(t.Box, "Accent", null, HudTheme.Cyan);
            HudFactory.TopStrip(t.Accent.rectTransform, 31f, 1.5f, 2f, 2f);
            Image icon = HudFactory.Icon(t.Box, "Icon", HudIcon.Robot, 20f, HudTheme.TextHi);
            HudFactory.Place(icon.rectTransform, new Vector2(0f, 1f), new Vector2(.5f, .5f), new Vector2(18f, -17f), new Vector2(20f, 20f));
            t.Id = HudFactory.Text(t.Box, "Id", id, HudTheme.FontValue, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(t.Id.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -4f), new Vector2(40f, 26f));
            t.Role = HudFactory.Text(t.Box, "Role", "", HudTheme.FontMicro, HudTheme.TextMid, false, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(t.Role.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(64f, -9f), new Vector2(0f, 20f));
            t.Role.gameObject.SetActive(false);
            t.State = HudFactory.Chip(t.Box, "State", "", HudTheme.Cyan, 112f, 22f);
            HudFactory.Place(t.State.Root, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-6f, -6f), new Vector2(112f, 22f));
            t.State.Label.characterSpacing = 0f;
            t.Activity = HudFactory.Text(t.Box, "Activity", "", HudTheme.FontBody, HudTheme.TextHi, true, TextAlignmentOptions.Left, .5f);
            HudFactory.Place(t.Activity.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(12f, -37f), new Vector2(TagW - 24f, 26f));
            t.Load = HudFactory.Text(t.Box, "Load", "", HudTheme.FontSmall, HudTheme.TextMid);
            HudFactory.Place(t.Load.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(12f, -65f), new Vector2(150f, 22f));
            t.Cargo = HudFactory.Text(t.Box, "Cargo", "", HudTheme.FontSmall, HudTheme.TextMid, true, TextAlignmentOptions.Right);
            HudFactory.Place(t.Cargo.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -65f), new Vector2(90f, 22f));
            return t;
        }

        public void Tick(float dt, HudModel m, Camera cam)
        {
            time += dt;
            UpdateRobot(r1Tag, m.R1, m, cam, -1f, dt, null);
            UpdateRobot(r2Tag, m.R2, m, cam, 1f, dt, r1Tag);

            seen.Clear();
            if (ShowResources && cam != null)
            {
                foreach (ResourceVM res in m.Resources)
                {
                    if (res.Transform == null) continue;
                    string key = "M:" + res.Id;
                    bool reserved = res.OrderId >= 0;
                    bool show = (ShowAll && reserved) || Hovered(key, res.Transform, cam) ||
                                (smallTags.TryGetValue(key, out SmallTag open) && open.Alpha > .5f && OverBox(open));
                    if (!show) continue;
                    string kind = res.Kind == MarkKind.Pallet ? "PALLET" : res.Kind == MarkKind.Bench ? "BENCH" : "TRUCK";
                    string text = "<color=#" + HudTheme.Hex(HudTheme.TextMid) + ">" + kind + "</color>  <b>" + res.Id + "</b>" +
                                  (string.IsNullOrEmpty(res.Detail) ? "" : "  <color=#" + HudTheme.Hex(HudTheme.TextMid) + ">·</color>  " + res.Detail);
                    UpdateSmall(key, res.Transform, text, reserved ? HudTheme.Orange : HudTheme.Cyan, cam, dt,
                                res.Kind == MarkKind.Truck ? .4f : .3f);
                }
            }
            stale.Clear();
            foreach (KeyValuePair<string, SmallTag> kv in smallTags)
            {
                if (seen.Contains(kv.Key)) continue;
                SmallTag s = kv.Value;
                s.Alpha = Mathf.MoveTowards(s.Alpha, 0f, dt * 4f);
                s.Group.alpha = s.Alpha;
                HudFactory.SetColor(s.Leader, HudTheme.A(HudTheme.Orange, .7f * s.Alpha));
                if (s.Alpha <= 0f) stale.Add(kv.Key);
            }
            // les cartouches robots restent au premier plan ; une étiquette de ressource cachée dessous s'efface
            r1Tag.Box.SetAsLastSibling();
            r2Tag.Box.SetAsLastSibling();
            var tagA = new Rect(r1Tag.BoxPos, new Vector2(TagW, TagH));
            var tagB = new Rect(r2Tag.BoxPos, new Vector2(TagW, TagH));
            foreach (KeyValuePair<string, SmallTag> kv in smallTags)
            {
                SmallTag s = kv.Value;
                var r = new Rect(s.Box.anchoredPosition - new Vector2(s.Box.sizeDelta.x * .5f, 0f), s.Box.sizeDelta);
                bool hidden = (r1Tag.Group.alpha > .1f && r.Overlaps(tagA)) || (r2Tag.Group.alpha > .1f && r.Overlaps(tagB));
                if (hidden) s.Group.alpha = Mathf.Min(s.Group.alpha, .2f);
            }
            foreach (string key in stale)
            {
                SmallTag s = smallTags[key];
                Object.Destroy(s.Box.gameObject);
                Object.Destroy(s.Leader.gameObject);
                smallTags.Remove(key);
            }
        }

        private bool Project(Camera cam, Vector3 world, out Vector2 local)
        {
            local = Vector2.zero;
            if (cam == null) return false;
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= .1f) return false;
            if (sp.x < -50f || sp.y < -50f || sp.x > Screen.width + 50f || sp.y > Screen.height + 50f) return false;
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(Root, sp, null, out local);
        }

        private void UpdateRobot(RobotTag t, RobotVM r, HudModel m, Camera cam, float side, float dt, RobotTag other)
        {
            bool visible = ShowRobots && r.Present && r.Transform != null;
            if (visible && !ShowAll)
                visible = Hovered("R:" + r.Id, r.Transform, cam) ||
                          (t.Group.alpha > .5f && MouseOk && new Rect(t.BoxPos, new Vector2(TagW, TagH)).Contains(Mouse));
            Vector2 p = Vector2.zero;
            if (visible) visible = Project(cam, r.Transform.position + Vector3.up * .8f, out p);
            t.Group.alpha = Mathf.MoveTowards(t.Group.alpha, visible ? 1f : 0f, dt * 5f);
            float a = t.Group.alpha;
            if (!visible && a <= 0f)
            {
                HudFactory.SetColor(t.Anchor, Color.clear);
                HudFactory.SetColor(t.AnchorRing, Color.clear);
                HudFactory.SetColor(t.Leader, Color.clear);
                return;
            }
            if (visible)
            {
                if (!t.Init) { t.Smoothed = p; t.Init = true; }
                t.Smoothed = Vector2.Lerp(t.Smoothed, p, 1f - Mathf.Exp(-18f * dt));
            }
            OrderVM o = m.FindOrder(r.OrderId);
            Color c = o != null && o.Urgent ? HudTheme.Orange : HudTheme.Cyan;
            Vector2 anchor = t.Smoothed;

            // cartouche au-dessus du robot, décalé vers l'extérieur, puis contraint à la zone libre
            Vector2 desired = anchor + new Vector2(side < 0f ? -TagW - 30f : 30f, 46f);
            if (SafeArea.width > TagW && SafeArea.height > TagH)
            {
                desired.x = Mathf.Clamp(desired.x, SafeArea.xMin, SafeArea.xMax - TagW);
                desired.y = Mathf.Clamp(desired.y, SafeArea.yMin, SafeArea.yMax - TagH);
            }
            // jamais sur le cercle Muse : on glisse le cartouche sur le côté le plus proche
            if (AvoidArea.width > 0f && new Rect(desired, new Vector2(TagW, TagH)).Overlaps(AvoidArea))
            {
                bool left = desired.x + TagW * .5f < AvoidArea.center.x;
                desired.x = left ? AvoidArea.xMin - TagW - 10f : AvoidArea.xMax + 10f;
            }
            // pas de chevauchement entre les deux cartouches : on décale verticalement le second
            if (other != null && other.Group.alpha > .05f)
            {
                var me = new Rect(desired, new Vector2(TagW, TagH));
                var it = new Rect(other.BoxPos, new Vector2(TagW, TagH));
                if (me.Overlaps(it))
                {
                    float below = it.y - TagH - 10f, above = it.y + TagH + 10f;
                    bool canBelow = SafeArea.height <= TagH || below >= SafeArea.yMin;
                    desired.y = canBelow ? below : above;
                }
            }
            t.BoxPos = t.Init && t.BoxPos != Vector2.zero ? Vector2.Lerp(t.BoxPos, desired, 1f - Mathf.Exp(-12f * dt)) : desired;
            t.Box.anchoredPosition = t.BoxPos;
            Vector2 attach = new Vector2(Mathf.Clamp(anchor.x, t.BoxPos.x + 10f, t.BoxPos.x + TagW - 10f),
                                         anchor.y < t.BoxPos.y ? t.BoxPos.y : t.BoxPos.y + TagH);
            t.Leader.SetEnds(attach, anchor);
            t.Anchor.rectTransform.anchoredPosition = anchor;
            t.AnchorRing.rectTransform.anchoredPosition = anchor;
            float pulse = .5f + .5f * Mathf.Sin(time * 4f);
            t.AnchorRing.rectTransform.localScale = Vector3.one * (1f + pulse * .4f);
            HudFactory.SetColor(t.Leader, HudTheme.A(c, .85f * a));
            HudFactory.SetColor(t.Anchor, HudTheme.A(c, a));
            HudFactory.SetColor(t.AnchorRing, HudTheme.A(c, a * (1f - pulse * .6f)));
            HudFactory.SetColor(t.Header, HudTheme.A(c, .16f));
            HudFactory.SetColor(t.Accent, HudTheme.A(c, .9f));
            HudFactory.Set(t.Role, r.Role);
            t.State.Set(r.State, StateColor(r));
            HudFactory.Set(t.Activity, r.Activity + (string.IsNullOrEmpty(r.ActivityTarget) ? "" : "  <color=#" + HudTheme.Hex(c) + ">› " + r.ActivityTarget + "</color>"));
            HudFactory.Set(t.Load, r.Role + (r.OrderId >= 0 ? "  ·  #" + r.OrderId : ""));
            HudFactory.Set(t.Cargo, r.Carrying ? r.Load : "EMPTY");
            HudFactory.SetColor(t.Cargo, r.Carrying ? HudTheme.Amber : HudTheme.TextLow);
        }

        /// <summary>
        /// La souris est-elle sur l'objet ? Test sur le rectangle écran de ses volumes de rendu
        /// réels (élargi de 10 px), avec un court maintien pour éviter le clignotement.
        /// </summary>
        private bool Hovered(string key, Transform target, Camera cam)
        {
            bool over = MouseOk && cam != null && ScreenRect(target, cam, out Rect r) && r.Contains(Mouse);
            if (over) hoverUntil[key] = time + HoverLinger;
            return over || (hoverUntil.TryGetValue(key, out float until) && time < until);
        }

        private bool OverBox(SmallTag s)
        {
            if (!MouseOk) return false;
            var r = new Rect(s.Box.anchoredPosition - new Vector2(s.Box.sizeDelta.x * .5f, 0f), s.Box.sizeDelta);
            return r.Contains(Mouse);
        }

        private bool ScreenRect(Transform t, Camera cam, out Rect rect)
        {
            rect = default;
            // cache rafraîchi : des cartons quittent une palette (pris par R1) ou arrivent sur un banc
            if (!renderers.TryGetValue(t, out Renderer[] list) || list == null ||
                !renderersAt.TryGetValue(t, out float at) || time - at > 1.5f)
            {
                renderersAt[t] = time;
                var found = new List<Renderer>();
                foreach (Renderer r in t.GetComponentsInChildren<Renderer>())
                    if (r != null && !(r is ParticleSystemRenderer) && !(r is LineRenderer)) found.Add(r);
                list = found.ToArray();
                renderers[t] = list;
            }
            bool has = false;
            Bounds b = default;
            foreach (Renderer r in list)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || !r.transform.IsChildOf(t)) continue;
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!has) b = new Bounds(t.position + Vector3.up * .5f, Vector3.one);
            Vector3 mn = b.min, mx = b.max;
            corners8[0] = new Vector3(mn.x, mn.y, mn.z); corners8[1] = new Vector3(mx.x, mn.y, mn.z);
            corners8[2] = new Vector3(mn.x, mx.y, mn.z); corners8[3] = new Vector3(mx.x, mx.y, mn.z);
            corners8[4] = new Vector3(mn.x, mn.y, mx.z); corners8[5] = new Vector3(mx.x, mn.y, mx.z);
            corners8[6] = new Vector3(mn.x, mx.y, mx.z); corners8[7] = new Vector3(mx.x, mx.y, mx.z);
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            int ok = 0;
            foreach (Vector3 c in corners8)
            {
                Vector3 sp = cam.WorldToScreenPoint(c);
                if (sp.z <= .1f) continue;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Root, sp, null, out Vector2 l)) continue;
                xMin = Mathf.Min(xMin, l.x); xMax = Mathf.Max(xMax, l.x);
                yMin = Mathf.Min(yMin, l.y); yMax = Mathf.Max(yMax, l.y);
                ok++;
            }
            if (ok == 0) return false;
            rect = Rect.MinMaxRect(xMin - 10f, yMin - 10f, xMax + 10f, yMax + 10f);
            return true;
        }

        private static Color StateColor(RobotVM r)
        {
            switch (r.State)
            {
                case "OFFLINE": case "STOPPED": case "ABORTED": case "PATH BLOCKED": return HudTheme.Red;
                case "SUSPENDED": case "REPLANNING": case "BENCH FULL": case "DOCK BUSY": case "WAITING": return HudTheme.Amber;
                case "IDLE": return HudTheme.TextMid;
                default: return HudTheme.Green;
            }
        }

        private void UpdateSmall(string key, Transform target, string text, Color c, Camera cam, float dt, float lift)
        {
            float top = TopOf(target);
            if (!Project(cam, new Vector3(target.position.x, top + lift, target.position.z), out Vector2 p)) return;
            seen.Add(key);
            if (!smallTags.TryGetValue(key, out SmallTag s))
            {
                s = BuildSmall(key);
                smallTags[key] = s;
            }
            if (!s.Init) { s.Smoothed = p; s.Init = true; }
            s.Smoothed = Vector2.Lerp(s.Smoothed, p, 1f - Mathf.Exp(-18f * dt));
            s.Alpha = Mathf.MoveTowards(s.Alpha, 1f, dt * 4f);
            s.Group.alpha = s.Alpha;
            Vector2 box = s.Smoothed + new Vector2(0f, 24f);
            s.Box.anchoredPosition = box;
            s.Leader.SetEnds(box, s.Smoothed);
            HudFactory.SetColor(s.Leader, HudTheme.A(c, .75f * s.Alpha));
            HudFactory.SetColor(s.Stroke, HudTheme.A(c, .9f));
            HudFactory.SetColor(s.Dot, c);
            HudFactory.Set(s.Text, text);
            float w = Mathf.Min(260f, s.Text.GetPreferredValues(text).x + 34f);
            s.Box.sizeDelta = new Vector2(w, 28f);
        }

        private SmallTag BuildSmall(string key)
        {
            var s = new SmallTag();
            s.Leader = HudFactory.Add<SegmentGraphic>(Root, key + " Leader");
            s.Leader.Width = 1.4f;
            HudFactory.Place(s.Leader.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), Vector2.zero, new Vector2(10f, 10f));
            s.Box = HudFactory.Rect(key + " Tag", Root);
            s.Box.anchorMin = s.Box.anchorMax = new Vector2(.5f, .5f);
            s.Box.pivot = new Vector2(.5f, 0f);
            s.Group = s.Box.gameObject.AddComponent<CanvasGroup>();
            s.Group.blocksRaycasts = false;
            s.Group.alpha = 0f;
            Image fill = HudFactory.Image(s.Box, "Fill", HudSprites.Rounded(4), HudTheme.GlassStrong);
            HudFactory.Stretch(fill.rectTransform);
            s.Stroke = HudFactory.Image(s.Box, "Stroke", HudSprites.Rounded(4, 1.3f), HudTheme.Orange);
            HudFactory.Stretch(s.Stroke.rectTransform);
            s.Dot = HudFactory.Image(s.Box, "Dot", HudSprites.Circle(), HudTheme.Orange, false);
            HudFactory.Place(s.Dot.rectTransform, new Vector2(0f, .5f), new Vector2(.5f, .5f), new Vector2(13f, 0f), new Vector2(7f, 7f));
            s.Text = HudFactory.Text(s.Box, "Text", "", HudTheme.FontSmall, HudTheme.TextHi, false, TextAlignmentOptions.Left, 1f);
            HudFactory.Stretch(s.Text.rectTransform, 24f, 0f, 8f, 0f);
            return s;
        }

        private float TopOf(Transform t)
        {
            if (heights.TryGetValue(t, out float h)) return h;
            float top = t.position.y;
            bool has = false;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                top = has ? Mathf.Max(top, r.bounds.max.y) : r.bounds.max.y;
                has = true;
            }
            heights[t] = top;
            return top;
        }

        public void SetVisible(bool robots, bool resources)
        {
            ShowRobots = robots;
            ShowResources = resources;
        }
    }
}
