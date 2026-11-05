//====================[ Imports ]====================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using EFT.Interactive;
using HarmonyLib;

namespace KMABodyDrag.Interop
{
    //====================[ EuphoriaInterop ]====================
    // Euphoria Style Physics (amos.euphoriastylephysics) moves corpses on its own: "Corpses stay physical near you" (CorpseWake)
    // wakes a corpse near the local player and freezes it (CorpseRagdoll.StopRigidbody on every moving rigidbody) once they are
    // farther, and its dying motion (DeathMotion, a component on the corpse) drives the body for a while after death. On a held
    // body both fight the drag; up to 0.17.4 the distance was measured from the corpse's root, which stays at the death spot, so
    // the held body froze in the air once the dragger was that far from where it died.
    //
    // Since 0.21.1 Euphoria has a public lease (EuphoriaStylePhysics.Api.CorpseLeaseV1): while this mod holds a corpse, Euphoria
    // leaves it alone (no dying motion, no wake, freeze or rebuild, no walk push, EFT's own corpse-still answer), and once released
    // it decides again on its next tick. Acquired at the take, released when this peer's session on the body ends (removed off
    // screen, or handed back to Rupture), so a body picked up again while parked stays held. Local only, like the drag's physics.
    //
    // An older Euphoria (0.17.4) has no lease: a corpse this mod takes leaves its waking and dying motion for good, by its
    // internals (its dying motion aborted, CorpseWake forgets it).
    // Both bound by reflection, so Euphoria is not needed at compile or run time. Fail-open: with Euphoria missing or a version
    // matching neither, nothing is done.
    internal static class EuphoriaInterop
    {
        //====================[ Constants ]====================
        private const string EuphoriaGuid = "amos.euphoriastylephysics";
        private const string LeaseTypeName = "EuphoriaStylePhysics.Api.CorpseLeaseV1";
        private const int LeaseVersion = 1;
        private const string Owner = Plugin.PluginGuid;
        private const string WakeTypeName = "EuphoriaStylePhysics.CorpseWake";
        private const string MotionTypeName = "EuphoriaStylePhysics.DeathMotion";

        //====================[ State ]====================
        // CorpseLeaseV1.TryAcquire, Release and IsLeased.
        private static MethodInfo _acquire;
        private static MethodInfo _release;
        private static MethodInfo _isLeased;
        // The corpses this mod holds.
        private static readonly HashSet<Corpse> Held = new HashSet<Corpse>();
        private static bool _lease;

        // 0.17.4: CorpseWake.Corpses (List<Entry>) and ByCorpse (Dictionary<Corpse, Entry>).
        private static IList _corpses;
        private static IDictionary _byCorpse;
        // CorpseWake.Entry.Near (read by the corpse-still wrapper) and WokenByUs.
        private static FieldInfo _entryNear;
        private static FieldInfo _entryWoken;
        // DeathMotion (a MonoBehaviour) and its Abort().
        private static Type _motionType;
        private static MethodInfo _motionAbort;
        private static bool _internals;

        //====================[ Init ]====================
        // From Plugin.Awake. The soft BepInDependency on EuphoriaGuid makes Euphoria load first.
        public static void Init()
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(EuphoriaGuid, out var info) || info.Instance == null) return;
                var assembly = info.Instance.GetType().Assembly;
                string version = info.Metadata.Version.ToString();

                var leaseType = assembly.GetType(LeaseTypeName);
                if (leaseType != null)
                {
                    if (BindLease(leaseType))
                    {
                        _lease = true;
                        Plugin.LogSource.LogInfo($"[Interop] Euphoria Style Physics {version} found: a dragged corpse is leased from it (CorpseLeaseV1).");
                    }
                    else
                    {
                        Plugin.LogSource.LogWarning($"[Interop] Euphoria Style Physics {version} has a CorpseLeaseV1 this mod does not know (version {LeaseVersion} expected): " +
                            "with its \"Corpses stay physical near you\" on, a dragged corpse may freeze in place. Turn that setting off, or update this mod.");
                    }
                    return;
                }

                if (!BindInternals(assembly))
                {
                    Plugin.LogSource.LogWarning($"[Interop] Euphoria Style Physics {version} is not a version this mod knows (0.17.4, or 0.21.1+ with CorpseLeaseV1): " +
                        "with its \"Corpses stay physical near you\" on, a dragged corpse may freeze in place. Turn that setting off, or update this mod.");
                    return;
                }
                _internals = true;
                Plugin.LogSource.LogInfo($"[Interop] Euphoria Style Physics {version} found: a dragged corpse leaves its corpse waking and dying motion.");
            }
            catch (Exception ex)
            {
                _lease = _internals = false;
                Plugin.LogSource.LogError($"[Interop] Euphoria Style Physics binding failed: {ex.Message}");
            }
        }

        private static bool BindLease(Type type)
        {
            var version = AccessTools.Field(type, "Version");
            if (version == null || !version.IsLiteral || !(version.GetRawConstantValue() is int v) || v != LeaseVersion) return false;
            _acquire = AccessTools.Method(type, "TryAcquire", new[] { typeof(Corpse), typeof(string) });
            _release = AccessTools.Method(type, "Release", new[] { typeof(Corpse), typeof(string) });
            _isLeased = AccessTools.Method(type, "IsLeased", new[] { typeof(Corpse) });
            return _acquire != null && _acquire.ReturnType == typeof(bool) && _release != null
                && _isLeased != null && _isLeased.ReturnType == typeof(bool);
        }

        private static bool BindInternals(Assembly assembly)
        {
            var wake = assembly.GetType(WakeTypeName);
            var entryType = wake?.GetNestedType("Entry", BindingFlags.NonPublic);
            _corpses = wake != null ? AccessTools.Field(wake, "Corpses")?.GetValue(null) as IList : null;
            _byCorpse = wake != null ? AccessTools.Field(wake, "ByCorpse")?.GetValue(null) as IDictionary : null;
            _entryNear = entryType != null ? AccessTools.Field(entryType, "Near") : null;
            _entryWoken = entryType != null ? AccessTools.Field(entryType, "WokenByUs") : null;
            _motionType = assembly.GetType(MotionTypeName);
            _motionAbort = _motionType != null ? AccessTools.Method(_motionType, "Abort", Type.EmptyTypes) : null;
            return _corpses != null && _byCorpse != null && _entryNear != null && _entryWoken != null && _motionType != null && _motionAbort != null;
        }

        //====================[ Queries ]====================
        // Another mod holds this corpse through Euphoria's lease: it is not this mod's to take.
        public static bool IsHeldByOther(Corpse corpse)
        {
            if (!_lease || corpse == null || Held.Contains(corpse)) return false;
            try
            {
                return (bool)_isLeased.Invoke(null, new object[] { corpse });
            }
            catch (Exception ex)
            {
                Fail("IsLeased", ex);
                return false;
            }
        }

        //====================[ Take ]====================
        // From the take (CorpseBody), before this mod touches the rigidbodies: Euphoria leaves the corpse alone. With the lease,
        // a corpse Euphoria never handled cannot be acquired and needs not be.
        public static void Acquire(Corpse corpse)
        {
            if (corpse == null) return;
            if (_lease)
            {
                try
                {
                    if ((bool)_acquire.Invoke(null, new object[] { corpse, Owner }))
                    {
                        Held.Add(corpse);
                        DragLog.LogDebug($"[Interop] {corpse.PlayerProfileID}: leased from Euphoria Style Physics");
                    }
                    else DragLog.LogDebug($"[Interop] {corpse.PlayerProfileID}: not leased from Euphoria Style Physics (not its corpse, or held by another mod)");
                }
                catch (Exception ex)
                {
                    Fail("TryAcquire", ex);
                }
                return;
            }
            if (_internals) Forget(corpse);
        }

        // This peer's session on the body ended (CorpseBody): Euphoria has it again and decides on its next tick. A corpse gone
        // from the scene is only forgotten here (Euphoria prunes its own).
        public static void Release(Corpse corpse)
        {
            if (!_lease || corpse is null || !Held.Remove(corpse) || corpse == null) return;
            try
            {
                _release.Invoke(null, new object[] { corpse, Owner });
                DragLog.LogDebug($"[Interop] {corpse.PlayerProfileID}: given back to Euphoria Style Physics");
            }
            catch (Exception ex)
            {
                Fail("Release", ex);
            }
        }

        // Raid boundary: the corpses are gone.
        public static void Clear() => Held.Clear();

        // 0.17.4: its dying motion aborted, and CorpseWake forgets the corpse for good.
        private static void Forget(Corpse corpse)
        {
            try
            {
                foreach (var motion in corpse.gameObject.GetComponents(_motionType))
                    if (motion != null) _motionAbort.Invoke(motion, null);

                if (!_byCorpse.Contains(corpse)) return;
                var entry = _byCorpse[corpse];
                if (entry != null)
                {
                    // The corpse-still wrapper keeps reading Near: false leaves it saying what EFT's own check says.
                    _entryNear.SetValue(entry, false);
                    _entryWoken.SetValue(entry, false);
                    _corpses.Remove(entry);
                }
                _byCorpse.Remove(corpse);
                DragLog.LogDebug($"[Interop] {corpse.PlayerProfileID}: let go by Euphoria Style Physics");
            }
            catch (Exception ex)
            {
                Fail("Forget", ex);
            }
        }

        // A bound call threw: the interop goes off for the session, logged once.
        private static void Fail(string what, Exception ex)
        {
            if (!_lease && !_internals) return;
            _lease = _internals = false;
            Plugin.LogSource.LogError($"[Interop] Euphoria Style Physics {what} failed, Euphoria interop off: {(ex as TargetInvocationException)?.InnerException ?? ex}");
        }
    }
}
