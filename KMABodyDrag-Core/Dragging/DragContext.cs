//====================[ Imports ]====================
using EFT;

namespace KMABodyDrag.Dragging
{
    //====================[ DragContext ]====================
    // The part of a drag that exists only while it lasts, as this peer sees it, in one place: the DragSession (the
    // pickup stages and the fixed hold frame) and the dragger's arms (DraggerArms). The two need each other every tick (the
    // pickup is timed and met from how far the arms reach; the arms follow the session's stage and reach time), and this is the
    // only place that connects them, so neither refers to the other. The body's own physics (CorpseBody) lives with the
    // body, drag or none, and stays outside; it is only asked whether it is one.
    internal static class DragContext
    {
        //====================[ Lifecycle ]====================
        // The session for a dragged body, made or replaced to match the dragger now holding it.
        public static DragSession Begin(Player dragger, Player body) => DragSession.Ensure(dragger, body);

        // This peer's part of a drag that ended: the hands let go (they blend back to the animation, then the arms
        // are dropped) and the session is gone.
        public static void End(string bodyId)
        {
            if (!DragSession.TryGet(bodyId, out var session)) return;
            DraggerArms.Release(session.Dragger);
            DragSession.End(bodyId);
        }

        // Raid boundary: the players go with the scene.
        public static void ClearAll()
        {
            DragSession.ClearAll();
            DraggerArms.ClearAll();
        }

        public static bool TryGetSession(string bodyId, out DragSession session) => DragSession.TryGet(bodyId, out session);

        //====================[ Per Tick ]====================
        // Once per tick while the body is dragged (from the dragger's tick): the session (the fixed hold, and the pickup
        // from how far the dragger's arms reach), then the arms from the stage the session has come to.
        public static DragSession Tick(Player dragger, Player body)
        {
            var session = DragSession.Ensure(dragger, body);
            DragReach? reach = DraggerArms.TryGetShoulders(dragger, out var shoulders) ? shoulders : null;
            session.Tick(reach, CorpseBody.IsTaken(body.ProfileId));
            DraggerArms.Ensure(dragger, body, session.HandsOn, session.Ragdolled, session.ReachSeconds);
            return session;
        }
    }
}
