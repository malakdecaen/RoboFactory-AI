using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Panneau gauche ORDERS (V2) : cartes lisibles à distance (identifiant 24 px, composition
    /// 18 px, progression réelle chargé/préparé), quatre filtres, détail au clic, saisie
    /// d'une commande ou d'une urgence manuelle (mêmes API que l'ancien HUD).
    /// </summary>
    public sealed class OrdersPanelView
    {
        public RectTransform Root { get; private set; }
        public Func<string, int> SubmitOrder;
        public Action<string> SubmitUrgent;
        public Action<int> OrderFocused;
        public float MaxHeight = 680f;

        private const float W = HudTheme.OrdersWidth;
        private const float HeaderH = 48f, TabsH = 40f, FooterH = 58f, RowH = 86f, DetailH = 84f, RowGap = 8f;

        private TextMeshProUGUI countText, hintText, collapseText, emptyText;
        private RectTransform body, viewport, content, footer;
        private ScrollRect scroll;
        private bool collapsed;
        private int filter;                       // 0 ALL (non livrées), 1 ACTIVE (urgent compris), 2 PAUSED, 3 DONE
        private readonly List<Button> tabs = new List<Button>();
        private readonly List<TextMeshProUGUI> tabLabels = new List<TextMeshProUGUI>();
        private readonly List<Row> rows = new List<Row>();
        private readonly HashSet<int> expanded = new HashSet<int>();
        private readonly List<OrderVM> visible = new List<OrderVM>();
        private TMP_InputField input;
        private float hintUntil;

        private static readonly string[] TabNames = { "ALL", "ACTIVE", "PAUSED", "DONE" };

        private sealed class Row
        {
            public RectTransform Rt;
            public Image Bg, Accent, Flash;
            public TextMeshProUGUI Id, Status, Composition, Route, Count, Detail;
            public Chip Badge;
            public DualBar Bar;
            public int OrderId = -1;
            public string LastStatus = "";
            public float Y, TargetY, FlashT = 10f;
            public CanvasGroup Group;
        }

        public void Build(RectTransform parent)
        {
            Root = HudFactory.GlassPanel(parent, "Orders Panel");
            HudFactory.Place(Root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(HudTheme.Margin, -HudTheme.SideTop), new Vector2(W, 300f));

            HudFactory.Header(Root, HudIcon.Order, "ORDERS");
            Transform header = Root.Find("Header");
            countText = HudFactory.Text(header, "Count", "", HudTheme.FontSmall, HudTheme.TextMid, true, TextAlignmentOptions.Right, 1f);
            HudFactory.Place(countText.rectTransform, new Vector2(1f, .5f), new Vector2(1f, .5f), new Vector2(-48f, 0f), new Vector2(120f, 22f));
            Button collapse = HudFactory.Button(header, "Collapse", "–", HudTheme.Cyan, ToggleCollapsed, 28f, 26f, 18f);
            HudFactory.Place((RectTransform)collapse.transform, new Vector2(1f, .5f), new Vector2(1f, .5f), new Vector2(-12f, 0f), new Vector2(28f, 26f));
            collapseText = collapse.GetComponentInChildren<TextMeshProUGUI>();

            body = HudFactory.Stretch(HudFactory.Rect("Body", Root), 0f, HeaderH, 0f, 0f);

            RectTransform tabRow = HudFactory.TopStrip(HudFactory.Rect("Tabs", body), 8f, 28f, HudTheme.Pad, HudTheme.Pad);
            float tabW = (W - HudTheme.Pad * 2f - 3f * 4f) / 4f;
            for (int i = 0; i < TabNames.Length; i++)
            {
                int index = i;
                Button b = HudFactory.Button(tabRow, "Tab " + TabNames[i], TabNames[i], HudTheme.Cyan, () => SetFilter(index), tabW, 28f, HudTheme.FontMicro);
                HudFactory.Place((RectTransform)b.transform, new Vector2(0f, .5f), new Vector2(0f, .5f), new Vector2(i * (tabW + 4f), 0f), new Vector2(tabW, 28f));
                tabs.Add(b);
                TextMeshProUGUI tl = b.GetComponentInChildren<TextMeshProUGUI>();
                tl.characterSpacing = 0f;
                tl.overflowMode = TextOverflowModes.Overflow;
                tl.rectTransform.offsetMin = new Vector2(1f, 0f);
                tl.rectTransform.offsetMax = new Vector2(-1f, 0f);
                tabLabels.Add(tl);
            }

            viewport = HudFactory.Rect("Viewport", body);
            viewport.gameObject.AddComponent<RectMask2D>();
            Image vpHit = viewport.gameObject.AddComponent<Image>();
            vpHit.color = new Color(0f, 0f, 0f, 0f);
            content = HudFactory.Rect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
            emptyText = HudFactory.Text(viewport, "Empty", "", HudTheme.FontBody, HudTheme.TextLow, false, TextAlignmentOptions.Center);
            HudFactory.Stretch(emptyText.rectTransform);

            footer = HudFactory.Rect("Footer", body);
            footer.anchorMin = new Vector2(0f, 0f);
            footer.anchorMax = new Vector2(1f, 0f);
            footer.pivot = new Vector2(.5f, 0f);
            footer.offsetMin = new Vector2(HudTheme.Pad, 12f);
            footer.offsetMax = new Vector2(-HudTheme.Pad, 12f + 36f);
            input = BuildInput(footer);
            HudFactory.Place((RectTransform)input.transform, new Vector2(0f, .5f), new Vector2(0f, .5f), Vector2.zero, new Vector2(140f, 36f));
            Button send = HudFactory.Button(footer, "Send", "SEND", HudTheme.Cyan, OnSend, 66f, 36f, HudTheme.FontSmall);
            HudFactory.Place((RectTransform)send.transform, new Vector2(0f, .5f), new Vector2(0f, .5f), new Vector2(146f, 0f), new Vector2(66f, 36f));
            Button urgent = HudFactory.Button(footer, "Urgent", "URGENT", HudTheme.Orange, OnUrgent, 80f, 36f, HudTheme.FontSmall);
            HudFactory.Place((RectTransform)urgent.transform, new Vector2(1f, .5f), new Vector2(1f, .5f), Vector2.zero, new Vector2(80f, 36f));
            hintText = HudFactory.Text(Root, "Hint", "", HudTheme.FontMicro, HudTheme.Cyan, true, TextAlignmentOptions.Center, 1f);
            HudFactory.Place(hintText.rectTransform, new Vector2(.5f, 0f), new Vector2(.5f, 1f), new Vector2(0f, -6f), new Vector2(W, 20f));
        }

        private TMP_InputField BuildInput(Transform parent)
        {
            RectTransform rt = HudFactory.Rect("Order Input", parent);
            rt.gameObject.SetActive(false);
            Image bg = rt.gameObject.AddComponent<Image>();
            bg.sprite = HudSprites.Rounded(4);
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0f, .03f, .07f, .95f);
            Image stroke = HudFactory.Image(rt, "Stroke", HudSprites.Rounded(4, 1f), HudTheme.Stroke);
            HudFactory.Stretch(stroke.rectTransform);
            RectTransform area = HudFactory.Stretch(HudFactory.Rect("Text Area", rt), 10f, 2f, 8f, 2f);
            area.gameObject.AddComponent<RectMask2D>();
            TextMeshProUGUI placeholder = HudFactory.Text(area, "Placeholder", "A x2, B x1", HudTheme.FontBody, HudTheme.TextLow);
            placeholder.fontStyle = FontStyles.Italic;
            placeholder.alignment = TextAlignmentOptions.MidlineLeft;
            HudFactory.Stretch(placeholder.rectTransform);
            TextMeshProUGUI text = HudFactory.Text(area, "Text", "", HudTheme.FontBody, HudTheme.TextHi);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.overflowMode = TextOverflowModes.Overflow;
            HudFactory.Stretch(text.rectTransform);
            var field = rt.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.fontAsset = HudFonts.Regular;
            field.pointSize = HudTheme.FontBody;
            field.caretColor = HudTheme.Cyan;
            field.customCaretColor = true;
            field.selectionColor = HudTheme.A(HudTheme.Cyan, .3f);
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.targetGraphic = bg;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            field.text = "A x2, B x1";
            field.onSubmit.AddListener(_ => OnSend());
            rt.gameObject.SetActive(true);
            return field;
        }

        public bool InputFocused => input != null && input.isFocused;

        private void OnSend()
        {
            string text = input != null ? input.text : "";
            if (string.IsNullOrWhiteSpace(text) || SubmitOrder == null) return;
            int id = SubmitOrder(text);
            if (id > 0) SetHint("ORDER #" + id + " SENT TO MUSE", HudTheme.Cyan);
            else SetHint("UNREADABLE — FORMAT: A x2, B x1", HudTheme.Red);
        }

        private void OnUrgent()
        {
            string text = input != null ? input.text : "";
            if (string.IsNullOrWhiteSpace(text) || SubmitUrgent == null) return;
            SubmitUrgent(text);
            SetHint("URGENT REQUEST SENT TO MUSE", HudTheme.Orange);
        }

        private void SetHint(string text, Color c)
        {
            HudFactory.Set(hintText, text);
            HudFactory.SetColor(hintText, c);
            hintUntil = Time.unscaledTime + 4f;
        }

        private void ToggleCollapsed()
        {
            collapsed = !collapsed;
            body.gameObject.SetActive(!collapsed);
            HudFactory.Set(collapseText, collapsed ? "+" : "–");
        }

        private void SetFilter(int f)
        {
            filter = f;
            scroll.verticalNormalizedPosition = 1f;
        }

        private static bool Matches(OrderVM o, int f)
        {
            switch (f)
            {
                case 1: return o.Category == OrderCategory.Active || o.Category == OrderCategory.Urgent;
                case 2: return o.Category == OrderCategory.Suspended;
                case 3: return o.Category == OrderCategory.Done;
                // ALL = toutes les commandes encore à traiter ; les livrées sont dans DONE
                default: return o.Category != OrderCategory.Done;
            }
        }

        // ------------------------------------------------------------------ rafraîchissement

        public void Refresh(HudModel m)
        {
            int active = 0;
            int[] counts = new int[4];
            foreach (OrderVM o in m.Orders)
            {
                for (int f = 0; f < 4; f++) if (Matches(o, f)) counts[f]++;
                if (o.Category == OrderCategory.Active || o.Category == OrderCategory.Urgent) active++;
            }
            HudFactory.Set(countText, active > 0 ? active + " ACTIVE" : m.Orders.Count + " TOTAL");
            for (int i = 0; i < tabLabels.Count; i++)
            {
                bool on = filter == i;
                HudFactory.Set(tabLabels[i], i == 0 || counts[i] > 0 ? TabNames[i] + " " + counts[i] : TabNames[i]);
                HudFactory.SetColor((Image)tabs[i].targetGraphic, HudTheme.A(HudTheme.Cyan, on ? .32f : .06f));
                HudFactory.SetColor(tabLabels[i], on ? HudTheme.TextHi : counts[i] > 0 ? HudTheme.TextMid : HudTheme.TextLow);
            }

            visible.Clear();
            var sorted = new List<OrderVM>(m.Orders);
            sorted.Sort((a, b) =>
            {
                int c = ((int)a.Category).CompareTo((int)b.Category);
                if (c != 0) return c;
                return a.Category == OrderCategory.Done ? b.Id.CompareTo(a.Id) : a.Id.CompareTo(b.Id);
            });
            foreach (OrderVM o in sorted)
            {
                if (!Matches(o, filter)) continue;
                visible.Add(o);
            }

            while (rows.Count < visible.Count) rows.Add(BuildRow());
            float y = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                bool on = i < visible.Count;
                if (r.Rt.gameObject.activeSelf != on) r.Rt.gameObject.SetActive(on);
                if (!on) continue;
                OrderVM o = visible[i];
                if (r.OrderId != o.Id) { r.OrderId = o.Id; r.Y = -y - 12f; r.Group.alpha = 0f; r.LastStatus = o.Status; r.FlashT = 0f; }
                if (r.LastStatus != o.Status) { r.LastStatus = o.Status; r.FlashT = 0f; }   // surbrillance au changement d'état
                bool open = expanded.Contains(o.Id);
                float h = RowH + (open ? DetailH : 0f);
                r.TargetY = -y;
                r.Rt.sizeDelta = new Vector2(r.Rt.sizeDelta.x, h);
                FillRow(r, o, open, m.ActiveOrderId == o.Id);
                y += h + RowGap;
            }
            float listH = visible.Count == 0 ? 60f : Mathf.Max(0f, y - RowGap);
            content.sizeDelta = new Vector2(0f, Mathf.Max(0f, y - RowGap));
            emptyText.gameObject.SetActive(visible.Count == 0);
            if (visible.Count == 0)
                HudFactory.Set(emptyText, m.Orders.Count == 0 ? "No order yet"
                    : filter == 0 ? "All orders delivered  ·  see DONE" : "Nothing here");

            float maxList = Mathf.Max(RowH, MaxHeight - HeaderH - TabsH - FooterH - 12f);
            float shownList = Mathf.Min(listH, maxList);
            viewport.anchorMin = new Vector2(0f, 1f);
            viewport.anchorMax = new Vector2(1f, 1f);
            viewport.pivot = new Vector2(.5f, 1f);
            viewport.offsetMin = new Vector2(HudTheme.Pad, -(TabsH + 6f + shownList));
            viewport.offsetMax = new Vector2(-HudTheme.Pad, -(TabsH + 6f));
            float total = collapsed ? HeaderH : HeaderH + TabsH + 6f + shownList + FooterH + 6f;
            Root.sizeDelta = new Vector2(W, total);

            if (hintUntil > 0f && Time.unscaledTime > hintUntil) { hintUntil = 0f; HudFactory.Set(hintText, ""); }
        }

        public void Tick(float dt)
        {
            float k = 1f - Mathf.Exp(-12f * dt);
            foreach (Row r in rows)
            {
                if (!r.Rt.gameObject.activeSelf) continue;
                r.Y = Mathf.Lerp(r.Y, r.TargetY, k);
                r.Rt.anchoredPosition = new Vector2(0f, r.Y);
                r.Group.alpha = Mathf.MoveTowards(r.Group.alpha, 1f, dt * 4f);
                r.FlashT += dt;
                float f = Mathf.Clamp01(1f - r.FlashT / 1.2f);
                HudFactory.SetColor(r.Flash, HudTheme.A(r.Accent.color, f * .9f));
            }
        }

        private Row BuildRow()
        {
            var r = new Row();
            r.Rt = HudFactory.Rect("Order Card", content);
            r.Rt.anchorMin = new Vector2(0f, 1f);
            r.Rt.anchorMax = new Vector2(1f, 1f);
            r.Rt.pivot = new Vector2(.5f, 1f);
            r.Rt.sizeDelta = new Vector2(0f, RowH);
            r.Group = r.Rt.gameObject.AddComponent<CanvasGroup>();
            r.Bg = r.Rt.gameObject.AddComponent<Image>();
            r.Bg.sprite = HudSprites.ChamferFill(8);
            r.Bg.type = Image.Type.Sliced;
            r.Bg.color = HudTheme.GlassRow;
            var button = r.Rt.gameObject.AddComponent<Button>();
            button.targetGraphic = r.Bg;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock cb = button.colors;
            cb.highlightedColor = new Color(1.3f, 1.3f, 1.3f, 1f);
            cb.pressedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
            cb.colorMultiplier = 2f;
            button.colors = cb;
            Row captured = r;
            button.onClick.AddListener(() => ToggleRow(captured));

            r.Flash = HudFactory.Image(r.Rt, "Flash", HudSprites.ChamferStroke(8, 1.6f), Color.clear);
            HudFactory.Stretch(r.Flash.rectTransform);
            r.Accent = HudFactory.Image(r.Rt, "Accent", null, HudTheme.Cyan);
            r.Accent.rectTransform.anchorMin = new Vector2(0f, 1f);
            r.Accent.rectTransform.anchorMax = new Vector2(0f, 1f);
            r.Accent.rectTransform.pivot = new Vector2(0f, 1f);
            r.Accent.rectTransform.anchoredPosition = new Vector2(0f, -10f);
            r.Accent.rectTransform.sizeDelta = new Vector2(3f, RowH - 20f);

            r.Id = HudFactory.Text(r.Rt, "Id", "", HudTheme.FontKey, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(r.Id.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, -6f), new Vector2(64f, 30f));
            r.Badge = HudFactory.Chip(r.Rt, "Badge", "", HudTheme.Cyan, 108f, 22f);
            HudFactory.Place(r.Badge.Root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(72f, -10f), new Vector2(108f, 22f));
            r.Badge.Label.characterSpacing = .5f;
            r.Status = HudFactory.Text(r.Rt, "Status", "", HudTheme.FontSmall, HudTheme.TextMid, true, TextAlignmentOptions.Right, 1f);
            HudFactory.Place(r.Status.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -11f), new Vector2(112f, 20f));
            r.Composition = HudFactory.Text(r.Rt, "Composition", "", HudTheme.FontBody, HudTheme.TextHi);
            HudFactory.Place(r.Composition.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, -38f), new Vector2(130f, 24f));
            r.Route = HudFactory.Text(r.Rt, "Route", "", HudTheme.FontBody, HudTheme.TextMid, true, TextAlignmentOptions.Right);
            HudFactory.Place(r.Route.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -38f), new Vector2(140f, 24f));
            r.Bar = HudFactory.DualBar(r.Rt, "Progress", HudTheme.Green, HudTheme.A(HudTheme.Cyan, .55f), 4f);
            r.Bar.Root.anchorMin = new Vector2(0f, 1f);
            r.Bar.Root.anchorMax = new Vector2(1f, 1f);
            r.Bar.Root.pivot = new Vector2(.5f, 1f);
            r.Bar.Root.offsetMin = new Vector2(14f, -74f);
            r.Bar.Root.offsetMax = new Vector2(-62f, -70f);
            r.Count = HudFactory.Text(r.Rt, "Count", "", HudTheme.FontSmall, HudTheme.TextMid, true, TextAlignmentOptions.Right);
            HudFactory.Place(r.Count.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -63f), new Vector2(48f, 20f));
            r.Detail = HudFactory.Text(r.Rt, "Detail", "", HudTheme.FontSmall, HudTheme.TextMid);
            r.Detail.alignment = TextAlignmentOptions.TopLeft;
            HudFactory.Place(r.Detail.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, -RowH + 2f), new Vector2(W - 56f, DetailH - 6f));
            return r;
        }

        private void ToggleRow(Row r)
        {
            if (!expanded.Remove(r.OrderId)) expanded.Add(r.OrderId);
            OrderFocused?.Invoke(expanded.Contains(r.OrderId) ? r.OrderId : -1);
        }

        private static void FillRow(Row r, OrderVM o, bool open, bool lastActive)
        {
            Color c = o.FinishingForUrgent ? HudTheme.Orange : CategoryColor(o.Category);
            HudFactory.Set(r.Id, "#" + o.Id);
            r.Badge.Set(CategoryLabel(o), c);
            HudFactory.Set(r.Status, StatusLabel(o.Status));
            HudFactory.SetColor(r.Status, o.Category == OrderCategory.Done ? HudTheme.Green : HudTheme.TextMid);
            HudFactory.Set(r.Composition, o.Composition);
            HudFactory.Set(r.Route, o.Bench == "" && o.Truck == "" ? "—" : (o.Bench != "" ? o.Bench : "—") + " › " + (o.Truck != "" ? o.Truck : "—"));
            HudFactory.Set(r.Count, o.Loaded + "/" + o.Total);
            HudFactory.SetColor(r.Accent, c);
            HudFactory.SetColor(r.Bg, open || lastActive ? HudTheme.GlassRowHi : HudTheme.GlassRow);
            r.Bar.Set(o.LoadedFrac, o.PreparedFrac, true);
            if (r.Detail.gameObject.activeSelf != open) r.Detail.gameObject.SetActive(open);
            if (open)
            {
                string Line(string t, int need, int staged, int loaded) =>
                    need <= 0 ? "" : "<b><color=#" + HudTheme.Hex(HudTheme.TextHi) + ">" + t + "</color></b><pos=26>need " + need +
                                     "<pos=110>bench " + staged + "<pos=196>truck " + loaded + "\n";
                HudFactory.Set(r.Detail, Line("A", o.A, o.StagedA, o.LoadedA) + Line("B", o.B, o.StagedB, o.LoadedB) +
                                         Line("C", o.C, o.StagedC, o.LoadedC) + "priority " + o.Priority);
            }
        }

        public static Color CategoryColor(OrderCategory c)
        {
            switch (c)
            {
                case OrderCategory.Urgent: return HudTheme.Orange;
                case OrderCategory.Active: return HudTheme.Cyan;
                case OrderCategory.Suspended: return HudTheme.Amber;
                case OrderCategory.Done: return HudTheme.Green;
                default: return HudTheme.Violet;
            }
        }

        private static string CategoryLabel(OrderVM o)
        {
            switch (o.Category)
            {
                case OrderCategory.Urgent: return "URGENT";
                case OrderCategory.Active: return o.FinishingForUrgent ? "FINISHING" : "ACTIVE";
                case OrderCategory.Suspended: return o.Status == "suspending" ? "SUSPENDING" : "PAUSED";
                case OrderCategory.Done: return "DONE";
                default: return "PENDING";
            }
        }

        public static string StatusLabel(string status)
        {
            switch ((status ?? "").ToLowerInvariant())
            {
                case "pending": return "QUEUED";
                case "planned": return "PLANNED";
                case "queued": return "RESERVED";
                case "picking": return "PREPARING";
                case "staging": return "STAGING";
                case "loading": return "LOADING";
                case "loaded": return "DELIVERED";
                case "waiting": return "WAITING";
                case "blocked": return "BLOCKED";
                case "suspending": return "SUSPENDING";
                case "paused": return "PAUSED";
                default: return (status ?? "").ToUpperInvariant();
            }
        }
    }
}
