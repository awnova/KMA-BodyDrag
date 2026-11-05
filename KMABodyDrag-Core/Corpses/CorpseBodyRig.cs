//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using UnityEngine;

namespace KMABodyDrag.Corpses
{
    //====================[ CorpseBody: Rig ]====================
    // Putting the corpse's rigidbodies and joints back on its skeleton (EFT removes them once the corpse has settled and is off
    // screen, CorpseRagdoll.WorkingCycle), from the same spawners EFT's corpse ragdoll and KeepMeAlive's BodyRagdoll use, with
    // KeepMeAlive's human segment masses, anatomical joint limits and solver settings, and its collision model on the corpse's own
    // colliders (they stay the world contact, the hit boxes and what the loot ray finds): none of them collide with each other,
    // the limb capsules take KeepMeAlive's symmetric contact shapes, and slim limb spacers keep the legs apart and the arms out
    // of the chest. All of it is undone in RestoreColliders.
    internal static partial class CorpseBody
    {
        //====================[ Tuning ]====================
        // How hard the ground slows a sliding limb (m/s²) (KeepMeAlive's GroundFriction).
        private const float GroundFriction = 9.5f;

        // Solver passes per ragdoll body (the game default is 3/1); more keeps the joints tight under the pull.
        private const int SolverIterations = 12;
        private const int SolverVelocityIterations = 4;

        // KeepMeAlive's speed caps (BodyRagdollSpeed): the solver's own limits, applied inside the physics step, so a limb whipped
        // round by a turning dragger does not fly.
        private const float MaxPartSpeed = 3f;      // m/s, each part relative to the pelvis
        private const float MaxPartSpin = 10f;      // rad/s
        private const float MaxPelvisSpeed = 12f;   // m/s

        private static PhysicMaterial _contactMaterial;

        // More supports than anything puts on one rigidbody (EFT's corpse cycle one, Rupture hands back what it took).
        private const int MaxSupports = 4;

        //====================[ Rig Check ]====================
        // Whether a corpse's skeleton can be rigged: a chest, both upper arms, and the muscle pose fits it (a simplified zombie
        // skeleton may not). ClipRig logs its own reason once per player.
        public static bool CanRig(CorpseRegistry.Entry entry)
        {
            if (entry == null || !entry.Alive) return false;
            if (RigChecked.TryGetValue(entry.Id, out bool ok)) return ok;

            ok = false;
            try
            {
                bool chest = false, left = false, right = false;
                foreach (var spawner in entry.Pool.RigidbodySpawners)
                {
                    var part = spawner.GetComponent<BodyPartCollider>();
                    if (part == null) continue;
                    switch (part.BodyPartColliderType)
                    {
                        case EBodyPartColliderType.RibcageUp: chest = true; break;
                        case EBodyPartColliderType.LeftUpperArm: left = true; break;
                        case EBodyPartColliderType.RightUpperArm: right = true; break;
                    }
                }
                ok = chest && left && right && BakedClip.Settle != null && ClipRig.TryBuild(entry.Player, BakedClip.Settle) != null;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[CorpseBody] {entry.Id}: rig check failed: {ex.Message}");
            }
            RigChecked[entry.Id] = ok;
            if (!ok) Plugin.LogSource.LogInfo($"[CorpseBody] {entry.Id}: this body's skeleton cannot be dragged");
            return ok;
        }

        //====================[ Spawn ]====================
        // Creates (idempotently) and arms the joints and rigidbodies, the way CorpseRagdoll.Start does, with realistic human segment
        // masses so the limbs do not outweigh the trunk (KeepMeAlive's SpawnPhysics; its contact colliders are the corpse's own,
        // reshaped, rather than copies).
        // Nothing under a limb Rupture severed (severed): it has no body or joint there. A joint spawner creates its own body and
        // its parent's, never a child's, so skipping every spawner under those bones leaves them alone.
        private static void SpawnPhysics(PlayerPoolObject pool, Session session, Transform[] severed)
        {
            PhysicMaterial material = ContactMaterial();

            foreach (var spawner in pool.RigidbodySpawners)
            {
                if (IsSevered(spawner.transform, severed)) continue;
                Rigidbody body = spawner.Create();
                // Out of the game's physics step until ActivateBodies supports it once: whatever supported it before (EFT's corpse
                // cycle, Rupture handing it back) lets go here. The step keeps a list entry per support, and Unsupport takes one off,
                // or does nothing when there is none.
                for (int i = 0; i < MaxSupports; i++) PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
                var part = spawner.GetComponent<BodyPartCollider>()?.BodyPartColliderType ?? EBodyPartColliderType.None;

                // Still while the joints are zeroed (ZeroJoints steps the physics): Create gives the prefab's own setting, and a body
                // EFT left behind may be either. Collision mode first: a kinematic body must not be on continuous.
                body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                body.isKinematic = true;

                session.Bodies.Add(body);
                // The prefab's mass (what Create gives), not the body's: EFT's corpse keeps it, but say so outright.
                session.PrefabMass[body] = spawner.mass;
                body.mass = HumanSegmentMass(part);
                session.PartOf[body] = part;
                if (part != EBodyPartColliderType.None) session.BodyByPart[part] = body;

                // The ground grip and shape of KeepMeAlive's contact colliders go on the corpse's own colliders, given back on Remove.
                var collider = body.GetComponent<Collider>();
                if (collider != null)
                {
                    session.ColliderWas[collider] = (collider.sharedMaterial, collider.gameObject.layer);
                    collider.sharedMaterial = material;
                    if (collider is CapsuleCollider capsule) SetContactShape(session, capsule, part);
                }

                // The dragger holds the body by the shoulders of the upper chest.
                switch (part)
                {
                    case EBodyPartColliderType.RibcageUp: session.Chest = body; break;
                    case EBodyPartColliderType.LeftUpperArm: session.LeftShoulder = body; break;
                    case EBodyPartColliderType.RightUpperArm: session.RightShoulder = body; break;
                    case EBodyPartColliderType.Pelvis: session.Pelvis = body; break;
                }
            }

            foreach (var spawner in pool.JointSpawners)
            {
                if (IsSevered(spawner.transform, severed)) continue;
                // CharacterJointSpawner always creates a CharacterJoint.
                var joint = (CharacterJoint)spawner.Create();
                var own = joint.GetComponent<Rigidbody>();
                session.Joints.Add((joint, own));

                // Held at the prefab's anchors (KeepMeAlive: autoConfigureConnectedAnchor would work the anchor out again from a body
                // left out of shape, moving the shoulders a little further each time).
                joint.autoConfigureConnectedAnchor = false;
                joint.anchor = spawner.anchor;
                joint.connectedAnchor = spawner.connectedAnchor;

                // CorpseRagdoll.Start: no preprocessing, projection on so a stretched joint is pulled back instead of springing,
                // mass-ratio scaling from the prefab masses.
                joint.enablePreprocessing = false;
                joint.enableProjection = true;
                joint.projectionDistance = 0.05f;
                joint.projectionAngle = 10f;
                joint.massScale = PrefabMassOf(session, joint.connectedBody) / PrefabMassOf(session, own);
                joint.connectedMassScale = 1f;
            }

            // After the joints: the spacers need to know which bodies are jointed to which.
            IgnoreInternalCollisions(session);
            AddLimbSpacers(session, material);
        }

        //====================[ Contact Shapes ]====================
        // KeepMeAlive's contact shapes (its CopyShape), symmetric left and right: EFT's prefab has a thicker, longer right calf than
        // left and the forearms' centres differ, so the legs would trail unevenly. The prefab's shape is kept for RestoreColliders.
        private static void SetContactShape(Session session, CapsuleCollider capsule, EBodyPartColliderType part)
        {
            Vector3 center;
            float radius, height;
            switch (part)
            {
                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.RightThigh:
                    center = new Vector3(-0.228f, 0f, 0f); radius = 0.095f; height = 0.485f; break;
                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightCalf:
                    center = new Vector3(-0.240f, 0f, 0f); radius = 0.088f; height = 0.495f; break;
                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.RightUpperArm:
                    center = new Vector3(-0.148f, 0f, 0f); radius = 0.074f; height = 0.296f; break;
                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm:
                    center = new Vector3(-0.165f, 0f, 0f); radius = 0.045f; height = 0.305f; break;
                default:
                    return;
            }
            if (!session.ShapeWas.ContainsKey(capsule)) session.ShapeWas[capsule] = (capsule.center, capsule.radius, capsule.height);
            capsule.center = center;
            capsule.radius = radius;
            capsule.height = height;
        }

        //====================[ Internal Collisions ]====================
        // None of the corpse's own colliders collide with each other (KeepMeAlive's IgnoreInternalCollisions): on the Deadbody layer
        // they would, and neighbours that overlap at a joint, or the head against the shoulders, shove the chest the dragger's hands
        // follow. Every pair is kept so RestoreColliders can put it back.
        private static void IgnoreInternalCollisions(Session session)
        {
            var all = new List<Collider>(session.ColliderWas.Keys);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null) continue;
                for (int j = i + 1; j < all.Count; j++)
                {
                    if (all[j] == null || Physics.GetIgnoreCollision(all[i], all[j])) continue;
                    Physics.IgnoreCollision(all[i], all[j], true);
                    session.IgnoredPairs.Add((all[i], all[j]));
                }
            }
        }

        //====================[ Limb Spacers ]====================
        // KeepMeAlive's limb spacers, with its shapes: with no collisions inside the body the legs would pass through each other and
        // the arms into the chest. A spacer is a slimmer copy of a limb, kept off the joint ends, so a body in the muscle pose starts
        // clear. KeepMeAlive puts them on a layer that meets only its contacts; a corpse's contacts are its own Deadbody colliders,
        // so the spacers are Deadbody too and ignore what KeepMeAlive's never meet: their own body, the bodies jointed to it, and the
        // other spacers. Being Deadbody they also meet the world, so each is registered in ColliderWas: a snagged limb's spacer is
        // released through the world with it (ReleaseFromWorld), and it lies inside its limb's capsule, so it adds no contact.
        private const string SpacerObjectName = "KMABodyDrag_Spacer";

        private static readonly (EBodyPartColliderType Part, float Center, float Height, float Radius)[] SpacerShapes =
        {
            // Along each bone's -x (m): centre, length end to end, radius.
            (EBodyPartColliderType.LeftThigh, -0.26f, 0.40f, 0.07f),
            (EBodyPartColliderType.LeftCalf, -0.26f, 0.42f, 0.07f),
            (EBodyPartColliderType.LeftUpperArm, -0.17f, 0.25f, 0.06f),
            (EBodyPartColliderType.RightUpperArm, -0.17f, 0.25f, 0.06f),
            (EBodyPartColliderType.LeftForearm, -0.17f, 0.28f, 0.04f),
            (EBodyPartColliderType.RightForearm, -0.17f, 0.28f, 0.04f)
        };

        private static void AddLimbSpacers(Session session, PhysicMaterial material)
        {
            var neighbours = new Dictionary<Rigidbody, HashSet<Rigidbody>>();
            void Link(Rigidbody a, Rigidbody b)
            {
                if (!neighbours.TryGetValue(a, out var set)) neighbours[a] = set = new HashSet<Rigidbody>();
                set.Add(b);
            }
            foreach (var (joint, body) in session.Joints)
            {
                var parent = joint.connectedBody;
                if (parent == null || body == null) continue;
                Link(body, parent);
                Link(parent, body);
            }

            // The body's own colliders, before any spacer is added.
            var contacts = new List<Collider>(session.ColliderWas.Keys);
            int layer = LayersMaskController.DeadbodyLayer;

            foreach (var shape in SpacerShapes)
            {
                // A severed limb has no body here.
                if (!session.BodyByPart.TryGetValue(shape.Part, out var body) || body == null) continue;

                var host = new GameObject(SpacerObjectName) { layer = layer };
                host.transform.SetParent(body.transform, false);
                var spacer = host.AddComponent<CapsuleCollider>();
                spacer.direction = 0;
                spacer.center = new Vector3(shape.Center, 0f, 0f);
                spacer.height = shape.Height;
                spacer.radius = shape.Radius;
                spacer.sharedMaterial = material;

                neighbours.TryGetValue(body, out var near);
                foreach (var contact in contacts)
                {
                    if (contact == null) continue;
                    var other = contact.attachedRigidbody;
                    if (other == body || (near != null && near.Contains(other))) Physics.IgnoreCollision(spacer, contact, true);
                }
                foreach (var otherSpacer in session.Spacers)
                    if (otherSpacer != null) Physics.IgnoreCollision(spacer, otherSpacer, true);

                session.Spacers.Add(spacer);
                session.ColliderWas[spacer] = (material, layer);
            }
        }

        // Deactivated first: Destroy only takes effect at the end of the frame, and Rupture (or EFT) may read the corpse's colliders
        // before then.
        private static void DestroySpacers(Session session)
        {
            foreach (var spacer in session.Spacers)
            {
                if (spacer == null) continue;
                session.ColliderWas.Remove(spacer);
                session.Released.Remove(spacer);
                spacer.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(spacer.gameObject);
            }
            session.Spacers.Clear();
        }

        private static float PrefabMassOf(Session session, Rigidbody body) =>
            body != null && session.PrefabMass.TryGetValue(body, out float mass) ? mass : body != null ? body.mass : 1f;

        //====================[ Joint Zero ]====================
        private const float JointZeroStep = 0.001f;

        // Zeroes every CharacterJoint at KeepMeAlive's muscle pose, with its anatomical limits, while every body is still kinematic,
        // then puts the bones back as they lay. A joint's limits are measured from where its bodies were when it was set up: zeroed
        // on a crumpled corpse, a limb already bent to its limit could bend no further one way and twice as far the other. The
        // muscle motors are made in the same pose, so it is their target (CorpseBodyMuscles). Returns the pose for BuildMuscles.
        private static MusclePose.SkeletonCopy ZeroJoints(Player player, Session session)
        {
            var pose = MusclePose.Build(player);
            Transform pelvis = PlayerUtils.GetPelvis(player);
            Transform[] all = pelvis.GetComponentsInChildren<Transform>(true);
            var savedRot = new Quaternion[all.Length];
            var savedPos = new Vector3[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                savedRot[i] = all[i].localRotation;
                savedPos[i] = all[i].localPosition;
            }

            try
            {
                foreach (var bone in all)
                {
                    if (bone == pelvis || !pose.Has(bone) || !pose.Has(bone.parent)) continue;
                    bone.localRotation = Quaternion.Inverse(pose.Rotation(bone.parent)) * pose.Rotation(bone);
                }
                PhysicsExtensions.SyncTransforms();

                foreach (var (joint, body) in session.Joints)
                {
                    var parent = joint.connectedBody;
                    if (parent == null) continue;
                    if (session.PartOf.TryGetValue(body, out var part)) ConfigureAnatomicalJoint(joint, part);
                    // Connecting again sets the joint up from the pose the bodies are in now.
                    Vector3 anchor = joint.connectedAnchor;
                    joint.connectedBody = null;
                    joint.connectedBody = parent;
                    joint.connectedAnchor = anchor;
                }
                CreateMotors(session);

                PhysicsExtensions.SyncTransforms();
                PhysicsExtensions.Simulation.Simulate(JointZeroStep);
            }
            finally
            {
                for (int i = 0; i < all.Length; i++)
                {
                    all[i].localPosition = savedPos[i];
                    all[i].localRotation = savedRot[i];
                }
                PhysicsExtensions.SyncTransforms();
            }
            return pose;
        }

        //====================[ Activate ]====================
        // The bodies simulate (KeepMeAlive's ActivateBodies), and the game's manual physics step runs while they are registered.
        private static void ActivateBodies(Session session)
        {
            float maxDepenetration = EFTHardSettings.Instance.CorpseMaxDepenetrationVelocity;
            foreach (var body in session.Bodies)
            {
                body.isKinematic = false;
                body.maxDepenetrationVelocity = maxDepenetration;
                // Continuous against the static world: a limb whipped round by a turning dragger is fast and small enough to step
                // through a thin floor otherwise.
                body.collisionDetectionMode = CollisionDetectionMode.Continuous;
                body.solverIterations = SolverIterations;
                body.solverVelocityIterations = SolverVelocityIterations;
                // A body that falls asleep stops following the hold.
                body.sleepThreshold = 0f;
                body.maxAngularVelocity = MaxPartSpin;
                body.drag = 0.35f;
                body.angularDrag = 1.6f;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                PhysicsExtensions.UpdateController.SupportRigidbody(body, 0f);
            }
        }

        // Moves each body onto its joint (parent first, so what hangs below follows), so the physics starts from a pose its joints
        // agree with.
        private static void SeatOnJoints(Session session)
        {
            bool moved = false;
            foreach (var (joint, body) in session.Joints)
            {
                var parent = joint.connectedBody;
                if (parent == null) continue;
                Vector3 gap = parent.transform.TransformPoint(joint.connectedAnchor) - body.transform.TransformPoint(joint.anchor);
                if (gap.sqrMagnitude < 1e-6f) continue;
                body.transform.position += gap;
                moved = true;
            }
            if (moved) PhysicsExtensions.SyncTransforms();
        }

        // Once per tick, before the step: the limits the solver applies during it (KeepMeAlive's ApplySpeedLimits). Each limb's
        // limit rides on the pelvis's speed, so dragging the whole body is not slowed.
        private static void ApplySpeedLimits(Session session)
        {
            Rigidbody pelvis = session.Pelvis;
            float pelvisSpeed = 0f;
            if (pelvis != null)
            {
                pelvisSpeed = pelvis.velocity.magnitude;
                pelvis.maxLinearVelocity = MaxPelvisSpeed;
                pelvis.maxAngularVelocity = MaxPartSpin;
            }

            foreach (var body in session.Bodies)
            {
                if (body == null || body == pelvis) continue;
                body.maxAngularVelocity = MaxPartSpin;
                body.maxLinearVelocity = pelvisSpeed + MaxPartSpeed;
            }
        }

        // The length of the physics step the game takes this frame: the average of the last frames' lengths, capped
        // (PhysicsExtensions.Simulation.SmoothSimulate).
        private static float PhysicsStep()
        {
            float average = (float)PhysicsExtensions.Simulation.SimulationAVGDeltaTime;
            if (!(average > 0f)) average = Time.deltaTime;
            return Mathf.Min(average, EFTHardSettings.Instance.MaxSmoothPhysicsDeltaTime);
        }

        //====================[ Materials ]====================
        // Ground grip for the limbs, from GroundFriction (sliding friction decelerates at mu * g). Maximum friction combine so
        // low-friction floor meshes do not halve the body's traction.
        private static PhysicMaterial ContactMaterial()
        {
            if (_contactMaterial == null)
            {
                const float mu = GroundFriction / 9.81f;
                _contactMaterial = new PhysicMaterial("KMABodyDrag_CorpseContact")
                {
                    dynamicFriction = mu,
                    staticFriction = mu * 1.15f,
                    bounciness = 0f,
                    bounceCombine = PhysicMaterialCombine.Minimum,
                    frictionCombine = PhysicMaterialCombine.Maximum
                };
            }
            return _contactMaterial;
        }

        //====================[ Human Segment Mass ]====================
        // EFT's prefab gives each forearm and upper arm 5.625 kg (the two arms 22.5 kg, heavier than the legs or the trunk), which
        // yanks the shoulders around. KeepMeAlive's human body-segment masses (about 80 kg) instead.
        private static float HumanSegmentMass(EBodyPartColliderType part)
        {
            switch (part)
            {
                case EBodyPartColliderType.Pelvis: return 14.0f;
                case EBodyPartColliderType.RibcageLow: return 12.0f;
                case EBodyPartColliderType.RibcageUp: return 14.0f;
                case EBodyPartColliderType.HeadCommon: return 4.5f;
                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.RightUpperArm: return 2.4f;
                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm: return 1.6f;
                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.RightThigh: return 9.0f;
                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightCalf: return 4.5f;
                default: return 5.0f;
            }
        }

        //====================[ Anatomical Joint Limits ]====================
        // KeepMeAlive's limits, unchanged. In Unity's PhysX CharacterJoint, twist is measured from child to parent: a child rotation
        // of +theta about joint.axis goes toward lowTwistLimit (-theta), and of -theta toward highTwistLimit (+theta).
        //   * Spine2, Spine3, Head, Forearm (axis (0, 0, -1)): anterior flexion is bounded by lowTwistLimit, extension by highTwistLimit.
        //   * Thigh (axis (0, 0, +1)): hip flexion is bounded by highTwistLimit, extension by lowTwistLimit.
        //   * Calf (axis (0, 0, +1)): knee flexion is bounded by lowTwistLimit, hyperextension by highTwistLimit.
        private static void ConfigureAnatomicalJoint(CharacterJoint joint, EBodyPartColliderType part)
        {
            var zeroSpring = new SoftJointLimitSpring { spring = 0f, damper = 0f };
            joint.twistLimitSpring = zeroSpring;
            joint.swingLimitSpring = zeroSpring;

            switch (part)
            {
                case EBodyPartColliderType.RibcageLow: // Spine2 on Pelvis
                    joint.axis = new Vector3(0f, 0f, -1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    SetLimits(joint, -35f, 15f, 32f, 32f);
                    break;

                case EBodyPartColliderType.RibcageUp: // Spine3 on Spine2
                    joint.axis = new Vector3(0f, 0f, -1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    SetLimits(joint, -40f, 15f, 25f, 25f);
                    break;

                case EBodyPartColliderType.HeadCommon: // Head on Spine3
                    joint.axis = new Vector3(0f, 0f, -1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    SetLimits(joint, -45f, 38f, 25f, 25f);
                    break;

                case EBodyPartColliderType.LeftUpperArm:
                    joint.axis = new Vector3(1f, 0f, 0f);
                    joint.swingAxis = new Vector3(0f, 0f, -1f);
                    SetLimits(joint, -45f, 45f, 45f, 45f);
                    break;

                case EBodyPartColliderType.RightUpperArm:
                    joint.axis = new Vector3(-1f, 0f, 0f);
                    joint.swingAxis = new Vector3(0f, 0f, -1f);
                    SetLimits(joint, -45f, 45f, 45f, 45f);
                    break;

                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm:
                    joint.axis = new Vector3(0f, 0f, -1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    SetLimits(joint, -125f, 20f, 55f, 55f);
                    break;

                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.RightThigh:
                    joint.axis = new Vector3(0f, 0f, 1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    SetLimits(joint, -25f, 80f, 48f, 48f);
                    break;

                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightCalf:
                    joint.axis = new Vector3(0f, 0f, 1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    // Limit sideways knee motion while allowing the leg to trail during a drag.
                    SetLimits(joint, -115f, 10f, 5f, 5f);
                    break;
            }
        }

        private static void SetLimits(CharacterJoint joint, float lowTwist, float highTwist, float swing1, float swing2)
        {
            joint.lowTwistLimit = new SoftJointLimit { limit = lowTwist, bounciness = 0f, contactDistance = 0f };
            joint.highTwistLimit = new SoftJointLimit { limit = highTwist, bounciness = 0f, contactDistance = 0f };
            joint.swing1Limit = new SoftJointLimit { limit = swing1, bounciness = 0f, contactDistance = 0f };
            joint.swing2Limit = new SoftJointLimit { limit = swing2, bounciness = 0f, contactDistance = 0f };
        }
    }
}
