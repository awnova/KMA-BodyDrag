//====================[ Imports ]====================
using BepInEx.Bootstrap;
using JsonType;
using UnityEngine;

namespace KMABodyDrag.Networking
{
    //====================[ FikaBridge ]====================
    // The only network surface the rest of the mod uses: host/headless queries and outbound packets. Fika is optional: this class
    // names no Fika type, so it loads without Fika; with Fika installed every call goes on to FikaNet (the only code that touches
    // Fika's networking). Without Fika (base SPT, single player) this peer is the host, never headless, and there is nobody to send
    // to: every caller already applies its own state locally, so a send is simply dropped.
    internal static class FikaBridge
    {
        //====================[ Constants ]====================
        public const string FikaCoreGuid = "com.fika.core";

        //====================[ Presence ]====================
        // Fika's plugin loaded (the soft BepInDependency makes its Awake run first). Fixed for the session.
        private static bool? _present;

        public static bool Present => _present ??= Chainloader.PluginInfos.ContainsKey(FikaCoreGuid);

        //====================[ Network Utilities ]====================
        // Single player is its own host.
        public static bool IAmHost() => !Present || FikaNet.IsServer();

        public static bool IsHeadless() => Present && FikaNet.IsHeadless();

        //====================[ Drag Packet Senders ]====================
        public static void SendDragState(string corpseId, string draggerId, bool active, string declarerId = "")
        {
            if (Present) FikaNet.SendDragState(corpseId, draggerId, active, declarerId);
        }

        // Several times a second while dragging; only the latest matters.
        public static void SendDragHeading(string draggerId, float yaw)
        {
            if (Present) FikaNet.SendDragHeading(draggerId, yaw);
        }

        // Where a body let go of came to rest, as the declarer's own copy of it settled.
        public static void SendRest(string corpseId, string declarerId, Vector3 root, TransformSync[] bones)
        {
            if (Present) FikaNet.SendRest(corpseId, declarerId, root, bones);
        }
    }
}
