using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.Packing
{
    /// <summary>
    /// Shipping carton filled by the arm on a tray: open with its flaps up, a packing list of products, four
    /// places inside; closed (flaps folded, tape) once complete, then labelled and handed over to the transport robot.
    /// </summary>
    public sealed class PackingCarton : MonoBehaviour
    {
        public enum CartonState { Open, Closing, Closed, Labelled, Taken }

        public int Number;
        public CartonState State = CartonState.Open;
        public readonly List<ItemKind> PackingList = new List<ItemKind>();
        public readonly List<PackingItem> Contents = new List<PackingItem>();
        public Vector3 Size = new Vector3(.34f, .26f, .44f);
        public int TraySlot = -1;
        public string Destination = "";
        public string LabelModel = "";

        private readonly Transform[] flaps = new Transform[4];
        private readonly Quaternion[] flapOpen = new Quaternion[4], flapClosed = new Quaternion[4];
        private Transform tape;

        public int Capacity => PackingList.Count;
        public bool Full => Contents.Count >= PackingList.Count;

        /// <summary>Kinds still missing in the carton.</summary>
        public List<ItemKind> Missing()
        {
            var need = new List<ItemKind>(PackingList);
            foreach (PackingItem i in Contents)
                if (i != null && i.ScanKind.Length > 0 && PackingItem.TryParseKind(i.ScanKind, out ItemKind k)) need.Remove(k);
            return need;
        }

        public bool Needs(ItemKind kind) => State == CartonState.Open && Missing().Contains(kind);

        public static PackingCarton Create(int number, Vector3 floorCentre, Quaternion rotation, Vector3 size, IList<ItemKind> list, Transform parent)
        {
            var go = new GameObject("carton_" + number.ToString("00"));
            if (parent != null) go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(floorCentre, rotation);
            var c = go.AddComponent<PackingCarton>();
            c.Number = number;
            c.Size = size;
            c.PackingList.AddRange(list);
            c.Build();
            return c;
        }

        private void Build()
        {
            Color kraft = new Color(.76f, .6f, .38f), inside = new Color(.62f, .47f, .28f);
            float w = Size.x, h = Size.y, d = Size.z, t = .008f;
            Panel("bottom", new Vector3(0f, t / 2f, 0f), new Vector3(w, t, d), inside);
            Panel("wall_x+", new Vector3(w / 2f - t / 2f, h / 2f, 0f), new Vector3(t, h, d), kraft);
            Panel("wall_x-", new Vector3(-w / 2f + t / 2f, h / 2f, 0f), new Vector3(t, h, d), kraft);
            Panel("wall_z+", new Vector3(0f, h / 2f, d / 2f - t / 2f), new Vector3(w, h, t), kraft);
            Panel("wall_z-", new Vector3(0f, h / 2f, -d / 2f + t / 2f), new Vector3(w, h, t), kraft);
            // flaps hinged on the top edges, open outwards
            flaps[0] = Flap("flap_x+", new Vector3(w / 2f, h, 0f), new Vector3(w / 2f, t, d), Vector3.forward, -1f, kraft, 0);
            flaps[1] = Flap("flap_x-", new Vector3(-w / 2f, h, 0f), new Vector3(w / 2f, t, d), Vector3.forward, 1f, kraft, 1);
            flaps[2] = Flap("flap_z+", new Vector3(0f, h + t, d / 2f), new Vector3(w, t, d / 2f), Vector3.right, 1f, kraft, 2);
            flaps[3] = Flap("flap_z-", new Vector3(0f, h + t, -d / 2f), new Vector3(w, t, d / 2f), Vector3.right, -1f, kraft, 3);
            tape = Panel("tape", new Vector3(0f, h + 2.2f * t, 0f), new Vector3(.06f, .002f, d + .01f), new Color(.55f, .4f, .2f)).transform;
            tape.gameObject.SetActive(false);
        }

        private GameObject Panel(string name, Vector3 local, Vector3 size, Color color, Transform parent = null)
        {
            GameObject p = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(p.GetComponent<Collider>());
            p.name = name;
            p.transform.SetParent(parent != null ? parent : transform, false);
            p.transform.localPosition = local;
            p.transform.localScale = size;
            p.GetComponent<Renderer>().sharedMaterial = PackingItemFactory.Mat(color, .1f, 0f);
            return p;
        }

        /// <summary>Flap = hinge on the top edge + panel extending inwards when closed.</summary>
        private Transform Flap(string name, Vector3 hingeLocal, Vector3 size, Vector3 axis, float sign, Color color, int i)
        {
            var hinge = new GameObject(name).transform;
            hinge.SetParent(transform, false);
            hinge.localPosition = hingeLocal;
            // closed: panel lies flat towards the centre
            Vector3 inward = axis == Vector3.forward ? new Vector3(-Mathf.Sign(hingeLocal.x) * size.x / 2f, 0f, 0f)
                                                     : new Vector3(0f, 0f, -Mathf.Sign(hingeLocal.z) * size.z / 2f);
            Panel(name + "_panel", inward, size, color, hinge);
            flapClosed[i] = Quaternion.identity;
            flapOpen[i] = Quaternion.AngleAxis(sign * 110f, axis);
            hinge.localRotation = flapOpen[i];
            return hinge;
        }

        /// <summary>Place of the n-th product inside (local, on the bottom): 2 × 2 grid.</summary>
        public Vector3 SlotWorld(int index)
        {
            float x = (index % 2 == 0 ? -1f : 1f) * Size.x * .22f;
            float z = (index / 2 % 2 == 0 ? -1f : 1f) * Size.z * .22f;
            return transform.TransformPoint(new Vector3(x, .012f, z));
        }

        public void Put(PackingItem item)
        {
            int index = Contents.Count;
            Contents.Add(item);
            item.transform.SetParent(transform, true);
            item.transform.position = SlotWorld(index);
        }

        public IEnumerator CloseRoutine(float seconds = 1.2f)
        {
            State = CartonState.Closing;
            int[] order = { 0, 1, 2, 3 };
            foreach (int i in order)
            {
                float t = 0f;
                Quaternion from = flaps[i].localRotation;
                while (t < 1f)
                {
                    t += Time.deltaTime / (seconds / 4f);
                    flaps[i].localRotation = Quaternion.Slerp(from, flapClosed[i], Mathf.SmoothStep(0f, 1f, t));
                    yield return null;
                }
            }
            tape.gameObject.SetActive(true);
            State = CartonState.Closed;
        }

        /// <summary>Applies the destination label (texture generated by Muse Image, or a plain printed label).</summary>
        public void ApplyLabel(Texture2D texture, string destination, string model)
        {
            Destination = destination ?? "";
            LabelModel = model ?? "";
            GameObject label = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(label.GetComponent<Collider>());
            label.name = "label";
            label.transform.SetParent(transform, false);
            label.transform.localPosition = new Vector3(Size.x / 2f + .002f, Size.y * .5f, 0f);
            label.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
            label.transform.localScale = new Vector3(Size.z * .7f, Size.y * .7f, 1f);
            Material m = new Material(PackingItemFactory.Mat(Color.white, .1f, 0f));
            if (texture != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", texture);
            }
            label.GetComponent<Renderer>().sharedMaterial = m;
            State = CartonState.Labelled;
        }
    }
}
