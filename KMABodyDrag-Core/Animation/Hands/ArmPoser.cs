//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KMABodyDrag.Animation.Hands
{
    //====================[ ArmPoser ]====================
    // One arm of a player posed by the mod (the dragger's arms holding a body, the reviver's with the defibrillator), kinematic
    // and worked out afresh every frame from the bones: no simulation, so nothing springs or lags, and nothing of the game's
    // arm IK (its shoulders, elbow goals and weights come from animator curves that depend on what is in the hands) is relied
    // on. The caller says where the wrist goes, which way the hand faces and which way the elbow points; this does the rest:
    //   shoulder - the collarbone swings forward a little when the wrist is past the arm's straight reach;
    //   arm      - a two-bone solve puts the wrist exactly on the target, the elbow turned toward the bend direction;
    //   hand     - the forearm takes the roll (half at the elbow bone, the rest spread by the twist bones) and the wrist
    //              bends, within a limit, to lay the hand where it is wanted.
    // Rotations only: every bone stays on its parent, so the arm can never come off the body. Run straight after the
    // player's own late pass (BodyAnimation.AfterPlayerPose), so the game's pose is final and nothing rewrites the arms
    // afterwards. Visual only, so it runs on every peer, the player's own first-person view included.
    internal class ArmPoser
    {
        //====================[ Tuning ]====================
        // Furthest the collarbone swings forward (degrees) when the wrist is past the arm's straight reach, which it only
        // does beyond StraightArm of the arm's length.
        private const float ShoulderReach = 20f;
        private const float StraightArm = 0.98f;
        // Share of the hand's roll about the forearm taken by the forearm's elbow bone. The rig's twist bones then spread
        // only 20% / 40% of what is left (Forearm2 / Forearm3), the rest showing at the wrist.
        private const float ForearmRollShare = 0.5f;
        // The palm's skin sits this far out from the bones of the palm.
        public const float PalmThickness = 0.015f;

        //====================[ State ]====================
        public bool IsLeft;
        // The collarbone, when the upper arm hangs from one (null otherwise; the shoulder then never reaches).
        public Transform Clavicle;
        public Transform UpperBone;
        public Transform ForeBone;
        public Transform HandBone;
        public readonly List<TwistRelax> Twists = new List<TwistRelax>();

        // Shoulder to elbow and elbow to wrist (m), and shoulder to the knuckle line with the arm straight.
        public float UpperLength;
        public float ForeLength;
        public float Reach;

        // The hand's own frame as rotation-only offsets from the hand bone (scale-free): which way the fingers point, which
        // way the palm faces, and the middle of the palm's skin. False when the fingers were not found; the hand can then
        // only be locked in the pose it has.
        public bool HasHandFrame;
        public Vector3 FingerLocal;
        public Vector3 PalmLocal;
        public Vector3 PalmContactLocal;
        // The knuckle line, the palm's far end from the wrist joint: where the dragger's hands hold the body.
        public Vector3 KnuckleContactLocal;
        // The forearm's long axis from the elbow to the wrist, rotation-only local to the forearm bone.
        public Vector3 ForeAxisLocal;

        // How far short of its target the solve left the wrist this frame (m), for traces.
        public float Miss;

        //====================[ Build ]====================
        // The rig's twist relaxers: on Forearm2 and Forearm3 of each arm only.
        public static TwistRelax[] FindTwists(Player player) =>
            PlayerUtils.GetRibcage(player).GetComponentsInChildren<TwistRelax>(true);

        // An arm of a player (T is the class to make: this, or one that adds to it). The shoulder and elbow bones are the
        // ones carrying the arm's colliders (Base HumanL/RUpperarm and Base HumanL/RForearm1). Throws without them.
        public static T Create<T>(Player player, bool isLeft, Transform palm, TwistRelax[] twists, string tag) where T : ArmPoser, new()
        {
            var arm = new T
            {
                IsLeft = isLeft,
                UpperBone = DragGrip.RequirePart(player, isLeft ? EBodyPartColliderType.LeftUpperArm : EBodyPartColliderType.RightUpperArm),
                ForeBone = DragGrip.RequirePart(player, isLeft ? EBodyPartColliderType.LeftForearm : EBodyPartColliderType.RightForearm),
                HandBone = palm
            };

            Transform parent = arm.UpperBone.parent;
            if (parent != null && parent.name.IndexOf("Collarbone", StringComparison.OrdinalIgnoreCase) >= 0) arm.Clavicle = parent;

            // From the bones' own offsets, not their world distance: in first person the game squashes the arms along the view's
            // depth (FullScaleArms), which would make them shorter than they are.
            arm.UpperLength = ChainLength(arm.UpperBone, arm.ForeBone);
            arm.ForeLength = ChainLength(arm.ForeBone, palm);
            arm.ForeAxisLocal = HandFrames.LocalOffset(arm.ForeBone, palm).normalized;

            if (HandFrames.TryReadArm(palm, isLeft, PalmThickness, out var frame))
            {
                arm.FingerLocal = frame.FingerLocal;
                arm.PalmLocal = frame.PalmLocal;
                arm.PalmContactLocal = frame.PalmContactLocal;
                arm.KnuckleContactLocal = frame.KnuckleContactLocal;
                arm.HasHandFrame = true;
            }
            else
            {
                Plugin.LogSource.LogWarning($"[{tag}] No hand frame under {palm.name}; the hand keeps its own pose");
            }
            arm.Reach = arm.UpperLength + arm.ForeLength + (arm.HasHandFrame ? arm.KnuckleContactLocal.magnitude : 0f);

            foreach (var twist in twists)
            {
                if (twist != null && twist.transform.IsChildOf(arm.UpperBone)) arm.Twists.Add(twist);
            }
            return arm;
        }

        // The length of the bones from `from` down to `to`, summed from each bone's offset in its parent (so unaffected by any
        // scale above the chain). The world distance when `to` is not below `from`.
        private static float ChainLength(Transform from, Transform to)
        {
            if (!to.IsChildOf(from)) return Vector3.Distance(from.position, to.position);
            float length = 0f;
            for (Transform t = to; t != null && t != from; t = t.parent) length += t.localPosition.magnitude;
            return length;
        }

        //====================[ Pose ]====================
        // The arm's bones as they are now (world rotations), to blend the posed arm in from.
        public readonly struct Captured
        {
            public readonly Quaternion Clavicle, Upper, Fore, Hand;

            public Captured(Quaternion clavicle, Quaternion upper, Quaternion fore, Quaternion hand)
            {
                Clavicle = clavicle;
                Upper = upper;
                Fore = fore;
                Hand = hand;
            }
        }

        public Captured Capture() =>
            new Captured(Clavicle != null ? Clavicle.rotation : Quaternion.identity, UpperBone.rotation, ForeBone.rotation, HandBone.rotation);

        // Swings the collarbone forward (up to ShoulderReach) when the wrist is past the arm's straight reach, as a real
        // shoulder does, so the hand still gets there. Do this first: the elbow direction is worked out from where the
        // shoulder ends up.
        public void ReachWithShoulder(Vector3 wrist)
        {
            if (Clavicle == null) return;

            Vector3 shoulder = UpperBone.position;
            Vector3 toWrist = wrist - shoulder;
            float excess = toWrist.magnitude - (UpperLength + ForeLength) * StraightArm;
            if (excess <= 0f) return;

            Vector3 pivot = Clavicle.position;
            Vector3 wanted = shoulder + toWrist.normalized * excess;
            Quaternion turn = Quaternion.FromToRotation(shoulder - pivot, wanted - pivot);
            float angle = turn.ToSignedAngleAxis(out Vector3 axis);
            if (Mathf.Abs(angle) > ShoulderReach) turn = Quaternion.AngleAxis(Mathf.Sign(angle) * ShoulderReach, axis);
            Clavicle.rotation = turn * Clavicle.rotation;
        }

        // Which way an elbow at `elbow` points from the shoulder-wrist line (unit, square to it); zero when the arm is straight.
        public static Vector3 BendToward(Vector3 shoulder, Vector3 wrist, Vector3 elbow)
        {
            Vector3 bend = Vector3.ProjectOnPlane(elbow - shoulder, wrist - shoulder);
            return bend.sqrMagnitude > 1e-6f ? bend.normalized : Vector3.zero;
        }

        // Where the elbow lands for a bend direction, and the centre of the circle it can lie on.
        public Vector3 ElbowPoint(Vector3 shoulder, Vector3 wrist, Vector3 bend, out Vector3 centre) =>
            TwoBoneIk.MiddlePoint(shoulder, wrist, UpperLength, ForeLength, bend, out centre);

        // Two-bone solve on the real bones: the wrist (the hand bone's root) onto the target, the elbow toward bend (a unit
        // direction perpendicular to the shoulder-wrist line). Out of reach, the arm goes straight toward the target and
        // the hand stops short of it.
        public void Solve(Vector3 wrist, Vector3 bend)
        {
            TwoBoneIk.Solve(UpperBone, ForeBone, HandBone, wrist, UpperLength, ForeLength, bend);
            Miss = Vector3.Distance(HandBone.position, wrist);
        }

        // Lays the hand where it is wanted (the forearm and the wrist take the roll and the bend, the bend limited to
        // maxWristBend degrees), blends the arm in from how it was by weight, and lets the twist bones spread the remaining
        // roll along the forearm.
        public void Finish(Quaternion locked, float maxWristBend, Captured from, float weight)
        {
            LayHand(locked, maxWristBend);

            if (weight < 1f)
            {
                // World rotations, parent first: each bone's blend is set after its parent has moved.
                Quaternion clavicleTo = Clavicle != null ? Clavicle.rotation : Quaternion.identity;
                Quaternion upperTo = UpperBone.rotation;
                Quaternion foreTo = ForeBone.rotation;
                Quaternion handTo = HandBone.rotation;

                if (Clavicle != null) Clavicle.rotation = Quaternion.Slerp(from.Clavicle, clavicleTo, weight);
                UpperBone.rotation = Quaternion.Slerp(from.Upper, upperTo, weight);
                ForeBone.rotation = Quaternion.Slerp(from.Fore, foreTo, weight);
                HandBone.rotation = Quaternion.Slerp(from.Hand, handTo, weight);
            }

            // The twist bones spread the remaining roll along the forearm and keep the hand where it is.
            foreach (var twist in Twists) twist.Relax();
        }

        // The forearm's roll and the wrist's bend (limited to maxWristBend) that lay the hand toward `locked`, on the arm as it
        // stands. Repeating it on a laid hand changes nothing, so the arm can be solved again for where the palm really ended up.
        public void LayHand(Quaternion locked, float maxWristBend)
        {
            RollForearm(locked);
            HandBone.rotation = BendWrist(HandBone.rotation, locked, maxWristBend);
        }

        // Turns the forearm's elbow bone about its own axis by ForearmRollShare of the roll the locked hand needs, so the
        // whole forearm turns with the hand rather than the wrist twisting. The axis runs through the wrist, so the hand
        // stays on its target.
        private void RollForearm(Quaternion locked)
        {
            if (!HasHandFrame) return;
            Vector3 axis = ForeBone.rotation * ForeAxisLocal;
            Quaternion current = HandBone.rotation;
            Quaternion straight = Quaternion.FromToRotation(current * FingerLocal, axis) * current;
            float roll = global::KMABodyDrag.Animation.Shared.MathUtil.SignedTwist(locked * Quaternion.Inverse(straight), axis);
            ForeBone.rotation = Quaternion.AngleAxis(roll * ForearmRollShare, axis) * ForeBone.rotation;
        }

        // The locked hand, reached from a straight wrist (the current hand turned to carry straight on from the forearm) by a
        // roll about the forearm (left to the twist bones) and a bend at the wrist limited to maxBend; past that the hand
        // gives a little rather than the wrist breaking.
        private Quaternion BendWrist(Quaternion current, Quaternion locked, float maxBend)
        {
            Vector3 axis = ForeBone.rotation * ForeAxisLocal;
            Quaternion straight = HasHandFrame
                ? Quaternion.FromToRotation(current * FingerLocal, axis) * current
                : current;

            Quaternion delta = locked * Quaternion.Inverse(straight);
            Quaternion roll = global::KMABodyDrag.Animation.Shared.MathUtil.TwistAbout(delta, axis);
            Quaternion bend = delta * Quaternion.Inverse(roll);

            float angle = bend.ToSignedAngleAxis(out Vector3 bendAxis);
            if (Mathf.Abs(angle) > maxBend) bend = Quaternion.AngleAxis(Mathf.Sign(angle) * maxBend, bendAxis);

            return bend * roll * straight;
        }
    }
}
