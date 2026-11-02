using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.Packing
{
    /// <summary>
    /// Closed conveyor loop (polyline with rounded corners) on which the products ride. Positions are given as a
    /// distance along the loop. The belt is one piece: when it stops (an item waits at the pick point), every
    /// item stops; items keep a minimum gap and never overlap.
    /// </summary>
    public sealed class ConveyorTrack
    {
        public float Speed = .7f;
        public float MinGap = .32f;
        public bool Running = true;

        private readonly List<Vector3> points = new List<Vector3>();
        private readonly List<float> cumulative = new List<float>();
        public float Length { get; private set; }

        public sealed class Rider
        {
            public PackingItem Item;
            public float S;          // distance along the loop
            public float Travelled;  // total distance since it was spawned (laps)
        }

        public readonly List<Rider> Riders = new List<Rider>();

        /// <summary>Corners in order of travel (closed loop), belt surface height y, corner radius.</summary>
        public ConveyorTrack(IList<Vector3> corners, float cornerRadius, int arcSteps = 6)
        {
            int n = corners.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 prev = corners[(i - 1 + n) % n], c = corners[i], next = corners[(i + 1) % n];
                Vector3 dIn = (c - prev).normalized, dOut = (next - c).normalized;
                float r = Mathf.Min(cornerRadius, .45f * Vector3.Distance(prev, c), .45f * Vector3.Distance(c, next));
                Vector3 a = c - dIn * r, b = c + dOut * r;
                for (int k = 0; k <= arcSteps; k++)
                {
                    float t = k / (float)arcSteps;
                    // quadratic Bezier through the corner: smooth, tangent to both straight runs
                    Vector3 p = (1 - t) * (1 - t) * a + 2 * (1 - t) * t * c + t * t * b;
                    points.Add(p);
                }
            }
            cumulative.Add(0f);
            for (int i = 1; i <= points.Count; i++)
                cumulative.Add(cumulative[i - 1] + Vector3.Distance(points[i - 1], points[i % points.Count]));
            Length = cumulative[cumulative.Count - 1];
        }

        public Vector3 Evaluate(float s, out Vector3 forward)
        {
            s = Wrap(s);
            int lo = 0, hi = cumulative.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (cumulative[mid] <= s) lo = mid; else hi = mid;
            }
            Vector3 a = points[lo % points.Count], b = points[(lo + 1) % points.Count];
            float seg = cumulative[lo + 1] - cumulative[lo];
            float t = seg > 1e-5f ? (s - cumulative[lo]) / seg : 0f;
            forward = (b - a).sqrMagnitude > 1e-8f ? (b - a).normalized : Vector3.forward;
            return Vector3.Lerp(a, b, t);
        }

        /// <summary>Distance along the loop of the point closest to a world position.</summary>
        public float Project(Vector3 world)
        {
            float best = float.MaxValue, bestS = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 a = points[i], b = points[(i + 1) % points.Count];
                Vector3 ab = b - a;
                float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(world - a, ab) / ab.sqrMagnitude) : 0f;
                float d = (a + ab * t - world).sqrMagnitude;
                if (d < best) { best = d; bestS = cumulative[i] + t * ab.magnitude; }
            }
            return bestS;
        }

        public float Wrap(float s)
        {
            if (Length <= 0f) return 0f;
            s %= Length;
            return s < 0f ? s + Length : s;
        }

        /// <summary>Distance travelling forward from a to b (0..Length).</summary>
        public float Ahead(float a, float b) => Wrap(b - a);

        public bool IsFree(float s, float gap)
        {
            foreach (Rider r in Riders)
                if (Mathf.Min(Ahead(r.S, s), Ahead(s, r.S)) < gap) return false;
            return true;
        }

        public Rider Add(PackingItem item, float s)
        {
            var r = new Rider { Item = item, S = Wrap(s) };
            Riders.Add(r);
            Place(r);
            return r;
        }

        public void Remove(PackingItem item)
        {
            Riders.RemoveAll(r => r.Item == item);
        }

        /// <summary>
        /// Moves the belt. <paramref name="stopAt"/>: distance where an item must stop (pick point), or a negative
        /// value. Returns the rider that reached the stop point, if any.
        /// </summary>
        public Rider Step(float dt, float stopAt, System.Func<Rider, bool> mustStop)
        {
            if (!Running || Riders.Count == 0) return null;
            float step = Speed * dt;
            // the belt stops as a whole as soon as an item to be handled reaches the stop point
            if (stopAt >= 0f)
                foreach (Rider r in Riders)
                {
                    float toStop = Ahead(r.S, stopAt);
                    if (toStop <= step + 1e-4f && toStop < Length * .5f && mustStop(r))
                    {
                        float move = toStop;
                        foreach (Rider o in Riders) { o.S = Wrap(o.S + move); o.Travelled += move; Place(o); }
                        r.S = stopAt;
                        Place(r);
                        Running = false;
                        return r;
                    }
                }
            foreach (Rider r in Riders) { r.S = Wrap(r.S + step); r.Travelled += step; Place(r); }
            return null;
        }

        private void Place(Rider r)
        {
            if (r.Item == null) return;
            Vector3 p = Evaluate(r.S, out Vector3 f);
            r.Item.transform.position = p;
            Quaternion yaw = Quaternion.LookRotation(f, Vector3.up);
            // keep the item's own tilt (broken items lie tilted)
            Vector3 e = r.Item.transform.localEulerAngles;
            r.Item.transform.rotation = yaw * Quaternion.Euler(0f, 0f, e.z > 180f ? e.z - 360f : e.z);
        }
    }
}
