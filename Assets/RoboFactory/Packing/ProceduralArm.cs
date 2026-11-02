using UnityEngine;

namespace RoboFactory.Packing
{
    /// <summary>
    /// Industrial arm built by code (public version): turret (yaw), shoulder, elbow, wrist kept vertical, gripper.
    /// Analytic IK in the vertical plane of the target (elbow up), same contract as the IK arm of the full project.
    /// </summary>
    public sealed class ProceduralArm : IPackingArm
    {
        public float ShoulderHeight = 1.1f;
        public float ShoulderOffset = .3f;   // shoulder ahead of the turret axis
        public float Upper = .85f;
        public float Fore = .85f;
        public float Tool = .3f;
        public float Speed = 1.1f;

        private readonly Transform root, yawT, shoulderT, elbowT, wristT;
        private Vector3 tip, goal;

        public bool Ready => true;
        public Transform Grip { get; }
        public Vector3 ToolTip => tip;
        public Vector3 Home { get; }
        public Vector3 Goal { get => goal; set => goal = value; }
        public bool AtGoal => Vector3.Distance(tip, goal) < .006f;

        public ProceduralArm(Transform parent, Vector3 basePosition, float homeYaw)
        {
            Color orange = new Color(.95f, .5f, .1f), dark = new Color(.15f, .15f, .17f), steel = new Color(.6f, .62f, .65f);
            root = new GameObject("Packing Arm").transform;
            root.SetParent(parent, false);
            root.position = basePosition;
            Part(root, PrimitiveType.Cylinder, new Vector3(0f, .15f, 0f), new Vector3(.7f, .15f, .7f), dark);
            yawT = new GameObject("turret").transform;
            yawT.SetParent(root, false);
            yawT.localPosition = Vector3.up * .3f;
            Part(yawT, PrimitiveType.Cylinder, new Vector3(0f, .25f, 0f), new Vector3(.5f, .25f, .5f), orange);
            Part(yawT, PrimitiveType.Cube, new Vector3(0f, ShoulderHeight - .45f, ShoulderOffset * .5f), new Vector3(.32f, .5f, .45f), orange);
            shoulderT = new GameObject("shoulder").transform;
            shoulderT.SetParent(yawT, false);
            shoulderT.localPosition = new Vector3(0f, ShoulderHeight - .3f, ShoulderOffset);
            Part(shoulderT, PrimitiveType.Cylinder, Vector3.zero, new Vector3(.26f, .2f, .26f), dark).localRotation = Quaternion.Euler(0f, 0f, 90f);
            Part(shoulderT, PrimitiveType.Cube, new Vector3(0f, 0f, Upper / 2f), new Vector3(.18f, .2f, Upper), orange);
            elbowT = new GameObject("elbow").transform;
            elbowT.SetParent(shoulderT, false);
            elbowT.localPosition = new Vector3(0f, 0f, Upper);
            Part(elbowT, PrimitiveType.Cylinder, Vector3.zero, new Vector3(.2f, .16f, .2f), dark).localRotation = Quaternion.Euler(0f, 0f, 90f);
            Part(elbowT, PrimitiveType.Cube, new Vector3(0f, 0f, Fore / 2f), new Vector3(.13f, .14f, Fore), orange);
            wristT = new GameObject("wrist").transform;
            wristT.SetParent(elbowT, false);
            wristT.localPosition = new Vector3(0f, 0f, Fore);
            Part(wristT, PrimitiveType.Cylinder, new Vector3(0f, 0f, Tool * .3f), new Vector3(.1f, Tool * .3f, .1f), steel).localRotation = Quaternion.Euler(90f, 0f, 0f);
            Part(wristT, PrimitiveType.Cube, new Vector3(.04f, 0f, Tool * .85f), new Vector3(.02f, .06f, Tool * .3f), dark);
            Part(wristT, PrimitiveType.Cube, new Vector3(-.04f, 0f, Tool * .85f), new Vector3(.02f, .06f, Tool * .3f), dark);
            Grip = new GameObject("Packing Grip").transform;
            Grip.SetParent(wristT, false);
            Grip.localPosition = new Vector3(0f, 0f, Tool);
            Vector3 fwd = Quaternion.Euler(0f, homeYaw, 0f) * Vector3.forward;
            Home = basePosition + fwd * (.6f * (Upper + Fore)) + Vector3.up * (ShoulderHeight + .3f + .2f - Tool);
            tip = goal = Home;
            Solve(tip);
        }

        private Vector3 ShoulderWorld(float yaw) => root.position + Vector3.up * (.3f + ShoulderHeight - .3f) + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * ShoulderOffset;

        public bool CanReach(Vector3 t)
        {
            Vector3 w = t + Vector3.up * Tool;
            Vector3 flat = w - root.position;
            flat.y = 0f;
            float yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            return Vector3.Distance(w, ShoulderWorld(yaw)) <= (Upper + Fore) * .985f;
        }

        public void Tick(float dt)
        {
            tip = Vector3.MoveTowards(tip, goal, Speed * dt);
            Solve(tip);
        }

        public void LateTick() { }

        /// <summary>Turret yaw, shoulder pitch, elbow bend (elbow up), wrist vertical.</summary>
        private void Solve(Vector3 t)
        {
            Vector3 w = t + Vector3.up * Tool;
            Vector3 flat = w - root.position;
            flat.y = 0f;
            float yaw = flat.sqrMagnitude > 1e-6f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : 0f;
            yawT.localRotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 s = ShoulderWorld(yaw);
            Vector3 sw = w - s;
            float r = new Vector2(sw.x, sw.z).magnitude, z = sw.y;
            float d = Mathf.Clamp(Mathf.Sqrt(r * r + z * z), .05f, (Upper + Fore) * .999f);
            float elevation = Mathf.Atan2(z, r) * Mathf.Rad2Deg;
            float shoulderInner = Mathf.Acos(Mathf.Clamp((Upper * Upper + d * d - Fore * Fore) / (2f * Upper * d), -1f, 1f)) * Mathf.Rad2Deg;
            float elbowInner = Mathf.Acos(Mathf.Clamp((Upper * Upper + Fore * Fore - d * d) / (2f * Upper * Fore), -1f, 1f)) * Mathf.Rad2Deg;
            float shoulderPitch = elevation + shoulderInner;          // upwards from horizontal
            shoulderT.localRotation = Quaternion.Euler(-shoulderPitch, 0f, 0f);
            elbowT.localRotation = Quaternion.Euler(180f - elbowInner, 0f, 0f);
            Vector3 heading = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            wristT.rotation = Quaternion.LookRotation(Vector3.down, heading);
        }

        private static Transform Part(Transform parent, PrimitiveType type, Vector3 local, Vector3 scale, Color color)
        {
            GameObject g = GameObject.CreatePrimitive(type);
            Object.Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = local;
            g.transform.localScale = scale;
            g.GetComponent<Renderer>().sharedMaterial = PackingItemFactory.Mat(color, .45f, .2f);
            return g.transform;
        }
    }
}
