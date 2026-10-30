using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Compact truck indicators: real door state (TruckBay.Door), fill level,
    /// reserved order. The truck selected by Muse lights up.
    /// </summary>
    public sealed class TruckStripView
    {
        public RectTransform Root { get; private set; }

        private sealed class Cell
        {
            public RectTransform Rt;
            public Image Bg, Stroke, Glow;
            public TextMeshProUGUI Id, Door, Order;
            public DualBar Bar;
            public string LastDoor = "";
            public float Flash;
        }

        private readonly List<Cell> cells = new List<Cell>();
        private const float W = 158f, H = 46f, Gap = 10f;
        private float time;

        public void Build(RectTransform parent)
        {
            Root = HudFactory.Rect("Truck Strip", parent);
            HudFactory.Place(Root, new Vector2(.5f, 0f), new Vector2(.5f, 0f), new Vector2(0f, HudTheme.BottomRowY + HudTheme.VoiceH + 10f), new Vector2(600f, H));
        }

        public void Tick(float dt, HudModel m)
        {
            time += dt;
            while (cells.Count < m.Trucks.Count) cells.Add(BuildCell());
            float total = m.Trucks.Count * W + Mathf.Max(0, m.Trucks.Count - 1) * Gap;
            for (int i = 0; i < cells.Count; i++)
            {
                Cell c = cells[i];
                bool on = i < m.Trucks.Count;
                if (c.Rt.gameObject.activeSelf != on) c.Rt.gameObject.SetActive(on);
                if (!on) continue;
                TruckVM t = m.Trucks[i];
                c.Rt.anchoredPosition = new Vector2(-total * .5f + i * (W + Gap) + W * .5f, 0f);
                Color dc = DoorColor(t.Door);
                if (c.LastDoor != t.Door) { c.LastDoor = t.Door; c.Flash = 1f; }
                c.Flash = Mathf.MoveTowards(c.Flash, 0f, dt * 1.5f);
                float pulse = t.Selected ? .5f + .5f * Mathf.Sin(time * 3f) : 0f;
                HudFactory.Set(c.Id, t.Id);
                HudFactory.Set(c.Door, t.Door.ToUpperInvariant());
                HudFactory.SetColor(c.Door, dc);
                HudFactory.Set(c.Order, t.ReservedOrder >= 0 ? "#" + t.ReservedOrder : t.UrgentLane ? "URGENT" : "");
                HudFactory.SetColor(c.Order, t.UrgentLane ? HudTheme.Orange : HudTheme.TextMid);
                HudFactory.SetColor(c.Stroke, HudTheme.A(t.Selected ? dc : HudTheme.Cyan, t.Selected ? .7f + .3f * pulse : .3f));
                HudFactory.SetColor(c.Bg, t.Selected ? Color.Lerp(HudTheme.GlassStrong, HudTheme.A(dc, .95f), .16f + c.Flash * .25f) : HudTheme.GlassStrong);
                c.Bar.Set(t.Capacity > 0 ? t.Loaded / (float)t.Capacity : 0f, 0f, true);
            }
            Root.sizeDelta = new Vector2(Mathf.Max(total, 10f), H);
        }

        private Cell BuildCell()
        {
            var c = new Cell();
            c.Rt = HudFactory.Rect("Truck", Root);
            HudFactory.Place(c.Rt, new Vector2(.5f, .5f), new Vector2(.5f, .5f), Vector2.zero, new Vector2(W, H));
            c.Glow = HudFactory.Image(c.Rt, "Shadow", HudSprites.SoftShadow(), HudTheme.Shadow);
            HudFactory.Stretch(c.Glow.rectTransform, -18f, -14f, -18f, -22f);
            c.Bg = HudFactory.Image(c.Rt, "Bg", HudSprites.ChamferFill(7), HudTheme.GlassStrong);
            HudFactory.Stretch(c.Bg.rectTransform);
            c.Stroke = HudFactory.Image(c.Rt, "Stroke", HudSprites.ChamferStroke(7, 1.4f), HudTheme.Stroke);
            HudFactory.Stretch(c.Stroke.rectTransform);
            Image icon = HudFactory.Icon(c.Rt, "Icon", HudIcon.Truck, 20f, HudTheme.TextMid);
            HudFactory.Place(icon.rectTransform, new Vector2(0f, 1f), new Vector2(.5f, .5f), new Vector2(20f, -16f), new Vector2(20f, 20f));
            c.Id = HudFactory.Text(c.Rt, "Id", "", HudTheme.FontValue, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(c.Id.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -3f), new Vector2(50f, 26f));
            c.Door = HudFactory.Text(c.Rt, "Door", "", HudTheme.FontMicro, HudTheme.Cyan, true, TextAlignmentOptions.Right, 1f);
            HudFactory.Place(c.Door.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-10f, -6f), new Vector2(84f, 20f));
            c.Order = HudFactory.Text(c.Rt, "Order", "", HudTheme.FontMicro, HudTheme.TextMid, false, TextAlignmentOptions.Left);
            HudFactory.Place(c.Order.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(10f, 2f), new Vector2(60f, 18f));
            c.Bar = HudFactory.DualBar(c.Rt, "Fill", HudTheme.Cyan, HudTheme.Cyan, 3f);
            HudFactory.Place(c.Bar.Root, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-10f, 9f), new Vector2(76f, 3f));
            return c;
        }

        public static Color DoorColor(string door)
        {
            switch (door)
            {
                case "Opening": case "Closing": return HudTheme.Amber;
                case "Open": case "Ready": return HudTheme.Cyan;
                case "Loading": return HudTheme.Orange;
                case "Loaded": return HudTheme.Green;
                default: return HudTheme.TextLow;
            }
        }
    }
}
