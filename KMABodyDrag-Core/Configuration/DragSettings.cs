//====================[ Imports ]====================
using BepInEx.Configuration;
using UnityEngine;

namespace KMABodyDrag.Configuration
{
    //====================[ DragInput ]====================
    // How Interact (with Drag Modifier, if one is set) picks a corpse up (DragSettings.DRAG_INPUT).
    internal enum DragInput
    {
        // Hold it for Hold Time.
        Hold,
        // Press it twice inside the Interact key's double-click timeout.
        DoubleTap
    }

    //====================[ DragModifier ]====================
    // The key held with Interact to drag (DragSettings.DRAG_MODIFIER). Either side's key counts.
    internal enum DragModifier
    {
        None,
        Shift,
        Alt,
        Control
    }

    //====================[ DragSettings ]====================
    // The mod's only settings, all local. Everything about the drag itself is KeepMeAlive's tuning, kept as constants.
    internal static class DragSettings
    {
        //====================[ Local-Only Settings ]====================
        // Controls
        public static ConfigEntry<DragInput> DRAG_INPUT;
        public static ConfigEntry<DragModifier> DRAG_MODIFIER;
        public static ConfigEntry<float> HOLD_SECONDS;
        public static ConfigEntry<bool> INTERACT_DROPS;

        // Language
        public static ConfigEntry<string> LANGUAGE;

        //====================[ Modifier ]====================
        // A Drag Modifier is set: the drag is on it with Interact, and Interact alone is the game's own.
        public static bool HasModifier => DRAG_MODIFIER.Value != DragModifier.None;

        // The Drag Modifier is held (false with none set).
        public static bool ModifierHeld() => DRAG_MODIFIER.Value switch
        {
            DragModifier.Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift),
            DragModifier.Alt => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt),
            DragModifier.Control => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl),
            _ => false
        };

        //====================[ Init ]====================
        public static void Init(ConfigFile config)
        {
            //====================[ Controls ]====================
            DRAG_INPUT = config.Bind(
                "1. Controls",
                "Drag Input",
                DragInput.Hold,
                new ConfigDescription(
                    "How Interact picks a body up. Hold: hold Interact for Hold Time. DoubleTap: press Interact twice. Without a Drag Modifier, a tap or single press of Interact still loots.",
                    null,
                    new ConfigurationManagerAttributes { Order = 4 })
            );

            DRAG_MODIFIER = config.Bind(
                "1. Controls",
                "Drag Modifier",
                DragModifier.None,
                new ConfigDescription(
                    "A key held with Interact to drag (either side's key counts). Set: Interact alone works exactly as in the base game, and the modifier + Interact drags. None: Interact itself drags and loots (Drag Input tells them apart).",
                    null,
                    new ConfigurationManagerAttributes { Order = 3 })
            );

            HOLD_SECONDS = config.Bind(
                "1. Controls",
                "Hold Time",
                1f,
                new ConfigDescription(
                    "Drag Input Hold: how long Interact is held to pick a body up, in seconds.",
                    new AcceptableValueRange<float>(0.4f, 3f),
                    new ConfigurationManagerAttributes { Order = 2 })
            );

            INTERACT_DROPS = config.Bind(
                "1. Controls",
                "Interact Drops Body",
                true,
                new ConfigDescription(
                    "Pressing Interact while holding a body also puts it down. Standing up, going prone, drawing a weapon or turning away let go of it either way.",
                    null,
                    new ConfigurationManagerAttributes { Order = 1 })
            );

            //====================[ Language ]====================
            // The choices are the language files found at startup; a saved choice whose file is gone resets to Auto.
            LANGUAGE = config.Bind(
                "2. Language",
                "Language",
                Localization.Auto,
                new ConfigDescription("Language of KMA Body Drag's on-screen text. Auto follows the game's language when a file for it exists, else English. More languages: add <code>.json files to BepInEx/plugins/KMABodyDrag/Localizations and restart.", new AcceptableValueList<string>(Localization.Choices()))
            );
            LANGUAGE.SettingChanged += (_, _) => DragHud.RefreshText();
        }
    }
}
