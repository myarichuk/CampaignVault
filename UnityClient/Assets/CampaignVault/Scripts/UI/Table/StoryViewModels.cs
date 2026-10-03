using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Table
{
    /// <summary>Words the story log shows for a roll or a tool line.</summary>
    public static class StoryText
    {
        /// <summary>Driver lines ("⚙ take_turn", "✦ the DM consults X") as readable activity.</summary>
        public static string ActivityText(string text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.StartsWith("⚙")) { return "calls " + t.Substring(1).Trim(); }
            if (t.StartsWith("✦")) { return t.Substring(1).Trim(); }
            return t;
        }

        /// <summary>The rolled total: RollInfo.Total, or the number that opens "17 vs DC 14".</summary>
        public static int RollTotal(RollInfo roll)
        {
            if (roll.Total > 0) { return roll.Total; }
            var m = Regex.Match(roll.Detail ?? string.Empty, @"^\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>"17 vs DC 14" → "against DC 14"; details without a target are kept as they are.</summary>
        public static string RollAgainst(RollInfo roll)
        {
            string detail = roll.Detail ?? string.Empty;
            int vs = detail.IndexOf(" vs ", StringComparison.Ordinal);
            return vs >= 0 ? "against " + detail.Substring(vs + 4).Trim() : detail;
        }

        /// <summary>"CriticalSuccess" → "Critical Success"; "Critical hit" stays.</summary>
        public static string VerdictWords(string verdict)
        {
            return Regex.Replace(verdict ?? string.Empty, "(?<=[a-z])(?=[A-Z])", " ");
        }

        /// <summary>One of eight stable hues per speaker name (cv-speaker--N in USS), so each NPC keeps their colour all campaign; "none" for no name.</summary>
        public static string SpeakerHue(string speaker)
        {
            if (string.IsNullOrEmpty(speaker)) { return "none"; }
            unchecked
            {
                int hash = 23;
                for (int i = 0; i < speaker.Length; i++) { hash = hash * 31 + speaker[i]; }
                if (hash < 0) { hash = -hash; }
                return (hash % 8).ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Tool lines and the two-pass loop's notes: folded into the turn's strip, out of the story.</summary>
        public static bool IsActivity(TranscriptSegment seg)
        {
            return seg.Kind == SegmentKind.Notes || (seg.Kind == SegmentKind.ToolData && !seg.Text.StartsWith("\U0001F4AD"));
        }
    }

    /// <summary>One thing in the story, shown with the template for its kind (a <see cref="Controls.LiveRepeater"/> picks it).</summary>
    public abstract class StoryItemViewModel : ViewModel, ITemplated
    {
        public abstract string Template { get; }

        /// <summary>True when a changed segment can be shown by updating this item; otherwise the log swaps in a new one.</summary>
        public virtual bool Accepts(TranscriptSegment seg) { return false; }

        public virtual void Update(TranscriptSegment seg) { }

        /// <summary>The item for a segment that is not activity. <paramref name="animate"/>: a roll tumbles in (not for restored history).</summary>
        public static StoryItemViewModel For(TranscriptSegment seg, bool animate, Later later, VaultAppState state = null)
        {
            if (seg.Failure != null && state != null && seg.Kind == SegmentKind.System)
            {
                return new ErrorCardViewModel(seg.Failure, delegate { state.RequestRetryTurn(); }, delegate { state.RequestSettings(); },
                    delegate (string note) { state.RaiseToast(note, ToastKind.Success); }, delegate { return state.CanRetryTurn; }, state);
            }
            switch (seg.Kind)
            {
                case SegmentKind.Narration:
                case SegmentKind.Aside:
                case SegmentKind.ToolData:
                    return new ProseViewModel(seg);
                case SegmentKind.NpcVoice: return new VoiceViewModel(seg);
                case SegmentKind.Roll: return new RollViewModel(seg, animate, later);
                case SegmentKind.Player: return new PlayerViewModel(seg);
                case SegmentKind.Recap: return new RecapViewModel(seg);
                default: return new SystemLineViewModel(seg);
            }
        }
    }

    /// <summary>The DM's narration (streaming in place) or a quiet aside.</summary>
    public sealed class ProseViewModel : StoryItemViewModel
    {
        private readonly SegmentKind _kind;
        private string _text = string.Empty;
        private bool _streaming;

        public ProseViewModel(TranscriptSegment seg)
        {
            _kind = seg.Kind;
            Update(seg);
        }

        public override string Template { get { return _kind == SegmentKind.Narration ? "Story/Narration" : "Story/Aside"; } }
        [CreateProperty] public string Text { get { return _text; } private set { Set(ref _text, value); } }
        [CreateProperty] public bool Streaming { get { return _streaming; } private set { Set(ref _streaming, value); } }

        public override bool Accepts(TranscriptSegment seg)
        {
            return seg.Kind == _kind && (_kind == SegmentKind.Narration || _kind == SegmentKind.Aside);
        }

        public override void Update(TranscriptSegment seg)
        {
            // A reasoning line (💭) keeps only what follows its marker.
            string text = seg.Kind == SegmentKind.ToolData ? seg.Text.Substring(seg.Text.Length > 2 ? 2 : 0).Trim() : seg.Text;
            Text = DisplayText.Rich(text);
            Streaming = seg.Streaming;
        }
    }

    public sealed class VoiceViewModel : StoryItemViewModel
    {
        public VoiceViewModel(TranscriptSegment seg)
        {
            Speaker = DisplayText.Plain((seg.Speaker ?? string.Empty).ToUpperInvariant());
            Hue = StoryText.SpeakerHue(seg.Speaker);
            Line = DisplayText.Rich("“" + seg.Text + "”");
        }

        public override string Template { get { return "Story/Voice"; } }
        [CreateProperty] public string Speaker { get; private set; }
        [CreateProperty] public string Hue { get; private set; }
        [CreateProperty] public string Line { get; private set; }
    }

    /// <summary>The player's own line, right-aligned.</summary>
    public sealed class PlayerViewModel : StoryItemViewModel
    {
        public PlayerViewModel(TranscriptSegment seg) { Text = DisplayText.Plain(seg.Text); }
        public override string Template { get { return "Story/Player"; } }
        [CreateProperty] public string Text { get; private set; }
    }

    /// <summary>A session boundary: a gilt rule with the heading, then the "previously…" text.</summary>
    public sealed class RecapViewModel : StoryItemViewModel
    {
        public RecapViewModel(TranscriptSegment seg)
        {
            Title = DisplayText.Plain((seg.Speaker ?? string.Empty).ToUpperInvariant());
            Text = string.IsNullOrEmpty(seg.Text) ? string.Empty : DisplayText.Rich(seg.Text);
        }

        public override string Template { get { return "Story/Recap"; } }
        [CreateProperty] public string Title { get; private set; }
        [CreateProperty] public string Text { get; private set; }
    }

    /// <summary>A line from the client or the server ("⚠ …" is an error).</summary>
    public sealed class SystemLineViewModel : StoryItemViewModel
    {
        public SystemLineViewModel(TranscriptSegment seg)
        {
            string text = seg.Text ?? string.Empty;
            Error = text.StartsWith("⚠");
            if (Error) { text = text.Substring(1).Trim(); }
            Text = DisplayText.Plain(text);
            Icon = Error ? "warning" : "spark";
        }

        public override string Template { get { return "Story/System"; } }
        [CreateProperty] public string Text { get; private set; }
        [CreateProperty] public bool Error { get; private set; }
        [CreateProperty] public string Icon { get; private set; }
    }

    /// <summary>A roll card: the die carries the total, the ribbon the server's verdict. A fresh roll tumbles in with the dice sound.</summary>
    public sealed class RollViewModel : StoryItemViewModel
    {
        private bool _entering;

        public RollViewModel(TranscriptSegment seg, bool animate, Later later)
        {
            var roll = seg.Roll ?? new RollInfo();
            switch (roll.Outcome)
            {
                case RollOutcome.CriticalSuccess: Outcome = "crit-success"; break;
                case RollOutcome.Success: Outcome = "success"; break;
                case RollOutcome.CriticalFailure: Outcome = "crit-failure"; break;
                default: Outcome = "failure"; break;
            }
            int total = StoryText.RollTotal(roll);
            Total = total > 0 ? total.ToString(CultureInfo.InvariantCulture) : "?";
            Label = DisplayText.Plain(roll.Label);
            string against = StoryText.RollAgainst(roll);
            Against = DisplayText.Plain(against);
            HasAgainst = against.Length > 0;
            Verdict = DisplayText.Plain(StoryText.VerdictWords(roll.Verdict).ToUpperInvariant());
            if (!animate) { return; }
            _entering = true;
            later(delegate { Entering = false; }, 20);
            VaultSfx.Play(VaultSfx.Cue.Dice);
            later(delegate { VaultSfx.Play(roll.Success ? VaultSfx.Cue.Success : VaultSfx.Cue.Fail); }, 520);
        }

        public override string Template { get { return "Story/Roll"; } }
        /// <summary>"crit-success", "success", "failure" or "crit-failure": the card's colours are a USS class per outcome.</summary>
        [CreateProperty] public string Outcome { get; private set; }
        [CreateProperty] public string Total { get; private set; }
        [CreateProperty] public string Label { get; private set; }
        [CreateProperty] public string Against { get; private set; }
        [CreateProperty] public bool HasAgainst { get; private set; }
        [CreateProperty] public string Verdict { get; private set; }
        /// <summary>True for the first moments, so the transition into the card has a state to start from.</summary>
        [CreateProperty] public bool Entering { get { return _entering; } private set { Set(ref _entering, value); } }
    }

    /// <summary>One line inside a strip: a tool call, or the DM's notes (the template differs).</summary>
    public sealed class ActivityLineViewModel : ViewModel, ITemplated
    {
        public ActivityLineViewModel(TranscriptSegment seg)
        {
            IsNotes = seg.Kind == SegmentKind.Notes;
            Text = IsNotes ? DisplayText.Rich(seg.Text) : DisplayText.Plain(StoryText.ActivityText(seg.Text));
        }

        public bool IsNotes { get; private set; }
        public string Template { get { return IsNotes ? "Story/ActivityNotes" : "Story/ActivityLine"; } }
        [CreateProperty] public string Text { get; private set; }
    }

    /// <summary>Consecutive tool activity, folded into one expandable strip.</summary>
    public sealed class ActivityStripViewModel : StoryItemViewModel
    {
        private readonly List<ActivityLineViewModel> _all = new List<ActivityLineViewModel>();
        private List<ActivityLineViewModel> _lines = new List<ActivityLineViewModel>();
        private string _title = string.Empty;
        private bool _open;

        public ActivityStripViewModel()
        {
            Toggle = delegate { Open = !Open; };
        }

        public override string Template { get { return "Story/Activity"; } }
        public int Count { get { return _all.Count; } }
        [CreateProperty] public string Title { get { return _title; } private set { Set(ref _title, value); } }
        [CreateProperty] public bool Open { get { return _open; } private set { Set(ref _open, value); } }
        [CreateProperty] public List<ActivityLineViewModel> Lines { get { return _lines; } private set { SetList(ref _lines, value); } }
        [CreateProperty] public Action Toggle { get; private set; }

        public ActivityLineViewModel Add(TranscriptSegment seg)
        {
            var line = new ActivityLineViewModel(seg);
            _all.Add(line);
            Rebuild();
            return line;
        }

        public void Remove(ActivityLineViewModel line)
        {
            _all.Remove(line);
            Rebuild();
        }

        private void Rebuild()
        {
            int calls = 0;
            bool notes = false;
            foreach (var l in _all) { if (l.IsNotes) { notes = true; } else { calls++; } }
            string title = calls > 0 ? "THE DM CONSULTS THE LEDGER · " + calls : "THE DM'S NOTES";
            if (calls > 0 && notes) { title += " · NOTES"; }
            Title = DisplayText.Plain(title);
            Lines = new List<ActivityLineViewModel>(_all);
        }
    }

    /// <summary>
    /// The story: one item per transcript segment, kept in step through the transcript's Added/Removed/Updated
    /// events (no diffing, no full rebuilds; the 400-segment cap trims one item at a time). Consecutive tool
    /// activity folds into a strip. The view follows new text only while the reader is at the bottom; a LATEST
    /// button appears when they've scrolled up and more arrives.
    /// </summary>
    public sealed class StoryLogViewModel : ViewModel
    {
        private sealed class Entry
        {
            /// <summary>Set for story items; null for activity lines.</summary>
            public StoryItemViewModel Item;
            /// <summary>For activity lines: the strip they live in (shared by consecutive lines) and the line.</summary>
            public ActivityStripViewModel Strip;
            public ActivityLineViewModel Line;
        }

        private readonly VaultAppState _s;
        private readonly Later _later;
        private readonly ObservableCollection<StoryItemViewModel> _items = new ObservableCollection<StoryItemViewModel>();
        private readonly List<Entry> _entries = new List<Entry>();
        private bool _pinned = true;
        private bool _showJump;
        private int _scrollTick;

        public StoryLogViewModel(VaultAppState state, Later later)
        {
            _s = state;
            _later = later;
            Jump = Pin;
            var transcript = state.Transcript;
            foreach (var seg in transcript.Segments) { Append(seg, false); }
            // Restored history arrives in a burst: no dice tumbling or sounds for rolls made last week.
            transcript.Added += OnAdded;
            transcript.Removed += OnRemoved;
            transcript.Updated += OnUpdated;
            transcript.Cleared += OnCleared;
        }

        [CreateProperty] public IList Items { get { return _items; } }
        public ObservableCollection<StoryItemViewModel> Stories { get { return _items; } }
        /// <summary>One per transcript segment (a strip holds several).</summary>
        public int EntryCount { get { return _entries.Count; } }
        public bool Pinned { get { return _pinned; } }
        [CreateProperty] public bool ShowJump { get { return _showJump; } private set { Set(ref _showJump, value); } }
        /// <summary>Goes up when the view should snap to the newest line.</summary>
        [CreateProperty] public int ScrollTick { get { return _scrollTick; } private set { Set(ref _scrollTick, value); } }
        [CreateProperty] public Action Jump { get; private set; }

        /// <summary>The reader scrolled (or came back to the bottom). Only the reader moves the pin; content growth never does.</summary>
        public void SetPinned(bool pinned)
        {
            _pinned = pinned;
            if (pinned) { ShowJump = false; }
        }

        /// <summary>Snap to the newest line and follow from here on.</summary>
        public void Pin()
        {
            SetPinned(true);
            ScrollTick = ScrollTick + 1;
        }

        public override void Dispose()
        {
            var transcript = _s.Transcript;
            transcript.Added -= OnAdded;
            transcript.Removed -= OnRemoved;
            transcript.Updated -= OnUpdated;
            transcript.Cleared -= OnCleared;
            base.Dispose();
        }

        private void OnAdded(TranscriptSegment seg) { Append(seg, _s.FxEnabled && !seg.Restored); }

        private void OnCleared()
        {
            _entries.Clear();
            _items.Clear();
        }

        private void Append(TranscriptSegment seg, bool animate)
        {
            if (seg.Kind == SegmentKind.Player) { Pin(); }
            var entry = new Entry();
            if (StoryText.IsActivity(seg))
            {
                var strip = _entries.Count > 0 ? _entries[_entries.Count - 1].Strip : null;
                if (strip == null)
                {
                    strip = new ActivityStripViewModel();
                    _items.Add(strip);
                }
                entry.Strip = strip;
                entry.Line = strip.Add(seg);
            }
            else
            {
                entry.Item = StoryItemViewModel.For(seg, animate, _later, _s);
                _items.Add(entry.Item);
            }
            _entries.Add(entry);
            NoteNewContent();
        }

        private void NoteNewContent()
        {
            if (!_pinned) { ShowJump = true; }
        }

        private void OnRemoved(int index, TranscriptSegment seg)
        {
            if (index < 0 || index >= _entries.Count) { return; }
            var entry = _entries[index];
            _entries.RemoveAt(index);
            if (entry.Strip == null) { _items.Remove(entry.Item); return; }
            entry.Strip.Remove(entry.Line);
            if (entry.Strip.Count == 0) { _items.Remove(entry.Strip); }
        }

        private void OnUpdated(TranscriptSegment seg)
        {
            int index = IndexOf(seg);
            if (index < 0) { return; }
            var entry = _entries[index];
            // Streaming text grows in place; a kind change (aside promoted to narration, a cut-off reply demoted to
            // aside) swaps the item.
            if (entry.Strip == null)
            {
                if (entry.Item.Accepts(seg)) { entry.Item.Update(seg); }
                else
                {
                    var replacement = StoryItemViewModel.For(seg, false, _later, _s);
                    int at = _items.IndexOf(entry.Item);
                    if (at >= 0) { _items[at] = replacement; }
                    entry.Item = replacement;
                }
            }
            NoteNewContent();
        }

        private int IndexOf(TranscriptSegment seg)
        {
            var segments = _s.Transcript.Segments;
            // Updates hit the newest segments: search from the end.
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(segments[i], seg)) { return i < _entries.Count ? i : -1; }
            }
            return -1;
        }
    }
}
