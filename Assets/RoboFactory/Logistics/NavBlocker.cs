using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// No-go zone for GridMover navigation — logical, not physical:
    /// the BoxCollider stays a trigger, nothing collides, the other systems
    /// (Robot 1, Warcool interaction) do not see it.
    ///
    /// The GridMover marks the covered cells as blocked: the robot
    /// goes around the zone and the overlay shows it in red. A translucent red
    /// volume makes it visible in game/build.
    ///
    /// Usage: add it to a GameObject, fit the BoxCollider to the zone
    /// to forbid (e.g. one side of a bench to force the approach from the other side).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NavBlocker : MonoBehaviour
    {
        /// <summary>Every active volume in the scene.</summary>
        public static readonly List<NavBlocker> All = new List<NavBlocker>();

        [Tooltip("Translucent red volume visible in game/build.")]
        public bool ShowVolume = true;
        public Color VolumeColor = new Color(1f, .15f, .15f, .18f);

        private BoxCollider area;
        private Renderer volumeRenderer;

        private void OnEnable()
        {
            area = GetComponent<BoxCollider>();
            if (area == null) area = gameObject.AddComponent<BoxCollider>();
            area.isTrigger = true;
            if (!All.Contains(this)) All.Add(this);
            if (ShowVolume) BuildVolume();
        }

        private void OnDisable()
        {
            All.Remove(this);
        }

        /// <summary>True if the cell (center + world half-extents) touches the volume.</summary>
        public bool Overlaps(Vector3 center, Vector3 halfExtents)
        {
            if (area == null) return false;
            Bounds b = area.bounds;
            return Mathf.Abs(center.x - b.center.x) <= halfExtents.x + b.extents.x &&
                   Mathf.Abs(center.z - b.center.z) <= halfExtents.z + b.extents.z &&
                   Mathf.Abs(center.y - b.center.y) <= halfExtents.y + b.extents.y;
        }

        /// <summary>True if a point (e.g. a dock) is inside the volume.</summary>
        public bool Contains(Vector3 point)
        {
            return area != null && area.bounds.Contains(point);
        }

        /// <summary>Static test: does one of the active volumes cover this point?</summary>
        public static bool BlocksCell(Vector3 center, Vector3 halfExtents)
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i] != null && All[i].Overlaps(center, halfExtents)) return true;
            return false;
        }

        public static bool BlocksPoint(Vector3 point)
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i] != null && All[i].Contains(point)) return true;
            return false;
        }

        private void BuildVolume()
        {
            Transform existing = transform.Find("nav_blocker_volume");
            GameObject cube = existing != null ? existing.gameObject
                                               : GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "nav_blocker_volume";
            if (cube.transform.parent != transform) cube.transform.SetParent(transform, false);
            Collider col = cube.GetComponent<Collider>();
            if (col != null)
            {
                if (Application.isPlaying) Destroy(col);
                else DestroyImmediate(col);
            }
            // The cube matches the BoxCollider locally: same size/center.
            cube.transform.localPosition = area != null ? area.center : Vector3.zero;
            cube.transform.localRotation = Quaternion.identity;
            cube.transform.localScale = area != null ? area.size : Vector3.one;
            volumeRenderer = cube.GetComponent<Renderer>();
            volumeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader) { color = VolumeColor };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", VolumeColor);
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
            volumeRenderer.sharedMaterial = mat;
        }

        private void OnDrawGizmos()
        {
            var col = GetComponent<BoxCollider>();
            if (col == null) return;
            Gizmos.color = new Color(1f, .1f, .1f, .5f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(col.center, col.size);
        }
    }
}
