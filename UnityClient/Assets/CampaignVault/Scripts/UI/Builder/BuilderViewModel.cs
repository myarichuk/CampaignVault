using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Builder
{
    /// <summary>One step on the rail, named "step-&lt;key&gt;". Red only once the player has been there.</summary>
    public sealed class StepPillViewModel : ViewModel, IKeyed
    {
        private string _label = string.Empty;
        private string _icon = string.Empty;
        private bool _current;
        private bool _done;
        private bool _wrong;

        public StepPillViewModel(string key, Action go)
        {
            Key = key;
            Name = "step-" + key;
            Go = go;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Label { get { return _label; } set { Set(ref _label, value); } }
        [CreateProperty] public string Icon { get { return _icon; } set { Set(ref _icon, value); } }
        [CreateProperty] public bool Current { get { return _current; } set { Set(ref _current, value); } }
        [CreateProperty] public bool Done { get { return _done; } set { Set(ref _done, value); } }
        [CreateProperty] public bool Wrong { get { return _wrong; } set { Set(ref _wrong, value); } }
        [CreateProperty] public Action Go { get; private set; }
    }

    /// <summary>One of the DM's picks as a one-tap button, named "use-&lt;id&gt;".</summary>
    public sealed class SuggestionViewModel : ViewModel, IKeyed
    {
        private string _label = string.Empty;

        public SuggestionViewModel(string id, Action use)
        {
            Key = id;
            Name = "use-" + id;
            Use = use;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Label { get { return _label; } set { Set(ref _label, value); } }
        [CreateProperty] public Action Use { get; private set; }
    }

    /// <summary>
    /// The character builder page (Templates/Builder/CharacterBuilder.uxml): the recipe's steps on a rail, the
    /// current step's widget, the server-derived sheet beside it, "Ask the DM" docked under them. Every rule lives on
    /// the server; this collects choices through the controller and shows what came back.
    /// </summary>
    public sealed class BuilderViewModel : ViewModel
    {
        public const string LevelCapFormat = "The builder goes up to level {0} for this system. Build at {0} and level up in play.";

        private readonly VaultAppState _s;
        private readonly VaultController _c;

        private List<StepPillViewModel> _steps = new List<StepPillViewModel>();
        private bool _hasSteps;
        private string _levelText = string.Empty;
        private bool _canLevelDown;
        private bool _canLevelUp;
        private bool _showLevel;
        private string _levelUpHint = string.Empty;
        private string _levelCapReason = string.Empty;
        private string _error = string.Empty;
        private string _clearedNote = string.Empty;

        private bool _hasStep;
        private string _emptyText = string.Empty;
        private string _stepTitle = string.Empty;
        private List<string> _issues = new List<string>();
        private List<string> _warnings = new List<string>();
        private StepViewModel _stepWidget;

        private bool _showAdvice;
        private string _adviceError = string.Empty;
        private List<string> _adviceParagraphs = new List<string>();
        private List<SuggestionViewModel> _suggestions = new List<SuggestionViewModel>();
        private string _notOptionsText = string.Empty;

        private string _previewCaption = string.Empty;
        private ViewModel _previewContent;
        private CharacterSheet _previewOf;
        private bool _previewAsStatBlock;
        private List<string> _notes = new List<string>();

        private string _askText = string.Empty;
        private string _seenAskDraft;
        private string _askPlaceholder = string.Empty;
        private bool _askEnabled = true;
        private string _askLabel = string.Empty;
        private bool _canAsk;
        private string _askHint = string.Empty;

        private string _fillLabel = string.Empty;
        private bool _canFill;
        private string _fillHint = string.Empty;
        private bool _showFilled;
        private string _filledText = string.Empty;
        private string _fillError = string.Empty;
        private List<string> _fillParagraphs = new List<string>();

        private string _status = string.Empty;
        private bool _showAnother;
        private bool _canBack;
        private bool _showNext;
        private string _commitLabel = string.Empty;
        private bool _canCommit;

        public BuilderViewModel(VaultAppState state, VaultController controller)
        {
            _s = state;
            _c = controller;
            LevelDown = delegate { _c.Run(_c.SetBuilderLevel(_s.Builder.Draft.Level - 1)); };
            LevelUp = delegate { _c.Run(_c.SetBuilderLevel(_s.Builder.Draft.Level + 1)); };
            DismissNote = delegate { _c.DismissBuilderNote(); };
            Ask = delegate { _c.Run(_c.AskDmAboutStep(_askText)); };
            Fill = delegate { _c.Run(_c.BuilderDmFill()); };
            DismissFill = delegate { _c.DismissDmFill(); };
            Another = delegate { _c.Run(_c.NewBuilderDraft(_s.Builder.Draft.Kind)); };
            Back = delegate { _c.BuilderStepBy(-1); };
            Next = delegate { _c.BuilderStepBy(1); };
            Commit = delegate { _c.Run(_c.BuilderCommit()); };
            Watch(state, StateArea.Builder | StateArea.Busy | StateArea.Driver | StateArea.Providers);
        }

        // ------------------------------------------------------------------ toolbar
        [CreateProperty] public List<StepPillViewModel> Steps { get { return _steps; } private set { SetList(ref _steps, value); } }
        [CreateProperty] public bool HasSteps { get { return _hasSteps; } private set { Set(ref _hasSteps, value); } }
        /// <summary>The level stepper: shown once there are steps, unless the system has no levels (Narrative).</summary>
        [CreateProperty] public bool ShowLevel { get { return _showLevel; } private set { Set(ref _showLevel, value); } }
        [CreateProperty] public string LevelText { get { return _levelText; } private set { Set(ref _levelText, value); } }
        [CreateProperty] public bool CanLevelDown { get { return _canLevelDown; } private set { Set(ref _canLevelDown, value); } }
        [CreateProperty] public bool CanLevelUp { get { return _canLevelUp; } private set { Set(ref _canLevelUp, value); } }
        [CreateProperty] public string LevelUpHint { get { return _levelUpHint; } private set { Set(ref _levelUpHint, value); } }
        /// <summary>Why the + is disabled, on its wrapper (a disabled button gets no hover).</summary>
        [CreateProperty] public string LevelCapReason { get { return _levelCapReason; } private set { Set(ref _levelCapReason, value); } }
        [CreateProperty] public Action LevelDown { get; private set; }
        [CreateProperty] public Action LevelUp { get; private set; }
        [CreateProperty] public string Error { get { return _error; } private set { Set(ref _error, value); } }
        [CreateProperty] public string ClearedNote { get { return _clearedNote; } private set { Set(ref _clearedNote, value); } }
        [CreateProperty] public Action DismissNote { get; private set; }

        // ------------------------------------------------------------------ step
        [CreateProperty] public bool HasStep { get { return _hasStep; } private set { Set(ref _hasStep, value); } }
        [CreateProperty] public string EmptyText { get { return _emptyText; } private set { Set(ref _emptyText, value); } }
        [CreateProperty] public string StepTitle { get { return _stepTitle; } private set { Set(ref _stepTitle, value); } }
        [CreateProperty] public List<string> Issues { get { return _issues; } private set { SetList(ref _issues, value); } }
        [CreateProperty] public List<string> Warnings { get { return _warnings; } private set { SetList(ref _warnings, value); } }
        [CreateProperty] public StepViewModel StepWidget { get { return _stepWidget; } private set { Set(ref _stepWidget, value); } }

        [CreateProperty] public bool ShowAdvice { get { return _showAdvice; } private set { Set(ref _showAdvice, value); } }
        [CreateProperty] public string AdviceError { get { return _adviceError; } private set { Set(ref _adviceError, value); } }
        [CreateProperty] public List<string> AdviceParagraphs { get { return _adviceParagraphs; } private set { SetList(ref _adviceParagraphs, value); } }
        [CreateProperty] public List<SuggestionViewModel> Suggestions { get { return _suggestions; } private set { SetList(ref _suggestions, value); } }
        [CreateProperty] public string NotOptionsText { get { return _notOptionsText; } private set { Set(ref _notOptionsText, value); } }

        // ------------------------------------------------------------------ preview
        [CreateProperty] public string PreviewCaption { get { return _previewCaption; } private set { Set(ref _previewCaption, value); } }
        /// <summary>The derived sheet (a companion's as its stat block); null before the first preview.</summary>
        [CreateProperty] public ViewModel PreviewContent { get { return _previewContent; } private set { Set(ref _previewContent, value); } }
        [CreateProperty] public List<string> Notes { get { return _notes; } private set { SetList(ref _notes, value); } }

        // ------------------------------------------------------------------ dock
        /// <summary>The question as typed; kept on the builder state so it survives closing the page.</summary>
        [CreateProperty]
        public string AskText
        {
            get { return _askText; }
            set { if (Set(ref _askText, value ?? string.Empty)) { _s.Builder.AskDraft = _seenAskDraft = _askText; } }
        }

        [CreateProperty] public string AskPlaceholder { get { return _askPlaceholder; } private set { Set(ref _askPlaceholder, value); } }
        [CreateProperty] public bool AskEnabled { get { return _askEnabled; } private set { Set(ref _askEnabled, value); } }
        [CreateProperty] public string AskLabel { get { return _askLabel; } private set { Set(ref _askLabel, value); } }
        [CreateProperty] public bool CanAsk { get { return _canAsk; } private set { Set(ref _canAsk, value); } }
        [CreateProperty] public string AskHint { get { return _askHint; } private set { Set(ref _askHint, value); } }
        [CreateProperty] public Action Ask { get; private set; }

        /// <summary>"The DM fills the rest": one call proposes picks for every open step, for the player to review.</summary>
        [CreateProperty] public string FillLabel { get { return _fillLabel; } private set { Set(ref _fillLabel, value); } }
        [CreateProperty] public bool CanFill { get { return _canFill; } private set { Set(ref _canFill, value); } }
        [CreateProperty] public string FillHint { get { return _fillHint; } private set { Set(ref _fillHint, value); } }
        [CreateProperty] public Action Fill { get; private set; }
        /// <summary>The card saying what the DM filled (and what it couldn't), until dismissed.</summary>
        [CreateProperty] public bool ShowFilled { get { return _showFilled; } private set { Set(ref _showFilled, value); } }
        [CreateProperty] public string FilledText { get { return _filledText; } private set { Set(ref _filledText, value); } }
        [CreateProperty] public string FillError { get { return _fillError; } private set { Set(ref _fillError, value); } }
        [CreateProperty] public List<string> FillParagraphs { get { return _fillParagraphs; } private set { SetList(ref _fillParagraphs, value); } }
        [CreateProperty] public Action DismissFill { get; private set; }

        // ------------------------------------------------------------------ foot
        [CreateProperty] public string Status { get { return _status; } private set { Set(ref _status, value); } }
        [CreateProperty] public bool ShowAnother { get { return _showAnother; } private set { Set(ref _showAnother, value); } }
        [CreateProperty] public Action Another { get; private set; }
        [CreateProperty] public bool CanBack { get { return _canBack; } private set { Set(ref _canBack, value); } }
        [CreateProperty] public Action Back { get; private set; }
        [CreateProperty] public bool ShowNext { get { return _showNext; } private set { Set(ref _showNext, value); } }
        [CreateProperty] public Action Next { get; private set; }
        [CreateProperty] public string CommitLabel { get { return _commitLabel; } private set { Set(ref _commitLabel, value); } }
        [CreateProperty] public bool CanCommit { get { return _canCommit; } private set { Set(ref _canCommit, value); } }
        [CreateProperty] public Action Commit { get; private set; }

        public override void Refresh()
        {
            var b = _s.Builder;
            RefreshToolbar(b);
            RefreshStep(b);
            RefreshPreview(b);
            RefreshDock(b);
            RefreshFoot(b);
        }

        public override void Dispose()
        {
            base.Dispose();
            if (_stepWidget != null) { _stepWidget.Dispose(); }
        }

        private void RefreshToolbar(BuilderState b)
        {
            int at = BuilderDependencies.IndexOf(b.Steps, b.Current);
            var indexed = new List<int>();
            for (int i = 0; i < b.Steps.Count; i++) { indexed.Add(i); }
            Steps = ItemList.Sync(_steps, indexed, delegate (int i) { return b.Steps[i].Key; },
                delegate (int i) { string key = b.Steps[i].Key; return new StepPillViewModel(key, delegate { _c.BuilderGoTo(key); }); },
                delegate (StepPillViewModel pill, int i)
                {
                    var step = b.Steps[i];
                    bool done = step.Kind == StepKinds.Identity ? b.Draft.Name.Length > 0 : b.Draft.Has(step.Key);
                    bool reached = done || i < at;
                    bool wrong = reached && b.IssuesFor(step.Key, false).Count > 0;
                    pill.Label = (i + 1) + "  " + step.Title.ToUpperInvariant();
                    pill.Icon = done && !wrong ? "check" : wrong ? "warning" : string.Empty;
                    pill.Current = step.Key == b.Current;
                    pill.Done = done && !wrong;
                    pill.Wrong = wrong;
                });

            HasSteps = b.Steps.Count > 0;
            ShowLevel = HasSteps && b.System != "narrative";
            LevelText = b.Draft.Level.ToString();
            CanLevelDown = b.Draft.Level > 1;
            int max = _c.BuilderMaxLevel;
            bool capped = b.Draft.Level >= max;
            CanLevelUp = !capped;
            LevelUpHint = capped ? string.Empty : "One level higher";
            LevelCapReason = capped ? string.Format(LevelCapFormat, max) : string.Empty;
            Error = DisplayText.Plain(b.Error);
            ClearedNote = DisplayText.Plain(b.ClearedNote);
        }

        private void RefreshStep(BuilderState b)
        {
            var step = b.CurrentStep;
            HasStep = step != null;
            if (step == null)
            {
                EmptyText = b.Error.Length > 0 ? DisplayText.Plain(b.Error) : "Reading the campaign's rules…";
                ReplaceWidget(null);
                ShowAdvice = false;
                return;
            }
            StepTitle = DisplayText.Plain(step.Title);
            var issues = new List<string>();
            // Before the player has touched a step, "pick one" isn't news.
            if (b.Draft.Has(step.Key) || step.Kind == StepKinds.Identity)
            {
                foreach (var issue in b.IssuesFor(step.Key, false)) { issues.Add(DisplayText.Plain(issue.Message)); }
            }
            Issues = issues;
            var warnings = new List<string>();
            foreach (var issue in b.IssuesFor(step.Key, true)) { warnings.Add(DisplayText.Plain(issue.Message)); }
            List<string> rejected;
            if (b.Rejected.TryGetValue(step.Key, out rejected) && rejected.Count > 0)
            {
                warnings.Add(DisplayText.Plain("Not used from the DM's picks, as they aren't options here: " + string.Join(", ", rejected.ToArray()) + "."));
            }
            Warnings = warnings;

            if (_stepWidget == null || _stepWidget.Step.Key != step.Key || _stepWidget.Step.Kind != step.Kind)
            {
                ReplaceWidget(StepViewModel.For(step, _c));
            }
            _stepWidget.Update(step, b);
            RefreshAdvice(b, step);
        }

        private void ReplaceWidget(StepViewModel widget)
        {
            if (_stepWidget != null && _stepWidget != widget) { _stepWidget.Dispose(); }
            StepWidget = widget;
        }

        /// <summary>The DM's last answer about this step, its picks as one-tap options, and ids that aren't options.</summary>
        private void RefreshAdvice(BuilderState b, BuilderStep step)
        {
            ShowAdvice = b.AskStep == step.Key && (b.AskReply.Length > 0 || b.AskError.Length > 0);
            if (!ShowAdvice) { return; }
            AdviceError = DisplayText.Plain(b.AskError);
            AdviceParagraphs = DisplayText.RichChunks(b.AskReply);
            StepOptions opts;
            b.Options.TryGetValue(step.Key, out opts);
            Suggestions = ItemList.Sync(_suggestions, b.Suggested, delegate (string id) { return id; },
                delegate (string id) { return new SuggestionViewModel(id, delegate { UseSuggestion(id); }); },
                delegate (SuggestionViewModel vm, string id)
                {
                    string label = id;
                    if (opts != null) { foreach (var o in opts.Options) { if (o.Id == id) { label = o.Label; } } }
                    vm.Label = "USE " + label.ToUpperInvariant();
                });
            NotOptionsText = b.NotOptions.Count > 0 ? DisplayText.Plain("Not options for this step, so left out: " + string.Join(", ", b.NotOptions.ToArray()) + ".") : string.Empty;
        }

        private void UseSuggestion(string id)
        {
            var b = _s.Builder;
            var step = b.CurrentStep;
            if (step == null) { return; }
            switch (step.Kind)
            {
                case StepKinds.PickOne:
                    _c.Run(_c.BuilderChoose(step.Key, JsonValue.FromString(id)));
                    break;
                case StepKinds.Spells:
                    StepOptions opts;
                    string group = SpellGroups.Known;
                    if (b.Options.TryGetValue(step.Key, out opts)) { foreach (var o in opts.Options) { if (o.Id == id && o.Group.Length > 0) { group = o.Group; } } }
                    if (!CharacterDraft.Strings(b.Draft.Get(step.Key).Get(group)).Contains(id)) { _c.Run(_c.BuilderToggleSpell(step.Key, group, id)); }
                    break;
                case StepKinds.LevelChoices:
                    // The id's slot is its option's group; an ability offered by several improvements goes to the first still open.
                    StepOptions levelOpts;
                    if (!b.Options.TryGetValue(step.Key, out levelOpts)) { break; }
                    var picks = LevelChoices.Picks(b.Draft.Get(step.Key));
                    foreach (var slot in levelOpts.Slots)
                    {
                        if (picks.ContainsKey(slot.Id) || !levelOpts.Options.Exists(delegate (BuilderOption o) { return o.Group == slot.Id && o.Id == id; })) { continue; }
                        _c.Run(_c.BuilderLevelPick(step.Key, slot, id));
                        break;
                    }
                    break;
                default:
                    if (!b.Draft.GetList(step.Key).Contains(id)) { _c.Run(_c.BuilderToggle(step.Key, id)); }
                    break;
            }
        }

        private void RefreshPreview(BuilderState b)
        {
            PreviewCaption = b.CommittedId.Length > 0 ? DisplayText.Plain("SAVED · " + b.CommittedId) : "PREVIEW · NOTHING SAVED YET";
            bool statBlock = b.Draft.Kind != "pc";
            // A new view model only for a new preview: a refresh for anything else leaves the sheet's elements alone.
            if (b.Preview != _previewOf || statBlock != _previewAsStatBlock)
            {
                _previewOf = b.Preview;
                _previewAsStatBlock = statBlock;
                PreviewContent = Sheet.SheetViewModels.For(b.Preview, statBlock, SchemaOf(b));
            }
            var notes = new List<string>();
            foreach (string n in b.Notes) { notes.Add(DisplayText.Plain(n)); }
            Notes = notes;
        }

        private void RefreshDock(BuilderState b)
        {
            var step = b.CurrentStep;
            bool busy = _s.Driver != null && _s.Driver.IsBusy;
            // Only when the draft changed underneath the field (sent, cleared): never over typing still on its way in.
            if (b.AskDraft != _seenAskDraft) { _seenAskDraft = b.AskDraft; Set(ref _askText, b.AskDraft, "AskText"); }
            AskPlaceholder = step == null ? string.Empty
                : "Ask the DM about " + step.Title.ToLowerInvariant() + ", e.g. “what fits a smuggler who owes the wrong people?”";
            AskEnabled = !b.AskBusy;
            AskLabel = b.AskBusy ? "THE DM IS THINKING…" : "ASK THE DM";
            CanAsk = !b.AskBusy && !busy;
            string notReady;
            bool ready = _s.ProviderReady(out notReady);
            AskHint = ready
                ? "The DM answers in the same conversation as your campaign setup and suggests options from this step."
                : "Needs a working AI provider: " + notReady;
            FillLabel = b.FillBusy ? "THE DM IS PICKING…" : "DM FILLS THE REST";
            CanFill = !b.FillBusy && !b.AskBusy && !busy && b.Steps.Count > 0;
            FillHint = ready
                ? "The DM picks for every step still open (skills, spells, level choices), never your ability scores or who they are. You review it all before saving."
                : "Needs a working AI provider: " + notReady;
            FillError = DisplayText.Plain(b.FillError);
            FillParagraphs = DisplayText.RichChunks(b.FillReply);
            ShowFilled = b.FillError.Length > 0 || b.FillReply.Length > 0 || b.Filled.Count > 0 || b.Rejected.Count > 0;
            FilledText = FilledLine(b);
        }

        private void RefreshFoot(BuilderState b)
        {
            int at = BuilderDependencies.IndexOf(b.Steps, b.Current);
            Status = StatusOf(b);
            ShowAnother = b.CommittedId.Length > 0;
            CanBack = at > 0;
            ShowNext = at >= 0 && at < b.Steps.Count - 1;
            bool committing = _s.IsBusy("builder-commit");
            CommitLabel = committing ? "SAVING…" : b.CommittedId.Length > 0 ? "SAVE CHANGES" : "SAVE CHARACTER";
            // Commit only from a clean preview; a stale one is re-checked on the way in.
            CanCommit = !committing && b.Steps.Count > 0 && (b.Errors.Count == 0 || !b.PreviewCurrent);
        }

        /// <summary>The stat block the identity step edits, so the parchment shows every field of it.</summary>
        private static StatBlockSchema SchemaOf(BuilderState b)
        {
            foreach (var step in b.Steps)
            {
                if (step.Kind != StepKinds.Identity || step.Schema.Length == 0) { continue; }
                foreach (var schema in b.StatBlocks) { if (string.Equals(schema.Name, step.Schema, StringComparison.OrdinalIgnoreCase)) { return schema; } }
            }
            return null;
        }

        /// <summary>"The DM filled level choices and spells. Look them over before you save." and what it left open.</summary>
        public static string FilledLine(BuilderState b)
        {
            var parts = new List<string>();
            if (b.Filled.Count > 0)
            {
                var titles = new List<string>();
                foreach (string t in b.Filled) { titles.Add(t.ToLowerInvariant()); }
                parts.Add("The DM filled " + string.Join(", ", titles.ToArray()) + ". Look them over before you save.");
            }
            else if (b.FillReply.Length > 0 || b.Rejected.Count > 0) { parts.Add("None of the DM's picks were options, so nothing changed."); }
            if (b.Rejected.Count > 0)
            {
                var steps = new List<string>();
                foreach (string key in b.Rejected.Keys) { var step = b.Step(key); steps.Add(step != null ? step.Title.ToLowerInvariant() : key); }
                parts.Add("Some of its picks weren't options, so they weren't used (see " + string.Join(", ", steps.ToArray()) + ").");
            }
            return DisplayText.Plain(string.Join(" ", parts.ToArray()));
        }

        public static string StatusOf(BuilderState b)
        {
            if (b.Steps.Count == 0) { return string.Empty; }
            if (b.Errors.Count == 0) { return b.PreviewCurrent ? "Ready to save." : "Checking…"; }
            var steps = new List<string>();
            foreach (var e in b.Errors)
            {
                var step = b.Step(e.Step);
                string title = step != null ? step.Title.ToLowerInvariant() : e.Step;
                if (!steps.Contains(title)) { steps.Add(title); }
            }
            return DisplayText.Plain("Still open: " + string.Join(", ", steps.ToArray()) + ".");
        }
    }
}
