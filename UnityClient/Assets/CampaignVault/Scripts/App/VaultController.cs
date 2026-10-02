using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.App
{
    /// <summary>
    /// Every player-facing command, UI-free. Commands that talk to the server
    /// are IEnumerators, so a view fires them with Run(...) while the smoke
    /// scenario and tests yield them directly (no play mode needed). All
    /// writes to VaultAppState happen here and are announced via Notify.
    /// </summary>
    public sealed partial class VaultController
    {
        private readonly VaultAppState _s;
        private readonly MonoBehaviour _host;
        private readonly IVaultPrefs _prefs;

        public VaultController(VaultAppState state, MonoBehaviour host, IVaultPrefs prefs)
        {
            _s = state;
            _host = host;
            _prefs = prefs;
            if (_s.Driver != null) { _s.Driver.ToolSucceeded += OnDriverTool; }
        }

        // The session number the log already shows a recap for (0 = none this run).
        private int _recappedSession;
        // True while a player turn runs: its segments are saved together when it ends.
        private bool _turnActive;

        /// <summary>Tools that only read; a turn that ran nothing else changed nothing the table shows.</summary>
        private static readonly HashSet<string> ReadOnlyTools = new HashSet<string>
        {
            SystemPromptProvider.LoadSkillTool, "lookup", "get_entity", "search_world", "start_session", "end_session", "list_campaigns",
        };

        public VaultAppState State { get { return _s; } }

        /// <summary>Fire-and-forget for views.</summary>
        public Coroutine Run(IEnumerator routine) { return _host.StartCoroutine(routine); }

        // =====================================================================
        // Persistence
        // =====================================================================

        public void LoadPreferences()
        {
            _s.Config.ServerUrl = _prefs.GetString(PrefKeys.Server, _s.Config.ServerUrl);
            _s.Config.Connector = _prefs.GetString(PrefKeys.Connector, "play");
            _s.Prompts.CampaignSlug = _prefs.GetString(PrefKeys.Campaign, string.Empty);
            _s.PcId = _prefs.GetString(PrefKeys.PcId, string.Empty);
            _s.FxEnabled = _prefs.GetInt(PrefKeys.Fx, 1) == 1;
            _s.SfxMuted = _prefs.GetInt(PrefKeys.Sfx, 1) == 0;
            _s.StoryTextSize = Mathf.Clamp(_prefs.GetInt(PrefKeys.StoryTextSize, VaultAppState.DefaultStoryTextSize), 0, VaultAppState.StoryTextSizes.Length - 1);
            if (_s.Server != null)
            {
                _s.Server.Config = _s.Config;
                _s.Server.Port = _prefs.GetInt(PrefKeys.EmbeddedPort, 5275);
                _s.Server.AutoStart = _prefs.GetInt(PrefKeys.EmbeddedAutostart, 1) == 1;
            }
            _s.CompanionIds.Clear();
            foreach (string id in _prefs.GetString(PrefKeys.Companions, string.Empty).Split(';'))
            {
                string clean = id.Trim();
                if (clean.Length > 0 && !_s.CompanionIds.Contains(clean)) { _s.CompanionIds.Add(clean); }
            }
        }

        private void SaveCompanionIds()
        {
            _prefs.SetString(PrefKeys.Companions, string.Join(";", _s.CompanionIds.ToArray()));
            _prefs.Save();
        }

        private void SavePcId()
        {
            if (string.IsNullOrEmpty(_s.PcId)) { _prefs.Delete(PrefKeys.PcId); }
            else { _prefs.SetString(PrefKeys.PcId, _s.PcId); }
            _prefs.Save();
        }

        public void SetFx(bool enabled)
        {
            _s.FxEnabled = enabled;
            _prefs.SetInt(PrefKeys.Fx, enabled ? 1 : 0);
            _prefs.Save();
            _s.Notify(StateArea.Preferences);
        }

        /// <summary>Sets the story text size step (clamped); returns the step now in effect.</summary>
        public int SetStoryTextSize(int step)
        {
            step = Mathf.Clamp(step, 0, VaultAppState.StoryTextSizes.Length - 1);
            if (step == _s.StoryTextSize) { return step; }
            _s.StoryTextSize = step;
            _prefs.SetInt(PrefKeys.StoryTextSize, step);
            _prefs.Save();
            _s.Notify(StateArea.Preferences);
            return step;
        }

        public void SetSfxMuted(bool muted)
        {
            _s.SfxMuted = muted;
            _prefs.SetInt(PrefKeys.Sfx, muted ? 0 : 1);
            _prefs.Save();
            _s.Notify(StateArea.Preferences);
        }

        // =====================================================================
        // Campaign + session
        // =====================================================================

        /// <summary>
        /// The one place the active campaign changes. Everything scoped to the
        /// old campaign goes with it: chat history (so the model never carries
        /// one table's context into another), session digest, PC/companion ids.
        /// Empty slug = no active campaign (e.g. after deleting it).
        /// </summary>
        public void SelectCampaign(string slug, string system)
        {
            slug = (slug ?? string.Empty).Trim();
            bool changed = slug != _s.Prompts.CampaignSlug;
            _s.Prompts.CampaignSlug = slug;
            if (!string.IsNullOrEmpty(system)) { _s.Prompts.Ruleset = system; }
            if (slug.Length == 0) { _prefs.Delete(PrefKeys.Campaign); }
            else { _prefs.SetString(PrefKeys.Campaign, slug); }
            if (changed)
            {
                if (string.IsNullOrEmpty(system)) { _s.Prompts.Ruleset = string.Empty; }
                _s.Prompts.PartyLine = string.Empty;
                _s.Prompts.PartyFingerprint = string.Empty;
                _s.Session = null;
                _s.SessionStatus = string.Empty;
                _s.SetupPending = false;
                _s.Pc = null;
                _s.PcEntity = null;
                _s.PcError = string.Empty;
                _s.PcId = string.Empty;
                _s.CompanionIds.Clear();
                _s.Companions.Clear();
                _s.SearchResults.Clear();
                SavePcId();
                SaveCompanionIds();
                _s.Driver.ResetConversation();
                // Each campaign keeps its own chronicle: the log switches with the table.
                _s.Transcript.Clear();
                bool returning = RestoreHistory();
                if (slug.Length > 0)
                {
                    _s.Transcript.Add(new TranscriptSegment
                    {
                        Kind = SegmentKind.System,
                        Text = returning
                            ? "Back at “" + slug + "”. The chronicle above is where you left off; the next line you send picks the session back up."
                            : "Campaign “" + slug + "” is at the table. Send a line to begin the session.",
                    });
                }
            }
            _prefs.Save();
            _s.Notify(StateArea.Campaign | StateArea.Session | StateArea.Pc | StateArea.Companions | StateArea.Search | StateArea.Campaigns | StateArea.Driver);
        }

        /// <summary>
        /// Loads the active campaign's saved chronicle into the log and gives
        /// the storyteller its recent scenes back. True when there was any.
        /// </summary>
        public bool RestoreHistory()
        {
            _recappedSession = 0;
            if (_s.Store == null || !_s.HasCampaign) { return false; }
            var restored = _s.Store.Load(_s.CampaignSlug);
            if (restored.Count == 0) { return false; }
            foreach (var seg in restored) { _s.Transcript.Add(seg); }
            _s.Driver.RestorePassages(TranscriptStore.Passages(restored, Storyteller.MaxPassages));
            _s.Notify(StateArea.Driver);
            return true;
        }

        /// <summary>The session this client left open for the active campaign (0 = none), so a relaunch resumes it.</summary>
        public int RememberedOpenSession
        {
            get
            {
                if (!_s.HasCampaign) { return 0; }
                int n;
                return int.TryParse(_prefs.GetString(PrefKeys.OpenSessionPrefix + _s.CampaignSlug, string.Empty), out n) ? n : 0;
            }
        }

        /// <summary>
        /// Folds a start_session digest in: ruleset, PC roster and party
        /// fingerprint for the DM prompt, companions to track, and a default PC.
        /// The first time a session shows up this run, the log gets its recap.
        /// </summary>
        public void ApplySession(SessionDigest digest)
        {
            _s.Session = digest;
            _s.Prompts.PartyFingerprint = digest.Fingerprint;
            if (!string.IsNullOrEmpty(digest.System)) { _s.Prompts.Ruleset = digest.System; }
            var roster = new List<string>();
            bool pcStillInParty = false;
            foreach (var pc in digest.Pcs)
            {
                roster.Add(pc.Id + " — " + pc.Name);
                if (pc.Id == _s.PcId) { pcStillInParty = true; }
            }
            if (roster.Count > 0) { _s.Prompts.PartyLine = string.Join("; ", roster.ToArray()); }
            bool trackedNew = false;
            foreach (var companion in digest.Companions)
            {
                if (!_s.CompanionIds.Contains(companion.Id)) { _s.CompanionIds.Add(companion.Id); trackedNew = true; }
            }
            if (trackedNew) { SaveCompanionIds(); }
            if (!pcStillInParty && digest.Pcs.Count > 0)
            {
                _s.PcId = digest.Pcs[0].Id;
                _s.Pc = null;
                _s.PcEntity = null;
                SavePcId();
            }
            _s.SessionStatus = DescribeSession(digest);
            if (digest.SessionNumber > 0 && _s.HasCampaign)
            {
                _prefs.SetString(PrefKeys.OpenSessionPrefix + _s.CampaignSlug, digest.SessionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _prefs.Save();
            }
            AddRecap(digest);
            AnnounceLevelUps(digest);
            _s.Notify(StateArea.Session | StateArea.Pc | StateArea.Companions);
        }

        private void AddRecap(SessionDigest digest)
        {
            if (digest == null || digest.SessionNumber <= 0 || digest.SessionNumber == _recappedSession) { return; }
            _recappedSession = digest.SessionNumber;
            var recap = RecapFor(digest);
            // Relaunched without playing: the saved chronicle already ends on this same recap.
            var last = LastStorySegment();
            if (last != null && last.Kind == SegmentKind.Recap && last.Speaker == recap.Speaker && last.Text == recap.Text) { return; }
            AddChronicle(recap);
        }

        /// <summary>A line for the chronicle outside a player turn (a turn saves its own lines when it ends).</summary>
        private void AddChronicle(TranscriptSegment seg)
        {
            _s.Transcript.Add(seg);
            if (!_turnActive && _s.Store != null && _s.HasCampaign) { _s.Store.Append(_s.CampaignSlug, new[] { seg }); }
        }

        private TranscriptSegment LastStorySegment()
        {
            var segments = _s.Transcript.Segments;
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                if (segments[i].Kind != SegmentKind.System) { return segments[i]; }
            }
            return null;
        }

        /// <summary>The "previously…" card a session opens with: last session's handoff, else the recent digest.</summary>
        public static TranscriptSegment RecapFor(SessionDigest digest)
        {
            string heading = "Session " + digest.SessionNumber + (digest.Resumed ? " · resumed" : " begins");
            string title = (digest.Title ?? string.Empty).Trim();
            if (title.Length > 0 && title != digest.SessionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)) { heading += " — " + title; }
            var parts = new List<string>();
            var h = digest.Handoff;
            if (h != null && h.LastSession.Length > 0)
            {
                parts.Add((digest.Resumed && h.Checkpoint ? "So far this session: " : "Previously: ") + h.LastSession.Trim());
            }
            else if (h != null && h.StorySoFar.Length > 0)
            {
                parts.Add("The story so far: " + h.StorySoFar.Trim());
            }
            else if (digest.RecentDigest.Length > 0)
            {
                parts.Add(digest.RecentDigest.Trim());
            }
            if (h != null && h.PartyIntent.Length > 0) { parts.Add("You meant to: " + h.PartyIntent.Trim()); }
            if (parts.Count == 0 && !digest.Resumed && digest.SessionNumber == 1) { parts.Add("The story begins."); }
            return new TranscriptSegment
            {
                Kind = SegmentKind.Recap,
                Speaker = heading,
                Text = TextSanitizer.Clean(string.Join("\n\n", parts.ToArray()), 2400),
            };
        }

        /// <summary>The session closed (by the player's handoff or the DM's own end_session): mark it in the log.</summary>
        private void MarkSessionEnded(string summary)
        {
            int number = _s.Session != null ? _s.Session.SessionNumber : _recappedSession;
            _s.Session = null;
            _recappedSession = 0;
            if (_s.HasCampaign) { _prefs.Delete(PrefKeys.OpenSessionPrefix + _s.CampaignSlug); _prefs.Save(); }
            AddChronicle(new TranscriptSegment
            {
                Kind = SegmentKind.Recap,
                Speaker = number > 0 ? "Session " + number + " ends" : "The session ends",
                Text = TextSanitizer.Clean(summary ?? string.Empty, 1200),
            });
        }

        /// <summary>Follows sessions the DM opens or closes on its own, so the table never shows a stale one.</summary>
        internal void OnDriverTool(string tool, string resultText)
        {
            if (tool != "start_session" && tool != "end_session") { return; }
            JsonValue parsed;
            if (!JsonValue.TryParse(resultText, out parsed) || parsed.Kind != JsonKind.Object || !parsed.GetBool("success", true)) { return; }
            var data = parsed.Get("data");
            if (data.IsNull) { data = parsed; }
            if (tool == "start_session")
            {
                ApplySession(SessionDigest.FromResult(data));
            }
            else if (!data.GetBool("checkpoint", data.GetBool("Checkpoint", false)))
            {
                MarkSessionEnded(string.Empty);
                _s.SessionStatus = "The DM ended the session.";
                _s.Notify(StateArea.Session);
            }
        }

        public static string DescribeSession(SessionDigest digest)
        {
            if (digest == null) { return string.Empty; }
            var parts = new List<string> { "Session " + digest.SessionNumber + (digest.Resumed ? " (resumed)" : string.Empty) };
            if (!string.IsNullOrEmpty(digest.CampaignDisplay)) { parts.Add(digest.CampaignDisplay); }
            if (!string.IsNullOrEmpty(digest.Time)) { parts.Add(digest.Time); }
            return string.Join(" · ", parts.ToArray());
        }

        private bool RequireCampaign(Action<string> onMissing)
        {
            if (_s.HasCampaign) { return true; }
            onMissing("Pick a campaign first.");
            return false;
        }

        /// <summary>Opens (or resumes) the session. Title only matters for a brand-new one.</summary>
        public IEnumerator StartSession(string title)
        {
            yield return OpenSession(title, "session", true, false);
        }

        /// <summary>
        /// Re-reads start_session for fresh HP, quests and pressures. The server
        /// resumes the open session; it never forks a second one.
        /// </summary>
        public IEnumerator RefreshTable()
        {
            yield return OpenSession(null, "refresh", false, false);
        }

        /// <param name="soft">A failure is a warning, not an error (the player's line still goes to the DM).</param>
        /// <summary>start_session's refusal for a campaign that has no player character yet.</summary>
        internal static bool IsNoPartyError(string message)
        {
            return (message ?? string.Empty).IndexOf("no party members", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private IEnumerator OpenSession(string title, string busyKey, bool announce, bool soft, bool quiet = false)
        {
            if (!RequireCampaign(delegate (string m) { _s.SessionStatus = m; _s.Notify(StateArea.Session); })) { yield break; }
            if (!_s.TryBeginBusy(busyKey)) { yield break; }
            try
            {
                var args = CampaignArgs();
                if (!string.IsNullOrEmpty(title)) { args.ObjectValue["title"] = JsonValue.FromString(title.Trim()); }
                McpOutcome<ToolPayload> result = null;
                yield return _s.Mcp.CallToolData(_s.Config, "play", "start_session", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
                if (result == null || !result.Ok)
                {
                    string error = "start_session failed: " + (result != null ? result.ErrorMessage : "no response");
                    if (result != null && IsNoPartyError(result.ErrorMessage))
                    {
                        // Not an error to shout about: the campaign is waiting for its characters.
                        _s.SetupPending = true;
                        _s.SessionStatus = "No player characters yet. Tell the DM about yours and they'll be created before the first scene.";
                        _s.Notify(StateArea.Session);
                        if (!quiet) { _s.RaiseToast(_s.SessionStatus, ToastKind.Info); }
                        yield break;
                    }
                    _s.SessionStatus = error;
                    _s.Notify(StateArea.Session);
                    if (quiet) { yield break; }
                    _s.RaiseToast(soft ? "Couldn't open the session first (" + (result != null ? result.ErrorMessage : "no response") + "); the DM will try." : error,
                        soft ? ToastKind.Warning : ToastKind.Error);
                    yield break;
                }
                ApplySession(SessionDigest.FromResult(result.Data.Data));
                _s.SetupPending = false;
                if (announce)
                {
                    _s.RaiseToast("Session " + _s.Session.SessionNumber + " open" + (_s.Session.Resumed ? " (resumed)." : "."), ToastKind.Success);
                }
            }
            finally { _s.EndBusy(busyKey); }
        }

        /// <summary>
        /// Checkpoint stores the handoff and keeps the session open; end closes
        /// it. Validation problems land in HandoffIssues without a server call.
        /// </summary>
        public IEnumerator EndSession(HandoffDraft draft, bool checkpoint)
        {
            _s.HandoffIssues.Clear();
            if (!_s.HasCampaign)
            {
                _s.HandoffIssues.Add("Pick a campaign first.");
                _s.Notify(StateArea.Session);
                yield break;
            }
            var threads = RosterParser.ParseLines(draft.Threads);
            var npcs = RosterParser.ParseStances(draft.Npcs);
            var issues = HandoffBuilder.Validate(draft.StorySoFar, draft.LastSession, threads, npcs, draft.Intent, draft.Tone);
            if (issues.Count > 0)
            {
                _s.HandoffIssues.AddRange(issues);
                _s.Notify(StateArea.Session);
                yield break;
            }
            if (!_s.TryBeginBusy("handoff")) { yield break; }
            try
            {
                var args = HandoffBuilder.BuildArgs(_s.CampaignSlug, draft.StorySoFar, draft.LastSession, threads, npcs, draft.Intent, draft.Tone, checkpoint);
                McpOutcome<ToolPayload> result = null;
                yield return _s.Mcp.CallToolData(_s.Config, "play", "end_session", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
                if (result == null || !result.Ok)
                {
                    _s.HandoffIssues.Add("end_session failed: " + (result != null ? result.ErrorMessage : "no response"));
                    _s.Notify(StateArea.Session);
                    yield break;
                }
                if (checkpoint)
                {
                    _s.SessionStatus = "Checkpoint stored — session still open.";
                    _s.RaiseToast("Checkpoint stored.", ToastKind.Success);
                }
                else
                {
                    MarkSessionEnded(draft.LastSession);
                    _s.SessionStatus = "Session ended. The handoff carries the story forward.";
                    _s.RaiseToast("Session ended with handoff.", ToastKind.Success);
                }
                _s.Notify(StateArea.Session);
            }
            finally { _s.EndBusy("handoff"); }
        }

        /// <summary>Downtime goes through the DM so it's committed and narrated, not silently applied.</summary>
        public bool AdvanceDays(int days)
        {
            if (days < 1) { days = 1; }
            return SendPlayerText("[Table action] Advance the calendar " + days + " days, then tell what changes.");
        }

        // =====================================================================
        // Chat
        // =====================================================================

        /// <summary>Starts a DM turn. False (with a toast) when it can't: no provider, or a turn is running.</summary>
        public bool SendPlayerText(string text)
        {
            if (!CanSend(text)) { return false; }
            Run(SendPlayerTextRoutine(text));
            return true;
        }

        /// <summary>
        /// One player line, start to finish: the first in-character line of a
        /// sitting opens (or resumes) the session so its recap lands first;
        /// then the DM's turn; then the turn is saved to the chronicle and, if
        /// it changed the world, the table (HP, quests, time) is re-read.
        /// </summary>
        public IEnumerator SendPlayerTextRoutine(string text) { return SendPlayerTextRoutine(text, null); }

        /// <param name="shown">What the player's bubble says, when it differs from what the DM is sent.</param>
        private IEnumerator SendPlayerTextRoutine(string text, string shown)
        {
            if (!CanSend(text)) { yield break; }
            string line = text.Trim();
            string slug = _s.CampaignSlug;
            _turnActive = false;
            if (_s.Session == null && !_s.SetupPending && !Storyteller.IsOocPlayer(line))
            {
                yield return OpenSession(null, "session", false, true);
                if (_s.Driver.IsBusy || _s.CampaignSlug != slug) { yield break; }
            }
            // Before the party exists there's no scene to play: everything is setup talk.
            bool setup = _s.SetupPending && _s.Session == null;
            string sent = setup && !Storyteller.IsOocPlayer(line) ? Storyteller.OocPrefix + " " + line : line;
            var player = new TranscriptSegment { Kind = SegmentKind.Player, Text = shown ?? line };
            _turnActive = true;
            try
            {
                _s.Transcript.Add(player);
                _s.Notify(StateArea.Driver);
                yield return _s.Driver.SendPlayerText(sent, _s.Transcript, delegate { _s.Notify(StateArea.Driver); });
            }
            finally
            {
                _turnActive = false;
                SaveTurn(slug, player);
            }
            if (setup && _s.CampaignSlug == slug && ChangedTheWorld(_s.Driver.CurrentTurn))
            {
                yield return BeginFirstScene(slug);
                yield break;
            }
            if (_s.Session != null && _s.CampaignSlug == slug && ChangedTheWorld(_s.Driver.CurrentTurn))
            {
                yield return RefreshTable();
            }
        }

        /// <summary>
        /// The setup turn wrote to the world: if the party now exists, open session 1
        /// and ask for its opening scene (setup turns are out of character, so the
        /// storyteller never narrates them).
        /// </summary>
        private IEnumerator BeginFirstScene(string slug)
        {
            yield return OpenSession(null, "session", false, true, true);
            if (_s.Session == null || _s.CampaignSlug != slug || _s.Driver.IsBusy) { yield break; }
            _s.RaiseToast("The party is ready. Session " + _s.Session.SessionNumber + " begins.", ToastKind.Success);
            yield return SendPlayerTextRoutine(OpeningSceneLine, "▸ The first session begins.");
        }

        internal const string OpeningSceneLine =
            "The first session begins. Set the opening scene where the party stands: get_entity the party's location and whoever is there, "
            + "commit nothing that hasn't happened yet, then DONE.";

        private void SaveTurn(string slug, TranscriptSegment player)
        {
            if (_s.Store == null || string.IsNullOrEmpty(slug) || slug != _s.CampaignSlug) { return; }
            var segments = _s.Transcript.Segments;
            int start = -1;
            for (int i = segments.Count - 1; i >= 0; i--) { if (ReferenceEquals(segments[i], player)) { start = i; break; } }
            if (start < 0) { return; }
            var turn = new List<TranscriptSegment>();
            for (int i = start; i < segments.Count; i++) { turn.Add(segments[i]); }
            _s.Store.Append(slug, turn);
        }

        internal static bool ChangedTheWorld(TurnRecord turn)
        {
            if (turn == null) { return false; }
            foreach (string tool in turn.Tools) { if (!ReadOnlyTools.Contains(tool)) { return true; } }
            return false;
        }

        private bool CanSend(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { return false; }
            string notReady;
            if (!_s.ProviderReady(out notReady))
            {
                _s.RaiseToast("Chat needs a working AI provider: " + notReady, ToastKind.Warning);
                _s.RequestSetup();
                return false;
            }
            if (!_s.HasCampaign)
            {
                _s.RaiseToast("Choose a campaign first: the Dungeon Master needs a table to run.", ToastKind.Info);
                _s.RequestCampaigns();
                return false;
            }
            if (_s.IsBusy("session"))
            {
                _s.RaiseToast("The session is still opening.", ToastKind.Info);
                return false;
            }
            if (_s.Driver.IsBusy)
            {
                _s.RaiseToast("The DM is still resolving the last action.", ToastKind.Info);
                return false;
            }
            return true;
        }

        public void CancelTurn()
        {
            _s.Driver.Cancel();
            _s.Notify(StateArea.Driver);
        }

        /// <summary>The turn ledger as markdown (see TranscriptExport), for measurement.</summary>
        public string TranscriptMarkdown()
        {
            return TranscriptExport.ToMarkdown(_s.CampaignSlug, _s.Ruleset, _s.Driver.Turns, _s.Driver.SessionUsage);
        }

        /// <summary>Writes the transcript under dir (default: persistentDataPath/Exports); returns the file path, or null.</summary>
        public string ExportTranscript(string dir = null)
        {
            try
            {
                dir = dir ?? System.IO.Path.Combine(Application.persistentDataPath, "Exports");
                System.IO.Directory.CreateDirectory(dir);
                string slug = string.IsNullOrEmpty(_s.CampaignSlug) ? "no-campaign" : _s.CampaignSlug;
                string path = System.IO.Path.Combine(dir, "transcript-" + slug + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
                System.IO.File.WriteAllText(path, TranscriptMarkdown());
                return path;
            }
            catch (Exception ex)
            {
                _s.RaiseToast("Export failed: " + ex.Message, ToastKind.Error);
                return null;
            }
        }

        // =====================================================================
        // Party
        // =====================================================================

        public void SetPcId(string id)
        {
            id = (id ?? string.Empty).Trim();
            if (id.Length == 0 || id == _s.PcId) { return; }
            _s.PcId = id;
            _s.Pc = null;
            _s.PcEntity = null;
            SavePcId();
            _s.Notify(StateArea.Pc);
        }

        public IEnumerator LoadPc()
        {
            if (!_s.TryBeginBusy("pc")) { yield break; }
            try
            {
                McpOutcome<JsonValue> result = null;
                yield return GetCharacter(_s.PcId, delegate (McpOutcome<JsonValue> o) { result = o; });
                if (!result.Ok)
                {
                    _s.PcError = result.ErrorMessage;
                    _s.Pc = null;
                    _s.Notify(StateArea.Pc);
                    yield break;
                }
                _s.PcEntity = result.Data;
                _s.Driver.PcCard = Storyteller.PcCard(result.Data);
                var sheet = PcSheet.FromEntity(result.Data);
                if (string.IsNullOrEmpty(sheet.Ruleset)) { sheet.Ruleset = _s.Ruleset; }
                _s.Pc = sheet;
                _s.PcError = string.Empty;
                _s.Notify(StateArea.Pc);
            }
            finally { _s.EndBusy("pc"); }
        }

        /// <summary>get_entity for a character in the active campaign: the character entity alone. Bare ids get the chars/ prefix.</summary>
        public IEnumerator GetCharacter(string id, Action<McpOutcome<JsonValue>> done)
        {
            yield return GetCharacterDetail(id, delegate (McpOutcome<JsonValue> o)
            {
                if (!o.Ok) { done(o); return; }
                var character = o.Data.Get("character");
                done(McpOutcome<JsonValue>.Success(character.Kind == JsonKind.Object ? character : o.Data));
            });
        }

        /// <summary>
        /// get_entity's whole answer for a character: the entity plus equipped/carried gear, recent
        /// interactions and needs, which the sheet shows.
        /// </summary>
        public IEnumerator GetCharacterDetail(string id, Action<McpOutcome<JsonValue>> done)
        {
            if (!_s.HasCampaign)
            {
                done(McpOutcome<JsonValue>.Fail("NO_CAMPAIGN", "pick a campaign first."));
                yield break;
            }
            string entityId = (id ?? string.Empty).Trim();
            if (entityId.Length == 0)
            {
                done(McpOutcome<JsonValue>.Fail("NO_ID", "no character selected."));
                yield break;
            }
            if (entityId.IndexOf('/') < 0) { entityId = "chars/" + entityId; }
            var args = CampaignArgs();
            args.ObjectValue["entityId"] = JsonValue.FromString(entityId);
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, _s.Config.ActiveConnector(), "get_entity", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                done(McpOutcome<JsonValue>.Fail(result != null ? result.ErrorCode : "MCP", result != null ? result.ErrorMessage : "no response"));
                yield break;
            }
            if (result.Data.Data.Kind != JsonKind.Object)
            {
                done(McpOutcome<JsonValue>.Fail("PROTOCOL", entityId + " is not a character."));
                yield break;
            }
            done(McpOutcome<JsonValue>.Success(result.Data.Data));
        }

        public bool UseItem(InventoryItem item)
        {
            return SendPlayerText("[Table action] " + _s.PcId + " uses " + ItemRef(item) + ". Commit it.");
        }

        public bool ToggleEquip(InventoryItem item)
        {
            string verb = item.Equipped ? "unequips" : "equips";
            return SendPlayerText("[Table action] " + _s.PcId + " " + verb + " " + ItemRef(item) + ". Commit it.");
        }

        private static string ItemRef(InventoryItem item)
        {
            return item.Name + (string.IsNullOrEmpty(item.Id) ? string.Empty : " (" + item.Id + ")");
        }

        public IEnumerator TrackCompanion(string id)
        {
            id = (id ?? string.Empty).Trim();
            if (id.Length == 0 || _s.CompanionIds.Contains(id)) { yield break; }
            _s.CompanionIds.Add(id);
            SaveCompanionIds();
            yield return LoadCompanions();
        }

        public void UntrackCompanion(string id)
        {
            if (!_s.CompanionIds.Remove(id)) { return; }
            _s.Companions.RemoveAll(delegate (CompanionEntry e) { return e.Id == id; });
            SaveCompanionIds();
            _s.Notify(StateArea.Companions);
        }

        public IEnumerator LoadCompanions()
        {
            if (!_s.TryBeginBusy("companions")) { yield break; }
            try
            {
                var entries = new List<CompanionEntry>();
                foreach (string id in _s.CompanionIds.ToArray())
                {
                    McpOutcome<JsonValue> result = null;
                    yield return GetCharacter(id, delegate (McpOutcome<JsonValue> o) { result = o; });
                    entries.Add(new CompanionEntry
                    {
                        Id = id,
                        Companion = result.Ok ? Companion.FromEntity(result.Data) : null,
                        Error = result.Ok ? string.Empty : result.ErrorMessage,
                    });
                }
                _s.Companions.Clear();
                _s.Companions.AddRange(entries);
                _s.Notify(StateArea.Companions);
            }
            finally { _s.EndBusy("companions"); }
        }

        // =====================================================================
        // Campaigns + world
        // =====================================================================

        public IEnumerator ListCampaigns()
        {
            if (!_s.TryBeginBusy("campaigns")) { yield break; }
            try
            {
                McpOutcome<ToolPayload> result = null;
                yield return _s.Mcp.CallToolData(_s.Config, "build", "list_campaigns", JsonValue.NewObject(),
                    delegate (McpOutcome<ToolPayload> o) { result = o; });
                _s.CampaignsLoaded = true;
                if (result == null || !result.Ok)
                {
                    _s.CampaignsError = result != null ? result.ErrorMessage : "no response";
                    _s.Notify(StateArea.Campaigns);
                    yield break;
                }
                _s.CampaignsError = string.Empty;
                _s.Campaigns.Clear();
                _s.Campaigns.AddRange(ExtractCampaigns(result.Data.Data));
                _s.Notify(StateArea.Campaigns);
            }
            finally { _s.EndBusy("campaigns"); }
        }

        /// <summary>list_campaigns data: [{name (the slug), displayName, system, createdAt}].</summary>
        internal static List<CampaignRow> ExtractCampaigns(JsonValue data)
        {
            var rows = new List<CampaignRow>();
            if (data.Kind != JsonKind.Array || data.ArrayValue == null) { return rows; }
            foreach (var entry in data.ArrayValue)
            {
                string slug = entry.GetStringAny(new[] { "name", "slug" }, string.Empty);
                if (string.IsNullOrEmpty(slug)) { continue; }
                rows.Add(new CampaignRow
                {
                    Slug = slug,
                    Display = entry.GetString("displayName", slug),
                    System = entry.GetString("system", string.Empty),
                });
            }
            return rows;
        }

        /// <summary>Irreversible. The view owns the confirmation; the server requires the slug echoed back.</summary>
        public IEnumerator DeleteCampaign(string slug)
        {
            if (!_s.TryBeginBusy("delete:" + slug)) { yield break; }
            try
            {
                var args = JsonValue.NewObject();
                args.ObjectValue["campaignName"] = JsonValue.FromString(slug);
                args.ObjectValue["confirmName"] = JsonValue.FromString(slug);
                McpOutcome<ToolPayload> result = null;
                yield return _s.Mcp.CallToolData(_s.Config, "build", "delete_campaign", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
                if (result != null && result.Ok)
                {
                    if (_s.CampaignSlug == slug) { SelectCampaign(string.Empty, null); }
                    if (_s.Store != null) { _s.Store.Delete(slug); }
                    _prefs.Delete(PrefKeys.OpenSessionPrefix + slug);
                    _prefs.Save();
                    _s.RaiseToast("Campaign “" + slug + "” deleted.", ToastKind.Success);
                }
                else
                {
                    _s.RaiseToast("Delete failed: " + (result != null ? result.ErrorMessage : "no response"), ToastKind.Error);
                }
            }
            finally { _s.EndBusy("delete:" + slug); }
            yield return ListCampaigns();
        }

        private const int MaxSearchHits = 12;

        public IEnumerator SearchWorld(string query)
        {
            if (!RequireCampaign(delegate (string m) { _s.SearchError = m; _s.Notify(StateArea.Search); })) { yield break; }
            if (!_s.TryBeginBusy("search")) { yield break; }
            try
            {
                var args = CampaignArgs();
                args.ObjectValue["query"] = JsonValue.FromString(query ?? string.Empty);
                McpOutcome<ToolPayload> result = null;
                yield return _s.Mcp.CallToolData(_s.Config, _s.Config.ActiveConnector(), "search_world", args,
                    delegate (McpOutcome<ToolPayload> o) { result = o; });
                _s.SearchResults.Clear();
                if (result == null || !result.Ok)
                {
                    _s.SearchError = result != null ? result.ErrorMessage : "no response";
                    _s.SearchSummary = string.Empty;
                    _s.Notify(StateArea.Search);
                    yield break;
                }
                _s.SearchError = string.Empty;
                _s.SearchSummary = result.Data.Summary;
                foreach (var match in result.Data.Data.GetArray("matches"))
                {
                    if (_s.SearchResults.Count >= MaxSearchHits) { break; }
                    if (match.Kind != JsonKind.Object)
                    {
                        _s.SearchResults.Add(new SearchHit { Title = match.ToJson() });
                        continue;
                    }
                    _s.SearchResults.Add(new SearchHit
                    {
                        Title = match.GetStringAny(new[] { "name", "title", "subject", "id" }, "(untitled)"),
                        Id = match.GetString("id", string.Empty),
                        Detail = match.GetStringAny(new[] { "description", "summary", "content", "text", "details" }, string.Empty),
                    });
                }
                _s.Notify(StateArea.Search);
            }
            finally { _s.EndBusy("search"); }
        }

        private JsonValue CampaignArgs()
        {
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(_s.CampaignSlug);
            return args;
        }

        // =====================================================================
        // Onboarding
        // =====================================================================

        /// <summary>Client-side preview of the server's slug rule ("Dragon Heist" → dragon-heist).</summary>
        public static string Slugify(string name)
        {
            var sb = new System.Text.StringBuilder();
            bool dash = false;
            foreach (char c in (name ?? string.Empty).Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) { sb.Append(c); dash = false; }
                else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
            }
            return sb.ToString().TrimEnd('-');
        }

        /// <summary>
        /// Opens the questionnaire. Name, display name and system are answered
        /// up front and submitted automatically when their questions come up.
        /// </summary>
        public IEnumerator BeginOnboarding(string name, string displayName, string system)
        {
            string slug = Slugify(name);
            var ob = _s.Onboarding;
            if (slug.Length == 0)
            {
                ob.Error = "Give the campaign a name.";
                _s.Notify(StateArea.Onboarding);
                yield break;
            }
            ob.Prefilled.Clear();
            ob.Slug = slug;
            ob.System = string.IsNullOrEmpty(system) ? OnboardingState.SystemOptions[0] : system;
            ob.Prefilled["campaign_name"] = string.IsNullOrEmpty((displayName ?? string.Empty).Trim()) ? name.Trim() : displayName.Trim();
            ob.Prefilled["system"] = ob.System;
            yield return FetchOnboarding();
        }

        public IEnumerator ResumeOnboarding(string slug)
        {
            slug = (slug ?? string.Empty).Trim();
            if (slug.Length == 0) { yield break; }
            _s.Onboarding.Prefilled.Clear();
            _s.Onboarding.Slug = slug;
            yield return FetchOnboarding();
        }

        private IEnumerator FetchOnboarding()
        {
            var ob = _s.Onboarding;
            SetOnboarding(OnboardingPhase.Working, "Opening onboarding…");
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(ob.Slug);
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, "build", "start_campaign_onboarding", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                ob.Error = "Onboarding failed: " + (result != null ? result.ErrorMessage : "no response");
                SetOnboarding(OnboardingPhase.Failed, string.Empty);
                yield break;
            }
            // The server's normalized slug is authoritative for every later call.
            string serverSlug = result.Data.Data.Get("state").GetString("campaignSlug", string.Empty);
            if (!string.IsNullOrEmpty(serverSlug)) { ob.Slug = serverSlug; }
            yield return AdvanceOnboarding(result.Data.Data);
        }

        /// <summary>Shows the next question, auto-answering prefilled ones.</summary>
        private IEnumerator AdvanceOnboarding(JsonValue payload)
        {
            var ob = _s.Onboarding;
            while (true)
            {
                var question = Pick(payload, "currentQuestion", "CurrentQuestion");
                bool ready = payload.GetBool("isReadyToBuild", payload.GetBool("IsReadyToBuild", false))
                    || payload.Get("state").GetBool("isComplete", false);
                ob.Answered = (int)payload.Get("state").GetNumber("currentQuestionIndex", ob.Answered);
                ob.Progress = TextSanitizer.Clean(payload.GetString("summary", string.Empty), 160);
                ReadAnswers(payload.Get("state"), ob.Answers);
                if (ready || question.IsNull)
                {
                    ob.Question = null;
                    SetOnboarding(OnboardingPhase.ReadyToFinalize, string.Empty);
                    yield break;
                }
                var parsed = ParseQuestion(question);
                string prefilled;
                if (!ob.Prefilled.TryGetValue(parsed.Key, out prefilled))
                {
                    MoveToQuestion(ob, parsed);
                    ob.Error = string.Empty;
                    SetOnboarding(OnboardingPhase.Question, string.Empty);
                    yield break;
                }
                ob.Prefilled.Remove(parsed.Key);
                JsonValue next = null;
                yield return PostAnswer(prefilled, delegate (JsonValue p) { next = p; });
                if (next == null) { yield break; }
                payload = next;
            }
        }

        /// <summary>state.collectedAnswers → key/answer strings, for the brainstorm prompt and the party line.</summary>
        /// <summary>
        /// A question arrives. On a new one the draft resets, but the conversation with the DM carries on
        /// (a divider marks the move), so a plot talked through on the world question is still there when
        /// the plot question comes. The same question again (a rejected answer) changes nothing.
        /// </summary>
        internal static void MoveToQuestion(OnboardingState ob, OnboardingQuestion parsed)
        {
            if (ob.Question == null || ob.Question.Key != parsed.Key)
            {
                ob.Draft = string.Empty;
                ob.Brainstorming = false;
                ob.BrainstormError = string.Empty;
                if (OnboardingBrainstorm.HasTalk(ob.BrainstormChat))
                {
                    ob.BrainstormChat.Add(new KeyValuePair<string, string>(OnboardingBrainstorm.MarkerRole, parsed.Text));
                }
            }
            ob.Question = parsed;
        }

        internal static void ReadAnswers(JsonValue state, Dictionary<string, string> into)
        {
            var answers = Pick(state, "collectedAnswers", "CollectedAnswers");
            if (answers.Kind != JsonKind.Object || answers.ObjectValue == null) { return; }
            into.Clear();
            foreach (var kv in answers.ObjectValue)
            {
                if (kv.Value.Kind == JsonKind.String) { into[kv.Key] = TextSanitizer.Clean(kv.Value.StringValue, 3000); }
                else if (kv.Value.Kind == JsonKind.Number) { into[kv.Key] = kv.Value.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            }
        }

        internal static OnboardingQuestion ParseQuestion(JsonValue question)
        {
            var q = new OnboardingQuestion
            {
                Key = question.GetString("key", string.Empty),
                Text = TextSanitizer.Clean(question.GetStringAny(new[] { "text", "Text" }, string.Empty), 800),
                Help = TextSanitizer.Clean(question.GetStringAny(new[] { "helpText", "HelpText" }, string.Empty), 800),
            };
            var options = question.GetArray("enumOptions");
            if (options.Count == 0) { options = question.GetArray("EnumOptions"); }
            foreach (var o in options) { if (o.Kind == JsonKind.String) { q.Options.Add(o.StringValue); } }
            var raw = Pick(question, "answerType", "AnswerType");
            int n = -1;
            if (raw.Kind == JsonKind.Number) { n = (int)raw.NumberValue; }
            else if (raw.Kind == JsonKind.String)
            {
                string text = raw.StringValue.ToLowerInvariant();
                if (text.Contains("enum") || text.Contains("option") || text.Contains("choice")) { n = 1; }
                else if (text.Contains("bool")) { n = 2; }
                else if (text.Contains("list") || text.Contains("array")) { n = 3; }
                else if (text.Contains("number") || text.Contains("int")) { n = 4; }
                else if (text.Contains("party")) { n = 5; }
            }
            switch (n)
            {
                case 1: q.Type = q.Options.Count > 0 ? AnswerType.Choice : AnswerType.Text; break;
                case 2: q.Type = AnswerType.YesNo; break;
                case 3: q.Type = AnswerType.List; break;
                case 4: q.Type = AnswerType.Number; break;
                case 5: q.Type = AnswerType.Party; break;
                default: q.Type = AnswerType.Text; break;
            }
            return q;
        }

        /// <summary>A list answer typed one entry per line, in the server's "a; b; c" form.</summary>
        public static string FormatListAnswer(string lines)
        {
            return string.Join("; ", RosterParser.ParseLines(lines).ToArray());
        }

        public IEnumerator SubmitOnboardingAnswer(string answer)
        {
            answer = (answer ?? string.Empty).Trim();
            if (answer.Length == 0 || _s.Onboarding.Phase == OnboardingPhase.Working) { yield break; }
            JsonValue next = null;
            yield return PostAnswer(answer, delegate (JsonValue p) { next = p; });
            if (next != null) { yield return AdvanceOnboarding(next); }
        }

        private IEnumerator PostAnswer(string answer, Action<JsonValue> done)
        {
            var ob = _s.Onboarding;
            SetOnboarding(OnboardingPhase.Working, "Answering…");
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(ob.Slug);
            args.ObjectValue["answer"] = JsonValue.FromString(answer);
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, "build", "submit_onboarding_answer", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                // Re-fetch the current question so the player can correct the answer.
                _s.RaiseToast("Answer rejected: " + (result != null ? result.ErrorMessage : "no response"), ToastKind.Warning);
                done(null);
                yield return FetchOnboarding();
                yield break;
            }
            // The player characters as the DM prompt's party line, until the party exists on the server.
            if (ob.Question != null && ob.Question.Key == "pc_roster" && _s.Prompts != null && _s.Prompts.PartyLine.Length == 0)
            {
                _s.Prompts.PartyLine = TextSanitizer.Clean(answer.Replace("\n", "; "), 600);
            }
            if (ob.Question != null && ob.Question.Key == "party" && _s.Prompts != null)
            {
                var names = new List<string>();
                foreach (var m in ob.Party) { names.Add(m.ClassLine.Length > 0 ? m.Name + " — " + m.ClassLine : m.Name); }
                if (names.Count > 0 && answer.Contains("\"" + OnboardingState.PartyBuildAtTable + "\"") == false) { _s.Prompts.PartyLine = TextSanitizer.Clean(string.Join("; ", names.ToArray()), 600); }
            }
            done(result.Data.Data);
        }

        public IEnumerator FinalizeOnboarding()
        {
            var ob = _s.Onboarding;
            SetOnboarding(OnboardingPhase.Working, "Finalizing…");
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(ob.Slug);
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, "build", "finalize_campaign_onboarding", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                ob.Error = "Finalize failed: " + (result != null ? result.ErrorMessage : "no response");
                SetOnboarding(OnboardingPhase.ReadyToFinalize, string.Empty);
                yield break;
            }
            var finalized = result.Data.Data;
            ob.DoneSummary = TextSanitizer.Clean(finalized.GetString("summary", result.Data.Summary), 1200);
            ob.SeedBrief = finalized.GetString("seedBrief", string.Empty).Trim();
            ob.NextSteps.Clear();
            foreach (var step in finalized.GetArray("nextSteps"))
            {
                if (step.Kind == JsonKind.String) { ob.NextSteps.Add(TextSanitizer.Clean(step.StringValue, 300)); }
            }
            // SelectCampaign resets the party line for a new table; this one comes from the answers just given.
            string partyLine = _s.Prompts.PartyLine;
            SelectCampaign(ob.Slug, finalized.GetString("system", ob.System));
            _s.Prompts.PartyLine = partyLine;
            _s.SetupPending = true;
            _s.Notify(StateArea.Session | StateArea.Campaign);
            SetOnboarding(OnboardingPhase.Done, string.Empty);
        }

        /// <summary>After finalize: the DM seeds the world with world_build and opens the first session.</summary>
        public bool SeedWorldThroughDm()
        {
            return SendPlayerText(SeedMessage(_s.Onboarding.Slug, _s.Onboarding.SeedBrief));
        }

        /// <summary>
        /// The OOC line that seeds a fresh campaign. It carries the onboarding brief
        /// itself: the DM has no tool that reads the answers back, so a line that only
        /// mentioned them left it asking the player to repeat everything.
        /// </summary>
        internal static string SeedMessage(string slug, string brief)
        {
            if (string.IsNullOrEmpty(brief))
            {
                // An older server without a brief: start_session repeats it, or the DM asks.
                return "OOC: seed the starter world for \"" + slug + "\" from its onboarding answers: create the player characters "
                    + "(world_build, isPc=true) and the opening location. Skip start_session; this client opens the session once they exist.";
            }
            return "OOC: set up the campaign \"" + slug + "\" from its onboarding brief below. Do its character and world steps with world_build. "
                + "Where it says to show me options or walk me through making characters, ask me here and wait for my answers. "
                + "Skip start_session and the opening scene: this client opens the session and asks for the first scene once the player characters exist.\n\n"
                + brief;
        }

        // ---- brainstorming the current question with the model ----

        public void OpenBrainstorm()
        {
            var ob = _s.Onboarding;
            if (!OnboardingBrainstorm.Supports(ob.Question)) { return; }
            string notReady;
            if (!_s.ProviderReady(out notReady))
            {
                _s.RaiseToast("Brainstorming needs a working AI provider: " + notReady, ToastKind.Warning);
                return;
            }
            ob.Brainstorming = true;
            ob.BrainstormError = string.Empty;
            _s.Notify(StateArea.Onboarding);
        }

        /// <summary>Back to the question; the conversation is kept for the whole setup.</summary>
        public void CloseBrainstorm()
        {
            _s.Onboarding.Brainstorming = false;
            _s.Notify(StateArea.Onboarding);
        }

        public IEnumerator SendBrainstorm(string text)
        {
            var ob = _s.Onboarding;
            text = TextSanitizer.Clean(text, 0).Trim();
            if (text.Length == 0 || ob.BrainstormBusy || ob.Question == null) { yield break; }
            if (text.Length > OnboardingBrainstorm.MaxMessageChars)
            {
                // Never cut what the player wrote: the composer already shows the limit; say it again and keep the text.
                ob.BrainstormError = "That message is " + text.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters; the limit is "
                    + OnboardingBrainstorm.MaxMessageChars.ToString("N0", CultureInfo.InvariantCulture) + ". Trim it or send it in parts.";
                _s.Notify(StateArea.Onboarding);
                yield break;
            }
            ob.BrainstormChat.Add(new KeyValuePair<string, string>("user", text));
            ob.BrainstormDraft = string.Empty;
            yield return BrainstormTurn(null);
        }

        /// <summary>The model writes the settled answer up; it lands in the question's field to edit and submit.</summary>
        public IEnumerator WriteUpBrainstorm()
        {
            var ob = _s.Onboarding;
            if (ob.BrainstormBusy || ob.Question == null || !OnboardingBrainstorm.HasTalk(ob.BrainstormChat)) { yield break; }
            yield return BrainstormTurn(OnboardingBrainstorm.FinalizeInstruction(ob.Question));
        }

        private IEnumerator BrainstormTurn(string finalizeInstruction)
        {
            var ob = _s.Onboarding;
            var question = ob.Question;
            ob.BrainstormBusy = true;
            ob.BrainstormError = string.Empty;
            _s.Notify(StateArea.Onboarding);
            var messages = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("system", OnboardingBrainstorm.SystemPrompt(question, ob.Answers)),
            };
            // Over the budget the middle of the chat drops out; the overlay marks those messages as no longer sent.
            var dropped = OnboardingBrainstorm.Dropped(ob.BrainstormChat, OnboardingBrainstorm.MaxConversationChars);
            messages.AddRange(OnboardingBrainstorm.ModelMessages(ob.BrainstormChat, dropped));
            if (finalizeInstruction != null) { messages.Add(new KeyValuePair<string, string>("user", finalizeInstruction)); }
            string reply = null;
            string error = null;
            // Not streamed: the overlay re-renders on every change, which would drop what the player is typing.
            yield return _s.Driver.Brainstorm(messages, null, delegate (string r, string e) { reply = r; error = e; });
            ob.BrainstormBusy = false;
            if (ob.Question != question) { yield break; }
            if (error != null)
            {
                ob.BrainstormError = TextSanitizer.Clean(error, 400);
            }
            else if (finalizeInstruction == null)
            {
                ob.BrainstormChat.Add(new KeyValuePair<string, string>("assistant", TextSanitizer.Clean(reply, OnboardingBrainstorm.MaxReplyChars)));
            }
            else
            {
                ob.Draft = OnboardingBrainstorm.CleanAnswer(TextSanitizer.Clean(reply, 0));
                ob.Brainstorming = false;
            }
            _s.Notify(StateArea.Onboarding);
        }

        public void ResetOnboarding()
        {
            var ob = _s.Onboarding;
            ob.Prefilled.Clear();
            ob.Slug = string.Empty;
            ob.Question = null;
            ob.Error = string.Empty;
            ob.DoneSummary = string.Empty;
            ob.NextSteps.Clear();
            ob.SeedBrief = string.Empty;
            ob.Answers.Clear();
            ob.Draft = string.Empty;
            ob.Party.Clear();
            ob.PartyLevel = 1;
            ob.Drafting = false;
            ob.DraftError = string.Empty;
            ob.ClearBrainstorm();
            SetOnboarding(OnboardingPhase.Idle, string.Empty);
        }

        private void SetOnboarding(OnboardingPhase phase, string status)
        {
            _s.Onboarding.Phase = phase;
            _s.Onboarding.Status = status;
            if (phase != OnboardingPhase.Failed && phase != OnboardingPhase.ReadyToFinalize) { _s.Onboarding.Error = string.Empty; }
            _s.Notify(StateArea.Onboarding);
        }

        private static JsonValue Pick(JsonValue obj, string camel, string pascal)
        {
            var v = obj.Get(camel);
            return v.IsNull ? obj.Get(pascal) : v;
        }

        // =====================================================================
        // Tools (plugin allowlist)
        // =====================================================================

        public IEnumerator LoadTools()
        {
            if (!_s.TryBeginBusy("tools")) { yield break; }
            try
            {
                var tools = new List<ToolToggle>();
                _s.ToolsErrors.Clear();
                foreach (string connector in new[] { "play", "build" })
                {
                    McpOutcome<List<McpToolInfo>> listed = null;
                    string captured = connector;
                    yield return _s.Mcp.ListTools(_s.Config, captured, delegate (McpOutcome<List<McpToolInfo>> o) { listed = o; });
                    if (listed == null || !listed.Ok)
                    {
                        _s.ToolsErrors[captured] = listed != null ? listed.ErrorMessage : "no response";
                        continue;
                    }
                    foreach (var t in listed.Data)
                    {
                        tools.Add(new ToolToggle { Name = t.Name, Description = t.Description, Connector = captured });
                    }
                }
                _s.Tools.Clear();
                _s.Tools.AddRange(tools);
                _s.Notify(StateArea.Tools);
            }
            finally { _s.EndBusy("tools"); }
        }

        public IEnumerator ReloadTools()
        {
            _s.Driver.InvalidateTools();
            yield return LoadTools();
        }

        public void ToggleTool(string name)
        {
            var allowed = _s.Driver.AllowedTools;
            if (allowed.Count == 0)
            {
                // Empty means "everything on": seed the full list first so this
                // toggle disables just one tool.
                foreach (var t in _s.Tools) { allowed.Add(t.Name); }
            }
            if (!allowed.Remove(name)) { allowed.Add(name); }
            _s.Notify(StateArea.Tools);
        }

        public void EnableAllTools()
        {
            _s.Driver.AllowedTools.Clear();
            _s.Notify(StateArea.Tools);
        }

        // =====================================================================
        // Server connection + embedded server
        // =====================================================================

        /// <summary>
        /// /health alone only proves the process is up; healthy here also
        /// requires an MCP session and a tools/list on /play, which is what
        /// everything else depends on.
        /// </summary>
        public IEnumerator CheckConnection()
        {
            if (!_s.TryBeginBusy("connection")) { yield break; }
            try
            {
                _s.Connection = ConnectionStatus.Checking;
                _s.ConnectionMessage = "Checking…";
                _s.Notify(StateArea.Connection);
                bool healthy = false;
                string message = string.Empty;
                yield return _s.Config.CheckHealth(delegate (bool ok, string msg) { healthy = ok; message = msg; });
                if (!healthy)
                {
                    SetConnection(ConnectionStatus.Down, "Server: " + message);
                    yield break;
                }
                _s.Mcp.ResetSessions();
                McpOutcome<List<McpToolInfo>> tools = null;
                yield return _s.Mcp.ListTools(_s.Config, "play", delegate (McpOutcome<List<McpToolInfo>> o) { tools = o; });
                if (tools == null || !tools.Ok)
                {
                    SetConnection(ConnectionStatus.Down, "Server is up, but MCP failed: " + (tools != null ? tools.ErrorMessage : "no response"));
                    yield break;
                }
                string versionWarning = EmbeddedServerSupport.VersionWarning(ServerHostManager.ExpectedVersion(), _s.Config.ReportedServerVersion);
                SetConnection(ConnectionStatus.Healthy, "Healthy · MCP connected (" + tools.Data.Count + " play tools)"
                    + (versionWarning != null ? " · " + versionWarning : string.Empty));
                if (versionWarning != null) { _s.RaiseToast(versionWarning, ToastKind.Warning); }
            }
            finally { _s.EndBusy("connection"); }
        }

        private void SetConnection(ConnectionStatus status, string message)
        {
            _s.Connection = status;
            _s.ConnectionMessage = message;
            _s.Notify(StateArea.Connection);
        }

        public void SetServerUrl(string url)
        {
            _s.Config.ServerUrl = (url ?? string.Empty).Trim();
            _prefs.SetString(PrefKeys.Server, _s.Config.ServerUrl);
            _prefs.Save();
            _s.Mcp.ResetSessions();
            _s.Driver.InvalidateTools();
            _s.PluginsLoaded = false;
            SetConnection(ConnectionStatus.Unknown, string.Empty);
        }

        public void SetConnector(string connector)
        {
            _s.Config.Connector = connector == "build" ? "build" : "play";
            _prefs.SetString(PrefKeys.Connector, _s.Config.Connector);
            _prefs.Save();
            _s.Driver.InvalidateTools();
            _s.Notify(StateArea.Connection);
        }

        public void SetBearerToken(string token)
        {
            _s.Config.SetBearerToken(token);
            _s.Mcp.ResetSessions();
            _s.RaiseToast("Bearer token kept in memory only.", ToastKind.Info);
        }

        public void ClearBearerToken()
        {
            _s.Config.ClearSecrets();
            _s.Mcp.ResetSessions();
            _s.RaiseToast("Bearer token cleared.", ToastKind.Info);
        }

        /// <summary>
        /// False until the launch-time autostart has either found a server,
        /// started one or given up, so the first connection check waits for it
        /// instead of racing a server that is still unpacking or booting.
        /// </summary>
        public bool AutostartSettled { get; private set; }

        /// <summary>Autostart only ever hijacks a loopback URL, never a remote server.</summary>
        public IEnumerator AutostartEmbedded()
        {
            try
            {
                if (_s.Server == null || !_s.Server.AutoStart || !ServerHostManager.IsLoopbackUrl(_s.Config.ServerUrl)) { yield break; }
                bool healthy = false;
                yield return _s.Config.CheckHealth(delegate (bool ok, string msg) { healthy = ok; });
                // Healthy and not ours (a server you run yourself): use it. Healthy but an orphan
                // of an earlier session: StartEmbedded stops it and starts a fresh, owned one.
                if (healthy && !_s.Server.HasOrphan) { yield break; }
                yield return StartEmbedded(_s.Server.Port);
            }
            finally { AutostartSettled = true; }
        }

        public IEnumerator StartEmbedded(int port)
        {
            if (_s.Server == null || _s.Server.IsRunning) { yield break; }
            if (!_s.TryBeginBusy("embedded")) { yield break; }
            try
            {
                _s.Server.Port = port;
                _prefs.SetInt(PrefKeys.EmbeddedPort, port);
                _prefs.Save();
                _s.EmbeddedMessage = "Starting embedded server…";
                _s.Notify(StateArea.Embedded);
                bool started = false;
                string message = string.Empty;
                yield return _s.Server.StartEmbedded(delegate (bool ok, string msg) { started = ok; message = msg; }, delegate (string line)
                {
                    _s.EmbeddedMessage = line;
                    _s.Notify(StateArea.Embedded);
                });
                _s.EmbeddedMessage = message;
                if (started) { _s.PluginsLoaded = false; }
                else { SetConnection(ConnectionStatus.Down, "Built-in server: " + message); }
                _s.Notify(StateArea.Embedded);
                _s.RaiseToast(started ? "Embedded server started." : "The built-in server didn't start. " + message, started ? ToastKind.Success : ToastKind.Error);
            }
            finally { _s.EndBusy("embedded"); }
            // A (re)start can move the port and drops every MCP session: re-check so the
            // status and the tool list match the new server instead of the last check.
            if (_s.Server.IsRunning) { yield return CheckConnection(); }
        }

        public void StopEmbedded()
        {
            if (_s.Server == null) { return; }
            _s.Server.StopEmbedded();
            _s.EmbeddedMessage = "Embedded server stopped.";
            _s.Notify(StateArea.Embedded);
        }

        /// <summary>
        /// Saves the RavenDB license the embedded server starts with: input is
        /// the license JSON itself or a path to a file holding it. Takes effect
        /// on the next server start.
        /// </summary>
        public bool SaveRavenLicense(string input)
        {
            if (_s.Server == null) { return false; }
            string text = (input ?? string.Empty).Trim();
            if (text.Length == 0) { _s.RaiseToast("Paste the license, or the path to its file.", ToastKind.Warning); return false; }
            if (!text.StartsWith("{", StringComparison.Ordinal))
            {
                string path = text.Trim('"');
                if (!System.IO.File.Exists(path)) { _s.RaiseToast("No license file at " + path, ToastKind.Warning); return false; }
                try { text = System.IO.File.ReadAllText(path); }
                catch (System.IO.IOException ex) { _s.RaiseToast("Couldn't read the license file: " + ex.Message, ToastKind.Warning); return false; }
            }
            string name, error;
            if (!EmbeddedServerSupport.ValidateLicense(text, out name, out error)) { _s.RaiseToast(error, ToastKind.Warning); return false; }
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_s.Server.LicensePath));
                System.IO.File.WriteAllText(_s.Server.LicensePath, text.Trim());
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                _s.RaiseToast("Couldn't save the license: " + ex.Message, ToastKind.Warning);
                return false;
            }
            _s.RaiseToast("RavenDB license saved" + (name.Length > 0 ? " (" + name + ")" : string.Empty)
                + (_s.Server.IsRunning ? ". Restart the server to apply it." : "."), ToastKind.Success);
            _s.Notify(StateArea.Embedded);
            return true;
        }

        public void RemoveRavenLicense()
        {
            if (_s.Server == null) { return; }
            EmbeddedServerSupport.TryDelete(_s.Server.LicensePath);
            _s.RaiseToast("RavenDB license removed" + (_s.Server.IsRunning ? ". Restart the server to apply it." : "."), ToastKind.Info);
            _s.Notify(StateArea.Embedded);
        }

        /// <summary>Who the saved license is for, "" when it has no name, or null when none is saved.</summary>
        public string RavenLicenseHolder()
        {
            if (_s.Server == null || !System.IO.File.Exists(_s.Server.LicensePath)) { return null; }
            string name, error;
            try
            {
                return EmbeddedServerSupport.ValidateLicense(System.IO.File.ReadAllText(_s.Server.LicensePath), out name, out error) ? name : string.Empty;
            }
            catch (System.IO.IOException) { return string.Empty; }
        }

        public void SetEmbeddedAutostart(bool autostart)
        {
            if (_s.Server == null) { return; }
            _s.Server.AutoStart = autostart;
            _prefs.SetInt(PrefKeys.EmbeddedAutostart, autostart ? 1 : 0);
            _prefs.Save();
            _s.Notify(StateArea.Embedded);
        }

        // =====================================================================
        // Plugins (N6): listed from any server, managed only on the embedded one
        // =====================================================================

        /// <summary>
        /// Install, uninstall and enable/disable change the built-in server's
        /// folders, so they're offered only while connected to it (or to the
        /// loopback address it will run on, when it's stopped).
        /// </summary>
        public bool CanManagePlugins
        {
            get
            {
                if (_s.Server == null || !ServerHostManager.IsLoopbackUrl(_s.Config.ServerUrl)) { return false; }
                if (!_s.Server.IsRunning) { return true; }
                return string.Equals(_s.Config.ServerUrl.TrimEnd('/'), "http://127.0.0.1:" + _s.Server.ActivePort, StringComparison.OrdinalIgnoreCase);
            }
        }

        public IEnumerator LoadPlugins()
        {
            if (!_s.TryBeginBusy("plugins")) { yield break; }
            try
            {
                bool ok = false;
                string body = string.Empty;
                yield return _s.Config.GetText("/plugins", delegate (bool o, string b) { ok = o; body = b; });
                List<PluginEntry> plugins;
                string engine;
                if (!ok)
                {
                    _s.PluginsError = body.Contains("404") ? "This server is older than the plugin manager (no /plugins)." : "Couldn't read the server's plugins: " + body;
                }
                else if (!PluginPackages.TryParseListing(body, out plugins, out engine))
                {
                    _s.PluginsError = "The server's /plugins reply wasn't understood.";
                }
                else
                {
                    _s.Plugins.Clear();
                    _s.Plugins.AddRange(plugins);
                    _s.PluginsEngineVersion = engine;
                    _s.PluginsError = string.Empty;
                }
                _s.PluginsLoaded = true;
                _s.Notify(StateArea.Plugins);
            }
            finally { _s.EndBusy("plugins"); }
        }

        /// <summary>Enables or disables a plugin id for the next embedded server start.</summary>
        public void SetPluginEnabled(string id, bool enabled)
        {
            if (!CanManagePlugins) { return; }
            try
            {
                if (PluginPackages.SetDisabled(_s.Server.DisabledPluginsFile, id, !enabled)) { MarkPluginsChanged(id, enabled); }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                _s.RaiseToast("Couldn't save the plugin list: " + ex.Message, ToastKind.Warning);
            }
        }

        public bool IsPluginDisabled(string id)
        {
            return _s.Server != null && PluginPackages.ReadDisabled(_s.Server.DisabledPluginsFile).Contains(id);
        }

        /// <summary>User-installed packages on disk (id to folder), including ones the server hasn't loaded yet.</summary>
        public Dictionary<string, string> InstalledPlugins()
        {
            return _s.Server != null ? PluginPackages.ScanInstalled(_s.Server.UserPluginsDir) : new Dictionary<string, string>();
        }

        /// <summary>
        /// Checks a zip for install: a readable package, no unsafe paths, an
        /// id not already on the server or in the plugins folder, a server
        /// new enough. Toasts the reason and returns null when it can't go in.
        /// </summary>
        public PluginZipInfo CheckPluginZip(string path)
        {
            if (!CanManagePlugins) { _s.RaiseToast("Plugins can only be installed on the built-in server.", ToastKind.Warning); return null; }
            string zip = (path ?? string.Empty).Trim().Trim('"');
            if (zip.Length == 0) { _s.RaiseToast("Enter the path to the plugin's .zip file.", ToastKind.Warning); return null; }
            PluginZipInfo info;
            string error;
            if (!PluginPackages.Inspect(zip, out info, out error)) { _s.RaiseToast(error, ToastKind.Warning); return null; }
            var known = new List<string>(InstalledPlugins().Keys);
            foreach (var p in _s.Plugins) { known.Add(p.Id); }
            string engine = _s.PluginsEngineVersion.Length > 0 ? _s.PluginsEngineVersion : ServerHostManager.ExpectedVersion();
            string blocker = PluginPackages.InstallBlocker(info, known, engine);
            if (blocker != null) { _s.RaiseToast(blocker, ToastKind.Warning); return null; }
            return info;
        }

        /// <summary>Extracts a checked zip into the user plugins folder; it loads on the next server start.</summary>
        public bool InstallPlugin(PluginZipInfo info)
        {
            if (info == null || !CanManagePlugins) { return false; }
            try
            {
                PluginPackages.Install(info, _s.Server.UserPluginsDir);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is System.IO.InvalidDataException)
            {
                _s.RaiseToast("Couldn't install " + info.Name + ": " + ex.Message, ToastKind.Warning);
                return false;
            }
            _s.RaiseToast(info.Name + " " + info.Version + " installed" + (_s.Server.IsRunning ? ". Restarting the server to load it…" : "; it loads when the server starts."), ToastKind.Success);
            MarkPluginsChanged(info.Id, true);
            return true;
        }

        public bool UninstallPlugin(string id)
        {
            if (!CanManagePlugins) { return false; }
            string error;
            if (!PluginPackages.Uninstall(_s.Server.UserPluginsDir, id, out error)) { _s.RaiseToast(error, ToastKind.Warning); return false; }
            try { PluginPackages.SetDisabled(_s.Server.DisabledPluginsFile, id, false); }
            catch (System.IO.IOException) { }
            _s.RaiseToast("Plugin removed" + (_s.Server.IsRunning ? ". Restarting the server to unload it…" : "."), ToastKind.Info);
            MarkPluginsChanged(id, false);
            return true;
        }

        /// <summary>
        /// Quiet time after the last plugin change before the automatic
        /// restart, so flipping several switches restarts the server once.
        /// </summary>
        public float PluginRestartDelay = 1.5f;

        private bool _pluginRestartScheduled;
        private float _pluginRestartDue;
        // What each changed plugin should look like after the restart: true = loaded.
        private readonly Dictionary<string, bool> _pluginExpectations = new Dictionary<string, bool>(StringComparer.Ordinal);

        private void MarkPluginsChanged(string id, bool expectLoaded)
        {
            bool running = _s.Server != null && _s.Server.IsRunning;
            _s.PluginsRestartNeeded = running;
            if (!string.IsNullOrEmpty(id)) { _pluginExpectations[id] = expectLoaded; }
            _s.Notify(StateArea.Plugins);
            if (!running) { return; }
            _pluginRestartDue = Time.realtimeSinceStartup + PluginRestartDelay;
            if (!_pluginRestartScheduled)
            {
                _pluginRestartScheduled = true;
                Run(RestartWhenQuiet());
            }
        }

        /// <summary>
        /// The automatic restart: waits out the debounce, any turn in flight
        /// (a restart mid-turn would fail its tool calls) and any other server
        /// start/stop, then restarts unless someone already pressed the button.
        /// </summary>
        private IEnumerator RestartWhenQuiet()
        {
            try
            {
                while (Time.realtimeSinceStartup < _pluginRestartDue || _s.Driver.IsBusy || _s.IsBusy("embedded"))
                {
                    yield return null;
                }
                if (!_s.PluginsRestartNeeded) { yield break; }
                yield return RestartEmbedded();
            }
            finally { _pluginRestartScheduled = false; }
        }

        /// <summary>
        /// Stops and starts the embedded server so plugin changes apply, then
        /// re-reads the list, runs the health check (/health plus an MCP
        /// tools/list) and checks each changed plugin landed as expected.
        /// </summary>
        public IEnumerator RestartEmbedded()
        {
            if (_s.Server == null) { yield break; }
            int port = _s.Server.Port;
            StopEmbedded();
            yield return StartEmbedded(port);
            if (!_s.Server.IsRunning)
            {
                _s.RaiseToast("The server didn't come back after the restart: " + _s.EmbeddedMessage, ToastKind.Warning);
                yield break;
            }
            _s.PluginsRestartNeeded = false;
            _s.Driver.InvalidateTools();
            _s.Mcp.ResetSessions();
            yield return LoadPlugins();
            yield return CheckConnection();
            if (_s.Connection != ConnectionStatus.Healthy)
            {
                _s.RaiseToast("The server restarted but failed its health check: " + _s.ConnectionMessage, ToastKind.Warning);
                yield break;
            }
            string problems = PluginExpectationProblems(_pluginExpectations, _s.Plugins);
            _pluginExpectations.Clear();
            _s.RaiseToast(problems ?? "Server restarted; plugin changes applied.", problems == null ? ToastKind.Success : ToastKind.Warning);
        }

        /// <summary>Null when every changed plugin is loaded (or gone) as intended, else what went wrong.</summary>
        public static string PluginExpectationProblems(IDictionary<string, bool> expectations, IList<PluginEntry> listed)
        {
            var problems = new List<string>();
            foreach (var pair in expectations)
            {
                PluginEntry entry = null;
                foreach (var p in listed) { if (p.Id == pair.Key) { entry = p; break; } }
                bool loaded = entry != null && entry.Loaded;
                if (pair.Value && !loaded)
                {
                    string why = entry == null ? "the server didn't find it"
                        : entry.Errors.Count > 0 ? string.Join("; ", entry.Errors.ToArray()) : "no error reported";
                    problems.Add((entry != null && entry.Name.Length > 0 ? entry.Name : pair.Key) + " didn't load: " + why);
                }
                else if (!pair.Value && loaded)
                {
                    problems.Add((entry.Name.Length > 0 ? entry.Name : pair.Key) + " is still loaded");
                }
            }
            return problems.Count == 0 ? null : string.Join(". ", problems.ToArray()) + ".";
        }

        public void OpenPluginsFolder()
        {
            if (_s.Server == null) { return; }
            try { System.IO.Directory.CreateDirectory(_s.Server.UserPluginsDir); }
            catch (System.IO.IOException) { }
            Application.OpenURL(new Uri(_s.Server.UserPluginsDir).AbsoluteUri);
        }

        // =====================================================================
        // AI provider profiles (one source of truth for Settings and Setup)
        // =====================================================================

        public void SelectProfile(int index)
        {
            _s.Byok.ActiveIndex = Mathf.Clamp(index, 0, _s.Byok.Profiles.Count - 1);
            _s.Byok.Save();
            _s.Driver.ClearProviderProblem();
            _s.Notify(StateArea.Providers | StateArea.Driver);
        }

        public void AddProfile(string presetId)
        {
            _s.Byok.AddProfile(presetId);
            _s.Byok.Save();
            _s.Notify(StateArea.Providers);
        }

        public void ApplyPreset(string presetId)
        {
            _s.Byok.ApplyPreset(_s.Byok.Active, presetId);
            _s.Notify(StateArea.Providers);
        }

        /// <summary>Copies the edited fields into the active profile. A blank key keeps the saved one.</summary>
        public bool SaveProfile(ProviderProfile edited, string newKey, out string reason)
        {
            var p = _s.Byok.Active;
            if (!string.IsNullOrEmpty((edited.Name ?? string.Empty).Trim())) { p.Name = edited.Name.Trim(); }
            p.BaseUrl = (edited.BaseUrl ?? string.Empty).Trim();
            p.Model = (edited.Model ?? string.Empty).Trim();
            p.Temperature = Mathf.Clamp(edited.Temperature, 0f, 2f);
            p.MaxTokens = Math.Max(0, edited.MaxTokens);
            p.ReasoningEffort = edited.ReasoningEffort ?? string.Empty;
            p.DisableStreaming = edited.DisableStreaming;
            p.SinglePass = edited.SinglePass;
            if (!string.IsNullOrEmpty((newKey ?? string.Empty).Trim())) { _s.Byok.SetApiKey(newKey); }
            _s.Byok.Save();
            // New settings deserve a fresh try: the old refusal was about the old ones.
            _s.Driver.ClearProviderProblem();
            bool ok = _s.Byok.Validate(out reason);
            _s.Notify(StateArea.Providers | StateArea.Driver);
            return ok;
        }

        /// <summary>
        /// Tests the active profile and records the verdict: a rejected key or an
        /// unknown model blocks play until fixed; an unreachable provider doesn't
        /// (networks blip), it only shows in the result.
        /// </summary>
        public IEnumerator TestProvider(Action<bool, string> done)
        {
            yield return _s.Byok.TestConnection(delegate (bool ok, string message)
            {
                if (ok) { _s.Driver.ClearProviderProblem(); }
                else if (IsSettingsProblem(message)) { _s.Driver.ReportProviderProblem(message); }
                _s.Notify(StateArea.Providers | StateArea.Driver);
                if (done != null) { done(ok, message); }
            });
        }

        /// <summary>Startup: a configured provider is tested once, quietly, so a dead key shows before the first turn.</summary>
        public IEnumerator CheckProvider()
        {
            string reason;
            if (!_s.Byok.Validate(out reason)) { yield break; }
            yield return TestProvider(null);
        }

        internal static bool IsSettingsProblem(string testMessage)
        {
            string m = testMessage ?? string.Empty;
            return m.IndexOf("rejected the API key", StringComparison.Ordinal) >= 0
                || m.IndexOf("not in the provider's model list", StringComparison.Ordinal) >= 0;
        }

        public void ForgetProviderKey()
        {
            _s.Byok.ClearSecrets();
            _s.Notify(StateArea.Providers);
        }

        public void DeleteProfile()
        {
            _s.Byok.DeleteActive();
            _s.Byok.Save();
            _s.Notify(StateArea.Providers | StateArea.Driver);
        }
    }
}
