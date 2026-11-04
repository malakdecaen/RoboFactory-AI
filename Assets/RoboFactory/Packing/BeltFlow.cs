using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RoboFactory.Packing
{
    /// <summary>
    /// Makes the scrolling texture of the belt modules of the full project run in the same direction (and at the same
    /// speed) as the products on the belt. The factory asset animates its belt material ("Line_Belt" shader) with
    /// offset = time x _Animation_Speed along the U axis of the mesh: depending on how each module is rotated, the
    /// texture ran the wrong way. For every belt module of the packing loop, the direction in which the texture
    /// moves is computed from the UV layout of its mesh, and the module's _Animation_Speed (a per-renderer property
    /// block, the shared material is untouched) is given the sign and size that make the texture move along the belt
    /// at the products' speed. Nothing happens in the public version (it has no such material).
    /// The mesh directions are kept in Resources/BeltFlow.json (written by the editor), because the meshes of a
    /// build are not readable.
    /// </summary>
    public static class BeltFlow
    {
        [Serializable] private sealed class Entry { public string key; public Vector3 t; }
        [Serializable] private sealed class Cache { public List<Entry> items = new List<Entry>(); }

        private static readonly int SpeedId = Shader.PropertyToID("_Animation_Speed");

        public static void Apply(ConveyorTrack track, float speed)
        {
            if (track == null) return;
            try { ApplyInner(track, speed); }
            catch (Exception e) { Debug.LogWarning("[PACK] belt flow not applied: " + e.Message); }
        }

        private static void ApplyInner(ConveyorTrack track, float speed)
        {
            Cache cache = LoadCache();
            bool changed = false;
            int modules = 0, reversed = 0, skipped = 0, shown = 0;
            var why = new Dictionary<string, int>();
            void Skip(string reason, Renderer rr, Mesh mm) { skipped++; why.TryGetValue(reason, out int n); why[reason] = n + 1; if (shown++ < 3) Debug.Log("[PACK] belt flow: skipped " + rr.name + " (mesh " + (mm != null ? mm.name + ", readable " + mm.isReadable : "-") + "): " + reason); }
            var block = new MaterialPropertyBlock();
            foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Material[] mats = r.sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                {
                    Material mat = mats[m];
                    if (mat == null || !mat.HasProperty(SpeedId) || mat.shader == null || !mat.shader.name.Contains("Line_Belt")) continue;
                    // only the modules of the packing loop
                    Vector3 c = r.bounds.center;
                    Vector3 p = track.Evaluate(track.Project(c), out Vector3 f);
                    if (new Vector2(p.x - c.x, p.z - c.z).magnitude > .6f) continue;
                    f.y = 0f;
                    if (f.sqrMagnitude < 1e-4f) continue;
                    f.Normalize();

                    Mesh mesh = mf.sharedMesh;
                    string key = mesh.name + "|" + m;
                    Vector3 tLocal;
                    Entry e = cache.items.Find(x => x.key == key);
                    if (e != null) tLocal = e.t;
                    else if (!TryDerivative(mesh, m, out tLocal)) { Skip("mesh data not readable", r, mesh); continue; }
                    else { cache.items.Add(new Entry { key = key, t = tLocal }); changed = true; }

                    Vector4 tiling = mat.HasProperty("_Albedo_Tiling") ? mat.GetVector("_Albedo_Tiling") : new Vector4(1f, 1f, 0f, 0f);
                    float tileU = Mathf.Abs(tiling.x) > 1e-4f ? tiling.x : 1f;
                    // world velocity of the texture per unit of _Animation_Speed (offset grows by 1 per second at speed 1)
                    Vector3 flow = -r.transform.TransformVector(tLocal) / tileU;
                    flow.y = 0f;
                    float len2 = flow.sqrMagnitude;
                    if (len2 < 1e-6f) { Skip("texture does not run along the ground", r, mesh); continue; }
                    float s = speed * Vector3.Dot(flow, f) / len2;
                    if (Mathf.Abs(s) < 1e-3f) { Skip("belt axis across the texture direction", r, mesh); continue; }
                    r.GetPropertyBlock(block, m);
                    block.SetFloat(SpeedId, s);
                    r.SetPropertyBlock(block, m);
                    modules++;
                    if (s < 0f) reversed++;
                }
            }
            if (changed) SaveCache(cache);
            if (modules > 0 || skipped > 0)
                Debug.Log("[PACK] belt flow: " + modules + " belt modules set to move with the products (" + reversed + " reversed), " + skipped + " skipped");
        }

        /// <summary>Average dP/du (local space) of the upward-facing triangles of a sub-mesh: the direction of the U axis of the texture.</summary>
        private static bool TryDerivative(Mesh mesh, int sub, out Vector3 t)
        {
            t = Vector3.zero;
            if (!mesh.isReadable && !Application.isEditor) return false;
            Vector3[] v; Vector2[] uv; int[] tri;
            try
            {
                if (sub < 0 || sub >= mesh.subMeshCount) sub = 0;
                v = mesh.vertices; uv = mesh.uv; tri = mesh.GetTriangles(sub);
            }
            catch (Exception) { return false; }
            if (v == null || uv == null || uv.Length != v.Length || tri == null || tri.Length < 3) return false;
            Vector3 sumTop = Vector3.zero, sumAll = Vector3.zero;
            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                Vector3 p0 = v[tri[i]], e1 = v[tri[i + 1]] - p0, e2 = v[tri[i + 2]] - p0;
                Vector2 a0 = uv[tri[i]], d1 = uv[tri[i + 1]] - a0, d2 = uv[tri[i + 2]] - a0;
                float det = d1.x * d2.y - d2.x * d1.y;
                if (Mathf.Abs(det) < 1e-12f) continue;
                Vector3 n = Vector3.Cross(e1, e2);
                float area = n.magnitude;
                if (area < 1e-9f) continue;
                Vector3 tri_t = (e1 * d2.y - e2 * d1.y) / det * area;
                sumAll += tri_t;
                if (n.y / area > .8f) sumTop += tri_t;   // model space is Y-up: the belt surface
            }
            Vector3 sum = sumTop.sqrMagnitude > 1e-12f ? sumTop : sumAll;
            if (sum.sqrMagnitude < 1e-12f) return false;
            // unit weight: the average is only used for its direction and scale, so normalise by the surface it came from
            float areaTotal = 0f;
            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                Vector3 n = Vector3.Cross(v[tri[i + 1]] - v[tri[i]], v[tri[i + 2]] - v[tri[i]]);
                float a = n.magnitude;
                if (a > 1e-9f && (sumTop.sqrMagnitude < 1e-12f || n.y / a > .8f)) areaTotal += a;
            }
            t = areaTotal > 1e-9f ? sum / areaTotal : sum;
            return true;
        }

        // ------------------------------------------------------------------ cache (Resources/BeltFlow.json)

        private static Cache LoadCache()
        {
            try
            {
                TextAsset ta = Resources.Load<TextAsset>("BeltFlow");
                if (ta != null)
                {
                    Cache c = JsonUtility.FromJson<Cache>(ta.text);
                    if (c != null && c.items != null) return c;
                }
            }
            catch (Exception) { }
            return new Cache();
        }

        private static void SaveCache(Cache cache)
        {
#if UNITY_EDITOR
            try
            {
                string dir = Path.Combine(Application.dataPath, "RoboFactory", "Resources");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "BeltFlow.json"), JsonUtility.ToJson(cache, true));
            }
            catch (Exception e) { Debug.LogWarning("[PACK] belt flow cache not saved: " + e.Message); }
#endif
        }
    }
}
