//====================[ Imports ]====================
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KMABodyDrag.Utilities
{
    //====================[ PlayerUtils ]====================
    // The part of KeepMeAlive's PlayerUtils the drag uses. A dead player is not in it: corpses are found by CorpseRegistry.
    internal static class PlayerUtils
    {
        public static Player GetYourPlayer()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                return null;
            }

            var player = Singleton<GameWorld>.Instance.MainPlayer;
            return (player != null && player.IsYourPlayer) ? player : null;
        }

        // A player who is (or was) in this raid, by profile id.
        public static Player GetPlayerById(string id)
        {
            if (string.IsNullOrEmpty(id) || !Singleton<GameWorld>.Instantiated)
            {
                return null;
            }

            return Singleton<GameWorld>.Instance.GetEverExistedPlayerByID(id);
        }

        // Unity's null check first: a player object destroyed (left the raid) is not C# null, and its health controller field can
        // still say alive.
        public static bool IsAlive(Player player) =>
            player != null && player.HealthController is { IsAlive: true };

        public static bool IsHuman(Player player) =>
            player != null && !player.IsAI && player.AIData?.IsAI != true;

        public static bool IsLocalPlayer(Player player) =>
            player != null && player.IsYourPlayer;

        public static bool IsLocalPlayer(string profileId)
        {
            if (string.IsNullOrEmpty(profileId))
            {
                return false;
            }

            var local = GetYourPlayer();
            return local != null && local.ProfileId == profileId;
        }

        public static bool TryGetLocalOwnerPlayer(GamePlayerOwner owner, out Player player)
        {
            player = owner?.Player;
            return player != null && player.IsYourPlayer;
        }

        // Nothing in the player's hands.
        public static bool IsEmptyHanded(Player player) =>
            player?.HandsController is Player.EmptyHandsController;

        // A firearm in the player's hands (it needs only the right; the left can keep a dragged body).
        public static bool HoldsFirearm(Player player) =>
            player?.HandsController is IFirearmHandsController;

        public static bool IsFirstPersonLocal(Player player) =>
            player != null && player.IsYourPlayer && player.FirstPersonPointOfView;

        // The game's main camera, when it has one.
        public static bool TryGetMainCamera(out Camera camera)
        {
            if (EFT.CameraControl.CameraManager.Exist)
            {
                camera = EFT.CameraControl.CameraManager.Instance.Camera;
                return camera != null;
            }

            camera = null;
            return false;
        }

        public static Transform GetPelvis(Player player) =>
            player?.PlayerBones?.Pelvis?.Original;

        public static Transform GetRibcage(Player player) =>
            player?.PlayerBones?.Ribcage?.Original;
    }
}
