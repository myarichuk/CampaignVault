using UnityEngine;
using UnityEngine.UI;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The Vault look: a dark arcane-library theme. Deep ink backgrounds, a
    /// gold accent reserved for interactive / mechanical truth (roll chips,
    /// headers), parchment prose, italic speaker-colored NPC voices, and a
    /// blood-to-leaf HP language shared by every panel.
    ///
    /// Font slots are designer-swappable: drop a serif display face, a body
    /// face, and a mono face in the inspector and every panel follows. With
    /// nothing assigned the client falls back to the built-in Arial and still
    /// differentiates voices purely through style, size, and color.
    /// </summary>
    public static class VaultTheme
    {
        public static readonly Color Ink = new Color(0.055f, 0.067f, 0.086f);
        public static readonly Color Panel = new Color(0.09f, 0.106f, 0.141f);
        public static readonly Color PanelRaised = new Color(0.118f, 0.141f, 0.18f);
        public static readonly Color Gold = new Color(0.788f, 0.635f, 0.153f);
        public static readonly Color GoldDim = new Color(0.55f, 0.44f, 0.13f);
        public static readonly Color Parchment = new Color(0.914f, 0.863f, 0.765f);
        public static readonly Color Muted = new Color(0.604f, 0.639f, 0.698f);
        public static readonly Color Faint = new Color(0.42f, 0.455f, 0.51f);
        public static readonly Color Blood = new Color(0.839f, 0.271f, 0.271f);
        public static readonly Color Leaf = new Color(0.435f, 0.812f, 0.486f);
        public static readonly Color Arcane = new Color(0.431f, 0.659f, 0.996f);
        public static readonly Color RollBg = new Color(0.13f, 0.11f, 0.05f);

        public static readonly Color[] SpeakerHues =
        {
            new Color(0.62f, 0.82f, 1.0f),
            new Color(1.0f, 0.78f, 0.62f),
            new Color(0.72f, 1.0f, 0.78f),
            new Color(1.0f, 0.68f, 0.85f),
            new Color(0.85f, 0.8f, 1.0f),
            new Color(1.0f, 0.9f, 0.6f),
            new Color(0.65f, 1.0f, 0.95f),
            new Color(1.0f, 0.62f, 0.62f),
        };

        public const int BodySize = 17;
        public const int SmallSize = 13;
        public const int HeaderSize = 26;
        public const int SubHeaderSize = 19;
        public const int RollSize = 15;

        public static Font BodyFont;
        public static Font DisplayFont;
        public static Font MonoFont;

        public static void EnsureFonts()
        {
            if (BodyFont == null) { BodyFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
            if (DisplayFont == null) { DisplayFont = BodyFont; }
            if (MonoFont == null) { MonoFont = BodyFont; }
        }

        public static Color SpeakerColor(string speaker)
        {
            if (string.IsNullOrEmpty(speaker)) { return Muted; }
            unchecked
            {
                int hash = 23;
                for (int i = 0; i < speaker.Length; i++) { hash = hash * 31 + speaker[i]; }
                if (hash < 0) { hash = -hash; }
                return SpeakerHues[hash % SpeakerHues.Length];
            }
        }

        public static Color HealthColor(double fraction)
        {
            if (fraction > 0.6) { return Leaf; }
            if (fraction > 0.3) { return Gold; }
            return Blood;
        }

        public static GameObject PanelBox(Transform parent, string name, Color bg)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = bg;
            // A hairline of dim gold: cards read as framed tabletop pieces, not flat fills.
            var frame = go.AddComponent<Outline>();
            frame.effectColor = new Color(GoldDim.r, GoldDim.g, GoldDim.b, 0.35f);
            frame.effectDistance = new Vector2(1f, -1f);
            return go;
        }

        public static Text MakeText(Transform parent, string name, int size, Color color, FontStyle style, Font font)
        {
            EnsureFonts();
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = font ?? BodyFont;
            text.fontSize = size;
            text.color = color;
            text.fontStyle = style;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        public static Text NarrationText(Transform parent, string name, string content)
        {
            var text = MakeText(parent, name, BodySize, Parchment, FontStyle.Normal, BodyFont);
            text.text = content;
            FitVertical(text);
            return text;
        }

        public static void FitVertical(Text text)
        {
            var fitter = text.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        public static Button MakeButton(Transform parent, string name, string label, int size)
        {
            EnsureFonts();
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = PanelRaised;
            var button = go.AddComponent<Button>();
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var labelText = labelGo.AddComponent<Text>();
            Stretch(labelGo.GetComponent<RectTransform>(), 0, 0, 0, 0);
            labelText.font = BodyFont;
            labelText.fontSize = size;
            labelText.color = Parchment;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.text = label;
            var colors = button.colors;
            colors.normalColor = PanelRaised;
            colors.highlightedColor = new Color(0.16f, 0.19f, 0.24f);
            colors.pressedColor = GoldDim;
            colors.selectedColor = GoldDim;
            button.colors = colors;
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 34;
            go.AddComponent<HoverGlow>();
            return button;
        }

        public static Button GoldButton(Transform parent, string name, string label, int size)
        {
            var button = MakeButton(parent, name, label, size);
            button.GetComponent<Image>().color = new Color(0.32f, 0.25f, 0.08f);
            button.GetComponentInChildren<Text>().color = Gold;
            return button;
        }

        public static InputField MakeInput(Transform parent, string name, string placeholder, bool password)
        {
            EnsureFonts();
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var bg = go.AddComponent<Image>();
            bg.color = Ink;
            var input = go.AddComponent<InputField>();

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var text = textGo.AddComponent<Text>();
            text.font = BodyFont;
            text.fontSize = BodySize - 2;
            text.color = Parchment;
            Stretch(textGo.GetComponent<RectTransform>(), 8, 8, 6, 6);

            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(go.transform, false);
            var ph = phGo.AddComponent<Text>();
            ph.font = BodyFont;
            ph.fontSize = BodySize - 2;
            ph.fontStyle = FontStyle.Italic;
            ph.color = Faint;
            ph.text = placeholder;
            Stretch(phGo.GetComponent<RectTransform>(), 8, 8, 6, 6);

            input.textComponent = text;
            input.placeholder = ph;
            if (password) { input.contentType = InputField.ContentType.Password; }
            input.lineType = InputField.LineType.SingleLine;
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 36;
            layout.flexibleWidth = 1;
            return input;
        }

        public static InputField MakeArea(Transform parent, string name, string placeholder, int minHeight)
        {
            var input = MakeInput(parent, name, placeholder, false);
            input.lineType = InputField.LineType.MultiLineNewline;
            input.GetComponent<LayoutElement>().minHeight = minHeight;
            return input;
        }

        public static GameObject Row(Transform parent, string name, int spacing)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            return go;
        }

        public static GameObject Column(Transform parent, string name, int spacing)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return go;
        }

        public static ScrollRect MakeScrollView(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Stretch(go.AddComponent<RectTransform>(), 0, 0, 0, 0);
            var scroll = go.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24;
            var scrollLayout = go.AddComponent<LayoutElement>();
            scrollLayout.flexibleHeight = 1;
            scrollLayout.flexibleWidth = 1;

            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(go.transform, false);
            Stretch(viewport.AddComponent<RectTransform>(), 0, 0, 0, 0);
            var maskImage = viewport.AddComponent<Image>();
            maskImage.color = Panel;
            viewport.AddComponent<Mask>().showMaskGraphic = false;

            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0, 0);
            var group = content.AddComponent<VerticalLayoutGroup>();
            group.spacing = 8;
            group.padding = new RectOffset(12, 12, 12, 12);
            group.childControlWidth = true;
            group.childControlHeight = false;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.content = contentRect;
            return scroll;
        }

        public static GameObject HealthBar(Transform parent, string name, double fraction, string label)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var bg = go.AddComponent<Image>();
            bg.color = Ink;
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 26;
            layout.flexibleWidth = 1;

            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(go.transform, false);
            var fill = fillGo.AddComponent<Image>();
            fill.color = HealthColor(fraction);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = 0;
            fill.fillAmount = (float)fraction;
            Stretch(fillGo.GetComponent<RectTransform>(), 2, 2, 2, 2);
            var anim = go.AddComponent<BarFill>();
            anim.Fill = fill;
            anim.Target = (float)fraction;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var text = labelGo.AddComponent<Text>();
            EnsureFonts();
            text.font = BodyFont;
            text.fontSize = SmallSize;
            text.fontStyle = FontStyle.Bold;
            text.color = Parchment;
            text.alignment = TextAnchor.MiddleCenter;
            text.text = label;
            Stretch(labelGo.GetComponent<RectTransform>(), 0, 0, 0, 0);
            return go;
        }

        public static void Stretch(RectTransform rect, float left, float right, float top, float bottom)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        public static void ClearChildren(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(parent.GetChild(i).gameObject);
            }
        }
    }
}
