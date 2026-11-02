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
    /// The tool is a cylinder (suction gripper): its end is the gripping point and, after the IK has placed the
    /// wrist, the wrist is re-oriented every frame so the cylinder points straight down onto the product.
    /// </summary>
    public sealed class IkTargetArm : IPackingArm
    {
        public float Speed = 1.1f;          // m/s of the tool tip
        public float Tolerance = .01f;

        private Transform stand, arm1, arm2, wrist, arm1Base, target;
        private Vector3 axisLocal, hingeLocal, tipLocal;
        private float upper, fore;
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

        public Vector3 ToolTip => wrist != null ? wrist.position + wrist.rotation * tipLocal : goal;
        public bool AtGoal => Vector3.Distance(ToolTip, goal) <= Tolerance + .002f;

        /// <summary>
        /// Finds the first component of type <paramref name="typeName"/> exposing the IK fields.
        /// <paramref name="toolTip"/> / <paramref name="toolAxis"/>: end of the gripping cylinder and its outward axis,
        /// in the wrist's local space (measured on the model); null = estimated from the tool meshes.
        /// </summary>
        public static IkTargetArm Find(string typeName = "ArmIK", Vector3? toolTip = null, Vector3? toolAxis = null)
        {
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (mb == null || mb.GetType().Name != typeName) continue;
                var arm = new IkTargetArm();
                if (arm.Bind(mb, toolTip, toolAxis)) return arm;
            }
            return null;
        }

        private static Transform Field(object o, string name)
        {
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f != null ? f.GetValue(o) as Transform : null;
        }

        private bool Bind(MonoBehaviour ik, Vector3? toolTip, Vector3? toolAxis)
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
            // the joints turn about the stand's local Z: that axis is constant in the wrist frame
            hingeLocal = Quaternion.Inverse(wrist.rotation) * (stand.rotation * Vector3.forward);

            if (toolTip.HasValue && toolAxis.HasValue && toolAxis.Value.sqrMagnitude > 1e-6f)
            {
                tipLocal = toolTip.Value;
                axisLocal = toolAxis.Value.normalized;
            }
            else
            {
                // estimate: lowest point of the tool meshes, axis from the wrist to it
                Vector3 low = wrist.position + Vector3.down * .3f;
                float best = float.MaxValue;
                foreach (Renderer r in wrist.GetComponentsInChildren<Renderer>())
                    if (r.bounds.min.y < best) { best = r.bounds.min.y; low = new Vector3(r.bounds.center.x, r.bounds.min.y, r.bounds.center.z); }
                tipLocal = Quaternion.Inverse(wrist.rotation) * (low - wrist.position);
                axisLocal = tipLocal.sqrMagnitude > 1e-6f ? tipLocal.normalized : Vector3.down;
            }

            var grip = new GameObject("Packing Grip").transform;
            grip.SetParent(wrist, false);
            grip.localPosition = tipLocal;
            Grip = grip;
            Vector3 fwd = stand.rotation * Vector3.right;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            Home = arm1Base.position + fwd * (.55f * (upper + fore)) + Vector3.down * .15f;
            goal = Home;
            wristPos = target.position;
            Ready = true;
            Description = "IK arm (" + ik.GetType().Name + ") upper " + upper.ToString("F2") + " m, fore " + fore.ToString("F2") +
                          " m, gripping cylinder end at " + tipLocal.ToString("F3") + " (wrist frame), " +
                          (toolTip.HasValue ? "measured" : "estimated");
            return true;
        }

        /// <summary>Stand rotation the IK gives for a wrist target (same convention as the IK component).</summary>
        private Quaternion StandFor(Vector3 wristTarget)
        {
            Vector3 d = wristTarget - stand.position;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg - 90f;
            return Quaternion.Euler(0f, yaw, 0f);
        }

        /// <summary>Wrist orientation with the cylinder pointing straight down, for a given stand rotation.</summary>
        private Quaternion WristRotation(Quaternion standRot)
        {
            Quaternion local = Quaternion.LookRotation(axisLocal, hingeLocal);
            Quaternion world = Quaternion.LookRotation(Vector3.down, standRot * Vector3.forward);
            return world * Quaternion.Inverse(local);
        }

        /// <summary>Wrist position that puts the cylinder end on <paramref name="tip"/> (the yaw depends on the wrist: iterate).</summary>
        private Vector3 WristFor(Vector3 tip)
        {
            Vector3 w = tip - Vector3.down * tipLocal.magnitude;
            for (int i = 0; i < 4; i++) w = tip - WristRotation(StandFor(w)) * tipLocal;
            return w;
        }

        public bool CanReach(Vector3 tip)
        {
            if (!Ready) return false;
            Vector3 w = WristFor(tip);
            Vector3 shoulder = stand.position + StandFor(w) * arm1BaseInStand;
            return Vector3.Distance(w, shoulder) <= (upper + fore) * .985f;
        }

        public void Tick(float dt)
        {
            if (!Ready) return;
            Vector3 want = WristFor(goal);
            wristPos = Vector3.MoveTowards(wristPos, want, Speed * dt);
            target.position = wristPos;
        }

        /// <summary>After the IK has run: cylinder straight down.</summary>
        public void LateTick()
        {
            if (!Ready) return;
            wrist.rotation = WristRotation(stand.rotation);
        }
    }
}
