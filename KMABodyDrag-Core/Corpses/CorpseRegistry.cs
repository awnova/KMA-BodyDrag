//====================[ Imports ]====================
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using UnityEngine;

namespace KMABodyDrag.Corpses
{
    //====================[ CorpseRegistry ]====================
    // Every corpse in the raid, by the dead player's profile id (the same on every peer). A corpse is the dead player's own
    // GameObject with a Corpse added (Player.CreateCorpse), so its Player and PlayerBones are still there for the grip and the
    // arms; but the game unregisters the player a frame after death, so it is looked up here, never through the game world.
    internal static class CorpseRegistry
    {
        //====================[ Entry ]====================
        internal sealed class Entry
        {
            public string Id;
            public Corpse Corpse;
            public Player Player;
            public PlayerPoolObject Pool;
            // When the corpse was made (Corpse.Init), for HasLanded.
            public float MadeAt;

            // The scene still has it (a despawn limit or the raid end destroys the object).
            public bool Alive => Corpse != null && Player != null && Pool != null;
        }

        //====================[ State ]====================
        private static readonly Dictionary<string, Entry> ById = new Dictionary<string, Entry>();
        private static readonly Dictionary<Corpse, Entry> ByCorpse = new Dictionary<Corpse, Entry>();

        //====================[ Registry ]====================
        // From CorpseInitPatch, for every corpse the game makes (a death here, or a still corpse loaded with the map).
        public static void Register(Corpse corpse)
        {
            if (corpse == null || string.IsNullOrEmpty(corpse.PlayerProfileID)) return;
            var player = corpse.GetComponent<Player>();
            var pool = corpse.GetComponent<PlayerPoolObject>();
            if (player == null || pool == null)
            {
                DragLog.LogDebug($"[Corpse] {corpse.PlayerProfileID}: no player or pool object on the corpse, not draggable");
                return;
            }

            if (ById.TryGetValue(corpse.PlayerProfileID, out var old) && old.Corpse != null) ByCorpse.Remove(old.Corpse);
            var entry = new Entry { Id = corpse.PlayerProfileID, Corpse = corpse, Player = player, Pool = pool, MadeAt = Time.time };
            ById[entry.Id] = entry;
            ByCorpse[corpse] = entry;
            DragLog.LogDebug($"[Corpse] registered {entry.Id} (zombie={corpse.IsZombieCorpse}, ragdoll={corpse.HasRagdoll})");
        }

        public static bool TryGet(string id, out Entry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(id) || !ById.TryGetValue(id, out entry)) return false;
            if (entry.Alive) return true;
            Forget(entry);
            entry = null;
            return false;
        }

        public static bool TryGet(Corpse corpse, out Entry entry)
        {
            entry = null;
            if (corpse == null || !ByCorpse.TryGetValue(corpse, out entry)) return false;
            if (entry.Alive) return true;
            Forget(entry);
            entry = null;
            return false;
        }

        // Landed, and with a skeleton the hold can use. A body still falling is picked up once it has landed, as KeepMeAlive's
        // drag waits for the fall; a boarded one (the train) is left where it rides. Rupture must let this mod have it (not someone
        // else's, the grip's limbs not severed), and no other mod may hold it through Euphoria Style Physics' lease.
        public static bool IsReady(Entry entry) =>
            entry != null && entry.Alive && entry.Corpse.HasRagdoll && entry.Corpse.Ragdoll != null
            && entry.Corpse.Platform == null && HasLanded(entry)
            && DragGrip.For(entry.Player) != null && CorpseBody.CanRig(entry) && CorpseBody.MayBorrow(entry)
            && !EuphoriaInterop.IsHeldByOther(entry.Corpse);

        //====================[ Landed ]====================
        // Below this every part of the body counts as at rest (m/s, rad/s).
        private const float RestSpeed = 0.3f;
        private const float RestSpin = 1.5f;
        // EFT's own corpse cycle waits this long before it first asks whether the body is still (CorpseRagdoll.WorkingCycle).
        private const float MinLandedSeconds = 1f;

        // EFT says its corpse physics is done, or every part has come to rest. EFT's word alone is not enough: on a Fika client
        // every corpse is an ObservedCorpse, which counts as still only once the host has said so (CheckCorpseIsStill: _isDone,
        // from a CorpseSyncPacket) or after 30 s, and Fika 2.4.6 sends no corpse packets at all, so a client would wait 30 s for
        // every body. The take stops a body that is not done yet where it is (CorpseRagdoll.ForceStopRigidBody).
        // A body Rupture is moving in its own scene has kinematic rigidbodies meanwhile, which would read as at rest: it has landed
        // once Rupture's shadow of it sleeps (at most Rupture's Ragdoll Sleep Time).
        private static bool HasLanded(Entry entry)
        {
            if (RuptureInterop.IsSimulating(entry.Corpse)) return false;
            var ragdoll = entry.Corpse.Ragdoll;
            if (ragdoll._isPhysicsDone) return true;
            if (Time.time - entry.MadeAt < MinLandedSeconds) return false;
            foreach (var spawner in entry.Pool.RigidbodySpawners)
            {
                var body = spawner.Rigidbody;
                if (body == null || body.isKinematic) continue;
                if (body.velocity.sqrMagnitude > RestSpeed * RestSpeed || body.angularVelocity.sqrMagnitude > RestSpin * RestSpin) return false;
            }
            return true;
        }

        // Raid boundary.
        public static void Clear()
        {
            ById.Clear();
            ByCorpse.Clear();
        }

        // The corpse is gone from the scene: so is anything holding it.
        private static void Forget(Entry entry)
        {
            ById.Remove(entry.Id);
            if (entry.Corpse != null) ByCorpse.Remove(entry.Corpse);
            DragLog.LogDebug($"[Corpse] {entry.Id} is gone from the scene");
        }
    }
}
