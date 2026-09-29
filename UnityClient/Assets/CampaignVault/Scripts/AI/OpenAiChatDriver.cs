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

        private readonly List<JsonValue> _history = new List<JsonValue>();
        // tool_call_id -> tool name, so compaction knows what a tool message was.
        private readonly Dictionary<string, string> _callNames = new Dictionary<string, string>();
        // Index in _history where the current (uncompacted) turn starts.
        private int _turnStart;

        private List<McpToolInfo> _toolCache = new List<McpToolInfo>();
        private string _toolCacheKey = string.Empty;

        /// <summary>Forget the conversation (campaign switched): the next message starts clean.</summary>
        public void ResetConversation()
        {
            _history.Clear();
            _callNames.Clear();
            _turnStart = 0;
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
            try
            {
                string reason;
                if (!Byok.Validate(out reason))
                {
                    AddSystem(transcript, "Chat blocked: " + reason);
                    yield break;
                }
                if (_toolCacheKey != Vault.ConnectorPath(Vault.ActiveConnector()))
                {
                    McpOutcome<int> refresh = null;
                    yield return RefreshTools(delegate (McpOutcome<int> o) { refresh = o; });
                    if (refresh == null || !refresh.Ok)
                    {
                        AddSystem(transcript, "Chat blocked: cannot reach CampaignVault tools (" + (refresh != null ? refresh.ErrorMessage : "?") + ").");
                        yield break;
                    }
                }

                CompactFinishedTurns();
                _history.Add(RoleMessage("user", TextSanitizer.Clean(playerText, 4000)));
                TrimToBudget();
                string systemPrompt = Prompts.BuildSystemPrompt();

                for (int turn = 0; turn < MaxToolIterations; turn++)
                {
                    JsonValue request = BuildRequest(systemPrompt);
                    JsonValue response = null;
                    string failure = null;
                    yield return PostChat(request, delegate (JsonValue r, string err) { response = r; failure = err; });
                    if (failure != null)
                    {
                        AddSystem(transcript, "Chat error: " + failure);
                        break;
                    }
                    JsonValue firstChoice = response.GetArray("choices").Count > 0
                        ? response.GetArray("choices")[0]
                        : JsonValue.Null;
                    JsonValue message = firstChoice.IsNull ? JsonValue.Null : firstChoice.Get("message");
                    if (message.IsNull)
                    {
                        AddSystem(transcript, "Chat error: model returned no message.");
                        break;
                    }

                    string content = message.GetString("content", string.Empty);
                    var toolCalls = message.GetArray("tool_calls");
                    _history.Add(PruneMessage(message));

                    if (!string.IsNullOrEmpty(content))
                    {
                        SegmentSplitter.AddNarration(transcript, content);
                        changed();
                    }
                    if (toolCalls.Count == 0) { break; }

                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        string callId = toolCalls[i].GetString("id", "call-" + i);
                        string toolName = toolCalls[i].Get("function").GetString("name", string.Empty);
                        string argText = toolCalls[i].Get("function").GetString("arguments", "{}");
                        JsonValue args;
                        if (!JsonValue.TryParse(argText, out args)) { args = JsonValue.NewObject(); }
                        _callNames[callId] = toolName;

                        string resultText;
                        if (toolName == SystemPromptProvider.LoadSkillTool)
                        {
                            string skill = args.GetString("name", string.Empty);
                            transcript.Add(new TranscriptSegment { Kind = SegmentKind.ToolData, Text = "✦ the DM consults " + skill });
                            changed();
                            string body;
                            resultText = Prompts.TryLoadSkill(skill, out body)
                                ? body
                                : "No skill named '" + skill + "'. Available: " + string.Join(", ", Prompts.SkillNames().ToArray());
                        }
                        else
                        {
                            transcript.Add(new TranscriptSegment { Kind = SegmentKind.ToolData, Text = "⚙ " + toolName });
                            changed();
                            McpOutcome<string> call = null;
                            yield return Mcp.CallTool(Vault, Vault.ActiveConnector(), toolName, args,
                                delegate (McpOutcome<string> o) { call = o; });
                            resultText = call != null && call.Ok
                                ? call.Data
                                : "error [" + (call != null ? call.ErrorCode : "MCP") + "] " + (call != null ? call.ErrorMessage : "?");
                            foreach (var roll in SegmentSplitter.ExtractRolls(resultText)) { transcript.Add(roll); }
                            changed();
                        }
                        _history.Add(ToolMessage(callId, resultText));
                    }
                }
            }
            finally
            {
                IsBusy = false;
                changed();
            }
        }

        private JsonValue BuildRequest(string systemPrompt)
        {
            var request = JsonValue.NewObject();
            request.ObjectValue["model"] = JsonValue.FromString(Byok.Model.Trim());
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
                if (role == "tool")
                {
                    string callId = message.GetString("tool_call_id", string.Empty);
                    string name;
                    _callNames.TryGetValue(callId, out name);
                    message.ObjectValue["content"] = JsonValue.FromString(
                        CompactResult(name ?? string.Empty, message.GetString("content", string.Empty)));
                }
                else if (role == "assistant")
                {
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
        }

        private int HistoryChars()
        {
            int total = 0;
            foreach (var message in _history) { total += message.ToJson().Length; }
            return total;
        }

        private IEnumerator PostChat(JsonValue request, Action<JsonValue, string> done)
        {
            byte[] payload = Encoding.UTF8.GetBytes(request.ToJson());
            using (UnityWebRequest web = new UnityWebRequest(Byok.ChatUrl(), "POST"))
            {
                web.uploadHandler = new UploadHandlerRaw(payload);
                web.downloadHandler = new DownloadHandlerBuffer();
                web.SetRequestHeader("Content-Type", "application/json");
                web.SetRequestHeader("Authorization", "Bearer " + Byok.ApiKey());
                // Model turns (long narration, reasoning models) outlast tool calls.
                web.timeout = Math.Max(120, Vault != null ? Vault.TimeoutSeconds * 2 : 120);
                yield return web.SendWebRequest();
                if (web.result != UnityWebRequest.Result.Success)
                {
                    string detail = string.Empty;
                    JsonValue errorBody;
                    if (JsonValue.TryParse(web.downloadHandler.text ?? string.Empty, out errorBody))
                    {
                        detail = errorBody.Get("error").GetString("message", string.Empty);
                    }
                    done(null, "HTTP " + web.responseCode + " from chat endpoint"
                        + (detail.Length > 0 ? ": " + TextSanitizer.Clean(detail, 300) : "."));
                    yield break;
                }
                JsonValue response;
                if (!JsonValue.TryParse(web.downloadHandler.text ?? string.Empty, out response))
                {
                    done(null, "Chat endpoint returned invalid JSON.");
                    yield break;
                }
                if (!response.Get("error").IsNull)
                {
                    done(null, TextSanitizer.Clean(response.Get("error").GetString("message", "chat error"), 300));
                    yield break;
                }
                done(response, null);
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

        /// <summary>Keep assistant messages replayable without protocol leftovers.</summary>
        private static JsonValue PruneMessage(JsonValue message)
        {
            var m = JsonValue.NewObject();
            m.ObjectValue["role"] = JsonValue.FromString("assistant");
            string content = message.GetString("content", string.Empty);
            if (!string.IsNullOrEmpty(content)) { m.ObjectValue["content"] = JsonValue.FromString(content); }
            var calls = message.GetArray("tool_calls");
            if (calls.Count > 0)
            {
                var kept = JsonValue.NewArray();
                for (int i = 0; i < calls.Count; i++)
                {
                    var c = JsonValue.NewObject();
                    c.ObjectValue["id"] = JsonValue.FromString(calls[i].GetString("id", string.Empty));
                    c.ObjectValue["type"] = JsonValue.FromString("function");
                    var fn = JsonValue.NewObject();
                    fn.ObjectValue["name"] = JsonValue.FromString(calls[i].Get("function").GetString("name", string.Empty));
                    fn.ObjectValue["arguments"] = JsonValue.FromString(calls[i].Get("function").GetString("arguments", "{}"));
                    c.ObjectValue["function"] = fn;
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
    }
}
