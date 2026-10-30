using System.Collections;
using UnityEngine;

namespace RoboFactory.Hackathon
{
    /// <summary>
    /// Motion boundary between the RoboFactory pick logic and a real industrial arm.
    /// The simulation keeps deciding *what* to pick and *where* to put it; the driver owns
    /// the kinematics, the joint limits and the gripper of the physical robot.
    /// </summary>
    public interface IArmDriver
    {
        /// <summary>Transform the grasped piece is attached to (the real tool centre point).</summary>
        Transform GraspAnchor { get; }

        /// <summary>True when the tool centre point has reached the commanded position.</summary>
        bool Converged { get; }

        /// <summary>Distance in meters between the tool centre point and the last commanded point.</summary>
        float LastError { get; }

        /// <summary>True while the real gripper jaws are still travelling.</summary>
        bool GripperMoving { get; }

        /// <summary>Moves the real arm to a world position and waits for the joints to settle.</summary>
        IEnumerator MoveTo(Vector3 worldTarget);

        /// <summary>Opens or closes the real gripper.</summary>
        void SetGripper(bool closed);

        /// <summary>Opens the gripper and releases the held piece.</summary>
        void Release();

        /// <summary>Estimated travel time in seconds to a world point, used to predict moving targets.</summary>
        float EstimateSeconds(Vector3 worldTarget);
    }
}
