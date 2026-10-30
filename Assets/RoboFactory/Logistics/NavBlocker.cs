using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Zone interdite pour la navigation GridMover — logique, pas physique :
    /// le BoxCollider reste trigger, rien ne collisionne, les autres systèmes
    /// (Robot 1, interaction Warcool) ne la voient pas.
    ///
    /// Le GridMover marque les cellules couvertes comme bloquées : le robot
    /// contourne la zone et l'overlay la montre en rouge. Un volume rouge
    /// translucide la rend visible en jeu/build.
    ///
    /// Usage : ajouter sur un GameObject, régler le BoxCollider sur la zone
    /// à interdire (ex. un côté de banc pour forcer l'approche par l'autre côté).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NavBlocker : MonoBehaviour
    {
        /// <summary>Tous les volumes actifs de la scène.</summary>
        public static readonly List<NavBlocker> All = new List<NavBlocker>();

        [Tooltip("Volume rouge translucide visible en jeu/build.")]
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

        /// <summary>Vrai si la cellule (centre + demi-extents monde) touche le volume.</summary>
        public bool Overlaps(Vector3 center, Vector3 halfExtents)
        {
            if (area == null) return false;
            Bounds b = area.bounds;
            return Mathf.Abs(center.x - b.center.x) <= halfExtents.x + b.extents.x &&
                   Mathf.Abs(center.z - b.center.z) <= halfExtents.z + b.extents.z &&
                   Mathf.Abs(center.y - b.center.y) <= halfExtents.y + b.extents.y;
        }

        /// <summary>Vrai si un point (ex. un dock) est dans le volume.</summary>
        public bool Contains(Vector3 point)
        {
            return area != null && area.bounds.Contains(point);
        }

        /// <summary>Test statique : un des volumes actifs couvre-t-il ce point ?</summary>
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
            // Le cube épouse le BoxCollider en local : taille/centre identiques.
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
