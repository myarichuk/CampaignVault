using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.World
{
    /// <summary>
    /// A character built for the party: name, class line, level, and EDIT (Templates/Onboarding/PartyCard.uxml). A DM
    /// draft not reviewed yet says so, lists what the drafter changed and the preview objected to, and offers REVIEW;
    /// saving it in the builder replaces the card (a new view model, since the id changes).
    /// </summary>
    public sealed class PartyMemberViewModel : ViewModel, IKeyed
    {
        private string _title = string.Empty;
        private string _line = string.Empty;
        private string _issues = string.Empty;

        public PartyMemberViewModel(PartyMember member, VaultController controller)
        {
            Key = member.Id;
            string id = member.Id;
            string kind = member.Kind;
            Edit = delegate { controller.OpenPartyBuilder(id, kind); };
            EditName = (member.Pending ? "party-review-" : "party-edit-") + Slug(member.Name);
            EditLabel = member.Pending ? "REVIEW" : "EDIT";
        }

        public string Key { get; private set; }
        [CreateProperty] public string EditName { get; private set; }
        [CreateProperty] public string EditLabel { get; private set; }
        [CreateProperty] public Action Edit { get; private set; }
        [CreateProperty] public string Title { get { return _title; } private set { Set(ref _title, value); } }
        [CreateProperty] public string Line { get { return _line; } private set { Set(ref _line, value); } }
        [CreateProperty] public string Issues { get { return _issues; } private set { Set(ref _issues, value); } }

        /// <param name="narrative">A narrative game has no class or level: the card shows the character's concept instead.</param>
        public void Update(PartyMember member, bool narrative = false)
        {
            Title = DisplayText.Plain(member.Name.Length > 0 ? member.Name : "Unnamed");
            var parts = new List<string>();
            // The class line already ends in the level ("Fighter 1").
            if (narrative) { if (member.Draft.Concept.Length > 0) { parts.Add(member.Draft.Concept); } }
            else { parts.Add(member.ClassLine.Length > 0 ? member.ClassLine : "level " + member.Level); }
            if (member.Kind == "companion") { parts.Add("companion"); }
            if (member.Pending) { parts.Add("DM draft, not saved: review it"); }
            Line = DisplayText.Plain(string.Join(" · ", parts.ToArray()));
            Issues = DisplayText.Plain(string.Join("\n", member.Issues.ToArray()));
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
        private readonly ActionViewModel _suggest;
        private readonly ActionViewModel _takeSuggestion;
        private string _levelLabel = string.Empty;
        private string _levelTier = string.Empty;
        private string _suggestionText = string.Empty;
        private bool _canLower;
        private bool _canRaise;
        private bool _hasSuggestion;
        private readonly ActionViewModel _add;
        private readonly ActionViewModel _addCompanion;
        private readonly ActionViewModel _drafts;
        private readonly ActionViewModel _table;
        private readonly ActionViewModel _use;
        private readonly ActionViewModel _discard;
        private List<PartyMemberViewModel> _members = new List<PartyMemberViewModel>();
        private List<ActionViewModel> _actions = new List<ActionViewModel>();
        private List<ActionViewModel> _suggestionActions = new List<ActionViewModel>();
        private bool _showLevel = true;
        private bool _empty = true;
        private string _draftError = string.Empty;

        public PartyPageViewModel(VaultController controller)
        {
            _c = controller;
            Lower = delegate { _c.SetPartyLevel(_c.State.Onboarding.PartyLevel - 1); };
            Raise = delegate { _c.SetPartyLevel(_c.State.Onboarding.PartyLevel + 1); };
            _suggest = new ActionViewModel("suggest-level", "party-level-suggest", delegate { _c.SuggestPartyLevel(); }, ghost: true);
            _takeSuggestion = new ActionViewModel("take-level", "party-level-take", delegate { _c.SetPartyLevel(_c.State.Onboarding.LevelSuggestion); });
            _add = new ActionViewModel("add", "party-add", delegate { _c.OpenPartyBuilder(string.Empty); }, primary: true);
            _addCompanion = new ActionViewModel("add-companion", "party-add-companion", delegate { _c.OpenPartyBuilder(string.Empty, "companion"); });
            _drafts = new ActionViewModel("drafts", "party-dm", delegate { _c.DraftCompanions(); });
            _discard = new ActionViewModel("discard", "party-discard", delegate { _c.DiscardDrafts(); }, ghost: true);
            _table = new ActionViewModel("table", "party-table", delegate { _c.SubmitParty(OnboardingState.PartyBuildAtTable); }, ghost: true);
            _use = new ActionViewModel("use", "party-use", delegate { _c.SubmitParty(OnboardingState.PartyBuildNow); }, primary: true);
        }

        public override string Template { get { return "Onboarding/PartyPage"; } }

        /// <summary>The level stepper: 1 to the builder's cap for the chosen system (5e: 20; PF2e: 3).</summary>
        [CreateProperty] public string LevelLabel { get { return _levelLabel; } private set { Set(ref _levelLabel, value); } }
        /// <summary>What the level means in play ("Tier 2 · local heroes").</summary>
        [CreateProperty] public string LevelTier { get { return _levelTier; } private set { Set(ref _levelTier, value); } }
        [CreateProperty] public bool CanLower { get { return _canLower; } private set { Set(ref _canLower, value); } }
        [CreateProperty] public bool CanRaise { get { return _canRaise; } private set { Set(ref _canRaise, value); } }
        [CreateProperty] public Action Lower { get; private set; }
        [CreateProperty] public Action Raise { get; private set; }
        /// <summary>The DM's read on the level from the plot, shown with buttons to take it or ask again. Never applied on its own.</summary>
        [CreateProperty] public string SuggestionText { get { return _suggestionText; } private set { Set(ref _suggestionText, value); } }
        [CreateProperty] public bool HasSuggestion { get { return _hasSuggestion; } private set { Set(ref _hasSuggestion, value); } }
        [CreateProperty] public List<ActionViewModel> SuggestionActions { get { return _suggestionActions; } private set { SetList(ref _suggestionActions, value); } }
        [CreateProperty] public bool ShowLevel { get { return _showLevel; } private set { Set(ref _showLevel, value); } }
        [CreateProperty] public bool Empty { get { return _empty; } private set { Set(ref _empty, value); } }
        [CreateProperty] public string DraftError { get { return _draftError; } private set { Set(ref _draftError, value); } }
        [CreateProperty] public List<PartyMemberViewModel> Members { get { return _members; } private set { SetList(ref _members, value); } }
        [CreateProperty] public List<ActionViewModel> Actions { get { return _actions; } private set { SetList(ref _actions, value); } }

        /// <summary>The server's help describes the answer's JSON for other clients; this page builds it from buttons.</summary>
        protected override string HelpOf(OnboardingQuestion q)
        {
            return "Build your characters now (the DM can draft companions around them for you to review), or leave it all for the table.";
        }

        /// <summary>Levels in the SRD's own tiers (5e: 1-4, 5-10, 11-16, 17-20); a shorter ladder is split in thirds.</summary>
        internal static string TierOf(int level, int max)
        {
            if (max >= 20)
            {
                return level <= 4 ? "Apprentice: local heroes" : level <= 10 ? "Seasoned: heroes of the realm" : level <= 16 ? "Veteran: masters of their craft" : "Legend: world-shaking";
            }
            return level <= 1 ? "Fresh start" : "Seasoned adventurers";
        }

        public override void Update(OnboardingState ob)
        {
            base.Update(ob);
            bool narrative = ob.System == "Narrative";
            ShowLevel = !narrative;
            int maxLevel = VaultController.MaxBuilderLevelFor(VaultController.RulesetOf(ob.System));
            LevelLabel = "LEVEL " + ob.PartyLevel;
            LevelTier = TierOf(ob.PartyLevel, maxLevel);
            CanLower = ob.PartyLevel > 1;
            CanRaise = ob.PartyLevel < maxLevel;
            bool differs = ob.LevelSuggestion > 0 && ob.LevelSuggestion != ob.PartyLevel;
            HasSuggestion = ob.SuggestingLevel || ob.LevelSuggestion > 0;
            SuggestionText = ob.SuggestingLevel ? "The DM is reading your plot…"
                : ob.LevelSuggestion > 0 ? "The DM suggests level " + ob.LevelSuggestion + (ob.LevelReason.Length > 0 ? ": " + ob.LevelReason : ".")
                : string.Empty;
            _takeSuggestion.Show("USE LEVEL " + ob.LevelSuggestion, "check", differs);
            _suggest.Show("ASK AGAIN", "spark", !ob.SuggestingLevel);
            SuggestionActions = differs ? new List<ActionViewModel> { _takeSuggestion, _suggest } : new List<ActionViewModel> { _suggest };
            Members = ItemList.Sync(_members, ob.Party, delegate (PartyMember m) { return m.Id; },
                delegate (PartyMember m) { return new PartyMemberViewModel(m, _c); },
                delegate (PartyMemberViewModel vm, PartyMember m) { vm.Update(m, narrative); });
            Empty = ob.Party.Count == 0;

            bool pending = VaultController.HasPendingDrafts(ob);
            bool pc = VaultController.HasBuiltPc(ob);
            DraftError = DisplayText.Plain(ob.DraftError);
            // A narrative game's builder asks only who they are: name, concept, look, descriptors, drives and fears.
            // One player character: once built, EDIT on its card changes it, and the button goes away.
            _add.Show("CREATE PLAYER CHARACTER", "character", true,
                narrative ? "Open the character builder: who you are, no stats." : "Open the character builder for your character.");
            _addCompanion.Show("ADD A COMPANION", "character", true,
                narrative ? "Someone who travels with the party: who they are, no stats."
                : "A hireling, pet or ally who travels with the party: pick a template or write a stat block.");
            // Drafted companions share a history with the player's characters, so there must be one to write them around.
            // The DM drafts stat blocks, so a narrative game adds its companions by hand.
            _drafts.Show(ob.Drafting ? "DRAFTING…" : pending ? "DRAFT AGAIN" : "DM DRAFTS COMPANIONS", "spark", !narrative && pc && !ob.Drafting,
                narrative ? "A narrative game has no stat blocks to draft: add companions yourself."
                : !pc ? "Build your character first: the DM drafts companions around them."
                : "One request: the DM drafts companions with a shared history, for you to review one by one.");
            _discard.Show("DISCARD DRAFTS", "close", pending && !ob.Drafting, "Drop the DM's drafts that aren't saved.");
            _table.Show("WE'LL BUILD AT THE TABLE", "chevron", Empty, Empty ? "Skip this: the Dungeon Master walks you through it before the first scene." : "You have started building characters: use them, or add more.");
            _use.Show("USE THIS PARTY", "check", !Empty && !pending && !ob.Drafting, pending ? "Review or discard the DM's drafts first." : null);
            var actions = new List<ActionViewModel>();
            if (!pc) { actions.Add(_add); }
            actions.Add(_addCompanion);
            if (!Empty) { actions.Add(_use); }
            actions.Add(_drafts);
            if (pending) { actions.Add(_discard); }
            actions.Add(_table);
            Actions = actions;
        }
    }
}
