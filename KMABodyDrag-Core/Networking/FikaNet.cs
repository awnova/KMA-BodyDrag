//====================[ Imports ]====================
using System;
using System.Runtime.CompilerServices;
using Comfort.Common;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using JsonType;
using UnityEngine;

namespace KMABodyDrag.Networking
{
    //====================[ FikaNet ]====================
    // Fika's side of FikaBridge: only ever called when Fika is installed (FikaBridge.Present), so its Fika types are never loaded
    // without it. Every Send* method logs a network trace and pushes the packet over the wire in one step (KeepMeAlive's
    // FikaBridge, cut down). NoInlining keeps the Fika references out of FikaBridge's own compiled code.
    //
    // Note on `broadcast: true`: on a client this only sends to the host (FikaClient.SendData); the host then relays to every
    // other peer except the original sender (FikaServer.OnNetworkReceive). A sender never receives its own packet back, so every
    // Send* caller applies its own state locally before or after calling here.
    internal static class FikaNet
    {
        //====================[ Network Utilities ]====================
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsServer() => FikaBackendUtils.IsServer;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsHeadless() => FikaBackendUtils.IsHeadless;

        //====================[ Send Helper ]====================
        private static void SendPacket<T>(ref T packet, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered) where T : struct, INetSerializable
        {
            if (!Singleton<IFikaNetworkManager>.Instantiated)
            {
                return;
            }

            try
            {
                Singleton<IFikaNetworkManager>.Instance.SendData(ref packet, delivery, true);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError(ex);
            }
        }

        //====================[ Drag Packet Senders ]====================
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendDragState(string corpseId, string draggerId, bool active, string declarerId)
        {
            DragLog.LogNetworkTrace($"Sending corpse drag packet: {draggerId} {(active ? "dragging" : "released")} {corpseId}" +
                (string.IsNullOrEmpty(declarerId) ? "" : $", rest to come from {declarerId}"));
            CorpseDragStatePacket packet = new() { corpseId = corpseId, draggerId = draggerId, active = active, declarerId = declarerId };
            SendPacket(ref packet);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendDragHeading(string draggerId, float yaw)
        {
            CorpseDragHeadingPacket packet = new() { draggerId = draggerId, yaw = yaw };
            SendPacket(ref packet, DeliveryMethod.Sequenced);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendRest(string corpseId, string declarerId, Vector3 root, TransformSync[] bones)
        {
            DragLog.LogNetworkTrace($"Sending corpse rest: {corpseId} at {root}, {bones?.Length ?? 0} bones (from {declarerId})");
            CorpseRestPacket packet = new() { corpseId = corpseId, declarerId = declarerId, root = root, bones = bones };
            SendPacket(ref packet);
        }
    }
}
