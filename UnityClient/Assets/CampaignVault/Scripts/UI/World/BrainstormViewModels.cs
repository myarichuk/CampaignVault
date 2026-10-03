using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Table;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.World
{
    /// <summary>One entry of the brainstorm conversation: a message or a divider where the setup moved on.</summary>
    public abstract class ChatItemViewModel : ViewModel, IKeyed, ITemplated
    {
        private bool _gone;

        protected ChatItemViewModel(string key) { Key = key; }

        public string Key { get; private set; }
        public abstract string Template { get; }

        /// <summary>No longer sent to the DM: the conversation is over its limit and this message fell out of it.</summary>
        [CreateProperty] public bool Gone { get { return _gone; } protected set { Set(ref _gone, value); } }
    }

    public sealed class ChatDividerViewModel : ChatItemViewModel
    {
        private string _text = string.Empty;

        public ChatDividerViewModel(string key) : base(key) { }

        public override string Template { get { return "Onboarding/ChatDivider"; } }

        [CreateProperty] public string Text { get { return _text; } private set { Set(ref _text, value); } }

        public void Update(string message, bool gone)
        {
            // Where the setup moved on, so the chat reads as one conversation across the questions.
            string where = BuilderAdvisor.IsMarker(message) ? message : "NEXT QUESTION · " + message;
            Text = DisplayText.Plain(where.ToUpperInvariant());
            Gone = gone;
        }
    }

    public sealed class ChatBubbleViewModel : ChatItemViewModel
    {
        private string _speaker = string.Empty;
        private List<string> _paragraphs = new List<string>();
        private bool _mine;
        private bool _canToggle;
        private string _toggleLabel = "SHOW ALL";

        public ChatBubbleViewModel(string key, Action toggle) : base(key) { Toggle = toggle; }

        public override string Template { get { return "Onboarding/ChatBubble"; } }

        [CreateProperty] public string Speaker { get { return _speaker; } private set { Set(ref _speaker, value); } }
        [CreateProperty] public List<string> Paragraphs { get { return _paragraphs; } private set { SetList(ref _paragraphs, value); } }
        [CreateProperty] public bool Mine { get { return _mine; } private set { Set(ref _mine, value); } }
        /// <summary>A long message of yours folds; this unfolds or refolds it.</summary>
        [CreateProperty] public bool CanToggle { get { return _canToggle; } private set { Set(ref _canToggle, value); } }
        [CreateProperty] public string ToggleLabel { get { return _toggleLabel; } private set { Set(ref _toggleLabel, value); } }
        [CreateProperty] public Action Toggle { get; private set; }

        public void Update(string role, string text, bool gone, bool expanded)
        {
            bool mine = role == "user";
            string folded = mine && !expanded ? OnboardingText.Fold(text, OnboardingText.FoldChars, OnboardingText.FoldLines) : null;
            Mine = mine;
            Gone = gone;
            Speaker = (mine ? "YOU" : "THE DM") + (gone ? " · NO LONGER SENT TO THE DM" : string.Empty);
            // Rendered to chunks; the string list is the same while the text is, so nothing repaints.
            var chunks = DisplayText.RichChunks(folded ?? text);
            Paragraphs = SameAs(_paragraphs, chunks) ? _paragraphs : chunks;
            CanToggle = mine && (folded != null || expanded);
            ToggleLabel = folded != null ? "SHOW ALL" : "SHOW LESS";
        }

        private static bool SameAs(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) { return false; }
            for (int i = 0; i < a.Count; i++) { if (a[i] != b[i]) { return false; } }
            return true;
        }
    }

    /// <summary>A side chat with the model about the current question (Templates/Onboarding/BrainstormPage.uxml); its write-up becomes the draft answer.</summary>
    public sealed class BrainstormPageViewModel : ViewModel, ITemplated
    {
        private readonly HashSet<int> _expanded = new HashSet<int>();
        private List<ChatItemViewModel> _items = new List<ChatItemViewModel>();
        private string _question = string.Empty;
        private bool _showIntro;
        private string _trimmed = string.Empty;
        private bool _busy;
        private string _error = string.Empty;
        private int _scroll;
        private int _seenCount = -1;
        private bool _seenBusy;

        public string Template { get { return "Onboarding/BrainstormPage"; } }

        [CreateProperty] public string Question { get { return _question; } private set { Set(ref _question, value); } }
        [CreateProperty] public bool ShowIntro { get { return _showIntro; } private set { Set(ref _showIntro, value); } }
        /// <summary>Set once the conversation is over its limit and the DM no longer sees its middle.</summary>
        [CreateProperty] public string Trimmed { get { return _trimmed; } private set { Set(ref _trimmed, value); } }
        [CreateProperty] public List<ChatItemViewModel> Items { get { return _items; } private set { SetList(ref _items, value); } }
        [CreateProperty] public bool Busy { get { return _busy; } private set { Set(ref _busy, value); } }
        private readonly ErrorCardSlot _errorSlot = new ErrorCardSlot();
        private object _card;
        [CreateProperty] public object Card { get { return _card; } private set { Set(ref _card, value); } }
        [CreateProperty] public string Error { get { return _error; } private set { Set(ref _error, value); } }
        /// <summary>Goes up whenever the newest message should be brought into view.</summary>
        [CreateProperty] public int ScrollTick { get { return _scroll; } private set { Set(ref _scroll, value); } }

        public void Update(OnboardingState ob)
        {
            var chat = ob.BrainstormChat;
            if (chat.Count == 0) { _expanded.Clear(); }
            Question = DisplayText.Plain(ob.Question.Text);
            ShowIntro = !OnboardingBrainstorm.HasTalk(chat);
            var dropped = OnboardingBrainstorm.Dropped(chat, OnboardingBrainstorm.MaxConversationChars);
            Trimmed = dropped.Count == 0 ? string.Empty : DisplayText.Plain(
                "This conversation is over its " + OnboardingText.Count(OnboardingBrainstorm.MaxConversationChars) + "-character limit, so the DM no longer sees "
                + (dropped.Count == 1 ? "1 earlier message" : dropped.Count + " earlier messages") + " (marked below). Your first message and the latest ones are still sent. "
                + "WRITE IT UP now to keep what you settled, or restate what matters in your next message.");
            Busy = ob.BrainstormBusy;
            var card = _errorSlot.Sync(ob.BrainstormFailure, ob.BrainstormError);
            Card = card;
            Error = card != null ? string.Empty : DisplayText.Plain(ob.BrainstormError);

            var indexes = new List<int>();
            for (int i = 0; i < chat.Count; i++) { indexes.Add(i); }
            Items = ItemList.Sync(_items, indexes,
                delegate (int i) { return (chat[i].Key == OnboardingBrainstorm.MarkerRole ? "d" : "m") + i; },
                delegate (int i)
                {
                    if (chat[i].Key == OnboardingBrainstorm.MarkerRole) { return (ChatItemViewModel)new ChatDividerViewModel("d" + i); }
                    int index = i;
                    return new ChatBubbleViewModel("m" + i, delegate { if (!_expanded.Remove(index)) { _expanded.Add(index); } Update(ob); });
                },
                delegate (ChatItemViewModel item, int i)
                {
                    bool gone = dropped.Contains(i);
                    var divider = item as ChatDividerViewModel;
                    if (divider != null) { divider.Update(chat[i].Value, gone); }
                    else { ((ChatBubbleViewModel)item).Update(chat[i].Key, chat[i].Value, gone, _expanded.Contains(i)); }
                });

            if (chat.Count != _seenCount || ob.BrainstormBusy != _seenBusy)
            {
                _seenCount = chat.Count;
                _seenBusy = ob.BrainstormBusy;
                ScrollTick = _scroll + 1;
            }
        }
    }

    /// <summary>The reply box under the chat (Templates/Onboarding/Composer.uxml), docked so a long draft scrolls inside it.</summary>
    public sealed class BrainstormComposerViewModel : ViewModel, ITemplated
    {
        private readonly OnboardingState _ob;
        private readonly Action _changed;
        private string _draft = string.Empty;
        private string _seenDraft;
        private string _placeholder = string.Empty;
        private bool _enabled = true;
        private string _count = string.Empty;
        private bool _over;
        private string _conversation = string.Empty;
        private bool _conversationOver;
        private bool _conversationVisible;
        private int _focus;

        public BrainstormComposerViewModel(OnboardingState ob, Action changed)
        {
            _ob = ob;
            _changed = changed;
        }

        public string Template { get { return "Onboarding/Composer"; } }

        [CreateProperty]
        public string Draft
        {
            get { return _draft; }
            set
            {
                if (!Set(ref _draft, value ?? string.Empty)) { return; }
                _seenDraft = _draft;
                _ob.BrainstormDraft = _draft;
                Paint();
            }
        }

        [CreateProperty] public string Placeholder { get { return _placeholder; } private set { Set(ref _placeholder, value); } }
        [CreateProperty] public bool Enabled { get { return _enabled; } private set { Set(ref _enabled, value); } }
        [CreateProperty] public int FocusRequest { get { return _focus; } private set { Set(ref _focus, value); } }
        /// <summary>"1,204 / 16,000", or why the message can't be sent.</summary>
        [CreateProperty] public string Count { get { return _count; } private set { Set(ref _count, value); } }
        [CreateProperty] public bool Over { get { return _over; } private set { Set(ref _over, value); } }
        [CreateProperty] public string Conversation { get { return _conversation; } private set { Set(ref _conversation, value); } }
        [CreateProperty] public bool ConversationOver { get { return _conversationOver; } private set { Set(ref _conversationOver, value); } }
        /// <summary>Only worth the space once the chat is a real share of the budget.</summary>
        [CreateProperty] public bool ConversationVisible { get { return _conversationVisible; } private set { Set(ref _conversationVisible, value); } }

        public void Update()
        {
            bool talked = OnboardingBrainstorm.HasTalk(_ob.BrainstormChat);
            Placeholder = !talked ? "e.g. something with sea caves and smugglers" : "reply";
            Enabled = !_ob.BrainstormBusy;
            if (_ob.BrainstormDraft != _seenDraft)
            {
                _seenDraft = _ob.BrainstormDraft;
                Set(ref _draft, _ob.BrainstormDraft, "Draft");
                if (_focus == 0) { FocusRequest = 1; }
            }
            int used = OnboardingBrainstorm.ConversationChars(_ob.BrainstormChat);
            Conversation = "CONVERSATION " + OnboardingText.Count(used) + " / " + OnboardingText.Count(OnboardingBrainstorm.MaxConversationChars);
            ConversationOver = OnboardingBrainstorm.Dropped(_ob.BrainstormChat, OnboardingBrainstorm.MaxConversationChars).Count > 0;
            ConversationVisible = used > OnboardingBrainstorm.MaxConversationChars / 2;
            Paint();
        }

        private void Paint()
        {
            int n = _draft.Length;
            int max = OnboardingBrainstorm.MaxMessageChars;
            Over = n > max;
            Count = Over ? "TOO LONG TO SEND · " + OnboardingText.Count(n) + " / " + OnboardingText.Count(max) : OnboardingText.Count(n) + " / " + OnboardingText.Count(max);
            _changed();
        }

        public bool CanSend { get { return !_ob.BrainstormBusy && !_over; } }
    }
}
