using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// Unity-side roleplay driver (the opencode role, in-client): sends the
    /// system prompt + player text to the BYOK chat endpoint, executes the
    /// model's MCP tool calls against CampaignVault, and feeds results back
    /// until the model answers in prose. Bounded: at most MaxToolIterations
    /// model turns per player message, one tool timeout each.
    ///
    /// Token budget, since every model call resends everything:
    /// - only the active connector's tools are offered (/play by default; the
    ///   server's profiles exist so a playing client doesn't pay for /build);
    /// - skills load on demand through the local load_skill tool instead of
    ///   riding in the system prompt;
    /// - finished turns are compacted: tool results shrink to their envelope
    ///   summary and bulky tool-call arguments are elided (the server holds
    ///   the real state; the model re-reads it with a tool when it needs it);
    /// - history is capped by characters, trimmed a whole turn at a time.
    /// </summary>
    public class OpenAiChatDriver : MonoBehaviour
    {
        public const int MaxToolIterations = 8;
        public const int MaxHistoryChars = 48000;
        public const int CompactedArgumentChars = 400;
        public const int CompactedResultChars = 280;

        public ByokSettings Byok;
        public VaultClientConfig Vault;
        public McpClient Mcp;
        public SystemPromptProvider Prompts;

        /// <summary>Empty = every advertised tool is offered to the model.</summary>
        public HashSet<string> AllowedTools = new HashSet<string>();

        /// <summary>True while a player message is being resolved; the UI blocks a second send.</summary>
        public bool IsBusy { get; private set; }

        /// <summary>Human-readable phase for the chat status line ("contacting model", "calling take_turn"…).</summary>
        public string Status { get; private set; }

        /// <summary>Tool currently executing, if any (shown while busy).</summary>
        public string CurrentTool { get; private set; }

        /// <summary>Last failure text, kept after IsBusy clears so the UI can show it.</summary>
        public string LastError { get; private set; }

        /// <summary>The last failed call in full: friendly text, copy-pastable technical block, whether a retry makes sense. Null after a success.</summary>
        public LlmFailure LastFailure { get; private set; }

        /// <summary>
        /// Set when the provider refused outright (key rejected, model or endpoint
        /// unknown): retrying can't help until the settings change. Cleared by the
        /// next successful reply or by <see cref="ClearProviderProblem"/>.
        /// </summary>
        public string ProviderProblem { get; private set; } = string.Empty;

        public void ClearProviderProblem() { ProviderProblem = string.Empty; }

        /// <summary>A failed provider check (Settings → test, or the one at startup) counts too.</summary>
        public void ReportProviderProblem(string problem) { ProviderProblem = problem ?? string.Empty; }

        /// <summary>What an HTTP status means for the provider's setup; empty when a retry could still work.</summary>
        internal static string ProviderProblemFor(long status, string model)
        {
            if (status == 401 || status == 403) { return "the provider rejected the API key (HTTP " + status + ")."; }
            if (status == 404) { return "the provider doesn't know model \"" + model + "\" or this endpoint (HTTP 404)."; }
            return string.Empty;
        }

        /// <summary>One-line summary of the last request (model, message/tool counts) for the Inspect panel.</summary>
        public string LastRequestMeta = string.Empty;

        /// <summary>Last raw model response JSON (capped) for the Inspect panel.</summary>
        public string LastResponseJson = string.Empty;

        /// <summary>Recent tool calls, oldest first (capped at 30).</summary>
        public readonly List<string> ToolLog = new List<string>();

        public const int MaxTurnRecords = 200;

        /// <summary>Finished player messages this conversation, oldest first (capped), for the inspector and transcript export.</summary>
        public readonly List<TurnRecord> Turns = new List<TurnRecord>();

        /// <summary>Provider-reported tokens since the conversation started.</summary>
        public TokenUsage SessionUsage = new TokenUsage();

        /// <summary>The campaign's persisted running spend; every counted call also lands here (null = not tracked).</summary>
        public CampaignUsageStore CampaignUsage;

        /// <summary>
        /// Who the player's character is, for the storyteller (name, looks,
        /// traits); filled from get_entity when the PC loads. Empty is fine.
        /// </summary>
        public string PcCard = string.Empty;

        // The storyteller's recent scenes: (player line, passage), oldest first.
        private readonly List<KeyValuePair<string, string>> _passages = new List<KeyValuePair<string, string>>();

        /// <summary>The turn being resolved right now, or the last one.</summary>
        public TurnRecord CurrentTurn { get; private set; }

        /// <summary>
        /// A server tool the model called succeeded: (tool name, its raw result
        /// text). The app layer follows the session through it (a start_session
        /// or end_session the DM made on its own).
        /// </summary>
        public event Action<string, string> ToolSucceeded;

        private readonly List<JsonValue> _history = new List<JsonValue>();

        /// <summary>The replayable conversation, for tests and the dev inspector.</summary>
        internal IReadOnlyList<JsonValue> History { get { return _history; } }
        // tool_call_id -> tool name, so compaction knows what a tool message was.
        private readonly Dictionary<string, string> _callNames = new Dictionary<string, string>();
        // Index in _history where the current (uncompacted) turn starts.
        private int _turnStart;

        private List<McpToolInfo> _toolCache = new List<McpToolInfo>();
        private string _toolCacheKey = string.Empty;
        private bool _cancelRequested;
        // Bumped per turn: a turn abandoned by OnDisable can't clear the busy
        // flag of the turn that replaced it.
        private int _generation;
        private UnityWebRequest _activeChat;
        // Endpoints that rejected stream:true but answered without it.
        private readonly HashSet<string> _noStreamEndpoints = new HashSet<string>();
        // Endpoints that stream but reject stream_options (usage reporting).
        private readonly HashSet<string> _noStreamOptions = new HashSet<string>();

        /// <summary>
        /// Stops the running turn now: the model call and any tool call on the
        /// wire are aborted, not waited out. A tool the server already received
        /// may still commit, and the transcript says so.
        /// </summary>
        public void Cancel()
        {
            if (!IsBusy) { return; }
            _cancelRequested = true;
            if (_activeChat != null) { _activeChat.Abort(); }
            if (Mcp != null) { Mcp.AbortAll(); }
        }

        /// <summary>
        /// Unity drops a stopped coroutine without running its finally blocks,
        /// so a disabled driver resets its own busy state instead of trusting them.
        /// </summary>
        private void OnDisable()
        {
            if (!IsBusy) { return; }
            Cancel();
            _generation++;
            IsBusy = false;
            Status = string.Empty;
            CurrentTool = string.Empty;
        }

        /// <summary>Forget the conversation (campaign switched): the next message starts clean.</summary>
        public void ResetConversation()
        {
            _history.Clear();
            _callNames.Clear();
            _turnStart = 0;
            LastRequestMeta = string.Empty;
            LastResponseJson = string.Empty;
            ToolLog.Clear();
            LastError = string.Empty;
            LastFailure = null;
            Turns.Clear();
            _passages.Clear();
            SessionUsage = new TokenUsage();
            CurrentTurn = null;
        }

        /// <summary>
        /// Gives the storyteller back its recent scenes (from the client's saved
        /// history) after a restart, so the prose picks up where it left off.
        /// </summary>
        public void RestorePassages(IList<KeyValuePair<string, string>> passages)
        {
            _passages.Clear();
            if (passages == null) { return; }
            foreach (var p in passages)
            {
                _passages.Add(new KeyValuePair<string, string>(p.Key, Storyteller.Cap(p.Value, Storyteller.MaxPassageChars)));
            }
            while (_passages.Count > Storyteller.MaxPassages) { _passages.RemoveAt(0); }
        }

        /// <summary>Force a fresh tools/list on the next message (server, connector or plugins changed).</summary>
        public void InvalidateTools() { _toolCacheKey = string.Empty; }

        public IEnumerator RefreshTools(Action<McpOutcome<int>> done)
        {
            string connector = Vault.ActiveConnector();
            McpOutcome<List<McpToolInfo>> listed = null;
            yield return Mcp.ListTools(Vault, connector, delegate (McpOutcome<List<McpToolInfo>> o) { listed = o; });
            if (listed == null || !listed.Ok || listed.Data.Count == 0)
            {
                done(McpOutcome<int>.Fail(
                    listed != null ? listed.ErrorCode : "MCP",
                    listed != null && !listed.Ok ? listed.ErrorMessage : "no tools advertised"));
                yield break;
            }
            _toolCache = listed.Data;
            _toolCacheKey = Vault.ConnectorPath(connector);
            done(McpOutcome<int>.Success(_toolCache.Count));
        }

        /// <param name="changed">Called whenever the transcript grows, so the UI can show progress mid-turn.</param>
        public IEnumerator SendPlayerText(string playerText, VaultTranscript transcript, Action changed)
        {
            if (IsBusy) { yield break; }
            IsBusy = true;
            int generation = ++_generation;
            _cancelRequested = false;
            LastError = string.Empty;
            LastFailure = null;
            CurrentTool = string.Empty;
            JsonValue userMessage = null;
            bool assistantReplied = false;
            bool narrated = false;
            bool anyOutput = false;
            bool loopFailed = false;
            TranscriptSegment lastAside = null;
            // Two-pass (default): the loop does the bookkeeping and says DONE,
            // then a tool-free storyteller call writes the scene from a brief.
            bool twoPass = !Byok.Active.SinglePass;
            bool oocPlayer = twoPass && Storyteller.IsOocPlayer(playerText);
            bool handToStoryteller = false;
            var brief = new TurnBrief();
            var record = new TurnRecord { Player = playerText ?? string.Empty, Model = Byok.Model };
            CurrentTurn = record;
            try
            {
                string reason;
                if (!Byok.Validate(out reason))
                {
                    Fail(transcript, "Chat blocked: " + reason, changed);
                    yield break;
                }
                if (_toolCacheKey != Vault.ConnectorPath(Vault.ActiveConnector()))
                {
                    Status = "reaching CampaignVault tools…";
                    McpOutcome<int> refresh = null;
                    yield return RefreshTools(delegate (McpOutcome<int> o) { refresh = o; });
                    if (refresh == null || !refresh.Ok)
                    {
                        Fail(transcript, "Chat blocked: cannot reach CampaignVault tools (" + (refresh != null ? refresh.ErrorMessage : "?") + ").", changed);
                        yield break;
                    }
                }

                CompactFinishedTurns();
                // OOC lines may carry the onboarding brief, which runs past a normal turn's length.
                string cleanPlayer = TextSanitizer.Clean(playerText, Storyteller.IsOocPlayer(playerText) ? 12000 : 4000);
                userMessage = RoleMessage("user", twoPass ? Storyteller.LoopUserMessage(LastPassage, cleanPlayer) : cleanPlayer);
                _history.Add(userMessage);
                TrimToBudget();
                string systemPrompt = twoPass ? Prompts.BuildSystemPrompt(Storyteller.LoopContract) : Prompts.BuildSystemPrompt(Storyteller.SinglePassNote);

                for (int turn = 0; turn < MaxToolIterations; turn++)
                {
                    if (_cancelRequested) { AddStopped(transcript, changed); break; }
                    Status = "the DM is thinking…";
                    CurrentTool = string.Empty;

                    // Streamed text lands in one live segment; once the reply is
                    // complete it's replaced by its final form (narration split
                    // into voices, or a muted aside when tool calls ride along).
                    // In two-pass mode the loop's words are never the story, so
                    // they aren't streamed at all: the status line says it's working.
                    TranscriptSegment live = null;
                    Action<string> onDelta = delegate (string delta)
                    {
                        if (twoPass)
                        {
                            Status = "the DM works through what happens…";
                            return;
                        }
                        if (live == null)
                        {
                            live = new TranscriptSegment { Kind = SegmentKind.Narration, Streaming = true };
                            transcript.Add(live);
                        }
                        live.Text += delta;
                        transcript.NotifyUpdated(live);
                        changed();
                    };
                    JsonValue response = null;
                    string failure = null;
                    yield return RequestReply(
                        delegate (bool stream, bool usageOptions) { return BuildRequest(systemPrompt, stream, usageOptions); },
                        onDelta, delegate (JsonValue r, string err) { response = r; failure = err; });

                    if (_cancelRequested || failure != null)
                    {
                        if (live != null)
                        {
                            // Keep what already arrived on screen, visibly unfinished.
                            live.Streaming = false;
                            live.Kind = SegmentKind.Aside;
                            live.Text = live.Text.TrimEnd() + " …";
                            transcript.NotifyUpdated(live);
                        }
                        if (_cancelRequested) { AddStopped(transcript, changed); }
                        else { Fail(transcript, failure.StartsWith("The AI service") ? failure : "Chat error: " + failure, changed); }
                        loopFailed = true;
                        break;
                    }
                    LastResponseJson = CapText(response.ToJson(), 8000);
                    var usage = MeasureUsage(response, Byok.Model);
                    record.Usage.Add(usage);
                    RecordUsage(usage, Byok.Model);
                    JsonValue firstChoice = response.GetArray("choices").Count > 0
                        ? response.GetArray("choices")[0]
                        : JsonValue.Null;
                    JsonValue message = firstChoice.IsNull ? JsonValue.Null : firstChoice.Get("message");
                    if (message.IsNull)
                    {
                        if (live != null) { transcript.Remove(live); }
                        Fail(transcript, Byok.Model + " returned no message (empty choices). Open Inspect for the raw response.", changed,
                            LlmFailure.Malformed("no message in the reply", FailureContext(Byok.ChatUrl(), 1, LastResponseJson)));
                        loopFailed = true;
                        break;
                    }

                    string content;
                    string reasoning;
                    ExtractContent(message, out content, out reasoning);
                    var toolCalls = message.GetArray("tool_calls");
                    _history.Add(PruneMessage(message, content));
                    assistantReplied = true;
                    if (live != null) { transcript.Remove(live); }

                    // A whitespace-only reply is not narration: without the trim it
                    // renders as a blank line and defeats the empty-turn guard.
                    string trimmed = content.Trim();
                    if (twoPass && toolCalls.Count == 0)
                    {
                        string ooc;
                        string before;
                        if (Storyteller.TryOocReply(trimmed, out ooc) || (oocPlayer && !Storyteller.IsDone(trimmed)))
                        {
                            // Out of character: shown as is, no scene.
                            string answer = ooc.Length > 0 ? ooc : trimmed;
                            transcript.Add(new TranscriptSegment { Kind = SegmentKind.Narration, Text = TextSanitizer.Clean(answer) });
                            record.AddNarration(answer);
                            narrated = true;
                            anyOutput = true;
                            changed();
                        }
                        else if (Storyteller.IsDone(trimmed))
                        {
                            handToStoryteller = !oocPlayer;
                            if (oocPlayer) { AddSystem(transcript, "The DM applied that; no scene for an out-of-character request."); anyOutput = true; changed(); }
                        }
                        else
                        {
                            // The loop narrated despite the contract: keep its words as
                            // notes (folded away with the tool activity) and let the
                            // storyteller write the scene, so the story isn't told twice.
                            string notes = Storyteller.EndsWithDone(trimmed, out before) ? before : trimmed;
                            if (notes.Length > 0)
                            {
                                transcript.Add(new TranscriptSegment { Kind = SegmentKind.Notes, Text = TextSanitizer.Clean(notes) });
                                changed();
                            }
                            handToStoryteller = true;
                        }
                        break;
                    }
                    if (trimmed.Length > 0)
                    {
                        if (toolCalls.Count > 0 && twoPass)
                        {
                            // Two-pass: the storyteller tells the story; this is bookkeeping.
                            transcript.Add(new TranscriptSegment { Kind = SegmentKind.Notes, Text = TextSanitizer.Clean(trimmed) });
                        }
                        else if (toolCalls.Count > 0)
                        {
                            // Words sent with tool calls are the DM thinking aloud
                            // ("let me check the rules"), not the story.
                            lastAside = new TranscriptSegment { Kind = SegmentKind.Aside, Text = TextSanitizer.Clean(trimmed) };
                            transcript.Add(lastAside);
                        }
                        else
                        {
                            SegmentSplitter.AddNarration(transcript, trimmed);
                            record.AddNarration(trimmed);
                            narrated = true;
                        }
                        anyOutput = true;
                        changed();
                    }
                    else if (!string.IsNullOrEmpty(reasoning) && toolCalls.Count == 0)
                    {
                        // Reasoning models sometimes park everything here and
                        // leave content blank: show the thought, honestly labeled.
                        transcript.Add(new TranscriptSegment { Kind = SegmentKind.ToolData, Text = "💭 " + TextSanitizer.Clean(reasoning, 1200) });
                        anyOutput = true;
                        changed();
                    }
                    if (toolCalls.Count == 0) { break; }
                    anyOutput = true;

                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        string callId = toolCalls[i].GetString("id", "call-" + i);
                        string toolName = toolCalls[i].Get("function").GetString("name", string.Empty);
                        _callNames[callId] = toolName;
                        if (_cancelRequested)
                        {
                            // Every tool_call needs a result or the next request is
                            // rejected; say plainly that this one never ran.
                            _history.Add(ToolMessage(callId, "not run: the player stopped the turn before this call"));
                            continue;
                        }
                        string argText = toolCalls[i].Get("function").GetString("arguments", "{}");
                        JsonValue args;
                        if (!JsonValue.TryParse(argText, out args)) { args = JsonValue.NewObject(); }
                        CurrentTool = toolName;
                        record.Tools.Add(toolName);

                        string resultText;
                        if (toolName == SystemPromptProvider.LoadSkillTool)
                        {
                            string skill = args.GetString("name", string.Empty);
                            Status = "the DM consults a skill (" + skill + ")…";
                            transcript.Add(new TranscriptSegment { Kind = SegmentKind.ToolData, Text = "✦ the DM consults " + skill });
                            changed();
                            string body;
                            if (Prompts.TryLoadSkill(skill, out body))
                            {
                                resultText = body;
                                LogTool("load_skill:" + skill + " ok · " + body.Length + " chars");
                            }
                            else
                            {
                                resultText = "No skill named '" + skill + "'. Available: " + string.Join(", ", Prompts.SkillNames().ToArray());
                                LogTool("load_skill:" + skill + " MISSING");
                                Fail(transcript, "Unknown skill '" + skill + "'.", changed);
                            }
                        }
                        else
                        {
                            Status = "calling " + toolName + "…";
                            transcript.Add(new TranscriptSegment { Kind = SegmentKind.ToolData, Text = "⚙ " + toolName });
                            changed();
                            McpOutcome<string> call = null;
                            yield return Mcp.CallTool(Vault, Vault.ActiveConnector(), toolName, args,
                                delegate (McpOutcome<string> o) { call = o; });
                            if (call != null && call.Ok)
                            {
                                resultText = call.Data;
                                LogTool(toolName + " ok · " + (call.Data != null ? call.Data.Length : 0) + " chars");
                                foreach (var roll in SegmentSplitter.ExtractRolls(resultText))
                                {
                                    transcript.Add(roll);
                                    record.Rolls.Add(roll.Roll.Label + " " + roll.Roll.Detail + ": " + roll.Roll.Verdict);
                                }
                                brief.Add(toolName, args, resultText, true);
                                if (ToolSucceeded != null)
                                {
                                    try { ToolSucceeded(toolName, resultText); }
                                    catch (Exception ex) { Debug.LogException(ex); }
                                }
                            }
                            else if (_cancelRequested)
                            {
                                resultText = "interrupted: the player stopped the turn while this call was in flight; it may or may not have been applied";
                                LogTool(toolName + " ABORTED");
                                AddSystem(transcript, "⚠ " + toolName + " was cut off mid-call. The server may already have applied it.");
                            }
                            else
                            {
                                resultText = "error [" + (call != null ? call.ErrorCode : "MCP") + "] " + (call != null ? call.ErrorMessage : "?");
                                LogTool(toolName + " FAILED · " + (call != null ? call.ErrorMessage : "no response"));
                                Fail(transcript, "Tool " + toolName + " failed: " + (call != null ? call.ErrorMessage : "no response"), changed);
                                brief.Add(toolName, args, resultText, false);
                            }
                            changed();
                        }
                        _history.Add(ToolMessage(callId, resultText));
                    }
                    if (turn == MaxToolIterations - 1)
                    {
                        if (twoPass && !_cancelRequested)
                        {
                            // Out of steps but state was committed: still tell the story of what landed.
                            AddSystem(transcript, "The DM hit the tool-step cap (" + MaxToolIterations + "); narrating what was committed.");
                            handToStoryteller = !oocPlayer;
                        }
                        else
                        {
                            Fail(transcript, "The DM ran out of tool steps for this message (cap " + MaxToolIterations + "). Ask again to continue.", changed);
                        }
                    }
                }

                if (twoPass && handToStoryteller && !loopFailed && !_cancelRequested)
                {
                    bool told = false;
                    yield return Narrate(cleanPlayer, brief, transcript, changed, record, delegate (bool ok) { told = ok; });
                    narrated = told;
                    anyOutput = true;
                }
                else if (!twoPass && !narrated && lastAside != null && !_cancelRequested)
                {
                    // The model told the story alongside its tool calls and then
                    // went quiet: that aside was the narration after all.
                    lastAside.Kind = SegmentKind.Narration;
                    record.AddNarration(lastAside.Text);
                    transcript.NotifyUpdated(lastAside);
                    changed();
                }
                // A turn that prints nothing is a bug report, not a shrug: say
                // so out loud instead of fading the spinner into silence.
                if (!anyOutput && assistantReplied && !_cancelRequested)
                {
                    Fail(transcript, Byok.Model + " returned an empty reply (no text, no tool calls). "
                        + "Check the endpoint serves OpenAI-style chat completions — open Inspect for the raw response.", changed,
                        LlmFailure.Malformed("no text and no tool calls", FailureContext(Byok.ChatUrl(), 1, LastResponseJson)));
                }
            }
            finally
            {
                // No reply at all: forget the player's line too, or the next
                // send stacks two user messages (strict gateways reject that).
                if (!assistantReplied && userMessage != null) { _history.Remove(userMessage); }
                if (assistantReplied)
                {
                    Turns.Add(record);
                    while (Turns.Count > MaxTurnRecords) { Turns.RemoveAt(0); }
                }
                if (generation == _generation)
                {
                    IsBusy = false;
                    Status = string.Empty;
                    CurrentTool = string.Empty;
                    _activeChat = null;
                }
                changed();
            }
        }

        /// <summary>The last scene the storyteller wrote, or empty.</summary>
        public string LastPassage
        {
            get { return _passages.Count > 0 ? _passages[_passages.Count - 1].Value : string.Empty; }
        }

        /// <summary>
        /// The storyteller call: no tools, prose-only context. Order is most
        /// stable first so providers can cache the prefix: narration system
        /// prompt (header + narration skill + table + PC card), the last few
        /// scenes as prior turns, then the player's line with the brief.
        /// </summary>
        private IEnumerator Narrate(string playerText, TurnBrief brief, VaultTranscript transcript, Action changed, TurnRecord record, Action<bool> done)
        {
            Status = "the storyteller writes the scene…";
            CurrentTool = string.Empty;
            JsonValue messages = BuildNarrationMessages(playerText, brief);
            // Same model as the tool loop: one profile, one model.
            string model = Byok.Model.Trim();

            TranscriptSegment live = null;
            Action<string> onDelta = delegate (string delta)
            {
                if (live == null)
                {
                    live = new TranscriptSegment { Kind = SegmentKind.Narration, Streaming = true };
                    transcript.Add(live);
                }
                live.Text += delta;
                transcript.NotifyUpdated(live);
                changed();
            };
            JsonValue response = null;
            string failure = null;
            yield return RequestReply(
                delegate (bool stream, bool usageOptions) { return BuildNarrationRequest(messages, model, stream, usageOptions); },
                onDelta, delegate (JsonValue r, string err) { response = r; failure = err; });
            if (_cancelRequested || failure != null)
            {
                if (live != null)
                {
                    live.Streaming = false;
                    live.Kind = SegmentKind.Aside;
                    live.Text = live.Text.TrimEnd() + " …";
                    transcript.NotifyUpdated(live);
                }
                if (_cancelRequested) { AddStopped(transcript, changed); }
                else { Fail(transcript, "Storyteller error: " + failure, changed); }
                done(false);
                yield break;
            }
            LastResponseJson = CapText(response.ToJson(), 8000);
            var usage = MeasureUsage(response, model);
            record.Usage.Add(usage);
            RecordUsage(usage, model);
            if (live != null) { transcript.Remove(live); }
            var choices = response.GetArray("choices");
            JsonValue message = choices.Count > 0 ? choices[0].Get("message") : JsonValue.Null;
            string content = string.Empty;
            string reasoning;
            if (!message.IsNull) { ExtractContent(message, out content, out reasoning); }
            string scene = content.Trim();
            if (scene.Length == 0)
            {
                Fail(transcript, model + " returned an empty scene. Open Inspect for the raw response.", changed,
                    LlmFailure.Malformed("an empty scene", FailureContext(Byok.ChatUrl(), 1, LastResponseJson)));
                done(false);
                yield break;
            }
            SegmentSplitter.AddNarration(transcript, scene);
            record.AddNarration(scene);
            _passages.Add(new KeyValuePair<string, string>(playerText, Storyteller.Cap(scene, Storyteller.MaxPassageChars)));
            while (_passages.Count > Storyteller.MaxPassages) { _passages.RemoveAt(0); }
            changed();
            done(true);
        }

        /// <summary>
        /// A tool-free side conversation (onboarding brainstorming): the caller
        /// owns the messages; nothing touches the play history or the server.
        /// done(reply, error) — exactly one of them is non-null.
        /// </summary>
        public IEnumerator Brainstorm(IList<KeyValuePair<string, string>> roleMessages, Action<string> onDelta, Action<string, string> done, bool retryBusy = true)
        {
            string reason;
            if (!Byok.Validate(out reason))
            {
                LastFailure = LlmFailure.Setup(reason, FailureContext(Byok.ChatUrl(), 0, string.Empty));
                done(null, reason);
                yield break;
            }
            if (IsBusy)
            {
                const string busy = "The DM is busy with a turn. Try again when it finishes.";
                LastFailure = LlmFailure.Other(busy, FailureContext(Byok.ChatUrl(), 0, string.Empty));
                done(null, busy);
                yield break;
            }
            LastFailure = null;
            IsBusy = true;
            _cancelRequested = false;
            _retryBusy = retryBusy;
            try
            {
                var messages = JsonValue.NewArray();
                foreach (var m in roleMessages) { messages.ArrayValue.Add(RoleMessage(m.Key, m.Value)); }
                string model = Byok.Model.Trim();
                JsonValue response = null;
                string failure = null;
                yield return RequestReply(
                    delegate (bool stream, bool usageOptions)
                    {
                        var request = NewRequest(model, stream, usageOptions);
                        request.ObjectValue["messages"] = messages;
                        return request;
                    },
                    onDelta ?? delegate { },
                    delegate (JsonValue r, string err) { response = r; failure = err; });
                if (failure != null) { done(null, failure); yield break; }
                RecordUsage(MeasureUsage(response, model), model);
                var choices = response.GetArray("choices");
                JsonValue message = choices.Count > 0 ? choices[0].Get("message") : JsonValue.Null;
                string content = string.Empty;
                string reasoning;
                if (!message.IsNull) { ExtractContent(message, out content, out reasoning); }
                content = content.Trim();
                if (content.Length == 0)
                {
                    LastFailure = LlmFailure.Malformed("an empty reply", FailureContext(Byok.ChatUrl(), 1, CapText(response.ToJson(), 1500)));
                    done(null, model + " returned an empty reply.");
                    yield break;
                }
                done(content, null);
            }
            finally { IsBusy = false; _retryBusy = true; }
        }

        /// <summary>Quiet retries of a busy provider; off for a throwaway helper call (a level hint) that shouldn't hold the DM up.</summary>
        private bool _retryBusy = true;

        internal JsonValue BuildNarrationMessages(string playerText, TurnBrief brief)
        {
            string skill;
            if (!Prompts.TryLoadSkill("dnd-narration", out skill)) { skill = string.Empty; }
            var messages = JsonValue.NewArray();
            messages.ArrayValue.Add(RoleMessage("system", Storyteller.NarrationSystemPrompt(skill, PcCard, Prompts.CampaignLine())));
            foreach (var passage in _passages)
            {
                messages.ArrayValue.Add(RoleMessage("user", "The player says:\n" + passage.Key));
                messages.ArrayValue.Add(RoleMessage("assistant", passage.Value));
            }
            messages.ArrayValue.Add(RoleMessage("user", brief.Build(playerText)));
            return messages;
        }

        private JsonValue BuildNarrationRequest(JsonValue messages, string model, bool stream, bool usageOptions)
        {
            var request = NewRequest(model, stream, usageOptions);
            request.ObjectValue["messages"] = messages;
            return request;
        }

        private void AddStopped(VaultTranscript transcript, Action changed)
        {
            AddSystem(transcript, "Stopped — the table keeps whatever the DM already committed.");
            changed();
        }

        /// <summary>Counts one priced call toward the session and, when a campaign is open, toward its persisted total.</summary>
        private void RecordUsage(TokenUsage usage, string model)
        {
            SessionUsage.Add(usage);
            if (CampaignUsage != null && Prompts != null) { CampaignUsage.Add(Prompts.CampaignSlug, model, usage); }
        }

        /// <summary>One call's reported tokens, priced for the active profile.</summary>
        private TokenUsage MeasureUsage(JsonValue response, string model)
        {
            TokenUsage usage = TokenUsage.FromJson(response.Get("usage"));
            usage.ApplyPrice(ModelPricing.Bundled, Byok.Active, model);
            return usage;
        }

        public const int MaxTransientRetries = 2;

        /// <summary>No answer, a dropped stream (a 200 that failed), a timeout, a rate limit or a server error: a retry can work.</summary>
        internal static bool IsTransient(long status)
        {
            return status == 0 || status == 200 || status == 408 || status == 425 || status == 429 || (status >= 500 && status <= 599);
        }

        /// <summary>What a failure report needs to know about the call that just ran (the key is passed only so it can be scrubbed).</summary>
        private LlmFailure.Context FailureContext(string endpoint, int attempts, string body)
        {
            return new LlmFailure.Context
            {
                Endpoint = endpoint,
                Model = Byok.Model.Trim(),
                RequestMeta = LastRequestMeta,
                Attempts = Math.Max(1, attempts),
                ApiKey = Byok.HasKey ? Byok.ApiKey() : string.Empty,
                Body = CapText(body, 1500),
            };
        }

        /// <summary>The provider's raw reply to the last PostChat, kept only to describe a failure.</summary>
        private string _lastBody = string.Empty;

        /// <summary>
        /// One model call. Streams when the endpoint allows it; an endpoint that
        /// rejects stream:true (HTTP 400/422) is retried once without it and
        /// remembered, so it's never asked to stream again this run.
        /// </summary>
        private IEnumerator RequestReply(Func<bool, bool, JsonValue> build, Action<string> onDelta, Action<JsonValue, string> done)
        {
            string endpoint = Byok.ChatUrl();
            bool stream = !Byok.Active.DisableStreaming && !_noStreamEndpoints.Contains(endpoint);
            bool usageOptions = stream && !_noStreamOptions.Contains(endpoint);
            JsonValue request = build(stream, usageOptions);
            LastRequestMeta = DescribeRequest(request);
            JsonValue response = null;
            string failure = null;
            long status = 0;
            bool streamedText = false;
            Action<string> trackDelta = delegate (string delta) { streamedText = true; onDelta(delta); };
            int attempts = 0;
            Action<JsonValue, string, long> keep = delegate (JsonValue r, string err, long code) { response = r; failure = err; status = code; attempts++; };
            yield return PostChat(request, stream, trackDelta, keep);
            if (usageOptions && failure != null && !streamedText && !_cancelRequested && (status == 400 || status == 422))
            {
                // Some endpoints stream fine but reject stream_options: drop it before giving up on streaming.
                request = build(true, false);
                LastRequestMeta = DescribeRequest(request) + " · no stream_options";
                yield return PostChat(request, true, trackDelta, keep);
                if (failure == null) { _noStreamOptions.Add(endpoint); }
            }
            if (stream && failure != null && !streamedText && !_cancelRequested && (status == 400 || status == 422))
            {
                request = build(false, false);
                LastRequestMeta = DescribeRequest(request) + " · stream retry";
                yield return PostChat(request, false, onDelta, keep);
                if (failure == null) { _noStreamEndpoints.Add(endpoint); }
            }
            // A busy provider (free models especially) drops or refuses requests that work a moment later: try again, quietly.
            for (int attempt = 1; attempt <= MaxTransientRetries && _retryBusy && failure != null && !streamedText && !_cancelRequested && IsTransient(status); attempt++)
            {
                Status = "the provider is busy: trying again (" + attempt + "/" + MaxTransientRetries + ")…";
                float until = Time.realtimeSinceStartup + 2f * attempt;
                while (Time.realtimeSinceStartup < until && !_cancelRequested) { yield return null; }
                if (_cancelRequested) { break; }
                request = build(stream, usageOptions && !_noStreamOptions.Contains(endpoint));
                yield return PostChat(request, stream, trackDelta, keep);
            }
            if (failure == null)
            {
                ProviderProblem = string.Empty;
                LastFailure = null;
            }
            else
            {
                LlmFailure.Context context = FailureContext(endpoint, attempts, _lastBody);
                if (_cancelRequested) { LastFailure = LlmFailure.Cancelled(context); }
                else
                {
                    LastFailure = LlmFailure.From(status, failure, context);
                    // Callers still get one string: the friendly wording for the quiet-retry kinds, the provider's own for the rest.
                    if (_retryBusy && IsTransient(status)) { failure = LastFailure.Friendly; }
                    string problem = ProviderProblemFor(status, Byok.Model.Trim());
                    if (problem.Length > 0) { ProviderProblem = problem; }
                }
            }
            done(response, failure);
        }

        /// <summary>
        /// Reads the reply text tolerantly: most providers send a plain string,
        /// but some gateways send content blocks ([{type:text, text:…}]) and
        /// reasoning models park visible text in reasoning_content, leaving
        /// content null. All three used to render as total silence.
        /// </summary>
        internal static void ExtractContent(JsonValue message, out string content, out string reasoning)
        {
            content = message.GetString("content", null);
            if (content == null)
            {
                var sb = new StringBuilder();
                foreach (var block in message.GetArray("content"))
                {
                    string text = block.GetString("text", string.Empty);
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (sb.Length > 0) { sb.Append('\n'); }
                        sb.Append(text);
                    }
                }
                content = sb.ToString();
            }
            reasoning = message.GetString("reasoning_content",
                message.GetString("reasoning", string.Empty));
            if (content == null) { content = string.Empty; }
            if (reasoning == null) { reasoning = string.Empty; }
        }

        private static string CapText(string text, int max)
        {
            if (text != null && text.Length > max)
            {
                return text.Substring(0, max) + "\n… (" + text.Length + " chars total, capped for display)";
            }
            return text ?? string.Empty;
        }

        private string DescribeRequest(JsonValue request)
        {
            int messages = request.Get("messages").Kind == JsonKind.Array
                ? request.GetArray("messages").Count
                : 0;
            int tools = request.Get("tools").Kind == JsonKind.Array
                ? request.GetArray("tools").Count
                : 0;
            string meta = request.GetString("model", Byok.Model) + " · " + messages + " msgs · " + request.ToJson().Length + " chars · " + tools + " tools";
            if (request.Get("temperature").Kind == JsonKind.Number) { meta += " · T" + request.GetNumber("temperature", 1); }
            if (request.Get("max_tokens").Kind == JsonKind.Number) { meta += " · ≤" + (int)request.GetNumber("max_tokens", 0) + " tok"; }
            if (!request.Get("reasoning").IsNull) { meta += " · effort " + request.Get("reasoning").GetString("effort", "?"); }
            if (request.GetBool("stream", false)) { meta += " · streaming"; }
            return meta;
        }

        /// <summary>Only OpenRouter is known to accept the reasoning block; strict endpoints 400 it.</summary>
        private static bool IsOpenRouter(string baseUrl)
        {
            return (baseUrl ?? string.Empty).ToLowerInvariant().Contains("openrouter.ai");
        }

        private void LogTool(string line)
        {
            ToolLog.Add(line);
            while (ToolLog.Count > 30) { ToolLog.RemoveAt(0); }
        }

        /// <summary>Model, streaming and sampling knobs shared by the loop and the storyteller.</summary>
        private JsonValue NewRequest(string model, bool stream, bool usageOptions)
        {
            var request = JsonValue.NewObject();
            request.ObjectValue["model"] = JsonValue.FromString(model);
            if (stream) { request.ObjectValue["stream"] = JsonValue.FromBool(true); }
            if (stream && usageOptions)
            {
                // Streamed replies only report token usage when asked.
                var options = JsonValue.NewObject();
                options.ObjectValue["include_usage"] = JsonValue.FromBool(true);
                request.ObjectValue["stream_options"] = options;
            }
            // Sampling knobs ride only when customized: strict endpoints
            // (o1-style models, plain OpenAI) reject a temperature they didn't
            // ask for, and max_tokens has no safe universal default.
            float temp = Mathf.Clamp(Byok.Active.Temperature, 0f, 2f);
            if (Mathf.Abs(temp - 1f) > 0.0001f) { request.ObjectValue["temperature"] = JsonValue.FromNumber(temp); }
            if (Byok.Active.MaxTokens > 0) { request.ObjectValue["max_tokens"] = JsonValue.FromNumber(Byok.Active.MaxTokens); }
            if (!string.IsNullOrEmpty(Byok.Active.ReasoningEffort) && IsOpenRouter(Byok.BaseUrl))
            {
                var reasoning = JsonValue.NewObject();
                reasoning.ObjectValue["effort"] = JsonValue.FromString(Byok.Active.ReasoningEffort);
                request.ObjectValue["reasoning"] = reasoning;
            }
            return request;
        }

        private JsonValue BuildRequest(string systemPrompt, bool stream, bool usageOptions)
        {
            var request = NewRequest(Byok.Model.Trim(), stream, usageOptions);
            var messages = JsonValue.NewArray();
            messages.ArrayValue.Add(RoleMessage("system", systemPrompt));
            for (int i = 0; i < _history.Count; i++) { messages.ArrayValue.Add(_history[i]); }
            request.ObjectValue["messages"] = messages;

            var tools = JsonValue.NewArray();
            for (int i = 0; i < _toolCache.Count; i++)
            {
                var t = _toolCache[i];
                if (AllowedTools.Count > 0 && !AllowedTools.Contains(t.Name)) { continue; }
                tools.ArrayValue.Add(FunctionTool(t.Name,
                    string.IsNullOrEmpty(t.Description) ? t.Name : t.Description,
                    t.InputSchema ?? JsonValue.NewObject()));
            }
            var skillNames = Prompts.SkillNames();
            if (skillNames.Count > 0) { tools.ArrayValue.Add(LoadSkillDefinition(skillNames)); }
            if (tools.ArrayValue.Count > 0) { request.ObjectValue["tools"] = tools; }
            return request;
        }

        private static JsonValue LoadSkillDefinition(List<string> skillNames)
        {
            var names = JsonValue.NewArray();
            foreach (string n in skillNames) { names.ArrayValue.Add(JsonValue.FromString(n)); }
            var nameProp = JsonValue.NewObject();
            nameProp.ObjectValue["type"] = JsonValue.FromString("string");
            nameProp.ObjectValue["enum"] = names;
            var props = JsonValue.NewObject();
            props.ObjectValue["name"] = nameProp;
            var required = JsonValue.NewArray();
            required.ArrayValue.Add(JsonValue.FromString("name"));
            var schema = JsonValue.NewObject();
            schema.ObjectValue["type"] = JsonValue.FromString("object");
            schema.ObjectValue["properties"] = props;
            schema.ObjectValue["required"] = required;
            return FunctionTool(SystemPromptProvider.LoadSkillTool,
                "Read a DM skill's full text (see the SKILLS index in the system prompt). Local, free of game side effects.",
                schema);
        }

        private static JsonValue FunctionTool(string name, string description, JsonValue parameters)
        {
            var def = JsonValue.NewObject();
            def.ObjectValue["type"] = JsonValue.FromString("function");
            var fn = JsonValue.NewObject();
            fn.ObjectValue["name"] = JsonValue.FromString(name);
            fn.ObjectValue["description"] = JsonValue.FromString(description);
            fn.ObjectValue["parameters"] = parameters;
            def.ObjectValue["function"] = fn;
            return def;
        }

        /// <summary>
        /// Shrinks every message before the new turn: tool results become a
        /// one-line summary, skill bodies a reload hint, and bulky tool-call
        /// arguments an elision marker. Call ids stay intact so the protocol
        /// pairing (assistant tool_calls -> tool results) remains valid.
        /// </summary>
        private void CompactFinishedTurns()
        {
            for (int i = 0; i < _history.Count; i++)
            {
                var message = _history[i];
                string role = message.GetString("role", string.Empty);
                if (role == "user")
                {
                    // Only the newest loop message carries the last scene.
                    string content = message.GetString("content", string.Empty);
                    string stripped = Storyteller.StripScene(content);
                    if (!ReferenceEquals(stripped, content)) { message.ObjectValue["content"] = JsonValue.FromString(stripped); }
                }
                else if (role == "tool")
                {
                    string callId = message.GetString("tool_call_id", string.Empty);
                    string name;
                    _callNames.TryGetValue(callId, out name);
                    message.ObjectValue["content"] = JsonValue.FromString(
                        CompactResult(name ?? string.Empty, message.GetString("content", string.Empty)));
                }
                else if (role == "assistant")
                {
                    // Reasoning replay only matters inside the turn that made it.
                    message.ObjectValue.Remove("reasoning_details");
                    foreach (var call in message.GetArray("tool_calls"))
                    {
                        var fn = call.Get("function");
                        string arguments = fn.GetString("arguments", "{}");
                        if (arguments.Length <= CompactedArgumentChars) { continue; }
                        var marker = JsonValue.NewObject();
                        marker.ObjectValue["elided"] = JsonValue.FromString(arguments.Length + " chars, already sent and applied");
                        fn.ObjectValue["arguments"] = JsonValue.FromString(marker.ToJson());
                    }
                }
            }
            _turnStart = _history.Count;
        }

        internal static string CompactResult(string toolName, string content)
        {
            if (content.StartsWith("[earlier", StringComparison.Ordinal)) { return content; }
            if (toolName == SystemPromptProvider.LoadSkillTool)
            {
                return "[earlier skill load, dropped to save context; call load_skill again if you need it]";
            }
            string summary = content;
            JsonValue parsed;
            if (JsonValue.TryParse(content, out parsed) && parsed.Kind == JsonKind.Object)
            {
                string envelopeSummary = parsed.GetString("summary", string.Empty);
                if (!parsed.GetBool("success", true)) { envelopeSummary = "FAILED: " + envelopeSummary; }
                if (envelopeSummary.Length > 0) { summary = envelopeSummary; }
            }
            if (summary.Length > CompactedResultChars) { summary = summary.Substring(0, CompactedResultChars) + "…"; }
            return "[earlier " + toolName + " result] " + summary;
        }

        /// <summary>
        /// Drops the oldest whole turns (a user message up to the next user
        /// message) while history is over budget, never touching the current
        /// turn, so a tool result is never replayed without the tool_calls
        /// that produced it.
        /// </summary>
        private void TrimToBudget()
        {
            while (HistoryChars() > MaxHistoryChars)
            {
                int nextUser = -1;
                // _turnStart is the current user message: the furthest a cut may reach.
                for (int i = 1; i <= _turnStart && i < _history.Count; i++)
                {
                    if (_history[i].GetString("role", string.Empty) == "user") { nextUser = i; break; }
                }
                if (nextUser < 0) { break; }
                _history.RemoveRange(0, nextUser);
                _turnStart -= nextUser;
            }
            PruneCallNames();
        }

        /// <summary>
        /// Drops call-id labels for tool calls no longer in history, so the
        /// map can't grow without bound over a long campaign.
        /// </summary>
        private void PruneCallNames()
        {
            if (_callNames.Count == 0) { return; }
            var live = new HashSet<string>();
            foreach (var message in _history)
            {
                if (message.GetString("role", string.Empty) != "assistant") { continue; }
                foreach (var call in message.GetArray("tool_calls"))
                {
                    string id = call.GetString("id", string.Empty);
                    if (!string.IsNullOrEmpty(id)) { live.Add(id); }
                }
            }
            var dead = new List<string>();
            foreach (string id in _callNames.Keys)
            {
                if (!live.Contains(id)) { dead.Add(id); }
            }
            foreach (string id in dead) { _callNames.Remove(id); }
        }

        private int HistoryChars()
        {
            int total = 0;
            foreach (var message in _history) { total += message.ToJson().Length; }
            return total;
        }

        private IEnumerator PostChat(JsonValue request, bool stream, Action<string> onDelta, Action<JsonValue, string, long> done)
        {
            byte[] payload = Encoding.UTF8.GetBytes(request.ToJson());
            _lastBody = string.Empty;
            using (UnityWebRequest web = new UnityWebRequest(Byok.ChatUrl(), "POST"))
            {
                ChatStreamAccumulator accumulator = null;
                SseDownloadHandler sse = null;
                if (stream)
                {
                    accumulator = new ChatStreamAccumulator();
                    var decoder = new SseDecoder();
                    decoder.OnEvent = delegate (string data)
                    {
                        string delta = accumulator.Feed(data);
                        if (delta.Length > 0) { onDelta(delta); }
                    };
                    sse = new SseDownloadHandler(decoder);
                    web.downloadHandler = sse;
                }
                else
                {
                    web.downloadHandler = new DownloadHandlerBuffer();
                }
                web.uploadHandler = new UploadHandlerRaw(payload);
                web.SetRequestHeader("Content-Type", "application/json");
                if (stream) { web.SetRequestHeader("Accept", "text/event-stream"); }
                if (Byok.HasKey) { web.SetRequestHeader("Authorization", "Bearer " + Byok.ApiKey()); }
                // Model turns (long narration, reasoning models) outlast tool calls.
                web.timeout = Math.Max(120, Vault != null ? Vault.TimeoutSeconds * 2 : 120);
                _activeChat = web;
                yield return web.SendWebRequest();
                _activeChat = null;

                string body = sse != null ? sse.RawText : (web.downloadHandler.text ?? string.Empty);
                _lastBody = body;
                if (web.result != UnityWebRequest.Result.Success)
                {
                    string detail = string.Empty;
                    JsonValue errorBody;
                    if (JsonValue.TryParse(body, out errorBody))
                    {
                        detail = errorBody.Get("error").GetString("message", string.Empty);
                    }
                    string what = web.responseCode == 0
                        ? (web.error ?? "no response") + " contacting the chat endpoint"
                        : "HTTP " + web.responseCode + " from chat endpoint";
                    done(null, what + (detail.Length > 0 ? ": " + TextSanitizer.Clean(detail, 300) : "."), web.responseCode);
                    yield break;
                }
                if (stream)
                {
                    if (accumulator.Error != null)
                    {
                        done(null, TextSanitizer.Clean(accumulator.Error, 300), web.responseCode);
                        yield break;
                    }
                    if (accumulator.Chunks == 0)
                    {
                        // A 200 that wasn't SSE: some gateways ignore stream:true.
                        JsonValue plain;
                        if (JsonValue.TryParse(body, out plain) && plain.Kind == JsonKind.Object)
                        {
                            done(plain, null, web.responseCode);
                            yield break;
                        }
                        done(null, "Chat endpoint returned an empty stream.", web.responseCode);
                        yield break;
                    }
                    done(accumulator.ToResponse(), null, web.responseCode);
                    yield break;
                }
                JsonValue response;
                if (!JsonValue.TryParse(body, out response))
                {
                    done(null, "Chat endpoint returned invalid JSON.", web.responseCode);
                    yield break;
                }
                if (!response.Get("error").IsNull)
                {
                    done(null, TextSanitizer.Clean(response.Get("error").GetString("message", "chat error"), 300), web.responseCode);
                    yield break;
                }
                done(response, null, web.responseCode);
            }
        }

        private static JsonValue RoleMessage(string role, string text)
        {
            var m = JsonValue.NewObject();
            m.ObjectValue["role"] = JsonValue.FromString(role);
            m.ObjectValue["content"] = JsonValue.FromString(text);
            return m;
        }

        private static JsonValue ToolMessage(string callId, string text)
        {
            var m = JsonValue.NewObject();
            m.ObjectValue["role"] = JsonValue.FromString("tool");
            m.ObjectValue["tool_call_id"] = JsonValue.FromString(callId);
            string clipped = text.Length > McpClient.MaxResultChars
                ? text.Substring(0, McpClient.MaxResultChars) + "\n… (result truncated)"
                : text;
            m.ObjectValue["content"] = JsonValue.FromString(clipped);
            return m;
        }

        /// <summary>
        /// Keep assistant messages replayable without protocol leftovers. The
        /// content key is always present (possibly empty): a bare
        /// {"role":"assistant"} with neither content nor tool_calls is rejected
        /// by some OpenAI-compatible endpoints on replay. Content comes from
        /// ExtractContent, so block-array replies aren't flattened to "".
        /// reasoning_details and per-call extra_content ride along because
        /// some providers (Gemini thought signatures via OpenRouter or the
        /// OpenAI-compat endpoint) reject a tool loop replayed without them.
        /// </summary>
        internal static JsonValue PruneMessage(JsonValue message, string content)
        {
            var m = JsonValue.NewObject();
            m.ObjectValue["role"] = JsonValue.FromString("assistant");
            m.ObjectValue["content"] = JsonValue.FromString(content ?? string.Empty);
            var details = message.Get("reasoning_details");
            if (details.Kind == JsonKind.Array && details.ArrayValue.Count > 0) { m.ObjectValue["reasoning_details"] = details; }
            var calls = message.GetArray("tool_calls");
            if (calls.Count > 0)
            {
                var kept = JsonValue.NewArray();
                for (int i = 0; i < calls.Count; i++)
                {
                    var c = JsonValue.NewObject();
                    c.ObjectValue["id"] = JsonValue.FromString(calls[i].GetString("id", "call-" + i));
                    c.ObjectValue["type"] = JsonValue.FromString("function");
                    var fn = JsonValue.NewObject();
                    fn.ObjectValue["name"] = JsonValue.FromString(calls[i].Get("function").GetString("name", string.Empty));
                    fn.ObjectValue["arguments"] = JsonValue.FromString(calls[i].Get("function").GetString("arguments", "{}"));
                    c.ObjectValue["function"] = fn;
                    var extra = calls[i].Get("extra_content");
                    if (!extra.IsNull) { c.ObjectValue["extra_content"] = extra; }
                    kept.ArrayValue.Add(c);
                }
                m.ObjectValue["tool_calls"] = kept;
            }
            return m;
        }

        private static void AddSystem(VaultTranscript transcript, string text)
        {
            transcript.Add(new TranscriptSegment { Kind = SegmentKind.System, Text = text });
        }

        /// <summary>
        /// A visible failure: persisted for the status line and rendered in
        /// red (the UI colors ⚠ system lines as errors), so problems can't
        /// slide by as faint footnotes.
        /// </summary>
        private void Fail(VaultTranscript transcript, string text, Action changed, LlmFailure failure = null)
        {
            LastError = text;
            // A transport failure was already described by RequestReply; anything else gets a generic record here.
            if (failure != null) { LastFailure = failure; }
            else if (LastFailure == null) { LastFailure = LlmFailure.Other(text, FailureContext(Byok.ChatUrl(), 1, string.Empty)); }
            transcript.Add(new TranscriptSegment { Kind = SegmentKind.System, Text = "⚠ " + text, Failure = LastFailure });
            changed();
        }
    }
}
