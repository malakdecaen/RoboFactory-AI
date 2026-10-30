using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// AI DECISIONS panel (V3) — the most important one for the jury. A hero card readable from
    /// a distance: ORDER # and its real state (EXECUTING / PAUSED / DELIVERED), then the instructions
    /// Muse Wonder actually sent — R1 PREPARE pallet › bench, R2 LOAD bench › truck,
    /// TRUCK SHIP — each with its real progress (DONE / ACTIVE / QUEUED / PAUSED), WHY
    /// (the model's justification), validator verdict, real model and latency.
    /// The card follows the decision still being executed: a delivered urgent order gives way
    /// to the plan actually running. Missing field = « — », never a fictitious example.
    /// </summary>
    public sealed class DecisionPanelView
    {
        public RectTransform Root { get; private set; }

        private const float W = HudTheme.DecisionsWidth;
        private const float RowH = 50f;
        private const float PlanLabelH = 24f;

        private TextMeshProUGUI modelText, latencyText, linkText;
        private Image linkDot, panelFlash, headerBand;
        private RectTransform card, planGroup, whyGroup, validatorGroup, incidentGroup, historyGroup;
        private Image cardFill, cardAccent, cardFlash;
        private TextMeshProUGUI title, sub, whyText, incidentText, emptyText;
        private Chip verdictChip, triggerChip, replanChip;
        private readonly TextMeshProUGUI[] keys = new TextMeshProUGUI[3];
        private readonly TextMeshProUGUI[] values = new TextMeshProUGUI[3];
        private readonly TextMeshProUGUI[] metas = new TextMeshProUGUI[3];
        private readonly Chip[] states = new Chip[3];
        private readonly Image[] icons = new Image[3];
        private readonly TextMeshProUGUI[] history = new TextMeshProUGUI[2];
        private readonly Image[] historyDots = new Image[2];
        private readonly List<CanvasGroup> reveal = new List<CanvasGroup>();
        private float revealT = 10f, flashT = 10f;
        private Color accent = HudTheme.Cyan;

        // instructions sent by Wonder: R1 pick, R2 load, truck shipping
        private static readonly string[] Keys = { "R1", "R2", "TRUCK" };
        private static readonly HudIcon[] Icons = { HudIcon.Robot, HudIcon.Robot, HudIcon.Truck };
        private DecisionVM shown;

        public void Build(RectTransform parent)
        {
            Root = HudFactory.GlassPanel(parent, "AI Decisions Panel");
            HudFactory.Place(Root, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-HudTheme.Margin, -HudTheme.SideTop), new Vector2(W, 600f));
            panelFlash = HudFactory.Image(Root, "Flash", HudSprites.ChamferStroke(12, 2.5f), Color.clear);
            HudFactory.Stretch(panelFlash.rectTransform, -2f, -2f, -2f, -2f);

            HudFactory.Header(Root, HudIcon.Spark, "AI DECISIONS");
            Transform header = Root.Find("Header");
            headerBand = header.Find("Band").GetComponent<Image>();
            linkDot = HudFactory.Image(header, "Link Dot", HudSprites.Circle(), HudTheme.Green, false);
            HudFactory.Place(linkDot.rectTransform, new Vector2(1f, .5f), new Vector2(.5f, .5f), new Vector2(-112f, 0f), new Vector2(9f, 9f));
            linkText = HudFactory.Text(header, "Link", "", HudTheme.FontMicro, HudTheme.Green, true, TextAlignmentOptions.Left, 1.5f);
            HudFactory.Place(linkText.rectTransform, new Vector2(1f, .5f), new Vector2(0f, .5f), new Vector2(-102f, 0f), new Vector2(96f, 20f));

            // ---- real model + latency
            RectTransform meta = HudFactory.TopStrip(HudFactory.Rect("Meta Link", Root), 56f, 50f, HudTheme.Pad, HudTheme.Pad);
            TextMeshProUGUI ml = HudFactory.Label(meta, "Model Label", "META MODEL", HudTheme.FontMicro);
            HudFactory.Place(ml.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(200f, 18f));
            modelText = HudFactory.Text(meta, "Model", "—", HudTheme.FontBody, HudTheme.TextHi, true);
            HudFactory.Place(modelText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 2f), new Vector2(210f, 26f));
            TextMeshProUGUI ll = HudFactory.Label(meta, "Latency Label", "LATENCY", HudTheme.FontMicro, null, TextAlignmentOptions.Right);
            HudFactory.Place(ll.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), Vector2.zero, new Vector2(100f, 18f));
            latencyText = HudFactory.Text(meta, "Latency", "—", HudTheme.FontKey, HudTheme.Cyan, true, TextAlignmentOptions.Right);
            HudFactory.Place(latencyText.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(110f, 30f));

            // ---- hero card
            card = HudFactory.Rect("Decision Card", Root);
            HudFactory.TopStrip(card, 116f, 380f, 10f, 10f);
            cardFill = HudFactory.Image(card, "Fill", HudSprites.ChamferFill(10), HudTheme.GlassLight);
            HudFactory.Stretch(cardFill.rectTransform);
            cardFlash = HudFactory.Image(card, "Flash", HudSprites.ChamferStroke(10, 1.8f), Color.clear);
            HudFactory.Stretch(cardFlash.rectTransform);
            cardAccent = HudFactory.Image(card, "Accent", null, HudTheme.Cyan);
            cardAccent.rectTransform.anchorMin = new Vector2(0f, 1f);
            cardAccent.rectTransform.anchorMax = new Vector2(0f, 1f);
            cardAccent.rectTransform.pivot = new Vector2(0f, 1f);
            cardAccent.rectTransform.anchoredPosition = new Vector2(0f, -12f);
            cardAccent.rectTransform.sizeDelta = new Vector2(4f, 52f);

            RectTransform head = Group(card, "Title", 8f, 66f);
            title = HudFactory.Text(head, "Title", "", HudTheme.FontHero, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -2f), new Vector2(W - 60f, 38f));
            verdictChip = HudFactory.Chip(head, "Verdict", "", HudTheme.Green, 100f, 24f);
            HudFactory.Place(verdictChip.Root, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -10f), new Vector2(100f, 24f));
            triggerChip = HudFactory.Chip(head, "Trigger", "", HudTheme.Violet, 156f, 22f);
            HudFactory.Place(triggerChip.Root, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(16f, 2f), new Vector2(156f, 22f));
            replanChip = HudFactory.Chip(head, "Replan", "REPLAN", HudTheme.Amber, 80f, 22f);
            HudFactory.Place(replanChip.Root, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(178f, 2f), new Vector2(80f, 22f));
            sub = HudFactory.Text(head, "Age", "", HudTheme.FontMicro, HudTheme.TextLow, false, TextAlignmentOptions.Right);
            HudFactory.Place(sub.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-12f, 3f), new Vector2(110f, 20f));

            planGroup = Group(card, "Plan", 80f, PlanLabelH + Keys.Length * RowH);
            TextMeshProUGUI pl = HudFactory.Label(planGroup, "Label", "COMMANDS SENT BY WONDER", HudTheme.FontMicro);
            HudFactory.Place(pl.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -2f), new Vector2(W - 40f, 18f));
            for (int i = 0; i < Keys.Length; i++)
            {
                float y = -PlanLabelH - i * RowH;
                if (i > 0)
                {
                    Image sep = HudFactory.Divider(planGroup, "Separator " + i, .18f);
                    HudFactory.TopStrip(sep.rectTransform, PlanLabelH + i * RowH - 1f, 1f, 16f, 16f);
                }
                icons[i] = HudFactory.Icon(planGroup, "Icon " + Keys[i], Icons[i], 22f, HudTheme.Cyan);
                HudFactory.Place(icons[i].rectTransform, new Vector2(0f, 1f), new Vector2(.5f, .5f), new Vector2(28f, y - RowH * .5f), new Vector2(22f, 22f));
                keys[i] = HudFactory.Label(planGroup, "Key " + Keys[i], Keys[i], HudTheme.FontMicro);
                HudFactory.Place(keys[i].rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(46f, y - 6f), new Vector2(60f, 18f));
                values[i] = HudFactory.Text(planGroup, "Command " + Keys[i], "—", HudTheme.FontValue, HudTheme.TextHi, true);
                values[i].textWrappingMode = TextWrappingModes.NoWrap;
                values[i].overflowMode = TextOverflowModes.Ellipsis;
                HudFactory.Place(values[i].rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(46f, y - 20f), new Vector2(W - 156f, 26f));
                metas[i] = HudFactory.Text(planGroup, "Detail " + Keys[i], "", HudTheme.FontSmall, HudTheme.TextMid);
                metas[i].textWrappingMode = TextWrappingModes.NoWrap;
                metas[i].overflowMode = TextOverflowModes.Ellipsis;
                HudFactory.Place(metas[i].rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(96f, y - 3f), new Vector2(W - 206f, 20f));
                states[i] = HudFactory.Chip(planGroup, "State " + Keys[i], "", HudTheme.Cyan, 92f, 24f);
                HudFactory.Place(states[i].Root, new Vector2(1f, 1f), new Vector2(1f, .5f), new Vector2(-12f, y - RowH * .5f), new Vector2(92f, 24f));
            }

            whyGroup = Group(card, "Why", 80f + PlanLabelH + Keys.Length * RowH + 6f, 76f);
            Image whyBack = HudFactory.Image(whyGroup, "Back", HudSprites.Rounded(4), new Color(0f, .02f, .05f, .6f));
            HudFactory.Stretch(whyBack.rectTransform, 12f, 0f, 12f, 0f);
            TextMeshProUGUI wl = HudFactory.Text(whyGroup, "Label", "WHY", HudTheme.FontMicro, HudTheme.Cyan, true, TextAlignmentOptions.Left, 2f);
            HudFactory.Place(wl.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -6f), new Vector2(60f, 18f));
            whyText = HudFactory.Text(whyGroup, "Text", "", 16f, HudTheme.TextHi);
            whyText.textWrappingMode = TextWrappingModes.Normal;
            whyText.maxVisibleLines = 3;
            whyText.alignment = TextAlignmentOptions.TopLeft;
            whyText.lineSpacing = -6f;
            HudFactory.Place(whyText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(66f, -5f), new Vector2(W - 100f, 66f));

            validatorGroup = Group(card, "Validator", 80f + PlanLabelH + Keys.Length * RowH + 86f, 26f);
            Image vi = HudFactory.Icon(validatorGroup, "Icon", HudIcon.Check, 18f, HudTheme.Green);
            HudFactory.Place(vi.rectTransform, new Vector2(0f, .5f), new Vector2(.5f, .5f), new Vector2(26f, 0f), new Vector2(18f, 18f));
            TextMeshProUGUI vt = HudFactory.Text(validatorGroup, "Text", "", HudTheme.FontSmall, HudTheme.TextMid);
            HudFactory.Place(vt.rectTransform, new Vector2(0f, .5f), new Vector2(0f, .5f), new Vector2(42f, 0f), new Vector2(W - 70f, 22f));

            emptyText = HudFactory.Text(card, "Empty", "Waiting for Muse's first decision", HudTheme.FontBody, HudTheme.TextLow, false, TextAlignmentOptions.Center);
            emptyText.textWrappingMode = TextWrappingModes.Normal;
            HudFactory.Stretch(emptyText.rectTransform, 16f, 30f, 16f, 30f);

            incidentGroup = HudFactory.TopStrip(HudFactory.Rect("Incident", Root), 504f, 46f, HudTheme.Pad, HudTheme.Pad);
            TextMeshProUGUI il = HudFactory.Label(incidentGroup, "Label", "LAST INCIDENT", HudTheme.FontMicro, HudTheme.Amber);
            HudFactory.Place(il.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(200f, 18f));
            incidentText = HudFactory.Text(incidentGroup, "Text", "", HudTheme.FontSmall, HudTheme.TextHi);
            HudFactory.Place(incidentText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -20f), new Vector2(W - 28f, 22f));

            historyGroup = HudFactory.TopStrip(HudFactory.Rect("History", Root), 554f, 60f, HudTheme.Pad, HudTheme.Pad);
            TextMeshProUGUI hl = HudFactory.Label(historyGroup, "Label", "PREVIOUS", HudTheme.FontMicro);
            HudFactory.Place(hl.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(200f, 18f));
            for (int i = 0; i < history.Length; i++)
            {
                historyDots[i] = HudFactory.Image(historyGroup, "Dot " + i, HudSprites.Circle(), HudTheme.Green, false);
                HudFactory.Place(historyDots[i].rectTransform, new Vector2(0f, 1f), new Vector2(.5f, .5f), new Vector2(5f, -30f - i * 20f), new Vector2(8f, 8f));
                history[i] = HudFactory.Text(historyGroup, "Row " + i, "", HudTheme.FontSmall, HudTheme.TextMid);
                HudFactory.Place(history[i].rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -20f - i * 20f), new Vector2(W - 44f, 20f));
            }
            validatorDetail = vt;
            validatorIcon = vi;
        }

        private TextMeshProUGUI validatorDetail;
        private Image validatorIcon;

        private RectTransform Group(RectTransform parent, string name, float y, float h)
        {
            RectTransform rt = HudFactory.TopStrip(HudFactory.Rect(name, parent), y, h);
            reveal.Add(rt.gameObject.AddComponent<CanvasGroup>());
            return rt;
        }

        public void PlayReveal() { revealT = 0f; flashT = 0f; }

        public Vector3 CardWorldCenter => card.TransformPoint(card.rect.center);

        public void Tick(float dt)
        {
            revealT += dt;
            for (int i = 0; i < reveal.Count; i++)
            {
                float local = Mathf.Clamp01((revealT - i * .1f) / .35f);
                reveal[i].alpha = local;
                RectTransform rt = (RectTransform)reveal[i].transform;
                rt.anchoredPosition = new Vector2((1f - local) * 18f, rt.anchoredPosition.y);
            }
            flashT += dt;
            float f = Mathf.Clamp01(1f - flashT / 2f);
            float wave = f * (.6f + .4f * Mathf.Sin(flashT * 14f));
            HudFactory.SetColor(panelFlash, HudTheme.A(accent, wave));
            HudFactory.SetColor(cardFlash, HudTheme.A(accent, f));
            HudFactory.SetColor(headerBand, HudTheme.A(accent, .06f + f * .22f));
            float pop = 1f + Mathf.Clamp01(1f - flashT / .35f) * .06f;
            title.rectTransform.localScale = new Vector3(pop, pop, 1f);
        }

        public void Refresh(HudModel m)
        {
            StatusVM s = m.Status;
            HudFactory.Set(modelText, string.IsNullOrEmpty(s.Model) || s.Model == "-"
                ? (string.IsNullOrEmpty(s.HealthModel) ? "—" : s.HealthModel) : s.Model);
            HudFactory.Set(latencyText, s.LatencyMs > 0 ? FormatMs(s.LatencyMs) : "—");
            Color lc = s.Link == "CALLING" ? HudTheme.Cyan : s.LinkOk ? HudTheme.Green : (s.Link == "—" ? HudTheme.TextLow : HudTheme.Red);
            HudFactory.Set(linkText, s.Link);
            HudFactory.SetColor(linkText, lc);
            HudFactory.SetColor(linkDot, HudTheme.A(lc, s.Link == "CALLING" ? .5f + .5f * Mathf.Sin(Time.unscaledTime * 8f) : 1f));

            DecisionVM d = PickHero(m);
            shown = d;
            emptyText.gameObject.SetActive(d == null);
            float cardH;
            if (d == null)
            {
                foreach (CanvasGroup g in reveal) if (g.gameObject.activeSelf) g.gameObject.SetActive(false);
                cardH = 120f;
            }
            else cardH = FillDecision(d, m);
            HudFactory.TopStrip(card, 116f, cardH, 10f, 10f);

            float y = 116f + cardH + 12f;
            bool hasIncident = !string.IsNullOrEmpty(m.LastIncident);
            incidentGroup.gameObject.SetActive(hasIncident);
            if (hasIncident)
            {
                HudFactory.Set(incidentText, m.LastIncident);
                HudFactory.TopStrip(incidentGroup, y, 46f, HudTheme.Pad, HudTheme.Pad);
                y += 50f;
            }
            int shownCount = 0;
            int h = 0;
            for (int i = 0; i < history.Length; i++)
            {
                // previous decisions: all except the one on the card
                while (h < m.History.Count && m.History[h] == d) h++;
                bool on = h < m.History.Count;
                history[i].gameObject.SetActive(on);
                historyDots[i].gameObject.SetActive(on);
                if (!on) continue;
                shownCount++;
                DecisionVM hd = m.History[h++];
                string what = hd.HasPlan ? "#" + hd.OrderId + "  " + string.Join("+", hd.Pallets) + " › " + hd.Bench + " › " + hd.Truck
                                         : (hd.Action != "" ? hd.Action.ToUpperInvariant().Replace('_', ' ') : "CALL") + (hd.OrderId > 0 ? " #" + hd.OrderId : "");
                OrderVM ho = hd.HasPlan ? m.FindOrder(hd.OrderId) : null;
                string state = ho == null ? "" : ho.Category == OrderCategory.Done ? "  ·  DELIVERED" : "";
                HudFactory.Set(history[i], what + state + "  <color=#" + HudTheme.Hex(HudTheme.TextLow) + ">" + Ago(hd.Time) + "</color>");
                HudFactory.SetColor(historyDots[i], hd.Accepted ? HudTheme.Green : HudTheme.Red);
            }
            historyGroup.gameObject.SetActive(shownCount > 0);
            if (shownCount > 0)
            {
                HudFactory.TopStrip(historyGroup, y, 22f + shownCount * 20f, HudTheme.Pad, HudTheme.Pad);
                y += 26f + shownCount * 20f;
            }
            Root.sizeDelta = new Vector2(W, y + 8f);
        }

        /// <summary>
        /// Decision shown on the card: the most recent one if it has just arrived or if it
        /// has no plan (refusal, wait); otherwise the most recent one whose order is still
        /// being executed with that plan (a delivered urgent order gives way to the plan that
        /// is actually running); failing that the last decision, shown as delivered.
        /// </summary>
        private static DecisionVM PickHero(HudModel m)
        {
            DecisionVM latest = m.Decision;
            if (latest == null) return null;
            if (!latest.Accepted || !latest.HasPlan)
                if (Time.time - latest.Time < 20f) return latest;
            var seen = new HashSet<int>();
            foreach (DecisionVM d in m.History)
            {
                if (d == null || !d.Accepted || !d.HasPlan || !seen.Add(d.OrderId)) continue;
                OrderVM o = m.FindOrder(d.OrderId);
                if (o == null || o.Category == OrderCategory.Done || o.Category == OrderCategory.Pending) continue;
                // plan still the order's plan (otherwise a decision made stale by a replan)
                if (!string.IsNullOrEmpty(o.Bench) && o.Bench != d.Bench) continue;
                return d;
            }
            return latest;
        }

        private float FillDecision(DecisionVM d, HudModel m)
        {
            OrderVM order = d.HasPlan ? m.FindOrder(d.OrderId) : null;
            bool delivered = order != null && order.Category == OrderCategory.Done;
            bool paused = order != null && order.Category == OrderCategory.Suspended;
            accent = !d.Accepted ? HudTheme.Red : delivered ? HudTheme.Green : d.Urgent ? HudTheme.Orange : HudTheme.Cyan;
            string action = (d.Action ?? "").Trim().ToLowerInvariant();
            string head;
            if (d.HasPlan) head = "ORDER #" + d.OrderId;
            else if (action == "wait" || action == "no_plan") head = "HOLD";
            else if (action == "defer_order") head = "DEFER #" + d.OrderId;
            else if (action == "finish_order") head = "FINISH #" + d.OrderId;
            else if (!d.Accepted) head = "NO DECISION";
            else head = string.IsNullOrEmpty(action) ? "RESPONSE" : action.ToUpperInvariant().Replace('_', ' ');
            HudFactory.Set(title, head);
            HudFactory.SetColor(title, delivered ? HudTheme.TextMid : HudTheme.TextHi);
            HudFactory.SetColor(cardAccent, accent);

            if (!d.Accepted) verdictChip.Set("REJECTED", HudTheme.Red);
            else if (delivered) verdictChip.Set("DELIVERED", HudTheme.Green);
            else if (paused) verdictChip.Set("PAUSED", HudTheme.Amber);
            else if (d.HasPlan) verdictChip.Set(d.Urgent ? "URGENT" : "EXECUTING", d.Urgent ? HudTheme.Orange : HudTheme.Cyan);
            else if (action == "finish_order") verdictChip.Set("STRATEGY", HudTheme.Orange);
            else verdictChip.Set("ACCEPTED", HudTheme.Green);
            triggerChip.Set((d.Trigger ?? "").ToUpperInvariant().Replace('_', ' '), d.Urgent ? HudTheme.Orange : HudTheme.Violet);
            replanChip.SetActive(d.IsReplan);
            HudFactory.Set(sub, Ago(d.Time));

            bool plan = d.HasPlan;
            for (int i = 0; i < reveal.Count; i++)
            {
                bool on = reveal[i].transform != planGroup || plan;
                if (reveal[i].gameObject.activeSelf != on) reveal[i].gameObject.SetActive(on);
            }
            if (plan) FillCommands(d, order, m);

            string reason = !string.IsNullOrEmpty(d.Reason) ? d.Reason
                          : !d.Accepted ? d.RejectReason
                          : !string.IsNullOrEmpty(d.Applied) ? d.Applied : "no justification returned by the model";
            HudFactory.Set(whyText, reason);

            float planH = plan ? PlanLabelH + Keys.Length * RowH : 0f;
            HudFactory.TopStrip(whyGroup, 80f + planH + 6f, 76f);
            HudFactory.TopStrip(validatorGroup, 80f + planH + 88f, 26f);
            if (d.Accepted)
            {
                HudFactory.Set(validatorDetail, "VALIDATOR ACCEPTED" + (d.LatencyMs > 0 ? "  ·  " + FormatMs(d.LatencyMs) : ""));
                HudFactory.SetColor(validatorDetail, HudTheme.Green);
                HudFactory.SetColor(validatorIcon, HudTheme.Green);
            }
            else
            {
                HudFactory.Set(validatorDetail, "VALIDATOR REJECTED — " + d.RejectReason);
                HudFactory.SetColor(validatorDetail, Color.Lerp(HudTheme.Red, Color.white, .25f));
                HudFactory.SetColor(validatorIcon, HudTheme.Red);
            }
            return 80f + planH + 122f;
        }

        /// <summary>
        /// The three instructions of the accepted plan, each with its progress measured on the real state:
        /// boxes staged/loaded for the order and order actually served by R1 / R2.
        /// </summary>
        private void FillCommands(DecisionVM d, OrderVM o, HudModel m)
        {
            bool done = o != null && o.Category == OrderCategory.Done;
            bool paused = o != null && o.Category == OrderCategory.Suspended;
            Color iconColor = d.Urgent && !done ? HudTheme.Orange : HudTheme.Cyan;
            string cyan = "<color=#" + HudTheme.Hex(HudTheme.Cyan) + ">";

            // ---- R1: pick pallet(s) › bench
            var picks = new List<string>();
            foreach (PickVM p in d.Picks) picks.Add(p.Type + "×" + p.Count + (d.Pallets.Count > 1 ? " @" + p.Pallet : ""));
            bool noPick = d.Picks.Count == 0;
            HudFactory.Set(values[0], noPick ? "NO PICK" : "PREPARE  " + string.Join("+", d.Pallets));
            HudFactory.Set(metas[0], (noPick ? "already staged" : string.Join("  ", picks)) + "  " + cyan + "› " + d.Bench + "</color>");
            bool r1Here = m.R1.MissionActive && m.R1.OrderId == d.OrderId;
            bool picked = o != null && o.Staged + o.Loaded >= o.Total;
            SetState(0, done || picked || noPick ? Step.Done : r1Here ? Step.Active : paused ? Step.Paused : Step.Queued,
                     r1Here ? m.R1.Activity : null);

            // ---- R2: load bench › truck
            HudFactory.Set(values[1], "LOAD  " + (d.Truck != "" ? d.Truck : "—"));
            HudFactory.Set(metas[1], "from " + d.Bench + (o != null ? "  ·  " + o.Loaded + "/" + o.Total + " loaded" : ""));
            bool r2Here = m.R2.MissionActive && m.R2.OrderId == d.OrderId;
            bool loaded = o != null && o.Total > 0 && o.Loaded >= o.Total;
            SetState(1, done || loaded ? Step.Done : r2Here ? Step.Active : paused ? Step.Paused : Step.Queued,
                     r2Here ? m.R2.Activity : null);

            // ---- truck: shipping
            TruckVM truck = null;
            foreach (TruckVM t in m.Trucks) if (t.Id == d.Truck) truck = t;
            HudFactory.Set(keys[2], "TRUCK");
            HudFactory.Set(values[2], "SHIP  " + (d.Truck != "" ? d.Truck : "—"));
            string door = truck != null && !string.IsNullOrEmpty(truck.Door) ? truck.Door.ToUpperInvariant() : "";
            HudFactory.Set(metas[2], done ? "order shipped" : door != "" ? "door " + door.ToLowerInvariant() : "");
            bool open = door == "OPENING" || door == "READY" || door == "LOADING";
            SetState(2, done ? Step.Done : paused ? Step.Paused : open ? Step.Active : Step.Queued, done ? "SHIPPED" : open ? "OPEN" : null);

            for (int i = 0; i < icons.Length; i++) HudFactory.SetColor(icons[i], done ? HudTheme.TextLow : iconColor);
        }

        private enum Step { Queued, Active, Paused, Done }

        private void SetState(int i, Step s, string activeLabel)
        {
            switch (s)
            {
                case Step.Done:
                    states[i].Set(activeLabel ?? "DONE", HudTheme.Green);
                    HudFactory.SetColor(values[i], HudTheme.TextMid);
                    break;
                case Step.Active:
                    states[i].Set(string.IsNullOrEmpty(activeLabel) ? "ACTIVE" : activeLabel, HudTheme.Cyan);
                    HudFactory.SetColor(values[i], HudTheme.TextHi);
                    break;
                case Step.Paused:
                    states[i].Set("PAUSED", HudTheme.Amber);
                    HudFactory.SetColor(values[i], HudTheme.TextMid);
                    break;
                default:
                    states[i].Set("QUEUED", HudTheme.TextLow);
                    HudFactory.SetColor(values[i], HudTheme.TextHi);
                    break;
            }
        }

        public static string FormatMs(int ms)
        {
            return ms >= 1000 ? (ms / 1000f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s" : ms + " ms";
        }

        public static string Ago(float t)
        {
            float s = Mathf.Max(0f, Time.time - t);
            if (s < 60f) return Mathf.FloorToInt(s) + " s ago";
            return Mathf.FloorToInt(s / 60f) + " min ago";
        }
    }
}
