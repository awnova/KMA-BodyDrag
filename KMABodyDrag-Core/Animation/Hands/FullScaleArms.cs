//====================[ Imports ]====================
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KMABodyDrag.Animation.Hands
{
    //====================[ FullScaleArms ]====================
    // In first person the game squashes the player's upper body and arms along the view's depth axis (the Ribcage's local scale
    // is (1, 1, s), s = Lerp(1, 0.65, FOV factor), about 0.65 at the default field of view), so the arms and the weapon look right
    // at a wide FOV. An arm reaching forward is then only ~65% as long, and cannot reach what the mod puts its hands on: the
    // dragged body's shoulders, the revivee's chest. The game turns the compensation off itself when the hands must reach
    // something in the world (DoorInteractState: MovementContext.FovCompensationOn). This does the same for as long as the mod
    // poses a player's arms, and puts it back after.
    // Only the local player in first person is squashed: everyone else's skeleton is seen in third person, at full size.
    internal static class FullScaleArms
    {
        // Extra scale per second, on top of the game's easing, while restoring the arms to full size.
        private const float GrowRate = 1.25f;

        // Who wants the local player's arms at full size (a drag's arms, a defibrillator sequence).
        private static readonly Dictionary<Player, HashSet<object>> Owners = new Dictionary<Player, HashSet<object>>();
        // The last frame the arms were grown, so two owners asking in one frame do not grow them twice as fast.
        private static int _grownFrame = -1;

        // owner wants the player's arms at full size (or no longer does). Call every frame while it wants: the game can put
        // the compensation back (a change of pose or view), so the target is held at full size.
        public static void Want(Player player, object owner, bool want)
        {
            if (player == null || owner == null) return;

            Owners.TryGetValue(player, out var owners);
            bool had = owners != null && owners.Count > 0;
            if (want)
            {
                if (owners == null) Owners[player] = owners = new HashSet<object>();
                owners.Add(owner);
            }
            else if (owners != null)
            {
                owners.Remove(owner);
                if (owners.Count == 0) Owners.Remove(player);
            }
            bool has = want || (owners != null && owners.Count > 0);

            if (!player.IsYourPlayer) return;
            if (has && !had) player.MovementContext.FovCompensationOn(false);
            else if (!has && had) player.MovementContext.FovCompensationOn(true);
            else if (has && player.RibcageScaleCurrentTarget < 1f) player.RibcageScaleCurrentTarget = 1f;

            // The game's FirstPersonStrategy applies the scale next frame and eases on from here.
            if (has && player.RibcageScaleCurrent < 1f && _grownFrame != Time.frameCount)
            {
                _grownFrame = Time.frameCount;
                player.RibcageScaleCurrent = Mathf.MoveTowards(player.RibcageScaleCurrent, 1f, GrowRate * Time.deltaTime);
            }
        }

        // How far the player's arms have grown back to full size (1 once they have; always 1 outside first person).
        public static float Scale(Player player) =>
            PlayerUtils.IsFirstPersonLocal(player) ? player.RibcageScaleCurrent : 1f;

        // Raid boundary: the local player can outlive the scene transition, so restore EFT's depth compensation
        // before dropping the owners that were keeping their arms at full scale.
        public static void ClearAll()
        {
            foreach (var pair in Owners)
            {
                var player = pair.Key;
                if (player != null && player.IsYourPlayer && pair.Value.Count > 0)
                    player.MovementContext.FovCompensationOn(true);
            }
            Owners.Clear();
        }
    }
}
