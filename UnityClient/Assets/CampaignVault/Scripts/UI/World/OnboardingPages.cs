using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Properties;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Table;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.World
{
    /// <summary>Words and numbers the onboarding pages share.</summary>
    public static class OnboardingText
    {
        /// <summary>A pasted page of lore folds to this much in the chat history.</summary>
        public const int FoldChars = 600;
        public const int FoldLines = 8;

        public static string Count(int n) { return n.ToString("N0", CultureInfo.InvariantCulture); }

        /// <summary>The first lines of a long message, cut at a word; null when it is short enough to show whole.</summary>
        public static string Fold(string text, int maxChars, int maxLines)
        {
            if (string.IsNullOrEmpty(text)) { return null; }
            int cut = text.Length;
            int lines = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' && ++lines > maxLines) { cut = i; break; }
            }
            if (cut > maxChars) { cut = maxChars; }
            if (cut >= text.Length) { return null; }
            int space = text.LastIndexOfAny(new[] { ' ', '\n' }, cut - 1, cut);
            if (space > maxChars / 2) { cut = space; }
            return text.Substring(0, cut).TrimEnd() + " …";
        }

        /// <summary>Server option ids read as plain words on the buttons.</summary>
        public static string ChoiceLabel(string option)
        {
            switch (option)
            {
                case "describe-now": return "I'll describe them";
                case "dm-pregenerates": return "The DM makes them, I approve";
                case "build-at-table": return "Build them with the DM, step by step";
                case "user-provided": return "I have a plot idea";
                case "generated-surprise": return "Surprise me";
                case "generated-with-direction": return "Generate it, with my direction";
                case "party-existing": return "Party, existing world";
                case "party-homebrew": return "Party, homebrew world";
                default: return option;
            }
        }
    }

    /// <summary>A line with an icon: working, failed, or waiting on the player to finalize.</summary>
    public sealed class StatusPageViewModel : ViewModel, ITemplated
    {
        private string _notice = string.Empty;
        private string _icon = "seal";
        private string _error = string.Empty;

        public string Template { get { return "Onboarding/StatusPage"; } }

        [CreateProperty] public string Notice { get { return _notice; } private set { Set(ref _notice, value); } }
        [CreateProperty] public string NoticeIcon { get { return _icon; } private set { Set(ref _icon, value); } }
        [CreateProperty] public string Error { get { return _error; } private set { Set(ref _error, value); } }

        public void Show(string icon, string notice, string error)
        {
            NoticeIcon = icon;
            Notice = DisplayText.Plain(notice);
            Error = DisplayText.Plain(error);
        }
    }

    /// <summary>Name the campaign and choose its rules, or resume one you started.</summary>
    public sealed class StartPageViewModel : ViewModel, ITemplated
    {
        private readonly VaultController _c;
        private readonly List<ChoiceViewModel> _systems = new List<ChoiceViewModel>();
        private string _name = string.Empty;
        private string _display = string.Empty;
        private string _resume = string.Empty;
        private string _system = OnboardingState.SystemOptions[0];
        private string _error = string.Empty;

        public StartPageViewModel(VaultController controller)
        {
            _c = controller;
            for (int i = 0; i < OnboardingState.SystemOptions.Length; i++)
            {
                string option = OnboardingState.SystemOptions[i];
                _systems.Add(new ChoiceViewModel(option, OnboardingState.SystemLabels[i].ToUpperInvariant(), "d20", false, delegate { System = option; }, "system-" + option));
            }
            Resume = delegate { _c.Run(_c.ResumeOnboarding(_resume)); };
            PaintSystems();
        }

        public string Template { get { return "Onboarding/StartPage"; } }

        [CreateProperty] public string Name { get { return _name; } set { if (Set(ref _name, value ?? string.Empty)) { Notify("SlugPreview"); } } }
        [CreateProperty] public string Display { get { return _display; } set { Set(ref _display, value ?? string.Empty); } }
        [CreateProperty] public string ResumeSlug { get { return _resume; } set { Set(ref _resume, value ?? string.Empty); } }
        [CreateProperty] public string System { get { return _system; } private set { if (Set(ref _system, value)) { PaintSystems(); } } }
        [CreateProperty] public List<ChoiceViewModel> Systems { get { return _systems; } }
        [CreateProperty] public string SlugPreview { get { string slug = VaultController.Slugify(_name); return slug.Length > 0 ? "campaign id: " + slug : string.Empty; } }
        [CreateProperty] public string Error { get { return _error; } private set { Set(ref _error, value); } }
        [CreateProperty] public Action Resume { get; private set; }

        public void Begin() { _c.Run(_c.BeginOnboarding(_name, _display, _system)); }

        public void Update(OnboardingState ob) { Error = DisplayText.Plain(ob.Error); }

        private void PaintSystems()
        {
            for (int i = 0; i < _systems.Count; i++) { _systems[i].Update(OnboardingState.SystemLabels[i].ToUpperInvariant(), "d20", _systems[i].Key == _system); }
        }
    }

    /// <summary>What every question page opens with: which campaign, how far along, the question and its help.</summary>
    public abstract class QuestionPageViewModel : ViewModel, ITemplated
    {
        private string _caption = string.Empty;
        private string _progress = string.Empty;
        private string _text = string.Empty;
        private string _help = string.Empty;

        public abstract string Template { get; }

        [CreateProperty] public string Caption { get { return _caption; } private set { Set(ref _caption, value); } }
        [CreateProperty] public string Progress { get { return _progress; } private set { Set(ref _progress, value); } }
        [CreateProperty] public string Text { get { return _text; } private set { Set(ref _text, value); } }
        [CreateProperty] public string Help { get { return _help; } private set { Set(ref _help, value); } }

        /// <summary>The line under the question. The server's help, unless a page words it for its own controls.</summary>
        protected virtual string HelpOf(OnboardingQuestion q) { return q.Help; }

        public virtual void Update(OnboardingState ob)
        {
            var q = ob.Question;
            Caption = DisplayText.Plain(ob.Slug.ToUpperInvariant() + " · " + ob.Answered + " ANSWERED");
            Progress = DisplayText.Plain(ob.Progress);
            Text = DisplayText.Plain(q.Text);
            Help = DisplayText.Plain(HelpOf(q));
        }

        public static QuestionPageViewModel For(OnboardingState ob, VaultController controller)
        {
            QuestionPageViewModel page;
            switch (ob.Question.Type)
            {
                case AnswerType.Choice: page = new ChoicePageViewModel(controller); break;
                case AnswerType.YesNo: page = new YesNoPageViewModel(controller); break;
                case AnswerType.Party: page = new PartyPageViewModel(controller); break;
                default: page = new TextPageViewModel(ob, controller); break;
            }
            page.Update(ob);
            return page;
        }
    }

    public sealed class ChoicePageViewModel : QuestionPageViewModel
    {
        private readonly VaultController _c;
        private List<ChoiceViewModel> _options = new List<ChoiceViewModel>();

        public ChoicePageViewModel(VaultController controller) { _c = controller; }

        public override string Template { get { return "Onboarding/ChoicePage"; } }

        [CreateProperty] public List<ChoiceViewModel> Options { get { return _options; } private set { SetList(ref _options, value); } }

        public override void Update(OnboardingState ob)
        {
            base.Update(ob);
            Options = ItemList.Sync(_options, ob.Question.Options, delegate (string o) { return o; },
                delegate (string o) { string answer = o; return new ChoiceViewModel(answer, string.Empty, null, false, delegate { _c.Run(_c.SubmitOnboardingAnswer(answer)); }, "answer-" + answer); },
                delegate (ChoiceViewModel vm, string o) { vm.Update(DisplayText.Plain(OnboardingText.ChoiceLabel(o)), null, false); });
        }
    }

    public sealed class YesNoPageViewModel : QuestionPageViewModel
    {
        public YesNoPageViewModel(VaultController controller)
        {
            Yes = delegate { controller.Run(controller.SubmitOnboardingAnswer("yes")); };
            No = delegate { controller.Run(controller.SubmitOnboardingAnswer("no")); };
        }

        public override string Template { get { return "Onboarding/YesNoPage"; } }

        [CreateProperty] public Action Yes { get; private set; }
        [CreateProperty] public Action No { get; private set; }
    }

    /// <summary>A free-text, list or number answer, with the brainstorm helpers.</summary>
    public sealed class TextPageViewModel : QuestionPageViewModel
    {
        private readonly OnboardingState _ob;
        private readonly VaultController _c;
        private readonly bool _list;
        private readonly bool _number;
        private string _draft = string.Empty;
        private string _seenDraft;
        private bool _multiline;
        private string _count = string.Empty;
        private bool _countVisible;
        private bool _over;
        private bool _canBrainstorm;
        private string _brainstormLabel = string.Empty;
        private bool _brainstormEnabled = true;
        private bool _showWriteUp;
        private string _writeUpLabel = string.Empty;
        private string _writeUpTip = string.Empty;
        private bool _busy;
        private string _brainstormError = string.Empty;
        private int _focus;

        public TextPageViewModel(OnboardingState ob, VaultController controller)
        {
            _ob = ob;
            _c = controller;
            _list = ob.Question.Type == AnswerType.List;
            _number = ob.Question.Type == AnswerType.Number;
            Placeholder = _list ? "one entry per line" : _number ? "a number" : "your answer";
            Brainstorm = delegate { controller.OpenBrainstorm(); };
            WriteUp = delegate { controller.Run(controller.WriteUpBrainstorm()); };
            OverNote = DisplayText.Plain("Over " + OnboardingText.Count(OnboardingBrainstorm.MaxAnswerChars) + " characters. It's kept whole, but every later step re-reads your answers, so trimming it keeps setup quicker and cheaper.");
        }

        public override string Template { get { return "Onboarding/TextPage"; } }

        [CreateProperty] public string Placeholder { get; private set; }
        [CreateProperty] public string OverNote { get; private set; }
        [CreateProperty] public Action Brainstorm { get; private set; }
        [CreateProperty] public Action WriteUp { get; private set; }

        /// <summary>The answer being typed. Kept in state too, so opening the brainstorm and coming back loses nothing.</summary>
        [CreateProperty]
        public string Draft
        {
            get { return _draft; }
            set
            {
                if (!Set(ref _draft, value ?? string.Empty)) { return; }
                _seenDraft = _draft;
                _ob.Draft = _draft;
                PaintCount();
            }
        }

        /// <summary>A brainstormed write-up can run to a paragraph: give it room.</summary>
        [CreateProperty] public bool Multiline { get { return _multiline; } private set { Set(ref _multiline, value); } }
        [CreateProperty] public int FocusRequest { get { return _focus; } private set { Set(ref _focus, value); } }
        /// <summary>"1,204 / 6,000", shown once the answer is half way to the limit.</summary>
        [CreateProperty] public string Count { get { return _count; } private set { Set(ref _count, value); } }
        [CreateProperty] public bool CountVisible { get { return _countVisible; } private set { Set(ref _countVisible, value); } }
        [CreateProperty] public bool Over { get { return _over; } private set { Set(ref _over, value); } }
        [CreateProperty] public bool CanBrainstorm { get { return _canBrainstorm; } private set { Set(ref _canBrainstorm, value); } }
        [CreateProperty] public string BrainstormLabel { get { return _brainstormLabel; } private set { Set(ref _brainstormLabel, value); } }
        [CreateProperty] public bool BrainstormEnabled { get { return _brainstormEnabled; } private set { Set(ref _brainstormEnabled, value); } }
        [CreateProperty] public bool ShowWriteUp { get { return _showWriteUp; } private set { Set(ref _showWriteUp, value); } }
        [CreateProperty] public string WriteUpLabel { get { return _writeUpLabel; } private set { Set(ref _writeUpLabel, value); } }
        [CreateProperty] public string WriteUpTooltip { get { return _writeUpTip; } private set { Set(ref _writeUpTip, value); } }
        [CreateProperty] public bool Busy { get { return _busy; } private set { Set(ref _busy, value); } }
        private readonly ErrorCardSlot _brainstormSlot = new ErrorCardSlot();
        private object _brainstormCard;
        [CreateProperty] public object BrainstormCard { get { return _brainstormCard; } private set { Set(ref _brainstormCard, value); } }
        [CreateProperty] public string BrainstormError { get { return _brainstormError; } private set { Set(ref _brainstormError, value); } }

        /// <summary>What ANSWER sends.</summary>
        public string Answer { get { return _list ? VaultController.FormatListAnswer(_draft) : _draft.Trim(); } }

        public override void Update(OnboardingState ob)
        {
            base.Update(ob);
            // Take the draft from state only when it changed there (a write-up landed, or this is the first look),
            // never over what's being typed.
            if (ob.Draft != _seenDraft)
            {
                _seenDraft = ob.Draft;
                Set(ref _draft, ob.Draft, "Draft");
                Multiline = _list || ob.Draft.Length > 80;
                FocusRequest = _focus + 1;
                PaintCount();
            }
            bool supports = OnboardingBrainstorm.Supports(ob.Question);
            bool talked = OnboardingBrainstorm.HasTalk(ob.BrainstormChat);
            bool replaces = ob.Draft.Trim().Length > 0;
            CanBrainstorm = supports;
            BrainstormLabel = talked ? "CONTINUE THE CONVERSATION" : "BRAINSTORM WITH THE DM";
            BrainstormEnabled = !ob.BrainstormBusy;
            ShowWriteUp = supports && talked;
            WriteUpLabel = replaces ? "REWRITE FROM OUR CONVERSATION" : "WRITE IT UP FROM OUR CONVERSATION";
            WriteUpTooltip = replaces
                ? "The DM drafts this answer from what you've discussed so far. It replaces what's in the field now."
                : "The DM drafts this answer from what you've discussed so far, for you to edit.";
            Busy = ob.BrainstormBusy;
            var card = _brainstormSlot.Sync(ob.BrainstormFailure, ob.BrainstormError);
            BrainstormCard = card;
            BrainstormError = card != null ? string.Empty : DisplayText.Plain(ob.BrainstormError);
        }

        private void PaintCount()
        {
            int n = _draft.Length;
            int max = OnboardingBrainstorm.MaxAnswerChars;
            // Long answers are kept whole; past the limit the page says why trimming helps instead of cutting it.
            Count = OnboardingText.Count(n) + " / " + OnboardingText.Count(max);
            CountVisible = n > max / 2;
            Over = n > max;
        }
    }
}
