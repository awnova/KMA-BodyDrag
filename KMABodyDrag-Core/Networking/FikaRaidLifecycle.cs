//====================[ Imports ]====================
using Comfort.Common;
using EFT;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;

namespace KMABodyDrag.Networking
{
    //====================[ FikaRaidLifecycle ]====================
    // Fika only (called when FikaBridge.Present): packet registration when Fika's network manager is made, the raid boundaries
    // from Fika's events (RaidLifecycle), and the pose patch on Fika's remote players.
    internal static class FikaRaidLifecycle
    {
        //====================[ Init ]====================
        // Event subscriptions are installed once for the plugin lifetime.
        private static bool _initialized;

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(e => CorpsePacketHandlers.Register(e.Manager));
            FikaEventDispatcher.SubscribeEvent<GameWorldStartedEvent>(e => RaidLifecycle.OnRaidStarted(e.GameWorld, "game-world-started"));
            FikaEventDispatcher.SubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(e => RaidLifecycle.OnRaidEnded("game-ended"));

            new ObservedPlayerPosePatch().Enable();

            Plugin.LogSource.LogInfo("Fika integration initialized!");
        }

        //====================[ Raid Boundaries ]====================
        private static void OnRaidStarted(FikaRaidStartedEvent e)
        {
            if (Singleton<GameWorld>.Instantiated) RaidLifecycle.OnRaidStarted(Singleton<GameWorld>.Instance, "raid-started");
        }
    }
}
