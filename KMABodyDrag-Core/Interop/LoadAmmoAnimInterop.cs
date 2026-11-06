//====================[ Imports ]====================
using System;
using System.Reflection;
using BepInEx.Bootstrap;
using EFT;
using HarmonyLib;

namespace KMABodyDrag.Interop
{
    //====================[ LoadAmmoAnimInterop ]====================
    // Manimal's Ammo Loading Animations (com.manimal.loadammoanim) plays a magazine-loading animation by swapping the player's
    // hands controller for its own (LoadAmmoBundleController): it drops the weapon, builds the bundle controller, and when the
    // loading ends puts it away and re-equips the last weapon. A drag keeps the player crouched and empty-handed (or on the
    // sidearm), and lets go the moment anything else is in their hands, so the two cannot share the hands:
    //   - loading a magazine while picking up or dragging: the animation is not started (the magazine still loads, only the
    //     hands animation is skipped), so the drag is not broken by a foreign controller or a re-equipped rifle;
    //   - a drag while the animation runs: not started (CorpseDragController.CanStartDrag), the player finishes loading first.
    // Bound by reflection to the mod's internals (LoadAmmoAnimState), so it is not needed at compile or run time. Fail-open: with
    // it missing or a version whose internals differ, nothing is done.
    internal static class LoadAmmoAnimInterop
    {
        //====================[ Constants ]====================
        private const string LoadAmmoAnimGuid = "com.manimal.loadammoanim";
        private const string StateTypeName = "Manimal.LoadAmmoAnim.Patches.LoadAmmoAnimState";

        //====================[ State ]====================
        // LoadAmmoAnimState.AnyIsOurAnimation(): true from the controller swap's start to the end of its put-away.
        private static Func<bool> _anyAnimating;
        private static bool _active;

        //====================[ Init ]====================
        // From Plugin.Awake. The soft BepInDependency on LoadAmmoAnimGuid makes the mod load first.
        public static void Init()
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(LoadAmmoAnimGuid, out var info) || info.Instance == null) return;

                var state = info.Instance.GetType().Assembly.GetType(StateTypeName);
                var started = state != null ? AccessTools.Method(state, "OnLoadingStarted", new[] { typeof(Player) }) : null;
                var any = state != null ? AccessTools.Method(state, "AnyIsOurAnimation", Type.EmptyTypes) : null;
                if (started == null || any == null || any.ReturnType != typeof(bool))
                {
                    Plugin.LogSource.LogWarning($"[Interop] Ammo Loading Animations {info.Metadata.Version} is not the version this mod knows: " +
                        "loading a magazine while dragging a body may break the drag.");
                    return;
                }

                _anyAnimating = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), any);
                new Harmony(Plugin.PluginGuid + ".loadammoanim").Patch(started,
                    prefix: new HarmonyMethod(typeof(LoadAmmoAnimInterop), nameof(SkipDuringDrag)));

                _active = true;
                Plugin.LogSource.LogInfo($"[Interop] Ammo Loading Animations {info.Metadata.Version} found: its animation yields to a drag.");
            }
            catch (Exception ex)
            {
                _active = false;
                Plugin.LogSource.LogError($"[Interop] Ammo Loading Animations binding failed: {ex.Message}");
            }
        }

        //====================[ Queries ]====================
        // The hands animation is running (or swapping in or out): the hands are not the player's to use.
        public static bool IsAnimating()
        {
            if (!_active) return false;
            try
            {
                return _anyAnimating();
            }
            catch (Exception ex)
            {
                _active = false;
                Plugin.LogSource.LogError($"[Interop] Ammo Loading Animations query failed, interop off: {ex.Message}");
                return false;
            }
        }

        //====================[ Patch ]====================
        // Prefix of LoadAmmoAnimState.OnLoadingStarted(Player player). Skipping it leaves the loading count and the animation
        // alone; the loading itself runs on, and its end (OnLoadingEnded) clamps at zero.
        private static bool SkipDuringDrag(Player player)
        {
            try
            {
                return !(player != null && player.IsYourPlayer && Dragging.CorpseDragController.IsLocalDragging);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Interop] Ammo Loading Animations skip check failed: {ex.Message}");
                return true;
            }
        }
    }
}
