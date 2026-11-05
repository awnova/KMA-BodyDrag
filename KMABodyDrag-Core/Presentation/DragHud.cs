//====================[ Imports ]====================
using System;
using System.Linq;
using BepInEx.Bootstrap;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KMABodyDrag.Presentation
{
    //====================[ DragHud ]====================
    // The mod's own overlay canvas, in Tarkov's Bender font, cut down from KeepMeAlive's HUD (as KMA-Wheel's WheelHud): its
    // bottom strip, worded and laid out as KeepMeAlive's look hint on a downed teammate, and its hold bar above it. Looking at a
    // corpse: "[ F ] Loot   Hold [ F ] Drag" (or "Double", and "Shift + F" with a Drag Modifier); holding one with Interact Drops
    // Body on: "[ F ] Drop   [ Tab ] Loot" (the inventory key loots the held body).
    // While Interact is held to pick a body up: the hold bar, "Grabbing body 0.6", filling as KeepMeAlive's revive hold does.
    // Built the first time a hint shows, never at raid start.
    internal static class DragHud
    {
        //====================[ Constants ]====================
        // The game's UI font. Bender's bold, italic and bold-italic SDF assets are marked "DON'T USE!", so bold is a style on this
        // one. There is no fallback font: without it the strip stays hidden.
        private const string FontAssetName = "Jovanny Lemonad - Bender Normal SDF";

        private const float ReferenceWidth = 1920f;
        private const float ReferenceHeight = 1080f;

        // Sizes follow KeepMeAlive's HUD, in em of this font size.
        private const float FontPx = 22f;
        private const float StripFontScale = 0.92f;
        private const float BarHeightEm = 2.7f;
        private const float BarPadEm = 2.2f;
        private const float EdgeEm = 0.22f;
        private const float LetterSpacing = 10f; // TMP units; 10 = 0.1 em
        private const float MinHoldWidthEm = 22f;

        // Distances from the screen's bottom edge, in reference pixels: where KeepMeAlive's strip and hold bar are.
        private const float StripBottomPx = 49f;
        private const float HoldBottomPx = 135f;

        private const string RootName = "KMABodyDrag_Hud";
        private const string FikaHeadlessGuid = "com.fika.headless";

        // KeepMeAlive's palette.
        private static readonly Color BarColor = new Color(13f / 255f, 15f / 255f, 17f / 255f, 0.88f);
        private const string GoldTag = "#E5B358";
        private const string TextTag = "#E1DFD6";
        private const string GreyTag = "#9CA3AF";
        // The hold bar's colour: KeepMeAlive's green (#38E07B, its heal hold and revive states).
        private static readonly Color HoldColor = new Color(0x38 / 255f, 0xE0 / 255f, 0x7B / 255f, 1f);
        private const string HoldTag = "#38E07B";

        //====================[ State ]====================
        private static GameObject _root;
        private static Bar _strip;
        private static Bar _hold;
        private static TMP_FontAsset _font;
        private static bool _fontFailLogged;

        // What the strip shows (all false: hidden). heldLoot: "[ Tab ] Loot" for the body in the dragger's hands.
        private static bool _loot, _drag, _drop, _heldLoot;

        // The hold bar: shown, what it says, how full (0..1), and seconds left.
        private static bool _holdShown;
        private static string _holdLabel;
        private static float _holdFill, _holdRemaining;

        // Last rendered text keys, so a bar is only rebuilt when something visible changes.
        private static string _stripKey, _holdKey;
        private static int _holdTick = -1;

        //====================[ Public API ]====================
        internal static void SetHint(bool loot, bool drag, bool drop, bool heldLoot = false)
        {
            _loot = loot;
            _drag = drag;
            _drop = drop;
            _heldLoot = heldLoot;
        }

        internal static void ClearHint() => SetHint(false, false, false);

        private static bool HasHint => _loot || _drag || _drop || _heldLoot;

        // The hold bar, set each frame of a hold (CorpseInteraction): its label ("Grabbing body"), fill01 of the
        // hold done, remaining seconds left.
        internal static void ShowHold(string label, float fill01, float remaining)
        {
            _holdShown = true;
            _holdLabel = label;
            _holdFill = Mathf.Clamp01(fill01);
            _holdRemaining = Mathf.Max(0f, remaining);
        }

        internal static void HideHold() => _holdShown = false;

        // Forces the bars to rebuild their text on the next update (the Language setting changed: same state, new words).
        internal static void RefreshText()
        {
            _stripKey = null;
            _holdKey = null;
        }

        // The key the game has bound to interact.
        internal static string InteractKeyName() =>
            global::Comfort.Common.Singleton<global::EFT.Settings.SettingsManager>.Instance?.Control.Settings
                .GetKeyName(global::EFT.InputSystem.EGameKey.Interact) ?? "F";

        // The key the game has bound to the inventory (as KeepMeAlive's Hud.InventoryKeyName).
        internal static string InventoryKeyName() =>
            global::Comfort.Common.Singleton<global::EFT.Settings.SettingsManager>.Instance?.Control.Settings
                .GetKeyName(global::EFT.InputSystem.EGameKey.Inventory) ?? "Tab";

        //====================[ Per-frame ]====================
        internal static void Tick()
        {
            try
            {
                if (_root == null && ((!HasHint && !_holdShown) || !EnsureBuilt())) return;
                if (_strip == null) return; // no font: nothing to draw
                RenderStrip();
                RenderHold();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DragHud] Tick error: {ex.Message}");
            }
        }

        // Esc menu and Settings share CommonUI with EftBattleUIScreen: the strip shows only while BattleUI is the current screen.
        internal static bool IsBattleUiActive()
        {
            var mgr = EFT.UI.Screens.EftScreenManager.Instance;
            return mgr == null || mgr.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.BattleUI);
        }

        private static void RenderStrip()
        {
            if (!HasHint || !IsBattleUiActive())
            {
                _strip.SetActive(false);
                _stripKey = null;
                return;
            }

            _strip.SetActive(true);
            string interactKey = InteractKeyName();
            var mode = DragSettings.DRAG_INPUT.Value;
            string dragKey = DragSettings.HasModifier ? ModifierName(DragSettings.DRAG_MODIFIER.Value) + " + " + interactKey : interactKey;
            string key = $"{_loot}|{_drag}|{_drop}|{_heldLoot}|{interactKey}|{InventoryKeyName()}|{mode}|{dragKey}";
            if (key == _stripKey) return;
            _stripKey = key;

            var sb = new System.Text.StringBuilder(160);
            if (_loot) KeyItem(sb, interactKey, PlayerFacingMessages.HudLabels.Loot);
            if (_loot && _drag) sb.Append("<space=1.8em>");
            if (_drag)
                HintItem(sb, mode == DragInput.Hold ? PlayerFacingMessages.HudLabels.Hold : PlayerFacingMessages.HudLabels.Double, dragKey,
                    PlayerFacingMessages.HudLabels.Drag);
            if (_drop)
            {
                if (_loot || _drag) sb.Append("<space=1.8em>");
                KeyItem(sb, interactKey, PlayerFacingMessages.HudLabels.Drop);
            }
            if (_heldLoot)
            {
                if (_loot || _drag || _drop) sb.Append("<space=1.8em>");
                KeyItem(sb, InventoryKeyName(), PlayerFacingMessages.HudLabels.Loot);
            }
            _strip.SetText(sb.ToString(), true);
        }

        // KeepMeAlive's hold bar: "<green, bold>Grabbing body</green>  0.6", the background tinted and the edge filled up to how far
        // the hold has got.
        private static void RenderHold()
        {
            if (!_holdShown || !IsBattleUiActive())
            {
                _hold.SetActive(false);
                _holdKey = null;
                _holdTick = -1;
                return;
            }

            _hold.SetActive(true);
            int tick = Mathf.CeilToInt(_holdRemaining * 10f);
            string key = _holdLabel;
            if (key != _holdKey || tick != _holdTick)
            {
                bool layoutChanged = key != _holdKey;
                _holdKey = key;
                _holdTick = tick;
                var sb = new System.Text.StringBuilder(120);
                sb.Append("<color=").Append(HoldTag).Append("><b>").Append(_holdLabel).Append("</b></color>");
                sb.Append("  <color=").Append(TextTag).Append("><mspace=0.56em>").Append(tick / 10).Append('.').Append(tick % 10)
                  .Append("</mspace></color>");
                _hold.SetText(sb.ToString(), layoutChanged);
            }
            _hold.SetEdge(HoldColor, _holdFill);
        }

        //====================[ Text helpers ]====================
        // The Drag Modifier on the hint ("Shift + F"): either side's key counts, so no side is named.
        private static string ModifierName(DragModifier modifier) => modifier switch
        {
            DragModifier.Shift => "Shift",
            DragModifier.Alt => "Alt",
            _ => "Ctrl"
        };

        // "<grey, small>how</grey> [ key ] label", as KeepMeAlive's "Tap [ F ] Drag".
        private static void HintItem(System.Text.StringBuilder sb, string how, string keyText, string label)
        {
            sb.Append("<color=").Append(GreyTag).Append("><size=80%>").Append(how).Append("</size></color> ");
            KeyItem(sb, keyText, label);
        }

        private static void KeyItem(System.Text.StringBuilder sb, string keyText, string label)
        {
            sb.Append("<color=").Append(GoldTag).Append("><b>[ ").Append(keyText).Append(" ]</b></color> ");
            sb.Append("<color=").Append(TextTag).Append('>').Append(label).Append("</color>");
        }

        //====================[ Build ]====================
        private static bool EnsureBuilt()
        {
            // A Fika headless client draws nothing.
            if (Chainloader.PluginInfos.ContainsKey(FikaHeadlessGuid)) return false;
            if (_root != null) return true;

            try
            {
                if (!MonoBehaviourSingleton<GameUI>.Instantiated) return false;
                var gameUi = MonoBehaviourSingleton<GameUI>.Instance;
                if (gameUi == null) return false;

                _root = new GameObject(RootName, typeof(RectTransform));
                UnityEngine.Object.DontDestroyOnLoad(_root);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrderAbove(gameUi);

                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;

                _strip = _hold = null;
                if (_font == null) _font = ResolveFont();
                if (_font != null)
                {
                    float stripFont = FontPx * StripFontScale;
                    _strip = Bar.Create(_root.transform, "Strip", _font, stripFont, stripFont * BarHeightEm, stripFont * BarPadEm, StripBottomPx, 0f);
                    _hold = Bar.Create(_root.transform, "Hold", _font, FontPx, FontPx * BarHeightEm, FontPx * BarPadEm, HoldBottomPx, FontPx * MinHoldWidthEm);
                    _strip.SetActive(false);
                    _hold.SetActive(false);
                }

                Plugin.LogSource.LogInfo($"[DragHud] built (font '{(_font != null ? _font.name : "none")}', sortingOrder {canvas.sortingOrder})");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DragHud] build failed: {ex}");
                if (_root != null) UnityEngine.Object.Destroy(_root);
                _root = null;
                return false;
            }
        }

        // Just above GameUI's own canvas, so the strip covers the HUD but not menus placed higher.
        private static int SortingOrderAbove(GameUI gameUi)
        {
            var canvas = gameUi.GetComponent<Canvas>() ?? gameUi.GetComponentInParent<Canvas>();
            return canvas != null ? canvas.rootCanvas.sortingOrder + 1 : 0;
        }

        private static TMP_FontAsset ResolveFont()
        {
            var fonts = Resources.LoadAll<TMP_FontAsset>("UI/Fonts");
            var font = fonts.FirstOrDefault(f => f != null && f.name == FontAssetName);
            if (font == null && !_fontFailLogged)
            {
                _fontFailLogged = true;
                Plugin.LogSource.LogError(
                    $"[DragHud] font '{FontAssetName}' not found in Resources/UI/Fonts ({string.Join(", ", fonts.Select(f => f.name))}); the strip stays hidden.");
            }
            return font;
        }

        //====================[ Bar ]====================
        // One flat bar, as KeepMeAlive's: background, rich text, a faint track along its bottom edge, and (the hold bar) an edge
        // over the track and a tint of the background, both filled from the left (SetEdge).
        private sealed class Bar
        {
            private RectTransform _rect;
            private RectTransform _wash;
            private RectTransform _edge;
            private Image _washImage;
            private Image _edgeImage;
            private TextMeshProUGUI _text;
            private float _textLeftPx;
            private float _textRightPx;
            private float _minWidth;

            public static Bar Create(Transform parent, string name, TMP_FontAsset font, float fontPx, float heightPx, float padPx, float bottomPx,
                float minWidthPx)
            {
                float sidePadPx = padPx * 0.5f;
                float edgePx = fontPx * EdgeEm;
                var bar = new Bar { _textLeftPx = sidePadPx, _textRightPx = sidePadPx, _minWidth = minWidthPx };

                var go = new GameObject(name, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(parent, false);
                bar._rect = go.GetComponent<RectTransform>();
                var bg = go.GetComponent<Image>();
                bg.color = BarColor;
                bg.raycastTarget = false;

                var anchor = new Vector2(0.5f, 0f);
                bar._rect.anchorMin = bar._rect.anchorMax = bar._rect.pivot = anchor;
                bar._rect.anchoredPosition = new Vector2(0f, bottomPx);
                bar._rect.sizeDelta = new Vector2(minWidthPx, heightPx);

                // Tinted background up to the edge, then the faint track, then the edge (both empty until SetEdge).
                bar._washImage = NewImage(go.transform, "Wash", out bar._wash);
                bar._wash.anchorMin = Vector2.zero;
                bar._wash.anchorMax = new Vector2(0f, 1f);
                bar._wash.offsetMin = bar._wash.offsetMax = Vector2.zero;
                bar._washImage.enabled = false;

                var track = NewImage(go.transform, "Track", out var trackRect);
                track.color = new Color(1f, 1f, 1f, 0.08f);
                trackRect.anchorMin = Vector2.zero;
                trackRect.anchorMax = new Vector2(1f, 0f);
                trackRect.offsetMin = Vector2.zero;
                trackRect.offsetMax = new Vector2(0f, edgePx);

                bar._edgeImage = NewImage(go.transform, "Edge", out bar._edge);
                bar._edge.anchorMin = Vector2.zero;
                bar._edge.anchorMax = Vector2.zero;
                bar._edge.offsetMin = Vector2.zero;
                bar._edge.offsetMax = new Vector2(0f, edgePx);
                bar._edgeImage.enabled = false;

                var textGo = new GameObject("Text", typeof(RectTransform));
                textGo.transform.SetParent(go.transform, false);
                var textRect = textGo.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(bar._textLeftPx, 0f);
                textRect.offsetMax = new Vector2(-bar._textRightPx, 0f);

                bar._text = textGo.AddComponent<TextMeshProUGUI>();
                bar._text.font = font;
                bar._text.fontSize = fontPx;
                bar._text.fontStyle = FontStyles.UpperCase;
                bar._text.characterSpacing = LetterSpacing;
                bar._text.alignment = TextAlignmentOptions.Center;
                bar._text.enableWordWrapping = false;
                bar._text.overflowMode = TextOverflowModes.Overflow;
                bar._text.richText = true;
                bar._text.raycastTarget = false;
                return bar;
            }

            public void SetActive(bool active)
            {
                if (_rect.gameObject.activeSelf != active) _rect.gameObject.SetActive(active);
            }

            public void SetText(string rich, bool resize)
            {
                _text.text = rich;
                if (!resize) return;

                _text.ForceMeshUpdate(true);
                float width = Mathf.Max(_minWidth, _text.preferredWidth + _textLeftPx + _textRightPx);
                _rect.sizeDelta = new Vector2(width, _rect.sizeDelta.y);
            }

            // The edge and the background tint, filled from the left to fill01 (KeepMeAlive's hold bar).
            public void SetEdge(Color color, float fill01)
            {
                _edgeImage.enabled = true;
                _edgeImage.color = color;
                _edge.anchorMax = new Vector2(fill01, 0f);

                _washImage.enabled = true;
                var wash = color;
                wash.a = 0.22f;
                _washImage.color = wash;
                _wash.anchorMax = new Vector2(fill01, 1f);
            }

            private static Image NewImage(Transform parent, string name, out RectTransform rect)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(parent, false);
                rect = go.GetComponent<RectTransform>();
                var image = go.GetComponent<Image>();
                image.raycastTarget = false;
                return image;
            }
        }
    }

    //====================[ BodyDragDriver ]====================
    // Ticks everything that is not a player's own tick: the drag press and the look hint, claims whose dragger is gone, bodies
    // let go, and the strip. Made at the first raid start (RaidLifecycle), not in Plugin.Awake: an object made before the first
    // scene never gets its Update (KeepMeAlive and KMA-Wheel hang theirs on the HUD canvas for that reason).
    internal sealed class BodyDragDriver : MonoBehaviour
    {
        private static BodyDragDriver _instance;

        public static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("KMABodyDrag_Driver");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<BodyDragDriver>();
        }

        private void Update()
        {
            Step("interaction", CorpseInteraction.Tick);
            Step("claims", CorpseClaims.TickLost);
            Step("bodies", CorpseBody.TickFree);
            Step("leases", CorpseBody.TickLeases);
            DragHud.Tick();
        }

        private static void Step(string what, Action tick)
        {
            try { tick(); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[Driver] {what} error: {ex}"); }
        }
    }
}
