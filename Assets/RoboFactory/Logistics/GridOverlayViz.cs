using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Debug overlay on the floor: green = cell traversable for the planner,
    /// red = blocked (colliders). Purely visual layer — reads GridMover.CellFree.
    /// World positions: the object is NOT parented to the robot.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GridOverlayViz : MonoBehaviour
    {
        [Header("Source")]
        public GridMover Mover;

        [Header("Grid")]
        public bool ShowGridDebug = true;
        [Tooltip("Delay between two grid refreshes, in seconds.")]
        public float GridRefresh = 1f;
        public Color FreeCellColor = new Color(0f, .9f, .3f, .22f);
        public Color BlockedCellColor = new Color(1f, .15f, .1f, .35f);

        private Transform gridRoot;
        private Mesh gridMesh;
        private float nextRefresh;
        private readonly List<Vector3> gridVerts = new List<Vector3>();
        private readonly List<int> gridFree = new List<int>();
        private readonly List<int> gridBlocked = new List<int>();

        private void Awake()
        {
            if (Mover == null) Mover = GetComponent<GridMover>();
        }

        private void OnDestroy()
        {
            if (gridRoot != null) Destroy(gridRoot.gameObject);
        }

        private void LateUpdate()
        {
            if (ShowGridDebug)
            {
                Ensure();
                if (Time.time >= nextRefresh)
                {
                    nextRefresh = Time.time + GridRefresh;
                    Rebuild();
                }
            }
            if (gridRoot != null && gridRoot.gameObject.activeSelf != ShowGridDebug)
                gridRoot.gameObject.SetActive(ShowGridDebug);
        }

        private void Ensure()
        {
            if (gridRoot != null) return;
            var go = new GameObject("Grid Overlay Viz (" + name + ")");
            gridRoot = go.transform; // scene root: the mesh stores world positions
            gridMesh = new Mesh { name = "Grid overlay", indexFormat = IndexFormat.UInt32 };
            go.AddComponent<MeshFilter>().sharedMesh = gridMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.sharedMaterials = new[] { TransparentMaterial(FreeCellColor), TransparentMaterial(BlockedCellColor) };
        }

        private void Rebuild()
        {
            float range = Mover != null && Mover.HalfExtent > .1f ? Mover.HalfExtent : 8f;
            float cell = Mover != null ? Mover.Cell : .25f;
            int n = Mathf.FloorToInt(range / cell);
            float y = transform.position.y + .015f;
            float h = cell * .42f;
            gridVerts.Clear();
            gridFree.Clear();
            gridBlocked.Clear();
            for (int ix = -n; ix <= n; ix++)
            {
                for (int iz = -n; iz <= n; iz++)
                {
                    Vector3 c = new Vector3(ix * cell, y, iz * cell);
                    int v = gridVerts.Count;
                    gridVerts.Add(c + new Vector3(-h, 0f, -h));
                    gridVerts.Add(c + new Vector3(h, 0f, -h));
                    gridVerts.Add(c + new Vector3(h, 0f, h));
                    gridVerts.Add(c + new Vector3(-h, 0f, h));
                    List<int> idx = Mover == null || Mover.CellFree(c) ? gridFree : gridBlocked;
                    idx.Add(v); idx.Add(v + 2); idx.Add(v + 1); // double face
                    idx.Add(v); idx.Add(v + 3); idx.Add(v + 2);
                    idx.Add(v); idx.Add(v + 1); idx.Add(v + 2);
                    idx.Add(v); idx.Add(v + 2); idx.Add(v + 3);
                }
            }
            gridMesh.Clear();
            gridMesh.SetVertices(gridVerts);
            gridMesh.subMeshCount = 2;
            gridMesh.SetIndices(gridFree, MeshTopology.Triangles, 0);
            gridMesh.SetIndices(gridBlocked, MeshTopology.Triangles, 1);
            gridMesh.RecalculateBounds();
        }

        private static Material TransparentMaterial(Color color)
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
                mat.renderQueue = (int)RenderQueue.Transparent;
            }
            return mat;
        }
    }
}
