using UnityEngine;
using UnityEngine.SceneManagement;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Start-up of the public cube scene, matching what the full project gets from its robot prefab and
    /// from WarehouseRobotAutopilotBootstrap:
    ///  - each robot has a RobotCargoInteractor handed to its pilot (also for scenes generated before it existed),
    ///    so the pick / drop gestures take the same time as in the full project;
    ///  - R1 has its vision window (WarehouseRobotVisionHud).
    /// </summary>
    public static class SimRobotSetup
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            // also after a scene reload (REPLAY / F5): sceneLoaded runs after Awake and before Start
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) { Install(); }

        private static void Install()
        {
            foreach (GridRobotController robot in Object.FindObjectsByType<GridRobotController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                RobotCargoInteractor interactor = robot.GetComponent<RobotCargoInteractor>();
                if (interactor == null)
                {
                    interactor = robot.gameObject.AddComponent<RobotCargoInteractor>();
                    interactor.gridController = robot;
                    Debug.Log("[Sim] cargo interactor added on " + robot.name);
                }
                var pilot = robot.GetComponent<RoboFactory.Integration.WarehouseRobotAutopilot>();
                if (pilot != null && pilot.Interactor == null) pilot.Interactor = interactor;
                // Like the full project's start-up (WarehouseRobotAutopilotBootstrap, which only knows the asset
                // scene): R1 gets its vision window, used by the HUD route display and the replan on a blocked route.
                // AutoStart is NOT touched: the orchestrator starts the pilots in directed mode.
                if (pilot != null && robot.GetComponent<RoboFactory.Logistics.TruckLoaderAgent>() == null &&
                    robot.GetComponent<RoboFactory.Integration.WarehouseRobotVisionHud>() == null)
                    robot.gameObject.AddComponent<RoboFactory.Integration.WarehouseRobotVisionHud>();
                var loader = robot.GetComponent<RoboFactory.Logistics.TruckLoaderAgent>();
                if (loader != null && loader.Interactor == null) loader.Interactor = interactor;
            }
        }
    }
}
