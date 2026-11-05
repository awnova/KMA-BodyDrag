//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InputSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KMABodyDrag.Patches
{
    //====================[ Drag Pickup Lock ]====================
    // While the local player picks a body up (CorpseDragController.IsPickupLocked, about a second), they
    // stay where they are, crouched and empty-handed: movement input is dropped and stance and weapon commands are
    // consumed. Looking around still works. Once the body is lifted everything is theirs again (and breaking the
    // stance then lets go).
    internal class DragPickupMovePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.Move));

        [PatchPrefix]
        private static void Prefix(Player __instance, ref Vector2 direction)
        {
            try
            {
                if (PlayerUtils.IsLocalPlayer(__instance) && CorpseDragController.IsPickupLocked)
                    direction = Vector2.zero;
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DragPickupMove] error: {ex.Message}"); }
        }
    }

    internal class DragPickupCommandPatch : ModulePatch
    {
        private static readonly HashSet<ECommand> Blocked = new HashSet<ECommand>
        {
            // Stance.
            ECommand.ToggleDuck,
            ECommand.ToggleProne,
            ECommand.NextWalkPose,
            ECommand.PreviousWalkPose,
            ECommand.Jump,
            ECommand.RestorePose,
            // Anything into the hands.
            ECommand.SelectFirstPrimaryWeapon,
            ECommand.SelectSecondPrimaryWeapon,
            ECommand.SelectSecondaryWeapon,
            ECommand.SelectKnife,
            ECommand.QuickSelectSecondaryWeapon,
            ECommand.SelectFastSlot4,
            ECommand.SelectFastSlot5,
            ECommand.SelectFastSlot6,
            ECommand.SelectFastSlot7,
            ECommand.SelectFastSlot8,
            ECommand.SelectFastSlot9,
            ECommand.SelectFastSlot0,
            ECommand.QuickKnifeKick,
            ECommand.ThrowGrenade,
            ECommand.PressThrowGrenade
        };

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.TranslateCommand));

        [PatchPrefix]
        private static bool Prefix(EftGamePlayerOwner __instance, ECommand command, ref InputNode.ETranslateResult __result)
        {
            try
            {
                if (!PlayerUtils.TryGetLocalOwnerPlayer(__instance, out _) || !Blocked.Contains(command) || !CorpseDragController.IsPickupLocked) return true;
                __result = InputNode.ETranslateResult.BlockAll;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DragPickupCommand] error: {ex.Message}");
                return true;
            }
        }
    }

    // The lifted body is held in the left hand, so the sidearm is the right's. What needs the left hand lets go of
    // the body first; the command then goes through. (Taking anything else into the hands lets go on its own,
    // CorpseDragController.BrokenStance.)
    internal class DragLeftHandCommandPatch : ModulePatch
    {
        private static readonly HashSet<ECommand> LeftHand = new HashSet<ECommand>
        {
            // Every weapon command of FirearmHandsInputTranslator except firing and aiming (ToggleShooting,
            // EndShooting, ToggleAlternativeShooting, EndAlternativeShooting).
            ECommand.ReloadWeapon,
            ECommand.QuickReloadWeapon,
            ECommand.UnloadMagazine,
            ECommand.ChamberUnload,
            ECommand.CheckAmmo,
            ECommand.CheckChamber,
            ECommand.CheckFireMode,
            ECommand.ChangeWeaponMode,
            ECommand.ForceAutoWeaponMode,
            ECommand.ExamineWeapon,
            ECommand.FoldStock,
            ECommand.ChangeScope,
            ECommand.ChangeScopeMagnification,
            ECommand.OpticCalibrationSwitchUp,
            ECommand.OpticCalibrationSwitchDown,
            ECommand.ToggleTacticalDevice,
            ECommand.NextTacticalDevice,
            ECommand.ReleaseTactical,
            ECommand.ToggleTacticalInteraction,
            ECommand.ToggleBipods,
            ECommand.LeftStanceToggle
        };

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.TranslateCommand));

        [PatchPrefix]
        private static void Prefix(EftGamePlayerOwner __instance, ECommand command)
        {
            try
            {
                if (!PlayerUtils.TryGetLocalOwnerPlayer(__instance, out var player)) return;
                if (!LeftHand.Contains(command) || !CorpseDragController.IsLocalHoldingBody) return;
                // Only with the sidearm out: empty hands have no weapon to work (Examine there is the empty hands').
                if (!PlayerUtils.HoldsFirearm(player)) return;
                CorpseDragController.StopDrag(player, "left-hand", notify: true);
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DragLeftHandCommand] error: {ex.Message}"); }
        }
    }
}
