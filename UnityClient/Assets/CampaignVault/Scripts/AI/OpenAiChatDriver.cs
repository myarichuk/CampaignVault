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
    public partial class OpenAiChatDriver : MonoBehaviour
    {
        public const int MaxToolIterations = 8;
        public const int MaxHistoryChars = 48000;
        public const int CompactedArgumentChars = 400;
        public const int CompactedResultChars = 280;

        /// <summary>
        /// Replaces the single-pass client note (null keeps it). The measurer sets it to empty to play the prompt as a web connector
        /// sees it: no "cards" note, so the model writes its roll block above the scene.
        /// </summary>
        public string SinglePassNoteOverride;
        /// <summary>
        /// The storyteller's length target, pinned exemplar and word-floor filter (NARRATION_LENGTH_PLAN.md, L5). On by default;
        /// the debug measurer turns it off to record a baseline.
        /// </summary>
        public bool NarrationRemedies = true;
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
        /// <summary>A tool call that returned success: its name, the arguments the model sent, and the raw result text.</summary>
        public event Action<string, JsonValue, string> ToolSucceeded;

        /// <summary>Only the engine's own commits carry roll lines; a lookup or an entity read never does, so no roll card is cut from them (A6).</summary>
        internal static bool CarriesRolls(string toolName)
        {
            return toolName == "take_turn" || toolName == "combat";
        }

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
            // Only this turn's calls: a table refresh or a codex search in flight is not the player's to stop (A13).
            if (Mcp != null) { Mcp.Abort(McpOwner); }
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
                // Trimming is silent to the model, so the prompt's "after a compaction send forceFullReseed" never fired (A20).
                string trimNote = _historyTrimmed ? "\n\n" + TrimmedNote : string.Empty;
                _historyTrimmed = false;
                userMessage = RoleMessage("user", (twoPass ? Storyteller.LoopUserMessage(LastPassage, cleanPlayer) : cleanPlayer) + trimNote);
                _history.Add(userMessage);
                TrimToBudget();
                _loopTurn = twoPass;
                string systemPrompt = twoPass ? Prompts.BuildSystemPrompt(Storyteller.LoopContract, true) : Prompts.BuildSystemPrompt(SinglePassNoteOverride ?? Storyteller.SinglePassNote);

                for (int turn = 0; turn < MaxToolIterations; turn++)
                {
                    if (_cancelRequested) { AddStopped(transcript, changed); break; }
                    Status = "the DM is thinking…";
                    CurrentTool = string.Empty;
                    // Tool results pile up inside one turn; older turns go first so the request stays under budget (A9).
                    TrimToBudget();

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
                    // A reply cut off at the output limit: its tool arguments are incomplete and its prose ends mid-sentence (A5).
                    bool cutOff = firstChoice.GetString("finish_reason", string.Empty) == "length";
                    if (cutOff && toolCalls.Count == 0)
                    {
                        AddSystem(transcript, "⚠ The reply was cut off at the model's output limit (finish_reason=length). Raise the output token limit in Settings if this repeats.");
                    }
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
                        if (!JsonValue.TryParse(argText, out args) || args.Kind != JsonKind.Object)
                        {
                            // Never sent to the server as "{}": the model gets a parse error it can resend from (A5).
                            LogTool(toolName + " BAD ARGS · " + argText.Length + " chars" + (cutOff ? " (cut off)" : string.Empty));
                            _history.Add(ToolMessage(callId, "error [BadArguments] the arguments for " + toolName + " were not a complete JSON object ("
                                + argText.Length + " chars" + (cutOff ? ", the reply was cut off at the output limit" : string.Empty)
                                + "). Nothing was sent to the server; resend the call with complete JSON."));
                            continue;
                        }
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
                                record.Succeeded.Add(toolName);
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
                                delegate (McpOutcome<string> o) { call = o; }, McpOwner);
                            if (call != null && call.Ok)
                            {
                                resultText = call.Data;
                                record.Succeeded.Add(toolName);
                                LogTool(toolName + " ok · " + (call.Data != null ? call.Data.Length : 0) + " chars");
                                if (CarriesRolls(toolName))
                                {
                                    foreach (var roll in SegmentSplitter.ExtractRolls(resultText))
                                    {
                                        transcript.Add(roll);
                                        record.Rolls.Add(roll.Roll.Label + " " + roll.Roll.Detail + ": " + roll.Roll.Verdict);
                                    }
                                }
                                brief.Add(toolName, args, resultText, true);
                                if (ToolSucceeded != null)
                                {
                                    try { ToolSucceeded(toolName, args, resultText); }
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

        internal JsonValue BuildNarrationMessages(string playerText, TurnBrief brief)
        {
            string skill;
            string examples;
            if (!Prompts.TryLoadSkill("dnd-narration", out skill)) { skill = string.Empty; }
            if (!Prompts.TryLoadSkill("dnd-narration-examples", out examples)) { examples = string.Empty; }
            var messages = JsonValue.NewArray();
            messages.ArrayValue.Add(RoleMessage("system", Storyteller.NarrationSystemPrompt(skill, examples, PcCard, Prompts.CampaignLine())));
            if (NarrationRemedies)
            {
                messages.ArrayValue.Add(RoleMessage("user", "The player says:\n" + Storyteller.ExemplarPlayer));
                messages.ArrayValue.Add(RoleMessage("assistant", Storyteller.ExemplarScene));
            }
            foreach (var passage in _passages)
            {
                // A thin scene replayed as an assistant turn becomes the style of the next one.
                if (NarrationRemedies && Storyteller.WordCount(passage.Value) < Storyteller.MinReplayWords) { continue; }
                messages.ArrayValue.Add(RoleMessage("user", "The player says:\n" + passage.Key));
                messages.ArrayValue.Add(RoleMessage("assistant", passage.Value));
            }
            messages.ArrayValue.Add(RoleMessage("user", brief.Build(playerText, Storyteller.MaxBriefChars, NarrationRemedies)));
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

        /// <summary>The current turn is the two-pass loop: no narration sections in its prompt, no narration skills in its menu (A24).</summary>
        private bool _loopTurn;

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
