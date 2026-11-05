//====================[ Imports ]====================
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KMABodyDrag.Patches
{
    //====================[ Solo Raid Patches ]====================
    // The raid boundaries without Fika (base SPT), the same hooks KeepMeAlive's single-player build uses. Enabled only when Fika
    // is not installed; with Fika its events say the same (FikaRaidLifecycle).

    // TarkovApplication.LocalGameCreate calls GameWorld.OnGameStarted once the local game is running.
    internal class SoloRaidStartedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix(GameWorld __instance) => RaidLifecycle.OnRaidStarted(__instance, "game-started");
    }

    // Every single-player raid exit (extract, leaving from the menu, MIA timer, death) goes through BaseLocalGame.Stop.
    internal class SoloRaidStopPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BaseLocalGame<EftGamePlayerOwner>), nameof(BaseLocalGame<EftGamePlayerOwner>.Stop));

        [PatchPrefix]
        private static void Prefix() => RaidLifecycle.OnRaidEnded("local-game-stop");
    }

    // Fallback on the game world's teardown.
    internal class SoloGameWorldDisposePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GameWorld), nameof(GameWorld.Dispose));

        [PatchPrefix]
        private static void Prefix() => RaidLifecycle.OnRaidEnded("gameworld-dispose");
    }
}
