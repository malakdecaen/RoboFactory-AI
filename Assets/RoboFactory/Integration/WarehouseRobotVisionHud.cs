using System.Collections.Generic;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Integration
{
    /// <summary>
    /// Robot vision window and live path display.
    ///
    /// - top-left: a small framed viewport showing exactly what the robot's front camera sees;
    /// - in the Game view and in a build: the planned route is drawn as a 2D overlay, cyan when
    ///   clear and red as soon as something is detected on it;
    /// - when the route is blocked the pilot recomputes a new one around the obstacle.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WarehouseRobotVisionHud : MonoBehaviour
    {
        [Header("Vision window")]
        public bool ShowVision = true;
        public int ViewWidth = 240;
        public int ViewHeight = 160;
        public Vector2 Margin = new Vector2(12f, 12f);
        [Tooltip("Height of the camera on the robot, in metres.")]
        public float EyeHeight = .95f;
        public float EyeForward = .28f;
        public float FieldOfView = 62f;

        [Header("Path")]
        public bool ShowPath = false;
        [Tooltip("Thickness of the floor trace, in metres.")]
        public float PathWidth = .16f;
        public float MarkerSize = .26f;
        public float MarkerSpacing = .45f;
        public Color ClearColour = new Color(.15f, .95f, 1f, .9f);
        public Color BlockedColour = new Color(1f, .2f, .15f, .95f);
        [Tooltip("Distance analysed in front of the robot to detect an obstacle.")]
        public float LookAhead = 1.4f;
        public float ReplanCooldown = .6f;

        private WarehouseRobotAutopilot pilot;
        private Camera eye;
        private RenderTexture target;
        private MeshFilter pathFilter;
        private Material pathMaterial;
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<int> triangles = new List<int>();
        private readonly List<Transform> markers = new List<Transform>();
        private Material markerMaterial;
        private float nextCheck;
        private bool blocked;
        private Mesh pathMesh;
        private MeshRenderer pathRenderer;
        private int lastPathCount = -1;
        private Vector3 lastPathEnd;

        public bool Blocked => blocked;

        private void Awake()
        {
            pilot = GetComponent<WarehouseRobotAutopilot>();
            BuildEye();
        }

        /// <summary>Path drawn by a MeshRenderer (visible in Game view + build, no LineRenderer).</summary>
        private void BuildPathMesh()
        {
            var go = new GameObject("Autopilot path mesh");
            go.transform.SetParent(transform, false);
            pathMesh = new Mesh { name = "Autopilot path" };
            go.AddComponent<MeshFilter>().sharedMesh = pathMesh;
            pathRenderer = go.AddComponent<MeshRenderer>();
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var material = new Material(shader) { color = ClearColour };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", ClearColour);
            pathRenderer.sharedMaterial = material;
            pathRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pathRenderer.receiveShadows = false;
        }

        private void UpdatePathMesh()
        {
            if (pathMesh == null || pilot == null) return;
            IReadOnlyList<Vector3> path = pilot.CurrentPath;
            int count = path != null ? path.Count : 0;
            Vector3 end = count > 0 ? path[count - 1] : Vector3.zero;
            if (count == lastPathCount && Vector3.Distance(end, lastPathEnd) < .01f)
            {
                ApplyPathColour();
                return;
            }
            lastPathCount = count;
            lastPathEnd = end;

            pathMesh.Clear();
            if (count == 0) return;

            float y = transform.position.y + .02f;
            float half = Mathf.Max(PathWidth, .12f) * .5f;
            var vertices = new List<Vector3>(count * 4 + 4);
            var triangles = new List<int>(count * 12 + 12);
            Vector3 previous = new Vector3(transform.position.x, y, transform.position.z);

            for (int i = 0; i < count; i++)
            {
                Vector3 next = new Vector3(path[i].x, y, path[i].z);
                Vector3 direction = next - previous;
                if (direction.sqrMagnitude < 1e-6f) continue;
                Vector3 side = Vector3.Cross(Vector3.up, direction.normalized) * half;
                int v = vertices.Count;
                vertices.Add(previous - side); vertices.Add(previous + side);
                vertices.Add(next - side); vertices.Add(next + side);
                triangles.Add(v); triangles.Add(v + 2); triangles.Add(v + 1);
                triangles.Add(v + 1); triangles.Add(v + 2); triangles.Add(v + 3);
                triangles.Add(v); triangles.Add(v + 1); triangles.Add(v + 2);
                triangles.Add(v + 1); triangles.Add(v + 3); triangles.Add(v + 2);
                previous = next;
            }
            pathMesh.SetVertices(vertices);
            pathMesh.SetTriangles(triangles, 0);
            pathMesh.RecalculateBounds();
            ApplyPathColour();
        }

        private void ApplyPathColour()
        {
            if (pathRenderer == null || pathRenderer.sharedMaterial == null) return;
            Color colour = blocked ? BlockedColour : ClearColour;
            pathRenderer.sharedMaterial.color = colour;
            if (pathRenderer.sharedMaterial.HasProperty("_BaseColor"))
                pathRenderer.sharedMaterial.SetColor("_BaseColor", colour);
        }

        private void BuildEye()
        {
            var go = new GameObject("Robot eye camera");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, EyeHeight, EyeForward);
            go.transform.localRotation = Quaternion.identity;
            eye = go.AddComponent<Camera>();
            eye.fieldOfView = FieldOfView;
            eye.nearClipPlane = .05f;
            eye.farClipPlane = 40f;
            eye.clearFlags = CameraClearFlags.SolidColor;
            eye.backgroundColor = new Color(.05f, .07f, .09f);
            eye.cullingMask = ~0;
            eye.useOcclusionCulling = false;
            target = new RenderTexture(ViewWidth, ViewHeight, 16, RenderTextureFormat.ARGB32);
            target.Create();
            eye.targetTexture = target;
        }

        private void LateUpdate()
        {
            if (eye != null)
            {
                eye.transform.localPosition = new Vector3(0f, EyeHeight, EyeForward);
                eye.transform.localRotation = Quaternion.identity;
            }
            if (Time.time >= nextCheck) CheckRoute();
        }

        /// <summary>
        /// Builds a thick ribbon along the REMAINING route only: the part already driven is removed
        /// and nothing is drawn behind the robot.
        /// </summary>
        /// <summary>Looks ahead on the route: anything solid there means red path + replan.</summary>
        private void CheckRoute()
        {
            nextCheck = Time.time + ReplanCooldown;
            if (pilot == null) return;
            IReadOnlyList<Vector3> path = pilot.CurrentPath;
            if (path == null || path.Count == 0)
            {
                blocked = false;
                return;
            }

            Vector3 origin = transform.position;
            bool hit = false;
            float travelled = 0f;
            Vector3 previous = origin;
            for (int i = 0; i < path.Count && travelled < LookAhead; i++)
            {
                Vector3 node = path[i];
                travelled += Vector3.Distance(previous, node);
                previous = node;
                // SAME test as the planner (ObstacleMask + clearance): testing
                // every layer saw colliders that the route can never
                // avoid -> infinite replan loop, frozen robot.
                if (i < path.Count - 1 && !pilot.PathCellFree(node)) { hit = true; break; }
            }

            blocked = hit;
            if (hit)
            {
                // Anti-loop guard: a persistent obstacle must not trigger
                // a replan at every cooldown — the planner has its own wait.
                forcedReplans++;
                if (forcedReplans <= 8)
                {
                    pilot.RequestReplan();
                    if (WarehouseRobotAutopilot.Verbose)
                        Debug.Log("[Autopilot] obstacle detected on the path: new path computed.");
                }
                else if (forcedReplans == 9 && WarehouseRobotAutopilot.Verbose)
                {
                    Debug.LogWarning("[Autopilot] persistent obstacle on the path: " +
                                     "vision replans suspended (the pilot handles the wait).");
                }
            }
            else forcedReplans = 0;
        }

        private int forcedReplans;

        private void OnGUI()
        {
            if (!ShowVision || target == null) return;

            var frame = new Rect(Margin.x, Margin.y, ViewWidth + 8f, ViewHeight + 26f);
            Color previous = GUI.color;

            GUI.color = blocked ? new Color(1f, .35f, .3f, 1f) : new Color(.2f, .95f, 1f, 1f);
            GUI.DrawTexture(new Rect(frame.x, frame.y, frame.width, frame.height), Texture2D.whiteTexture);
            GUI.color = new Color(.06f, .08f, .10f, .96f);
            GUI.DrawTexture(new Rect(frame.x + 2f, frame.y + 2f, frame.width - 4f, frame.height - 4f), Texture2D.whiteTexture);

            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(frame.x + 4f, frame.y + 22f, ViewWidth, ViewHeight), target, ScaleMode.ScaleToFit, false);

            var title = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(.85f, .95f, 1f) } };
            GUI.Label(new Rect(frame.x + 6f, frame.y + 3f, ViewWidth, 18f),
                blocked ? "ROBOT VISION  -  PATH BLOCKED" : "ROBOT VISION  -  PATH CLEAR", title);
            GUI.color = previous;
        }

        private void OnDestroy()
        {
            if (target != null) { target.Release(); Destroy(target); }
            if (eye != null) Destroy(eye.gameObject);
            if (pathMesh != null) Destroy(pathMesh);
            if (pathMaterial != null) Destroy(pathMaterial);
            if (pathMesh != null) Destroy(pathMesh);
        }
    }
}
