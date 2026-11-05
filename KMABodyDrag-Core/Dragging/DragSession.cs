//====================[ Imports ]====================
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KMABodyDrag.Dragging
{
    //====================[ DragReach ]====================
    // Where the dragger's shoulders are (world), how far each hand's contact is from its shoulder at the furthest the pickup
    // stretches the arm (PickupReach), and the contact each hand took hold of as the body lay (Rest, world), for the pickup
    // (DraggerArms.TryGetShoulders).
    internal readonly struct DragReach
    {
        public readonly Vector3 LeftShoulder, RightShoulder;
        public readonly float LeftPickupReach, RightPickupReach;
        public readonly Vector3 LeftRest, RightRest;
        // How far each hand's contact is from where the game's own hand is when the hands take hold (m).
        public readonly float LeftTravel, RightTravel;
        public DragReach(Vector3 leftShoulder, Vector3 rightShoulder,
                         float leftPickupReach, float rightPickupReach, Vector3 leftRest, Vector3 rightRest,
                         float leftTravel, float rightTravel)
        {
            LeftTravel = leftTravel;
            RightTravel = rightTravel;
            LeftShoulder = leftShoulder;
            RightShoulder = rightShoulder;
            LeftPickupReach = leftPickupReach;
            RightPickupReach = rightPickupReach;
            LeftRest = leftRest;
            RightRest = rightRest;
        }
    }

    //====================[ DragSession ]====================
    // One drag, as this peer sees it: who holds whom, how far the pickup has come, and the hold frame. KeepMeAlive's, unchanged
    // but for the body being a corpse. Built from the replicated claim (CorpseClaims) on every peer, so each one plays the same
    // pickup from when it learned of the claim:
    //   stance    - the dragger crouches and stows what is in their hands (locked in place from here to the lift);
    //   turn over - a body lying on its front is rolled onto its back (only where this peer has it in its physics, CorpseBody);
    //   reach     - the dragger's hands go for the body's grip (DragGrip). A crouched dragger cannot reach a body on the
    //               floor (their shoulders are 0.6-0.75 m above its grip, the arm reaches ~0.65 m), so the hands go as far
    //               as the arm stretches toward it and the body's shoulders rise over the second half to meet them: both
    //               arrive at the meet point at the same moment, on every pickup;
    //   grip      - the hands close on the shoulders;
    //   lift      - the body is lifted on from the meet point into the hold frame;
    //   hold      - the dragger is free to move; the body follows the hold frame.
    // The hold frame is a fixed place in front of the dragger (HoldX / HoldY / HoldZ, in their facing), never from the body and
    // never moved to suit the arms: the body always targets that exact place and lags behind it physically as it is pulled. The
    // arms follow the body only as far as their limit (DraggerArms.StretchLimit) and let go of the shoulders past it. Every peer's
    // copy of the body and the dragger's arms all follow it, so nothing chases anything else.
    internal sealed class DragSession
    {
        //====================[ Tuning ]====================
        private const float StanceSeconds = 0.3f;
        private const float TurnOverSeconds = 0.8f;
        // A body still not on its back this long after the turn (stuck on something) is taken as it lies.
        private const float TurnOverGrace = 1f;
        // Reach duration scales with hand travel so the body rises as the dragger's hands arrive.
        private const float ReachBaseSeconds = 0.45f;
        private const float ReachSecondsPerMetre = 0.45f;
        private const float ReachMinSeconds = 0.5f;
        private const float ReachMaxSeconds = 1.2f;
        // Share of the reach the body lies still before its shoulders start rising to meet the hands (the hands are on their
        // way by then). From the farthest pickups in the log (the grip at twice the arm's reach) the shoulders rise ~0.5 m.
        private const float RiseStart = 0.2f;
        // The hands close on the shoulders before they pull.
        private const float GripSeconds = 0.15f;
        // Lifting a person's upper body off the floor is an effort: slower than the reach.
        private const float LiftSeconds = 0.55f;
        // The stance waits for the weapon to be put away (up to this long), so both hands set off together: in the 2026-10-15 log the
        // right hand started 0.5 s after the left while the rifle was still being stowed, and arrived after the body had risen.
        private const float StanceMaxSeconds = 2f;
        // Where the middle of the body's two shoulders is held: a FIXED place in front of the dragger, in the way they face (m): to their
        // right, above their feet, and out in front of their feet. Tuned in game with sliders by the user (2026-10-15) and baked here.
        // Measured from nothing but their feet and facing, so it is the same wherever they crouch, walk or turn.
        private const float HoldX = 0.06f;
        private const float HoldY = 0.60f;
        private const float HoldZ = 0.65f;
        //====================[ Stages ]====================
        public enum Stage { Stance, TurnOver, Reach, Grip, Lift, Hold }

        public readonly Player Dragger;
        public readonly Player Body;
        // Whether this peer has the body in its physics (CorpseBody; DragContext says so each tick; DraggerArms reads it).
        public bool Ragdolled { get; private set; }
        public Stage Current { get; private set; }
        public float ReachSeconds { get; private set; } = ReachMinSeconds;
        private bool _reachFixed;
        // Whether this peer's ragdoll of the body was on its front when the hands reached for it (it is turned over).
        public bool FaceDown { get; private set; }
        private float _stageStart;

        //====================[ Hold Frame ]====================
        // The middle of the body's two swing points (the shoulders, DragGrip.SwingHeight up the spine) is held at a FIXED place in
        // front of the dragger: HoldX to their right, HoldY above their feet and HoldZ out in front of their feet, in the way they
        // face (constants, so it is a number that never changes): not their shoulders, their knees nor how far their arms reach, so it
        // is the same wherever they crouch, walk or turn. The body faces the way its head points (toward the dragger), chest up,
        // level. The grip's own frame (DragGrip.HoldRotation) is put onto it.
        public Vector3 HoldPoint { get; private set; }
        public Quaternion HoldRotation { get; private set; } = Quaternion.identity;
        // Horizontal unit direction from the dragger to the body: the way the dragger faces.
        public Vector3 Away { get; private set; }
        // Horizontal unit direction to the dragger's right.
        private Vector3 _right;
        // How far from where the body lay toward the hold frame its shoulders must rise for both hands to reach them
        // (0..1): worked out through the reach, kept from the grip on.
        private float _meet;

        private static readonly Dictionary<string, DragSession> Active = new Dictionary<string, DragSession>();

        private DragSession(Player dragger, Player body)
        {
            Dragger = dragger;
            Body = body;
            Begin(Stage.Stance);
            Away = Heading();
            UpdateHold();
        }

        //====================[ Registry ]====================
        // The session for a dragged body, made or replaced to match the dragger now holding it.
        public static DragSession Ensure(Player dragger, Player body)
        {
            if (Active.TryGetValue(body.ProfileId, out var session) && session.Dragger == dragger && session.Body == body)
                return session;
            session = new DragSession(dragger, body);
            Active[body.ProfileId] = session;
            DragLog.LogDebug($"[Drag] session {dragger.ProfileId} -> {body.ProfileId}");
            return session;
        }

        public static void End(string bodyId)
        {
            if (string.IsNullOrEmpty(bodyId)) return;
            Active.Remove(bodyId);
        }

        public static bool TryGet(string bodyId, out DragSession session)
        {
            session = null;
            return !string.IsNullOrEmpty(bodyId) && Active.TryGetValue(bodyId, out session);
        }

        // Raid boundary.
        public static void ClearAll()
        {
            Active.Clear();
            RemoteHeadings.Clear();
        }

        //====================[ Progress ]====================
        public bool IsHeld => Current == Stage.Hold;
        // The dragger's hands are on the body (from the reach on).
        public bool HandsOn => Current >= Stage.Reach;
        public float StageTime => Time.time - _stageStart;
        // 0..1 through the turn over (1 once past it).
        public float TurnProgress => Current == Stage.TurnOver ? Mathf.Clamp01(StageTime / TurnOverSeconds) : Current > Stage.TurnOver ? 1f : 0f;
        // 0..1 of the way the body's shoulders have come from where they lay to the hold frame: up to the meet point
        // over the second half of the reach, still through the grip, on to the hold through the lift (1 once held).
        // Each part is a human movement (global::KMABodyDrag.Animation.Shared.MathUtil.MinJerk), starting and ending at rest.
        public float PickupProgress
        {
            get
            {
                switch (Current)
                {
                    case Stage.Reach: return _meet * global::KMABodyDrag.Animation.Shared.MathUtil.MinJerk((StageTime / ReachSeconds - RiseStart) / (1f - RiseStart));
                    case Stage.Grip: return _meet;
                    case Stage.Lift: return Mathf.Lerp(_meet, 1f, global::KMABodyDrag.Animation.Shared.MathUtil.MinJerk(StageTime / LiftSeconds));
                    case Stage.Hold: return 1f;
                    default: return 0f;
                }
            }
        }

        private void Begin(Stage stage)
        {
            Current = stage;
            _stageStart = Time.time;
            DragLog.LogDebug($"[Drag] {Dragger.ProfileId}->{Body.ProfileId} stage {stage} (faceDown={FaceDown}, meet {_meet:F2}) hold={HoldPoint} dragger={Dragger.Position}");
        }

        // Once per tick, from the dragger's tick (DragContext, CorpseDragController), which knows the rest of the drag: the
        // dragger's arms (null before their hands have taken hold) and whether this peer's body is a ragdoll.
        public void Tick(DragReach? arms, bool ragdolled)
        {
            Ragdolled = ragdolled;
            Advance();
            UpdateHold();
            // Without arms on this peer (a headless host) the body is lifted straight from where it lies.
            if (Current == Stage.Reach && arms != null)
            {
                if (!_reachFixed)
                {
                    ReachSeconds = ReachDuration(arms.Value);
                    _reachFixed = true;
                }
                _meet = MeetFraction(arms.Value);
            }
        }

        private void Advance()
        {
            switch (Current)
            {
                case Stage.Stance:
                    if (StageTime < StanceSeconds) return;
                    // The same test as DraggerArms' (a firearm keeps the right hand off the body), on every peer.
                    if (PlayerUtils.HoldsFirearm(Dragger) && StageTime < StanceMaxSeconds) return;
                    // Only a ragdoll can be turned over (a headless host has none, and neither has a body that has not
                    // become one yet). The body's own body counts: it is ragdolled like every peer's copy of it, so
                    // every peer runs the same stages. A body on its side needs no turn: the lift squares it.
                    FaceDown = !FikaBridge.IsHeadless() && Ragdolled && ChestFacesDown();
                    Begin(FaceDown ? Stage.TurnOver : Stage.Reach);
                    return;
                case Stage.TurnOver:
                    if (StageTime < TurnOverSeconds) return;
                    if (DragGrip.IsFaceUp(Body) || StageTime >= TurnOverSeconds + TurnOverGrace) Begin(Stage.Reach);
                    return;
                case Stage.Reach:
                    if (StageTime >= ReachSeconds) Begin(Stage.Grip);
                    return;
                case Stage.Grip:
                    if (StageTime >= GripSeconds) Begin(Stage.Lift);
                    return;
                case Stage.Lift:
                    if (StageTime >= LiftSeconds) Begin(Stage.Hold);
                    return;
            }
        }

        //====================[ Hold Frame ]====================
        // The exact hold (see above), every tick: a fixed place in front of the dragger's feet, in the way they face.
        private void UpdateHold()
        {
            Vector3 heading = Heading();
            Vector3 feet = Dragger.Position;
            Vector3 point = feet + heading * HoldZ + Vector3.Cross(Vector3.up, heading).normalized * HoldX;
            point.y = feet.y + HoldY;
            HoldPoint = point;
            Away = heading;
            _right = Vector3.Cross(Vector3.up, heading).normalized;
            HoldRotation = Quaternion.LookRotation(-heading, Vector3.up);
        }

        // How far along from where the body lay to the hold frame (0..1) its shoulders must come for both hands to
        // reach them with the arms at most at their pickup stretch: each hand's contact goes from where it took hold to
        // its place in the hold frame (the left hand's on the dragger's left), the smallest share that is in reach of
        // both. 0 when the body is already in reach as it lies, 1 when it is not even at the hold.
        private float MeetFraction(DragReach reach)
        {
            var grip = DragGrip.For(Body);
            if (grip == null) return 0f;
            HoldContacts(grip, out Vector3 leftHold, out Vector3 rightHold);
            return Mathf.Max(FirstInReach(reach.LeftShoulder, reach.LeftRest, leftHold, reach.LeftPickupReach),
                             FirstInReach(reach.RightShoulder, reach.RightRest, rightHold, reach.RightPickupReach));
        }

        // Where each hand's contact is in the hold frame (the left one on the dragger's left).
        private void HoldContacts(DragGrip grip, out Vector3 left, out Vector3 right)
        {
            Vector3 across = _right * grip.HalfWidth;
            left = HoldPoint - across;
            right = HoldPoint + across;
        }

        // How long the reach takes (ReachBaseSeconds...): by the longer way a hand goes, from the game's hand to the body as it
        // lay, then on with it to the hold (an upper bound of its path, since the body rises to meet it).
        private float ReachDuration(DragReach reach)
        {
            var grip = DragGrip.For(Body);
            if (grip == null) return ReachMinSeconds;
            HoldContacts(grip, out Vector3 leftHold, out Vector3 rightHold);
            float travel = Mathf.Max(reach.LeftTravel + Vector3.Distance(reach.LeftRest, leftHold),
                                     reach.RightTravel + Vector3.Distance(reach.RightRest, rightHold));
            return Mathf.Clamp(ReachBaseSeconds + ReachSecondsPerMetre * travel, ReachMinSeconds, ReachMaxSeconds);
        }

        // The smallest u (0..1) for which from + (to - from) u is within r of the shoulder: the nearer root of
        // |a + b u|^2 = r^2 (a = from - shoulder, b = to - from). Continuous, so the meet point never jumps.
        private static float FirstInReach(Vector3 shoulder, Vector3 from, Vector3 to, float r)
        {
            Vector3 a = from - shoulder;
            Vector3 b = to - from;
            float c = a.sqrMagnitude - r * r;
            if (c <= 0f) return 0f;
            float bb = b.sqrMagnitude;
            float ab = Vector3.Dot(a, b);
            float discriminant = ab * ab - bb * c;
            if (bb < 1e-8f || discriminant < 0f) return 1f;
            // Out of reach at the start, so both roots have the same sign: negative means the way leads away.
            float u = (-ab - Mathf.Sqrt(discriminant)) / bb;
            return u < 0f ? 1f : Mathf.Min(u, 1f);
        }

        // Whether the body's chest faces the floor.
        private bool ChestFacesDown()
        {
            var grip = DragGrip.For(Body);
            Transform chest = DragGrip.Chest(Body);
            return grip != null && chest != null && !grip.ChestFacesUp(chest);
        }

        //====================[ Dragger Heading ]====================
        // Which way a remote dragger's body faces, as the dragger's own client says (DragHeadingPacket). Fika sends a player's
        // look, never their body's yaw, so this peer's copy of the dragger faces only roughly where they do; the hold frame
        // would then sit off to one side of where the dragger's own has it.
        private const float HeadingStaleSeconds = 0.5f;
        // How quickly the hold frame turns onto a new heading (1/s): the packets come a few times a second.
        private const float HeadingFollowRate = 15f;
        private static readonly Dictionary<string, (float Yaw, float Time)> RemoteHeadings = new Dictionary<string, (float, float)>();
        private float _headingYaw;
        private bool _hasHeadingYaw;

        public static void OnRemoteHeading(string draggerId, float yaw)
        {
            if (string.IsNullOrEmpty(draggerId)) return;
            RemoteHeadings[draggerId] = (yaw, Time.time);
        }

        // The dragger's facing, level: the dragger's own word when it is remote and recent, else the body on this peer.
        private Vector3 Heading()
        {
            if (Dragger.IsYourPlayer || !RemoteHeadings.TryGetValue(Dragger.ProfileId, out var heading)
                || Time.time - heading.Time > HeadingStaleSeconds)
            {
                _hasHeadingYaw = false;
                return global::KMABodyDrag.Animation.Shared.MathUtil.Level(Dragger.Transform.forward, Vector3.forward);
            }

            _headingYaw = _hasHeadingYaw
                ? Mathf.LerpAngle(_headingYaw, heading.Yaw, global::KMABodyDrag.Animation.Shared.MathUtil.Follow(HeadingFollowRate))
                : heading.Yaw;
            _hasHeadingYaw = true;
            return global::KMABodyDrag.Animation.Shared.MathUtil.YawForward(_headingYaw);
        }
    }
}
