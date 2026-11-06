//====================[ Imports ]====================
using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using HarmonyLib;
using JsonType;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KMABodyDrag.Networking
{
    //====================[ FikaNet ]====================
    // Everything of the mod that names a Fika type. Built as its own assembly and embedded in KMABodyDrag.dll, which serves it
    // from the resource only when Fika is installed (FikaBridge.Init): KMABodyDrag.dll itself does not reference Fika.Core at all,
    // so every one of its types loads on base SPT. Other mods list every loaded assembly's types (WTT-ClientCommonLib's
    // RegisterCustomParents, Newtonsoft's UnityConverterInitializer), and a single type that cannot load, a packet or even a
    // compiler-made delegate cache over a Fika type, made that throw, so WTT registered no custom items and the game stopped at
    // /client/items (with Ammo Loading Animations installed).
    //
    // KMABodyDrag.dll calls IsServer, IsHeadless and the Send* methods directly (only from its FikaLink); this side never
    // references KMABodyDrag.dll and calls back through the delegates given to Init (packets received, raid boundaries, the
    // remote players' pose pass).
    //
    // Note on `broadcast: true`: on a client this only sends to the host (FikaClient.SendData); the host then relays to every
    // other peer except the original sender (FikaServer.OnNetworkReceive). A sender never receives its own packet back, so every
    // Send* caller applies its own state locally before or after calling here.
    public static class FikaNet
    {
        //====================[ Host Callbacks ]====================
        private static Action<object> _logError;
        private static Action<GameWorld, string> _onRaidStarted;
        private static Action<string> _onRaidEnded;
        private static Action<string, string, bool, string> _onDragState;
        private static Action<string, float> _onDragHeading;
        private static Action<string, string, Vector3, TransformSync[]> _onRest;
        internal static Action<Player> AfterPlayerPose;
        private static bool _initialized;

        //====================[ Init ]====================
        // Event subscriptions are installed once for the plugin lifetime.
        public static void Init(
            Action<object> logError,
            Action<GameWorld, string> onRaidStarted,
            Action<string> onRaidEnded,
            Action<Player> afterPlayerPose,
            Action<string, string, bool, string> onDragState,
            Action<string, float> onDragHeading,
            Action<string, string, Vector3, TransformSync[]> onRest)
        {
            if (_initialized) return;
            _initialized = true;

            _logError = logError;
            _onRaidStarted = onRaidStarted;
            _onRaidEnded = onRaidEnded;
            AfterPlayerPose = afterPlayerPose;
            _onDragState = onDragState;
            _onDragHeading = onDragHeading;
            _onRest = onRest;

            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(e => RegisterPackets(e.Manager));
            FikaEventDispatcher.SubscribeEvent<GameWorldStartedEvent>(e => _onRaidStarted(e.GameWorld, "game-world-started"));
            FikaEventDispatcher.SubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(e => _onRaidEnded("game-ended"));

            new ObservedPlayerPosePatch().Enable();
        }

        //====================[ Raid Boundaries ]====================
        private static void OnRaidStarted(FikaRaidStartedEvent e)
        {
            if (Singleton<GameWorld>.Instantiated) _onRaidStarted(Singleton<GameWorld>.Instance, "raid-started");
        }

        //====================[ Network Utilities ]====================
        public static bool IsServer() => FikaBackendUtils.IsServer;

        public static bool IsHeadless() => FikaBackendUtils.IsHeadless;

        //====================[ Receive ]====================
        // Receives drag claims, headings and rests; the mod's side (CorpseNetReceive) applies them.
        private static void RegisterPackets(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<CorpseDragStatePacket, NetPeer>(OnDragState);
            manager.RegisterPacket<CorpseDragHeadingPacket, NetPeer>(OnDragHeading);
            manager.RegisterPacket<CorpseRestPacket, NetPeer>(OnRest);
        }

        private static void OnDragState(CorpseDragStatePacket packet, NetPeer peer) =>
            _onDragState(packet.corpseId, packet.draggerId, packet.active, packet.declarerId);

        private static void OnDragHeading(CorpseDragHeadingPacket packet, NetPeer peer) =>
            _onDragHeading(packet.draggerId, packet.yaw);

        private static void OnRest(CorpseRestPacket packet, NetPeer peer) =>
            _onRest(packet.corpseId, packet.declarerId, packet.root, packet.bones);

        //====================[ Send ]====================
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
                _logError?.Invoke(ex);
            }
        }

        public static void SendDragState(string corpseId, string draggerId, bool active, string declarerId)
        {
            CorpseDragStatePacket packet = new() { corpseId = corpseId, draggerId = draggerId, active = active, declarerId = declarerId };
            SendPacket(ref packet);
        }

        // Several times a second while dragging; only the latest matters.
        public static void SendDragHeading(string draggerId, float yaw)
        {
            CorpseDragHeadingPacket packet = new() { draggerId = draggerId, yaw = yaw };
            SendPacket(ref packet, DeliveryMethod.Sequenced);
        }

        public static void SendRest(string corpseId, string declarerId, Vector3 root, TransformSync[] bones)
        {
            CorpseRestPacket packet = new() { corpseId = corpseId, declarerId = declarerId, root = root, bones = bones };
            SendPacket(ref packet);
        }
    }

    //====================[ Observed Player Pose ]====================
    // Fika's ObservedPlayer overrides Player.LateUpdate without calling the base, so the mod's PlayerPosePatch never runs for
    // remote players: the dragger's arms are posed straight after their late pass here instead (BodyAnimation.AfterPlayerPose).
    internal class ObservedPlayerPosePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ObservedPlayer), nameof(ObservedPlayer.LateUpdate));

        [PatchPostfix]
        private static void Postfix(ObservedPlayer __instance)
        {
            FikaNet.AfterPlayerPose?.Invoke(__instance);
        }
    }
}
