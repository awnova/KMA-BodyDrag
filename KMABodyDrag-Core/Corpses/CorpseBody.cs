//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using JsonType;
using UnityEngine;
using HandoffState = KMABodyDrag.Interop.RuptureInterop.HandoffState;

namespace KMABodyDrag.Corpses
{
    //====================[ CorpseBody ]====================
    // A dragged corpse's physics on this peer (KeepMeAlive's BodyRagdoll, for a body that is already dead):
    //   take    - the drag claims the corpse: EFT's own corpse cycle is stopped (it would remove the rigidbodies halfway through
    //             the drag), and the joints and rigidbodies are put back on the skeleton, zeroed at the muscle pose (CorpseBodyRig);
    //   held    - the dragger's hands carry it through the pickup into the hold frame (CorpseBodyHold);
    //   free    - let go, it falls and slides until it has stopped moving, its muscles pulling it toward KeepMeAlive's settle pose
    //             (CorpseBodyMuscles; limp while held);
    //   settled - its bodies stop, its bones are put back on their joints, and it is set down where the peer that let go of it
    //             says (CorpseRestPacket), so every peer ends on the same body; the corpse root is moved under it;
    //   parked  - kinematic until it is off screen, then the joints and rigidbodies are removed as EFT's own cycle does.
    // A corpse has no animation to go back to and no owner moving a real body underneath: the ragdoll is the body.
    // A headless host has no visuals, so it never takes a body; it only sets it down where the declarer says (OnRest), so its bots
    // find the corpse where it lies.
    internal static partial class CorpseBody
    {
        //====================[ Tuning ]====================
        // A body let go has settled once every part is slower than this (m/s) and spins less than SettleSpinPerSpeed times that;
        // SettleTimeoutSeconds settles it regardless; MinSettleSeconds lets a body dropped in the air start falling first.
        private const float SettleSpeed = 1f;
        private const float SettleSpinPerSpeed = 5f;
        private const float MinSettleSeconds = 0.3f;
        private const float SettleTimeoutSeconds = 3f;
        // How far the corpse root may be found from the pelvis's ground point (m): further, the body lies over a gap.
        private const float GroundProbe = 1.5f;

        //====================[ Session ]====================
        private sealed class Session
        {
            public CorpseRegistry.Entry Entry;
            public Rigidbody Chest;
            // The upper arms; their joints are the shoulders the dragger holds the body by.
            public Rigidbody LeftShoulder;
            public Rigidbody RightShoulder;
            // The bottom of the spine, for which way the body faces.
            public Rigidbody Pelvis;

            public readonly List<Rigidbody> Bodies = new List<Rigidbody>();
            public readonly List<(CharacterJoint Joint, Rigidbody Body)> Joints = new List<(CharacterJoint, Rigidbody)>();
            public readonly Dictionary<Rigidbody, float> PrefabMass = new Dictionary<Rigidbody, float>();
            public readonly Dictionary<Rigidbody, EBodyPartColliderType> PartOf = new Dictionary<Rigidbody, EBodyPartColliderType>();
            // Each body's own collider as the corpse had it: given back on Remove (the material) and after a snag (the layer).
            public readonly Dictionary<Collider, (PhysicMaterial Material, int Layer)> ColliderWas = new Dictionary<Collider, (PhysicMaterial, int)>();
            public readonly Dictionary<Collider, Release> Released = new Dictionary<Collider, Release>();
            // KeepMeAlive's collision model on the corpse's own colliders (CorpseBodyRig), all undone in RestoreColliders: the pairs
            // of its own colliders set to ignore each other, each limb capsule's shape as the prefab had it, and the limb spacers.
            public readonly Dictionary<EBodyPartColliderType, Rigidbody> BodyByPart = new Dictionary<EBodyPartColliderType, Rigidbody>();
            public readonly List<(Collider A, Collider B)> IgnoredPairs = new List<(Collider, Collider)>();
            public readonly Dictionary<CapsuleCollider, (Vector3 Center, float Radius, float Height)> ShapeWas =
                new Dictionary<CapsuleCollider, (Vector3, float, float)>();
            public readonly List<Collider> Spacers = new List<Collider>();

            // The muscles (CorpseBodyMuscles): a motor on each jointed body, on from the take to the removal, held or let go.
            public readonly Dictionary<Rigidbody, ConfigurableJoint> Motors = new Dictionary<Rigidbody, ConfigurableJoint>();
            public readonly List<Muscle> Muscles = new List<Muscle>();
            public readonly List<LeafBone> LeafBones = new List<LeafBone>();

            // The dragger's hands (CorpseBodyHold).
            public GameObject Hand;
            public Rigidbody HandBody;
            public ConfigurableJoint HandAngularJoint;
            public Vector3 LastHoldTarget;
            public Vector3 PickupFrom;
            public Quaternion PickupFromRotation;
            // Roll of the held body away from lying on its back. RollArmed: it has been within MaxGripRoll since, so a body still
            // being squared up is not let go before it is.
            public bool RollArmed;
            public bool Rolled;

            // When it was let go (-1 while held).
            public float FreeSince = -1f;
            // Settled and kinematic, waiting to be off screen for the removal (or, borrowed from Rupture, for the rest to hand it back).
            public bool Parked;
            public float ParkedAt;
            // Where the corpse root went when it was set down, to catch anything moving it afterwards (debug).
            public Vector3 ParkedRoot;
            // A rest from the peer that let go, come before this copy settled.
            public TransformSync[] PendingBones;
            public Vector3 PendingRoot;
        }

        // When a released collider was first let go, and the earliest it may collide again.
        private struct Release
        {
            public float Since;
            public float Until;
        }

        //====================[ State ]====================
        private static readonly Dictionary<string, Session> Active = new Dictionary<string, Session>();
        // Corpses whose take failed this raid: not tried again.
        private static readonly HashSet<string> Failed = new HashSet<string>();
        // The result of CanRig per corpse.
        private static readonly Dictionary<string, bool> RigChecked = new Dictionary<string, bool>();
        // Whose word on where a let-go body rests is awaited, per corpse (KeepMeAlive's RMPlayer.DropDeclarerId).
        private static readonly Dictionary<string, string> Declarer = new Dictionary<string, string>();
        // The frame each corpse's rigidbodies were last removed: Unity destroys a component only at the end of the frame, while the
        // spawner forgets it at once, so a take in the same frame would add a second Rigidbody (which Unity refuses).
        private static readonly Dictionary<string, int> RemovedFrame = new Dictionary<string, int>();
        private static readonly List<Session> Scratch = new List<Session>();

        //====================[ Queries ]====================
        // This peer has the body in its physics (DraggerArms takes a face-up body by the shoulder on each hand's side then).
        public static bool IsTaken(string id) => !string.IsNullOrEmpty(id) && Active.TryGetValue(id, out var s) && !s.Parked;

        public static bool TryGetChest(string id, out Vector3 position)
        {
            position = default;
            if (string.IsNullOrEmpty(id) || !Active.TryGetValue(id, out var session) || session.Chest == null) return false;
            position = session.Chest.position;
            return true;
        }

        // The held body rolled out of the dragger's hands.
        public static bool HasRolled(string id) =>
            !string.IsNullOrEmpty(id) && Active.TryGetValue(id, out var session) && session.Hand != null && session.Rolled;

        //====================[ Per Tick ]====================
        // From the dragger's tick (CorpseDragController), every peer: hold is the drag, null once the dragger is dead.
        public static void Sync(CorpseRegistry.Entry entry, DragSession hold)
        {
            if (entry == null || FikaBridge.IsHeadless()) return;
            if (!Active.TryGetValue(entry.Id, out var session))
            {
                if (hold == null || !TryTake(entry, out session)) return;
            }
            if (hold == null) return;

            // Picked up again: what the last release said no longer counts, and a parked body simulates again.
            if (session.FreeSince >= 0f || session.Parked)
            {
                Declarer.Remove(entry.Id);
                session.PendingBones = null;
            }
            if (session.Parked) Unpark(session);
            Drive(session, hold);
        }

        // From the driver, every frame: bodies no living dragger holds fall and settle (a held one is driven from its dragger's tick,
        // Sync); parked ones go once off screen.
        public static void TickFree()
        {
            if (Active.Count == 0) return;
            Scratch.Clear();
            Scratch.AddRange(Active.Values);
            foreach (var session in Scratch)
            {
                try
                {
                    if (!session.Entry.Alive)
                    {
                        Discard(session);
                        continue;
                    }
                    if (session.Parked)
                    {
                        // Borrowed from Rupture: back to it once set down where the declarer says (or that word is late).
                        if (Leases.TryGetValue(session.Entry.Id, out var lease) && lease.Phase == LeasePhase.Held)
                        {
                            if (!Declarer.ContainsKey(session.Entry.Id) || Time.time - session.ParkedAt >= RestWaitSeconds) HandBack(session, lease);
                        }
                        else if (!IsVisible(session)) Remove(session);
                        continue;
                    }
                    if (CorpseClaims.TryGetDragger(session.Entry.Id, out var draggerId)
                        && PlayerUtils.IsAlive(PlayerUtils.GetPlayerById(draggerId))) continue;
                    Drive(session, null);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError($"[CorpseBody] {session.Entry.Id}: tick failed: {ex}");
                    Discard(session);
                }
            }
        }

        //====================[ Take ]====================
        private static bool TryTake(CorpseRegistry.Entry entry, out Session session)
        {
            session = null;
            if (Failed.Contains(entry.Id) || !CanRig(entry)) return false;
            if (RemovedFrame.TryGetValue(entry.Id, out int removed) && removed == Time.frameCount) return false;
            var corpse = entry.Corpse;
            var ragdoll = corpse.Ragdoll;
            if (ragdoll == null) return false;

            // Out of Rupture's ragdoll physics before anything here touches the rigidbodies: borrowed for the drag through its
            // handoff, or for good without it (CorpseBodyLease, RuptureInterop). Waits while Rupture is letting go.
            if (BeginTake(entry, out var lease) != TakeGate.Go) return false;
            // Out of Euphoria Style Physics' corpse waking and dying motion while this peer has the body (given back when the session
            // ends, Remove/Discard): it would freeze the held body, and its dying motion would fight the hold (EuphoriaInterop).
            EuphoriaInterop.Acquire(corpse);
            // EFT's corpse cycle would make the bodies kinematic once they sleep and remove them once off screen, in the middle of a
            // drag; Fika's observed corpse runs its own finale. From here this mod removes them (Remove).
            corpse.StopAllCoroutines();
            // A claim from another peer on a body still falling here: stopped where it is, as it would have come to rest.
            if (!ragdoll._isPhysicsDone) ragdoll.ForceStopRigidBody();
            // A weapon still hung off the forearm would be a dead weight on the arm (EFT drops it there itself once off screen). One
            // Rupture simulates off the hand is not on the arm, and clearing it would take it from Rupture for good.
            if (lease == null || !lease.WeaponIndependent) ragdoll.ClearWeapon();

            session = new Session { Entry = entry };
            Active[entry.Id] = session;
            try
            {
                SpawnPhysics(entry.Pool, session, lease?.SeveredRoots);
                if (session.Chest == null || session.LeftShoulder == null || session.RightShoulder == null)
                    throw new InvalidOperationException("ragdoll has no chest or shoulder bodies");
                var pose = ZeroJoints(entry.Player, session);
                // KeepMeAlive's order: the muscles are measured on bodies that are active and seated on their joints.
                ActivateBodies(session);
                SeatOnJoints(session);
                try
                {
                    BuildMuscles(session, pose, lease?.SeveredRoots);
                }
                catch (Exception ex)
                {
                    // Without muscle tone the body is just limp: not worth giving up the drag for (KeepMeAlive's StartPhysics).
                    DestroyMotors(session);
                    Plugin.LogSource.LogWarning($"[CorpseBody] {entry.Id}: no muscle tone: {ex.Message}");
                }
                DragLog.LogDebug($"[CorpseBody] {entry.Id}: taken ({session.Bodies.Count} bodies, {session.Joints.Count} joints, " +
                    $"{session.Muscles.Count} muscles, {session.Spacers.Count} spacers, {session.IgnoredPairs.Count} ignored pairs)");
                return true;
            }
            catch (Exception ex)
            {
                Failed.Add(entry.Id);
                Plugin.LogSource.LogError($"[CorpseBody] {entry.Id}: take failed: {ex}");
                Remove(session);
                if (lease != null && Leases.ContainsKey(entry.Id)) AdoptLease(lease, "take failed");
                session = null;
                return false;
            }
        }

        //====================[ Drive ]====================
        // One tick of the physics: held by the dragger's hands, or lying free until it settles.
        private static void Drive(Session session, DragSession drag)
        {
            if (!session.Chest.position.IsFinite())
            {
                Plugin.LogSource.LogWarning($"[CorpseBody] {session.Entry.Id}: the simulation produced an invalid position; settled where it is");
                Settle(session);
                return;
            }

            float step = PhysicsStep();
            FixSnags(session);
            UpdateReleased(session);
            ApplySpeedLimits(session);
            // Held or let go, the same muscles (KeepMeAlive's Drive).
            UpdateMuscleDrives(session);
            PoseLeafBones(session);

            if (drag == null)
            {
                if (session.Hand != null) DestroyHand(session);
                if (session.FreeSince < 0f) session.FreeSince = Time.time;
                if (HasSettled(session)) Settle(session);
                return;
            }
            session.FreeSince = -1f;
            DriveHold(session, drag, step);
        }

        // Whether a body let go has come to rest: every part slower than the threshold, in speed and in spin.
        private static bool HasSettled(Session session)
        {
            float free = Time.time - session.FreeSince;
            if (free < MinSettleSeconds) return false;
            if (free >= SettleTimeoutSeconds) return true;

            float spin = SettleSpeed * SettleSpinPerSpeed;
            foreach (var body in session.Bodies)
            {
                if (body.velocity.sqrMagnitude > SettleSpeed * SettleSpeed || body.angularVelocity.sqrMagnitude > spin * spin) return false;
            }
            return true;
        }

        //====================[ Settle ]====================
        // Still: the bodies stop, the bones go back on their joints (the drag's stretch dropped), and the body is set down where the
        // one who let go of it says, or as this peer's own physics left it until that word comes.
        private static void Settle(Session session)
        {
            var entry = session.Entry;
            StopBodies(session);
            entry.Corpse.Ragdoll?.GetPartsTogether();
            PhysicsExtensions.SyncTransforms();

            TransformSync[] bones = entry.Corpse.GetTransformSync();
            Vector3 root = GroundUnder(session);
            bool declared = CorpseDragController.OnBodySettled(entry.Id, root, bones);
            if (declared) Declarer.Remove(entry.Id);
            else if (session.PendingBones != null)
            {
                bones = session.PendingBones;
                root = session.PendingRoot;
                session.PendingBones = null;
                Declarer.Remove(entry.Id);
            }

            ApplyRest(session, root, bones);
            session.Parked = true;
            session.ParkedAt = Time.time;
            DragLog.LogDebug($"[CorpseBody] {entry.Id}: settled at {root} ({(declared ? "declared it" : "own physics or the declarer's word")}), parked");
        }

        // The bodies stop and go kinematic (KeepMeAlive's StopPhysics); the hand lets go. They stay on the skeleton until Remove.
        private static void StopBodies(Session session)
        {
            // The motors stay on (KeepMeAlive switches them off only to remove them): a kinematic body does not feel them, and a
            // body picked up again before it is removed has its muscles at once.
            DestroyHand(session);
            foreach (var body in session.Bodies)
            {
                if (body == null) continue;
                PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
                // Velocity can only be cleared while dynamic; kinematic bodies reject the write.
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                // Collision mode first: a body going kinematic must not be left on continuous.
                body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                body.isKinematic = true;
            }
            foreach (var collider in session.Released.Keys) RestoreLayer(session, collider);
            session.Released.Clear();
            session.FreeSince = -1f;
            session.RollArmed = session.Rolled = false;
        }

        // A parked body picked up again: it simulates again on the joints it already has.
        private static void Unpark(Session session)
        {
            session.Parked = false;
            ActivateBodies(session);
            DragLog.LogDebug($"[CorpseBody] {session.Entry.Id}: picked up again before it was removed");
        }

        //====================[ Rest ]====================
        // A body was let go of (every peer, from the release): whose word on where it comes to rest to wait for. A release with no
        // declarer (before the lift, a refused claim) changes nothing (KeepMeAlive's ExpectDropRest).
        public static void ExpectRest(string corpseId, string draggerId, string declarerId)
        {
            if (string.IsNullOrEmpty(corpseId) || string.IsNullOrEmpty(declarerId)) return;
            if (CorpseClaims.IsHeld(corpseId)) return;
            // The dragger's word is the one when two peers let go at once.
            if (Declarer.TryGetValue(corpseId, out var current) && current != declarerId && declarerId != draggerId) return;
            Declarer[corpseId] = declarerId;
        }

        // Where a body let go of came to rest (every peer but the declarer, a headless host included): only from the one the
        // release named, and not once someone has picked it up again. Set down at once if this copy has settled (or never moved),
        // else when it does.
        public static void OnRest(string corpseId, string declarerId, Vector3 root, TransformSync[] bones)
        {
            if (CorpseClaims.IsHeld(corpseId)) return;
            if (!Declarer.TryGetValue(corpseId, out var expected) || expected != declarerId) return;
            if (!CorpseRegistry.TryGet(corpseId, out var entry)) return;
            if (bones == null || bones.Length != entry.Pool.RigidbodySpawners.Length)
            {
                Plugin.LogSource.LogWarning($"[CorpseBody] {corpseId}: rest from {declarerId} has {bones?.Length ?? 0} bones, this body {entry.Pool.RigidbodySpawners.Length}; ignored");
                return;
            }

            if (Active.TryGetValue(corpseId, out var session) && !session.Parked)
            {
                session.PendingBones = bones;
                session.PendingRoot = root;
                return;
            }

            Declarer.Remove(corpseId);
            // Parked here: a body borrowed from Rupture goes back to it from TickFree.
            if (session != null)
            {
                ApplyRest(session, root, bones);
                DragLog.LogDebug($"[CorpseBody] {corpseId}: set down where {declarerId} says");
                return;
            }
            if (!SetDownUntaken(entry, declarerId)) return;
            ApplyRest(entry, root, bones);
            DragLog.LogDebug($"[CorpseBody] {corpseId}: set down where {declarerId} says");
            // Borrowed from Rupture without being taken (a headless host): back to it from where it now lies.
            if (Leases.TryGetValue(corpseId, out var lease)) AdoptLease(lease, "set down");
        }

        // A corpse this peer never took (a headless host, a failed take), about to be set down by the declarer's word. Whether its
        // bones may be written: with Rupture's handoff, only while it is released to this mod or Rupture does not manage it (a
        // body Rupture took back, by its lease running out or by a timeout here, follows Rupture's host instead). Without it, out
        // of Rupture's physics first, or a wake would put it back.
        private static bool SetDownUntaken(CorpseRegistry.Entry entry, string declarerId)
        {
            if (!RuptureInterop.HasHandoff)
            {
                StopUntaken(entry);
                return true;
            }
            if (Leases.TryGetValue(entry.Id, out var lease))
            {
                if (StillBorrowed(lease)) return true;
            }
            else
            {
                // Unavailable: Rupture's physics is off or not running for it (CorpseBodyLease.MayBorrow).
                RuptureInterop.Inspect(entry.Corpse, out var info);
                if (info.State == HandoffState.Unmanaged || info.State == HandoffState.Unavailable) return true;
            }
            DragLog.LogDebug($"[CorpseBody] {entry.Id}: Rupture has the body; the rest from {declarerId} is not written");
            return false;
        }

        // A corpse this peer never took, set down by the declarer's word: Rupture lets go of it (RuptureInterop), which hands its
        // rigidbodies back dynamic; they are stopped where they lie, as EFT's own cycle stops them (CorpseRagdoll.StopRigidbody).
        // Left to EFT from there: its cycle, if still running, removes them once off screen.
        private static void StopUntaken(CorpseRegistry.Entry entry)
        {
            if (!RuptureInterop.IsTracked(entry.Corpse)) return;
            RuptureInterop.Release(entry.Corpse);
            foreach (var spawner in entry.Pool.RigidbodySpawners)
            {
                var body = spawner.Rigidbody;
                if (body == null) continue;
                for (int i = 0; i < MaxSupports; i++) PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
                body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                body.isKinematic = true;
            }
        }

        private static void ApplyRest(Session session, Vector3 root, TransformSync[] bones)
        {
            ApplyRest(session.Entry, root, bones);
            session.ParkedRoot = root;
        }

        // The corpse root goes under the body, then every bone back to its world pose: anything that reads the corpse's own
        // position (its loot item, culling, bot looting) finds it where the body lies.
        private static void ApplyRest(CorpseRegistry.Entry entry, Vector3 root, TransformSync[] bones)
        {
            if (bones == null || bones.Length != entry.Pool.RigidbodySpawners.Length) return;
            entry.Corpse.transform.position = root;
            // Corpse.ApplyTransformSync moves each ragdoll bone's transform to its world pose, parents first.
            entry.Corpse.ApplyTransformSync(bones);
            PhysicsExtensions.SyncTransforms();
        }

        // Where the corpse root belongs: the ground under the pelvis, or the pelvis itself over a gap.
        private static Vector3 GroundUnder(Session session)
        {
            Vector3 pelvis = session.Pelvis != null ? session.Pelvis.position : session.Chest.position;
            Vector3 from = pelvis + Vector3.up * 0.5f;
            return Physics.Raycast(from, Vector3.down, out var hit, GroundProbe + 0.5f, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)
                ? hit.point
                : pelvis;
        }

        //====================[ Remove ]====================
        private static bool IsVisible(Session session)
        {
            var body = session.Entry.Player != null ? session.Entry.Player.PlayerBody : null;
            return body != null && body.IsVisible();
        }

        // Off screen (or a failed take): the joints and rigidbodies come off the skeleton as EFT's own cycle removes them, the
        // colliders get their own material and layer back, and the session ends.
        private static void Remove(Session session)
        {
            var entry = session.Entry;
            Active.Remove(entry.Id);
            DestroyHand(session);
            DestroyMotors(session);
            foreach (var body in session.Bodies)
                if (body != null) PhysicsExtensions.UpdateController.UnsupportRigidbody(body);

            if (entry.Alive)
            {
                if (DragLog.IsDebugLogsEnabled && session.Parked && (entry.Corpse.transform.position - session.ParkedRoot).sqrMagnitude > 0.25f)
                    Plugin.LogSource.LogWarning($"[CorpseBody] {entry.Id}: the corpse root moved from {session.ParkedRoot} to {entry.Corpse.transform.position} after it was set down");

                foreach (var spawner in entry.Pool.JointSpawners) spawner.Remove();
                foreach (var spawner in entry.Pool.RigidbodySpawners) spawner.Remove();
                RemovedFrame[entry.Id] = Time.frameCount;
            }
            RestoreColliders(session);
            // A body going back to Rupture is given back to Euphoria only once Rupture has its bodies on again (AdoptLease): with
            // none, Euphoria would rebuild the ragdoll itself.
            if (!Leases.ContainsKey(entry.Id)) EuphoriaInterop.Release(entry.Corpse);
            DragLog.LogDebug($"[CorpseBody] {entry.Id}: removed");
        }

        // Each body's collider gets its own material and layer back (before a handback too: Rupture builds its ragdoll from the
        // colliders as they are).
        private static void RestoreColliders(Session session)
        {
            foreach (var pair in session.ColliderWas)
            {
                if (pair.Key == null) continue;
                pair.Key.sharedMaterial = pair.Value.Material;
                pair.Key.gameObject.layer = pair.Value.Layer;
            }
            // KeepMeAlive's collision model (CorpseBodyRig) undone: the corpse's colliders collide with each other again, with the
            // prefab's own shapes, and the spacers go.
            foreach (var (a, b) in session.IgnoredPairs)
                if (a != null && b != null) Physics.IgnoreCollision(a, b, false);
            session.IgnoredPairs.Clear();
            foreach (var shape in session.ShapeWas)
            {
                if (shape.Key == null) continue;
                shape.Key.center = shape.Value.Center;
                shape.Key.radius = shape.Value.Radius;
                shape.Key.height = shape.Value.Height;
            }
            session.ShapeWas.Clear();
            DestroySpacers(session);
        }

        // The corpse left the scene: only what lives outside it to clean up.
        private static void Discard(Session session)
        {
            Active.Remove(session.Entry.Id);
            DestroyHand(session);
            DestroyMotors(session);
            DestroySpacers(session);
            foreach (var body in session.Bodies)
                if (body != null) PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
            EuphoriaInterop.Release(session.Entry.Corpse);
        }

        //====================[ Raid Boundary ]====================
        public static void ClearAll()
        {
            Scratch.Clear();
            Scratch.AddRange(Active.Values);
            foreach (var session in Scratch)
            {
                try { Discard(session); }
                catch (Exception ex) { Plugin.LogSource.LogWarning($"[CorpseBody] cleanup failed: {ex.Message}"); }
            }
            Active.Clear();
            Failed.Clear();
            RigChecked.Clear();
            Declarer.Clear();
            RemovedFrame.Clear();
            ClearLeases();
            EuphoriaInterop.Clear();
        }
    }
}
