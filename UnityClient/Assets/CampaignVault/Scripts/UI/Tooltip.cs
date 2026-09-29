using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Hover tooltips for first-class actions (session lifecycle, compaction
    /// notes). Desktop-first: touch devices get the same text inline next to
    /// the control instead. One shared floating card, shown on pointer enter.
    /// </summary>
    public static class Tooltip
    {
        private static GameObject _card;
        private static Text _label;

        public static void Attach(GameObject go, string text)
        {
            var trigger = go.GetComponent<EventTrigger>();
            if (trigger == null) { trigger = go.AddComponent<EventTrigger>(); }
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(delegate (BaseEventData data) { Show(text); });
            trigger.triggers.Add(enter);
            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(delegate (BaseEventData data) { Hide(); });
            trigger.triggers.Add(exit);
        }

        private static void Ensure()
        {
            if (_card != null) { return; }
            VaultTheme.EnsureFonts();
            var canvas = Object.FindFirstObjectByType<Canvas>();
            _card = new GameObject("Tooltip");
            _card.transform.SetParent(canvas != null ? canvas.transform : null, false);
            var image = _card.AddComponent<Image>();
            image.color = VaultTheme.PanelRaised;
            var outline = _card.AddComponent<Outline>();
            outline.effectColor = VaultTheme.GoldDim;
            var rect = _card.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(0, 0);
            rect.pivot = new Vector2(0, 1);
            rect.sizeDelta = new Vector2(340, 60);
            var fitter = _card.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var textGo = new GameObject("Tip");
            textGo.transform.SetParent(_card.transform, false);
            _label = textGo.AddComponent<Text>();
            _label.font = VaultTheme.BodyFont;
            _label.fontSize = VaultTheme.SmallSize;
            _label.color = VaultTheme.Parchment;
            _label.fontStyle = FontStyle.Italic;
            _label.alignment = TextAnchor.UpperLeft;
            _label.horizontalOverflow = HorizontalWrapMode.Wrap;
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10, 8);
            textRect.offsetMax = new Vector2(-10, -8);
            var textFitter = textGo.AddComponent<ContentSizeFitter>();
            textFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _card.SetActive(false);
        }

        public static void Show(string text)
        {
            Ensure();
            _label.text = text;
            Vector2 mouse = Input.mousePosition;
            var rect = _card.GetComponent<RectTransform>();
            float x = Mathf.Min(mouse.x + 16, Screen.width - 356);
            float y = Mathf.Max(mouse.y - 12, 80);
            rect.anchoredPosition = new Vector2(x, y);
            _card.SetActive(true);
            _card.transform.SetAsLastSibling();
        }

        public static void Hide()
        {
            if (_card != null) { _card.SetActive(false); }
        }
    }
}
