//====================[ Imports ]====================
using System;
using System.Collections;
using System.Reflection;
using BepInEx.Bootstrap;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace KMABodyDrag.Interop
{
    //====================[ RuptureInterop ]====================
    // Rupture (com.rootdarkarchon.rupture) can simulate corpses in its own PhysX scene ("Localized Ragdoll Physics", on by default):
    // each corpse gets a shadow rig, the corpse's own rigidbodies are made kinematic, and the shadow's poses are written onto the
    // corpse's bones every frame while it moves. A settled rig is kept, and a bullet or blast on the corpse (or on one touching it)
    // wakes it again ("Wake Corpses On Impact"), at the pose it settled in, its death spot. On a corpse this mod has in its physics,
    // that would fight the hold every frame; on one already put down elsewhere, it would snap the body back to where it died.
    // So a corpse this mod takes is given back to EFT's own physics for good, the way Rupture gives back a rig it cannot run
    // (ShadowRagdollWorld.AbortToNative): the corpse's rigidbodies take the shadow's pose and come back, and the rig is destroyed, so
    // nothing of Rupture's moves that corpse again. Its wounds, blood and dismemberment stay.
    // Rupture's public CorpseDragV1 API is no help here: it runs a drag in Rupture's own scene, and only on the network authority.
    // Bound by reflection to Rupture's internals (1.2.2), so Rupture is not needed at compile or run time. Fail-open: with Rupture
    // missing, its physics off (no rigs), or a version whose internals differ, nothing is done.
    //
    // A Rupture with the public CorpseHandoffV1 API (Rupture.Integration, ABI 1) is used through that instead, and the internals
    // above are not bound: the corpse is released to this mod on every peer for the drag (Rupture keeps its wounds and severed
    // limbs, and stops simulating and networking it) and adopted back once it is set down, so Rupture simulates it from where it
    // lies (CorpseBody's leases). Also bound by reflection, so this mod builds and runs without that version.
    internal static class RuptureInterop
    {
        //====================[ Constants ]====================
        private const string RuptureGuid = "com.rootdarkarchon.rupture";
        private const string WorldTypeName = "Rupture.Ragdolls.Classes.ShadowRagdollWorld";
        private const string Reason = "taken by KMA Body Drag";
        private const string HandoffTypeName = "Rupture.Integration.CorpseHandoffV1";
        private const string HandoffInfoTypeName = "Rupture.Integration.CorpseHandoffInfo";
        private const string HandoffReleaseTypeName = "Rupture.Integration.HandoffRelease";
        private const int HandoffAbi = 1;

        //====================[ Handoff Contract ]====================
        // CorpseHandoffV1's HandoffResult and HandoffState, by value (the contract fixes them).
        public enum HandoffResult { Ok = 0, Pending = 1, NotManaged = 2, Busy = 3, Unavailable = 4, InvalidArgument = 5, InvalidToken = 6, Failed = 7, Stale = 8 }
        public enum HandoffState { Unmanaged = 0, Preparing = 1, Managed = 2, Released = 3, Unavailable = 4 }

        // CorpseHandoffInfo, copied.
        public struct HandoffInfo
        {
            public HandoffState State;
            // Rupture is still moving the body (falling, or playing back the host's fall).
            public bool Simulating;
            public uint OwnershipEpoch;
            // Bones whose subtree Rupture removed (a severed limb): no bodies or joints under them.
            public Transform[] SeveredRoots;
            // The weapon is a Rupture body of its own, off the hand.
            public bool WeaponIndependent;
        }

        //====================[ State ]====================
        // ShadowRagdollWorld.RigsByCorpse (Dictionary<Corpse, ShadowRig>) and PendingBuilds (List<PendingBuild>).
        private static IDictionary _rigsByCorpse;
        private static IList _pendingBuilds;
        private static FieldInfo _buildRagdoll;
        // ShadowRig.Active: the shadow is simulating (not dormant, not settled).
        private static FieldInfo _rigActive;
        private static MethodInfo _abortToNative;
        private static bool _active;

        // CorpseHandoffV1: its calls, the info fields read, and the one release request (Rupture copies it).
        private static MethodInfo _inspect;
        private static MethodInfo _release;
        private static MethodInfo _renew;
        private static MethodInfo _adopt;
        private static FieldInfo _infoState;
        private static FieldInfo _infoSimulating;
        private static FieldInfo _infoEpoch;
        private static FieldInfo _infoSevered;
        private static FieldInfo _infoWeapon;
        private static object _releaseRequest;
        // Reused argument arrays: every call is on the main thread (the API refuses others).
        private static readonly object[] InspectArgs = new object[2];
        private static readonly object[] ReleaseArgs = new object[3];
        private static readonly object[] TokenArgs = new object[1];
        private static bool _handoff;

        // Rupture with CorpseHandoffV1 is here and bound: corpses are released and adopted through it.
        public static bool HasHandoff => _handoff;

        //====================[ Init ]====================
        // From Plugin.Awake. The soft BepInDependency on RuptureGuid makes Rupture load first.
        public static void Init()
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(RuptureGuid, out var info) || info.Instance == null) return;

                var assembly = info.Instance.GetType().Assembly;
                if (assembly.GetType(HandoffTypeName) != null)
                {
                    if (BindHandoff(assembly))
                    {
                        _handoff = true;
                        Plugin.LogSource.LogInfo($"[Interop] Rupture {info.Metadata.Version} found: a dragged corpse is handed off from its ragdoll physics and back (CorpseHandoffV1).");
                    }
                    else
                    {
                        Plugin.LogSource.LogWarning($"[Interop] Rupture {info.Metadata.Version} has a CorpseHandoffV1 this mod does not know (ABI {HandoffAbi} expected): " +
                            "with its Localized Ragdoll Physics on, a dragged corpse may be pulled back to where it died. Turn that setting off, or update this mod.");
                    }
                    return;
                }

                var world = assembly.GetType(WorldTypeName);
                var rigType = world?.GetNestedType("ShadowRig", BindingFlags.NonPublic);
                var buildType = world?.GetNestedType("PendingBuild", BindingFlags.NonPublic);
                _rigsByCorpse = AccessTools.Field(world, "RigsByCorpse")?.GetValue(null) as IDictionary;
                _pendingBuilds = AccessTools.Field(world, "PendingBuilds")?.GetValue(null) as IList;
                _buildRagdoll = buildType != null ? AccessTools.Field(buildType, "Ragdoll") : null;
                _rigActive = rigType != null ? AccessTools.Field(rigType, "Active") : null;
                _abortToNative = world != null && rigType != null
                    ? AccessTools.Method(world, "AbortToNative", new[] { rigType, typeof(string), typeof(bool) })
                    : null;

                if (_rigsByCorpse == null || _pendingBuilds == null || _buildRagdoll == null || _rigActive == null || _abortToNative == null)
                {
                    Plugin.LogSource.LogWarning($"[Interop] Rupture {info.Metadata.Version} is not the version this mod knows (1.2.2): with its " +
                        "Localized Ragdoll Physics on, a dragged corpse may be pulled back to where it died. Turn that setting off, or update this mod.");
                    return;
                }

                _active = true;
                Plugin.LogSource.LogInfo($"[Interop] Rupture {info.Metadata.Version} found: a dragged corpse leaves its ragdoll physics.");
            }
            catch (Exception ex)
            {
                _active = false;
                Plugin.LogSource.LogError($"[Interop] Rupture binding failed: {ex.Message}");
            }
        }

        // CorpseHandoffV1 by its documented shape: every member, ABI 1.
        private static bool BindHandoff(Assembly assembly)
        {
            var api = assembly.GetType(HandoffTypeName);
            var infoType = assembly.GetType(HandoffInfoTypeName);
            var releaseType = assembly.GetType(HandoffReleaseTypeName);
            if (api == null || infoType == null || releaseType == null) return false;
            if (!(AccessTools.Property(api, "AbiVersion")?.GetValue(null) is int abi) || abi != HandoffAbi) return false;

            _inspect = AccessTools.Method(api, "Inspect", new[] { typeof(Corpse), infoType.MakeByRefType() });
            _release = AccessTools.Method(api, "Release", new[] { typeof(Corpse), releaseType, typeof(ulong).MakeByRefType() });
            _renew = AccessTools.Method(api, "Renew", new[] { typeof(ulong) });
            _adopt = AccessTools.Method(api, "Adopt", new[] { typeof(ulong) });
            _infoState = AccessTools.Field(infoType, "State");
            _infoSimulating = AccessTools.Field(infoType, "Simulating");
            _infoEpoch = AccessTools.Field(infoType, "OwnershipEpoch");
            _infoSevered = AccessTools.Field(infoType, "SeveredRoots");
            _infoWeapon = AccessTools.Field(infoType, "WeaponIndependent");
            var timeout = AccessTools.Field(releaseType, "LeaseTimeout");
            if (_inspect == null || _release == null || _renew == null || _adopt == null || _infoState == null || _infoSimulating == null
                || _infoEpoch == null || _infoSevered == null || _infoWeapon == null || timeout == null)
                return false;

            _releaseRequest = Activator.CreateInstance(releaseType);
            timeout.SetValue(_releaseRequest, LeaseTimeoutSeconds);
            return true;
        }

        //====================[ Handoff ]====================
        // The lease Rupture gives each release (unscaled seconds), refreshed by every Renew; CorpseBody renews well inside it.
        public const float LeaseTimeoutSeconds = 30f;

        // Where Rupture stands on this corpse. Read-only, every peer. A failed call reads as Unavailable (Rupture keeps it).
        public static HandoffResult Inspect(Corpse corpse, out HandoffInfo info)
        {
            info = new HandoffInfo { State = HandoffState.Unavailable };
            if (!_handoff || corpse == null) return HandoffResult.Unavailable;
            try
            {
                InspectArgs[0] = corpse;
                InspectArgs[1] = null;
                var result = (HandoffResult)Convert.ToInt32(_inspect.Invoke(null, InspectArgs));
                var dto = InspectArgs[1];
                InspectArgs[0] = InspectArgs[1] = null;
                if (dto != null)
                {
                    info.State = (HandoffState)Convert.ToInt32(_infoState.GetValue(dto));
                    info.Simulating = (bool)_infoSimulating.GetValue(dto);
                    info.OwnershipEpoch = (uint)_infoEpoch.GetValue(dto);
                    info.SeveredRoots = _infoSevered.GetValue(dto) as Transform[];
                    info.WeaponIndependent = (bool)_infoWeapon.GetValue(dto);
                }
                else if (result == HandoffResult.NotManaged) info.State = HandoffState.Unmanaged;
                return result;
            }
            catch (Exception ex)
            {
                Fail("Inspect", ex);
                return HandoffResult.Unavailable;
            }
        }

        // This peer's copy of the corpse leaves Rupture for this mod (Ok, or Pending until Inspect says Released). The token is
        // local to this peer; 0 on a refusal.
        public static HandoffResult Release(Corpse corpse, out ulong token)
        {
            token = 0;
            if (!_handoff || corpse == null) return HandoffResult.Unavailable;
            try
            {
                ReleaseArgs[0] = corpse;
                ReleaseArgs[1] = _releaseRequest;
                ReleaseArgs[2] = 0UL;
                var result = (HandoffResult)Convert.ToInt32(_release.Invoke(null, ReleaseArgs));
                token = (ulong)ReleaseArgs[2];
                ReleaseArgs[0] = null;
                return result;
            }
            catch (Exception ex)
            {
                Fail("Release", ex);
                return HandoffResult.Failed;
            }
        }

        public static HandoffResult Renew(ulong token) => CallToken(_renew, "Renew", token);

        // Back to Rupture, which builds its ragdoll again from the bones as they are (Ok, or Pending until Inspect no longer says
        // Released).
        public static HandoffResult Adopt(ulong token) => CallToken(_adopt, "Adopt", token);

        private static HandoffResult CallToken(MethodInfo method, string what, ulong token)
        {
            if (!_handoff) return HandoffResult.Unavailable;
            try
            {
                TokenArgs[0] = token;
                return (HandoffResult)Convert.ToInt32(method.Invoke(null, TokenArgs));
            }
            catch (Exception ex)
            {
                Fail(what, ex);
                return HandoffResult.Failed;
            }
        }

        //====================[ Queries ]====================
        // Rupture is moving this corpse in its own scene now (its corpse's own rigidbodies are kinematic meanwhile, so they cannot
        // say whether it has landed).
        public static bool IsSimulating(Corpse corpse)
        {
            if (_handoff)
            {
                Inspect(corpse, out var handoff);
                return handoff.Simulating;
            }
            if (!_active || corpse == null) return false;
            try
            {
                var rig = _rigsByCorpse.Contains(corpse) ? _rigsByCorpse[corpse] : null;
                return rig != null && (bool)_rigActive.GetValue(rig);
            }
            catch (Exception ex)
            {
                Fail("IsSimulating", ex);
                return false;
            }
        }

        // Rupture has a rig for this corpse (simulating, asleep or settled) or is about to build one.
        public static bool IsTracked(Corpse corpse)
        {
            if (!_active || corpse == null) return false;
            try
            {
                if (_rigsByCorpse.Contains(corpse)) return true;
                var ragdoll = corpse.Ragdoll;
                foreach (var build in _pendingBuilds)
                    if (build != null && ReferenceEquals(_buildRagdoll.GetValue(build), ragdoll)) return true;
                return false;
            }
            catch (Exception ex)
            {
                Fail("IsTracked", ex);
                return false;
            }
        }

        //====================[ Release ]====================
        // The corpse goes back to EFT's physics and Rupture forgets its rig: from the take (CorpseBody), before this mod touches its
        // rigidbodies, and wherever a peer sets the body down without having taken it (a headless host). A rig still waiting to be
        // built is dropped too. The rigidbodies come back dynamic and supported, as Rupture found them; the caller stops them.
        public static void Release(Corpse corpse)
        {
            if (!_active || corpse == null) return;
            try
            {
                var ragdoll = corpse.Ragdoll;
                for (int i = _pendingBuilds.Count - 1; i >= 0; i--)
                {
                    var build = _pendingBuilds[i];
                    if (build != null && ReferenceEquals(_buildRagdoll.GetValue(build), ragdoll)) _pendingBuilds.RemoveAt(i);
                }

                if (!_rigsByCorpse.Contains(corpse)) return;
                var rig = _rigsByCorpse[corpse];
                if (rig == null) return;
                _abortToNative.Invoke(null, new[] { rig, Reason, (object)false });
                DragLog.LogDebug($"[Interop] {corpse.PlayerProfileID}: handed back from Rupture's ragdoll physics");
            }
            catch (Exception ex)
            {
                Fail("Release", ex);
            }
        }

        // A bound call threw: the interop goes off for the session, logged once.
        private static void Fail(string what, Exception ex)
        {
            if (!_active && !_handoff) return;
            _active = false;
            _handoff = false;
            Plugin.LogSource.LogError($"[Interop] Rupture {what} failed, Rupture interop off: {(ex as TargetInvocationException)?.InnerException ?? ex}");
        }
    }
}
