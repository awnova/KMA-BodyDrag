//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KMABodyDrag.Animation.Hands
{
    //====================[ DraggerArms ]====================
    // Poses the arms of a player dragging a corpse so each hand holds one of the dragged body's shoulders (KeepMeAlive's
    // DraggerArms, without its stim jab).
    // Kinematic, worked out afresh every frame from the bones (no simulation, so nothing springs or lags):
    //   grip     - a point on top of the shoulder, from the body's DragGrip (its shoulder joints in the bind pose,
    //              fixed in its chest frame), so it is the same spot on every pickup and moves with the body;
    //   shoulder - the collarbone swings forward a little when the hand is past the arm's straight reach;
    //   arm      - a two-bone solve puts the wrist exactly on the grip, the elbow turned by a set angle between
    //              straight down and straight out (and, in first person, away from the camera);
    //   hand     - on the shoulder at a set angle to the arm; the forearm takes the roll (half at the elbow bone, the
    //              rest spread by the twist bones) and the wrist bends, within a limit, to lay it there.
    // Taking hold and letting go are human movements (Motion): the hand goes straight from the game's hand to the grip
    // with a reach's bell-shaped speed (global::KMABodyDrag.Animation.Shared.MathUtil.MinJerk), turning as it goes, the elbow swinging with it; the fingers
    // close as it arrives. A body still out of reach is reached for only as far as the arm stretches (PickupStretch),
    // and DragSession raises it to meet the hands. Letting go opens the fingers and takes the hand back the same way.
    // Run straight after the dragger's own late pass (BodyAnimation.AfterPlayerPose), so the game's pose is final and nothing
    // rewrites the arms afterwards. Purely visual, driven by the DragSession (hands on from its reach stage), so it
    // runs on every peer (the dragger's own first-person view included).
    internal sealed class DraggerArms
    {
        //====================[ Tuning ]====================
        // The pickup: the furthest the arm stretches toward a body still out of reach (shoulder to wrist, fraction of its length); the
        // body rises to meet the hands there (DragSession).
        private const float PickupStretch = 0.95f;
        // The furthest an arm ever stretches, in every phase (shoulder to wrist, fraction of its length). The body always goes to its
        // exact hold place and lags behind it as it is pulled; the wrist follows the body only as far as this, and past it the hand
        // lets go of the shoulder (the fingers close on air) instead of the arm stretching. Below the collarbone's reach (StraightArm
        // in ArmPoser), so the shoulder never swings to make up the difference.
        private const float StretchLimit = 0.95f;
        // A reach and a release are human point-to-point movements (global::KMABodyDrag.Animation.Shared.MathUtil.MinJerk): the hand goes straight from where it is to
        // where it is going, starting and stopping at rest. The reach takes the session's ReachSeconds (the body's rise is timed to
        // it, and it is longer for a longer way, DragSession); letting go takes ReleaseBaseSeconds and as much more per metre from
        // where the hand let go to the game's hand (at least ReleaseMinSeconds, at most ReleaseMaxSeconds), a little quicker than
        // the reach. The hand turns to its grip over the first HandTurnLead of the reach, so it is shaped for the shoulder before it
        // gets there.
        private const float ReleaseBaseSeconds = 0.3f;
        private const float ReleaseSecondsPerMetre = 0.4f;
        private const float ReleaseMinSeconds = 0.4f;
        private const float ReleaseMaxSeconds = 0.9f;
        private const float HandTurnLead = 0.8f;
        // Share of a reach (at its start) and of a release (at its end) the posed arm is cross-faded with the game's, which only
        // takes up the forearm's roll and the wrist's limit: the hand's path already starts and ends on the game's hand.
        private const float CrossFade = 0.3f;
        // The fingers close on the shoulder once the reach is this far through, over FingerCloseSeconds, and open over
        // FingerOpenSeconds as a release begins (before the hand has gone far).
        private const float GripAt = 0.85f;
        // A hand that arrived at its stretch with the body a little short of it settles onto the grip over this long (s).
        private const float SettleSeconds = 0.2f;
        private const float FingerCloseSeconds = 0.15f;
        private const float FingerOpenSeconds = 0.1f;
        // Which way the elbows point, turned about the shoulder-wrist line from their rest direction (down, leaning
        // a little back) toward straight out (degrees), and how quickly they follow a change of it (time constant).
        private const float ElbowAngle = 35f;
        private const float ElbowSmoothing = 0.5f;
        // How far the elbows' rest direction leans back toward the dragger, against straight down (tan of the lean).
        private const float ElbowBackLean = 0.5f;
        // First person only: an elbow or upper arm nearer the camera than this swings away from it (m).
        private const float CameraClearance = 0.3f;
        // The hand on the shoulder, laid out in Blender (Build/Tools/DragPose/make_drag_pose.py, Build/Assets/Downed/Drag_Hold_Pose.blend): the knuckle line
        // on the body's swing point (each shoulder joint DragGrip.SwingHeight, 8.2 cm, up its spine: the top of the shoulder), the palm
        // pressing the shoulder (the surface there faces up the spine), the fingers curled like a grip on the cloth (HoldGrip) and
        // pointing toward the chest front. The angle (degrees), from fingers carrying straight on from the arm with the palm flat on the
        // shoulder: turned about the palm (positive outward), fingertips lifted, and rolled onto its little-finger edge, which follows the
        // shoulder's slope from the neck (the mesh leans 22 deg outward there). Turn and tilt are mirrored between the hands.
        private const float HandTurn = -22.5f;
        private const float HandLift = -5f;
        private const float HandTilt = 25f;
        // The whole hand, turned about the palm contact by this many degrees toward the body: the body's spine direction swung toward
        // its chest front, applied on top of the hand angle above (0: not used now).
        private const float HandTowardBody = 0f;
        // The hand stands this far off the swing point, out along the body's spine (up the shoulder), so the glove rests on the shirt
        // instead of sinking into it: with the knuckles exactly on the shoulder's surface the palm and curled fingers went through the
        // mesh by up to 5 cm. The swing point (what the body hangs from) does not move.
        private const float GripStandOff = 0.02f;
        // Furthest the wrist bends from straight to lay the hand there; past it the hand gives instead. A hand laid on a
        // shoulder from above needs most of a real wrist's flexion.
        private const float MaxWristBend = 80f;
        // How quickly the shoulder reference the hold is worked out from follows the real shoulders (time constant,
        // s): the first-person shoulders move with the camera, and the body should not shake with them.
        private const float ShoulderFollow = 0.3f;
        // How many times the arm is solved again for where the palm ended up (see Pose).
        private const int HandPasses = 4;
        // Arms not kept by their drag session for this long (it ended without a release, e.g. the body left) let go.
        private const float EnsureTimeout = 0.5f;

        // How often the hand trace logs an arm on its way there or back (s).
        private const float MovingTraceInterval = 0.1f;

        //====================[ State ]====================
        // Where an arm is: the game's (Off), going for the body (Reaching), on it (On), or going back to the game's (Releasing).
        private enum Motion { Off, Reaching, On, Releasing }

        private sealed class Arm : ArmPoser
        {
            // How far this hand's pose is the mod's (0 the game's, 1 posed): cross-fades only the ends of a reach and a
            // release (the hand's path does the moving). The right hand stays off the body with a weapon in hand.
            public float Weight;
            public Motion Phase;
            // Seconds into the current reach or release, and how long this release takes.
            public float PhaseTime;
            public float ReleaseSeconds;
            // How far the contact was from the game's own hand when the hands took hold (m).
            public float Travel;
            // The reach starts from the game's hand as it is each frame; one that turns round a release starts from where the
            // hand was instead (From*).
            public bool FromAnimation;
            // Where a release (or a reach that turned one round) starts: the knuckle contact, hand rotation and elbow direction
            // as they were posed, in the dragger's heading frame from their pelvis, so they go with the dragger and not the body.
            public Vector3 FromPoint;
            public Quaternion FromRotation;
            public Vector3 FromBend;
            // The last pose this arm was given (world), for a release to start from.
            public Vector3 LastContact;
            public Quaternion LastHand;
            // How far the fingers are closed on the shoulder (0..1).
            public float Grip;
            // The contact this hand took hold of, as the body lay (world), for DragSession's meet point.
            public Vector3 RestContact;
            // How much further from the shoulder the contact is than the wrist (m), as the locked hand has it: what the reach
            // to the contact is beyond the arm's own. Followed smoothly (ShoulderFollow).
            public float ContactBeyondWrist;
            // Where the shoulder sits relative to the dragger's pelvis, in their heading frame, taken when the hands
            // take hold (crouched): a reference for how far the hands reach that does not move when they look around
            // (the first-person shoulders follow the camera).
            public Vector3 ShoulderFromPelvis;

            // The hold, from the body's DragGrip (offsets along its chest bone's rotation, so it moves and turns with
            // the body): the swing point held (Joint), the shoulder joint under it (ShoulderJoint) and the body's own
            // directions there (up the spine, toward its head). Without a hand frame the hand keeps HandLocal,
            // its pose when it took hold.
            public Vector3 Joint;
            public Vector3 ShoulderJoint;
            public Vector3 Up;
            public Quaternion HandLocal;

            // The smoothed elbow direction (zero until the arm first takes hold).
            public Vector3 Bend;

            // The body's own directions in its chest frame: out of its chest (which way the hand's fingers point when the line to
            // the grip gives none).
            public Vector3 Front;

            // The hand's frame (HandFrames.FromKnuckles) relative to the palm bone, and its finger bones closed round the shoulder
            // (HoldGrip; HoldFingers is null when they could not be set up: the hand then keeps the game's fingers).
            public Quaternion HandFrameLocal;
            public HandFingers HoldFingers;
            // This frame's shoulder, wrist target and palm contact (world), for the trace.
            public Vector3 TraceShoulder, TraceWrist, TraceContact;
            // How far (degrees) the hand ended up from the rotation the hold wants for it (the wrist cannot bend past its limit), and
            // how far the wrist is bent from straight.
            public float TraceHandOff, TraceWristBend;
        }

        // Keyed by dragger profile id.
        private static readonly Dictionary<string, DraggerArms> Active = new Dictionary<string, DraggerArms>();
        // dragger>body pairs whose arms could not be built; not retried this raid.
        private static readonly HashSet<string> Failed = new HashSet<string>();

        private readonly Player _dragger;
        // The dragged body: the corpse's Player (its bones are still there, CorpseRegistry).
        private readonly Player _body;
        private readonly string _draggerId;
        // The dragged body's upper chest bone: the frame the hands are locked to.
        private Transform _chest;
        private Transform _draggerPelvis;
        private readonly List<Arm> _arms = new List<Arm>();

        // The dragger has a weapon in the right hand: only the left holds the body.
        private bool _rightFree;
        // Whether the drag session wants the hands on the body, and when it last said so.
        private bool _handsOn;
        // Whether this peer has taken the body into its own physics (DragContext says so).
        private bool _bodyTaken;
        private float _lastEnsure;
        // The hands have taken hold (TakeHold done) for the current hold; cleared once they have let go.
        private bool _held;
        // Per hold, for the log line when the hands let go: frames on the body, frames an arm was past StretchLimit, the most it
        // reached (fraction of its length) and the furthest its wrist ended from where it was put (m).
        private int _statFrames, _statOver;
        private float _statMaxReach, _statMaxMiss;
        // The drag wanted the hands on the body last frame: a new hold (a fresh pickup by the same pair) takes hold afresh.
        private bool _wanted;
        // How long the session says the reach takes (DragSession.ReachSeconds).
        private float _reachSeconds = 0.5f;

        private float _nextArmTrace;

        private DraggerArms(Player dragger, Player body)
        {
            _dragger = dragger;
            _body = body;
            _draggerId = dragger.ProfileId;
        }

        //====================[ Public API ]====================
        // Per tick while the body is dragged (CorpseDragController): builds the arms, and puts the hands on the
        // body (handsOn) or keeps them off it. bodyTaken: this peer has the body in its own physics (CorpseBody).
        // A weapon in the dragger's hand is held by the right hand, so only the left holds the body.
        public static void Ensure(Player dragger, Player body, bool handsOn, bool bodyTaken, float reachSeconds)
        {
            if (dragger == null || body == null) return;
            if (FikaBridge.IsHeadless()) return;

            if (!Active.TryGetValue(dragger.ProfileId, out var rig) || rig._dragger != dragger || rig._body != body)
            {
                string pair = dragger.ProfileId + ">" + body.ProfileId;
                if (Failed.Contains(pair)) return;

                Discard(dragger.ProfileId);
                try
                {
                    rig = new DraggerArms(dragger, body);
                    rig.Build();
                    Active[dragger.ProfileId] = rig;
                    DragLog.LogDebug($"[DraggerArms] Built for {dragger.ProfileId} -> {body.ProfileId}");
                }
                catch (Exception ex)
                {
                    Failed.Add(pair);
                    Plugin.LogSource.LogError($"[DraggerArms] Build failed for {pair}: {ex}");
                    return;
                }
            }

            rig._handsOn = handsOn;
            rig._rightFree = PlayerUtils.HoldsFirearm(dragger);
            rig._bodyTaken = bodyTaken;
            rig._reachSeconds = reachSeconds;
            rig._lastEnsure = Time.time;
        }

        // The drag ended: the hands let go now (they blend back to the animation, then the arms are dropped).
        public static void Release(Player dragger)
        {
            if (dragger != null && Active.TryGetValue(dragger.ProfileId, out var rig) && rig._dragger == dragger)
            {
                rig._handsOn = false;
                // Not kept any longer: dropped once blended out.
                rig._lastEnsure = -EnsureTimeout;
            }
        }

        // Called straight after each player's own late pass (BodyAnimation.AfterPlayerPose).
        public static void AfterPlayerPose(Player player)
        {
            if (Active.Count == 0 || player == null) return;
            if (Active.TryGetValue(player.ProfileId, out var rig) && rig._dragger == player) rig.Tick();
        }

        // Where the dragger's shoulders are (world), how far each hand's contact is from its shoulder with the arm at
        // PickupStretch of its length, and the contact each hand took hold of, for DragSession to work out where the body
        // meets the hands in the pickup. The reach is the arm's own (shoulder to wrist) plus how much further the contact is
        // with the hand laid on it (ContactBeyondWrist), so the posed arm really is at that extension. The shoulders are kept relative to the pelvis in the dragger's heading
        // frame and follow the real ones smoothly (ShoulderFollow), so the body does not shake when the first-person
        // shoulders move with the camera. False until the hands have taken hold.
        public static bool TryGetShoulders(Player dragger, out DragReach reach)
        {
            reach = default;
            if (dragger == null || !Active.TryGetValue(dragger.ProfileId, out var rig) || rig._dragger != dragger || !rig._held) return false;

            Vector3 leftShoulder = Vector3.zero, rightShoulder = Vector3.zero, leftRest = Vector3.zero, rightRest = Vector3.zero;
            float leftPickup = 0f, rightPickup = 0f, leftTravel = 0f, rightTravel = 0f;
            Quaternion heading = rig.Heading();
            foreach (var arm in rig._arms)
            {
                Vector3 shoulder = rig._draggerPelvis.position + heading * arm.ShoulderFromPelvis;
                float pickup = (arm.UpperLength + arm.ForeLength) * PickupStretch + arm.ContactBeyondWrist;
                if (arm.IsLeft)
                {
                    leftShoulder = shoulder;
                    leftPickup = pickup;
                    leftRest = arm.RestContact;
                    leftTravel = arm.Travel;
                }
                else
                {
                    rightShoulder = shoulder;
                    rightPickup = pickup;
                    rightRest = arm.RestContact;
                    rightTravel = arm.Travel;
                }
            }
            reach = new DragReach(leftShoulder, rightShoulder, leftPickup, rightPickup, leftRest, rightRest, leftTravel, rightTravel);
            return true;
        }

        // Raid boundary: the players go with the scene.
        public static void ClearAll()
        {
            FullScaleArms.ClearAll();
            Active.Clear();
            Failed.Clear();
        }

        // Drops a dragger's arms.
        private static void Discard(string draggerId)
        {
            if (Active.TryGetValue(draggerId, out var rig)) FullScaleArms.Want(rig._dragger, rig, false);
            Active.Remove(draggerId);
        }

        //====================[ Build ]====================
        private void Build()
        {
            _chest = DragGrip.Chest(_body);
            _draggerPelvis = DragGrip.RequirePart(_dragger, EBodyPartColliderType.Pelvis);
            if (_chest == null) throw new InvalidOperationException("the dragged body has no chest bone");

            var twists = ArmPoser.FindTwists(_dragger);
            BuildArm(true, _dragger.PlayerBones.LeftPalm, twists);
            BuildArm(false, _dragger.PlayerBones.RightPalm, twists);
            foreach (var arm in _arms)
            {
                try
                {
                    BuildHoldFingers(arm);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogWarning($"[DraggerArms] Closed fingers not available for {_draggerId}: {ex.Message}");
                    arm.HoldFingers = null;
                }
            }
        }

        // The fingers of a hand, set from `bones` (HoldGrip's); also reads the hand's frame, which they are turned with. Null when
        // the knuckles or a bone are missing.
        private HandFingers BuildFingers(Arm arm, IReadOnlyList<string> bones)
        {
            if (!HandFrames.TryReadKnuckleFrame(arm.HandBone, arm.IsLeft, out var frameLocal)) return null;
            arm.HandFrameLocal = frameLocal;
            return HandFingers.TryCreate(_dragger, arm.HandBone, arm.IsLeft, bones, "DraggerArms");
        }

        // The fingers closed round the shoulder, for each hand (HoldGrip).
        private void BuildHoldFingers(Arm arm)
        {
            var hand = HoldGrip.Instance?.For(arm.IsLeft);
            if (hand != null) arm.HoldFingers = BuildFingers(arm, hand.Bones);
        }

        private void BuildArm(bool isLeft, Transform palm, TwistRelax[] twists) =>
            _arms.Add(ArmPoser.Create<Arm>(_dragger, isLeft, palm, twists, "DraggerArms"));

        //====================[ Taking Hold ]====================
        // Gives each hand one of the body's grip points and records the body's own directions there, all from its
        // DragGrip, so the grip is the same however the body lies. Kept in the chest frame for the whole hold.
        // A ragdoll on its back is always lifted into the same pose (head toward the dragger, its left shoulder on
        // their left: DragSession), so its left shoulder goes to the left hand whichever way it lies now. Anything
        // else (a body this peer has not taken into its physics, a ragdoll that would not turn over) is held by the shoulder on
        // each hand's side. False without a grip.
        private bool TakeHold()
        {
            var grip = DragGrip.For(_body);
            if (grip == null) return false;

            bool faceUp = grip.ChestFacesUp(_chest);
            bool crossed = false;
            if (!(faceUp && _bodyTaken))
            {
                Vector3 toBody = _chest.position - _dragger.Position;
                toBody.y = 0f;
                // The body's left shoulder further to the dragger's right than its right shoulder: the hands cross over.
                Vector3 across = DragGrip.World(_chest, grip.Left) - DragGrip.World(_chest, grip.Right);
                crossed = toBody.sqrMagnitude > 1e-4f && Vector3.Dot(across, Vector3.Cross(Vector3.up, toBody)) > 0f;
            }
            // The swing point is the top of the shoulder, whose surface faces up the spine (toward the head, where the dragger
            // is): the hands lie on it with the palm pressing the other way, whichever side of the body faces up.
            Vector3 up = grip.Spine;

            _statFrames = _statOver = 0;
            _statMaxReach = _statMaxMiss = 0f;
            Quaternion heading = Heading();
            Quaternion toChest = Quaternion.Inverse(_chest.rotation);
            HandTrace.Log($"drag {_draggerId}: hands take hold of {_body.ProfileId}: faceUp={faceUp} crossed={crossed} taken={_bodyTaken} " +
                $"chest {HandTrace.V(_chest.position)} up {HandTrace.V(_chest.rotation * grip.Front)} spine {HandTrace.V(_chest.rotation * grip.Spine)} " +
                $"dragger pose level {_dragger.MovementContext.PoseLevel:F2} firstPerson={_dragger.FirstPersonPointOfView}");
            foreach (var arm in _arms)
            {
                bool left = arm.IsLeft != crossed;
                arm.Joint = left ? grip.SwingLeft : grip.SwingRight;
                arm.ShoulderJoint = left ? grip.Left : grip.Right;
                arm.Up = up;
                arm.Front = grip.Front;
                arm.HandLocal = toChest * arm.HandBone.rotation;
                arm.ShoulderFromPelvis = Quaternion.Inverse(heading) * (arm.UpperBone.position - _draggerPelvis.position);
                arm.RestContact = Contact(arm);
                // The game's hand now (its pose, as this runs before the arms are posed) to where the body lies.
                arm.Travel = arm.HasHandFrame
                    ? Vector3.Distance(arm.HandBone.position + arm.HandBone.rotation * arm.KnuckleContactLocal, arm.RestContact)
                    : 0f;
                // A first estimate, followed every frame from here (Pose).
                arm.ContactBeyondWrist = arm.HasHandFrame ? MeasureBeyondWrist(arm.UpperBone.position, Contact(arm), LockedHand(arm) * arm.KnuckleContactLocal) : 0f;
            }
            return true;
        }

        // How much further from the shoulder a contact is than the wrist that puts the hand on it (offset: wrist to contact).
        private static float MeasureBeyondWrist(Vector3 shoulder, Vector3 contact, Vector3 offset) =>
            (contact - shoulder).magnitude - (contact - offset - shoulder).magnitude;

        // Where the palm meets the shoulder: the body's swing point, the shoulder joint's top.
        private Vector3 Contact(Arm arm) => ChestPosition + ChestRotation * arm.Joint;

        // The body's chest frame the hold is in: the chest bone.
        private Vector3 ChestPosition => _chest.position;
        private Quaternion ChestRotation => _chest.rotation;

        // The hand's rotation on the shoulder: from palm flat on the shoulder's top with the fingers carrying straight on from
        // the arm (the line from the dragger's shoulder to the grip, laid onto the surface), turned by the hand angle
        // (turn and tilt mirrored between the hands), then mapped onto the hand bone through its own frame.
        private Quaternion LockedHand(Arm arm)
        {
            if (!arm.HasHandFrame) return ChestRotation * arm.HandLocal;

            Vector3 up = ChestRotation * arm.Up;
            Vector3 fingers = Vector3.ProjectOnPlane(Contact(arm) - arm.UpperBone.position, up);
            if (fingers.sqrMagnitude < 1e-6f) fingers = Vector3.ProjectOnPlane(ChestRotation * arm.Front, up);
            if (fingers.sqrMagnitude < 1e-6f) return ChestRotation * arm.HandLocal;
            Quaternion grip = Quaternion.LookRotation(fingers, -up);

            // In the grip frame (z the fingers, y into the body), for the hand on the dragger's left: +y turns the
            // fingers to its left (outward), +z drops its left (little-finger) edge, and +x lifts the fingertips. The
            // right hand is the mirror image.
            float side = arm.IsLeft ? 1f : -1f;
            Quaternion angle = Quaternion.Euler(HandLift, side * HandTurn, side * HandTilt);

            Quaternion handFrame = Quaternion.LookRotation(arm.FingerLocal, arm.PalmLocal);
            Vector3 spine = ChestRotation * arm.Up;
            float toward = HandTowardBody * Mathf.Deg2Rad;
            Quaternion turn = Quaternion.FromToRotation(spine, spine * Mathf.Cos(toward) + ChestRotation * arm.Front * Mathf.Sin(toward));
            return turn * grip * angle * Quaternion.Inverse(handFrame);
        }

        // Where the hand must sit (the contact), the locked hand rotation, and where on the hand the contact is (from the wrist, in the
        // palm bone's frame); the wrist for them is contact - hand * contactLocal. A hand holding the body has the knuckle line, the
        // palm's far end from the wrist joint, on the swing point.
        private void PalmTarget(Arm arm, out Vector3 contact, out Quaternion hand, out Vector3 contactLocal)
        {
            hand = LockedHand(arm);
            contact = Contact(arm) + ChestRotation * arm.Up * GripStandOff;
            contactLocal = arm.KnuckleContactLocal;
        }

        private Arm RightArm() => _arms.Find(a => !a.IsLeft);

        // The fingers closed round the shoulder as far as the hand grips (open, the game's own, on the way there and back), turned
        // with the hand.
        private void ApplyHoldFingers(Arm arm)
        {
            if (arm.HoldFingers == null) return;
            arm.HoldFingers.Apply(arm.HandBone.rotation * arm.HandFrameLocal, HoldGrip.Instance.For(arm.IsLeft).Pose, arm.Grip * arm.Weight);
        }

        //====================[ Per Frame ]====================
        private void Tick()
        {
            if (_body == null || _chest == null || _draggerPelvis == null || _arms.Count == 0)
            {
                Discard(_draggerId);
                return;
            }

            // The body is a corpse: only the dragger has to be alive.
            bool kept = Time.time - _lastEnsure < EnsureTimeout;
            bool want = kept && _handsOn && PlayerUtils.IsAlive(_dragger);
            if (want && (!_held || !_wanted)) _held = TakeHold();
            _wanted = want;

            float onBody = 0f;
            foreach (var arm in _arms)
            {
                bool holds = want && _held && !(_rightFree && !arm.IsLeft);
                Advance(arm, holds);
                onBody = Mathf.Max(onBody, arm.Weight);
            }

            // The game squashes first-person arms along the view's depth; they must be full size to reach the body, so they start
            // growing back when the pickup begins (a second or so) and stay so until the hands have let go.
            FullScaleArms.Want(_dragger, this, kept || onBody > 0f);

            if (onBody <= 0f)
            {
                // Let go: the next hold takes hold afresh.
                if (!want) LogHoldStats();
                if (!want) _held = false;
                if (!kept) Discard(_draggerId);
                return;
            }

            // The game's shoulders this frame, before the pose moves the collarbones: the hold follows them.
            Quaternion toHeading = Quaternion.Inverse(Heading());
            float follow = global::KMABodyDrag.Animation.Shared.MathUtil.FollowOver(ShoulderFollow);
            foreach (var arm in _arms)
            {
                Vector3 now = toHeading * (arm.UpperBone.position - _draggerPelvis.position);
                arm.ShoulderFromPelvis = Vector3.Lerp(arm.ShoulderFromPelvis, now, follow);
            }

            Camera camera = PlayerUtils.IsFirstPersonLocal(_dragger) && PlayerUtils.TryGetMainCamera(out var mainCam) ? mainCam : null;
            foreach (var arm in _arms)
            {
                if (arm.Weight <= 0f) continue;
                Pose(arm, camera);
                ApplyHoldFingers(arm);
            }
            CountStretch();

            if (HandTrace.On && _held && Time.time >= _nextArmTrace)
            {
                // Closer together while a hand is on its way, to see the path.
                bool moving = _arms.Exists(a => a.Phase == Motion.Reaching || a.Phase == Motion.Releasing);
                _nextArmTrace = Time.time + (moving ? MovingTraceInterval : HandTrace.Interval);
                foreach (var arm in _arms)
                {
                    if (arm.Weight <= 0f) continue;
                    float length = arm.UpperLength + arm.ForeLength;
                    float reach = (arm.TraceWrist - arm.TraceShoulder).magnitude;
                    Vector3 joint = ChestPosition + ChestRotation * arm.ShoulderJoint;
                    HandTrace.Log($"drag {_draggerId} {(arm.IsLeft ? "L" : "R")} {arm.Phase} {arm.PhaseTime:F2}s weight {arm.Weight:F2} grip {arm.Grip:F2} beyond wrist {HandTrace.Cm(arm.ContactBeyondWrist)} | pose level {_dragger.MovementContext.PoseLevel:F2} pelvis y {_draggerPelvis.position.y:F2} speed {_dragger.Velocity.magnitude:F2} | shoulder {HandTrace.V(arm.TraceShoulder)} " +
                        $"ribcage scale {_dragger.PlayerBones.Ribcage.Original.localScale.z:F2} (target {_dragger.RibcageScaleCurrentTarget:F2}) | body joint {HandTrace.V(joint)} contact {HandTrace.V(arm.TraceContact)} (up the spine from the joint {HandTrace.Cm(Vector3.Dot(arm.TraceContact - joint, ChestRotation * arm.Up))}, out of the chest {HandTrace.Cm(Vector3.Dot(arm.TraceContact - joint, ChestRotation * arm.Front))}) | " +
                        $"wrist wanted {HandTrace.V(arm.TraceWrist)} got {HandTrace.V(arm.HandBone.position)} short {HandTrace.Cm(arm.Miss)} | knuckles off the contact by {HandTrace.Cm(Vector3.Distance(arm.HandBone.position + arm.HandBone.rotation * arm.KnuckleContactLocal, arm.TraceContact))} | hand off its pose by {arm.TraceHandOff:F0} deg, wrist bent {arm.TraceWristBend:F0} deg | " +
                        $"reach {HandTrace.Cm(reach)} of {HandTrace.Cm(length)} = {reach / length * 100f:F0}% | elbow {HandTrace.V(arm.ForeBone.position)} | " +
                        $"yaw body {YawText(_dragger.Transform.forward)} look {_dragger.MovementContext.Rotation.x:F0} shoulders {ShoulderLineYaw()}");
                }
            }
        }

        // How far each arm on the body reaches this frame (shoulder to wrist, fraction of its length), counted per hold.
        private void CountStretch()
        {
            if (!_held) return;
            bool counted = false;
            foreach (var arm in _arms)
            {
                if (arm.Phase != Motion.On || arm.Weight < 1f) continue;
                float reached = (arm.HandBone.position - arm.UpperBone.position).magnitude / (arm.UpperLength + arm.ForeLength);
                _statMaxReach = Mathf.Max(_statMaxReach, reached);
                _statMaxMiss = Mathf.Max(_statMaxMiss, arm.Miss);
                if (reached > StretchLimit + 0.005f) _statOver++;
                counted = true;
            }
            if (counted) _statFrames++;
        }

        private void LogHoldStats()
        {
            if (_statFrames == 0) return;
            HandTrace.Log($"drag {_draggerId}: hold over: {_statFrames} frames on the body, an arm past {StretchLimit * 100f:F0}% of its length on {_statOver}, " +
                $"most {_statMaxReach * 100f:F0}%, furthest wrist miss {HandTrace.Cm(_statMaxMiss)}");
            _statFrames = _statOver = 0;
        }

        // Yaw (degrees) of a direction, for the trace.
        private static string YawText(Vector3 direction) => $"{(global::KMABodyDrag.Animation.Shared.MathUtil.YawOf(direction)):F0}";

        // Yaw of the dragger's line from their left shoulder to their right plus 90 degrees, i.e. which way their shoulders face.
        private string ShoulderLineYaw()
        {
            var left = _arms.Find(a => a.IsLeft);
            var right = RightArm();
            if (left == null || right == null) return "?";
            Vector3 line = right.UpperBone.position - left.UpperBone.position;
            return YawText(Vector3.Cross(line, Vector3.up));
        }

        // The dragger's heading (yaw only).
        private Quaternion Heading() => global::KMABodyDrag.Animation.Shared.MathUtil.Heading(_dragger.Transform.forward);

        //====================[ Motion ]====================
        // Moves an arm on through its reach, hold and release, and sets its weight and grip for this frame. holds: it should be on
        // the body.
        private void Advance(Arm arm, bool holds)
        {
            float dt = Time.deltaTime;
            arm.PhaseTime += dt;
            switch (arm.Phase)
            {
                case Motion.Off:
                    if (holds) BeginReach(arm, fromAnimation: true);
                    break;
                case Motion.Reaching:
                    if (!holds) BeginRelease(arm);
                    else if (arm.PhaseTime >= _reachSeconds) Begin(arm, Motion.On);
                    break;
                case Motion.On:
                    if (!holds) BeginRelease(arm);
                    break;
                case Motion.Releasing:
                    // Taken hold of again on the way back: the reach turns round from where the hand is.
                    if (holds) BeginReach(arm, fromAnimation: false);
                    else if (arm.PhaseTime >= arm.ReleaseSeconds) Begin(arm, Motion.Off);
                    break;
            }

            // The pose cross-fades in over the start of a reach (from the game's arm) and out over the end of a release.
            switch (arm.Phase)
            {
                case Motion.Off:
                    arm.Weight = 0f;
                    break;
                case Motion.Reaching:
                    arm.Weight = Mathf.Max(arm.Weight, Mathf.SmoothStep(0f, 1f, arm.PhaseTime / (CrossFade * _reachSeconds)));
                    break;
                case Motion.On:
                    arm.Weight = 1f;
                    break;
                case Motion.Releasing:
                    float left = 1f - arm.PhaseTime / arm.ReleaseSeconds;
                    arm.Weight = Mathf.Min(arm.Weight, Mathf.SmoothStep(0f, 1f, left / CrossFade));
                    break;
            }

            // The fingers close as the hand arrives and stay closed; they open first thing on a release.
            bool grips = arm.Phase == Motion.On || (arm.Phase == Motion.Reaching && arm.PhaseTime >= GripAt * _reachSeconds);
            arm.Grip = Mathf.MoveTowards(arm.Grip, grips ? 1f : 0f, dt / (grips ? FingerCloseSeconds : FingerOpenSeconds));
        }

        private static void Begin(Arm arm, Motion phase)
        {
            arm.Phase = phase;
            arm.PhaseTime = 0f;
        }

        private void BeginReach(Arm arm, bool fromAnimation)
        {
            arm.FromAnimation = fromAnimation || arm.Weight <= 0f;
            if (!arm.FromAnimation) KeepFrom(arm);
            Begin(arm, Motion.Reaching);
        }

        private void BeginRelease(Arm arm)
        {
            if (arm.Weight <= 0f)
            {
                Begin(arm, Motion.Off);
                return;
            }
            KeepFrom(arm);
            // As long as the way back to the game's hand (this runs before the arm is posed, so its bones are the game's) asks.
            float way = arm.HasHandFrame
                ? Vector3.Distance(arm.LastContact, arm.HandBone.position + arm.HandBone.rotation * arm.KnuckleContactLocal)
                : 0f;
            arm.ReleaseSeconds = Mathf.Clamp(ReleaseBaseSeconds + ReleaseSecondsPerMetre * way, ReleaseMinSeconds, ReleaseMaxSeconds);
            Begin(arm, Motion.Releasing);
        }

        // Keeps the arm's last pose, in the dragger's own frame, as where a release (or a turned-round reach) starts.
        private void KeepFrom(Arm arm)
        {
            Quaternion toHeading = Quaternion.Inverse(Heading());
            arm.FromPoint = toHeading * (arm.LastContact - _draggerPelvis.position);
            arm.FromRotation = toHeading * arm.LastHand;
            arm.FromBend = toHeading * arm.Bend;
        }

        //====================[ Pose ]====================
        // Poses one arm onto its grip (or along its way there or back), then blends it in from the animated arm by its
        // weight. Rotations only: every bone stays on its parent, so the arm can never come off the body.
        private void Pose(Arm arm, Camera camera)
        {
            var from = arm.Capture();
            // The game's own hand and elbow this frame, before anything is posed: where a reach starts and a release ends.
            Vector3 gameWrist = arm.HandBone.position;
            Quaternion gameHand = arm.HandBone.rotation;
            Vector3 gameElbow = arm.ForeBone.position;

            PalmTarget(arm, out Vector3 contact, out Quaternion locked, out Vector3 contactLocal);
            if (!arm.HasHandFrame) contactLocal = Vector3.zero;
            Vector3 shoulder = arm.UpperBone.position;
            if (arm.HasHandFrame)
                arm.ContactBeyondWrist = Mathf.Lerp(arm.ContactBeyondWrist, MeasureBeyondWrist(shoulder, contact, locked * contactLocal), global::KMABodyDrag.Animation.Shared.MathUtil.FollowOver(ShoulderFollow));

            // On the way there or back: the hand goes straight between the game's hand and the grip, turning as it goes, and the
            // elbow swings between the game's and the hold's with it.
            bool reaching = arm.Phase == Motion.Reaching;
            bool releasing = arm.Phase == Motion.Releasing;
            float along = 1f;
            Vector3 fromBend = Vector3.zero;
            if (arm.Phase == Motion.On && arm.PhaseTime < SettleSeconds)
            {
                // Just arrived: the body met the hand within a centimetre or two of its stretch; the hand settles onto the grip.
                Vector3 stretched = WithinStretch(arm, shoulder, contact, locked * contactLocal);
                contact = Vector3.Lerp(stretched, contact, global::KMABodyDrag.Animation.Shared.MathUtil.MinJerk(arm.PhaseTime / SettleSeconds));
            }
            else if (reaching || releasing)
            {
                Quaternion heading = Heading();
                Vector3 gameContact = gameWrist + gameHand * contactLocal;
                bool fromGame = reaching && arm.FromAnimation;
                Vector3 fromContact = fromGame ? gameContact : _draggerPelvis.position + heading * arm.FromPoint;
                Quaternion fromHand = fromGame ? gameHand : heading * arm.FromRotation;
                fromBend = fromGame ? ArmPoser.BendToward(shoulder, gameWrist, gameElbow) : heading * arm.FromBend;
                if (reaching)
                {
                    // Out of reach as the body lies: aimed at the grip as far as the arm stretches; the body rises to meet it.
                    contact = WithinStretch(arm, shoulder, contact, locked * contactLocal);
                    along = global::KMABodyDrag.Animation.Shared.MathUtil.MinJerk(arm.PhaseTime / _reachSeconds);
                    contact = Vector3.Lerp(fromContact, contact, along);
                    locked = Quaternion.Slerp(fromHand, locked, global::KMABodyDrag.Animation.Shared.MathUtil.MinJerk(arm.PhaseTime / (HandTurnLead * _reachSeconds)));
                }
                else
                {
                    // Back from where it let go to the game's hand: the same kind of path the other way.
                    along = global::KMABodyDrag.Animation.Shared.MathUtil.MinJerk(arm.PhaseTime / arm.ReleaseSeconds);
                    contact = Vector3.Lerp(fromContact, gameContact, along);
                    locked = Quaternion.Slerp(fromHand, gameHand, along);
                }
            }

            Vector3 wrist = LimitWrist(arm, shoulder, contact - locked * contactLocal);
            arm.TraceShoulder = shoulder;
            arm.TraceContact = contact;
            arm.ReachWithShoulder(wrist);
            Vector3 bend;
            if (reaching)
                bend = ElbowBend(arm, wrist, camera, fromBend, along);
            else if (releasing)
                bend = ElbowBend(arm, wrist, camera, fromBend, along, ArmPoser.BendToward(arm.UpperBone.position, gameWrist, gameElbow));
            else
                bend = ElbowBend(arm, wrist, camera);
            float maxBend = MaxWristBend;

            // The wrist is solved for the palm as the locked hand would put it, but the wrist bends only so far, so the hand
            // gives and its palm lands off the contact. So the arm is solved again for the wrist that puts the palm where the hand
            // really ended up on the contact, a few times over (each pass moves the palm nearer).
            for (int pass = 0; pass < HandPasses; pass++)
            {
                arm.Solve(wrist, bend);
                if (!arm.HasHandFrame) break;
                arm.LayHand(locked, maxBend);
                wrist = LimitWrist(arm, shoulder, contact - arm.HandBone.rotation * contactLocal);
            }
            arm.Solve(wrist, bend);
            arm.TraceWrist = wrist;
            arm.Finish(locked, maxBend, from, arm.Weight);
            arm.TraceHandOff = Quaternion.Angle(locked, arm.HandBone.rotation);
            arm.TraceWristBend = arm.HasHandFrame ? Vector3.Angle(arm.HandBone.rotation * arm.FingerLocal, arm.ForeBone.rotation * arm.ForeAxisLocal) : 0f;

            // Where the hand is now, blend and all: a release starts from here.
            arm.LastHand = arm.HandBone.rotation;
            arm.LastContact = arm.HandBone.position + arm.LastHand * contactLocal;
        }

        // The wrist brought in, if need be, to StretchLimit of the arm's length from the shoulder: the arm never stretches further, in
        // any phase. The hand then stops short of the body's shoulder (it holds air) rather than the arm reaching for it.
        private static Vector3 LimitWrist(Arm arm, Vector3 shoulder, Vector3 wrist)
        {
            Vector3 toWrist = wrist - shoulder;
            float most = StretchLimit * (arm.UpperLength + arm.ForeLength);
            return toWrist.sqrMagnitude <= most * most ? wrist : shoulder + toWrist.normalized * most;
        }

        // The contact moved in, if need be, so the wrist that puts the locked hand on it (offset: wrist to contact) is no further
        // from the shoulder than PickupStretch of the arm.
        private static Vector3 WithinStretch(Arm arm, Vector3 shoulder, Vector3 contact, Vector3 offset)
        {
            Vector3 toWrist = contact - offset - shoulder;
            float most = PickupStretch * (arm.UpperLength + arm.ForeLength);
            return toWrist.sqrMagnitude <= most * most ? contact : shoulder + toWrist.normalized * most + offset;
        }

        // The elbow on the way there or back: swung from `from` (where it started) to `to` (where it is going; the hold's
        // direction when not given) as far as the hand has come (along, 0..1), so the elbow moves with the hand.
        private Vector3 ElbowBend(Arm arm, Vector3 wrist, Camera camera, Vector3 from, float along, Vector3 to = default)
        {
            Vector3 hold = ElbowTarget(arm, wrist, camera, out Vector3 axis, out Vector3 down);
            if (to.sqrMagnitude < 0.5f) to = hold;
            Vector3 bend = from.sqrMagnitude > 0.5f ? Vector3.Slerp(from, to, along) : to;
            return KeepBend(arm, bend, axis, down);
        }

        // The elbow on the body: the hold's direction, smoothed so a change of direction never pops.
        private Vector3 ElbowBend(Arm arm, Vector3 wrist, Camera camera)
        {
            Vector3 bend = ElbowTarget(arm, wrist, camera, out Vector3 axis, out Vector3 down);
            if (arm.Bend.sqrMagnitude > 0.5f)
                bend = Vector3.Slerp(arm.Bend, bend, global::KMABodyDrag.Animation.Shared.MathUtil.FollowOver(ElbowSmoothing));
            return KeepBend(arm, bend, axis, down);
        }

        // A bend laid square to the shoulder-wrist line, kept on the arm for the next frame.
        private static Vector3 KeepBend(Arm arm, Vector3 bend, Vector3 axis, Vector3 down)
        {
            if (axis.sqrMagnitude < 0.5f) return arm.Bend;
            bend = Vector3.ProjectOnPlane(bend, axis);
            if (bend.sqrMagnitude < 1e-6f) bend = down;
            if (bend.sqrMagnitude < 1e-6f) return arm.Bend;
            arm.Bend = bend.normalized;
            return arm.Bend;
        }

        // Which way the elbow points on the body, perpendicular to the shoulder-wrist line: turned from its rest direction
        // (down, leaning a little back) toward straight out by ElbowAngle; in first person swung away from the camera when
        // the arm would come too near it. Also gives the line (axis, zero when the wrist is on the shoulder) and the rest
        // direction (down).
        private Vector3 ElbowTarget(Arm arm, Vector3 wrist, Camera camera, out Vector3 axis, out Vector3 down)
        {
            Vector3 shoulder = arm.UpperBone.position;
            axis = wrist - shoulder;
            down = Vector3.zero;
            if (axis.sqrMagnitude < 1e-6f)
            {
                axis = Vector3.zero;
                return arm.Bend;
            }
            axis.Normalize();

            // Plain down is undefined when the shoulder is straight above the hand (looking down in first person);
            // leaning it back keeps it defined.
            down = Vector3.ProjectOnPlane(Vector3.down + Heading() * Vector3.back * ElbowBackLean, axis);
            if (down.sqrMagnitude < 1e-6f) down = Vector3.ProjectOnPlane(Vector3.down, axis);
            if (down.sqrMagnitude < 1e-6f)
            {
                down = Vector3.zero;
                return arm.Bend.sqrMagnitude > 0.5f ? arm.Bend : Vector3.Cross(axis, Vector3.up).normalized;
            }
            down.Normalize();
            Vector3 outward = Heading() * (arm.IsLeft ? Vector3.left : Vector3.right);
            outward = Vector3.ProjectOnPlane(Vector3.ProjectOnPlane(outward, axis), down);
            outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : Vector3.Cross(axis, down);

            float angle = ElbowAngle * Mathf.Deg2Rad;
            Vector3 bend = down * Mathf.Cos(angle) + outward * Mathf.Sin(angle);

            if (camera != null)
            {
                // The elbow circles the shoulder-wrist line; furthest from the camera is straight away from it.
                Vector3 cam = camera.transform.position;
                Vector3 elbow = arm.ElbowPoint(shoulder, wrist, bend, out Vector3 centre);
                float nearest = Mathf.Min(Vector3.Distance(elbow, cam), Vector3.Distance((shoulder + elbow) * 0.5f, cam));
                Vector3 away = Vector3.ProjectOnPlane(centre - cam, axis);
                if (nearest < CameraClearance && away.sqrMagnitude > 1e-6f)
                    bend = Vector3.Slerp(bend, away.normalized, 1f - nearest / CameraClearance);
            }
            return bend;
        }
    }
}
