//====================[ Imports ]====================
using EFT;
using EFT.InventoryLogic;
using JsonType;
using UnityEngine;

namespace KMABodyDrag.Dragging
{
    //====================[ CorpseDragController ]====================
    // Drag claims and releases, and each peer's part of a drag (KeepMeAlive's DownedDragController, for a corpse; DragSession has
    // the pickup and the hold frame):
    //   dragger - picks the body up (locked in place until it is lifted, DragPickupLockPatches), then lets go when the drag
    //             stance breaks (and also on Interact, when DragSettings.INTERACT_DROPS is on);
    //   everyone - their copy of the body is held by the dragger's hands (CorpseBody), and the dragger's arms are posed
    //             (DraggerArms).
    // A corpse has no owner moving a real body, so there is no revivee side: the ragdoll is the body, and where it comes to rest is
    // said by whoever let go of it.
    internal static class CorpseDragController
    {
        //====================[ Tuning ]====================
        // Past this the dragger lets go (teleport, a wall snag, or the body being left behind).
        private const float MaxLeashDistance = 7f;
        // Maximum angle away from the body before releasing.
        private const float MaxTurnAwayDegrees = 50f;
        // Wait for the crouch and hands changes before checking the drag stance.
        private const float StanceSettleTimeout = 2f;
        // The game's own line between crouching and standing (Player.TogglePose).
        private const float StandingPoseLevel = 0.5f;
        private const float StartRange = 3f;
        // How often the dragger tells the others which way their body faces (CorpseDragHeadingPacket) (s).
        private const float HeadingSendSeconds = 0.1f;
        // Wait this long for the body to settle before giving up on saying where it rests (it exceeds normal settling time).
        private const float DropRestWaitSeconds = 10f;

        //====================[ Local State ]====================
        // Which corpse the local player is dragging (null when none).
        private static string _draggingId;
        private static float _dragStartTime;
        private static bool _crouchSettled;
        private static bool _handsSettled;
        private static float _nextHeadingSend;
        // The body this client let go of and is to say where it comes to rest, and until when.
        private static string _declareFor;
        private static float _declareUntil;

        public static bool IsLocalDragging => !string.IsNullOrEmpty(_draggingId);

        // The corpse the local player drags, or null.
        public static string LocalDraggingId => _draggingId;

        // The local player is picking a body up: held in place, stance and hands locked, until it is lifted.
        public static bool IsPickupLocked => TryGetLocalSession(out var session) && !session.IsHeld;

        // The local player has lifted the body and still drags it.
        public static bool IsLocalHoldingBody => TryGetLocalSession(out var session) && session.IsHeld;

        private static bool TryGetLocalSession(out DragSession session)
        {
            session = null;
            return IsLocalDragging && DragContext.TryGetSession(_draggingId, out session);
        }

        //====================[ Eligibility ]====================
        // Where the body is: this peer's copy of it in the physics, or its chest bone as it lies.
        public static Vector3 BodyPosition(CorpseRegistry.Entry entry)
        {
            if (entry == null || !entry.Alive) return Vector3.zero;
            if (CorpseBody.TryGetChest(entry.Id, out Vector3 chest)) return chest;
            Transform bone = DragGrip.Chest(entry.Player) ?? PlayerUtils.GetPelvis(entry.Player);
            return bone != null ? bone.position : entry.Corpse.transform.position;
        }

        public static bool IsInRange(Player viewer, CorpseRegistry.Entry entry) =>
            viewer != null && entry != null && (viewer.Position - BodyPosition(entry)).sqrMagnitude <= StartRange * StartRange;

        // The local player may pick this body up now. KeepMeAlive owning the player's input (downed, its own drag, a revive hold,
        // the heal wheel) rules it out.
        public static bool CanStartDrag(Player dragger, CorpseRegistry.Entry entry)
        {
            if (dragger == null || !dragger.IsYourPlayer || !PlayerUtils.IsAlive(dragger)) return false;
            if (IsLocalDragging || FikaBridge.IsHeadless()) return false;
            if (KmaInterop.BusyReason() != null) return false;
            if (!CorpseRegistry.IsReady(entry) || CorpseClaims.IsHeld(entry.Id)) return false;
            // Dragging is done crouched; going prone is one of the ways to let go.
            if (dragger.MovementContext == null || dragger.MovementContext.IsInPronePose) return false;
            return IsInRange(dragger, entry);
        }

        //====================[ Dragger Actions ]====================
        public static void StartDrag(GamePlayerOwner owner, CorpseRegistry.Entry entry)
        {
            var dragger = owner?.Player;
            if (!CanStartDrag(dragger, entry)) return;
            if (!CorpseClaims.Apply(entry.Id, dragger.ProfileId, true)) return;

            _draggingId = entry.Id;
            // Picked up again before it settled: no longer this client's to say where it lies.
            if (_declareFor == entry.Id) _declareFor = null;
            // Now, not on the next tick: the pickup lock holds from this frame.
            DragContext.Begin(dragger, entry.Player);
            FikaBridge.SendDragState(entry.Id, dragger.ProfileId, true);
            EnterDragStance(dragger);
            DragLookLimit.Begin(dragger);

            VFX_UI.Text(Color.cyan, DragSettings.INTERACT_DROPS.Value
                ? PlayerFacingMessages.Interaction.DragHintInteract(DragHud.InteractKeyName())
                : PlayerFacingMessages.Interaction.DragHint);
            DragLog.LogDebug($"[Drag] {dragger.ProfileId} started dragging {entry.Id}");
        }

        // Dragger-initiated release (Interact, stance, distance, KeepMeAlive taking over). Broadcasts to everyone; notify tells the
        // dragger.
        public static void StopDrag(Player dragger, string reason, bool notify = false)
        {
            if (!IsLocalDragging || dragger == null) return;

            string corpseId = _draggingId;
            CorpseRegistry.TryGet(corpseId, out var entry);
            HandTrace.Log($"drag {dragger.ProfileId}: let go of {corpseId}: {reason} | {ReleaseSnapshot(dragger, entry)}");
            CorpseClaims.Apply(corpseId, dragger.ProfileId, false);
            Release(corpseId, dragger.ProfileId);

            EndLocalDraggerSession();
            DragContext.End(corpseId);
            DragLog.LogDebug($"[Drag] released {corpseId} ({reason})");
            if (notify) VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.DragReleased);
        }

        // KeepMeAlive took the local player's input (its interop release: downed, its own drag, a revive hold, the heal wheel, an
        // extract, the raid boundary). Must not throw.
        public static void StopLocal(string reason)
        {
            if (!IsLocalDragging) return;
            var player = PlayerUtils.GetYourPlayer();
            if (player != null) StopDrag(player, "kma:" + reason);
            else EndLocalDraggerSession();
        }

        // The peer that releases a body reports where it settles: the release names that peer, it watches its own copy settle,
        // then sends the rest (CorpseRestPacket). Both packets come from the same sender, so the rest follows the release. Unlike
        // KeepMeAlive's (a lifted body only), any body this peer has in its physics: the ragdoll is the corpse, and the pickup moves
        // it before the lift (the turn over, the shoulders rising to the hands), so every peer's copy has moved.
        private static void Release(string corpseId, string draggerId)
        {
            var local = PlayerUtils.GetYourPlayer();
            string declarer = local != null && CorpseBody.IsTaken(corpseId) ? local.ProfileId : string.Empty;
            Declare(corpseId, draggerId, declarer);
            FikaBridge.SendDragState(corpseId, draggerId, false, declarer);
        }

        private static void Declare(string corpseId, string draggerId, string declarer)
        {
            CorpseBody.ExpectRest(corpseId, draggerId, declarer);
            _declareFor = string.IsNullOrEmpty(declarer) ? null : corpseId;
            _declareUntil = Time.time + DropRestWaitSeconds;
        }

        // This peer's copy of a body has settled (CorpseBody): if this client let go of it, it says where it lies. True when it did.
        public static bool OnBodySettled(string corpseId, Vector3 root, TransformSync[] bones)
        {
            if (_declareFor == null || _declareFor != corpseId) return false;
            _declareFor = null;
            var local = PlayerUtils.GetYourPlayer();
            if (local == null || Time.time > _declareUntil) return false;
            FikaBridge.SendRest(corpseId, local.ProfileId, root, bones);
            return true;
        }

        // A dragger left the raid or died (CorpseClaims.TickLost, every peer). The local dragger's own state ends here (a dead
        // player has no tick of its own); the host, when it has the body in its physics, says where it comes to rest.
        public static void OnDraggerLost(string corpseId, string draggerId)
        {
            if (_draggingId == corpseId && PlayerUtils.IsLocalPlayer(draggerId))
            {
                EndLocalDraggerSession();
                // Only a corpse gone from the scene leaves a live dragger holding nothing.
                if (PlayerUtils.IsAlive(PlayerUtils.GetYourPlayer())) VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.DragReleased);
            }
            DragContext.End(corpseId);

            var local = PlayerUtils.GetYourPlayer();
            if (!FikaBridge.IAmHost() || local == null || !CorpseBody.IsTaken(corpseId)) return;
            Declare(corpseId, draggerId, local.ProfileId);
            FikaBridge.SendDragState(corpseId, draggerId, false, local.ProfileId);
        }

        // Crouch and stow the weapon. Once the body is lifted, undoing either lets go.
        private static void EnterDragStance(Player dragger)
        {
            var mc = dragger.MovementContext;
            if (mc.PoseLevel > 0f) dragger.CurrentManagedState.ChangePose(-mc.PoseLevel);
            if (!PlayerUtils.IsEmptyHanded(dragger)) dragger.SetEmptyHands(null);

            _dragStartTime = Time.time;
            _crouchSettled = false;
            _handsSettled = false;
        }

        // The weapon in hand is the one in the holster slot.
        private static bool HoldsSidearm(Player player)
        {
            var weapon = player.HandsController?.Item as Weapon;
            if (weapon == null) return false;
            var holster = player.Equipment?.GetSlot(EquipmentSlot.Holster);
            return holster != null && holster.ContainedItem == weapon;
        }

        // Returns the release reason when the drag stance is broken.
        private static string BrokenStance(Player dragger, CorpseRegistry.Entry entry)
        {
            var mc = dragger.MovementContext;
            bool settling = Time.time - _dragStartTime < StanceSettleTimeout;

            // Stood up (sprinting does that too) or went prone.
            bool crouched = !mc.IsInPronePose && mc.PoseLevel < StandingPoseLevel;
            if (!_crouchSettled) _crouchSettled = crouched || !settling;
            if (_crouchSettled && !crouched) return "stance";

            // Took anything but the sidearm into their hands (it needs only the right; the left keeps the body): weapon, grenade,
            // knife, meds.
            bool empty = PlayerUtils.IsEmptyHanded(dragger) || HoldsSidearm(dragger);
            if (!_handsSettled) _handsSettled = empty || !settling;
            if (_handsSettled && !empty) return "hands";

            Vector3 toBody = BodyPosition(entry) - dragger.Position;
            toBody.y = 0f;
            if (toBody.sqrMagnitude > 0.25f)
            {
                float bearing = global::KMABodyDrag.Animation.Shared.MathUtil.YawOf(toBody);
                float off = Mathf.Abs(Mathf.DeltaAngle(mc.Rotation.x, bearing));
                if (Mathf.Min(off, 180f - off) > MaxTurnAwayDegrees) return "turned";
            }

            return null;
        }

        // Every input the release checks read, as they are now, for the trace: so a let-go can be told from the log.
        private static string ReleaseSnapshot(Player dragger, CorpseRegistry.Entry entry)
        {
            if (dragger == null || entry == null) return "dragger or body missing";
            var mc = dragger.MovementContext;
            Vector3 toBody = BodyPosition(entry) - dragger.Position;
            float flat = new Vector2(toBody.x, toBody.z).magnitude;
            float bearing = global::KMABodyDrag.Animation.Shared.MathUtil.YawOf(new Vector3(toBody.x, 0f, toBody.z));
            float off = Mathf.Abs(Mathf.DeltaAngle(mc.Rotation.x, bearing));
            bool held = TryGetLocalSession(out var session) && session.IsHeld;
            return $"stage {(session != null ? session.Current.ToString() : "none")} held={held} for {Time.time - _dragStartTime:F2}s | " +
                $"dragger {HandTrace.V(dragger.Position)} look {mc.Rotation.x:F0} pose {mc.PoseLevel:F2} prone={mc.IsInPronePose} " +
                $"emptyHands={PlayerUtils.IsEmptyHanded(dragger)} sidearm={HoldsSidearm(dragger)} | " +
                $"body {HandTrace.Cm(flat)} away bearing {bearing:F0} off look {off:F0} (limit {MaxTurnAwayDegrees:F0}, counts {Mathf.Min(off, 180f - off):F0}) | " +
                $"rolled={CorpseBody.HasRolled(entry.Id)}";
        }

        // Local cleanup only; the shared state is cleared by the caller.
        private static void EndLocalDraggerSession()
        {
            _draggingId = null;
            DragLookLimit.Restore();
        }

        // Hard reset for raid boundaries; nothing is broadcast because the session is gone. The drags themselves (and the bodies
        // and arms) are cleared by BodyAnimation.ClearAll.
        public static void Clear()
        {
            _declareFor = null;
            EndLocalDraggerSession();
        }

        //====================[ Per-Player Tick ]====================
        // Runs for every human player from Player.UpdateTick (DragTickPatch): the drag this player is the dragger of, on this peer.
        // The corpse itself has no tick (its player is unregistered once dead), so it is driven from its dragger's.
        public static void Tick(Player player)
        {
            if (CorpseClaims.TryGetHeldBy(player.ProfileId, out var corpseId) && CorpseRegistry.TryGet(corpseId, out var entry))
            {
                // A headless host never takes the body, but borrows it from Rupture like every peer.
                if (FikaBridge.IsHeadless()) CorpseBody.HoldUntaken(entry);
                else
                {
                    var session = DragContext.Tick(player, entry.Player);
                    CorpseBody.Sync(entry, PlayerUtils.IsAlive(player) ? session : null);
                }
            }
            if (player.IsYourPlayer) TickDragger(player);
        }

        //====================[ Dragger: Release ]====================
        private static void TickDragger(Player dragger)
        {
            if (!IsLocalDragging) return;
            DragLookLimit.Hold();
            // The hold frame faces the way the dragger's body does; only this client knows that yaw (Fika sends the look).
            if (Time.time >= _nextHeadingSend)
            {
                _nextHeadingSend = Time.time + HeadingSendSeconds;
                FikaBridge.SendDragHeading(dragger.ProfileId, dragger.Transform.rotation.eulerAngles.y);
            }

            bool claimed = CorpseClaims.TryGetDragger(_draggingId, out var holder) && holder == dragger.ProfileId;
            if (!claimed || !CorpseRegistry.TryGet(_draggingId, out var entry))
            {
                // Lost the claim (refused by the host, someone else holds it) or the body left the scene.
                string corpseId = _draggingId;
                HandTrace.Log($"drag {dragger.ProfileId}: drag ended for {corpseId}: claim '{holder}'");
                EndLocalDraggerSession();
                DragContext.End(corpseId);
                VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.DragReleased);
                return;
            }

            if (!PlayerUtils.IsAlive(dragger))
            {
                StopDrag(dragger, "dragger-dead");
                return;
            }

            // Rupture would not let this client's copy go (someone else has the body): nothing to hold.
            if (CorpseBody.IsRefused(entry.Id))
            {
                StopDrag(dragger, "refused", true);
                return;
            }

            string reason;
            if ((dragger.Position - BodyPosition(entry)).sqrMagnitude > MaxLeashDistance * MaxLeashDistance) reason = "leash";
            // The pickup locks the stance and hands; the rest is checked once the body is lifted.
            else if (!IsLocalHoldingBody) return;
            // The body rolled out of the dragger's hands, as their own copy of it shows.
            else if (CorpseBody.HasRolled(entry.Id)) reason = "rolled";
            else reason = BrokenStance(dragger, entry);
            if (reason == null) return;

            // Standing, drawing or turning away is the dragger's own doing (the drag hint says so); only a drop they didn't choose
            // is told.
            bool notify = reason == "leash" || reason == "rolled";
            StopDrag(dragger, reason, notify);
        }
    }
}
