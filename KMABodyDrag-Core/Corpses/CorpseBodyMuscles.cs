//====================[ Imports ]====================
using System.Collections.Generic;
using EFT;
using KMABodyDrag.Animation.Shared;
using UnityEngine;

namespace KMABodyDrag.Corpses
{
    //====================[ CorpseBody: Muscles ]====================
    // KeepMeAlive's muscles (BodyRagdollMuscles): a drive-only ConfigurableJoint beside each CharacterJoint, pulling the body on its
    // parent toward the muscle pose (lying on the back, legs and spine straight, arms at the sides). Springs inside the physics
    // step, never a servo, so gravity, the floor and the dragger's hands still act on every part. As in KeepMeAlive they are on
    // from the take to the removal and the same held or let go: a held body's limbs hang under their muscles, and a body let go
    // settles into the pose. One deliberate difference: the neck and head have no pull, only KeepMeAlive's damping for that
    // joint, so a dead body's head falls and bounces on its own, and each swing dies away instead of building on the last.
    internal static partial class CorpseBody
    {
        //====================[ Tuning ]====================
        // KeepMeAlive's values, unchanged. Pull toward the pose (natural frequency squared, 1/s^2), damping relative to critical.
        private const float ArmMuscleStrength = 120f;
        private const float LegMuscleStrength = 100f;
        private const float SpineMuscleStrength = 150f;
        private const float NeckHeadMuscleStrength = 110f;
        private const float MuscleDampingRatio = 1f;

        // Postural tone against gravity for the neck, spine and arms; the knees only for the legs (not the hips), so the legs lie.
        private const float NeckPostureTone = 8f;
        private const float SpinePostureTone = 5f;
        private const float ArmPostureTone = 6f;
        private const float KneePostureTone = 6f;
        // Hip flexion stays soft so the legs rest on the ground; sideways and rolling motion is held.
        private const float HipFlexionShare = 0.15f;
        private const float HipSideTone = 6f;
        // The hip joint's flexion axis in the thigh's own frame (EFT's CharacterJoint axis on the thighs).
        private static readonly Vector3 HipFlexionAxis = Vector3.forward;

        // Leaf bones without a rigidbody, posed to the muscle pose alongside the physical joints (ankles, forearm twists, wrists).
        // Bones above a rigidbody are never written while the physics runs (PhysX writes them back, and the joint anchors hang off
        // them).
        private static readonly string[] PosedHelperBones =
        {
            BoneNames.LeftFoot,
            BoneNames.RightFoot,
            BoneNames.LeftForearmTwist2,
            BoneNames.LeftForearmTwist3,
            BoneNames.RightForearmTwist2,
            BoneNames.RightForearmTwist3,
            BoneNames.LeftPalm,
            BoneNames.RightPalm
        };

        //====================[ State ]====================
        private enum MuscleGroup
        {
            Arms,
            Legs,
            Spine,
            NeckHead
        }

        private sealed class Muscle
        {
            public Rigidbody Body;
            public ConfigurableJoint Drive;
            public MuscleGroup Group;
            public EBodyPartColliderType Part;
            // The joint's reduced swing inertia (kg m^2), turning the strength (1/s^2) into the drive's torque.
            public float Inertia;
            // The most torque gravity puts on the joint (N*m), scaling the postural tone.
            public float Weight;
            // What the drive was last written with (KeepMeAlive's UpdateMuscleDrives): rewritten only when it changes. -1 until then.
            public float AppliedStrength = -1f;
            public float AppliedDamping = -1f;
        }

        private struct LeafBone
        {
            public Transform Bone;
            public Quaternion Local;
        }

        //====================[ Motors ]====================
        // One drive-only motor per jointed body, made while the bones are in the muscle pose (from ZeroJoints, after the
        // CharacterJoints are set up there), so its zero and its target are that pose. No drive until BuildMuscles.
        private static void CreateMotors(Session session)
        {
            foreach (var (joint, body) in session.Joints)
            {
                var parent = joint.connectedBody;
                if (parent == null || session.Motors.ContainsKey(body)) continue;
                bool hip = session.PartOf.TryGetValue(body, out var part) && IsThigh(part);

                var motor = body.gameObject.AddComponent<ConfigurableJoint>();
                motor.autoConfigureConnectedAnchor = false;
                motor.anchor = joint.anchor;
                motor.connectedAnchor = joint.connectedAnchor;
                // The hips drive flexion (the joint's x) apart from the rest (y, z); every other joint is one slerp drive.
                motor.axis = hip ? HipFlexionAxis : Vector3.right;
                motor.secondaryAxis = Vector3.up;
                motor.connectedBody = parent;
                motor.xMotion = ConfigurableJointMotion.Free;
                motor.yMotion = ConfigurableJointMotion.Free;
                motor.zMotion = ConfigurableJointMotion.Free;
                motor.angularXMotion = ConfigurableJointMotion.Free;
                motor.angularYMotion = ConfigurableJointMotion.Free;
                motor.angularZMotion = ConfigurableJointMotion.Free;
                motor.rotationDriveMode = hip ? RotationDriveMode.XYAndZ : RotationDriveMode.Slerp;
                motor.targetRotation = Quaternion.identity;
                motor.slerpDrive = default;
                motor.enableCollision = false;
                motor.enablePreprocessing = false;
                motor.massScale = 1f;
                motor.connectedMassScale = 1f;
                session.Motors[body] = motor;
            }
        }

        // Every motor off and removed, before the rigidbodies are (a body with a joint on it cannot be removed). Off first: Destroy
        // only takes effect at the end of the frame, and a body handed back to Rupture or EFT may simulate before then.
        private static void DestroyMotors(Session session)
        {
            foreach (var motor in session.Motors.Values)
            {
                if (motor == null) continue;
                motor.slerpDrive = default;
                motor.angularXDrive = default;
                motor.angularYZDrive = default;
                Object.Destroy(motor);
            }
            session.Motors.Clear();
            session.Muscles.Clear();
            session.LeafBones.Clear();
        }

        //====================[ Build ]====================
        // Once the bodies are active and seated on their joints (KeepMeAlive's order): each motor's strength from what it moves,
        // the leaf bones' pose, and the drives switched on. None under a limb Rupture severed (severed): Rupture simulates that
        // piece itself.
        private static void BuildMuscles(Session session, MusclePose.SkeletonCopy pose, Transform[] severed)
        {
            var parentOf = new Dictionary<Rigidbody, Rigidbody>();
            var joints = new Dictionary<Rigidbody, CharacterJoint>();
            foreach (var (joint, body) in session.Joints)
            {
                if (joint.connectedBody == null) continue;
                parentOf[body] = joint.connectedBody;
                joints[body] = joint;
            }

            var children = new Dictionary<Rigidbody, List<Rigidbody>>();
            foreach (var pair in parentOf)
            {
                if (!children.TryGetValue(pair.Value, out var list)) children[pair.Value] = list = new List<Rigidbody>();
                list.Add(pair.Key);
            }

            foreach (var pair in parentOf)
            {
                var body = pair.Key;
                if (!session.Motors.TryGetValue(body, out var motor) || motor == null) continue;
                var part = session.PartOf.TryGetValue(body, out var p) ? p : EBodyPartColliderType.RibcageLow;

                var load = new List<Rigidbody>();
                CollectLoad(body, children, load);
                session.Muscles.Add(new Muscle
                {
                    Body = body,
                    Drive = motor,
                    Group = ClassifyMuscle(part),
                    Part = part,
                    Inertia = JointInertia(body, pair.Value, load, joints[body]),
                    Weight = GravityMoment(load, body.transform.TransformPoint(joints[body].anchor))
                });
            }

            session.LeafBones.Clear();
            var wanted = new HashSet<string>(PosedHelperBones);
            foreach (var bone in pose.AllBones)
            {
                if (bone == null || !wanted.Contains(bone.name) || !pose.Has(bone.parent) || IsSevered(bone, severed)) continue;
                session.LeafBones.Add(new LeafBone { Bone = bone, Local = pose.LocalRotation(bone) });
            }

            UpdateMuscleDrives(session);
        }

        //====================[ Drive ]====================
        // Every tick, held or let go (KeepMeAlive's UpdateMuscleDrives): every motor at its group's strength. Only a drive whose
        // values changed is rewritten.
        private static void UpdateMuscleDrives(Session session)
        {
            const float dampingRatio = MuscleDampingRatio;
            foreach (var muscle in session.Muscles)
            {
                if (muscle.Drive == null) continue;
                float strength = Mathf.Max(0f, GroupStrength(muscle.Group));
                if (strength == muscle.AppliedStrength && dampingRatio == muscle.AppliedDamping) continue;

                muscle.AppliedStrength = strength;
                muscle.AppliedDamping = dampingRatio;

                // The neck and head: KeepMeAlive's damping for the joint, without its pull, so the head falls and bounces.
                if (muscle.Group == MuscleGroup.NeckHead)
                {
                    muscle.Drive.slerpDrive = DamperOnly(strength * muscle.Inertia, muscle.Inertia, dampingRatio);
                    continue;
                }

                float spring = strength * muscle.Inertia + PostureTone(muscle.Group, muscle.Part) * muscle.Weight;
                if (IsThigh(muscle.Part))
                {
                    muscle.Drive.angularXDrive = Drive(spring * HipFlexionShare, muscle.Inertia, dampingRatio);
                    muscle.Drive.angularYZDrive = Drive(spring + HipSideTone * muscle.Weight, muscle.Inertia, dampingRatio);
                }
                else muscle.Drive.slerpDrive = Drive(spring, muscle.Inertia, dampingRatio);
            }
        }

        // The leaf bones without a body, held in the muscle pose every tick (nothing else writes them while the physics runs).
        private static void PoseLeafBones(Session session)
        {
            foreach (var helper in session.LeafBones)
                if (helper.Bone != null) helper.Bone.localRotation = helper.Local;
        }

        // Damped against the whole spring (critical at ratio 1 for the joint's inertia).
        private static JointDrive Drive(float spring, float inertia, float dampingRatio) => new JointDrive
        {
            positionSpring = spring,
            positionDamper = 2f * dampingRatio * Mathf.Sqrt(spring * inertia),
            maximumForce = float.MaxValue,
            useAcceleration = false
        };

        // The damping Drive would give for spring, with no spring.
        private static JointDrive DamperOnly(float spring, float inertia, float dampingRatio) => new JointDrive
        {
            positionSpring = 0f,
            positionDamper = 2f * dampingRatio * Mathf.Sqrt(spring * inertia),
            maximumForce = float.MaxValue,
            useAcceleration = false
        };

        //====================[ Helpers ]====================
        private static bool IsThigh(EBodyPartColliderType part) =>
            part == EBodyPartColliderType.LeftThigh || part == EBodyPartColliderType.RightThigh;

        private static MuscleGroup ClassifyMuscle(EBodyPartColliderType part)
        {
            switch (part)
            {
                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.RightUpperArm:
                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm:
                    return MuscleGroup.Arms;
                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.RightThigh:
                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightCalf:
                    return MuscleGroup.Legs;
                case EBodyPartColliderType.HeadCommon:
                    return MuscleGroup.NeckHead;
                default:
                    return MuscleGroup.Spine;
            }
        }

        private static float GroupStrength(MuscleGroup group)
        {
            switch (group)
            {
                case MuscleGroup.Arms: return ArmMuscleStrength;
                case MuscleGroup.Legs: return LegMuscleStrength;
                case MuscleGroup.NeckHead: return NeckHeadMuscleStrength;
                default: return SpineMuscleStrength;
            }
        }

        private static float PostureTone(MuscleGroup group, EBodyPartColliderType part)
        {
            switch (group)
            {
                case MuscleGroup.NeckHead: return NeckPostureTone;
                case MuscleGroup.Spine: return SpinePostureTone;
                case MuscleGroup.Arms: return ArmPostureTone;
                default: return part == EBodyPartColliderType.LeftCalf || part == EBodyPartColliderType.RightCalf ? KneePostureTone : 0f;
            }
        }

        private static void CollectLoad(Rigidbody body, Dictionary<Rigidbody, List<Rigidbody>> children, List<Rigidbody> load)
        {
            load.Add(body);
            if (!children.TryGetValue(body, out var list)) return;
            foreach (var child in list) CollectLoad(child, children, load);
        }

        // How hard the joint is to swing (kg m^2): the reduced inertia about the pivot of everything it moves against its parent,
        // averaged over the two axes across the bone (EFT's bones lie along their local x).
        private static float JointInertia(Rigidbody body, Rigidbody parent, List<Rigidbody> load, CharacterJoint joint)
        {
            Vector3 pivot = body.transform.TransformPoint(joint.anchor);
            Mat3 moved = Mat3.Zero;
            foreach (var part in load) moved = moved + InertiaTensorAbout(part, pivot);
            Mat3 other = InertiaTensorAbout(parent, pivot);

            Vector3 along = body.transform.right;
            Vector3 across1 = Vector3.Cross(along, Mathf.Abs(Vector3.Dot(along, Vector3.up)) < 0.9f ? Vector3.up : Vector3.forward).normalized;
            Vector3 across2 = Vector3.Cross(along, across1).normalized;

            float Reduced(Vector3 n)
            {
                float a = Vector3.Dot(n, moved * n), b = Vector3.Dot(n, other * n);
                return a + b > 1e-6f ? a * b / (a + b) : 0f;
            }
            return 0.5f * (Reduced(across1) + Reduced(across2));
        }

        // The weight of everything the joint moves at the distance of their common centre of mass from the pivot.
        private static float GravityMoment(List<Rigidbody> load, Vector3 pivot)
        {
            float mass = 0f;
            Vector3 weighted = Vector3.zero;
            foreach (var part in load)
            {
                mass += part.mass;
                weighted += part.mass * part.worldCenterOfMass;
            }
            if (mass <= 0f) return 0f;
            return mass * -Physics.gravity.y * Vector3.Distance(weighted / mass, pivot);
        }

        // A body's resistance to turning about a pivot, as a world-space tensor: its own inertia plus the parallel-axis term.
        private static Mat3 InertiaTensorAbout(Rigidbody body, Vector3 pivot)
        {
            Quaternion frame = body.rotation * body.inertiaTensorRotation;
            Vector3 t = body.inertiaTensor;
            Vector3 x = frame * Vector3.right, y = frame * Vector3.up, z = frame * Vector3.forward;
            Mat3 own = Mat3.Outer(x, x) * t.x + Mat3.Outer(y, y) * t.y + Mat3.Outer(z, z) * t.z;
            Vector3 r = body.worldCenterOfMass - pivot;
            Mat3 offset = (Mat3.Identity * r.sqrMagnitude - Mat3.Outer(r, r)) * body.mass;
            return own + offset;
        }

        private readonly struct Mat3
        {
            private readonly float m00, m01, m02, m10, m11, m12, m20, m21, m22;

            private Mat3(float a, float b, float c, float d, float e, float f, float g, float h, float i)
            {
                m00 = a; m01 = b; m02 = c; m10 = d; m11 = e; m12 = f; m20 = g; m21 = h; m22 = i;
            }

            public static Mat3 Zero => new Mat3(0, 0, 0, 0, 0, 0, 0, 0, 0);
            public static Mat3 Identity => new Mat3(1, 0, 0, 0, 1, 0, 0, 0, 1);

            public static Mat3 Outer(Vector3 u, Vector3 v) => new Mat3(
                u.x * v.x, u.x * v.y, u.x * v.z,
                u.y * v.x, u.y * v.y, u.y * v.z,
                u.z * v.x, u.z * v.y, u.z * v.z);

            public static Mat3 operator +(Mat3 a, Mat3 b) => new Mat3(
                a.m00 + b.m00, a.m01 + b.m01, a.m02 + b.m02,
                a.m10 + b.m10, a.m11 + b.m11, a.m12 + b.m12,
                a.m20 + b.m20, a.m21 + b.m21, a.m22 + b.m22);

            public static Mat3 operator -(Mat3 a, Mat3 b) => new Mat3(
                a.m00 - b.m00, a.m01 - b.m01, a.m02 - b.m02,
                a.m10 - b.m10, a.m11 - b.m11, a.m12 - b.m12,
                a.m20 - b.m20, a.m21 - b.m21, a.m22 - b.m22);

            public static Mat3 operator *(Mat3 a, float s) => new Mat3(
                a.m00 * s, a.m01 * s, a.m02 * s, a.m10 * s, a.m11 * s, a.m12 * s, a.m20 * s, a.m21 * s, a.m22 * s);

            public static Vector3 operator *(Mat3 a, Vector3 v) => new Vector3(
                a.m00 * v.x + a.m01 * v.y + a.m02 * v.z,
                a.m10 * v.x + a.m11 * v.y + a.m12 * v.z,
                a.m20 * v.x + a.m21 * v.y + a.m22 * v.z);
        }
    }
}
