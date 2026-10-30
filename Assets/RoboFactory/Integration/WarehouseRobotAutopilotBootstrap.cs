using UnityEngine;
using UnityEngine.SceneManagement;
using RoboFactory.Sim;

namespace RoboFactory.Integration
{
    /// <summary>
    /// Installs the automated pilot when the Warcool demo scene starts, so pressing Play is enough:
    /// no menu, no missing component, no disabled object.
    /// </summary>
    public static class WarehouseRobotAutopilotBootstrap
    {
        private const string SceneName = "warehouse_robot_demo";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!string.Equals(scene.name, SceneName, System.StringComparison.OrdinalIgnoreCase)) return;

            GridRobotController controller = null;
            foreach (GridRobotController candidate in Object.FindObjectsByType<GridRobotController>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                // The truck loading robot has its own pilot: no autopilot.
                if (candidate.GetComponent<RoboFactory.Logistics.TruckLoaderAgent>() != null) continue;
                controller = candidate;
                break;
            }
            if (controller == null)
            {
                Debug.LogWarning("[Autopilot] Warcool scene detected but no GridRobotController available.");
                return;
            }

            WarehouseRobotAutopilot pilot = controller.GetComponent<WarehouseRobotAutopilot>();
            if (pilot == null)
            {
                pilot = controller.gameObject.AddComponent<WarehouseRobotAutopilot>();
                Debug.Log("[Autopilot] component added automatically on " + controller.name);
            }

            pilot.Controller = controller;
            if (pilot.Animator == null) pilot.Animator = controller.GetComponent<Animator>();
            if (pilot.Interactor == null) pilot.Interactor = controller.GetComponent<RobotCargoInteractor>();
            pilot.AutoStart = true;
            if (controller.GetComponent<WarehouseRobotVisionHud>() == null)
                controller.gameObject.AddComponent<WarehouseRobotVisionHud>();
            pilot.enabled = true;
            Debug.Log("[Autopilot] ready: AutoStart=" + pilot.AutoStart + " animator=" + (pilot.Animator != null) +
                      " interactor=" + (pilot.Interactor != null) + " active=" + controller.gameObject.activeInHierarchy);
        }
    }
}
