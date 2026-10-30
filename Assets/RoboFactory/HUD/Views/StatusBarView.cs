using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Barre d'état inférieure (V2, 52 px) : robots, commandes, cartons livrés, sécurité,
    /// camions, liaison Meta. Valeurs 20 px lisibles à distance, aucun log.
    /// </summary>
    public sealed class StatusBarView
    {
        public RectTransform Root { get; private set; }

        private sealed class Cell
        {
            public TextMeshProUGUI Label, Value, Sub;
            public Image Icon;
        }

        private readonly Cell[] cells = new Cell[6];
        private static readonly string[] Labels = { "ROBOTS", "ACTIVE ORDERS", "CARTONS LOADED", "SAFETY", "TRUCKS", "META LINK" };
        private static readonly HudIcon[] Icons = { HudIcon.Robot, HudIcon.Order, HudIcon.Box, HudIcon.Shield, HudIcon.Truck, HudIcon.Signal };

        public void Build(RectTransform parent)
        {
            Root = HudFactory.GlassPanel(parent, "Status Bar", HudTheme.GlassStrong, HudTheme.StrokeSoft, 10);
            Root.anchorMin = new Vector2(0f, 0f);
            Root.anchorMax = new Vector2(1f, 0f);
            Root.pivot = new Vector2(.5f, 0f);
            Root.offsetMin = new Vector2(HudTheme.Margin, HudTheme.Margin);
            Root.offsetMax = new Vector2(-HudTheme.Margin, HudTheme.Margin + HudTheme.StatusBarH);

            for (int i = 0; i < cells.Length; i++)
            {
                RectTransform cell = HudFactory.Rect("Cell " + Labels[i], Root);
                cell.anchorMin = new Vector2(i / 6f, 0f);
                cell.anchorMax = new Vector2((i + 1) / 6f, 1f);
                cell.offsetMin = cell.offsetMax = Vector2.zero;
                var c = new Cell();
                c.Icon = HudFactory.Icon(cell, "Icon", Icons[i], 24f, HudTheme.Cyan);
                HudFactory.Place(c.Icon.rectTransform, new Vector2(0f, .5f), new Vector2(.5f, .5f), new Vector2(30f, 0f), new Vector2(24f, 24f));
                c.Label = HudFactory.Label(cell, "Label", Labels[i], HudTheme.FontMicro);
                HudFactory.Place(c.Label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(54f, -5f), new Vector2(240f, 18f));
                c.Value = HudFactory.Text(cell, "Value", "—", HudTheme.FontValue, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
                HudFactory.Place(c.Value.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(54f, 3f), new Vector2(170f, 26f));
                c.Sub = HudFactory.Text(cell, "Sub", "", HudTheme.FontMicro, HudTheme.TextMid);
                HudFactory.Place(c.Sub.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(150f, 6f), new Vector2(150f, 20f));
                if (i > 0)
                {
                    Image sep = HudFactory.Image(cell, "Separator", null, HudTheme.A(HudTheme.Cyan, .18f));
                    sep.rectTransform.anchorMin = new Vector2(0f, .2f);
                    sep.rectTransform.anchorMax = new Vector2(0f, .8f);
                    sep.rectTransform.sizeDelta = new Vector2(1f, 0f);
                    sep.rectTransform.anchoredPosition = Vector2.zero;
                }
                cells[i] = c;
            }
        }

        public void Refresh(HudModel m)
        {
            StatusVM s = m.Status;
            bool robotsOk = s.RobotsOnline == s.RobotsTotal && s.RobotsTotal > 0;
            Set(0, s.RobotsOnline + " / " + s.RobotsTotal, robotsOk ? "online" : "check", robotsOk ? HudTheme.Green : HudTheme.Amber);

            int paused = 0, pending = 0;
            foreach (OrderVM o in m.Orders)
            {
                if (o.Category == OrderCategory.Suspended) paused++;
                if (o.Category == OrderCategory.Pending) pending++;
            }
            string sub = s.UrgentOrders > 0 ? s.UrgentOrders + " urgent" : paused > 0 ? paused + " paused" : pending > 0 ? pending + " queued" : "";
            Set(1, s.ActiveOrders.ToString(), sub, s.UrgentOrders > 0 ? HudTheme.Orange : HudTheme.Cyan);
            Set(2, s.Delivered + " / " + s.Requested, s.Requested > 0 ? Mathf.RoundToInt(100f * s.Delivered / s.Requested) + " %" : "", HudTheme.Cyan);

            string safety; Color sc; string ssub;
            switch (s.Safety)
            {
                case SafetyLevel.Alert: safety = "ALERT"; sc = HudTheme.Red; ssub = "worker " + s.NearestWorker.ToString("0.0") + " m"; break;
                case SafetyLevel.Caution: safety = "CAUTION"; sc = HudTheme.Amber; ssub = "worker " + s.NearestWorker.ToString("0.0") + " m"; break;
                case SafetyLevel.Safe: safety = "SAFE"; sc = HudTheme.Green; ssub = float.IsPositiveInfinity(s.NearestWorker) ? "" : s.NearestWorker.ToString("0.0") + " m"; break;
                default: safety = "—"; sc = HudTheme.TextLow; ssub = ""; break;
            }
            Set(3, safety, ssub, sc);

            Set(4, s.TruckSummary, "", s.TruckSummary.Contains("LOADING") ? HudTheme.Orange : HudTheme.Cyan);
            Color lc = s.Link == "CALLING" ? HudTheme.Cyan : s.LinkOk ? HudTheme.Green : (s.Link == "—" ? HudTheme.TextLow : HudTheme.Red);
            Set(5, s.LatencyMs > 0 ? DecisionPanelView.FormatMs(s.LatencyMs) : "—", s.Link.ToLowerInvariant(), lc);
        }

        private void Set(int i, string value, string sub, Color c)
        {
            Cell cell = cells[i];
            HudFactory.Set(cell.Value, value);
            HudFactory.Set(cell.Sub, sub);
            HudFactory.SetColor(cell.Icon, c);
            HudFactory.SetColor(cell.Sub, Color.Lerp(c, HudTheme.TextMid, .35f));
            float w = cell.Value.GetPreferredValues(value).x;
            cell.Sub.rectTransform.anchoredPosition = new Vector2(54f + Mathf.Min(w, 170f) + 10f, 6f);
        }
    }
}
