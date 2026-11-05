//====================[ Imports ]====================
using System;
using EFT;
using EFT.Interactive;
using EFT.UI;
using UnityEngine;

namespace KMABodyDrag.Interaction
{
    //====================[ CorpseInteraction ]====================
    // Interact on a corpse drags by Drag Input (DragSettings.DRAG_INPUT): Hold (held for Hold Time, with KeepMeAlive's hold bar,
    // DragHud) or DoubleTap (two presses inside the Interact key's double-click timeout, InteractBinding).
    // No Drag Modifier: Interact itself loots and drags. The game's own "Search" entry stays in the menu; its action is wrapped
    // (CorpseActionsPatch), so a press only starts a window as long as the double-click timeout. DoubleTap: a second press on
    // the same body inside it drags; the window running out loots, as the press would have. Hold: the key let go inside it
    // loots; still held, the hold bar shows and the body is picked up when the hold completes (let go before that, nothing
    // happens). A body that can't be dragged now (still falling, held, KeepMeAlive busy...) is looted at once, without the wait.
    // A Drag Modifier (DragSettings.DRAG_MODIFIER): Interact alone is the game's own, looting at once; Interact pressed with the
    // modifier held is polled here (TickModifier) and drags by the same Drag Input, without a tap window (a hold's bar shows at
    // once), and Search does nothing for that press.
    // A zombie has nothing to loot: it gets a "Drag" entry of its own, and Interact on it drags (with or without the modifier).
    // Holding a body: Interact puts it down (Interact Drops Body, DraggerInteract), and the inventory key runs the game's own
    // Search on it with the body still in hand (TryLootHeldBody).
    // The look hint (DragHud) says which.
    internal static class CorpseInteraction
    {
        //====================[ Tuning ]====================
        // How often the look hint is re-evaluated (it looks at the corpse and the player's state).
        private const float HintRefreshSeconds = 0.25f;
        // The game's Search action name (InteractionContextHelper.GetAvailableInteractionState).
        private const string SearchAction = "Search";

        //====================[ State ]====================
        // A first press waiting for a second (DoubleTap) or held (Hold): on which body, since and until when, and the loot it
        // does if it turns out to be a single tap.
        private static string _pendingId;
        private static float _pendingSince;
        private static float _pendingUntil;
        private static bool _pendingHold;
        // Pressed with the Drag Modifier: held means both are held, and there is no tap to loot.
        private static bool _pendingModifier;
        private static Action _pendingSearch;
        private static GamePlayerOwner _pendingOwner;
        private static CorpseRegistry.Entry _pendingEntry;
        // The corpse this client last opened to loot, so a claim on it closes the loot screen, and when (the screen takes a moment
        // to open after the search starts: the game first checks the container).
        private static string _lootingId;
        private static float _lootingSince;
        private const float LootScreenGraceSeconds = 2f;
        private static float _nextHint;
        // While building a menu to run the game's own Search on the held body: Decorate leaves it as the game made it.
        private static bool _rawMenu;

        //====================[ Menu ]====================
        // Every build of a corpse's interaction menu (CorpseActionsPatch).
        public static void Decorate(GamePlayerOwner owner, CorpseRegistry.Entry entry, ref AvailableInteractionState state)
        {
            if (_rawMenu || owner?.Player == null || entry == null) return;

            // A body in someone's hands has nothing to loot: nobody searches a body being dragged.
            if (CorpseClaims.IsHeld(entry.Id))
            {
                state?.Actions.RemoveAll(a => a.Name == SearchAction);
                return;
            }

            var search = state?.Actions.Find(a => a.Name == SearchAction);
            if (search != null)
            {
                var vanilla = search.Action;
                string id = entry.Id;
                Action loot = () =>
                {
                    _lootingId = id;
                    _lootingSince = Time.time;
                    vanilla?.Invoke();
                };
                // With a Drag Modifier, Interact alone loots at once, as in the game; with the modifier held the press is a drag
                // press (TickModifier), and looting would open the loot screen over it.
                if (DragSettings.HasModifier) search.Action = () => { if (!DragSettings.ModifierHeld()) loot(); };
                else search.Action = () => OnPress(owner, entry, loot, false);
                return;
            }

            // Nothing to search (a zombie's corpse): an entry of its own. With the modifier held, TickModifier has the press.
            if (!CorpseRegistry.IsReady(entry)) return;
            state ??= new AvailableInteractionState();
            state.Actions.Add(new InteractionAction
            {
                Name = PlayerFacingMessages.Interaction.DragAction,
                Action = () => { if (!DragSettings.ModifierHeld()) OnPress(owner, entry, null, false); }
            });
        }

        // A press of Interact on a corpse: the wrapped menu action (search: the loot a tap does), or Interact with the Drag
        // Modifier held (modifier: no loot, and the modifier must stay held through a hold).
        private static void OnPress(GamePlayerOwner owner, CorpseRegistry.Entry entry, Action search, bool modifier)
        {
            // A menu built before someone picked the body up still has this entry (the game rebuilds a menu only when told to,
            // OnClaimChanged); and a dragger's hands are full: another body is neither picked up nor looted while one is held (the
            // held one is looted with the inventory key, TryLootHeldBody).
            if (CorpseClaims.IsHeld(entry.Id) || CorpseDragController.IsLocalDragging)
            {
                ClearPending();
                return;
            }

            var mode = DragSettings.DRAG_INPUT.Value;

            // A press during a hold (a second Interact binding) changes nothing.
            if (_pendingHold && _pendingId == entry.Id) return;

            if (mode == DragInput.DoubleTap && _pendingId == entry.Id && Time.time <= _pendingUntil)
            {
                ClearPending();
                CorpseDragController.StartDrag(owner, entry);
                return;
            }

            ClearPending();
            if (!CorpseDragController.CanStartDrag(owner.Player, entry))
            {
                search?.Invoke();
                return;
            }

            _pendingId = entry.Id;
            _pendingSince = Time.time;
            _pendingUntil = Time.time + InteractBinding.DoublePressSeconds;
            _pendingHold = mode == DragInput.Hold;
            _pendingModifier = modifier;
            _pendingSearch = search;
            _pendingOwner = owner;
            _pendingEntry = entry;
        }

        // Interact pressed with the Drag Modifier held, each frame: while looking at a corpse, in the battle UI, it is a drag
        // press. Polled rather than taken from the menu action, which the game may not run with a modifier held.
        private static void TickModifier()
        {
            if (!DragSettings.HasModifier || !DragSettings.ModifierHeld() || !InteractBinding.WentDown() || !DragHud.IsBattleUiActive())
                return;

            var player = PlayerUtils.GetYourPlayer();
            if (player == null || !PlayerUtils.IsAlive(player) || KmaInterop.BusyReason() != null) return;
            if (player.InteractableObject is not Corpse corpse || !CorpseRegistry.TryGet(corpse, out var entry)) return;
            var owner = player.GetComponent<GamePlayerOwner>();
            if (owner == null) return;
            OnPress(owner, entry, null, true);
        }

        // The tap window of a hold: without the modifier a tap there loots; with it there is nothing else, so the hold starts at once.
        private static float TapWindow => _pendingModifier ? 0f : InteractBinding.DoublePressSeconds;

        // Hold Time, never shorter than the tap window (a hold must outlast a tap).
        private static float HoldSeconds => Mathf.Max(DragSettings.HOLD_SECONDS.Value, TapWindow + 0.1f);

        // A pending hold, each frame. Let go inside the tap window: it loots. Let go after it, looked away, or the body is gone or
        // taken: it ends with nothing done. Held for Hold Time: the drag starts.
        private static void TickHold()
        {
            string id = _pendingId;
            var owner = _pendingOwner;
            var entry = _pendingEntry;
            var player = owner?.Player;
            float held = Time.time - _pendingSince;

            // Looked away, or the body can no longer be picked up (taken, out of reach, gone prone, KeepMeAlive took over...).
            if (!LookedAt(player, id) || !CorpseDragController.CanStartDrag(player, entry))
            {
                ClearPending();
                return;
            }

            float tapWindow = TapWindow;
            bool down = InteractBinding.IsHeld() && (!_pendingModifier || DragSettings.ModifierHeld());
            if (!down)
            {
                var search = _pendingSearch;
                ClearPending();
                if (held <= tapWindow) search?.Invoke();
                return;
            }

            float total = HoldSeconds;
            if (held >= total)
            {
                ClearPending();
                CorpseDragController.StartDrag(owner, entry);
                return;
            }

            // Past the tap window it is a hold: the bar shows, filling from empty over the rest of Hold Time (KeepMeAlive's
            // revive hold after its tap window).
            if (held >= tapWindow) DragHud.ShowHold(PlayerFacingMessages.HudLabels.Grabbing, (held - tapWindow) / (total - tapWindow), total - held);
        }

        //====================[ Held Body Loot ]====================
        // The held body has something to loot (a zombie's has nothing).
        private static bool HeldBodyLootable(out CorpseRegistry.Entry entry) =>
            CorpseRegistry.TryGet(CorpseDragController.LocalDraggingId, out entry) && entry.Corpse != null && !entry.Corpse.IsZombieCorpse;

        // The inventory key while holding a body (DraggerInputPatch): the held body's loot opens instead of the inventory alone,
        // and the body stays in hand. True when it opened (the key is then consumed); false leaves the key to the game.
        public static bool TryLootHeldBody(Player player)
        {
            if (player == null || !CorpseDragController.IsLocalHoldingBody || !HeldBodyLootable(out var entry)) return false;
            return LootHeldBody(player, entry);
        }

        // The game's own Search on the body in the dragger's hands: the menu the game builds for it, untouched (the held body's
        // menu has no Search otherwise), and its Search run as a press of it would. The body stays in hand.
        private static bool LootHeldBody(Player player, CorpseRegistry.Entry entry)
        {
            var owner = player.GetComponent<GamePlayerOwner>();
            if (owner == null) return false;

            AvailableInteractionState state;
            _rawMenu = true;
            try
            {
                state = InteractionContextHelper.GetAvailableActions(owner, entry.Corpse);
            }
            finally
            {
                _rawMenu = false;
            }

            var search = state?.Actions.Find(a => a.Name == SearchAction);
            if (search?.Action == null)
            {
                DragLog.LogDebug($"[CorpseInteraction] no Search on held body {entry.Id}");
                return false;
            }
            _lootingId = entry.Id;
            _lootingSince = Time.time;
            search.Action();
            return true;
        }

        //====================[ Per Frame ]====================
        public static void Tick()
        {
            TickModifier();
            if (_pendingId != null && _pendingHold) TickHold();
            else if (_pendingId != null && Time.time > _pendingUntil)
            {
                string id = _pendingId;
                var search = _pendingSearch;
                var player = _pendingOwner?.Player;
                ClearPending();
                // The press loots, as it would have: still alive, still looking at that body, and nobody has picked it up since.
                if (search != null && PlayerUtils.IsAlive(player) && LookedAt(player, id) && !CorpseClaims.IsHeld(id)
                    && !CorpseDragController.IsLocalDragging)
                    search();
            }

            // The loot screen this client opened has closed: a later claim on that body must not close an inventory opened since.
            if (_lootingId != null && Time.time - _lootingSince > LootScreenGraceSeconds && !InventoryScreenOpen()) _lootingId = null;

            if (Time.time < _nextHint) return;
            _nextHint = Time.time + HintRefreshSeconds;
            UpdateHint();
        }

        // The look hint: what Interact does on the corpse the local player looks at, or "Drop" while holding one (when Interact
        // drops it).
        private static void UpdateHint()
        {
            var player = PlayerUtils.GetYourPlayer();
            if (player == null || !PlayerUtils.IsAlive(player) || KmaInterop.BusyReason() != null)
            {
                DragHud.ClearHint();
                return;
            }

            if (CorpseDragController.IsLocalDragging)
            {
                bool holding = CorpseDragController.IsLocalHoldingBody;
                DragHud.SetHint(false, false, holding && DragSettings.INTERACT_DROPS.Value, holding && HeldBodyLootable(out _));
                return;
            }

            if (player.InteractableObject is not Corpse corpse || !CorpseRegistry.TryGet(corpse, out var entry))
            {
                DragHud.ClearHint();
                return;
            }

            bool loot = !corpse.IsZombieCorpse && !CorpseClaims.IsHeld(entry.Id);
            bool drag = CorpseDragController.CanStartDrag(player, entry);
            DragHud.SetHint(loot, drag, false);
        }

        private static bool LookedAt(Player player, string corpseId) =>
            player != null && player.InteractableObject is Corpse corpse && corpse.PlayerProfileID == corpseId;

        // The inventory screen (the loot screen is it) is the current screen, as the game's ActionPanel checks it.
        internal static bool InventoryScreenOpen()
        {
            var screens = EFT.UI.Screens.EftScreenManager.Instance;
            return screens != null && screens.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.Inventory);
        }

        //====================[ Claims ]====================
        // A claim arrived on the corpse this client has open to loot: the loot screen closes (as KeepMeAlive's closes when a downed
        // teammate it is looting gets up).
        public static void OnClaimed(string corpseId)
        {
            if (_pendingId == corpseId) ClearPending();
            if (_lootingId != corpseId) return;
            _lootingId = null;
            try
            {
                var player = PlayerUtils.GetYourPlayer();
                var owner = player != null ? player.GetComponent<GamePlayerOwner>() : null;
                owner?.CloseInventoryIfOpen();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[CorpseInteraction] CloseInventoryIfOpen failed: {ex.Message}");
            }
        }

        //====================[ Reset ]====================
        private static void ClearPending()
        {
            if (_pendingHold) DragHud.HideHold();
            _pendingId = null;
            _pendingHold = false;
            _pendingModifier = false;
            _pendingSearch = null;
            _pendingOwner = null;
            _pendingEntry = null;
        }

        // Raid boundary.
        public static void Clear()
        {
            ClearPending();
            _lootingId = null;
            DragHud.ClearHint();
        }
    }
}
