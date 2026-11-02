using UnityEngine;

namespace RoboFactory.Packing
{
    /// <summary>
    /// Receives the animation events of the warehouse robot model's load / unload clips (the same events the
    /// robot's own cargo script receives) on the transport robot R3, whose scripts were removed.
    /// </summary>
    public sealed class PackingRobotEvents : MonoBehaviour
    {
        public bool Attached { get; private set; }
        public bool Detached { get; private set; }
        public bool Finished { get; private set; }

        public void Clear() { Attached = Detached = Finished = false; }

        // names used by the animation events of the robot model
        public void AttachBoxToRobot() { Attached = true; }
        public void DetachBoxToShelf() { Detached = true; }
        public void OnCargoAnimationFinished() { Finished = true; }
        public void ForceBoxToCarryPose() { }
    }
}
