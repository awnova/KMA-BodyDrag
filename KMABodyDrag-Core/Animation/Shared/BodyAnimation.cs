//====================[ Imports ]====================
using System;
using EFT;

namespace KMABodyDrag.Animation.Shared
{
    //====================[ BodyAnimation ]====================
    // The one place that reaches every system posing bones itself, on top of or instead of EFT's animation:
    //   dragger's arms - DraggerArms, after the dragger's own late pass;
    //   dragged body   - CorpseBody (the corpse's physics), from the dragger's tick and the driver.
    internal static class BodyAnimation
    {
        //====================[ Per Frame ]====================
        // Straight after each player's own late pass (PlayerPosePatch), so the game's pose is final and nothing of the
        // game's rewrites these bones afterwards.
        public static void AfterPlayerPose(Player player)
        {
            try { DraggerArms.AfterPlayerPose(player); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DraggerArms] Pose error: {ex}"); }
        }

        //====================[ Raid Boundary ]====================
        // Forgets every body, drag and arm; the players and corpses go with the scene.
        public static void ClearAll()
        {
            DragContext.ClearAll();
            CorpseBody.ClearAll();
            SkeletonBind.ClearKnown();
            ClipRig.ClearReports();
        }
    }
}
