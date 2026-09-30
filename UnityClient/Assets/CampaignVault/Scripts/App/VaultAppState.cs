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

    public enum AnswerType { Text, Choice, YesNo, List }

    public sealed class OnboardingQuestion
    {
        public string Key = string.Empty;
        public string Text = string.Empty;
        public string Help = string.Empty;
        public AnswerType Type;
        public List<string> Options = new List<string>();
    }

    public sealed class OnboardingState
    {
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
        public string CampaignSlug { get { return Prompts != null ? Prompts.CampaignSlug : string.Empty; } }
        public string Ruleset { get { return Prompts != null ? Prompts.Ruleset : string.Empty; } }
        public bool HasCampaign { get { return !string.IsNullOrEmpty(CampaignSlug); } }
        public SessionDigest Session;
        public string SessionStatus = string.Empty;
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

        private readonly HashSet<string> _busy = new HashSet<string>();

        /// <summary>Some part of the state changed.</summary>
        public event Action<StateArea> Changed;
        /// <summary>A transient notice for the player (replaces the old system-line Notes).</summary>
        public event Action<string, ToastKind> Toast;
        /// <summary>Chat was tried without a usable provider: the UI should open first-run setup.</summary>
        public event Action SetupRequested;

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
