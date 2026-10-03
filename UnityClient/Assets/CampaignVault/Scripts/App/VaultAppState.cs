using System;
using System.Collections.Generic;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.App
{
    /// <summary>Which part of the state moved, so a view repaints only what it shows.</summary>
    [Flags]
    public enum StateArea
    {
        None = 0,
        Campaign = 1 << 0,
        Session = 1 << 1,
        Pc = 1 << 2,
        Companions = 1 << 3,
        Campaigns = 1 << 4,
        Search = 1 << 5,
        Tools = 1 << 6,
        Onboarding = 1 << 7,
        Connection = 1 << 8,
        Driver = 1 << 9,
        Embedded = 1 << 10,
        Preferences = 1 << 11,
        Providers = 1 << 12,
        Busy = 1 << 13,
        Plugins = 1 << 14,
        Builder = 1 << 15,
        LevelUp = 1 << 16,
        All = ~0,
    }

    public enum ToastKind { Info, Success, Warning, Error }

    public enum ConnectionStatus { Unknown, Checking, Healthy, Down }

    public sealed class CampaignRow
    {
        public string Slug = string.Empty;
        public string Display = string.Empty;
        public string System = string.Empty;
    }

    public sealed class CompanionEntry
    {
        public string Id = string.Empty;
        /// <summary>Null when the lookup failed (see Error).</summary>
        public Companion Companion;
        public string Error = string.Empty;
    }

    public sealed class SearchHit
    {
        public string Title = string.Empty;
        public string Id = string.Empty;
        public string Detail = string.Empty;
    }

    public sealed class ToolToggle
    {
        public string Name = string.Empty;
        public string Description = string.Empty;
        /// <summary>"play" or "build".</summary>
        public string Connector = string.Empty;
    }

    public enum OnboardingPhase { Idle, Working, Question, ReadyToFinalize, Done, Failed }

    public enum AnswerType { Text, Choice, YesNo, List, Number, Party }

    public sealed class OnboardingQuestion
    {
        public string Key = string.Empty;
        public string Text = string.Empty;
        public string Help = string.Empty;
        public AnswerType Type;
        public List<string> Options = new List<string>();
    }

    /// <summary>One character built for the party during onboarding, with the draft that made it (for EDIT).</summary>
    public sealed class PartyMember
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string ClassLine = string.Empty;
        public int Level = 1;
        public string Kind = "pc";
        public CharacterDraft Draft = new CharacterDraft();
        /// <summary>Drafted by the DM and not reviewed yet: Id is a placeholder ("draft-1"), nothing is saved until the builder commits it.</summary>
        public bool Pending;
        /// <summary>What the drafter changed or dropped, and what the preview objected to: shown on the card until it is reviewed.</summary>
        public readonly List<string> Issues = new List<string>();
    }

    /// <summary>The level-up menu for one character: loaded from character_level_up, picked like the builder's level choices.</summary>
    public sealed class LevelUpState
    {
        public string CharacterId = string.Empty;
        public bool Loading;
        public bool Applying;
        public string Error = string.Empty;
        public LevelUpOffer Offer;
        /// <summary>The builder's level choice: slot id → option id(s).</summary>
        public JsonValue Choice;
        /// <summary>Set when the level was gained: the overlay closes itself.</summary>
        public bool Done;
    }

    public sealed class OnboardingState
    {
        public const string PartyBuildNow = "build-now";
        public const string PartyDmDrafts = "dm-drafts";
        public const string PartyBuildAtTable = "build-at-table";

        // Must match the server's onboarding "system" question options verbatim.
        public static readonly string[] SystemOptions = { "Dnd5e", "Pathfinder2e", "Narrative" };
        public static readonly string[] SystemLabels = { "D&D 5e", "Pathfinder 2e", "Narrative" };

        public OnboardingPhase Phase;
        public string Slug = string.Empty;
        public string System = SystemOptions[0];
        /// <summary>Answers given up front, submitted automatically when their question comes up.</summary>
        public readonly Dictionary<string, string> Prefilled = new Dictionary<string, string>();
        public OnboardingQuestion Question;
        public int Answered;
        public string Progress = string.Empty;
        public string Status = string.Empty;
        public string Error = string.Empty;
        public string DoneSummary = string.Empty;
        public readonly List<string> NextSteps = new List<string>();
        /// <summary>The server's written-up answers and seeding steps, sent to the DM with "seed the world".</summary>
        public string SeedBrief = string.Empty;
        /// <summary>Answers recorded so far (question key → answer), as the server reports them.</summary>
        public readonly Dictionary<string, string> Answers = new Dictionary<string, string>();
        /// <summary>The party step: characters built so far (kept across the builder round trips), and the starting level.</summary>
        public readonly List<PartyMember> Party = new List<PartyMember>();
        public int PartyLevel = 1;
        /// <summary>The DM's suggested starting level from the campaign so far (0 = none yet), and why. Only ever a suggestion: the player's pick is PartyLevel.</summary>
        public int LevelSuggestion;
        public string LevelReason = string.Empty;
        public bool SuggestingLevel;
        /// <summary>The DM was asked once for this campaign: the step asks on its own only the first time.</summary>
        public bool LevelSuggestionAsked;
        /// <summary>"DM drafts companions": the one model call is out.</summary>
        public bool Drafting;
        public string DraftError = string.Empty;

        /// <summary>Pre-fills the current question's field (a brainstormed write-up); cleared per question.</summary>
        public string Draft = string.Empty;
        /// <summary>The brainstorm reply being typed, kept across repaints.</summary>
        public string BrainstormDraft = string.Empty;

        // Brainstorming the current question with the model (a side chat; nothing reaches the server).
        public bool Brainstorming;
        public bool BrainstormBusy;
        /// <summary>(role, text): "user" or "assistant".</summary>
        public readonly List<KeyValuePair<string, string>> BrainstormChat = new List<KeyValuePair<string, string>>();
        public string BrainstormLive = string.Empty;
        public string BrainstormError = string.Empty;

        public void ClearBrainstorm()
        {
            Brainstorming = false;
            BrainstormBusy = false;
            BrainstormChat.Clear();
            BrainstormDraft = string.Empty;
            BrainstormLive = string.Empty;
            BrainstormError = string.Empty;
        }
    }

    /// <summary>What a step offers for the current draft (character_builder action=options).</summary>
    public sealed class StepOptions
    {
        public readonly List<BuilderOption> Options = new List<BuilderOption>();
        /// <summary>Picks the step wants, or -1 when the recipe fixes none.</summary>
        public int Count = -1;
        /// <summary>A spells step's picks per group (cantrips, known, prepared).</summary>
        public readonly Dictionary<string, int> GroupCounts = new Dictionary<string, int>();
        /// <summary>A levelChoices step's slots, in level order (each option's group is a slot id).</summary>
        public readonly List<LevelSlot> Slots = new List<LevelSlot>();
        public string Error = string.Empty;

        public int GroupCount(string group)
        {
            int n;
            return GroupCounts.TryGetValue(group, out n) ? n : 0;
        }
    }

    /// <summary>The ability-score step's working state: the method, its pool, and which ability got which value.</summary>
    public sealed class AbilityWork
    {
        public static readonly string[] Abilities = { "Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma" };

        /// <summary>standardArray, pointBuy or roll.</summary>
        public string Method = string.Empty;
        /// <summary>The values to assign (the standard array, or the rolls).</summary>
        public readonly List<int> Pool = new List<int>();
        /// <summary>Ability → index into Pool (array and roll).</summary>
        public readonly Dictionary<string, int> Assigned = new Dictionary<string, int>();
        /// <summary>Ability → score (point buy).</summary>
        public readonly Dictionary<string, int> Bought = new Dictionary<string, int>();
        /// <summary>Every roll made, newest last; it stays on screen.</summary>
        public readonly List<string> RollLog = new List<string>();

        /// <summary>Ability → base score, for whatever the method has filled in so far.</summary>
        public Dictionary<string, int> Scores()
        {
            var scores = new Dictionary<string, int>();
            foreach (string ability in Abilities)
            {
                int index;
                int bought;
                if (Method == "pointBuy" && Bought.TryGetValue(ability, out bought)) { scores[ability] = bought; }
                else if (Method != "pointBuy" && Assigned.TryGetValue(ability, out index) && index >= 0 && index < Pool.Count) { scores[ability] = Pool[index]; }
            }
            return scores;
        }
    }

    /// <summary>
    /// The character builder, kept in state so the draft survives redraws and closing the dialog. Session-only: a
    /// draft that was never committed is gone when the app quits (open question 3's default).
    /// </summary>
    public sealed class BuilderState
    {
        /// <summary>The campaign the draft belongs to; a different campaign starts a new draft.</summary>
        public string Slug = string.Empty;
        public string System = string.Empty;
        public CharacterDraft Draft = new CharacterDraft();
        /// <summary>The recipe's steps for this draft (a step whose condition fails isn't listed).</summary>
        public readonly List<BuilderStep> Steps = new List<BuilderStep>();
        public readonly List<StatBlockSchema> StatBlocks = new List<StatBlockSchema>();
        public string Current = string.Empty;
        /// <summary>The recipe's highest level (from the server's steps); 0 until they load.</summary>
        public int MaxLevel;
        /// <summary>Step key → its options, for the draft as it was when they loaded.</summary>
        public readonly Dictionary<string, StepOptions> Options = new Dictionary<string, StepOptions>(StringComparer.OrdinalIgnoreCase);
        public readonly AbilityWork Abilities = new AbilityWork();
        /// <summary>Step key → the option filter as typed.</summary>
        public readonly Dictionary<string, string> Filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The derived sheet from the last preview; null before the first.</summary>
        public CharacterSheet Preview;
        public readonly List<BuilderIssue> Errors = new List<BuilderIssue>();
        public readonly List<BuilderIssue> Warnings = new List<BuilderIssue>();
        public readonly List<string> Notes = new List<string>();
        /// <summary>The preview matches the draft (no change since it was asked for).</summary>
        public bool PreviewCurrent;
        /// <summary>"Changing class cleared: skills, spells." Shown until the next change.</summary>
        public string ClearedNote = string.Empty;
        public string Error = string.Empty;
        /// <summary>Set once committed; committing again updates this character.</summary>
        public string CommittedId = string.Empty;
        /// <summary>The draft belongs to the campaign being set up (the onboarding party step), not the one at the table.</summary>
        public bool ForOnboarding;
        /// <summary>The party card this draft came from when it is a DM draft under review (its placeholder id); the commit replaces that card.</summary>
        public string PendingKey = string.Empty;
        /// <summary>Bumped on every draft change, so a slow reply about an older draft is dropped.</summary>
        public int Revision;

        // Asking the DM about the current step (the onboarding conversation carries it).
        public string AskDraft = string.Empty;
        public bool AskBusy;
        public string AskError = string.Empty;
        public string AskStep = string.Empty;
        public string AskReply = string.Empty;
        /// <summary>Option ids the DM suggested that the step offers.</summary>
        public readonly List<string> Suggested = new List<string>();
        /// <summary>Ids the DM suggested that aren't options: shown, never dropped silently.</summary>
        public readonly List<string> NotOptions = new List<string>();

        // "The DM fills the rest": one model call proposes picks for every open step; the player reviews them.
        public bool FillBusy;
        public string FillError = string.Empty;
        /// <summary>What the DM said about its picks, shown until dismissed or the next fill.</summary>
        public string FillReply = string.Empty;
        /// <summary>The titles of the steps the DM filled.</summary>
        public readonly List<string> Filled = new List<string>();
        /// <summary>Step key → the DM's picks that weren't options there ("pyromancy (Level 2 · Arcane Tradition)"): left open, flagged on the step.</summary>
        public readonly Dictionary<string, List<string>> Rejected = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public BuilderStep Step(string key)
        {
            int i = BuilderDependencies.IndexOf(Steps, key);
            return i >= 0 ? Steps[i] : null;
        }

        public BuilderStep CurrentStep { get { return Step(Current); } }

        public List<BuilderIssue> IssuesFor(string key, bool warnings)
        {
            var list = new List<BuilderIssue>();
            foreach (var issue in warnings ? Warnings : Errors)
            {
                if (string.Equals(issue.Step, key, StringComparison.OrdinalIgnoreCase)) { list.Add(issue); }
            }
            return list;
        }

        public void Reset(string slug, string system, string kind)
        {
            ForOnboarding = false;
            PendingKey = string.Empty;
            Slug = slug ?? string.Empty;
            System = system ?? string.Empty;
            Draft = new CharacterDraft { Kind = kind ?? "pc" };
            Steps.Clear();
            StatBlocks.Clear();
            Current = string.Empty;
            Options.Clear();
            Filters.Clear();
            Abilities.Method = string.Empty;
            Abilities.Pool.Clear();
            Abilities.Assigned.Clear();
            Abilities.Bought.Clear();
            Abilities.RollLog.Clear();
            Preview = null;
            Errors.Clear();
            Warnings.Clear();
            Notes.Clear();
            PreviewCurrent = false;
            ClearedNote = string.Empty;
            Error = string.Empty;
            CommittedId = string.Empty;
            Revision++;
            AskDraft = string.Empty;
            AskBusy = false;
            AskError = string.Empty;
            AskStep = string.Empty;
            AskReply = string.Empty;
            Suggested.Clear();
            NotOptions.Clear();
            MaxLevel = 0;
            ClearFill();
        }

        public void ClearFill()
        {
            FillBusy = false;
            FillError = string.Empty;
            FillReply = string.Empty;
            Filled.Clear();
            Rejected.Clear();
        }
    }

    /// <summary>The fields of an end-of-session handoff, as typed.</summary>
    public sealed class HandoffDraft
    {
        public string LastSession = string.Empty;
        public string StorySoFar = string.Empty;
        public string Threads = string.Empty;
        public string Npcs = string.Empty;
        public string Intent = string.Empty;
        public string Tone = string.Empty;
    }

    /// <summary>
    /// Everything the client knows, in one place, with no UI in it. Views
    /// read it and subscribe to Changed; only VaultController writes it.
    /// Replaces the old VaultUiContext.
    /// </summary>
    public sealed class VaultAppState
    {
        // ---- services ----
        public VaultClientConfig Config;
        public McpClient Mcp;
        public ByokSettings Byok;
        public OpenAiChatDriver Driver;
        public SystemPromptProvider Prompts;
        public ServerHostManager Server;

        // ---- the table ----
        public readonly VaultTranscript Transcript = new VaultTranscript();
        /// <summary>The client's saved history per campaign; null = nothing is kept (smoke runs, tests).</summary>
        public TranscriptStore Store;
        public string CampaignSlug { get { return Prompts != null ? Prompts.CampaignSlug : string.Empty; } }
        public string Ruleset { get { return Prompts != null ? Prompts.Ruleset : string.Empty; } }
        public bool HasCampaign { get { return !string.IsNullOrEmpty(CampaignSlug); } }

        /// <summary>
        /// Settings look complete and the provider hasn't refused them. Play (the
        /// command bar, brainstorming) waits on this; reason says what to fix.
        /// </summary>
        public bool ProviderReady(out string reason)
        {
            if (Byok == null) { reason = "No AI provider settings loaded."; return false; }
            if (!Byok.Validate(out reason)) { return false; }
            if (Driver != null && !string.IsNullOrEmpty(Driver.ProviderProblem))
            {
                reason = char.ToUpperInvariant(Driver.ProviderProblem[0]) + Driver.ProviderProblem.Substring(1);
                return false;
            }
            return true;
        }
        public SessionDigest Session;
        public string SessionStatus = string.Empty;
        /// <summary>
        /// The campaign has no player character yet (fresh from onboarding): lines go
        /// to the DM as out-of-character setup talk, and the first session opens once
        /// the party exists.
        /// </summary>
        public bool SetupPending;
        public List<string> HandoffIssues = new List<string>();

        // ---- party ----
        public string PcId = string.Empty;
        public PcSheet Pc;
        public JsonValue PcEntity;
        public string PcError = string.Empty;
        public readonly List<string> CompanionIds = new List<string>();
        public readonly List<CompanionEntry> Companions = new List<CompanionEntry>();

        // ---- world ----
        public readonly List<CampaignRow> Campaigns = new List<CampaignRow>();
        public string CampaignsError = string.Empty;
        public bool CampaignsLoaded;
        public readonly List<SearchHit> SearchResults = new List<SearchHit>();
        public string SearchSummary = string.Empty;
        public string SearchError = string.Empty;
        public readonly List<ToolToggle> Tools = new List<ToolToggle>();
        public readonly Dictionary<string, string> ToolsErrors = new Dictionary<string, string>();
        public readonly OnboardingState Onboarding = new OnboardingState();
        public readonly BuilderState Builder = new BuilderState();
        /// <summary>The level-up menu (Templates/Sheet/LevelUp.uxml): what the next level offers, and the picks so far.</summary>
        public readonly LevelUpState LevelUp = new LevelUpState();
        /// <summary>Party members whose earned level the player was already told about ("id@level"), so the toast comes once.</summary>
        public readonly HashSet<string> LevelUpAnnounced = new HashSet<string>();
        /// <summary>The connected server's GET /plugins, as of the last load.</summary>
        public readonly List<PluginEntry> Plugins = new List<PluginEntry>();
        public string PluginsError = string.Empty;
        public bool PluginsLoaded;
        public string PluginsEngineVersion = string.Empty;
        /// <summary>Installed, removed or toggled since the embedded server started: the list is stale until it restarts.</summary>
        public bool PluginsRestartNeeded;

        // ---- plumbing ----
        public ConnectionStatus Connection = ConnectionStatus.Unknown;
        public string ConnectionMessage = string.Empty;
        public string EmbeddedMessage = string.Empty;
        public bool FxEnabled = true;
        public bool SfxMuted;
        /// <summary>Story text size step, 0 (smallest) to StoryTextSizes.Length - 1; see StoryTextSizes.</summary>
        public int StoryTextSize = DefaultStoryTextSize;

        /// <summary>Narration font sizes (panel px at 1080p) the story text steps through.</summary>
        public static readonly int[] StoryTextSizes = { 16, 18, 21, 24, 27, 31 };
        public const int DefaultStoryTextSize = 2;

        private readonly HashSet<string> _busy = new HashSet<string>();

        /// <summary>Some part of the state changed.</summary>
        public event Action<StateArea> Changed;
        /// <summary>A transient notice for the player (replaces the old system-line Notes).</summary>
        public event Action<string, ToastKind> Toast;
        /// <summary>Chat was tried without a usable provider: the UI should open first-run setup.</summary>
        public event Action SetupRequested;
        /// <summary>Chat was tried with no campaign at the table: the UI should open the campaign book.</summary>
        public event Action CampaignsRequested;
        /// <summary>The onboarding party step wants the builder: a new character of this kind (pc or companion) when the id is empty, else the one with this id.</summary>
        public event Action<string, string> PartyBuilderRequested;
        /// <summary>A party member was saved from the builder opened over the setup dialogue: the builder closes and the dialogue continues.</summary>
        public event Action PartyBuilderDone;
        public void FinishPartyBuilder() { if (PartyBuilderDone != null) { PartyBuilderDone(); } }

        public void RequestPartyBuilder(string editId, string kind)
        {
            if (PartyBuilderRequested != null) { PartyBuilderRequested(editId ?? string.Empty, string.IsNullOrEmpty(kind) ? "pc" : kind); }
        }

        public void Notify(StateArea area)
        {
            if (Changed != null) { Changed(area); }
        }

        public void RaiseToast(string message, ToastKind kind)
        {
            if (Toast != null) { Toast(TextSanitizer.Clean(message, 400), kind); }
        }

        public void RequestSetup()
        {
            if (SetupRequested != null) { SetupRequested(); }
        }

        public void RequestCampaigns()
        {
            if (CampaignsRequested != null) { CampaignsRequested(); }
        }

        /// <summary>One in-flight operation per name: a second tap mid-call never forks a duplicate.</summary>
        public bool IsBusy(string operation) { return _busy.Contains(operation); }

        internal bool TryBeginBusy(string operation)
        {
            if (!_busy.Add(operation)) { return false; }
            Notify(StateArea.Busy);
            return true;
        }

        internal void EndBusy(string operation)
        {
            if (_busy.Remove(operation)) { Notify(StateArea.Busy); }
        }

        /// <summary>The PC's party row from the last start_session (gear lives here).</summary>
        public DashboardMember PcMember
        {
            get
            {
                if (Session == null) { return null; }
                foreach (var m in Session.Party) { if (m.Id == PcId) { return m; } }
                return null;
            }
        }

        /// <summary>Equipped first, then carried.</summary>
        public List<InventoryItem> Inventory()
        {
            var items = new List<InventoryItem>();
            var member = PcMember;
            if (member == null) { return items; }
            foreach (string n in member.Equipped) { items.Add(new InventoryItem { Name = n, Equipped = true }); }
            foreach (string n in member.Carried) { items.Add(new InventoryItem { Name = n, Equipped = false }); }
            return items;
        }

        public bool IsToolEnabled(string name)
        {
            return Driver == null || Driver.AllowedTools.Count == 0 || Driver.AllowedTools.Contains(name);
        }
    }
}
