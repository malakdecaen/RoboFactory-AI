using System.Collections;
using UnityEngine;

namespace RoboFactory.Hackathon
{
    /// <summary>
    /// Load/unload boundary between the RoboFactory missions and a real mobile robot.
    /// The mission decides when to take or release a carton; the driver plays the real
    /// loading animation and attaches the carton at the animation event, so the physical
    /// attachment always matches the machine's motion.
    /// </summary>
    public interface IMobileCargoDriver
    {
        bool Busy { get; }
        bool HasCargo { get; }
        Transform CargoPoint { get; }

        /// <summary>Takes the carton with the real load animation.</summary>
        IEnumerator Load(GameObject carton);

        /// <summary>Releases the carton with the real unload animation into a parent (null = world).</summary>
        IEnumerator Unload(GameObject carton, Transform parent, Vector3 localPosition, float localScale);
    }
}
