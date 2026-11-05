//====================[ Imports ]====================
using EFT;

namespace KMABodyDrag.Networking
{
    //====================[ RaidLifecycle ]====================
    // A clean slate at each raid boundary. Fed by Fika's events when Fika is installed (FikaRaidLifecycle), else by the
    // single-player raid patches (SoloRaidPatches). Names no Fika type.
    internal static class RaidLifecycle
    {
        //====================[ Raid Boundaries ]====================
        // A clean slate at raid start, even if the previous raid ended without an end event (crash/disconnect). Fika raises
        // FikaRaidStartedEvent and GameWorldStartedEvent in either order; whichever comes first resets, once per game world.
        // Corpses are registered as they are made (CorpseInitPatch), so a reset on the later event would forget any made between.
        private static GameWorld _resetFor;

        public static void OnRaidStarted(GameWorld world, string reason)
        {
            if (world == null || ReferenceEquals(world, _resetFor)) return;
            _resetFor = world;
            Reset(reason);
        }

        public static void OnRaidEnded(string reason)
        {
            // Both solo end hooks (BaseLocalGame.Stop, then GameWorld.Dispose) may fire: one reset is enough.
            if (_resetFor == null) return;
            Reset(reason);
            _resetFor = null;
        }

        private static void Reset(string reason)
        {
            DragLog.LogDebug($"[Raid] reset ({reason})");
            BodyDragDriver.Ensure();
            CorpseDragController.Clear();
            BodyAnimation.ClearAll();
            CorpseClaims.Clear();
            CorpseRegistry.Clear();
            CorpseInteraction.Clear();
        }
    }
}
