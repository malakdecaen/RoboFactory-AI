using System.Collections.Generic;
using RoboFactory.Hackathon.Orchestration;
using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Muse selection markers: a pulsing glowing disc on the floor under each
    /// chosen resource (pallets = cyan, bench = yellow, truck = green).
    /// </summary>
    public static class ResourceHighlighter
    {
        private sealed class Pulse : MonoBehaviour
        {
            public float Base = .55f;
            private Material mat;
            private void Start()
            {
                var r = GetComponent<Renderer>();
                if (r != null) mat = r.material;
            }
            private void Update()
            {
                float s = Base * (1f + .12f * Mathf.Sin(Time.time * 4f));
                transform.localScale = new Vector3(s, transform.localScale.y, s);
            }
            private void OnDestroy()
            {
                if (mat != null) Destroy(mat);
            }
        }

        private static readonly List<GameObject> marks = new List<GameObject>();
        private static Transform root;

        /// <summary>Pallets in cyan, bench in yellow, truck in green.</summary>
        public static void Show(Component host, MusePlan plan)
        {
            Clear();
            if (plan == null || WarehouseWorld.Instance == null) return;
            foreach (MusePlanPick p in plan.picks ?? new MusePlanPick[0])
                if (p != null) Mark(p.pallet_id, new Color(.1f, .8f, 1f, .4f));
            foreach (string fb in plan.fallback_pallets ?? new string[0])
                Mark(fb, new Color(.4f, .4f, 1f, .25f));
            Mark(plan.bench_id, new Color(1f, .85f, .1f, .4f));
            Mark(plan.truck_id, new Color(.2f, 1f, .35f, .4f));
        }

        public static void Clear()
        {
            foreach (GameObject mark in marks)
                if (mark != null) Object.Destroy(mark);
            marks.Clear();
        }

        private static void Mark(string resourceId, Color color)
        {
            if (string.IsNullOrEmpty(resourceId)) return;
            if (!WarehouseWorld.Instance.TryPositionOf(resourceId, out Vector3 pos)) return;
            if (root == null)
                root = new GameObject("Muse Selection Marks").transform;

            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "sel_" + resourceId;
            disc.transform.SetParent(root, false);
            disc.transform.position = new Vector3(pos.x, .03f, pos.z);
            disc.transform.localScale = new Vector3(.55f, .015f, .55f);
            Object.Destroy(disc.GetComponent<Collider>());
            var renderer = disc.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.sharedMaterial = GlowMaterial(color);
            disc.AddComponent<Pulse>();
            marks.Add(disc);
        }

        private static Material GlowMaterial(Color color)
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
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            return mat;
        }
    }
}
