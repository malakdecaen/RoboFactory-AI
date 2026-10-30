using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.Hackathon
{
    [Serializable]
    public sealed class PedestrianSpec
    {
        public string name = "Employee";
        public int priority = 1;
        public float speed = 1f;
        public float startDelay;
        public Vector3[] waypoints;
        public float[] pauseAtWaypoint;
    }

    public sealed class PedestrianAgent
    {
        private const float ReachCap = 0.6f;
        private readonly PedestrianSpec spec;
        private int target = 1;
        private float startDelay, pause;

        public PedestrianAgent(PedestrianSpec spec, Transform root, BoxCollider body, SphereCollider head)
        {
            this.spec = spec;
            Root = root;
            BodyCollider = body;
            HeadCollider = head;
            startDelay = spec.startDelay;
        }

        public string Name => spec.name;
        public int Priority => spec.priority;
        public float Speed => spec.speed;
        public Transform Root { get; }
        public BoxCollider BodyCollider { get; }
        public SphereCollider HeadCollider { get; }
        public Bounds BodyBounds => WarehouseSafetyController.ColliderBounds(BodyCollider);
        public Bounds HeadBounds => WarehouseSafetyController.ColliderBounds(HeadCollider);
        public Vector3 Velocity { get; private set; }
        public bool Yielding { get; private set; }

        public Bounds PredictedBody(float time) => Predict(BodyBounds, time);

        public Bounds PredictedHead(float time) => Predict(HeadBounds, time);

        public Bounds SweptBounds(float horizon)
        {
            Bounds swept = PredictedBody(0f);
            swept.Encapsulate(PredictedBody(horizon));
            swept.Encapsulate(PredictedHead(0f));
            swept.Encapsulate(PredictedHead(horizon));
            return swept;
        }

        public void Tick(float dt, IReadOnlyList<PedestrianAgent> all, float separation, float horizon, Bounds robotReach, System.Random random)
        {
            if (spec.waypoints == null || spec.waypoints.Length < 2) return;
            Vector3 before = Root.position;
            if (startDelay > 0f) startDelay -= dt;
            else if (pause > 0f) pause -= dt;
            else Walk(dt, all, separation, horizon, robotReach, random);
            Velocity = dt > 0f ? (Root.position - before) / dt : Vector3.zero;
        }

        private Bounds Predict(Bounds current, float time)
        {
            float allowance = Mathf.Min(spec.speed * time, ReachCap);
            Bounds predicted = WarehouseSafetyController.Shift(current, Velocity * time);
            predicted.Expand(new Vector3(2f * allowance, 0f, 2f * allowance));
            return predicted;
        }

        private void Walk(float dt, IReadOnlyList<PedestrianAgent> all, float separation, float horizon, Bounds robotReach, System.Random random)
        {
            Vector3 goal = spec.waypoints[target];
            Vector3 toGoal = new Vector3(goal.x - Root.position.x, 0f, goal.z - Root.position.z);
            float remaining = toGoal.magnitude;
            Vector3 direction = remaining > 1e-4f ? toGoal / remaining : Vector3.zero;
            float step = Mathf.Min(remaining, spec.speed * dt);
            Bounds next = WarehouseSafetyController.Shift(BodyBounds, direction * step);
            Bounds claim = WarehouseSafetyController.Inflate(next, separation);
            if (WarehouseSafetyController.BoxGap(claim, robotReach) < 0f || YieldsToHigherPriority(all, claim, horizon))
            {
                Yielding = true;
                return;
            }
            Yielding = false;
            Root.position += direction * step;
            if (remaining - step < 0.01f)
            {
                int reached = target;
                target = (target + 1) % spec.waypoints.Length;
                pause = PauseAt(reached, random);
            }
        }

        private bool YieldsToHigherPriority(IReadOnlyList<PedestrianAgent> all, Bounds claim, float horizon)
        {
            foreach (PedestrianAgent other in all)
            {
                if (other == this || other.Priority >= Priority) continue;
                if (WarehouseSafetyController.BoxGap(claim, other.SweptBounds(horizon)) < 0f) return true;
            }
            return false;
        }

        private float PauseAt(int index, System.Random random)
        {
            if (spec.pauseAtWaypoint != null && index < spec.pauseAtWaypoint.Length) return spec.pauseAtWaypoint[index];
            return 1f + (float)random.NextDouble() * 1.9f;
        }
    }

    /// <summary>Un groupe d'employés suivis par la sécurité (ex. EmployeeCrew).</summary>
    public interface IPedestrianSource
    {
        IReadOnlyList<PedestrianAgent> Agents { get; }
    }

    /// <summary>
    /// Registre des groupes d'employés actifs : l'orchestrateur mesure la distance robot / employé
    /// sans dépendre d'une scène particulière.
    /// </summary>
    public static class PedestrianSources
    {
        private static readonly List<IPedestrianSource> all = new List<IPedestrianSource>();

        public static IReadOnlyList<IPedestrianSource> All => all;

        public static void Register(IPedestrianSource source)
        {
            if (source != null && !all.Contains(source)) all.Add(source);
        }

        public static void Unregister(IPedestrianSource source) { all.Remove(source); }
    }
}
