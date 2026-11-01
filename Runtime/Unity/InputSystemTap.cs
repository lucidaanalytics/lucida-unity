#if UNITY_5_3_OR_NEWER
using System;
using System.Reflection;
using UnityEngine;

namespace Lucida
{
    /// <summary>
    /// First press of the primary pointer (mouse or touch) from the new Input System package, read by reflection
    /// (<c>Pointer.current.press.wasPressedThisFrame</c> and <c>position.ReadValue()</c>) so the package is not a
    /// dependency of this one. Returns false when the package is not installed or has no pointer.
    /// </summary>
    static class InputSystemTap
    {
        static bool resolved;
        static PropertyInfo current, press, position, wasPressed;
        static MethodInfo readPosition;

        static void Resolve()
        {
            resolved = true;
            try
            {
                var pointer = Type.GetType("UnityEngine.InputSystem.Pointer, Unity.InputSystem");
                if (pointer == null) return;
                current = pointer.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                press = pointer.GetProperty("press");
                position = pointer.GetProperty("position");
                wasPressed = press?.PropertyType.GetProperty("wasPressedThisFrame");
                readPosition = position?.PropertyType.GetMethod("ReadValue", Type.EmptyTypes);
            }
            catch (Exception) { current = null; }
        }

        public static bool TapDown(out Vector3 p)
        {
            p = default;
            if (!resolved) Resolve();
            if (current == null || wasPressed == null || readPosition == null) return false;
            try
            {
                var pointer = current.GetValue(null);
                if (pointer == null) return false;
                if (!(bool)wasPressed.GetValue(press.GetValue(pointer))) return false;
                var v = (Vector2)readPosition.Invoke(position.GetValue(pointer), null);
                p = new Vector3(v.x, v.y, 0);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
#endif
