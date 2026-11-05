//====================[ Imports ]====================
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;

namespace KMABodyDrag.Networking
{
    //====================[ CorpsePacketHandlers ]====================
    // Receives drag claims, headings and rests. A corpse has no owner the way a downed body has (KeepMeAlive's revivee settles the
    // claims on their own body), so the host owns every corpse and settles claims that crossed.
    internal static class CorpsePacketHandlers
    {
        public static void Register(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<CorpseDragStatePacket, NetPeer>(OnDragState);
            manager.RegisterPacket<CorpseDragHeadingPacket, NetPeer>(OnDragHeading);
            manager.RegisterPacket<CorpseRestPacket, NetPeer>(OnRest);
        }

        private static void OnDragState(CorpseDragStatePacket packet, NetPeer peer)
        {
            bool applied = CorpseClaims.Apply(packet.corpseId, packet.draggerId, packet.active);
            DragLog.LogNetworkTrace(
                $"[Packet] Corpse drag: {packet.draggerId} {(packet.active ? "started dragging" : "released")} {packet.corpseId} (applied={applied})");

            // Let go: wait for whoever let go to say where it came to rest (unless someone has picked it up since).
            if (!packet.active) CorpseBody.ExpectRest(packet.corpseId, packet.draggerId, packet.declarerId);

            if (!FikaBridge.IAmHost() || !packet.active || applied) return;

            // Refused (someone else holds it): that dragger lets go, and everyone, the refused peer included, re-learns the holder.
            // The host's own sends reach every client.
            FikaBridge.SendDragState(packet.corpseId, packet.draggerId, false);
            if (CorpseClaims.TryGetDragger(packet.corpseId, out var holder)) FikaBridge.SendDragState(packet.corpseId, holder, true);
        }

        private static void OnDragHeading(CorpseDragHeadingPacket packet, NetPeer peer)
        {
            if (PlayerUtils.IsLocalPlayer(packet.draggerId)) return;
            DragSession.OnRemoteHeading(packet.draggerId, packet.yaw);
        }

        private static void OnRest(CorpseRestPacket packet, NetPeer peer)
        {
            DragLog.LogNetworkTrace($"[Packet] Corpse rest: {packet.corpseId} at {packet.root}, {packet.bones?.Length ?? 0} bones (from {packet.declarerId})");
            CorpseBody.OnRest(packet.corpseId, packet.declarerId, packet.root, packet.bones);
        }
    }
}
