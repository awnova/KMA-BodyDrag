//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KMABodyDrag.Dragging
{
    //====================[ DragLookLimit ]====================
    // The local dragger cannot look down past a limit for as long as they pick up and hold a body (like KeepMeAlive's defibrillator hold's
    // look clamp, CrouchHoldState); looking up is free. Movement stays free: they walk with the body. Yaw is
    // left as it is. The game's own states put the limits back when they exit, so the limit is held every tick; letting go
    // restores what the player had.
    internal static class DragLookLimit
    {
        // How far below the horizon the look may go (degrees; baked from the slider tuned in game, 27 on 2026-10-15, 25 on 2026-10-16).
        private const float LookDownLimit = 25f;

        private static MovementContext _mc;
        private static Vector2 _savedYawLimit;
        private static Vector2 _savedPitchLimit;

        //====================[ Control ]====================
        public static void Begin(Player player)
        {
            if (_mc != null) Restore();
            var mc = player?.MovementContext;
            if (mc == null) return;

            _mc = mc;
            _savedYawLimit = mc._yawLimit;
            _savedPitchLimit = mc._pitchTargetLimit;
            Hold();
        }

        // Every tick while dragging: the limit stays whatever the game does.
        public static void Hold()
        {
            if (_mc == null) return;
            Vector2 wanted = Wanted();
            if (_mc._pitchTargetLimit != wanted) _mc.SetPitchSmoothly(wanted);
        }

        public static void Restore()
        {
            var mc = _mc;
            _mc = null;
            if (mc == null) return;
            // KeepMeAlive's revive hold sets its own cone after a drag here is let go (its interop release comes first), so this
            // never overwrites it.
            mc.SetRotationLimit(_savedYawLimit, _savedPitchLimit);
        }

        // The game's pitch is negative looking up and positive looking down (standing: -90..90); the up end is left at the full range.
        private const float LookUpFree = -90f;
        private static Vector2 Wanted() => new Vector2(LookUpFree, LookDownLimit);
    }
}
