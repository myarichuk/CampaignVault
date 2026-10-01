using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.World
{
    /// <summary>The campaign is made: its summary and what to do next.</summary>
    public sealed class DonePageViewModel : ViewModel, ITemplated
    {
        private List<string> _summary = new List<string>();
        private List<string> _steps = new List<string>();

        public string Template { get { return "Onboarding/DonePage"; } }

        [CreateProperty] public List<string> Summary { get { return _summary; } private set { SetList(ref _summary, value); } }
        [CreateProperty] public List<string> Steps { get { return _steps; } private set { SetList(ref _steps, value); } }

        public void Update(OnboardingState ob)
        {
            Summary = DisplayText.RichChunks(ob.DoneSummary);
            var steps = new List<string>();
            foreach (string step in ob.NextSteps) { steps.Add(DisplayText.Rich("- " + step)); }
            Steps = steps;
        }
    }

    /// <summary>
    /// Guided campaign creation (Templates/Onboarding/Onboarding.uxml): name and rules up front, then the server's
    /// questions one at a time. The page is whatever the setup is doing (a start form, a question, the brainstorm
    /// chat, a wait); the foot buttons and the docked reply box follow it.
    /// </summary>
    public sealed class OnboardingViewModel : ViewModel
    {
        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private readonly StartPageViewModel _start;
        private readonly StatusPageViewModel _status = new StatusPageViewModel();
        private readonly BrainstormPageViewModel _brainstorm = new BrainstormPageViewModel();
        private readonly DonePageViewModel _done = new DonePageViewModel();
        private readonly BrainstormComposerViewModel _composer;
        private readonly ActionViewModel _begin;
        private readonly ActionViewModel _answer;
        private readonly ActionViewModel _finalize;
        private readonly ActionViewModel _send;
        private readonly ActionViewModel _writeUp;
        private readonly ActionViewModel _back;
        private readonly ActionViewModel _toTable;
        private readonly ActionViewModel _seed;
        private readonly ActionViewModel _restart;
        private QuestionPageViewModel _question;
        private OnboardingQuestion _questionFor;
        private ViewModel _page;
        private ViewModel _dock;
        private List<ActionViewModel> _actions = new List<ActionViewModel>();
        private int _scroll;

        public OnboardingViewModel(VaultAppState state, VaultController controller, Action close)
        {
            _s = state;
            _c = controller;
            _start = new StartPageViewModel(controller);
            _composer = new BrainstormComposerViewModel(state.Onboarding, UpdateSend);
            _begin = new ActionViewModel("begin", "onboarding-begin", _start.Begin, primary: true).Show("BEGIN", "seal");
            _answer = new ActionViewModel("answer", "onboarding-answer", delegate { if (_question is TextPageViewModel) { Submit(((TextPageViewModel)_question).Answer); } }, primary: true).Show("ANSWER", "chevron");
            _finalize = new ActionViewModel("finalize", "onboarding-finalize", delegate { controller.Run(controller.FinalizeOnboarding()); }, primary: true).Show("FINALIZE", "seal");
            _send = new ActionViewModel("send", "onboarding-send", delegate { controller.Run(controller.SendBrainstorm(_s.Onboarding.BrainstormDraft)); }, primary: true).Show("SEND", "chevron");
            _writeUp = new ActionViewModel("writeup", "onboarding-writeup", delegate { controller.Run(controller.WriteUpBrainstorm()); });
            _back = new ActionViewModel("back", "onboarding-back", controller.CloseBrainstorm, ghost: true).Show("BACK TO THE QUESTION", "chevron");
            _toTable = new ActionViewModel("table", "onboarding-table", close, ghost: true).Show("TO THE TABLE");
            _seed = new ActionViewModel("seed", "onboarding-seed", delegate { if (controller.SeedWorldThroughDm()) { close(); } }, primary: true)
                .Show("SEED THE WORLD", "spark", true, "The Dungeon Master creates your characters and the starter places, people and quests from your answers. The first session opens as soon as the party exists.");
            _restart = new ActionViewModel("restart", "onboarding-restart", controller.ResetOnboarding).Show("START OVER", "refresh");
            Watch(state, StateArea.Onboarding);
        }

        [CreateProperty] public ViewModel Page { get { return _page; } private set { Set(ref _page, value); } }
        /// <summary>The reply box docked under the brainstorm chat; nothing otherwise.</summary>
        [CreateProperty] public ViewModel Dock { get { return _dock; } private set { Set(ref _dock, value); } }
        [CreateProperty] public List<ActionViewModel> Actions { get { return _actions; } private set { SetList(ref _actions, value); } }
        /// <summary>Goes up whenever the newest brainstorm message should be brought into view.</summary>
        [CreateProperty] public int ScrollTick { get { return _scroll; } private set { Set(ref _scroll, value); } }

        public override void Refresh()
        {
            var ob = _s.Onboarding;
            ViewModel dock = null;
            var actions = new List<ActionViewModel>();
            switch (ob.Phase)
            {
                case OnboardingPhase.Idle:
                    _start.Update(ob);
                    Page = _start;
                    actions.Add(_begin);
                    break;
                case OnboardingPhase.Working:
                    _status.Show("seal", string.IsNullOrEmpty(ob.Status) ? "Working…" : ob.Status, string.Empty);
                    Page = _status;
                    break;
                case OnboardingPhase.Question:
                    if (ob.Brainstorming)
                    {
                        _brainstorm.Update(ob);
                        _composer.Update();
                        Page = _brainstorm;
                        dock = _composer;
                        bool talked = OnboardingBrainstorm.HasTalk(ob.BrainstormChat);
                        _writeUp.Show("WRITE IT UP", "check", !ob.BrainstormBusy && talked, "The DM turns what you settled on into the answer, ready for you to edit and submit.");
                        UpdateSend();
                        actions.Add(_back);
                        actions.Add(_writeUp);
                        actions.Add(_send);
                        ScrollTick = _brainstorm.ScrollTick;
                    }
                    else
                    {
                        if (_question == null || !ReferenceEquals(_questionFor, ob.Question))
                        {
                            _questionFor = ob.Question;
                            _question = QuestionPageViewModel.For(ob, _c);
                        }
                        else { _question.Update(ob); }
                        Page = _question;
                        if (_question is TextPageViewModel) { actions.Add(_answer); }
                    }
                    break;
                case OnboardingPhase.ReadyToFinalize:
                    _status.Show("seal", "Every question is answered. Finalizing locks the campaign's rules, tone and setting. It doesn't write the world yet.", ob.Error);
                    Page = _status;
                    actions.Add(_finalize);
                    break;
                case OnboardingPhase.Done:
                    _done.Update(ob);
                    Page = _done;
                    actions.Add(_toTable);
                    actions.Add(_seed);
                    break;
                default:
                    _status.Show("warning", ob.Error, string.Empty);
                    Page = _status;
                    actions.Add(_restart);
                    break;
            }
            Dock = dock;
            Actions = actions;
        }

        private void UpdateSend()
        {
            _send.Show("SEND", "chevron", _composer.CanSend);
        }

        private void Submit(string answer) { _c.Run(_c.SubmitOnboardingAnswer(answer)); }
    }
}
