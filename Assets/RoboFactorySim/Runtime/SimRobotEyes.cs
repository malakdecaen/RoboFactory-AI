using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>Eyes of the public robot: a quick blink every few seconds (as the robot of the full project).</summary>
    [DisallowMultipleComponent]
    public class SimRobotEyes : MonoBehaviour
    {
        public Transform[] eyes = new Transform[0];
        public Vector2 interval = new Vector2(2.5f, 6f);
        public float blinkSeconds = .14f;

        private float next, start = -1f;
        private Vector3[] scales;

        private void Awake()
        {
            scales = new Vector3[eyes.Length];
            for (int i = 0; i < eyes.Length; i++) if (eyes[i] != null) scales[i] = eyes[i].localScale;
            next = Time.time + Random.Range(interval.x, interval.y);
        }

        private void Update()
        {
            if (start < 0f && Time.time >= next) start = Time.time;
            if (start < 0f) return;
            float t = (Time.time - start) / Mathf.Max(.01f, blinkSeconds);
            // closes then opens: vertical scale 1 → 0.1 → 1
            float k = t < 1f ? Mathf.Lerp(1f, .1f, Mathf.Sin(t * Mathf.PI)) : 1f;
            for (int i = 0; i < eyes.Length; i++)
                if (eyes[i] != null) eyes[i].localScale = new Vector3(scales[i].x, scales[i].y * k, scales[i].z);
            if (t >= 1f)
            {
                start = -1f;
                next = Time.time + Random.Range(interval.x, interval.y);
            }
        }
    }
}
