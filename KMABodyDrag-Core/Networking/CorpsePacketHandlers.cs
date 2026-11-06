//====================[ Imports ]====================
using JsonType;
using UnityEngine;

namespace KMABodyDrag.Networking
{
    //====================[ CorpsePacketHandlers ]====================
    // Receives drag claims, headings and rests (unpacked by the Fika assembly's FikaNet, which owns the packets; see FikaBridge).
    // A corpse has no owner the way a downed body has (KeepMeAlive's revivee settles the claims on their own body), so the host
    // owns every corpse and settles claims that crossed.
    internal static class CorpsePacketHandlers
    {
        public static void OnDragState(string corpseId, string draggerId, bool active, string declarerId)
        {
            bool applied = CorpseClaims.Apply(corpseId, draggerId, active);
            DragLog.LogNetworkTrace(
                $"[Packet] Corpse drag: {draggerId} {(active ? "started dragging" : "released")} {corpseId} (applied={applied})");

            // Let go: wait for whoever let go to say where it came to rest (unless someone has picked it up since).
            if (!active) CorpseBody.ExpectRest(corpseId, draggerId, declarerId);

            if (!FikaBridge.IAmHost() || !active || applied) return;

            // Refused (someone else holds it): that dragger lets go, and everyone, the refused peer included, re-learns the holder.
            // The host's own sends reach every client.
            FikaBridge.SendDragState(corpseId, draggerId, false);
            if (CorpseClaims.TryGetDragger(corpseId, out var holder)) FikaBridge.SendDragState(corpseId, holder, true);
        }

        public static void OnDragHeading(string draggerId, float yaw)
        {
            if (PlayerUtils.IsLocalPlayer(draggerId)) return;
            DragSession.OnRemoteHeading(draggerId, yaw);
        }

        public static void OnRest(string corpseId, string declarerId, Vector3 root, TransformSync[] bones)
        {
            DragLog.LogNetworkTrace($"[Packet] Corpse rest: {corpseId} at {root}, {bones?.Length ?? 0} bones (from {declarerId})");
            CorpseBody.OnRest(corpseId, declarerId, root, bones);
        }
    }
}
