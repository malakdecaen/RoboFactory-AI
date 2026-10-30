using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Bandeau temporaire PRIORITY OVERRIDE / RESUMING OPERATIONS, puis pastille persistante
    /// de progression de l'urgence. Piloté uniquement par les transitions réelles de
    /// MuseOrchestrator.Stage.
    /// </summary>
    public sealed class BannerView
    {
        public RectTransform Root { get; private set; }

        private RectTransform banner, pill;
        private CanvasGroup bannerGroup, pillGroup;
        private Image bannerFill, bannerStroke, hazardL, hazardR, sweep;
        private TextMeshProUGUI bannerTitle, bannerSub, pillText;
        private DualBar pillBar;

        // ---- FINISH TO FREE : carte persistante tant que l'urgente attend sa capacité
        private RectTransform capCard;
        private CanvasGroup capGroup;
        private Image capAccent, capStroke;
        private TextMeshProUGUI capLabel, capReason, capStrategy, capFreeing, capUrgent;
        private float bannerT = -1f, bannerDuration;
        private Color bannerColor = HudTheme.Orange;
        private float time;

        public void Build(RectTransform parent)
        {
            Root = HudFactory.Rect("Banners", parent);
            HudFactory.Place(Root, new Vector2(.5f, 1f), new Vector2(.5f, 1f), new Vector2(0f, -312f), new Vector2(700f, 76f));

            banner = HudFactory.Rect("Banner", Root);
            HudFactory.Place(banner, new Vector2(.5f, 1f), new Vector2(.5f, 1f), Vector2.zero, new Vector2(700f, 76f));
            bannerGroup = banner.gameObject.AddComponent<CanvasGroup>();
            bannerGroup.alpha = 0f;
            bannerGroup.blocksRaycasts = false;
            Image bshadow = HudFactory.Image(banner, "Shadow", HudSprites.SoftShadow(), HudTheme.Shadow);
            HudFactory.Stretch(bshadow.rectTransform, -22f, -16f, -22f, -28f);
            Image bbase = HudFactory.Image(banner, "Base", HudSprites.ChamferFill(14), HudTheme.GlassStrong);
            HudFactory.Stretch(bbase.rectTransform);
            bannerFill = HudFactory.Image(banner, "Fill", HudSprites.ChamferFill(14), HudTheme.A(HudTheme.Orange, .22f));
            HudFactory.Stretch(bannerFill.rectTransform);
            bannerStroke = HudFactory.Image(banner, "Stroke", HudSprites.ChamferStroke(14, 1.5f), HudTheme.Orange);
            HudFactory.Stretch(bannerStroke.rectTransform);
            hazardL = Hazard(banner, "Hazard L", true);
            hazardR = Hazard(banner, "Hazard R", false);
            sweep = HudFactory.Image(banner, "Sweep", HudSprites.Glow(), HudTheme.A(Color.white, .0f), false);
            HudFactory.Place(sweep.rectTransform, new Vector2(0f, .5f), new Vector2(.5f, .5f), Vector2.zero, new Vector2(120f, 70f));
            bannerTitle = HudFactory.Text(banner, "Title", "", 32f, HudTheme.TextHi, true, TextAlignmentOptions.Center, 8f);
            HudFactory.Place(bannerTitle.rectTransform, new Vector2(.5f, 1f), new Vector2(.5f, 1f), new Vector2(0f, -6f), new Vector2(560f, 40f));
            bannerSub = HudFactory.Text(banner, "Sub", "", HudTheme.FontSmall, HudTheme.TextHi, true, TextAlignmentOptions.Center, 1.5f);
            HudFactory.Place(bannerSub.rectTransform, new Vector2(.5f, 0f), new Vector2(.5f, 0f), new Vector2(0f, 8f), new Vector2(560f, 22f));

            pill = HudFactory.Rect("Urgent Pill", Root);
            HudFactory.Place(pill, new Vector2(.5f, 1f), new Vector2(.5f, 1f), new Vector2(0f, -4f), new Vector2(480f, 38f));
            pillGroup = pill.gameObject.AddComponent<CanvasGroup>();
            pillGroup.alpha = 0f;
            pillGroup.blocksRaycasts = false;
            Image pf = HudFactory.Image(pill, "Fill", HudSprites.Rounded(6), HudTheme.GlassStrong);
            HudFactory.Stretch(pf.rectTransform);
            Image ps = HudFactory.Image(pill, "Stroke", HudSprites.Rounded(6, 1.2f), HudTheme.A(HudTheme.Orange, .8f));
            HudFactory.Stretch(ps.rectTransform);
            pillText = HudFactory.Text(pill, "Text", "", HudTheme.FontSmall, HudTheme.TextHi, true, TextAlignmentOptions.Center, 2f);
            HudFactory.Stretch(pillText.rectTransform, 10f, 0f, 10f, 4f);
            pillBar = HudFactory.DualBar(pill, "Progress", HudTheme.Orange, HudTheme.A(HudTheme.Amber, .5f), 2f);
            pillBar.Root.anchorMin = new Vector2(0f, 0f);
            pillBar.Root.anchorMax = new Vector2(1f, 0f);
            pillBar.Root.pivot = new Vector2(.5f, 0f);
            pillBar.Root.offsetMin = new Vector2(14f, 3f);
            pillBar.Root.offsetMax = new Vector2(-14f, 5f);

            BuildCapacityCard();
        }

        private void BuildCapacityCard()
        {
            capCard = HudFactory.Rect("Capacity Card", Root);
            HudFactory.Place(capCard, new Vector2(.5f, 1f), new Vector2(.5f, 1f), new Vector2(0f, -88f), new Vector2(620f, 150f));
            capGroup = capCard.gameObject.AddComponent<CanvasGroup>();
            capGroup.alpha = 0f;
            capGroup.blocksRaycasts = false;
            Image shadow = HudFactory.Image(capCard, "Shadow", HudSprites.SoftShadow(), HudTheme.Shadow);
            HudFactory.Stretch(shadow.rectTransform, -22f, -16f, -22f, -28f);
            Image fill = HudFactory.Image(capCard, "Fill", HudSprites.ChamferFill(12), HudTheme.GlassStrong);
            HudFactory.Stretch(fill.rectTransform);
            capStroke = HudFactory.Image(capCard, "Stroke", HudSprites.ChamferStroke(12, 1.4f), HudTheme.A(HudTheme.Orange, .8f));
            HudFactory.Stretch(capStroke.rectTransform);
            capAccent = HudFactory.Image(capCard, "Accent", null, HudTheme.Orange);
            HudFactory.Place(capAccent.rectTransform, new Vector2(0f, .5f), new Vector2(0f, .5f), Vector2.zero, new Vector2(4f, 118f));
            capLabel = HudFactory.Text(capCard, "Label", "FINISH TO FREE  ·  MUSE STRATEGY", HudTheme.FontMicro, HudTheme.Amber, true, TextAlignmentOptions.Left, 2f);
            HudFactory.Place(capLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -8f), new Vector2(580f, 18f));
            capReason = HudFactory.Text(capCard, "Reason", "", HudTheme.FontValue, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(capReason.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -28f), new Vector2(580f, 26f));
            capStrategy = HudFactory.Text(capCard, "Strategy", "", HudTheme.FontKey, HudTheme.Orange, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(capStrategy.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -56f), new Vector2(580f, 30f));
            capFreeing = HudFactory.Text(capCard, "Freeing", "", HudTheme.FontBody, HudTheme.TextHi, false, TextAlignmentOptions.Left);
            HudFactory.Place(capFreeing.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -90f), new Vector2(580f, 24f));
            capUrgent = HudFactory.Text(capCard, "Urgent", "", HudTheme.FontBody, HudTheme.Amber, true, TextAlignmentOptions.Left);
            HudFactory.Place(capUrgent.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -116f), new Vector2(580f, 24f));
            foreach (TextMeshProUGUI t in new[] { capReason, capStrategy, capFreeing, capUrgent })
            {
                t.textWrappingMode = TextWrappingModes.NoWrap;
                t.overflowMode = TextOverflowModes.Ellipsis;
            }
        }

        /// <summary>Entrée en attente de capacité : bandeau PRIORITY OVERRIDE explicite.</summary>
        public void ShowCapacityOverride(int urgentId)
        {
            Show("PRIORITY OVERRIDE", "ALL LOADING BAYS OCCUPIED  ·  URGENT ORDER #" + urgentId + " WAITING FOR CAPACITY",
                 HudTheme.Orange, 6.5f, true);
        }

        /// <summary>Capacité libérée puis urgente lancée.</summary>
        public void ShowResourcesAvailable(int urgentId, string bench, string truck)
        {
            Show("RESOURCES AVAILABLE", "URGENT ORDER #" + urgentId + ": EXECUTING" +
                 (bench != "" && truck != "" ? "  ·  BENCH " + bench + " / DOCK " + truck : ""), HudTheme.Green, 5f, false);
        }

        private static Image Hazard(RectTransform parent, string name, bool left)
        {
            Image img = HudFactory.Image(parent, name, HudSprites.Hazard(), HudTheme.A(HudTheme.Orange, .55f), false);
            img.type = Image.Type.Tiled;
            img.pixelsPerUnitMultiplier = 2f;
            Vector2 a = left ? new Vector2(0f, .5f) : new Vector2(1f, .5f);
            HudFactory.Place(img.rectTransform, a, a, new Vector2(left ? 16f : -16f, 0f), new Vector2(48f, 48f));
            return img;
        }

        private bool liveOverride;

        /// <summary>Id de l'urgente : celui de l'orchestrateur, sinon la commande urgente en attente de plan.</summary>
        private static int UrgentId(HudModel m)
        {
            if (m.Urgent.OrderId >= 0) return m.Urgent.OrderId;
            foreach (OrderVM o in m.Orders) if (o.Urgent && o.Category != OrderCategory.Done) return o.Id;
            return -1;
        }

        public void ShowOverride(int orderId, int paused)
        {
            liveOverride = true;
            Show("PRIORITY OVERRIDE", "URGENT ORDER #" + orderId + "  ·  " + paused + " NORMAL ORDER(S) SUSPENDED", HudTheme.Orange, 6.5f, true);
        }

        /// <summary>Décision Muse acceptée pour l'urgente : rappel bref de la nouvelle affectation.</summary>
        public void ShowUrgentPlan(DecisionVM d)
        {
            Show("PRIORITY OVERRIDE", "URGENT #" + d.OrderId + "   " + string.Join(" + ", d.Pallets) + "  ›  " + d.Bench + "  ›  " + d.Truck, HudTheme.Orange, 4.5f, true);
        }

        public void ShowResuming(int resumed)
        {
            Show("RESUMING OPERATIONS", resumed > 0 ? resumed + " PAUSED ORDER(S) BACK IN EXECUTION" : "URGENT ORDER DELIVERED", HudTheme.Cyan, 4.5f, false);
        }

        public void ShowFailure(string error)
        {
            Show("URGENT ORDER FAILED", error, HudTheme.Red, 6f, false);
        }

        private void Show(string title, string sub, Color c, float duration, bool hazard)
        {
            if (title != "PRIORITY OVERRIDE" || !sub.StartsWith("URGENT ORDER")) liveOverride = false;
            HudFactory.Set(bannerTitle, title);
            HudFactory.Set(bannerSub, sub);
            bannerColor = c;
            bannerDuration = duration;
            bannerT = 0f;
            hazardL.gameObject.SetActive(hazard);
            hazardR.gameObject.SetActive(hazard);
        }

        /// <summary>Carte FINISH TO FREE : uniquement des valeurs réelles de l'orchestrateur.</summary>
        private void TickCapacity(float dt, HudModel m)
        {
            UrgentVM u = m.Urgent;
            bool on = u.WaitingCapacity;
            capGroup.alpha = Mathf.MoveTowards(capGroup.alpha, on ? 1f : 0f, dt * 3f);
            if (!on && capGroup.alpha <= 0f) return;
            int uid = UrgentId(m);
            bool ready = u.CapacityReady;
            Color c = ready ? HudTheme.Green : HudTheme.Orange;
            float pulse = .5f + .5f * Mathf.Sin(time * 3f);
            HudFactory.SetColor(capAccent, c);
            HudFactory.SetColor(capStroke, HudTheme.A(c, .55f + .35f * pulse));
            HudFactory.Set(capReason, ready ? "Resources available" : (u.CapacityReason != "" ? u.CapacityReason : "All loading bays occupied"));
            // stratégie Muse si elle existe ; sinon on dit clairement qu'on attend une place
            string strategy = u.FinishingId >= 0 ? "Muse Strategy: Finish Order #" + u.FinishingId
                            : u.CapacityStrategy != "" ? u.CapacityStrategy
                            : "Waiting for the first free bench + empty dock";
            HudFactory.Set(capStrategy, strategy);
            HudFactory.SetColor(capStrategy, ready ? HudTheme.Green : HudTheme.Orange);
            string freeing = ready ? "Bench " + u.CapacityBench + " / Dock " + u.CapacityTruck + " free  ·  empty truck at dock"
                           : u.FreeingBench != "" || u.FreeingTruck != ""
                               ? "Freeing Bench " + (u.FreeingBench != "" ? u.FreeingBench : "—") + " / Dock " +
                                 (u.FreeingTruck != "" ? u.FreeingTruck : "—") + (u.CapacityEta > 0f ? "   ·   est. ~" + Mathf.RoundToInt(u.CapacityEta) + " s" : "")
                               : u.CapacityNote != "" ? u.CapacityNote
                               : "Normal orders keep running — the first bay they free goes to the urgent order";
            HudFactory.Set(capFreeing, freeing);
            HudFactory.Set(capUrgent, "Urgent Order #" + (uid >= 0 ? uid.ToString() : "?") +
                                      (ready ? ": resources reserved — planning" : ": Waiting for capacity"));
            HudFactory.SetColor(capUrgent, ready ? HudTheme.Green : HudTheme.Amber);
        }

        public void Tick(float dt, HudModel m)
        {
            time += dt;
            if (bannerT >= 0f)
            {
                bannerT += dt;
                float inT = Mathf.Clamp01(bannerT / .35f);
                float outT = Mathf.Clamp01((bannerT - bannerDuration) / .5f);
                float a = inT * (1f - outT);
                bannerGroup.alpha = a;
                float s = Mathf.Lerp(.85f, 1f, 1f - (1f - inT) * (1f - inT));
                banner.localScale = new Vector3(s, Mathf.Lerp(.4f, 1f, inT), 1f);
                float pulse = .5f + .5f * Mathf.Sin(time * 6f);
                HudFactory.SetColor(bannerFill, HudTheme.A(bannerColor, .2f + .1f * pulse));
                HudFactory.SetColor(bannerStroke, HudTheme.A(bannerColor, .7f + .3f * pulse));
                HudFactory.SetColor(bannerTitle, Color.Lerp(bannerColor, Color.white, .35f));
                float sx = Mathf.Repeat(bannerT * .9f, 1.4f) - .2f;
                sweep.rectTransform.anchorMin = sweep.rectTransform.anchorMax = new Vector2(sx, .5f);
                HudFactory.SetColor(sweep, HudTheme.A(Color.Lerp(bannerColor, Color.white, .5f), .22f));
                hazardL.rectTransform.localRotation = Quaternion.identity;
                if (bannerT > bannerDuration + .6f) { bannerT = -1f; bannerGroup.alpha = 0f; }
            }

            // pastille persistante tant que l'urgence est réellement en cours
            if (liveOverride && bannerT >= 0f)
            {
                // le texte suit les vrais compteurs pendant que le bandeau est affiché
                int id = UrgentId(m);
                int held = Mathf.Max(m.Urgent.Paused, m.Urgent.Suspending);
                HudFactory.Set(bannerSub, (id >= 0 ? "URGENT ORDER #" + id : "URGENT ORDER") + "  ·  " +
                                          (m.Urgent.Suspending > 0 ? m.Urgent.Suspending + " ORDER(S) SUSPENDING" : held + " ORDER(S) PAUSED"));
            }
            TickCapacity(dt, m);
            bool urgentLive = m.Urgent.Stage == "Suspending" || m.Urgent.Stage == "UrgentActive" ||
                              m.Urgent.Stage == "WaitingForCapacity";
            float pillTarget = urgentLive && bannerT < 0f ? 1f : 0f;
            pillGroup.alpha = Mathf.MoveTowards(pillGroup.alpha, pillTarget, dt * 4f);
            if (urgentLive)
            {
                UrgentVM u = m.Urgent;
                int uid = UrgentId(m);
                OrderVM uo = m.FindOrder(uid);
                string stage = "URGENT #" + (uid >= 0 ? uid.ToString() : "?") +
                               (u.WaitingCapacity ? "  ·  WAITING FOR CAPACITY" + (u.FinishingId >= 0 ? "  ·  FINISHING #" + u.FinishingId : "")
                                : u.Stage == "Suspending" && u.Suspending > 0 ? "  ·  SUSPENDING " + u.Suspending : "");
                int loaded = u.Total > 0 ? u.Loaded : uo != null ? uo.Loaded : 0;
                int total = u.Total > 0 ? u.Total : uo != null ? uo.Total : 0;
                HudFactory.Set(pillText, stage + "   ·   LOADED " + loaded + "/" + total + "   ·   " + u.Paused + " PAUSED");
                pillBar.Set(total > 0 ? loaded / (float)total : 0f, 0f, true);
            }
        }
    }
}
