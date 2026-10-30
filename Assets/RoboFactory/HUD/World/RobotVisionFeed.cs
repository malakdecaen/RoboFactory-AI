using UnityEngine;

namespace RoboFactory.HUD
{
    /// <summary>
    /// Real on-board camera of a robot rendered into a RenderTexture at a limited rate and
    /// resolution (the camera is only enabled on the frames where an image is due).
    /// Mounted as a child of the robot's Transform: it sees what the robot sees.
    /// </summary>
    public sealed class RobotVisionFeed
    {
        public Transform Robot { get; private set; }
        public Camera Camera { get; private set; }
        public RenderTexture Texture { get; private set; }
        public float MeasuredFps { get; private set; }
        public bool Active;

        public float EyeHeight = .9f;
        public float EyeForward = .22f;
        [Tooltip("Tilt towards the floor (degrees): framing of the floor and the targets in front of the robot.")]
        public float Pitch = 12f;
        public float FieldOfView = 66f;

        private float next;
        private int framesThisSecond;
        private float secondStart;
        private int width, height;

        public void Attach(Transform robot, int cullingMask)
        {
            if (robot == Robot && Camera != null) return;
            Release();
            Robot = robot;
            if (robot == null) return;
            var go = new GameObject("HUD Robot Vision Camera");
            go.transform.SetParent(robot, false);
            go.transform.localPosition = new Vector3(0f, EyeHeight, EyeForward);
            go.transform.localRotation = Quaternion.Euler(Pitch, 0f, 0f);
            Camera = go.AddComponent<Camera>();
            Camera.fieldOfView = FieldOfView;
            Camera.nearClipPlane = .05f;
            Camera.farClipPlane = 40f;
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = new Color(.03f, .05f, .08f);
            Camera.cullingMask = cullingMask;
            Camera.useOcclusionCulling = false;
            Camera.allowMSAA = false;
            Camera.allowHDR = false;
            Camera.depth = -50f;
            Camera.enabled = false;
        }

        public void SetResolution(int w, int h)
        {
            if (Camera == null || (w == width && h == height && Texture != null)) return;
            width = w;
            height = h;
            RenderTexture old = Texture;
            Texture = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32) { name = "Robot Vision " + Robot.name, antiAliasing = 1 };
            Texture.Create();
            Camera.targetTexture = Texture;
            if (old != null)
            {
                old.Release();
                Object.Destroy(old);
            }
        }

        /// <summary>Enables the camera only for the frames that are due (target fps).</summary>
        public void Tick(float targetFps)
        {
            if (Camera == null) return;
            if (!Active || targetFps <= 0f)
            {
                if (Camera.enabled) Camera.enabled = false;
                return;
            }
            float now = Time.unscaledTime;
            bool due = now >= next;
            Camera.enabled = due;
            if (due)
            {
                next = now + 1f / targetFps;
                framesThisSecond++;
            }
            if (now - secondStart >= 1f)
            {
                MeasuredFps = framesThisSecond / (now - secondStart);
                framesThisSecond = 0;
                secondStart = now;
            }
        }

        public void Release()
        {
            if (Camera != null) Object.Destroy(Camera.gameObject);
            Camera = null;
            if (Texture != null)
            {
                Texture.Release();
                Object.Destroy(Texture);
            }
            Texture = null;
            width = height = 0;
        }
    }
}
