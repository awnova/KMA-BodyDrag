//====================[ Imports ]====================
using UnityEngine;

namespace KMABodyDrag.Animation.Hands
{
    // Logs the positions of mod-controlled hands for comparison with their baked animation data.
    //====================[ HandTrace ]====================
    internal static class HandTrace
    {
        private const bool Enabled = false;
        // Seconds between the per-frame lines of one trace.
        public const float Interval = 0.5f;

        public static bool On => Enabled || DragLog.IsDebugLogsEnabled;

        public static void Log(string message)
        {
            if (On) Plugin.LogSource.LogInfo("[HandTrace] " + message);
        }

        public static string V(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

        // Metres to whole centimetres, for readable lines.
        public static string Cm(float metres) => $"{metres * 100f:F0}cm";
    }
}
