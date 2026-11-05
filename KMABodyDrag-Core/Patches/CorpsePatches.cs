//====================[ Imports ]====================
using System;
using System.Reflection;
using EFT;
using EFT.Interactive;
using EFT.UI;
using EFT.InputSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KMABodyDrag.Patches
{
    //====================[ CorpseInitPatch ]====================
    // Every corpse the game makes (a death here, a still corpse loaded with the map) goes into CorpseRegistry. The overload declared
    // on Corpse itself (LootItem has its own Init, which this one calls).
    internal class CorpseInitPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.FirstMethod(typeof(Corpse), method =>
                method.Name == nameof(Corpse.Init) && method.DeclaringType == typeof(Corpse) && method.GetParameters().Length >= 10);

        [PatchPostfix]
        private static void Postfix(Corpse __instance)
        {
            try
            {
                CorpseRegistry.Register(__instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[CorpseInit] error: {ex.Message}");
            }
        }
    }

    //====================[ CorpseActionsPatch ]====================
    // The corpse's interaction menu: its Search entry loots on one press and drags on two (CorpseInteraction). KeepMeAlive's prefix
    // on the IInteractive overload only takes its own downed-body interactables, so a corpse still comes through here.
    internal class CorpseActionsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableActions),
                new[] { typeof(GamePlayerOwner), typeof(LootItem) });

        [PatchPostfix]
        private static void Postfix(GamePlayerOwner owner, LootItem lootItem, ref AvailableInteractionState __result)
        {
            try
            {
                if (lootItem is not Corpse corpse || !PlayerUtils.TryGetLocalOwnerPlayer(owner, out _)) return;
                if (!CorpseRegistry.TryGet(corpse, out var entry)) return;
                CorpseInteraction.Decorate(owner, entry, ref __result);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[CorpseActions] error: {ex.Message}");
            }
        }
    }

    //====================[ InteractBindingPatch ]====================
    // The Interact key's double-click timeout, read when EFT applies its bindings (start-up and every apply of the Controls
    // settings), as KMA-Wheel reads its key.
    internal class InteractBindingPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(InputPreset), nameof(InputPreset.UpdateBindings));

        [PatchPostfix]
        private static void Postfix(InputPreset __instance)
        {
            try
            {
                InteractBinding.Rebuild(__instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[InteractBinding] error: {ex.Message}");
            }
        }
    }

    //====================[ DragTickPatch ]====================
    // Each human player's tick (KeepMeAlive's RevivalTickPatch target): the drag that player is the dragger of, on this peer. The
    // corpse has no tick of its own once dead.
    internal class DragTickPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.UpdateTick));

        [PatchPostfix]
        private static void Postfix(Player __instance)
        {
            if (!PlayerUtils.IsHuman(__instance)) return;
            try
            {
                CorpseDragController.Tick(__instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DragTick] error: {ex}");
            }
        }
    }

    //====================[ Dragger Input ]====================
    // What the dragger's own keys do while they hold a body.
    //   Interact - drops it, when Interact Drops Body is on (consumed), besides the stance drops; otherwise it does what it always does.
    //   Inventory - opens the held body's loot, with the body still in hand (consumed; BlockAll, as KeepMeAlive's inventory-key
    //               loot, so the game does not open a second screen). A body with nothing to loot (a zombie's): the inventory
    //               opens as always, the body still in hand.
    // Interact reaches two input trees: the battle screen's ActionPanel (it runs the selected menu action, DraggerDropMenuPatch)
    // and the player owner (here). Which of them goes first is not fixed by anything this mod can rely on, so both drop the body:
    // the first one consumes the press (Block, never BlockAll, which would clear the frame's other commands) and the other never
    // sees it.
    internal class DraggerInputPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.TranslateCommand));

        [PatchPrefix]
        private static bool Prefix(EftGamePlayerOwner __instance, ECommand command, ref InputNode.ETranslateResult __result)
        {
            try
            {
                if (!CorpseDragController.IsLocalDragging || !PlayerUtils.TryGetLocalOwnerPlayer(__instance, out var player)) return true;

                if (command == ECommand.BeginInteracting && DraggerInteract.TryDrop(player))
                {
                    __result = InputNode.ETranslateResult.Block;
                    return false;
                }

                if (command == ECommand.ToggleInventory && DragHud.IsBattleUiActive() && CorpseInteraction.TryLootHeldBody(player))
                {
                    __result = InputNode.ETranslateResult.BlockAll;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DraggerInput] error: {ex.Message}");
                return true;
            }
        }
    }

    // The same Interact press as it reaches the battle screen's ActionPanel, before it runs whatever the dragger looks at.
    internal class DraggerDropMenuPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActionPanel), nameof(ActionPanel.TranslateCommand));

        [PatchPrefix]
        private static bool Prefix(ECommand command, ref InputNode.ETranslateResult __result)
        {
            try
            {
                if (command != ECommand.BeginInteracting || !CorpseDragController.IsLocalDragging) return true;
                var player = PlayerUtils.GetYourPlayer();
                if (!DraggerInteract.TryDrop(player)) return true;
                __result = InputNode.ETranslateResult.Block;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DraggerDropMenu] error: {ex.Message}");
                return true;
            }
        }
    }

    internal static class DraggerInteract
    {
        // Interact while holding a lifted body, with Interact Drops Body on: it is put down.
        public static bool TryDrop(Player player)
        {
            if (player == null || !DragSettings.INTERACT_DROPS.Value || !CorpseDragController.IsLocalHoldingBody) return false;
            CorpseDragController.StopDrag(player, "interact");
            return true;
        }
    }
}
