using UnityEngine;
using UnityEngine.EventSystems;

namespace RoboFactory.HUD
{
    /// <summary>
    /// HUD keyboard input and EventSystem module, compatible with the old
    /// input manager (current project: "Both") as well as with the Input System alone.
    /// </summary>
    public static class HudInput
    {
        public static bool KeyDown(KeyCode key)
        {
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            return Input.GetKeyDown(key);
#else
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return false;
            switch (key)
            {
                case KeyCode.F1: return kb.f1Key.wasPressedThisFrame;
                case KeyCode.F2: return kb.f2Key.wasPressedThisFrame;
                case KeyCode.F3: return kb.f3Key.wasPressedThisFrame;
                case KeyCode.F4: return kb.f4Key.wasPressedThisFrame;
                case KeyCode.H: return kb.hKey.wasPressedThisFrame;
                case KeyCode.M: return kb.mKey.wasPressedThisFrame;
                case KeyCode.N: return kb.nKey.wasPressedThisFrame;
                case KeyCode.C: return kb.cKey.wasPressedThisFrame;
                case KeyCode.LeftArrow: return kb.leftArrowKey.wasPressedThisFrame;
                case KeyCode.RightArrow: return kb.rightArrowKey.wasPressedThisFrame;
                case KeyCode.UpArrow: return kb.upArrowKey.wasPressedThisFrame;
                case KeyCode.DownArrow: return kb.downArrowKey.wasPressedThisFrame;
                default: return false;
            }
#endif
        }

        /// <summary>Mouse position on screen (pixels).</summary>
        public static Vector2 MousePosition()
        {
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            return Input.mousePosition;
#else
            var m = UnityEngine.InputSystem.Mouse.current;
            return m != null ? m.position.ReadValue() : Vector2.zero;
#endif
        }

        /// <summary>Left or right button held (orbit rotation).</summary>
        public static bool MouseHeld()
        {
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            return Input.GetMouseButton(0) || Input.GetMouseButton(1);
#else
            var m = UnityEngine.InputSystem.Mouse.current;
            return m != null && (m.leftButton.isPressed || m.rightButton.isPressed);
#endif
        }

        /// <summary>Wheel: only the sign is useful (+ = forward).</summary>
        public static float ScrollY()
        {
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            return Input.mouseScrollDelta.y;
#else
            var m = UnityEngine.InputSystem.Mouse.current;
            return m != null ? m.scroll.ReadValue().y : 0f;
#endif
        }

        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null || Object.FindFirstObjectByType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem (Muse HUD)");
            es.AddComponent<EventSystem>();
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            es.AddComponent<StandaloneInputModule>();
#else
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#endif
        }
    }
}
