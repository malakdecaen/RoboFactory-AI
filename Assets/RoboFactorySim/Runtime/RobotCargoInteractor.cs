using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Pick / drop of a box by a robot of the public version.
    ///
    /// The RoboFactory pilots (WarehouseRobotAutopilot, TruckLoaderAgent) send "TryInteract" and then wait
    /// for IsBusy / HasBox, exactly as with the robot of the full project. This component answers the same
    /// way and takes the same time: each gesture lasts as long as the robot's arm animation in the full
    /// project (load / unload, per level), the box is attached / released at the same moment of the gesture,
    /// and nothing happens if the robot is not docked on a storage access point.
    /// Same durations = same timeline, so a recorded scenario replays the same way in both projects.
    /// </summary>
    [DisallowMultipleComponent]
    public class RobotCargoInteractor : MonoBehaviour
    {
        public GridRobotController gridController;

        [Tooltip("Position of the carried box (its rotation is not used).")]
        public Transform cargoAttachPoint;

        [Header("Gesture timing (seconds, per level 0 / 1 / 2)")]
        public float[] loadSeconds = { 38f / 30f, 65f / 30f, 67f / 30f };
        [Tooltip("Moment of the load gesture (0..1) when the box leaves the storage.")]
        public float[] loadAttachAt = { .466f, .425f, .488f };
        public float[] unloadSeconds = { 38f / 30f, 55f / 30f, 75f / 30f };
        [Tooltip("Moment of the unload gesture (0..1) when the box is put down.")]
        public float[] unloadDetachAt = { .565f, .580f, .564f };

        public bool showDebugLogs;

        private ShelfInteractionZone currentZone;
        // Read by the RoboFactory pilots (same name as on the full project's robot).
        private Transform activeCarryRoot;
        private CargoBox carriedBox;
        private bool isBusy;
        private readonly List<ShelfInteractionZone> allZones = new List<ShelfInteractionZone>();

        public bool HasBox => carriedBox != null;
        public bool IsBusy => isBusy;

        private void Awake()
        {
            if (gridController == null) gridController = GetComponent<GridRobotController>();
            if (cargoAttachPoint == null) cargoAttachPoint = transform.Find("cargo_attach");
            var root = new GameObject("StableCargoRoot").transform;
            root.SetParent(transform, false);
            root.localPosition = cargoAttachPoint != null ? transform.InverseTransformPoint(cargoAttachPoint.position)
                                                          : new Vector3(0f, .75f, 0f);
            root.localRotation = Quaternion.identity;
            activeCarryRoot = root;
        }

        private void OnTriggerEnter(Collider other)
        {
            ShelfInteractionZone zone = other.GetComponent<ShelfInteractionZone>();
            if (zone != null) currentZone = zone;
        }

        private void OnTriggerExit(Collider other)
        {
            ShelfInteractionZone zone = other.GetComponent<ShelfInteractionZone>();
            if (zone != null && zone == currentZone) currentZone = null;
        }

        /// <summary>Called by the pilots through SendMessage("TryInteract").</summary>
        private void TryInteract()
        {
            if (isBusy) return;
            ShelfInteractionZone zone = DockedZone();
            if (zone == null || zone.shelf == null)
            {
                Log("not docked on a storage access point");
                return;
            }
            int column = zone.columnIndex;
            if (carriedBox == null)
            {
                if (!zone.shelf.HasBoxToTake(column, out int level, out CargoBox box)) { Log("no box to take"); return; }
                StartCoroutine(Load(zone.shelf, column, level, box));
            }
            else
            {
                if (!zone.shelf.HasFreePlaceToPut(column, out int level)) { Log("no free place"); return; }
                StartCoroutine(Unload(zone.shelf, column, level));
            }
        }

        /// <summary>The access point the robot is docked on (trigger first, then the closest docked one).</summary>
        private ShelfInteractionZone DockedZone()
        {
            if (currentZone != null && currentZone.IsRobotCorrectlyDocked(transform)) return currentZone;
            if (allZones.Count == 0 || allZones.Contains(null))
            {
                allZones.Clear();
                allZones.AddRange(FindObjectsByType<ShelfInteractionZone>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            }
            foreach (ShelfInteractionZone zone in allZones)
                if (zone != null && zone.isActiveAndEnabled && zone.IsRobotCorrectlyDocked(transform)) return zone;
            return null;
        }

        private IEnumerator Load(ShelfStorage shelf, int column, int level, CargoBox box)
        {
            isBusy = true;
            Lock(true);
            float duration = Pick(loadSeconds, level, 38f / 30f);
            float at = Mathf.Clamp01(Pick(loadAttachAt, level, .466f));
            yield return new WaitForSeconds(duration * at);
            if (box != null)
            {
                shelf.RemoveBox(column, level);
                Quaternion rotation = box.transform.rotation;
                box.transform.SetParent(activeCarryRoot, true);
                box.transform.SetPositionAndRotation(activeCarryRoot.position, rotation);
                box.isCarried = true;
                carriedBox = box;
            }
            yield return new WaitForSeconds(duration * (1f - at));
            isBusy = false;
            Lock(false);
        }

        private IEnumerator Unload(ShelfStorage shelf, int column, int level)
        {
            isBusy = true;
            Lock(true);
            float duration = Pick(unloadSeconds, level, 38f / 30f);
            float at = Mathf.Clamp01(Pick(unloadDetachAt, level, .565f));
            yield return new WaitForSeconds(duration * at);
            if (carriedBox != null)
            {
                shelf.PutBox(column, level, carriedBox, carriedBox.transform.rotation);
                carriedBox = null;
            }
            yield return new WaitForSeconds(duration * (1f - at));
            isBusy = false;
            Lock(false);
        }

        private static float Pick(float[] values, int level, float fallback)
        {
            if (values == null || values.Length == 0) return fallback;
            return values[Mathf.Clamp(level, 0, values.Length - 1)];
        }

        private void Lock(bool value)
        {
            if (gridController != null) gridController.controlsLocked = value;
        }

        private void Log(string message)
        {
            if (showDebugLogs) Debug.Log("[Sim] " + name + ": " + message);
        }
    }
}
