//====================[ Imports ]====================
using BepInEx.Logging;

namespace KMABodyDrag.Presentation
{
    //====================[ DragLog ]====================
    // KeepMeAlive's RevivalDebugLog cut down. There is no server switch here: the debug lines (and the hand traces) are on when
    // BepInEx writes Debug to its log file (BepInEx.cfg, [Logging.Disk] LogLevels), read once.
    internal static class DragLog
    {
        //====================[ Flags ]====================
        public static bool IsDebugLogsEnabled => _enabled ??= ReadLevel();

        private static bool? _enabled;

        private static bool ReadLevel()
        {
            foreach (var listener in Logger.Listeners)
                if (listener is DiskLogListener disk && (disk.DisplayedLogLevel & LogLevel.Debug) != 0) return true;
            return false;
        }

        //====================[ Logging API ]====================
        public static void LogDebug(string message)
        {
            if (!IsDebugLogsEnabled) return;
            Plugin.LogSource.LogDebug(message);
        }

        // Network events, as KeepMeAlive's LogNetworkTrace: info level, so they show without Debug enabled on the console filter.
        public static void LogNetworkTrace(string message)
        {
            if (!IsDebugLogsEnabled) return;
            Plugin.LogSource.LogInfo(message);
        }
    }
}
