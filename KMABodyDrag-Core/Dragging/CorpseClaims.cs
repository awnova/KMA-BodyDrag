//====================[ Imports ]====================
using System.Collections.Generic;

namespace KMABodyDrag.Dragging
{
    //====================[ CorpseClaims ]====================
    // Who holds which corpse, as this peer knows it: KeepMeAlive's RMPlayer.CurrentDraggerId and its ApplyDragState, kept per corpse
    // since a corpse has no player state. Every peer applies the same claims and releases (CorpsePacketHandlers); the host settles
    // claims that crossed.
    internal static class CorpseClaims
    {
        //====================[ State ]====================
        private static readonly Dictionary<string, string> DraggerOf = new Dictionary<string, string>();
        // The other way round: one body per dragger.
        private static readonly Dictionary<string, string> HeldBy = new Dictionary<string, string>();
        private static readonly List<string> Scratch = new List<string>();

        //====================[ Queries ]====================
        public static bool IsHeld(string corpseId) => !string.IsNullOrEmpty(corpseId) && DraggerOf.ContainsKey(corpseId);

        public static bool TryGetDragger(string corpseId, out string draggerId)
        {
            draggerId = null;
            return !string.IsNullOrEmpty(corpseId) && DraggerOf.TryGetValue(corpseId, out draggerId);
        }

        public static bool TryGetHeldBy(string draggerId, out string corpseId)
        {
            corpseId = null;
            return !string.IsNullOrEmpty(draggerId) && HeldBy.TryGetValue(draggerId, out corpseId);
        }

        //====================[ Shared State Mutation ]====================
        // A claim or a release, when the state allows it: a release only by its dragger, and the first claim keeps the body.
        public static bool Apply(string corpseId, string draggerId, bool active)
        {
            if (string.IsNullOrEmpty(corpseId) || string.IsNullOrEmpty(draggerId) || corpseId == draggerId) return false;
            bool held = DraggerOf.TryGetValue(corpseId, out var current);

            if (!active)
            {
                if (current != draggerId) return false;
                Remove(corpseId);
                return true;
            }

            if (held) return current == draggerId;
            // One body per dragger: a claim on another body ends the old one (a release that was lost on the way).
            if (HeldBy.TryGetValue(draggerId, out var other)) Remove(other);
            if (!CorpseRegistry.TryGet(corpseId, out _)) return false;

            DraggerOf[corpseId] = draggerId;
            HeldBy[draggerId] = corpseId;
            OnClaimChanged(corpseId);
            CorpseInteraction.OnClaimed(corpseId);
            return true;
        }

        //====================[ Per Frame ]====================
        // Every peer: a dragger who left the raid or died lets go, and a corpse gone from the scene is held by nobody. Each peer works
        // this out from what it sees, so no packet is needed; a host that has the body in its physics says where it rests.
        public static void TickLost()
        {
            if (DraggerOf.Count == 0) return;
            Scratch.Clear();
            foreach (var pair in DraggerOf)
            {
                bool bodyGone = !CorpseRegistry.TryGet(pair.Key, out _);
                bool draggerGone = !PlayerUtils.IsAlive(PlayerUtils.GetPlayerById(pair.Value));
                if (bodyGone || draggerGone) Scratch.Add(pair.Key);
            }

            // The local dragger too: a dead player's tick no longer runs, so its controller is told here.
            foreach (var corpseId in Scratch)
            {
                string draggerId = DraggerOf[corpseId];
                Remove(corpseId);
                DragLog.LogDebug($"[Drag] {draggerId} is gone: {corpseId} is let go");
                CorpseDragController.OnDraggerLost(corpseId, draggerId);
            }
        }

        // Raid boundary.
        public static void Clear()
        {
            DraggerOf.Clear();
            HeldBy.Clear();
        }

        private static void Remove(string corpseId)
        {
            if (DraggerOf.TryGetValue(corpseId, out var draggerId)) HeldBy.Remove(draggerId);
            DraggerOf.Remove(corpseId);
            OnClaimChanged(corpseId);
        }

        // Whoever looks at the body gets its menu rebuilt (Player compares the interactable's StateUpdateTime each cast): Search
        // goes while it is held and comes back once it is let go.
        private static void OnClaimChanged(string corpseId)
        {
            if (CorpseRegistry.TryGet(corpseId, out var entry)) entry.Corpse.SetStateUpdateTime();
        }
    }
}
