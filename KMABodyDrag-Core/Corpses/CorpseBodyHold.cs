//====================[ Imports ]====================
using UnityEngine;

namespace KMABodyDrag.Corpses
{
    //====================[ CorpseBody: Hold ]====================
    // The dragger's hands on this peer's copy of a dragged corpse (KeepMeAlive's BodyRagdollHold, unchanged but for what a corpse
    // does not have): a kinematic body joined to the chest at the two swing points of the body's DragGrip (each shoulder,
    // DragGrip.SwingHeight up the spine), so the hands hold the same spot of the body on every pickup. It plays the pickup the
    // DragSession is in, then follows the session's hold frame:
    //   stance         - holds the shoulders where they lie;
    //   turn over      - a body lying on its front is lifted a little and rolled onto its back about its spine;
    //   reach / grip   - the shoulders rise from there to meet the dragger's hands (DragSession.PickupProgress);
    //   lift           - eased on from there onto the hold frame (head toward the dragger, shoulders square to the pull);
    //   hold           - follows the hold frame.
    // No overlap-manager ignore: a corpse's character controller is off (Player.OnDead) and its Deadbody colliders are not in the
    // dragger's PlayerOverlapManager mask, so nothing pushes the dragger.
    internal static partial class CorpseBody
    {
        //====================[ Tuning ]====================
        // How far the hold may stretch before the chest is pulled hard back to it (m). Small = firm grip.
        private const float HoldSlack = 0.025f;
        // Compliant angular support on the dragger's grip so the chest cannot flip or pitch uncontrollably around the line between
        // the two shoulders while still allowing natural torso sway under the pull.
        private const float HoldAngularSpring = 120f;
        private const float HoldAngularDamper = 24f;
        // Natural reclined pitch of the upper chest (deg) while held by the shoulders with the pelvis on the floor.
        private const float DragChestPitch = 24f;
        // A hold target that jumps further than this in one tick is a teleport, not a pull: the whole body is moved with it.
        private const float TeleportDistance = 2.5f;
        // If the chest ends up this far from the hold (stuck on geometry), the body is moved back to it.
        private const float MaxHoldError = 2f;
        // Fastest the hands move and turn toward the hold once it is lifted (m/s, deg/s).
        private const float MaxHandSpeed = 6f;
        // Fast enough for the pickup's half turn of a body lying on its front (peaks near 450 deg/s).
        private const float MaxHandTurnSpeed = 540f;
        // A held body rolled further than this about its spine (from lying on its back) has slipped out of the dragger's hands.
        private const float MaxGripRoll = 70f;

        //====================[ Hand ]====================
        // Takes hold of the chest at the body's two swing points, exactly where they are now (the hand is placed in the grip's own
        // frame on the chest, so nothing snaps): the left shoulder on the hand's +x, the right on -x.
        private static void CreateHand(Session session)
        {
            var chest = session.Chest;
            var grip = DragGrip.For(session.Entry.Player);
            Transform frame = chest.transform;

            var hand = new GameObject("KMABodyDrag_Hand");
            hand.transform.SetPositionAndRotation(DragGrip.World(frame, grip.HoldPosition), frame.rotation * grip.HoldRotation);

            var body = hand.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            AddGrip(hand, chest, new Vector3(grip.HalfWidth, 0f, 0f), frame.InverseTransformPoint(DragGrip.World(frame, grip.SwingLeft)));
            AddGrip(hand, chest, new Vector3(-grip.HalfWidth, 0f, 0f), frame.InverseTransformPoint(DragGrip.World(frame, grip.SwingRight)));
            session.HandAngularJoint = AddAngularGrip(hand, chest);

            session.Hand = hand;
            session.HandBody = body;
            session.PickupFrom = hand.transform.position;
            session.PickupFromRotation = hand.transform.rotation;
            session.LastHoldTarget = session.PickupFrom;
            session.RollArmed = false;
            session.Rolled = false;
        }

        // One hand on one shoulder: a short leash from a point on the hand to a point on the chest.
        private static void AddGrip(GameObject hand, Rigidbody chest, Vector3 handPoint, Vector3 chestPoint)
        {
            var joint = hand.AddComponent<ConfigurableJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = handPoint;
            joint.connectedAnchor = chestPoint;
            joint.connectedBody = chest;
            joint.xMotion = ConfigurableJointMotion.Limited;
            joint.yMotion = ConfigurableJointMotion.Limited;
            joint.zMotion = ConfigurableJointMotion.Limited;
            joint.linearLimit = new SoftJointLimit { limit = HoldSlack };
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.projectionDistance = 0.08f;
            joint.enablePreprocessing = false;
        }

        // Keeps the two point-grips from acting as an unconstrained hinge line around the shoulders.
        private static ConfigurableJoint AddAngularGrip(GameObject hand, Rigidbody chest)
        {
            var joint = hand.AddComponent<ConfigurableJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = Vector3.zero;
            joint.connectedAnchor = Vector3.zero;
            joint.connectedBody = chest;
            joint.xMotion = ConfigurableJointMotion.Free;
            joint.yMotion = ConfigurableJointMotion.Free;
            joint.zMotion = ConfigurableJointMotion.Free;
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.slerpDrive = new JointDrive
            {
                positionSpring = HoldAngularSpring,
                positionDamper = HoldAngularDamper,
                maximumForce = float.MaxValue
            };
            joint.targetRotation = Quaternion.identity;
            joint.enablePreprocessing = false;
            return joint;
        }

        private static void DestroyHand(Session session)
        {
            if (session.Hand != null)
            {
                // Deactivated first: Destroy only takes effect at the end of the frame, and the joint would keep pulling the chest
                // through any physics step before then.
                session.Hand.SetActive(false);
                Object.Destroy(session.Hand);
            }
            session.Hand = null;
            session.HandBody = null;
            session.HandAngularJoint = null;
        }

        //====================[ Hold ]====================
        private static void DriveHold(Session session, DragSession drag, float step)
        {
            if (session.Hand == null) CreateHand(session);
            Rigidbody hand = session.HandBody;

            // Where the body rests before the lift: where it was grabbed, or lifted and turned onto its back.
            Vector3 rest = session.PickupFrom;
            Quaternion restRotation = session.PickupFromRotation;
            if (drag.FaceDown)
            {
                float turn = drag.TurnProgress;
                float lift = Mathf.Max(0f, drag.HoldPoint.y - session.PickupFrom.y);
                rest += Vector3.up * (lift * Mathf.SmoothStep(0f, 1f, turn));
                // Half a turn about the spine (the hand's forward), trailing the lift so the shoulders are off the floor before they
                // swing round.
                restRotation *= Quaternion.AngleAxis(180f * turn * turn, Vector3.forward);
            }

            // One target for pickup and hold alike: the pickup is smooth by itself (up to the dragger's hands over the reach, then on
            // into the hold frame), and once held the target is the hold frame itself.
            bool held = drag.IsHeld;
            float progress = held ? 1f : drag.PickupProgress;
            Vector3 target = Vector3.Lerp(rest, drag.HoldPoint, progress);
            Quaternion reclinedHoldRotation = drag.HoldRotation * Quaternion.Euler(-DragChestPitch, 0f, 0f);
            Quaternion targetRotation = Quaternion.Slerp(restRotation, reclinedHoldRotation, progress);

            // Teleport of the dragger (or of the body by the network): move the whole body with the hold instead of letting the
            // joint fling it across the gap.
            Vector3 jump = target - session.LastHoldTarget;
            if (jump.sqrMagnitude > TeleportDistance * TeleportDistance)
                ShiftBody(session, jump);
            else if (held)
            {
                Vector3 chestPosition = session.Chest.position;
                if ((chestPosition - target).sqrMagnitude > MaxHoldError * MaxHoldError)
                    ShiftBody(session, target - chestPosition);
            }
            session.LastHoldTarget = target;

            // MovePosition, not the transform: the hand then has a velocity for the step, so the joint pulls smoothly.
            hand.MovePosition(Vector3.MoveTowards(hand.position, target, MaxHandSpeed * step));
            hand.MoveRotation(Quaternion.RotateTowards(hand.rotation, targetRotation, MaxHandTurnSpeed * step));

            if (held) UpdateRoll(session);
        }

        // Moves every body of the ragdoll by the same offset and stops it, keeping its shape.
        private static void ShiftBody(Session session, Vector3 offset)
        {
            foreach (var body in session.Bodies)
            {
                body.position += offset;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            if (session.HandBody != null) session.HandBody.position += offset;
            session.PickupFrom += offset;
        }

        //====================[ Roll ]====================
        private static void UpdateRoll(Session session)
        {
            if (!TryGetRoll(session, out float roll)) return;
            if (!session.RollArmed)
            {
                session.RollArmed = roll <= MaxGripRoll;
                return;
            }
            session.Rolled = roll > MaxGripRoll;
        }

        // How far the body has rolled about its spine from lying on its back (degrees, 0 on its back, 90 on its side, 180 on its
        // front). False while the spine is too near vertical to tell.
        private static bool TryGetRoll(Session session, out float roll)
        {
            roll = 0f;
            if (!TryGetFront(session, out Vector3 front, out Vector3 spine)) return false;

            Vector3 level = Vector3.ProjectOnPlane(Vector3.up, spine);
            if (level.sqrMagnitude < 0.01f) return false;

            roll = Vector3.Angle(front, level);
            return true;
        }

        // The way the chest faces (out of the front) and the spine (pelvis to shoulders), from the bones alone.
        private static bool TryGetFront(Session session, out Vector3 front, out Vector3 spine)
        {
            front = Vector3.zero;
            spine = Vector3.zero;
            if (session.Pelvis == null) return false;
            return DragGrip.TryFront(session.LeftShoulder.position, session.RightShoulder.position, session.Pelvis.position, out front, out spine);
        }
    }
}
