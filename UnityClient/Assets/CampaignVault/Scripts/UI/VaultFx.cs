using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Small, asset-free motion for the RPG feel: entrances, typewriter
    /// narration, tumbling dice, hover glow, filling health bars, and a dim
    /// ember drift behind the page. Rules every effect follows so it can never
    /// fight uGUI layout: animate only scale, alpha, color, rotation or text,
    /// never a layout-driven position; decorative graphics never take raycasts;
    /// everything runs on unscaled time and settles to the exact final state.
    /// </summary>
    public static class VaultFx
    {
        /// <summary>Global kill switch (Settings): effects snap straight to their end state.</summary>
        public static bool Enabled = true;

        public static float EaseOutCubic(float t) { t = Mathf.Clamp01(t); float u = 1f - t; return 1f - u * u * u; }

        public static float EaseOutBack(float t)
        {
            t = Mathf.Clamp01(t);
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        public static void FadeIn(GameObject go, float duration = 0.35f, float fromScale = 0.97f)
        {
            if (!Enabled) { return; }
            var fx = go.AddComponent<FadeScaleIn>();
            fx.Duration = duration;
            fx.FromScale = fromScale;
        }

        /// <summary>
        /// Procedural radial vignette (no texture asset): transparent centre,
        /// ink at the corners. Returned texture is cached.
        /// </summary>
        public static Texture2D Vignette()
        {
            if (_vignette != null) { return _vignette; }
            const int size = 128;
            _vignette = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / 1.41421f;
                    float a = Mathf.SmoothStep(0.35f, 1f, d) * 0.85f;
                    pixels[y * size + x] = new Color32(0, 0, 0, (byte)(a * 255));
                }
            }
            _vignette.SetPixels32(pixels);
            _vignette.Apply();
            return _vignette;
        }

        private static Texture2D _vignette;
    }

    /// <summary>Alpha 0→1 plus a slight scale-up; the entrance for new transcript lines and panels.</summary>
    public class FadeScaleIn : MonoBehaviour
    {
        public float Duration = 0.35f;
        public float FromScale = 0.97f;
        private CanvasGroup _group;
        private float _t;

        private void OnEnable()
        {
            // Explicit null check: ?? bypasses UnityEngine.Object's destroyed-object equality.
            _group = GetComponent<CanvasGroup>();
            if (_group == null) { _group = gameObject.AddComponent<CanvasGroup>(); }
            _t = 0f;
            Apply(0f);
        }

        private void Update()
        {
            _t += Time.unscaledDeltaTime;
            float k = Duration <= 0f ? 1f : _t / Duration;
            Apply(VaultFx.EaseOutCubic(k));
            if (k >= 1f) { Destroy(this); }
        }

        private void OnDestroy() { if (_group != null) { _group.alpha = 1f; } transform.localScale = Vector3.one; }

        private void Apply(float e)
        {
            _group.alpha = e;
            float s = Mathf.LerpUnclamped(FromScale, 1f, e);
            transform.localScale = new Vector3(s, s, 1f);
        }
    }

    /// <summary>
    /// RPG dialogue reveal. Layout-stable: the full text is set up front and
    /// the unrevealed tail is rich-text-transparent, so the line reserves its
    /// final height instead of growing (and jumping the scroll) as it types.
    /// Click the line, or call <see cref="CompleteAll"/>, to finish instantly.
    /// </summary>
    public class Typewriter : MonoBehaviour, IPointerClickHandler
    {
        private static readonly List<Typewriter> Active = new List<Typewriter>();

        public float CharsPerSecond = 70f;
        public float MaxSeconds = 3.5f;

        private Text _text;
        private string _full = string.Empty;
        private float _shown;
        private float _rate;

        public static void Begin(Text text)
        {
            if (!VaultFx.Enabled || string.IsNullOrEmpty(text.text)) { return; }
            // Rich-text masking can't safely wrap text that already contains tags.
            if (text.text.IndexOf('<') >= 0) { return; }
            var fx = text.gameObject.AddComponent<Typewriter>();
            fx._text = text;
            fx._full = text.text;
            text.supportRichText = true;
            fx._rate = Mathf.Max(fx.CharsPerSecond, fx._full.Length / fx.MaxSeconds);
            fx.Render();
            Active.Add(fx);
        }

        public static void CompleteAll()
        {
            for (int i = Active.Count - 1; i >= 0; i--) { if (Active[i] != null) { Active[i].Finish(); } }
            Active.Clear();
        }

        public static bool AnyTyping { get { return Active.Count > 0; } }

        private void Update()
        {
            _shown += _rate * Time.unscaledDeltaTime;
            if (_shown >= _full.Length) { Finish(); return; }
            Render();
        }

        public void OnPointerClick(PointerEventData eventData) { Finish(); }

        private void Finish()
        {
            if (_text != null) { _text.text = _full; }
            Active.Remove(this);
            Destroy(this);
        }

        private void Render()
        {
            int n = Mathf.Clamp((int)_shown, 0, _full.Length);
            _text.text = _full.Substring(0, n) + "<color=#00000000>" + _full.Substring(n) + "</color>";
        }

        private void OnDestroy() { Active.Remove(this); }
    }

    /// <summary>
    /// Roll chip entrance: the outcome slot tumbles through d20 faces, then
    /// lands on SUCCESS/FAIL with a punch. Success flashes gold; a failure
    /// shudders. Plays the dice sound when it lands.
    /// </summary>
    public class DiceTumble : MonoBehaviour
    {
        public Text Outcome;
        public Image Background;
        public bool Success;
        public string FinalText = string.Empty;
        public Color FinalColor = Color.white;
        public Color BaseBackground = Color.black;

        private const float TumbleSeconds = 0.55f;
        private const float SettleSeconds = 0.45f;
        private float _t;
        private float _nextFace;
        private bool _landed;

        private void Start()
        {
            if (!VaultFx.Enabled) { Land(); Destroy(this); return; }
            VaultSfx.Play(VaultSfx.Cue.Dice);
        }

        private void Update()
        {
            _t += Time.unscaledDeltaTime;
            if (_t < TumbleSeconds)
            {
                if (_t >= _nextFace)
                {
                    Outcome.text = Random.Range(1, 21).ToString();
                    Outcome.color = VaultTheme.Parchment;
                    _nextFace = _t + 0.05f;
                }
                return;
            }
            if (!_landed) { Land(); VaultSfx.Play(Success ? VaultSfx.Cue.Success : VaultSfx.Cue.Fail); }
            float k = (_t - TumbleSeconds) / SettleSeconds;
            float punch = 1f + 0.25f * (1f - VaultFx.EaseOutCubic(k));
            Outcome.transform.localScale = new Vector3(punch, punch, 1f);
            if (Success)
            {
                Background.color = Color.Lerp(VaultTheme.Gold * 0.6f, BaseBackground, VaultFx.EaseOutCubic(k));
            }
            else
            {
                float shake = Mathf.Sin(k * 40f) * 3f * (1f - k);
                transform.localRotation = Quaternion.Euler(0f, 0f, shake);
            }
            if (k >= 1f) { Destroy(this); }
        }

        private void Land()
        {
            _landed = true;
            Outcome.text = FinalText;
            Outcome.color = FinalColor;
        }

        private void OnDestroy()
        {
            if (!_landed && Outcome != null) { Land(); }
            if (Outcome != null) { Outcome.transform.localScale = Vector3.one; }
            if (Background != null) { Background.color = BaseBackground; }
            transform.localRotation = Quaternion.identity;
        }
    }

    /// <summary>Buttons swell slightly and brighten on hover; a click ticks.</summary>
    public class HoverGlow : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        private float _target = 1f;
        private float _scale = 1f;
        private Button _button;

        private void Awake() { _button = GetComponent<Button>(); }

        public void OnPointerEnter(PointerEventData eventData) { if (Interactable()) { _target = 1.04f; } }
        public void OnPointerExit(PointerEventData eventData) { _target = 1f; }
        public void OnPointerClick(PointerEventData eventData)
        {
            if (!Interactable()) { return; }
            _scale = 0.95f;
            VaultSfx.Play(VaultSfx.Cue.Click);
        }

        private bool Interactable() { return _button == null || _button.interactable; }

        private void OnDisable() { _target = 1f; _scale = 1f; transform.localScale = Vector3.one; }

        private void Update()
        {
            if (!VaultFx.Enabled) { _scale = 1f; }
            else { _scale = Mathf.Lerp(_scale, _target, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime)); }
            transform.localScale = new Vector3(_scale, _scale, 1f);
        }
    }

    /// <summary>Health bar fills from empty to its value when it appears.</summary>
    public class BarFill : MonoBehaviour
    {
        public Image Fill;
        public float Target;
        private float _t;

        private void OnEnable()
        {
            _t = 0f;
            if (Fill != null) { Fill.fillAmount = VaultFx.Enabled ? 0f : Target; }
        }

        private void Update()
        {
            _t += Time.unscaledDeltaTime;
            float k = _t / 0.8f;
            Fill.fillAmount = Target * VaultFx.EaseOutCubic(k);
            if (k >= 1f) { Fill.fillAmount = Target; enabled = false; }
        }
    }

    /// <summary>Slow color breathing between two colors (the header title's candle-light shimmer).</summary>
    public class Breathe : MonoBehaviour
    {
        public Graphic Target;
        public Color From = Color.white;
        public Color To = Color.white;
        public float Period = 4f;

        private void Update()
        {
            if (Target == null) { return; }
            if (!VaultFx.Enabled) { Target.color = From; return; }
            float k = 0.5f - 0.5f * Mathf.Cos(Time.unscaledTime * Mathf.PI * 2f / Period);
            Target.color = Color.Lerp(From, To, k);
        }
    }

    /// <summary>
    /// Caps a stretched panel to a readable column, centered. Only touches the
    /// horizontal offsets, so the panel's own vertical stretch is preserved.
    /// </summary>
    public class CapWidth : MonoBehaviour
    {
        private float _max = 900f;
        private float _margin = 8f;
        private RectTransform _rect;

        public void Init(float max, float margin) { _max = max; _margin = margin; }

        private void LateUpdate()
        {
            if (_rect == null) { _rect = (RectTransform)transform; }
            var parent = _rect.parent as RectTransform;
            if (parent == null) { return; }
            float side = Mathf.Max(_margin, (parent.rect.width - _max) * 0.5f);
            _rect.offsetMin = new Vector2(side, _rect.offsetMin.y);
            _rect.offsetMax = new Vector2(-side, _rect.offsetMax.y);
        }
    }

    /// <summary>
    /// "The DM is weaving the tale…" line under the chat while a turn resolves:
    /// pulsing rune, cycling dots. Polls a predicate so it needs no wiring.
    /// </summary>
    public class ThinkingIndicator : MonoBehaviour
    {
        public System.Func<bool> IsBusy;
        public Text Label;
        public string Phrase = "The Dungeon Master weaves the tale";

        private CanvasGroup _group;

        private void Awake() { _group = gameObject.AddComponent<CanvasGroup>(); _group.blocksRaycasts = false; }

        private void Update()
        {
            bool busy = IsBusy != null && IsBusy();
            float target = busy ? 1f : 0f;
            _group.alpha = Mathf.MoveTowards(_group.alpha, target, Time.unscaledDeltaTime * 4f);
            if (_group.alpha <= 0f) { return; }
            int dots = (int)(Time.unscaledTime * 2.5f) % 4;
            float pulse = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 3f);
            Label.text = "✦ " + Phrase + new string('.', dots);
            Label.color = Color.Lerp(VaultTheme.GoldDim, VaultTheme.Gold, VaultFx.Enabled ? pulse : 1f);
        }
    }

    /// <summary>
    /// Keeps a scroll view pinned to the bottom while new lines arrive, unless
    /// the reader has scrolled up to reread (then it leaves them alone).
    /// </summary>
    public class ChatAutoScroll : MonoBehaviour
    {
        public ScrollRect Scroll;
        private bool _pinned = true;
        private bool _settling;
        private float _lastHeight;

        private void Start()
        {
            Scroll.onValueChanged.AddListener(delegate
            {
                if (!_settling) { _pinned = Scroll.verticalNormalizedPosition <= 0.02f; }
            });
        }

        public void Pin() { _pinned = true; }

        private void LateUpdate()
        {
            float height = Scroll.content.rect.height;
            if (!_pinned || Mathf.Approximately(height, _lastHeight)) { return; }
            _lastHeight = height;
            _settling = true;
            Canvas.ForceUpdateCanvases();
            Scroll.verticalNormalizedPosition = 0f;
            _settling = false;
        }
    }

    /// <summary>
    /// Dim embers drifting up behind the page: a few dozen tiny quads, sway
    /// on a sine, respawn at the bottom. Purely decorative (no raycasts), only
    /// visible where panels are transparent (the chat page, margins).
    /// </summary>
    public class EmberField : MonoBehaviour
    {
        public int Count = 28;

        private sealed class Ember
        {
            public RectTransform Rect;
            public Image Image;
            public float Speed;
            public float Phase;
            public float Sway;
            public float BaseAlpha;
            public float X;
        }

        private readonly List<Ember> _embers = new List<Ember>();
        private RectTransform _area;

        private void Start()
        {
            _area = GetComponent<RectTransform>();
            for (int i = 0; i < Count; i++)
            {
                var go = new GameObject("Ember");
                go.transform.SetParent(transform, false);
                var image = go.AddComponent<Image>();
                image.raycastTarget = false;
                var rect = go.GetComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = Vector2.zero;
                float size = Random.Range(2f, 4.5f);
                rect.sizeDelta = new Vector2(size, size);
                var ember = new Ember { Rect = rect, Image = image };
                Respawn(ember, true);
                _embers.Add(ember);
            }
        }

        private void Respawn(Ember e, bool anywhere)
        {
            Rect area = _area.rect;
            e.X = Random.Range(0f, area.width);
            e.Speed = Random.Range(12f, 34f);
            e.Phase = Random.Range(0f, 10f);
            e.Sway = Random.Range(6f, 22f);
            e.BaseAlpha = Random.Range(0.12f, 0.35f);
            Color c = Random.value < 0.7f ? VaultTheme.Gold : new Color(1f, 0.45f, 0.15f);
            e.Image.color = new Color(c.r, c.g, c.b, e.BaseAlpha);
            e.Rect.anchoredPosition = new Vector2(e.X, anywhere ? Random.Range(0f, area.height) : -6f);
        }

        private void Update()
        {
            float height = _area.rect.height;
            float dt = Time.unscaledDeltaTime;
            foreach (var e in _embers)
            {
                e.Image.enabled = VaultFx.Enabled;
                if (!VaultFx.Enabled) { continue; }
                Vector2 p = e.Rect.anchoredPosition;
                p.y += e.Speed * dt;
                p.x = e.X + Mathf.Sin(Time.unscaledTime * 0.6f + e.Phase) * e.Sway;
                e.Rect.anchoredPosition = p;
                float life = Mathf.Clamp01(p.y / Mathf.Max(1f, height));
                float flicker = 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 7f + e.Phase * 3f);
                var c = e.Image.color;
                c.a = e.BaseAlpha * flicker * (1f - life * life);
                e.Image.color = c;
                if (p.y > height + 8f) { Respawn(e, false); }
            }
        }
    }
}
