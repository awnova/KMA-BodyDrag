//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT.InputSystem;
using UnityEngine;

namespace KMABodyDrag.Interaction
{
    //====================[ InteractBinding ]====================
    // The Interact key as EFT has it bound, from EFT's live key combinations (InputPreset._workingKeyCombinations, as KMA-Wheel's
    // WheelKeyBinding reads the reload key's): its Double Click Timeout (the double press window, and the tap window of a hold)
    // and its keys (whether it is still held, for the hold). Every combination on Interact counts (a main and an alternate
    // binding; a modifier and a key). Read when EFT applies its bindings (InteractBindingPatch: start-up and the Controls
    // settings).
    internal static class InteractBinding
    {
        // EFT's Double Click Timeout, until the bindings have been read.
        private const float DefaultSeconds = 0.3f;

        public static float DoublePressSeconds { get; private set; } = DefaultSeconds;

        // The keys of each Interact combination. F until the bindings have been read.
        private static KeyCode[][] _combos = { new[] { KeyCode.F } };

        // The Interact key is down: every key of one of its combinations.
        public static bool IsHeld()
        {
            foreach (var keys in _combos)
            {
                bool all = keys.Length > 0;
                for (int i = 0; all && i < keys.Length; i++) all = Input.GetKey(keys[i]);
                if (all) return true;
            }
            return false;
        }

        // The Interact key went down this frame: one of its combinations is held, and one of that combination's keys went down
        // this frame.
        public static bool WentDown()
        {
            foreach (var keys in _combos)
            {
                bool all = keys.Length > 0, down = false;
                for (int i = 0; all && i < keys.Length; i++)
                {
                    all = Input.GetKey(keys[i]);
                    down |= Input.GetKeyDown(keys[i]);
                }
                if (all && down) return true;
            }
            return false;
        }

        internal static void Rebuild(InputPreset preset)
        {
            try
            {
                var combos = preset?._workingKeyCombinations;
                if (combos == null) return;
                var found = new List<KeyCode[]>();
                foreach (var entry in combos)
                {
                    if (entry is not InputKeyCombination combo || combo.GameKey != EGameKey.Interact) continue;
                    if (combo.DoubleClickTimeout > 0f) DoublePressSeconds = combo.DoubleClickTimeout;
                    var keys = new List<KeyCode>();
                    if (combo.InputKeys != null)
                        foreach (var gameKey in combo.InputKeys)
                            if (gameKey is InputKey key && key.Key != KeyCode.None) keys.Add(key.Key);
                    if (keys.Count > 0) found.Add(keys.ToArray());
                }
                if (found.Count > 0) _combos = found.ToArray();
                var names = new List<string>();
                foreach (var keys in _combos) names.Add(string.Join("+", keys));
                DragLog.LogDebug($"[InteractBinding] double press window {DoublePressSeconds:F2}s, keys [{string.Join(", ", names)}]");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[InteractBinding] could not read the Interact binding, keeping {DoublePressSeconds:F2}s: {ex.Message}");
            }
        }
    }
}
