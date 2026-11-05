//====================[ Imports ]====================
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;

namespace KMABodyDrag
{
    //====================[ Plugin ]====================
    // Drag corpses the way KeepMeAlive drags a downed teammate. Runs on base SPT (single player) and with Fika (the drag is synced
    // over it): Fika is a soft dependency, so its Awake has run when FikaBridge looks for it. KeepMeAlive is optional (its Solo
    // or Fika build; both have the same interop API), a soft dependency so its Awake has run when KmaInterop binds to it. Rupture
    // likewise, for RuptureInterop.
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.fika.headless", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(KMABodyDrag.Interop.KmaInterop.SoloGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(KMABodyDrag.Interop.KmaInterop.FikaGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.rootdarkarchon.rupture", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("amos.euphoriastylephysics", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(Plugin.PluginGuid, "KMA Body Drag", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        //====================[ Identity ]====================
        public const string PluginGuid = "com.keepmealive.bodydrag";

        //====================[ Shared State ]====================
        public static ManualLogSource LogSource;

        //====================[ Unity Lifecycle ]====================
        private void Awake()
        {
            LogSource = Logger;

            LogAssemblyInfo();
            LogSource.LogInfo("KMA Body Drag plugin loaded!");

            DragSettings.Init(Config);
            KmaInterop.Init();
            RuptureInterop.Init();
            EuphoriaInterop.Init();

            EnablePatches();

            if (FikaBridge.Present) InitFika();
            else EnableSoloPatches();
        }

        //====================[ Fika / Solo ]====================
        // Its own method, so Awake's compiled code names no Fika type: without Fika it is never compiled.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InitFika()
        {
            FikaRaidLifecycle.Init();
        }

        private static void EnableSoloPatches()
        {
            new SoloRaidStartedPatch().Enable();
            new SoloRaidStopPatch().Enable();
            new SoloGameWorldDisposePatch().Enable();
            LogSource.LogInfo("Fika not installed: running single player.");
        }

        //====================[ Patch Registration ]====================
        private static void EnablePatches()
        {
            new CorpseInitPatch().Enable();
            new CorpseActionsPatch().Enable();
            new InteractBindingPatch().Enable();
            new DragTickPatch().Enable();
            new DraggerInputPatch().Enable();
            new DraggerDropMenuPatch().Enable();
            new DragPickupMovePatch().Enable();
            new DragPickupCommandPatch().Enable();
            new DragLeftHandCommandPatch().Enable();
            new PlayerPosePatch().Enable();
        }

        //====================[ Diagnostics ]====================
        private static void LogAssemblyInfo()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                LogSource.LogInfo($"Body drag assembly: {asm.GetName().Name} v{asm.GetName().Version}");
            }
            catch (Exception ex)
            {
                LogSource.LogWarning($"Could not retrieve assembly info: {ex.Message}");
            }
        }
    }
}
