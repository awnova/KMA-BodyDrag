//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using HandoffResult = KMABodyDrag.Interop.RuptureInterop.HandoffResult;
using HandoffState = KMABodyDrag.Interop.RuptureInterop.HandoffState;

namespace KMABodyDrag.Corpses
{
    //====================[ CorpseBody: Lease ]====================
    // A corpse Rupture simulates, borrowed for the drag through its CorpseHandoffV1 (RuptureInterop), on every peer, a headless
    // host included:
    //   release - at the pickup, this peer's copy leaves Rupture (Pending for a frame or two; the take waits for it). Busy means
    //             someone else has it, and the pickup is refused;
    //   renew   - every RenewSeconds while it is borrowed, well inside Rupture's lease, which hands it back by itself if this mod
    //             stops renewing;
    //   adopt   - once the body is set down where the declarer says (or that word is late), this mod's joints and rigidbodies come
    //             off and the colliders get their own material back (Remove), then Rupture builds its ragdoll again from the bones
    //             as they lie. Every release ends in an adopt: a failed take, a body nobody took here, a refused claim.
    // A body picked up again before it was handed back keeps its lease. After the adopt nothing here touches the corpse's bodies
    // again: they are Rupture's (later shots find the body through them).
    // Without that API, the take hands the corpse to EFT's physics for good instead (RuptureInterop.Release).
    internal static partial class CorpseBody
    {
        //====================[ Tuning ]====================
        private const float RenewSeconds = 1f;
        // A release still pending after this (Rupture says two frames for a managed corpse): given up, the pickup refused.
        private const float ReleaseWaitSeconds = 5f;
        // An adopt still pending after this: forgotten (Rupture owns the corpse either way).
        private const float AdoptWaitSeconds = 5f;
        // How long a settled copy waits for the declarer's word on where the body rests before it is handed back as it lies; the
        // declarer sends it at most SettleTimeoutSeconds after the release.
        private const float RestWaitSeconds = 5f;
        // A headless host simulates nothing, so a missing word would hand back the body where it was picked up: longer.
        private const float HeadlessRestWaitSeconds = 15f;
        // A refused release is not asked again for this long.
        private const float RefuseSeconds = 1f;

        //====================[ Lease ]====================
        private enum LeasePhase { Releasing, Held, Adopting }

        private sealed class Lease
        {
            public string Id;
            public EFT.Interactive.Corpse Corpse;
            public ulong Token;
            public LeasePhase Phase;
            public float Since;
            public float NextRenew;
            // Last seen held by a dragger.
            public float LastHeld;
            public Transform[] SeveredRoots;
            public bool WeaponIndependent;
        }

        private enum TakeGate { Go, Wait, Refuse }

        //====================[ State ]====================
        private static readonly Dictionary<string, Lease> Leases = new Dictionary<string, Lease>();
        private static readonly Dictionary<string, float> RefusedUntil = new Dictionary<string, float>();
        private static readonly List<Lease> LeaseScratch = new List<Lease>();

        //====================[ Queries ]====================
        // Rupture refused this body to the take a moment ago (someone else has it).
        public static bool IsRefused(string id) => RefusedUntil.TryGetValue(id, out float until) && Time.time < until;

        // Whether Rupture lets this mod have the body: not someone else's, and with the chest and both upper arms the grip needs
        // still on it. Unavailable does not refuse it: Rupture 1.4.0 says so for every corpse while its Localized Ragdoll Physics
        // is off (its coordinator registers every corpse regardless, ShadowRagdollWorld.InspectHandoff), when nothing of it moves
        // the body; the take goes ahead without a lease then (BeginTake).
        public static bool MayBorrow(CorpseRegistry.Entry entry)
        {
            if (!RuptureInterop.HasHandoff || entry == null) return true;
            if (Leases.ContainsKey(entry.Id)) return true;
            if (IsRefused(entry.Id)) return false;

            RuptureInterop.Inspect(entry.Corpse, out var info);
            if (info.State == HandoffState.Released) return false;
            if (info.SeveredRoots == null || info.SeveredRoots.Length == 0) return true;
            foreach (var spawner in entry.Pool.RigidbodySpawners)
            {
                var part = spawner.GetComponent<BodyPartCollider>();
                if (part == null) continue;
                var type = part.BodyPartColliderType;
                if (type != EBodyPartColliderType.RibcageUp && type != EBodyPartColliderType.LeftUpperArm && type != EBodyPartColliderType.RightUpperArm) continue;
                if (IsSevered(spawner.transform, info.SeveredRoots)) return false;
            }
            return true;
        }

        // A bone in a subtree Rupture removed.
        private static bool IsSevered(Transform bone, Transform[] roots)
        {
            if (roots == null || bone == null) return false;
            foreach (var root in roots)
                if (root != null && bone.IsChildOf(root)) return true;
            return false;
        }

        //====================[ Release ]====================
        // Before the take touches the rigidbodies: Rupture lets go of this peer's copy. Go once it has (lease set when Rupture
        // manages the body, null when it does not), Wait while it is on the way.
        private static TakeGate BeginTake(CorpseRegistry.Entry entry, out Lease lease)
        {
            lease = null;
            if (!RuptureInterop.HasHandoff)
            {
                RuptureInterop.Release(entry.Corpse);
                return TakeGate.Go;
            }
            if (IsRefused(entry.Id)) return TakeGate.Refuse;

            if (Leases.TryGetValue(entry.Id, out var held))
            {
                switch (held.Phase)
                {
                    case LeasePhase.Held:
                        lease = held;
                        return TakeGate.Go;
                    case LeasePhase.Releasing:
                        return PollReleasing(held, out lease);
                    case LeasePhase.Adopting:
                        // Picked up again while Rupture is taking it back: borrowed afresh once it has.
                        if (!PollAdopting(held)) return TakeGate.Wait;
                        break;
                }
            }

            var result = RuptureInterop.Release(entry.Corpse, out ulong token);
            switch (result)
            {
                case HandoffResult.Ok:
                case HandoffResult.Pending:
                    var fresh = new Lease
                    {
                        Id = entry.Id,
                        Corpse = entry.Corpse,
                        Token = token,
                        Phase = LeasePhase.Releasing,
                        Since = Time.time,
                        NextRenew = Time.time + RenewSeconds,
                        LastHeld = Time.time
                    };
                    Leases[entry.Id] = fresh;
                    DragLog.LogDebug($"[CorpseBody] {entry.Id}: released by Rupture ({result})");
                    return PollReleasing(fresh, out lease);
                case HandoffResult.NotManaged:
                // Rupture's physics is off or not running for this corpse (see MayBorrow): EFT's physics has it.
                case HandoffResult.Unavailable:
                    return TakeGate.Go;
                default:
                    Refuse(entry.Id, $"Rupture would not release it ({result})");
                    return TakeGate.Refuse;
            }
        }

        // Released: Go with what Rupture says of the body now. Unmanaged: Rupture's setup failed and gave the body up, so EFT's
        // physics has it and the take goes ahead without a lease.
        private static TakeGate PollReleasing(Lease lease, out Lease ready)
        {
            ready = null;
            RuptureInterop.Inspect(lease.Corpse, out var info);
            switch (info.State)
            {
                case HandoffState.Released:
                    lease.Phase = LeasePhase.Held;
                    lease.SeveredRoots = info.SeveredRoots;
                    lease.WeaponIndependent = info.WeaponIndependent;
                    ready = lease;
                    return TakeGate.Go;
                case HandoffState.Unmanaged:
                    Leases.Remove(lease.Id);
                    DragLog.LogDebug($"[CorpseBody] {lease.Id}: Rupture gave the body up while releasing it; EFT's physics has it");
                    return TakeGate.Go;
            }
            if (Time.time - lease.Since < ReleaseWaitSeconds) return TakeGate.Wait;

            AdoptLease(lease, "release never completed");
            Refuse(lease.Id, $"Rupture did not finish releasing it ({info.State})");
            return TakeGate.Refuse;
        }

        private static void Refuse(string id, string why)
        {
            RefusedUntil[id] = Time.time + RefuseSeconds;
            DragLog.LogDebug($"[CorpseBody] {id}: not taken: {why}");
        }

        //====================[ Untaken ]====================
        // A headless host's part of a drag, every tick the body is held: its copy is released too (it never takes the body), so
        // Rupture's simulation and packets leave it alone until it is set down.
        public static void HoldUntaken(CorpseRegistry.Entry entry)
        {
            if (!RuptureInterop.HasHandoff || entry == null) return;
            BeginTake(entry, out _);
        }

        //====================[ Adopt ]====================
        // A settled copy whose rest is known (or late): this mod's bodies off, then back to Rupture. If Rupture has taken it back
        // already (its lease ran out), its bodies are Rupture's: nothing here touches them.
        private static void HandBack(Session session, Lease lease)
        {
            if (!StillBorrowed(lease))
            {
                LeaseLost(lease, "Rupture took the body back before it was set down");
                return;
            }
            Remove(session);
            AdoptLease(lease, "set down");
        }

        // Rupture still has this body released to this mod: the final pose may go on its bones.
        private static bool StillBorrowed(Lease lease)
        {
            if (lease == null || lease.Phase != LeasePhase.Held) return false;
            RuptureInterop.Inspect(lease.Corpse, out var info);
            return info.State == HandoffState.Released;
        }

        private static void AdoptLease(Lease lease, string why)
        {
            var result = RuptureInterop.Adopt(lease.Token);
            if (result == HandoffResult.Pending)
            {
                lease.Phase = LeasePhase.Adopting;
                lease.Since = Time.time;
            }
            else EndLease(lease);

            if (result == HandoffResult.Ok || result == HandoffResult.Pending)
                DragLog.LogDebug($"[CorpseBody] {lease.Id}: handed back to Rupture ({why}, {result})");
            else
                Plugin.LogSource.LogWarning($"[CorpseBody] {lease.Id}: Rupture refused it back ({why}, {result})");
        }

        // Rupture has finished taking it back (managed again, or left to EFT's physics if its rebuild failed). True once it has.
        private static bool PollAdopting(Lease lease)
        {
            RuptureInterop.Inspect(lease.Corpse, out var info);
            if (info.State != HandoffState.Released)
            {
                EndLease(lease);
                return true;
            }
            if (Time.time - lease.Since < AdoptWaitSeconds) return false;
            EndLease(lease);
            Plugin.LogSource.LogWarning($"[CorpseBody] {lease.Id}: Rupture is still taking the body back after {AdoptWaitSeconds:F0}s; forgotten");
            return true;
        }

        // Rupture has the body back: so does Euphoria Style Physics, if this peer held it there (Remove left it held meanwhile).
        private static void EndLease(Lease lease)
        {
            Leases.Remove(lease.Id);
            EuphoriaInterop.Release(lease.Corpse);
        }

        // The lease ended under this mod. Rupture took the body back (its lease ran out: Managed again, its own bodies on the bones):
        // this peer's copy is dropped without touching them, and not taken again this raid. Rupture let go of it altogether (its
        // physics turned off or failed, which cancels the lease and drops its rig): the drag goes on in EFT's physics.
        private static void LeaseLost(Lease lease, string why)
        {
            Leases.Remove(lease.Id);
            RuptureInterop.Inspect(lease.Corpse, out var info);
            bool rupture = info.State == HandoffState.Managed || info.State == HandoffState.Preparing;
            Plugin.LogSource.LogWarning($"[CorpseBody] {lease.Id}: {why}; {(rupture ? "Rupture has it again" : "left to EFT's physics")}");
            if (!rupture || !Active.TryGetValue(lease.Id, out var session)) return;
            Failed.Add(lease.Id);
            Discard(session);
            RestoreColliders(session);
        }

        //====================[ Per Frame ]====================
        // From the driver, every frame: leases renewed, and handed back once nobody here has the body (a headless host's after its
        // longer wait for the rest, which OnRest otherwise ends).
        public static void TickLeases()
        {
            if (Leases.Count == 0) return;
            LeaseScratch.Clear();
            LeaseScratch.AddRange(Leases.Values);
            foreach (var lease in LeaseScratch)
            {
                try
                {
                    // Gone from the scene: Rupture voids the token with the corpse.
                    if (lease.Corpse == null)
                    {
                        Leases.Remove(lease.Id);
                        continue;
                    }
                    if (lease.Phase == LeasePhase.Adopting)
                    {
                        PollAdopting(lease);
                        continue;
                    }

                    bool held = CorpseClaims.IsHeld(lease.Id);
                    if (held) lease.LastHeld = Time.time;
                    if (Time.time >= lease.NextRenew)
                    {
                        lease.NextRenew = Time.time + RenewSeconds;
                        var result = RuptureInterop.Renew(lease.Token);
                        if (result != HandoffResult.Ok && result != HandoffResult.Pending)
                        {
                            LeaseLost(lease, $"Rupture's lease on the body ended ({result})");
                            continue;
                        }
                    }

                    // A session hands its lease back itself once it is set down (TickFree).
                    if (held || Active.ContainsKey(lease.Id)) continue;
                    if (lease.Phase == LeasePhase.Releasing)
                    {
                        // Let go before Rupture had released it: once it has, straight back.
                        if (PollReleasing(lease, out _) == TakeGate.Go && Leases.ContainsKey(lease.Id)) AdoptLease(lease, "let go before it was taken");
                        continue;
                    }
                    float wait = FikaBridge.IsHeadless() ? HeadlessRestWaitSeconds : 0f;
                    if (Time.time - lease.LastHeld >= wait) AdoptLease(lease, FikaBridge.IsHeadless() ? "no word on where it rests" : "not taken here");
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError($"[CorpseBody] {lease.Id}: lease tick failed: {ex}");
                    EndLease(lease);
                }
            }
        }

        private static void ClearLeases()
        {
            // A raid reset voids every token on Rupture's side.
            Leases.Clear();
            RefusedUntil.Clear();
        }
    }
}
