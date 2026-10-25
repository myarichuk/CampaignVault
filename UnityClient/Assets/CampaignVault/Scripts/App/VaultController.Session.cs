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
    /// <summary>The active campaign and its session: selecting a table, restoring its chronicle, start_session/end_session, the table following the DM's own tool calls, the handoff.</summary>
    public sealed partial class VaultController
    {
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
            _appendedLines = 0;
            _historySkip = 0;
            if (_s.Store == null || !_s.HasCampaign) { return false; }
            var restored = _s.Store.Load(_s.CampaignSlug);
            _historySkip = TranscriptStore.LoadLimit;
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

        /// <summary>A new player character enters the CAMPAIGN line's roster and the chronicle (A19).</summary>
        internal void AddToPartyLine(string id, string name)
        {
            string entry = id + " — " + name;
            string line = _s.Prompts.PartyLine ?? string.Empty;
            if (line.IndexOf(id, StringComparison.Ordinal) < 0)
            {
                _s.Prompts.PartyLine = line.Length > 0 ? line + "; " + entry : entry;
            }
            AddChronicle(new TranscriptSegment { Kind = SegmentKind.System, Text = "✦ " + name + " joins the party." });
            _s.Notify(StateArea.Pc | StateArea.Session);
        }

        /// <summary>A line for the chronicle outside a player turn (a turn saves its own lines when it ends).</summary>
        private void AddChronicle(TranscriptSegment seg)
        {
            _s.Transcript.Add(seg);
            if (!_turnActive && _s.HasCampaign) { AppendChronicle(_s.CampaignSlug, new[] { seg }); }
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
        internal void OnDriverTool(string tool, string resultText) { OnDriverTool(tool, null, resultText); }

        /// <summary>
        /// The DM's own tool calls move the table: start_session opens it, end_session closes it, and every
        /// take_turn updates it (A1), so the client never re-reads start_session per turn (which forced the
        /// server into a full reseed and re-taught every one-time hint).
        /// </summary>
        internal void OnDriverTool(string tool, JsonValue args, string resultText)
        {
            if (tool != "start_session" && tool != "end_session" && tool != "take_turn" && tool != "combat") { return; }
            JsonValue parsed;
            if (!JsonValue.TryParse(resultText, out parsed) || parsed.Kind != JsonKind.Object || !parsed.GetBool("success", true)) { return; }
            var data = parsed.Get("data");
            if (data.IsNull) { data = parsed; }
            if (tool == "combat")
            {
                string action = args != null ? args.GetStringAny(new[] { "action", "Action" }, string.Empty) : string.Empty;
                if (action == "start") { AddEventLine("⚔ Combat begins. Initiative is rolled."); }
                else if (action == "end") { AddEventLine("⚔ Combat ends."); }
            }
            else if (tool == "take_turn")
            {
                ApplyTurn(args, data);
                NoteEngineWarnings(parsed, data);
            }
            else if (tool == "start_session")
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

        /// <summary>One committed take_turn: the table and the prompt's fingerprint follow it (A1, A2).</summary>
        internal void ApplyTurn(JsonValue args, JsonValue data)
        {
            if (_s.Session == null) { return; }
            var update = _s.Session.ApplyTurn(args, data);
            if (_s.Session.Fingerprint.Length > 0) { _s.Prompts.PartyFingerprint = _s.Session.Fingerprint; }
            if (!update.Any) { return; }
            foreach (string who in update.Downed) { AddEventLine("☠ " + who + " is down. A death save is due on their turn."); }
            if (update.Time) { _s.SessionStatus = DescribeSession(_s.Session); }
            AnnounceLevelUps(_s.Session);
            if (update.NewQuestIds.Count > 0) { Run(FillQuestTitles(_s.CampaignSlug, new List<string>(update.NewQuestIds))); }
            _s.Notify(StateArea.Session | StateArea.Pc | StateArea.Companions);
        }

        private string _lastEventLine = string.Empty;

        /// <summary>A muted card in the story for something the engine did (not chronicle: System lines are never saved). The same line twice in a row is shown once.</summary>
        private void AddEventLine(string text)
        {
            if (text == _lastEventLine) { return; }
            _lastEventLine = text;
            _s.Transcript.Add(new TranscriptSegment { Kind = SegmentKind.System, Text = TextSanitizer.Clean(text, 240) });
        }

        /// <summary>The engine's ENGINE WARNING lines (world pressure the DM has to fix): the player sees that the world asked for something, muted.</summary>
        private void NoteEngineWarnings(JsonValue envelope, JsonValue data)
        {
            var pressure = envelope.GetArray("worldPressure");
            if (pressure.Count == 0) { pressure = data.GetArray("worldPressure"); }
            if (pressure.Count == 0) { pressure = data.Get("worldStateDelta").GetArray("worldPressure"); }
            if (pressure.Count == 0) { pressure = data.Get("worldState").GetArray("worldPressure"); }
            foreach (var line in pressure)
            {
                if (line.Kind != JsonKind.String || !line.StringValue.StartsWith("ENGINE WARNING", System.StringComparison.Ordinal)) { continue; }
                string text = line.StringValue;
                int cut = text.IndexOf('{');
                if (cut > 0) { text = text.Substring(0, cut).TrimEnd(' ', ':', '—', '-'); }
                AddEventLine("⚙ " + text);
            }
        }

        /// <summary>A quest the DM just opened is known by id only: one get_entity gives the table its title and open objectives.</summary>
        private IEnumerator FillQuestTitles(string slug, List<string> questIds)
        {
            foreach (string id in questIds)
            {
                if (_s.Session == null || _s.CampaignSlug != slug) { yield break; }
                var args = CampaignArgs();
                args.ObjectValue["entityId"] = JsonValue.FromString(id);
                McpOutcome<ToolPayload> result = null;
                yield return _s.Mcp.CallToolData(_s.Config, "play", "get_entity", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
                if (result == null || !result.Ok || _s.Session == null || _s.CampaignSlug != slug) { continue; }
                var quest = result.Data.Data;
                var quests = new List<DashboardQuest>(_s.Session.Quests);
                int at = quests.FindIndex(delegate (DashboardQuest q) { return q.Id == id; });
                if (at < 0) { continue; }
                var filled = new DashboardQuest { Id = id, Title = quest.GetStringAny(new[] { "title", "Title" }, quests[at].Title), Deadline = quests[at].Deadline, Overdue = quests[at].Overdue };
                int open = 0;
                foreach (var objective in quest.GetArray("objectives"))
                {
                    if (!objective.GetBool("isComplete", objective.GetBool("IsComplete", objective.GetBool("completed", false)))) { open++; }
                }
                filled.OpenObjectives = open;
                quests[at] = filled;
                _s.Session.Quests = quests;
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

        /// <summary>start_session's refusal for a campaign that has no player character yet.</summary>
        internal static bool IsNoPartyError(string message)
        {
            return (message ?? string.Empty).IndexOf("no party members", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Opens or resumes the session on the server and folds its digest into the table.</summary>
        /// <param name="soft">A failure is a warning, not an error (the player's line still goes to the DM).</param>
        private IEnumerator OpenSession(string title, string busyKey, bool announce, bool soft, bool quiet = false)
        {
            if (!RequireCampaign(delegate (string m) { _s.SessionStatus = m; _s.Notify(StateArea.Session); })) { yield break; }
            if (!_s.TryBeginBusy(busyKey)) { yield break; }
            try
            {
                var args = CampaignArgs();
                if (!string.IsNullOrEmpty(title)) { args.ObjectValue["title"] = JsonValue.FromString(title.Trim()); }
                args.ObjectValue["includeScene"] = JsonValue.FromBool(false); // the table only needs the roster; the DM loads the scene on its first turn
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
            var npcs = draft.People.FindAll(delegate (NpcStanceRow n) { return n != null && !string.IsNullOrWhiteSpace(n.Id); });
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

        /// <summary>
        /// The DM drafts the handoff from this session's chronicle, the previous handoff and the people it showed,
        /// in one tool-free side call; the draft lands in the form to edit before CHECKPOINT or END.
        /// </summary>
        public IEnumerator DraftHandoff(HandoffDraft draft)
        {
            _s.HandoffIssues.Clear();
            string notReady;
            if (!_s.HasCampaign) { _s.HandoffIssues.Add("Pick a campaign first."); _s.Notify(StateArea.Session); yield break; }
            if (!_s.ProviderReady(out notReady)) { _s.HandoffIssues.Add("Drafting needs a working AI provider: " + notReady); _s.Notify(StateArea.Session); yield break; }
            if (!_s.TryBeginBusy("handoff")) { yield break; }
            try
            {
                var messages = HandoffDrafter.Messages(_s.Session, _s.Transcript.Segments, _s.Prompts.PartyLine);
                string reply = null;
                string error = null;
                yield return _s.Driver.Brainstorm(messages, null, delegate (string r, string e) { reply = r; error = e; });
                if (error == null && !HandoffDrafter.TryApply(reply, _s.Session, draft, out error)) { error = error ?? "the reply was not the expected JSON."; }
                if (error != null)
                {
                    _s.HandoffIssues.Add("The DM couldn't draft it: " + TextSanitizer.Clean(error, 300));
                }
                else
                {
                    draft.Version++;
                    _s.RaiseToast("The DM drafted the handoff. Read it over before you send it.", ToastKind.Success);
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
    }
}
