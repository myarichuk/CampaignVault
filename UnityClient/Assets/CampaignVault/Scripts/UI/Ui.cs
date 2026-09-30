using System;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Element factory for the cv-* design system (Theme/*.uss). Views build
    /// dynamic content with these instead of hand-setting styles, so every
    /// visual decision stays in USS.
    /// </summary>
    public static class Ui
    {
        public static VisualElement El(string classes = null, string name = null)
        {
            var e = new VisualElement();
            if (name != null) { e.name = name; }
            AddClasses(e, classes);
            return e;
        }

        public static T AddClasses<T>(T e, string classes) where T : VisualElement
        {
            if (string.IsNullOrEmpty(classes)) { return e; }
            foreach (string c in classes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) { e.AddToClassList(c); }
            return e;
        }

        /// <summary>Plain text: rich-text tags in the content are shown, never interpreted.</summary>
        public static Label Text(string text, string classes = null)
        {
            var l = new Label(MarkdownLite.Escape(TextSanitizer.Clean(text ?? string.Empty)));
            l.enableRichText = true;
            return AddClasses(l, classes);
        }

        /// <summary>Model or server prose: the markdown subset, rendered safely.</summary>
        public static Label Rich(string markdown, string classes = null)
        {
            var l = new Label(MarkdownLite.ToRichText(TextSanitizer.Clean(markdown ?? string.Empty)));
            l.enableRichText = true;
            return AddClasses(l, classes);
        }

        public static void SetText(Label label, string text)
        {
            label.text = MarkdownLite.Escape(TextSanitizer.Clean(text ?? string.Empty));
        }

        public static VisualElement Icon(string icon, string classes = null)
        {
            var e = El("cv-icon cv-icon--" + icon);
            e.pickingMode = PickingMode.Ignore;
            return AddClasses(e, classes);
        }

        public static Button Button(string text, string icon, string classes, Action onClick)
        {
            var b = new Button();
            b.AddToClassList("cv-btn");
            AddClasses(b, classes);
            if (!string.IsNullOrEmpty(icon)) { b.Add(Icon(icon)); }
            if (!string.IsNullOrEmpty(text))
            {
                var label = new Label(text);
                label.pickingMode = PickingMode.Ignore;
                label.AddToClassList("cv-btn__label");
                b.Add(label);
            }
            if (onClick != null) { b.clicked += delegate { VaultSfx.Play(VaultSfx.Cue.Click); onClick(); }; }
            return b;
        }

        public static Button IconButton(string icon, string tooltip, string classes, Action onClick)
        {
            var b = Button(null, icon, "cv-btn--icon " + (classes ?? string.Empty), onClick);
            if (!string.IsNullOrEmpty(tooltip)) { TooltipLayer.Attach(b, tooltip); }
            return b;
        }

        public static void SetButtonText(Button b, string text)
        {
            var label = b.Q<Label>(className: "cv-btn__label");
            if (label != null) { label.text = text; }
        }

        public static TextField Field(string placeholder, string value = null, bool multiline = false, string classes = null)
        {
            var f = new TextField();
            f.multiline = multiline;
            f.AddToClassList("cv-field");
            if (multiline) { f.AddToClassList("cv-field--multiline"); }
            AddClasses(f, classes);
            f.textEdition.placeholder = placeholder ?? string.Empty;
            f.textEdition.hidePlaceholderOnFocus = true;
            f.value = value ?? string.Empty;
            return f;
        }

        public static TextField Password(string placeholder)
        {
            var f = Field(placeholder);
            f.isPasswordField = true;
            return f;
        }

        /// <summary>A labelled field: small-caps caption, then the input.</summary>
        public static TextField LabeledField(VisualElement parent, string caption, string placeholder, string value = null, bool multiline = false)
        {
            parent.Add(Text(caption.ToUpperInvariant(), "cv-caption cv-field-caption"));
            var f = Field(placeholder, value, multiline);
            parent.Add(f);
            return f;
        }

        /// <summary>An on/off switch; returns a setter that repaints it without firing onChange.</summary>
        public static Action<bool> Switch(VisualElement parent, string label, bool on, Action<bool> onChange)
        {
            var root = El("cv-switch");
            var track = El("cv-switch__track");
            track.Add(El("cv-switch__knob"));
            root.Add(track);
            root.Add(Text(label, "cv-body"));
            bool state = on;
            Action<bool> paint = delegate (bool v) { state = v; root.EnableInClassList("cv-switch--on", v); };
            paint(on);
            root.RegisterCallback<ClickEvent>(delegate
            {
                paint(!state);
                VaultSfx.Play(VaultSfx.Cue.Click);
                if (onChange != null) { onChange(state); }
            });
            parent.Add(root);
            return paint;
        }

        public static VisualElement Chip(string text, string variant = null, string icon = null)
        {
            var c = El("cv-chip");
            if (!string.IsNullOrEmpty(variant)) { c.AddToClassList("cv-chip--" + variant); }
            if (!string.IsNullOrEmpty(icon)) { c.Add(Icon(icon)); }
            string clean = TextSanitizer.Clean(text ?? string.Empty, 48);
            if (clean.Length > 32) { clean = clean.Substring(0, 32) + "…"; }
            c.Add(Text(clean.ToUpperInvariant()));
            return c;
        }

        /// <summary>A bar with a lagging "damage ghost" behind the fill.</summary>
        public static VisualElement Bar(double fraction, string classes = null)
        {
            var bar = El("cv-bar");
            AddClasses(bar, classes);
            bar.Add(El("cv-bar__ghost"));
            bar.Add(El("cv-bar__fill"));
            SetBar(bar, fraction);
            return bar;
        }

        public static void SetBar(VisualElement bar, double fraction)
        {
            float f = Mathf.Clamp01((float)fraction);
            bar.Q(className: "cv-bar__fill").style.width = Length.Percent(f * 100f);
            bar.Q(className: "cv-bar__ghost").style.width = Length.Percent(f * 100f);
            bar.EnableInClassList("cv-bar--mid", f <= 0.6f && f > 0.3f);
            bar.EnableInClassList("cv-bar--low", f <= 0.3f);
        }

        /// <summary>Filigree corners on a framed element.</summary>
        public static T Frame<T>(T e) where T : VisualElement
        {
            e.AddToClassList("cv-frame");
            foreach (string corner in new[] { "tl", "tr", "bl", "br" })
            {
                var c = El("cv-frame__corner cv-frame__corner--" + corner);
                c.pickingMode = PickingMode.Ignore;
                e.Add(c);
            }
            return e;
        }

        public static VisualElement Empty(string icon, string text)
        {
            var e = El("cv-empty");
            e.Add(Icon(icon));
            e.Add(Text(text, "cv-empty__text"));
            return e;
        }

        public static VisualElement Card(string title)
        {
            var card = El("cv-card");
            if (!string.IsNullOrEmpty(title)) { card.Add(Text(title.ToUpperInvariant(), "cv-caption cv-card__title")); }
            return card;
        }

        public static VisualElement KeyValue(string key, string value)
        {
            var row = El("cv-kv");
            row.Add(Text(key.ToUpperInvariant(), "cv-kv__key"));
            row.Add(Text(value, "cv-kv__value"));
            return row;
        }

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

        /// <summary>A stable hue per speaker name, so each NPC keeps their color all campaign.</summary>
        public static Color SpeakerColor(string speaker)
        {
            if (string.IsNullOrEmpty(speaker)) { return new Color(0.5f, 0.47f, 0.4f); }
            unchecked
            {
                int hash = 23;
                for (int i = 0; i < speaker.Length; i++) { hash = hash * 31 + speaker[i]; }
                if (hash < 0) { hash = -hash; }
                return SpeakerHues[hash % SpeakerHues.Length];
            }
        }

        /// <summary>"locations/old-mill" → "Old Mill".</summary>
        public static string PrettyId(string id)
        {
            if (string.IsNullOrEmpty(id)) { return string.Empty; }
            string tail = id.Substring(id.LastIndexOf('/') + 1).Replace('-', ' ').Replace('_', ' ');
            var chars = tail.ToCharArray();
            bool start = true;
            for (int i = 0; i < chars.Length; i++)
            {
                if (start && char.IsLetter(chars[i])) { chars[i] = char.ToUpperInvariant(chars[i]); }
                start = chars[i] == ' ';
            }
            return new string(chars);
        }

        /// <summary>Two-letter monogram for a portrait tile.</summary>
        public static string Monogram(string name)
        {
            if (string.IsNullOrEmpty(name)) { return "?"; }
            var parts = name.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) { return (parts[0].Substring(0, 1) + parts[1].Substring(0, 1)).ToUpperInvariant(); }
            return name.Trim().Substring(0, Math.Min(2, name.Trim().Length)).ToUpperInvariant();
        }

        /// <summary>Adds a class next frame, so a transition from the base state actually plays.</summary>
        public static void Enter(VisualElement e, string enterClass, bool animate)
        {
            if (!animate) { return; }
            e.AddToClassList(enterClass);
            e.schedule.Execute(() => { e.RemoveFromClassList(enterClass); }).StartingIn(20);
        }
    }
}
