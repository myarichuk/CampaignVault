using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.World
{
    /// <summary>A character built for the party: name, class line, level, and EDIT (Templates/Onboarding/PartyCard.uxml).</summary>
    public sealed class PartyMemberViewModel : ViewModel, IKeyed
    {
        private string _title = string.Empty;
        private string _line = string.Empty;

        public PartyMemberViewModel(PartyMember member, VaultController controller)
        {
            Key = member.Id;
            string id = member.Id;
            Edit = delegate { controller.OpenPartyBuilder(id); };
            EditName = "party-edit-" + Slug(member.Name);
        }

        public string Key { get; private set; }
        [CreateProperty] public string EditName { get; private set; }
        [CreateProperty] public Action Edit { get; private set; }
        [CreateProperty] public string Title { get { return _title; } private set { Set(ref _title, value); } }
        [CreateProperty] public string Line { get { return _line; } private set { Set(ref _line, value); } }

        public void Update(PartyMember member)
        {
            Title = DisplayText.Plain(member.Name.Length > 0 ? member.Name : "Unnamed");
            var parts = new List<string>();
            // The class line already ends in the level ("Fighter 1").
            parts.Add(member.ClassLine.Length > 0 ? member.ClassLine : "level " + member.Level);
            if (member.Kind == "companion") { parts.Add("companion"); }
            Line = DisplayText.Plain(string.Join(" · ", parts.ToArray()));
        }

        private static string Slug(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in name.ToLowerInvariant()) { sb.Append(char.IsLetterOrDigit(c) ? c : '-'); }
            return sb.ToString().Trim('-');
        }
    }

    /// <summary>
    /// The party step: the characters built so far (each with EDIT), the starting level, and the ways to fill the
    /// party. Characters come from the builder, which opens over this page; what it saved is in
    /// <see cref="OnboardingState.Party"/>, so this page can be rebuilt freely.
    /// </summary>
    public sealed class PartyPageViewModel : QuestionPageViewModel
    {
        private readonly VaultController _c;
        private readonly List<ChoiceViewModel> _levels = new List<ChoiceViewModel>();
        private readonly ActionViewModel _add;
        private readonly ActionViewModel _drafts;
        private readonly ActionViewModel _table;
        private readonly ActionViewModel _use;
        private List<PartyMemberViewModel> _members = new List<PartyMemberViewModel>();
        private List<ActionViewModel> _actions = new List<ActionViewModel>();
        private bool _showLevel = true;
        private bool _empty = true;

        public PartyPageViewModel(VaultController controller)
        {
            _c = controller;
            for (int level = 1; level <= VaultController.MaxBuilderLevel; level++)
            {
                int pick = level;
                _levels.Add(new ChoiceViewModel("level-" + level, level.ToString(), null, true, delegate { _c.SetPartyLevel(pick); }, "party-level-" + level));
            }
            _add = new ActionViewModel("add", "party-add", delegate { _c.OpenPartyBuilder(string.Empty); }, primary: true);
            _drafts = new ActionViewModel("drafts", "party-dm", delegate { _c.SubmitParty(OnboardingState.PartyDmDrafts); });
            _table = new ActionViewModel("table", "party-table", delegate { _c.SubmitParty(OnboardingState.PartyBuildAtTable); }, ghost: true);
            _use = new ActionViewModel("use", "party-use", delegate { _c.SubmitParty(OnboardingState.PartyBuildNow); }, primary: true);
        }

        public override string Template { get { return "Onboarding/PartyPage"; } }

        [CreateProperty] public List<ChoiceViewModel> Levels { get { return _levels; } }
        [CreateProperty] public bool ShowLevel { get { return _showLevel; } private set { Set(ref _showLevel, value); } }
        [CreateProperty] public bool Empty { get { return _empty; } private set { Set(ref _empty, value); } }
        [CreateProperty] public List<PartyMemberViewModel> Members { get { return _members; } private set { SetList(ref _members, value); } }
        [CreateProperty] public List<ActionViewModel> Actions { get { return _actions; } private set { SetList(ref _actions, value); } }

        /// <summary>The server's help describes the answer's JSON for other clients; this page builds it from buttons.</summary>
        protected override string HelpOf(OnboardingQuestion q)
        {
            return "Build your characters now, let the DM draft the party for you to review, or leave it for the table.";
        }

        public override void Update(OnboardingState ob)
        {
            base.Update(ob);
            bool narrative = ob.System == "Narrative";
            ShowLevel = !narrative;
            for (int i = 0; i < _levels.Count; i++) { _levels[i].Update((i + 1).ToString(), null, ob.PartyLevel == i + 1); }
            Members = ItemList.Sync(_members, ob.Party, delegate (PartyMember m) { return m.Id; },
                delegate (PartyMember m) { return new PartyMemberViewModel(m, _c); },
                delegate (PartyMemberViewModel vm, PartyMember m) { vm.Update(m); });
            Empty = ob.Party.Count == 0;

            _add.Show(ob.Party.Count == 0 ? "ADD A CHARACTER" : "ADD ANOTHER", "character", !narrative,
                narrative ? "A narrative game has no character sheets to build." : "Open the character builder.");
            _drafts.Show("DM DRAFTS THE PARTY", "spark", Empty, Empty ? "The Dungeon Master drafts the party for you to review." : "You have started building characters: use them, or add more.");
            _table.Show("WE'LL BUILD AT THE TABLE", "chevron", Empty, Empty ? "Skip this: the Dungeon Master walks you through it before the first scene." : "You have started building characters: use them, or add more.");
            _use.Show("USE THIS PARTY", "check", !Empty);
            var actions = new List<ActionViewModel> { _add };
            if (!Empty) { actions.Add(_use); }
            actions.Add(_drafts);
            actions.Add(_table);
            Actions = actions;
        }
    }
}
