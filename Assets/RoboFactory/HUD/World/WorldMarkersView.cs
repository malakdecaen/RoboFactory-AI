using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Repères holographiques 3D posés sur les VRAIES ressources réservées (palettes, bancs,
    /// camions) et tracé lumineux des VRAIS chemins de navigation des robots
    /// (WarehouseRobotAutopilot.CurrentPath / GridMover.CurrentPath). Rien de fictif.
    /// </summary>
    public sealed class WorldMarkersView
    {
        public int Layer = 0;
        public bool ShowMarkers = true;
        public bool ShowPaths = true;
        public int FocusOrder = -1;

        private Transform root;
        private Material ringMaterial, pathMaterial;
        private Mesh quad;
        private MaterialPropertyBlock mpb;
        private readonly Dictionary<string, Marker> markers = new Dictionary<string, Marker>();
        private readonly List<string> stale = new List<string>();
        private readonly Dictionary<Transform, Bounds> boundsCache = new Dictionary<Transform, Bounds>();
        private readonly Dictionary<string, float> flashes = new Dictionary<string, float>();
        private LineRenderer pathR1, pathR2;
        private Transform endR1, endR2;
        private float time;

        private sealed class Marker
        {
            public LineRenderer Box;
            public Bounds Bounds;
            public Color Color;
            public float Spawn;
            public bool Seen;
            public int OrderId;
        }

        public void Build()
        {
            root = new GameObject("HUD World Markers").transform;
            quad = BuildQuad();
            mpb = new MaterialPropertyBlock();
            ringMaterial = Additive(HudSprites.SelectionRing());
            pathMaterial = Additive(HudSprites.BeamProfile());
            pathR1 = BuildLine("Path R1");
            pathR2 = BuildLine("Path R2");
            endR1 = BuildEndRing("Path End R1");
            endR2 = BuildEndRing("Path End R2");
        }

        public void Flash(IEnumerable<string> ids)
        {
            foreach (string id in ids)
                if (!string.IsNullOrEmpty(id)) flashes[id] = 1f;
        }

        public void Tick(float dt, HudModel m)
        {
            time += dt;
            if (root == null) return;

            // ---- repères de sélection
            foreach (Marker mk in markers.Values) mk.Seen = false;
            if (ShowMarkers)
            {
                foreach (MarkVM v in m.Marks)
                {
                    if (v.Transform == null) continue;
                    string key = v.Kind + ":" + v.Id;
                    if (!markers.TryGetValue(key, out Marker mk))
                    {
                        mk = CreateMarker(v);
                        markers[key] = mk;
                    }
                    mk.Seen = true;
                    mk.OrderId = v.OrderId;
                    mk.Color = HudTheme.Orange; // sélection Muse = orange (référence), urgence = orange plus intense
                    UpdateMarker(mk, v, dt);
                }
            }
            stale.Clear();
            foreach (KeyValuePair<string, Marker> kv in markers)
                if (!kv.Value.Seen) stale.Add(kv.Key);
            foreach (string key in stale)
            {
                Marker mk = markers[key];
                if (mk.Box != null) Object.Destroy(mk.Box.gameObject);
                markers.Remove(key);
            }

            // décroissance des flashs
            stale.Clear();
            foreach (string id in flashes.Keys) stale.Add(id);
            foreach (string id in stale)
            {
                float f = flashes[id] - dt / 1.6f;
                if (f <= 0f) flashes.Remove(id); else flashes[id] = f;
            }

            // ---- chemins réels
            UpdatePath(pathR1, endR1, m.R1, IsUrgent(m, m.R1) ? HudTheme.Orange : HudTheme.Cyan);
            UpdatePath(pathR2, endR2, m.R2, IsUrgent(m, m.R2) ? HudTheme.Orange : Color.Lerp(HudTheme.Cyan, HudTheme.Blue, .35f));
        }

        private static bool IsUrgent(HudModel m, RobotVM r)
        {
            OrderVM o = m.FindOrder(r.OrderId);
            return o != null && o.Urgent;
        }

        /// <summary>Contour filaire fin (12 arêtes) autour de la vraie ressource : ne masque jamais le modèle.</summary>
        private Marker CreateMarker(MarkVM v)
        {
            Bounds b = BoundsOf(v.Transform);
            b.Expand(new Vector3(.08f, .04f, .08f));
            var mk = new Marker { Spawn = time, Bounds = b };
            var go = new GameObject("Mark " + v.Id);
            go.layer = Layer;
            go.transform.SetParent(root, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = pathMaterial;
            lr.useWorldSpace = true;
            lr.loop = false;
            lr.widthMultiplier = .02f;
            lr.numCornerVertices = 0;
            lr.textureMode = LineTextureMode.Stretch;
            lr.alignment = LineAlignment.View;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            Vector3 mn = b.min, mx = b.max;
            Vector3 p000 = new Vector3(mn.x, mn.y, mn.z), p100 = new Vector3(mx.x, mn.y, mn.z), p110 = new Vector3(mx.x, mn.y, mx.z), p010 = new Vector3(mn.x, mn.y, mx.z);
            Vector3 p001 = new Vector3(mn.x, mx.y, mn.z), p101 = new Vector3(mx.x, mx.y, mn.z), p111 = new Vector3(mx.x, mx.y, mx.z), p011 = new Vector3(mn.x, mx.y, mx.z);
            // parcours continu des 12 arêtes (quelques arêtes repassées)
            Vector3[] path = { p000, p100, p110, p010, p000, p001, p101, p100, p101, p111, p110, p111, p011, p010, p011, p001 };
            lr.positionCount = path.Length;
            lr.SetPositions(path);
            mk.Box = lr;
            return mk;
        }

        private void UpdateMarker(Marker mk, MarkVM v, float dt)
        {
            float age = time - mk.Spawn;
            float appear = Mathf.Clamp01(age / .5f);
            float flash = flashes.TryGetValue(v.Id, out float f) ? f : 0f;
            float focus = FocusOrder < 0 ? 1f : (mk.OrderId == FocusOrder ? 1.2f : .3f);
            float pulse = .5f + .5f * Mathf.Sin(time * 2.6f + mk.Bounds.center.x);
            float alpha = appear * (.55f + .2f * pulse + flash * .45f) * focus * (v.Urgent ? 1.15f : 1f);
            mk.Box.widthMultiplier = .018f + flash * .03f + (v.Urgent ? .006f : 0f);
            SetColor(mk.Box, HudTheme.A(mk.Color, Mathf.Clamp01(alpha)));
        }

        private void UpdatePath(LineRenderer line, Transform end, RobotVM r, Color c)
        {
            IReadOnlyList<Vector3> path = r.Path;
            bool show = ShowPaths && r.Present && r.Transform != null && path != null && path.Count > 0;
            if (line.enabled != show) line.enabled = show;
            if (end.gameObject.activeSelf != show) end.gameObject.SetActive(show);
            if (!show) return;
            int n = path.Count + 1;
            if (line.positionCount != n) line.positionCount = n;
            float lift = .045f;
            Vector3 p0 = r.Transform.position;
            line.SetPosition(0, new Vector3(p0.x, p0.y + lift, p0.z));
            for (int i = 0; i < path.Count; i++)
                line.SetPosition(i + 1, new Vector3(path[i].x, p0.y + lift, path[i].z));
            float pulse = .5f + .5f * Mathf.Sin(time * 3f);
            Color lc = HudTheme.A(c, .45f + .2f * pulse);
            line.startColor = HudTheme.A(c, .25f);
            line.endColor = lc;
            SetColor(line, lc); // URP Unlit ignore les couleurs de sommets : teinte par bloc de propriétés
            Vector3 last = path[path.Count - 1];
            end.position = new Vector3(last.x, p0.y + lift, last.z);
            float s = .38f + .06f * pulse;
            end.localScale = new Vector3(s, s, 1f);
            end.rotation = Quaternion.Euler(90f, -time * 60f % 360f, 0f);
            SetColor(end.GetComponent<MeshRenderer>(), HudTheme.A(c, .85f));
        }

        // ------------------------------------------------------------------ construction

        private LineRenderer BuildLine(string name)
        {
            var go = new GameObject(name);
            go.layer = Layer;
            go.transform.SetParent(root, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = pathMaterial;
            lr.widthMultiplier = .05f;
            lr.textureMode = LineTextureMode.Stretch;
            lr.alignment = LineAlignment.TransformZ;
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // ruban à plat sur le sol
            lr.numCornerVertices = 3;
            lr.numCapVertices = 2;
            lr.useWorldSpace = true;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;
            return lr;
        }

        private Transform BuildEndRing(string name)
        {
            var go = new GameObject(name);
            go.layer = Layer;
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ringMaterial;
            Quiet(mr);
            go.SetActive(false);
            return go.transform;
        }

        private Bounds BoundsOf(Transform t)
        {
            if (boundsCache.TryGetValue(t, out Bounds b)) return b;
            bool has = false;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer || !r.enabled) continue;
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!has) b = new Bounds(t.position, Vector3.one);
            boundsCache[t] = b;
            return b;
        }

        private void SetColor(Renderer r, Color c)
        {
            if (r == null) return;
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", c);
            mpb.SetColor("_Color", c);
            r.SetPropertyBlock(mpb);
        }

        private static void Quiet(Renderer r)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private static float EaseOut(float t) { return 1f - (1f - t) * (1f - t); }

        private static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "HUD Quad" };
            mesh.vertices = new[] { new Vector3(-.5f, -.5f, 0f), new Vector3(.5f, -.5f, 0f), new Vector3(.5f, .5f, 0f), new Vector3(-.5f, .5f, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }; // double face
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Matériau additif non éclairé (URP Unlit, repli Sprites/Default).</summary>
        private static Material Additive(Texture tex)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            bool urp = shader != null;
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = "HUD Holo", hideFlags = HideFlags.DontSave };
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            mat.mainTexture = tex;
            if (urp)
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 2f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_ZWrite", 0f);
                mat.SetFloat("_Cull", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 10;
            return mat;
        }

        public void SetVisible(bool markersOn, bool pathsOn)
        {
            ShowMarkers = markersOn;
            ShowPaths = pathsOn;
        }

        public void Dispose()
        {
            if (root != null) Object.Destroy(root.gameObject);
            if (quad != null) Object.Destroy(quad);
            if (ringMaterial != null) Object.Destroy(ringMaterial);
            if (pathMaterial != null) Object.Destroy(pathMaterial);
        }
    }
}
