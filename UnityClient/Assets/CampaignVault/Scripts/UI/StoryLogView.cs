using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Model;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The story: one element per transcript segment, kept in step through
    /// the transcript's Added/Removed/Updated events (no diffing, no full
    /// rebuilds; the 400-segment cap trims one element at a time).
    /// Consecutive tool activity folds into a single expandable strip. The
    /// view follows new text only while the reader is at the bottom; a
    /// "latest" button appears when they've scrolled up and more arrives.
    /// </summary>
    public sealed class StoryLogView
    {
        private const float PinSlack = 48f;

        private sealed class Entry
        {
            public VisualElement Element;
            /// <summary>For activity items: the strip they live in (shared by consecutive items).</summary>
            public VisualElement Strip;
        }

        private readonly VaultAppState _state;
        private readonly ScrollView _scroll;
        private readonly Button _jump;
        private readonly List<Entry> _entries = new List<Entry>();
        private bool _pinned = true;
        private bool _programmatic;

        public StoryLogView(VisualElement host, VaultAppState state)
        {
            _state = state;
            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.AddToClassList("cv-log");
            _scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _scroll.mouseWheelScrollSize = 60f;
            // Elastic overscroll fights programmatic follow (the view drifts off the last line).
            _scroll.touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped;
            host.Add(_scroll);

            _jump = Ui.Button("LATEST", "chevron", "cv-btn--small cv-jump", delegate { Pin(); });
            _jump.style.display = DisplayStyle.None;
            host.Add(_jump);

            // Only the reader moves the pin; content growth never does (B6).
            _scroll.verticalScroller.valueChanged += delegate (float v)
            {
                if (_programmatic) { return; }
                _pinned = v >= _scroll.verticalScroller.highValue - PinSlack;
                if (_pinned) { _jump.style.display = DisplayStyle.None; }
            };
            _scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(delegate { if (_pinned) { ScrollToEnd(); } });

            var transcript = state.Transcript;
            foreach (var seg in transcript.Segments) { Append(seg, false); }
            // Restored history arrives in a burst: no dice tumbling or sounds for rolls made last week.
            transcript.Added += delegate (TranscriptSegment seg) { Append(seg, state.FxEnabled && !seg.Restored); };
            transcript.Removed += OnRemoved;
            transcript.Updated += OnUpdated;
            transcript.Cleared += delegate { _entries.Clear(); _scroll.Clear(); };
        }

        /// <summary>Elements currently on screen (tests, diagnostics).</summary>
        public int EntryCount { get { return _entries.Count; } }
        public ScrollView ScrollView { get { return _scroll; } }

        /// <summary>Snap to the newest line and follow from here on.</summary>
        public void Pin()
        {
            _pinned = true;
            _jump.style.display = DisplayStyle.None;
            ScrollToEnd();
        }

        private void ScrollToEnd()
        {
            float content = _scroll.contentContainer.layout.height;
            float viewport = _scroll.contentViewport.layout.height;
            if (float.IsNaN(content) || float.IsNaN(viewport)) { return; }
            _programmatic = true;
            _scroll.scrollOffset = new Vector2(0f, Mathf.Max(0f, content - viewport));
            _programmatic = false;
        }

        private void NoteNewContent()
        {
            if (_pinned) { return; }
            _jump.style.display = DisplayStyle.Flex;
        }

        // ------------------------------------------------------------ rendering

        private void Append(TranscriptSegment seg, bool animate)
        {
            if (seg.Kind == SegmentKind.Player) { Pin(); }
            var entry = new Entry();
            if (IsActivity(seg))
            {
                VisualElement strip = LastStrip();
                if (strip == null)
                {
                    strip = BuildStrip();
                    _scroll.Add(strip);
                }
                entry.Strip = strip;
                entry.Element = seg.Kind == SegmentKind.Notes
                    ? Ui.Rich(seg.Text, "cv-activity__notes")
                    : Ui.Text(ActivityText(seg.Text), "cv-activity__item");
                strip.Q(className: "cv-activity__items").Add(entry.Element);
                RefreshStripTitle(strip);
            }
            else
            {
                entry.Element = Build(seg, animate);
                _scroll.Add(entry.Element);
            }
            _entries.Add(entry);
            NoteNewContent();
        }

        /// <summary>Tool lines and the two-pass loop's notes: folded into the turn's strip, out of the story.</summary>
        private static bool IsActivity(TranscriptSegment seg)
        {
            return seg.Kind == SegmentKind.Notes || (seg.Kind == SegmentKind.ToolData && !seg.Text.StartsWith("\U0001F4AD"));
        }

        /// <summary>The strip the next activity item joins: only if activity was the very last thing shown.</summary>
        private VisualElement LastStrip()
        {
            if (_entries.Count == 0) { return null; }
            var last = _entries[_entries.Count - 1];
            return last.Strip;
        }

        private VisualElement BuildStrip()
        {
            var strip = Ui.El("cv-activity cv-seg");
            var head = Ui.El("cv-activity__head");
            head.Add(Ui.Icon("chevron", "cv-activity__chevron"));
            head.Add(Ui.Icon("inspect"));
            head.Add(Ui.Text(string.Empty, "cv-activity__title"));
            head.RegisterCallback<ClickEvent>(delegate { strip.ToggleInClassList("cv-activity--open"); });
            strip.Add(head);
            strip.Add(Ui.El("cv-activity__items"));
            return strip;
        }

        private static void RefreshStripTitle(VisualElement strip)
        {
            var items = strip.Q(className: "cv-activity__items");
            int n = 0;
            bool notes = false;
            foreach (var child in items.Children())
            {
                if (child.ClassListContains("cv-activity__notes")) { notes = true; } else { n++; }
            }
            var title = strip.Q<Label>(className: "cv-activity__title");
            string text = n > 0 ? "THE DM CONSULTS THE LEDGER · " + n : "THE DM'S NOTES";
            if (n > 0 && notes) { text += " · NOTES"; }
            Ui.SetText(title, text);
        }

        /// <summary>Driver lines ("⚙ take_turn", "✦ the DM consults X") as readable activity.</summary>
        internal static string ActivityText(string text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.StartsWith("⚙")) { return "calls " + t.Substring(1).Trim(); }
            if (t.StartsWith("✦")) { return t.Substring(1).Trim(); }
            return t;
        }

        private VisualElement Build(TranscriptSegment seg, bool animate)
        {
            switch (seg.Kind)
            {
                case SegmentKind.Narration: return BuildNarration(seg);
                case SegmentKind.NpcVoice: return BuildVoice(seg);
                case SegmentKind.Roll: return BuildRoll(seg, animate);
                case SegmentKind.Player: return BuildPlayer(seg);
                case SegmentKind.Aside: return Ui.Rich(seg.Text, "cv-aside");
                case SegmentKind.Recap: return BuildRecap(seg);
                case SegmentKind.ToolData: return Ui.Rich(seg.Text.Substring(seg.Text.Length > 2 ? 2 : 0).Trim(), "cv-aside"); // reasoning 💭
                default: return BuildSystem(seg);
            }
        }

        private static VisualElement BuildNarration(TranscriptSegment seg)
        {
            var label = Ui.Rich(seg.Text, "cv-seg cv-seg--narration");
            label.EnableInClassList("cv-seg--streaming", seg.Streaming);
            return label;
        }

        private static VisualElement BuildVoice(TranscriptSegment seg)
        {
            Color hue = Ui.SpeakerColor(seg.Speaker);
            var row = Ui.El("cv-voice");
            var bar = Ui.El("cv-voice__bar");
            bar.style.backgroundColor = hue;
            row.Add(bar);
            var body = Ui.El("cv-voice__body");
            var name = Ui.Text(seg.Speaker.ToUpperInvariant(), "cv-nameplate");
            name.style.color = hue;
            body.Add(name);
            body.Add(Ui.Rich("“" + seg.Text + "”", "cv-voice__line"));
            row.Add(body);
            return row;
        }

        /// <summary>A session boundary: a gilt rule with the heading, then the "previously…" text.</summary>
        private static VisualElement BuildRecap(TranscriptSegment seg)
        {
            var card = Ui.El("cv-recap");
            var head = Ui.El("cv-recap__head");
            head.Add(Ui.El("cv-recap__rule"));
            head.Add(Ui.Text((seg.Speaker ?? string.Empty).ToUpperInvariant(), "cv-recap__title"));
            head.Add(Ui.El("cv-recap__rule"));
            card.Add(head);
            if (!string.IsNullOrEmpty(seg.Text)) { card.Add(Ui.Rich(seg.Text, "cv-recap__text")); }
            return card;
        }

        private static VisualElement BuildPlayer(TranscriptSegment seg)
        {
            var row = Ui.El("cv-player");
            var bubble = Ui.El("cv-player__bubble");
            bubble.Add(Ui.Icon("send"));
            bubble.Add(Ui.Text(seg.Text, "cv-player__text"));
            row.Add(bubble);
            return row;
        }

        private static VisualElement BuildSystem(TranscriptSegment seg)
        {
            string text = seg.Text ?? string.Empty;
            bool error = text.StartsWith("⚠");
            if (error) { text = text.Substring(1).Trim(); }
            var row = Ui.El("cv-system cv-seg" + (error ? " cv-system--error" : string.Empty));
            row.Add(Ui.Icon(error ? "warning" : "spark"));
            row.Add(Ui.Text(text, "cv-system__text"));
            return row;
        }

        internal static string RollClass(RollOutcome outcome)
        {
            switch (outcome)
            {
                case RollOutcome.CriticalSuccess: return "cv-roll--crit-success";
                case RollOutcome.Success: return "cv-roll--success";
                case RollOutcome.CriticalFailure: return "cv-roll--crit-failure";
                default: return "cv-roll--failure";
            }
        }

        private static VisualElement BuildRoll(TranscriptSegment seg, bool animate)
        {
            var roll = seg.Roll ?? new RollInfo();
            var card = Ui.El("cv-roll " + RollClass(roll.Outcome));
            // The die face carries the total; the ribbon carries the server's verdict.
            var die = Ui.El("cv-roll__die");
            die.Add(Ui.Icon("d20", "cv-roll__face"));
            int total = RollTotal(roll);
            die.Add(Ui.Text(total > 0 ? total.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?", "cv-roll__total"));
            card.Add(die);
            var body = Ui.El("cv-roll__body");
            body.Add(Ui.Text(roll.Label, "cv-roll__label"));
            string against = RollAgainst(roll);
            if (against.Length > 0) { body.Add(Ui.Text(against, "cv-roll__detail")); }
            card.Add(body);
            var ribbon = Ui.El("cv-roll__ribbon");
            ribbon.Add(Ui.Text(VerdictWords(roll.Verdict).ToUpperInvariant(), "cv-roll__verdict"));
            card.Add(ribbon);
            if (animate)
            {
                Ui.Enter(card, "cv-roll--enter", true);
                VaultSfx.Play(VaultSfx.Cue.Dice);
                card.schedule.Execute(() => { VaultSfx.Play(roll.Success ? VaultSfx.Cue.Success : VaultSfx.Cue.Fail); }).StartingIn(520);
            }
            return card;
        }

        /// <summary>The rolled total: RollInfo.Total, or the number that opens "17 vs DC 14".</summary>
        internal static int RollTotal(RollInfo roll)
        {
            if (roll.Total > 0) { return roll.Total; }
            var m = System.Text.RegularExpressions.Regex.Match(roll.Detail ?? string.Empty, @"^\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>"17 vs DC 14" → "against DC 14"; details without a target are kept as they are.</summary>
        internal static string RollAgainst(RollInfo roll)
        {
            string detail = roll.Detail ?? string.Empty;
            int vs = detail.IndexOf(" vs ", System.StringComparison.Ordinal);
            return vs >= 0 ? "against " + detail.Substring(vs + 4).Trim() : detail;
        }

        /// <summary>"CriticalSuccess" → "Critical Success"; "Critical hit" stays.</summary>
        internal static string VerdictWords(string verdict)
        {
            return System.Text.RegularExpressions.Regex.Replace(verdict ?? string.Empty, "(?<=[a-z])(?=[A-Z])", " ");
        }

        // -------------------------------------------------------------- changes

        private void OnRemoved(int index, TranscriptSegment seg)
        {
            if (index < 0 || index >= _entries.Count) { return; }
            var entry = _entries[index];
            _entries.RemoveAt(index);
            entry.Element.RemoveFromHierarchy();
            if (entry.Strip != null)
            {
                if (entry.Strip.Q(className: "cv-activity__items").childCount == 0) { entry.Strip.RemoveFromHierarchy(); }
                else { RefreshStripTitle(entry.Strip); }
            }
        }

        private void OnUpdated(TranscriptSegment seg)
        {
            int index = IndexOf(seg);
            if (index < 0) { return; }
            var entry = _entries[index];
            // Streaming text grows in place; a kind change (aside promoted to
            // narration, a cut-off reply demoted to aside) swaps the element.
            var label = entry.Element as Label;
            bool sameShape = label != null && entry.Strip == null
                && ((seg.Kind == SegmentKind.Narration && label.ClassListContains("cv-seg--narration"))
                    || (seg.Kind == SegmentKind.Aside && label.ClassListContains("cv-aside")));
            if (sameShape)
            {
                label.text = MarkdownLite.ToRichText(Net.TextSanitizer.Clean(seg.Text));
                label.EnableInClassList("cv-seg--streaming", seg.Streaming);
            }
            else if (entry.Strip == null)
            {
                var replacement = Build(seg, false);
                int at = _scroll.contentContainer.IndexOf(entry.Element);
                entry.Element.RemoveFromHierarchy();
                _scroll.contentContainer.Insert(Mathf.Max(0, at), replacement);
                entry.Element = replacement;
            }
            NoteNewContent();
        }

        private int IndexOf(TranscriptSegment seg)
        {
            var segments = _state.Transcript.Segments;
            // Updates hit the newest segments: search from the end.
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(segments[i], seg)) { return i < _entries.Count ? i : -1; }
            }
            return -1;
        }
    }
}
