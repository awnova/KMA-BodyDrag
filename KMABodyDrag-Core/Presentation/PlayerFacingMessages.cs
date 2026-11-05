//====================[ Imports ]====================

namespace KMABodyDrag.Presentation
{
    //====================[ PlayerFacingMessages ]====================
    // Centralized catalog for player-visible text. The wording lives in Resources/Localization/en.json (and its
    // translations); each entry here is read when shown, so a language change applies to the next text drawn.
    internal static class PlayerFacingMessages
    {
        private static string T(string key) => Localization.Text(key);
        private static string F(string key, params object[] args) => Localization.Format(key, args);

        //====================[ HUD ]====================
        // Labels on the look hint (DragHud). Shown upper-case by the bar's font style.
        internal static class HudLabels
        {
            public static string Loot => T("Hud.Loot");
            public static string Drag => T("Hud.Drag");
            public static string Double => T("Hud.Double");
            public static string Hold => T("Hud.Hold");
            // The hold bar while Interact is held to pick a body up.
            public static string Grabbing => T("Hud.Grabbing");
            public static string Drop => T("Hud.Drop");
        }

        //====================[ Interaction ]====================
        internal static class Interaction
        {
            // The interaction menu's entry on a body that has nothing to loot (a zombie's).
            public static string DragAction => T("Interaction.DragAction");
            public static string DragHint => T("Interaction.BodyDragHint");
            public static string DragHintInteract(string key) => F("Interaction.BodyDragHintInteract", key);
            public static string DragReleased => T("Interaction.BodyDragReleased");
        }
    }
}
