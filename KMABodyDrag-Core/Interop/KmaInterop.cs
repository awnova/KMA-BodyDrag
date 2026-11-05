//====================[ Imports ]====================
using System;
using System.Linq;
using BepInEx.Bootstrap;

namespace KMABodyDrag.Interop
{
    //====================[ KmaInterop ]====================
    // KeepMeAlive owns the local player's input while it runs one of its own modes (downed, its own drag, the team-heal wheel, a
    // revive hold): no corpse is picked up then, and one already held is let go the moment KeepMeAlive takes over (going down
    // drops the body). Bound once, by reflection, to KeepMeAlive's public interop API (KeepMeAlive.Interop.KeepMeAliveApi) into
    // typed delegates, so neither plugin references the other at compile time (as KMA-Wheel's KmaInterop). Fail-open: with
    // KeepMeAlive missing, without the API, on another API version, or when a bound call throws, the drag runs standalone.
    internal static class KmaInterop
    {
        //====================[ Constants ]====================
        // KeepMeAlive Solo and Fika have their own plugin GUIDs and share one interop API; only one is ever installed.
        internal const string SoloGuid = "com.keepmealive.solo";
        internal const string FikaGuid = "com.keepmealive.fika";
        private static readonly string[] KmaGuids = { SoloGuid, FikaGuid };
        private const string ApiTypeName = "KeepMeAlive.Interop.KeepMeAliveApi";
        // The API's major version this mod is built for; any other is refused.
        private const int SupportedApi = 1;
        private const string OwnerId = Plugin.PluginGuid + "/corpse-drag";

        //====================[ State ]====================
        private static Func<string> _busyReason;

        private static bool Active;

        //====================[ Init ]====================
        // From Plugin.Awake. The soft BepInDependencies on KmaGuids makes KeepMeAlive's Awake run first.
        public static void Init()
        {
            try
            {
                var info = KmaGuids.Select(g => Chainloader.PluginInfos.TryGetValue(g, out var i) ? i : null).FirstOrDefault(i => i != null);
                if (info == null || info.Instance == null)
                {
                    Plugin.LogSource.LogInfo("[Interop] KeepMeAlive not installed; running standalone.");
                    return;
                }

                // Its own assembly, not a scan of every loaded one.
                var api = info.Instance.GetType().Assembly.GetType(ApiTypeName);
                if (api == null)
                {
                    Plugin.LogSource.LogWarning($"[Interop] KeepMeAlive {info.Metadata.Version} has no interop API. Update it; " +
                        "until then a body is not dropped when you go down.");
                    return;
                }

                int version = (int)api.GetField("ApiVersion").GetRawConstantValue();
                if (version != SupportedApi)
                {
                    Plugin.LogSource.LogWarning($"[Interop] KeepMeAlive interop API v{version}, this mod needs v{SupportedApi}. Running standalone.");
                    return;
                }

                _busyReason = (Func<string>)Delegate.CreateDelegate(typeof(Func<string>), api.GetMethod("LocalBusyReason"));
                var register = (Action<string, Action<string>>)Delegate.CreateDelegate(
                    typeof(Action<string, Action<string>>), api.GetMethod("RegisterInputOwner"));
                register(OwnerId, Release);

                Active = true;
                Plugin.LogSource.LogInfo($"[Interop] bound to KeepMeAlive {info.Metadata.Version} (API v{version}).");
            }
            catch (Exception ex)
            {
                Active = false;
                Plugin.LogSource.LogError($"[Interop] binding failed, running standalone: {ex.Message}");
            }
        }

        // KeepMeAlive took the local player's input: the body is let go now. Must not throw back into KeepMeAlive.
        private static void Release(string reason)
        {
            try
            {
                if (!CorpseDragController.IsLocalDragging) return;
                DragLog.LogDebug($"[Interop] KeepMeAlive took over ({reason}): letting go");
                CorpseDragController.StopLocal(reason);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Interop] release ({reason}) failed: {ex.Message}");
            }
        }

        //====================[ Queries ]====================
        // Why KeepMeAlive owns input, or null. A throw turns the interop off for the session (fail-open), logged once.
        public static string BusyReason()
        {
            if (!Active) return null;
            try
            {
                return _busyReason();
            }
            catch (Exception ex)
            {
                Active = false;
                Plugin.LogSource.LogError($"[Interop] LocalBusyReason threw, interop off: {ex.Message}");
                return null;
            }
        }
    }
}
