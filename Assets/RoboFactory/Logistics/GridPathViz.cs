using UnityEngine;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Couche visuelle uniquement : dessine le chemin restant d'un GridMover
    /// avec une LineRenderer au sol (visible en Game view ET dans un build).
    /// Aucune logique métier ni navigation ici.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GridPathViz : MonoBehaviour
    {
        [Header("Source")]
        public GridMover Mover;

        [Header("Ligne")]
        public bool ShowPath = true;
        public Color PathColor = new Color(1f, .6f, .1f); // orange, distinct du cyan du robot 1
        public float PathLineWidth = .03f;
        public float Lift = .02f;

        private Transform vizRoot;
        private LineRenderer pathLine;
        private Material pathMaterial;
        private int pathHash = -1;

        private void Awake()
        {
            if (Mover == null) Mover = GetComponent<GridMover>();
        }

        private void OnDestroy()
        {
            if (vizRoot != null) Destroy(vizRoot.gameObject);
        }

        private void LateUpdate()
        {
            bool show = ShowPath && Mover != null && Mover.Enabled && Mover.CurrentPath.Count > 0;
            if (show)
            {
                EnsureViz();
                int hash = PathHash();
                if (hash != pathHash)
                {
                    pathHash = hash;
                    Rebuild();
                }
                // Le premier point suit le robot en direct, même sans changement de trajet.
                if (pathLine.positionCount > 0)
                    pathLine.SetPosition(0, Mover.transform.position + Vector3.up * Lift);
            }
            else pathHash = -1;
            if (vizRoot != null && vizRoot.gameObject.activeSelf != show)
                vizRoot.gameObject.SetActive(show);
        }

        private void EnsureViz()
        {
            if (vizRoot != null) return;
            var go = new GameObject("Grid Path Viz");
            vizRoot = go.transform;
            vizRoot.SetParent(transform, false);
            pathLine = go.AddComponent<LineRenderer>();
            pathLine.useWorldSpace = true;
            pathLine.widthMultiplier = PathLineWidth;
            pathLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pathLine.receiveShadows = false;
            pathLine.sharedMaterial = Material;
        }

        private Material Material
        {
            get
            {
                if (pathMaterial == null)
                {
                    Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader == null) shader = Shader.Find("Unlit/Color");
                    if (shader == null) shader = Shader.Find("Sprites/Default");
                    if (shader == null) shader = Shader.Find("Standard");
                    if (shader != null) pathMaterial = new Material(shader) { color = PathColor };
                }
                return pathMaterial;
            }
        }

        private int PathHash()
        {
            var list = Mover.CurrentPath;
            int hash = list.Count * 7919;
            if (list.Count > 0)
            {
                Vector3 a = list[0], b = list[list.Count - 1];
                hash ^= Mathf.RoundToInt(a.x * 40f) ^ (Mathf.RoundToInt(a.z * 40f) << 8);
                hash ^= (Mathf.RoundToInt(b.x * 40f) << 16) ^ (Mathf.RoundToInt(b.z * 40f) << 4);
            }
            return hash;
        }

        private void Rebuild()
        {
            var list = Mover.CurrentPath;
            if (list.Count == 0 || pathLine == null) return;
            pathLine.positionCount = list.Count;
            for (int i = 0; i < list.Count; i++)
                pathLine.SetPosition(i, list[i] + Vector3.up * Lift);
        }
    }
}
