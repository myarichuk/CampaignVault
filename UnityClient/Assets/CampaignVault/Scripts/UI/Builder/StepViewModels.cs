using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Builder
{
    /// <summary>
    /// The current step, as the widget for its kind (one widget per kind, not one screen per system: a new system or
    /// a plugin's step needs no client work). Each names its template; the page's presenter swaps templates when the
    /// kind changes and only rebinds when it doesn't.
    /// </summary>
    public abstract class StepViewModel : ViewModel, ITemplated
    {
        protected readonly VaultController Controller;

        protected StepViewModel(BuilderStep step, VaultController controller)
        {
            Step = step;
            Controller = controller;
        }

        public BuilderStep Step { get; private set; }
        public abstract string Template { get; }

        /// <summary>Re-read the step's part of the builder state.</summary>
        public abstract void Update(BuilderStep step, BuilderState b);

        protected void Run(System.Collections.IEnumerator command)
        {
            if (Controller != null) { Controller.Run(command); }
        }

        /// <summary>The widget for a step's kind; a kind this client doesn't know gets a card that says so.</summary>
        public static StepViewModel For(BuilderStep step, VaultController controller)
        {
            switch (step.Kind)
            {
                case StepKinds.PickOne: return new PickStepViewModel(step, controller, false);
                case StepKinds.PickN:
                case StepKinds.Feats:
                case StepKinds.Allocate: return new PickStepViewModel(step, controller, true);
                case StepKinds.AbilityScores: return new AbilityStepViewModel(step, controller);
                case StepKinds.Spells: return new SpellsStepViewModel(step, controller);
                case StepKinds.Identity: return new IdentityStepViewModel(step, controller);
                default: return new UnsupportedStepViewModel(step);
            }
        }

        /// <summary>"2 OF 3 CHOSEN"; complete and over drive the green and red classes.</summary>
        public static string CountLine(string what, int picked, int count)
        {
            return picked + " OF " + (count > 0 ? count.ToString() : "ANY") + " " + what;
        }
    }

    /// <summary>The fallback for a step kind this client has no widget for.</summary>
    public sealed class UnsupportedStepViewModel : StepViewModel
    {
        public UnsupportedStepViewModel(BuilderStep step) : base(step, null) { }

        public override string Template { get { return "Builder/UnsupportedStep"; } }

        [CreateProperty] public string Message { get { return DisplayText.Plain("This client doesn't support step '" + Step.Kind + "' yet."); } }

        [CreateProperty]
        public string Detail
        {
            get
            {
                return Step.Optional
                    ? "It's optional, so you can skip it. An updated client will show it."
                    : "The server's rules need it, so the character can't be saved from here yet. Ask the DM to set it, or update the client.";
            }
        }

        public override void Update(BuilderStep step, BuilderState b) { }
    }

    /// <summary>One option as a card. Named "opt-&lt;id&gt;" (spells: "spell-&lt;group&gt;-&lt;id&gt;") so tests find it.</summary>
    public sealed class OptionViewModel : ViewModel, IKeyed
    {
        private string _label = string.Empty;
        private string _description = string.Empty;
        private bool _selected;
        private bool _suggested;
        private bool _full;
        private string _fullHint = string.Empty;
        private bool _visible = true;

        public OptionViewModel(string name, BuilderOption option, Action<OptionViewModel> pick)
        {
            Key = name;
            Name = name;
            Option = option;
            Pick = delegate { pick(this); };
        }

        public string Key { get; private set; }
        public BuilderOption Option { get; private set; }

        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Label { get { return _label; } private set { Set(ref _label, value); } }
        [CreateProperty] public string Description { get { return _description; } private set { Set(ref _description, value); } }
        [CreateProperty] public bool Selected { get { return _selected; } private set { Set(ref _selected, value); } }
        [CreateProperty] public bool Suggested { get { return _suggested; } private set { Set(ref _suggested, value); } }
        /// <summary>The step's count is reached and this one isn't picked.</summary>
        [CreateProperty] public bool Full { get { return _full; } private set { Set(ref _full, value); } }
        [CreateProperty] public string FullHint { get { return _fullHint; } private set { Set(ref _fullHint, value); } }
        /// <summary>Matches the filter.</summary>
        [CreateProperty] public bool Visible { get { return _visible; } set { Set(ref _visible, value); } }
        [CreateProperty] public Action Pick { get; private set; }

        public void Update(BuilderOption option, bool selected, bool suggested, bool full, string fullHint)
        {
            Option = option;
            Label = DisplayText.Plain(option.Label);
            Description = DisplayText.Plain(option.Description);
            Selected = selected;
            Suggested = suggested;
            Full = full;
            FullHint = full ? fullHint : string.Empty;
        }

        public bool Matches(string needle)
        {
            return needle.Length == 0
                || Option.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || Option.Description.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || Option.Group.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>A step whose choices come from the server's option list: the loading/failed/empty notice and the filter.</summary>
    public abstract class OptionsStepViewModel : StepViewModel
    {
        /// <summary>Past this many options the list gets a filter box.</summary>
        public const int FilterFrom = 12;

        private string _notice = string.Empty;
        private bool _noticeIsError;
        private bool _showFilter;
        private string _filter = string.Empty;
        private BuilderState _state;

        protected OptionsStepViewModel(BuilderStep step, VaultController controller) : base(step, controller) { }

        [CreateProperty] public string Notice { get { return _notice; } protected set { Set(ref _notice, value); } }
        [CreateProperty] public bool NoticeIsError { get { return _noticeIsError; } protected set { Set(ref _noticeIsError, value); } }
        [CreateProperty] public bool ShowFilter { get { return _showFilter; } protected set { Set(ref _showFilter, value); } }

        /// <summary>Filters the cards in place (label, description or group): typing never rebuilds the list.</summary>
        [CreateProperty]
        public string Filter
        {
            get { return _filter; }
            set
            {
                if (!Set(ref _filter, value ?? string.Empty)) { return; }
                if (_state != null) { _state.Filters[Step.Key] = _filter; }
                ApplyFilter();
            }
        }

        protected abstract IEnumerable<OptionViewModel> AllOptions();

        protected void ApplyFilter()
        {
            string needle = _filter.Trim();
            foreach (var o in AllOptions()) { o.Visible = o.Matches(needle); }
        }

        /// <summary>The step's options, or null with the notice set.</summary>
        protected StepOptions Loaded(BuilderState b)
        {
            _state = b;
            string filter;
            if (b.Filters.TryGetValue(Step.Key, out filter) && filter != _filter) { _filter = filter; Notify("Filter"); }
            StepOptions opts;
            if (!b.Options.TryGetValue(Step.Key, out opts)) { return Fail("Gathering the options…", false); }
            if (opts.Error.Length > 0) { return Fail(DisplayText.Plain("Couldn't load the options: " + opts.Error), true); }
            if (opts.Options.Count == 0)
            {
                return Fail(Step.Optional ? "Nothing to choose here. Skip it." : "Nothing to choose for the character as it is. An earlier step may still be open.", false);
            }
            Notice = string.Empty;
            NoticeIsError = false;
            return opts;
        }

        protected StepOptions Fail(string notice, bool error)
        {
            Notice = notice;
            NoticeIsError = error;
            ShowFilter = false;
            return null;
        }

        protected static bool Contains(List<string> ids, string id)
        {
            return ids.Exists(delegate (string p) { return string.Equals(p, id, StringComparison.OrdinalIgnoreCase); });
        }
    }

    /// <summary>pickOne, pickN, feats and allocate: option cards with label and description, and an "N of M" count.</summary>
    public sealed class PickStepViewModel : OptionsStepViewModel
    {
        private readonly bool _many;
        private bool _showCount;
        private string _countText = string.Empty;
        private bool _countComplete;
        private bool _countOver;
        private List<OptionViewModel> _options = new List<OptionViewModel>();

        public PickStepViewModel(BuilderStep step, VaultController controller, bool many) : base(step, controller)
        {
            _many = many;
        }

        public override string Template { get { return "Builder/PickStep"; } }

        [CreateProperty] public bool ShowCount { get { return _showCount; } private set { Set(ref _showCount, value); } }
        [CreateProperty] public string CountText { get { return _countText; } private set { Set(ref _countText, value); } }
        [CreateProperty] public bool CountComplete { get { return _countComplete; } private set { Set(ref _countComplete, value); } }
        [CreateProperty] public bool CountOver { get { return _countOver; } private set { Set(ref _countOver, value); } }
        [CreateProperty] public List<OptionViewModel> Options { get { return _options; } private set { SetList(ref _options, value); } }

        protected override IEnumerable<OptionViewModel> AllOptions() { return _options; }

        public override void Update(BuilderStep step, BuilderState b)
        {
            var opts = Loaded(b);
            if (opts == null)
            {
                ShowCount = false;
                Options = new List<OptionViewModel>();
                return;
            }
            var picked = b.Draft.GetList(step.Key);
            int count = _many ? (opts.Count > 0 ? opts.Count : step.Count) : 1;
            ShowCount = _many;
            CountText = CountLine("CHOSEN", picked.Count, count);
            CountComplete = count > 0 && picked.Count == count;
            CountOver = count > 0 && picked.Count > count;
            ShowFilter = opts.Options.Count > FilterFrom;
            Options = ItemList.Sync(_options, opts.Options, delegate (BuilderOption o) { return "opt-" + o.Id; },
                delegate (BuilderOption o) { return new OptionViewModel("opt-" + o.Id, o, Picked); },
                delegate (OptionViewModel vm, BuilderOption o)
                {
                    bool selected = Contains(picked, o.Id);
                    vm.Update(o, selected, b.Suggested.Contains(o.Id), _many && count > 0 && picked.Count >= count && !selected,
                        "You've chosen " + count + ". Drop one to pick this instead.");
                });
            ApplyFilter();
        }

        private void Picked(OptionViewModel o)
        {
            if (_many) { Run(Controller.BuilderToggle(Step.Key, o.Option.Id)); }
            else if (!o.Selected) { Run(Controller.BuilderChoose(Step.Key, JsonValue.FromString(o.Option.Id))); }
        }
    }

    /// <summary>One spell list (cantrips, known, prepared) with its count for the class and level.</summary>
    public sealed class SpellSectionViewModel : ViewModel, IKeyed
    {
        private string _countText = string.Empty;
        private bool _complete;
        private bool _over;
        private string _hint = string.Empty;
        private List<OptionViewModel> _options = new List<OptionViewModel>();

        public SpellSectionViewModel(string group, string title)
        {
            Key = group;
            Title = title.ToUpperInvariant();
        }

        public string Key { get; private set; }
        [CreateProperty] public string Title { get; private set; }
        [CreateProperty] public string CountText { get { return _countText; } set { Set(ref _countText, value); } }
        [CreateProperty] public bool Complete { get { return _complete; } set { Set(ref _complete, value); } }
        [CreateProperty] public bool Over { get { return _over; } set { Set(ref _over, value); } }
        [CreateProperty] public string Hint { get { return _hint; } set { Set(ref _hint, value); } }
        [CreateProperty] public List<OptionViewModel> Options { get { return _options; } set { SetList(ref _options, value); } }
    }

    /// <summary>
    /// spells: cantrips, spells known (or a spellbook) and prepared spells. Prepared picks come from the known list,
    /// or from the class list when the class learns no fixed list.
    /// </summary>
    public sealed class SpellsStepViewModel : OptionsStepViewModel
    {
        private List<SpellSectionViewModel> _sections = new List<SpellSectionViewModel>();

        public SpellsStepViewModel(BuilderStep step, VaultController controller) : base(step, controller) { }

        public override string Template { get { return "Builder/SpellsStep"; } }

        [CreateProperty] public List<SpellSectionViewModel> Sections { get { return _sections; } private set { SetList(ref _sections, value); } }

        protected override IEnumerable<OptionViewModel> AllOptions()
        {
            foreach (var s in _sections) { foreach (var o in s.Options) { yield return o; } }
        }

        public override void Update(BuilderStep step, BuilderState b)
        {
            var opts = Loaded(b);
            if (opts != null && opts.GroupCount(SpellGroups.Cantrips) + opts.GroupCount(SpellGroups.Known) + opts.GroupCount(SpellGroups.Prepared) == 0)
            {
                opts = Fail("No spells to choose at this level.", false);
            }
            if (opts == null)
            {
                Sections = new List<SpellSectionViewModel>();
                return;
            }
            int cantrips = opts.GroupCount(SpellGroups.Cantrips);
            int known = opts.GroupCount(SpellGroups.Known);
            int prepared = opts.GroupCount(SpellGroups.Prepared);
            var choice = b.Draft.Get(step.Key);
            var wanted = new List<string[]>();
            if (cantrips > 0) { wanted.Add(new[] { SpellGroups.Cantrips, "Cantrips" }); }
            if (known > 0) { wanted.Add(new[] { SpellGroups.Known, "Spells known" }); }
            if (prepared > 0) { wanted.Add(new[] { SpellGroups.Prepared, "Prepared each day" }); }
            int total = 0;
            Sections = ItemList.Sync(_sections, wanted, delegate (string[] w) { return w[0]; },
                delegate (string[] w) { return new SpellSectionViewModel(w[0], w[1]); },
                delegate (SpellSectionViewModel section, string[] w)
                {
                    string group = w[0];
                    var picked = CharacterDraft.Strings(choice.Get(group));
                    int count = opts.GroupCount(group);
                    List<BuilderOption> candidates;
                    if (group == SpellGroups.Prepared)
                    {
                        // A prepared caster with a list of its own (a wizard's spellbook) prepares from it; others from the class list.
                        var knownIds = CharacterDraft.Strings(choice.Get(SpellGroups.Known));
                        candidates = known > 0 ? opts.Options.FindAll(delegate (BuilderOption o) { return o.Group == SpellGroups.Known && Contains(knownIds, o.Id); })
                            : opts.Options.FindAll(delegate (BuilderOption o) { return o.Group == SpellGroups.Known; });
                        section.Hint = known > 0 && candidates.Count == 0 ? "Pick spells known first; you prepare from them." : string.Empty;
                    }
                    else
                    {
                        candidates = opts.Options.FindAll(delegate (BuilderOption o) { return o.Group == group; });
                        section.Hint = string.Empty;
                    }
                    section.CountText = CountLine("CHOSEN", picked.Count, count);
                    section.Complete = count > 0 && picked.Count == count;
                    section.Over = count > 0 && picked.Count > count;
                    total += candidates.Count;
                    section.Options = ItemList.Sync(section.Options, candidates, delegate (BuilderOption o) { return "spell-" + group + "-" + o.Id; },
                        delegate (BuilderOption o) { return new OptionViewModel("spell-" + group + "-" + o.Id, o, delegate (OptionViewModel vm) { Run(Controller.BuilderToggleSpell(Step.Key, group, vm.Option.Id)); }); },
                        delegate (OptionViewModel vm, BuilderOption o)
                        {
                            bool selected = picked.Contains(o.Id);
                            vm.Update(o, selected, b.Suggested.Contains(o.Id), !selected && picked.Count >= count, "You've chosen " + count + ". Drop one to pick this instead.");
                        });
                });
            ShowFilter = total > FilterFrom;
            ApplyFilter();
        }
    }

    /// <summary>One ability-score method tab, named "method-&lt;method&gt;".</summary>
    public sealed class MethodTabViewModel : ViewModel, IKeyed
    {
        private bool _active;

        public MethodTabViewModel(string method, string label, Action select)
        {
            Key = method;
            Name = "method-" + method;
            Label = label;
            Select = select;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Label { get; private set; }
        [CreateProperty] public bool Active { get { return _active; } set { Set(ref _active, value); } }
        [CreateProperty] public Action Select { get; private set; }
    }

    /// <summary>One value of the pool (standard array or rolls) in an ability's row, named "assign-&lt;Ability&gt;-&lt;i&gt;".</summary>
    public sealed class PoolValueViewModel : ViewModel, IKeyed
    {
        private string _text = string.Empty;
        private bool _selected;
        private bool _taken;

        public PoolValueViewModel(string ability, int index, Action assign)
        {
            Key = index.ToString();
            Name = "assign-" + ability + "-" + index;
            Assign = assign;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Text { get { return _text; } set { Set(ref _text, value); } }
        [CreateProperty] public bool Selected { get { return _selected; } set { Set(ref _selected, value); } }
        /// <summary>Assigned to another ability (tapping it swaps).</summary>
        [CreateProperty] public bool Taken { get { return _taken; } set { Set(ref _taken, value); } }
        [CreateProperty] public Action Assign { get; private set; }
    }

    /// <summary>One ability: the pool to assign from (or point-buy steps), and the score with the race's bonus.</summary>
    public sealed class AbilityRowViewModel : ViewModel, IKeyed
    {
        private bool _isPointBuy;
        private string _scoreText = string.Empty;
        private string _costText = string.Empty;
        private bool _noPool;
        private string _bonusText = string.Empty;
        private string _finalText = string.Empty;
        private List<PoolValueViewModel> _pool = new List<PoolValueViewModel>();

        public AbilityRowViewModel(string ability, Action down, Action up)
        {
            Key = ability;
            NameText = ability.ToUpperInvariant();
            DownName = "buy-" + ability + "-down";
            UpName = "buy-" + ability + "-up";
            DownHint = "Lower " + ability;
            UpHint = "Raise " + ability;
            Down = down;
            Up = up;
        }

        public string Key { get; private set; }
        [CreateProperty] public string NameText { get; private set; }
        [CreateProperty] public string DownName { get; private set; }
        [CreateProperty] public string UpName { get; private set; }
        [CreateProperty] public string DownHint { get; private set; }
        [CreateProperty] public string UpHint { get; private set; }
        [CreateProperty] public Action Down { get; private set; }
        [CreateProperty] public Action Up { get; private set; }
        [CreateProperty] public bool IsPointBuy { get { return _isPointBuy; } set { Set(ref _isPointBuy, value); } }
        [CreateProperty] public string ScoreText { get { return _scoreText; } set { Set(ref _scoreText, value); } }
        [CreateProperty] public string CostText { get { return _costText; } set { Set(ref _costText, value); } }
        [CreateProperty] public List<PoolValueViewModel> Pool { get { return _pool; } set { SetList(ref _pool, value); } }
        /// <summary>Roll chosen, nothing rolled yet.</summary>
        [CreateProperty] public bool NoPool { get { return _noPool; } set { Set(ref _noPool, value); } }
        /// <summary>"+1 race" from the preview; empty when the race adds nothing.</summary>
        [CreateProperty] public string BonusText { get { return _bonusText; } set { Set(ref _bonusText, value); } }
        /// <summary>"16 (+3)".</summary>
        [CreateProperty] public string FinalText { get { return _finalText; } set { Set(ref _finalText, value); } }
    }

    /// <summary>
    /// abilityScores: a tab per allowed method. Standard array and roll assign a pool of values to the six abilities
    /// (tap a value; tapping one another ability has swaps them); point buy steps each score with points left. The
    /// racial bonus from the preview sits beside each base score, and the roll log stays on screen.
    /// </summary>
    public sealed class AbilityStepViewModel : StepViewModel
    {
        private List<MethodTabViewModel> _methods = new List<MethodTabViewModel>();
        private bool _showHint;
        private bool _showPoints;
        private string _pointsText = string.Empty;
        private bool _pointsDone;
        private bool _showRoll;
        private string _rollLabel = string.Empty;
        private bool _rollPrimary;
        private bool _showAssigned;
        private string _assignedText = string.Empty;
        private bool _assignedComplete;
        private List<AbilityRowViewModel> _rows = new List<AbilityRowViewModel>();
        private List<string> _rollLog = new List<string>();

        public AbilityStepViewModel(BuilderStep step, VaultController controller) : base(step, controller)
        {
            Roll = delegate { Run(Controller.BuilderRollAbilities(Step.Key)); };
        }

        public override string Template { get { return "Builder/AbilityStep"; } }

        [CreateProperty] public List<MethodTabViewModel> Methods { get { return _methods; } private set { SetList(ref _methods, value); } }
        /// <summary>No method chosen yet.</summary>
        [CreateProperty] public bool ShowHint { get { return _showHint; } private set { Set(ref _showHint, value); } }
        [CreateProperty] public bool ShowPoints { get { return _showPoints; } private set { Set(ref _showPoints, value); } }
        [CreateProperty] public string PointsText { get { return _pointsText; } private set { Set(ref _pointsText, value); } }
        [CreateProperty] public bool PointsDone { get { return _pointsDone; } private set { Set(ref _pointsDone, value); } }
        [CreateProperty] public bool ShowRoll { get { return _showRoll; } private set { Set(ref _showRoll, value); } }
        [CreateProperty] public string RollLabel { get { return _rollLabel; } private set { Set(ref _rollLabel, value); } }
        [CreateProperty] public bool RollPrimary { get { return _rollPrimary; } private set { Set(ref _rollPrimary, value); } }
        [CreateProperty] public Action Roll { get; private set; }
        [CreateProperty] public bool ShowAssigned { get { return _showAssigned; } private set { Set(ref _showAssigned, value); } }
        [CreateProperty] public string AssignedText { get { return _assignedText; } private set { Set(ref _assignedText, value); } }
        [CreateProperty] public bool AssignedComplete { get { return _assignedComplete; } private set { Set(ref _assignedComplete, value); } }
        [CreateProperty] public List<AbilityRowViewModel> Rows { get { return _rows; } private set { SetList(ref _rows, value); } }
        [CreateProperty] public List<string> RollLog { get { return _rollLog; } private set { SetList(ref _rollLog, value); } }

        public override void Update(BuilderStep step, BuilderState b)
        {
            var work = b.Abilities;
            var methods = new List<string[]>();
            if (step.StandardArray.Count > 0) { methods.Add(new[] { "standardArray", "STANDARD ARRAY" }); }
            if (step.PointBuy != null) { methods.Add(new[] { "pointBuy", "POINT BUY" }); }
            if (AbilityDice.IsValid(step.Roll)) { methods.Add(new[] { "roll", "ROLL" }); }
            Methods = ItemList.Sync(_methods, methods, delegate (string[] m) { return m[0]; },
                delegate (string[] m) { string method = m[0]; return new MethodTabViewModel(method, m[1], delegate { Run(Controller.BuilderAbilityMethod(Step.Key, method)); }); },
                delegate (MethodTabViewModel tab, string[] m) { tab.Active = work.Method == m[0]; });

            bool chosen = work.Method.Length > 0;
            bool pointBuy = work.Method == "pointBuy" && step.PointBuy != null;
            ShowHint = !chosen;
            ShowPoints = pointBuy;
            if (pointBuy)
            {
                int left = step.PointBuy.Budget - step.PointBuy.Spent(work.Bought.Values);
                PointsText = "POINTS LEFT " + left + " / " + step.PointBuy.Budget;
                PointsDone = left == 0;
            }
            ShowRoll = work.Method == "roll";
            RollLabel = work.Pool.Count == 0 ? "ROLL " + step.Roll.Replace("dropLowest", ", DROP LOWEST").ToUpperInvariant() : "ROLL AGAIN";
            RollPrimary = work.Pool.Count == 0;
            ShowAssigned = work.Method == "standardArray";
            AssignedText = CountLine("ASSIGNED", work.Assigned.Count, AbilityWork.Abilities.Length);
            AssignedComplete = work.Assigned.Count == AbilityWork.Abilities.Length;

            var scores = work.Scores();
            Rows = !chosen ? new List<AbilityRowViewModel>() : ItemList.Sync(_rows, AbilityWork.Abilities, delegate (string a) { return a; },
                delegate (string a)
                {
                    return new AbilityRowViewModel(a, delegate { Run(Controller.BuilderBuyAbility(Step.Key, a, -1)); }, delegate { Run(Controller.BuilderBuyAbility(Step.Key, a, 1)); });
                },
                delegate (AbilityRowViewModel row, string a) { UpdateRow(row, a, step, b, scores, pointBuy); });

            var log = new List<string>();
            foreach (string line in work.RollLog) { log.Add(DisplayText.Plain(line)); }
            RollLog = log;
        }

        private void UpdateRow(AbilityRowViewModel row, string ability, BuilderStep step, BuilderState b, Dictionary<string, int> scores, bool pointBuy)
        {
            var work = b.Abilities;
            int score;
            bool has = scores.TryGetValue(ability, out score);
            row.IsPointBuy = pointBuy;
            row.ScoreText = has ? score.ToString() : "–";
            row.CostText = pointBuy && has ? step.PointBuy.CostOf(score) + " PT" : string.Empty;
            int mine;
            bool assigned = work.Assigned.TryGetValue(ability, out mine);
            var indexes = new List<int>();
            if (!pointBuy) { for (int i = 0; i < work.Pool.Count; i++) { indexes.Add(i); } }
            row.Pool = ItemList.Sync(row.Pool, indexes, delegate (int i) { return i.ToString(); },
                delegate (int i) { return new PoolValueViewModel(ability, i, delegate { Run(Controller.BuilderAssignAbility(Step.Key, ability, i)); }); },
                delegate (PoolValueViewModel v, int i)
                {
                    v.Text = work.Pool[i].ToString();
                    v.Selected = assigned && mine == i;
                    v.Taken = !(assigned && mine == i) && work.Assigned.ContainsValue(i);
                });
            row.NoPool = !pointBuy && work.Pool.Count == 0;

            // The preview's score minus the base is what the race added.
            int bonus = 0;
            if (has && b.Preview != null)
            {
                foreach (var a in b.Preview.Abilities)
                {
                    if (a.Name == ability && a.Score >= 0) { bonus = a.Score - score; }
                }
            }
            row.BonusText = bonus != 0 ? (bonus > 0 ? "+" : string.Empty) + bonus + " RACE" : string.Empty;
            row.FinalText = has ? (score + bonus) + " (" + CharacterSheet.Signed(CharacterSheet.Dnd5eMod(score + bonus)) + ")" : string.Empty;
        }
    }

    /// <summary>One stat-block field of a companion's identity step, named "stat-&lt;key&gt;".</summary>
    public sealed class StatFieldViewModel : ViewModel, IKeyed
    {
        private readonly Action<StatFieldViewModel, string> _changed;
        private string _value = string.Empty;
        private string _seen;
        private string _groupTitle = string.Empty;

        public StatFieldViewModel(StatBlockField field, Action<StatFieldViewModel, string> changed)
        {
            Field = field;
            Key = field.Key;
            Name = "stat-" + field.Key;
            Caption = DisplayText.Plain(field.Label.ToUpperInvariant());
            Hint = field.Type == "int" ? Range(field) : field.Type;
            _changed = changed;
        }

        public string Key { get; private set; }
        public StatBlockField Field { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Caption { get; private set; }
        [CreateProperty] public string Hint { get; private set; }
        /// <summary>The group's caption on the first field of each group.</summary>
        [CreateProperty] public string GroupTitle { get { return _groupTitle; } set { Set(ref _groupTitle, value); } }

        [CreateProperty]
        public string Value
        {
            get { return _value; }
            set { if (Set(ref _value, value ?? string.Empty)) { _seen = _value; _changed(this, _value); } }
        }

        /// <summary>From state: shown (only when it changed there), not sent back.</summary>
        public void Show(string value)
        {
            value = value ?? string.Empty;
            if (value == _seen) { return; }
            _seen = value;
            Set(ref _value, value, "Value");
        }

        private static string Range(StatBlockField f)
        {
            bool min = f.Min != int.MinValue;
            bool max = f.Max != int.MaxValue;
            if (min && max) { return f.Min + "–" + f.Max; }
            if (min) { return f.Min + " or more"; }
            if (max) { return "up to " + f.Max; }
            return "a number";
        }
    }

    /// <summary>
    /// identity: name, concept and look (fields of the draft itself), plus the stat block's fields when the step
    /// names a schema. A field commits when it loses focus (or on Enter), and that's when the preview catches up.
    /// </summary>
    public sealed class IdentityStepViewModel : StepViewModel
    {
        private string _name = string.Empty;
        private string _concept = string.Empty;
        private string _look = string.Empty;
        // What the draft held when last read: a field takes the draft's value only when it changed underneath it,
        // so a refresh caused by one field never pushes a stale value into another that's mid-edit.
        private string _seenName;
        private string _seenConcept;
        private string _seenLook;
        private string _schemaError = string.Empty;
        private bool _hasSchema;
        private List<StatFieldViewModel> _stats = new List<StatFieldViewModel>();

        public IdentityStepViewModel(BuilderStep step, VaultController controller) : base(step, controller) { }

        public override string Template { get { return "Builder/IdentityStep"; } }

        [CreateProperty]
        public string Name
        {
            get { return _name; }
            set { if (Set(ref _name, value ?? string.Empty)) { _seenName = _name; Commit(_name, null, null); } }
        }

        [CreateProperty]
        public string Concept
        {
            get { return _concept; }
            set { if (Set(ref _concept, value ?? string.Empty)) { _seenConcept = _concept; Commit(null, _concept, null); } }
        }

        [CreateProperty]
        public string Look
        {
            get { return _look; }
            set { if (Set(ref _look, value ?? string.Empty)) { _seenLook = _look; Commit(null, null, _look); } }
        }

        [CreateProperty] public string SchemaError { get { return _schemaError; } private set { Set(ref _schemaError, value); } }
        [CreateProperty] public bool HasSchema { get { return _hasSchema; } private set { Set(ref _hasSchema, value); } }
        [CreateProperty] public List<StatFieldViewModel> Stats { get { return _stats; } private set { SetList(ref _stats, value); } }

        private void Commit(string name, string concept, string look)
        {
            if (Controller == null) { return; }
            Controller.BuilderIdentity(name, concept, look);
            Run(Controller.BuilderPreview());
        }

        public override void Update(BuilderStep step, BuilderState b)
        {
            var d = b.Draft;
            if (d.Name != _seenName) { _seenName = d.Name; Set(ref _name, d.Name, "Name"); }
            if (d.Concept != _seenConcept) { _seenConcept = d.Concept; Set(ref _concept, d.Concept, "Concept"); }
            if (d.Look != _seenLook) { _seenLook = d.Look; Set(ref _look, d.Look, "Look"); }

            StatBlockSchema schema = null;
            if (step.Schema.Length > 0)
            {
                foreach (var s in b.StatBlocks) { if (string.Equals(s.Name, step.Schema, StringComparison.OrdinalIgnoreCase)) { schema = s; } }
            }
            SchemaError = step.Schema.Length > 0 && schema == null ? DisplayText.Plain("The server didn't send the '" + step.Schema + "' stat block.") : string.Empty;
            HasSchema = schema != null;
            if (schema == null) { Stats = new List<StatFieldViewModel>(); return; }
            var values = d.Get(step.Key);
            string group = null;
            Stats = ItemList.Sync(_stats, schema.Fields, delegate (StatBlockField f) { return f.Key; },
                delegate (StatBlockField f) { return new StatFieldViewModel(f, delegate (StatFieldViewModel vm, string text) { Run(Controller.BuilderStatField(Step.Key, vm.Field, text)); }); },
                delegate (StatFieldViewModel vm, StatBlockField f)
                {
                    bool starts = f.Group.Length > 0 && f.Group != group;
                    if (starts) { group = f.Group; }
                    vm.GroupTitle = starts ? DisplayText.Plain(f.Group.ToUpperInvariant()) : string.Empty;
                    var current = values.Get(f.Key);
                    vm.Show(current.Kind == JsonKind.Number ? ((int)current.NumberValue).ToString() : current.Kind == JsonKind.String ? current.StringValue : string.Empty);
                });
        }
    }
}
