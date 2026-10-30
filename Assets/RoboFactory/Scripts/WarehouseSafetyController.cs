using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.Hackathon
{
    public sealed class WarehouseSafetyController
    {
        public float SafetyMargin = 0.35f;
        public float PredictionHorizon = 2f;
        public float BrakingDeceleration = 1.5f;
        public float MaxDeceleration = 3f;
        public float StopGap = 0.05f;
        public float SampleStep = 0.1f;
        public Bounds Band = new Bounds(new Vector3(5.6f, 1f, 0f), new Vector3(6.8f, 4f, 2f));

        public float SpeedLimit(float conflictDistance, float cruiseSpeed)
        {
            if (float.IsPositiveInfinity(conflictDistance)) return cruiseSpeed;
            return Mathf.Min(cruiseSpeed, Mathf.Sqrt(2f * BrakingDeceleration * Mathf.Max(0f, conflictDistance - StopGap)));
        }

        public float FirstConflictDistance(Bounds robotShape, Vector3 robotPosition, IReadOnlyList<Vector3> path, int nextIndex, float cruiseSpeed, IReadOnlyList<PedestrianAgent> pedestrians)
        {
            float lookahead = cruiseSpeed * PredictionHorizon;
            Vector3 previous = robotPosition;
            float travelled = 0f;
            for (int i = nextIndex; i < path.Count; i++)
            {
                Vector3 segment = path[i] - previous;
                float length = segment.magnitude;
                for (float s = 0f; ; s += SampleStep)
                {
                    float along = Mathf.Min(s, length);
                    float distance = travelled + along;
                    if (distance > lookahead) return float.PositiveInfinity;
                    Vector3 sample = length > 1e-4f ? previous + segment * (along / length) : previous;
                    if (ConflictAt(robotShape, robotPosition, sample, pedestrians)) return distance;
                    if (along >= length) break;
                }
                travelled += length;
                previous = path[i];
            }
            return float.PositiveInfinity;
        }

        public Vector3 RetreatDirection(Bounds robotShape, Vector3 robotPosition, IReadOnlyList<PedestrianAgent> pedestrians)
        {
            Vector3 escape = Vector3.zero;
            foreach (PedestrianAgent pedestrian in pedestrians)
            {
                if (BoxGap(robotShape, pedestrian.SweptBounds(PredictionHorizon)) >= 0f) continue;
                Vector3 away = robotPosition - pedestrian.BodyBounds.center;
                away.y = 0f;
                if (away.sqrMagnitude > 1e-6f) escape += away.normalized;
            }
            return escape.sqrMagnitude > 1e-6f ? escape.normalized : Vector3.zero;
        }

        public static float BoxGap(Bounds a, Bounds b)
        {
            float x = Mathf.Abs(a.center.x - b.center.x) - (a.extents.x + b.extents.x);
            float y = Mathf.Abs(a.center.y - b.center.y) - (a.extents.y + b.extents.y);
            float z = Mathf.Abs(a.center.z - b.center.z) - (a.extents.z + b.extents.z);
            float deepest = Mathf.Max(x, Mathf.Max(y, z));
            if (deepest < 0f) return deepest;
            return new Vector3(Mathf.Max(x, 0f), Mathf.Max(y, 0f), Mathf.Max(z, 0f)).magnitude;
        }

        public static Bounds Shift(Bounds bounds, Vector3 offset)
        {
            return new Bounds(bounds.center + offset, bounds.size);
        }

        public static Bounds Inflate(Bounds bounds, float margin)
        {
            Bounds inflated = bounds;
            inflated.Expand(new Vector3(2f * margin, 0f, 2f * margin));
            return inflated;
        }

        public static Bounds ColliderBounds(Collider collider)
        {
            // World AABB: correct for rotated colliders (the Stage 6 cell is rotated inside the hall).
            if (collider == null) return new Bounds(Vector3.zero, Vector3.zero);
            return collider.bounds;
        }

        private bool ConflictAt(Bounds robotShape, Vector3 robotPosition, Vector3 sample, IReadOnlyList<PedestrianAgent> pedestrians)
        {
            Bounds claim = Inflate(Shift(robotShape, sample - robotPosition), SafetyMargin);
            if (BoxGap(claim, Band) < 0f) return false;
            foreach (PedestrianAgent pedestrian in pedestrians)
                if (BoxGap(claim, pedestrian.SweptBounds(PredictionHorizon)) < 0f) return true;
            return false;
        }
    }
}
