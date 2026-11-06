//====================[ Imports ]====================
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using JsonType;
using UnityEngine;

namespace KMABodyDrag.Networking
{
    //====================[ FikaBridge ]====================
    // The only network surface the rest of the mod uses: host/headless queries and outbound packets. Fika is optional, and this
    // assembly does not reference Fika.Core at all: everything that names a Fika type (the packets, their registration and
    // sending, Fika's raid events, the remote players' pose patch) is the KMABodyDrag.Fika assembly (FikaNet), embedded in this DLL
    // and served from the resource by Init's AssemblyResolve hook, only when Fika is installed. Other mods list every loaded
    // assembly's types (WTT-ClientCommonLib, Newtonsoft), so a single type here that could not load without Fika broke them (WTT
    // then registered no custom items and the game stopped at /client/items).
    // This assembly calls FikaNet directly, but only from FikaLink below; FikaNet calls back through the delegates given to its
    // Init. Without Fika (base SPT, single player) this peer is the host, never headless, and there is nobody to send to: every
    // caller already applies its own state locally, so a send is simply dropped.
    internal static class FikaBridge
    {
        //====================[ Constants ]====================
        public const string FikaCoreGuid = "com.fika.core";
        private const string FikaAssembly = "KMABodyDrag.Fika";

        //====================[ Presence ]====================
        // Fika's plugin loaded (the soft BepInDependency makes its Awake run first). Fixed for the session.
        private static bool? _present;

        public static bool Present => _present ??= Chainloader.PluginInfos.ContainsKey(FikaCoreGuid);

        // The Fika assembly is loaded and initialized (Fika installed and Init succeeded).
        public static bool Bound { get; private set; }

        private static Assembly _fikaAssembly;

        //====================[ Init ]====================
        // From Plugin.Awake. With Fika installed: serve the embedded Fika assembly when the runtime asks for it, and initialize it
        // (it subscribes Fika's events and registers the packets). Without Fika the hook is never installed and the assembly never
        // loads. On failure the mod runs without sync, logged.
        public static void Init()
        {
            if (!Present || Bound) return;
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFikaAssembly;
                FikaLink.Init();
                Bound = true;
                Plugin.LogSource.LogInfo("Fika integration initialized!");
            }
            catch (Exception ex)
            {
                Bound = false;
                Plugin.LogSource.LogError($"Fika integration failed, corpse drags will not sync: {ex}");
            }
        }

        private static Assembly ResolveFikaAssembly(object sender, ResolveEventArgs args)
        {
            if (new AssemblyName(args.Name).Name != FikaAssembly) return null;
            if (_fikaAssembly != null) return _fikaAssembly;
            using (var stream = typeof(FikaBridge).Assembly.GetManifestResourceStream(FikaAssembly + ".dll"))
            {
                if (stream == null) return null;
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return _fikaAssembly = Assembly.Load(buffer.ToArray());
                }
            }
        }

        //====================[ Network Utilities ]====================
        // Single player is its own host.
        public static bool IAmHost() => !Bound || FikaLink.IsServer();

        public static bool IsHeadless() => Bound && FikaLink.IsHeadless();

        //====================[ Drag Packet Senders ]====================
        public static void SendDragState(string corpseId, string draggerId, bool active, string declarerId = "")
        {
            if (!Bound) return;
            DragLog.LogNetworkTrace($"Sending corpse drag packet: {draggerId} {(active ? "dragging" : "released")} {corpseId}" +
                (string.IsNullOrEmpty(declarerId) ? "" : $", rest to come from {declarerId}"));
            FikaLink.SendDragState(corpseId, draggerId, active, declarerId);
        }

        // Several times a second while dragging; only the latest matters.
        public static void SendDragHeading(string draggerId, float yaw)
        {
            if (Bound) FikaLink.SendDragHeading(draggerId, yaw);
        }

        // Where a body let go of came to rest, as the declarer's own copy of it settled.
        public static void SendRest(string corpseId, string declarerId, Vector3 root, TransformSync[] bones)
        {
            if (!Bound) return;
            DragLog.LogNetworkTrace($"Sending corpse rest: {corpseId} at {root}, {bones?.Length ?? 0} bones (from {declarerId})");
            FikaLink.SendRest(corpseId, declarerId, root, bones);
        }
    }

    //====================[ FikaLink ]====================
    // The only code in this assembly that names the Fika assembly (FikaNet). Each call is its own method, never inlined, and only
    // reached with Fika installed (FikaBridge checks Present/Bound first): the runtime resolves FikaNet when it compiles one of
    // these, so without Fika it never does. Nothing here may become a field, a parameter of a lambda, or a method group outside
    // these bodies, or the compiler would put a Fika-assembly type where loading this assembly's types reaches it.
    internal static class FikaLink
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Init() => FikaNet.Init(
            Plugin.LogSource.LogError,
            RaidLifecycle.OnRaidStarted,
            RaidLifecycle.OnRaidEnded,
            BodyAnimation.AfterPlayerPose,
            CorpsePacketHandlers.OnDragState,
            CorpsePacketHandlers.OnDragHeading,
            CorpsePacketHandlers.OnRest);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsServer() => FikaNet.IsServer();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsHeadless() => FikaNet.IsHeadless();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendDragState(string corpseId, string draggerId, bool active, string declarerId) =>
            FikaNet.SendDragState(corpseId, draggerId, active, declarerId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendDragHeading(string draggerId, float yaw) => FikaNet.SendDragHeading(draggerId, yaw);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendRest(string corpseId, string declarerId, Vector3 root, TransformSync[] bones) =>
            FikaNet.SendRest(corpseId, declarerId, root, bones);
    }
}
