using System;
using System.Reflection;
using UnityEngine;

namespace RoboFactory.Packing
{
    /// <summary>Arm of the packing cell: the cell only asks for a tool-tip position and a grip point.</summary>
    public interface IPackingArm
    {
        bool Ready { get; }
        /// <summary>Where the gripped item is attached (follows the tool tip).</summary>
        Transform Grip { get; }
        Vector3 ToolTip { get; }
        Vector3 Home { get; }
        /// <summary>Commanded tool-tip position (world). The arm moves there at its own speed.</summary>
        Vector3 Goal { get; set; }
        bool AtGoal { get; }
        bool CanReach(Vector3 tip);
        void Tick(float dt);
        void LateTick();
    }

    /// <summary>
    /// Adapter for an articulated arm driven by an IK target (yaw + shoulder + elbow), such as the arm of the
    /// factory environment of the full project. It is found by its component type name and read through
    /// reflection, so the code does not depend on the store asset: the public version uses ProceduralArm instead.
    /// The wrist is re-oriented every frame after the IK so the gripper points straight down (pick from above).
    /// </summary>
    public sealed class IkTargetArm : IPackingArm
    {
        public float Speed = 1.1f;          // m/s of the tool tip
        public float Tolerance = .01f;

        private Transform stand, arm1, arm2, wrist, arm1Base, target;
        private Vector3 gripAxisLocal, hingeAxisLocal;
        private float tipLength, upper, fore;
        private Vector3 arm1BaseInStand;
        private Vector3 goal, wristPos;

        public bool Ready { get; private set; }
        public Transform Grip { get; private set; }
        public Vector3 Home { get; private set; }
        public string Description { get; private set; } = "";

        public Vector3 Goal
        {
            get => goal;
            set => goal = value;
        }

        public Vector3 ToolTip => wrist != null ? wrist.position + Vector3.down * tipLength : goal;
        public bool AtGoal => Vector3.Distance(ToolTip, goal) <= Tolerance + .002f;

        /// <summary>Finds the first component of type <paramref name="typeName"/> exposing the IK fields.</summary>
        public static IkTargetArm Find(string typeName = "ArmIK")
        {
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (mb == null || mb.GetType().Name != typeName) continue;
                var arm = new IkTargetArm();
                if (arm.Bind(mb)) return arm;
            }
            return null;
        }

        private static Transform Field(object o, string name)
        {
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f != null ? f.GetValue(o) as Transform : null;
        }

        private bool Bind(MonoBehaviour ik)
        {
            stand = Field(ik, "_stand");
            arm1 = Field(ik, "_arm1");
            arm2 = Field(ik, "_arm2");
            wrist = Field(ik, "_arm3");
            arm1Base = Field(ik, "_arm1Base");
            target = Field(ik, "_target");
            if (stand == null || arm1 == null || arm2 == null || wrist == null || arm1Base == null || target == null) return false;

            upper = Vector3.Distance(arm1.position, arm2.position);
            fore = Vector3.Distance(arm2.position, wrist.position);
            arm1BaseInStand = Quaternion.Inverse(stand.rotation) * (arm1Base.position - stand.position);

            // gripper axis = from the wrist towards the centre of the tool meshes (children of the wrist)
            Bounds tool = new Bounds(wrist.position, Vector3.zero);
            bool any = false;
            foreach (Renderer r in wrist.GetComponentsInChildren<Renderer>())
            {
                if (!any) { tool = r.bounds; any = true; } else tool.Encapsulate(r.bounds);
            }
            Vector3 axis = any ? tool.center - wrist.position : stand.rotation * Vector3.right;
            if (axis.sqrMagnitude < 1e-6f) axis = stand.rotation * Vector3.right;
            axis.Normalize();
            gripAxisLocal = Quaternion.Inverse(wrist.rotation) * axis;
            hingeAxisLocal = Quaternion.Inverse(wrist.rotation) * (stand.rotation * Vector3.forward);
            // tip = farthest point of the tool along its axis
            tipLength = .25f;
            if (any)
            {
                float best = 0f;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = new Vector3(i % 2 == 0 ? tool.min.x : tool.max.x, (i / 2) % 2 == 0 ? tool.min.y : tool.max.y, i / 4 == 0 ? tool.min.z : tool.max.z);
                    best = Mathf.Max(best, Vector3.Dot(c - wrist.position, axis));
                }
                tipLength = Mathf.Clamp(best * .85f, .1f, .8f);
            }

            var grip = new GameObject("Packing Grip").transform;
            grip.SetParent(wrist, false);
            Grip = grip;
            // home: above the base, tool down, half extended forward
            Vector3 fwd = stand.rotation * Vector3.right;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            Home = arm1Base.position + fwd * (.6f * (upper + fore)) + Vector3.up * (.2f - tipLength);
            goal = Home;
            wristPos = target.position;
            Ready = true;
            Description = "IK arm (" + ik.GetType().Name + ") upper " + upper.ToString("F2") + " m, fore " + fore.ToString("F2") +
                          " m, tool " + tipLength.ToString("F2") + " m";
            return true;
        }

        /// <summary>Wrist position for a tool tip pointing down at <paramref name="tip"/>.</summary>
        private Vector3 WristFor(Vector3 tip) => tip + Vector3.up * tipLength;

        public bool CanReach(Vector3 tip)
        {
            if (!Ready) return false;
            Vector3 w = WristFor(tip);
            Vector3 flat = w - stand.position;
            flat.y = 0f;
            float yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg - 90f;
            Vector3 shoulder = stand.position + Quaternion.Euler(0f, yaw, 0f) * arm1BaseInStand;
            return Vector3.Distance(w, shoulder) <= (upper + fore) * .985f;
        }

        public void Tick(float dt)
        {
            if (!Ready) return;
            Vector3 want = WristFor(goal);
            wristPos = Vector3.MoveTowards(wristPos, want, Speed * dt);
            target.position = wristPos;
        }

        /// <summary>After the IK has run: gripper straight down, hinge kept horizontal.</summary>
        public void LateTick()
        {
            if (!Ready) return;
            Quaternion local = Quaternion.LookRotation(gripAxisLocal, hingeAxisLocal);
            Vector3 hinge = stand.rotation * Vector3.forward;
            Quaternion world = Quaternion.LookRotation(Vector3.down, hinge);
            wrist.rotation = world * Quaternion.Inverse(local);
        }
    }
}
