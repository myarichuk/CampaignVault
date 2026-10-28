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
    /// <summary>The wire: building a chat request, posting it (streamed or not), transient retries, usage, the failure context for error cards.</summary>
    public partial class OpenAiChatDriver
    {
        /// <summary>Counts one priced call toward the session and, when a campaign is open, toward its persisted total.</summary>
        private void RecordUsage(TokenUsage usage, string model)
        {
            SessionUsage.Add(usage);
            if (CampaignUsage != null && Prompts != null) { CampaignUsage.Add(Prompts.CampaignSlug, model, usage); }
        }

        /// <summary>One call's reported tokens, priced for the profile that made it (its own price overrides win over the table).</summary>
        private static TokenUsage MeasureUsage(JsonValue response, ProviderProfile profile)
        {
            TokenUsage usage = TokenUsage.FromJson(response.Get("usage"));
            usage.ApplyPrice(ModelPricing.Bundled, profile, profile.Model.Trim());
            return usage;
        }

        /// <summary>Tag on the MCP calls a turn makes, so Stop aborts those and nothing else.</summary>
        public const string McpOwner = "turn";

        public const int MaxTransientRetries = 2;

        /// <summary>The last streamed reply ended in an error event that no retry can fix (a bad request, a refused model).</summary>
        private bool _permanentStreamError;

        /// <summary>A provider's in-stream error that invites a retry: rate limits, overload, a momentary outage.</summary>
        internal static bool LooksTransient(string error)
        {
            string e = (error ?? string.Empty).ToLowerInvariant();
            return e.Contains("rate limit") || e.Contains("rate_limit") || e.Contains("overload") || e.Contains("capacity")
                || e.Contains("try again") || e.Contains("temporarily") || e.Contains("timeout") || e.Contains("429") || e.Contains("503");
        }

        /// <summary>No answer, a dropped stream (a 200 that failed), a timeout, a rate limit or a server error: a retry can work.</summary>
        internal static bool IsTransient(long status)
        {
            return status == 0 || status == 200 || status == 408 || status == 425 || status == 429 || (status >= 500 && status <= 599);
        }

        /// <summary>What a failure report needs to know about the call that just ran (the key is passed only so it can be scrubbed).</summary>
        private LlmFailure.Context FailureContext(ProviderProfile profile, int attempts, string body)
        {
            return new LlmFailure.Context
            {
                Endpoint = ByokSettings.ChatUrl(profile),
                Model = profile.Model.Trim(),
                RequestMeta = LastRequestMeta,
                Attempts = Math.Max(1, attempts),
                ApiKey = ByokSettings.KeyOf(profile),
                Body = CapText(body, 1500),
            };
        }

        /// <summary>The provider's raw reply to the last PostChat, kept only to describe a failure.</summary>
        private string _lastBody = string.Empty;

        /// <summary>
        /// One model call through <paramref name="profile"/> (its endpoint, key and streaming setting). Streams when the
        /// endpoint allows it; an endpoint that rejects stream:true (HTTP 400/422) is retried once without it and
        /// remembered, so it's never asked to stream again this run.
        /// </summary>
        private IEnumerator RequestReply(ProviderProfile profile, Func<bool, bool, JsonValue> build, Action<string> onDelta, Action<JsonValue, string> done)
        {
            string endpoint = ByokSettings.ChatUrl(profile);
            _permanentStreamError = false;
            bool stream = !profile.DisableStreaming && !_noStreamEndpoints.Contains(endpoint);
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
            yield return PostChat(profile, request, stream, trackDelta, keep);
            if (usageOptions && failure != null && !streamedText && !_cancelRequested && (status == 400 || status == 422))
            {
                // Some endpoints stream fine but reject stream_options: drop it before giving up on streaming.
                request = build(true, false);
                LastRequestMeta = DescribeRequest(request) + " · no stream_options";
                yield return PostChat(profile, request, true, trackDelta, keep);
                if (failure == null) { _noStreamOptions.Add(endpoint); }
            }
            if (stream && failure != null && !streamedText && !_cancelRequested && (status == 400 || status == 422))
            {
                request = build(false, false);
                LastRequestMeta = DescribeRequest(request) + " · stream retry";
                yield return PostChat(profile, request, false, onDelta, keep);
                if (failure == null) { _noStreamEndpoints.Add(endpoint); }
            }
            // A busy provider (free models especially) drops or refuses requests that work a moment later: try again, quietly.
            for (int attempt = 1; attempt <= MaxTransientRetries && _retryBusy && failure != null && !streamedText && !_cancelRequested && IsTransient(status) && !_permanentStreamError; attempt++)
            {
                Status = "the provider is busy: trying again (" + attempt + "/" + MaxTransientRetries + ")…";
                float until = Time.realtimeSinceStartup + 2f * attempt;
                while (Time.realtimeSinceStartup < until && !_cancelRequested) { yield return null; }
                if (_cancelRequested) { break; }
                request = build(stream, usageOptions && !_noStreamOptions.Contains(endpoint));
                yield return PostChat(profile, request, stream, trackDelta, keep);
            }
            if (failure == null)
            {
                ProviderProblem = string.Empty;
                LastFailure = null;
            }
            else
            {
                LlmFailure.Context context = FailureContext(profile, attempts, _lastBody);
                if (_cancelRequested) { LastFailure = LlmFailure.Cancelled(context); }
                else
                {
                    LastFailure = LlmFailure.From(status, failure, context);
                    // Callers still get one string: the friendly wording for the quiet-retry kinds, the provider's own for the rest.
                    if (_retryBusy && IsTransient(status) && !_permanentStreamError) { failure = LastFailure.Friendly; }
                    string problem = ProviderProblemFor(status, profile.Model.Trim());
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
            string meta = request.GetString("model", "?") + " · " + messages + " msgs · " + request.ToJson().Length + " chars · " + tools + " tools";
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

        /// <summary>Model, streaming and sampling knobs, from the profile the call goes through (the loop's or the storyteller's).</summary>
        private static JsonValue NewRequest(ProviderProfile profile, bool stream, bool usageOptions)
        {
            var request = JsonValue.NewObject();
            request.ObjectValue["model"] = JsonValue.FromString(profile.Model.Trim());
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
            float temp = Mathf.Clamp(profile.Temperature, 0f, 2f);
            if (Mathf.Abs(temp - 1f) > 0.0001f) { request.ObjectValue["temperature"] = JsonValue.FromNumber(temp); }
            if (profile.MaxTokens > 0) { request.ObjectValue["max_tokens"] = JsonValue.FromNumber(profile.MaxTokens); }
            if (!string.IsNullOrEmpty(profile.ReasoningEffort) && IsOpenRouter(profile.BaseUrl))
            {
                var reasoning = JsonValue.NewObject();
                reasoning.ObjectValue["effort"] = JsonValue.FromString(profile.ReasoningEffort);
                request.ObjectValue["reasoning"] = reasoning;
            }
            return request;
        }

        private JsonValue BuildRequest(ProviderProfile loop, string systemPrompt, bool stream, bool usageOptions)
        {
            var request = NewRequest(loop, stream, usageOptions);
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
            var skillNames = Prompts.SkillNames(_loopTurn);
            if (skillNames.Count > 0) { tools.ArrayValue.Add(LoadSkillDefinition(skillNames)); }
            if (tools.ArrayValue.Count > 0) { request.ObjectValue["tools"] = tools; }
            return request;
        }


        private IEnumerator PostChat(ProviderProfile profile, JsonValue request, bool stream, Action<string> onDelta, Action<JsonValue, string, long> done)
        {
            byte[] payload = Encoding.UTF8.GetBytes(request.ToJson());
            _lastBody = string.Empty;
            string key = ByokSettings.KeyOf(profile);
            using (UnityWebRequest web = new UnityWebRequest(ByokSettings.ChatUrl(profile), "POST"))
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
                if (key.Length > 0) { web.SetRequestHeader("Authorization", "Bearer " + key); }
                // Model turns (long narration, reasoning models) outlast tool calls. A streamed reply is alive while bytes
                // keep coming, so it is cut off only after that many silent seconds, never on a fixed total (A11).
                int limit = Math.Max(120, Vault != null ? Vault.TimeoutSeconds * 2 : 120);
                web.timeout = sse != null ? 0 : limit;
                _activeChat = web;
                var sending = web.SendWebRequest();
                bool idle = false;
                while (!sending.isDone)
                {
                    if (sse != null && sse.SecondsSinceLastData > limit) { idle = true; web.Abort(); yield return null; break; }
                    yield return null;
                }
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
                    string what = idle
                        ? "no data from the chat endpoint for " + limit + " s"
                        : web.responseCode == 0
                        ? (web.error ?? "no response") + " contacting the chat endpoint"
                        : "HTTP " + web.responseCode + " from chat endpoint";
                    done(null, what + (detail.Length > 0 ? ": " + TextSanitizer.Clean(detail, 300) : "."), web.responseCode);
                    yield break;
                }
                if (stream)
                {
                    if (accumulator.Error != null)
                    {
                        // An error event inside a 200 stream is the provider's answer, not a dropped connection: the quiet
                        // retry is only for the ones that say to try again (A12).
                        _permanentStreamError = !LooksTransient(accumulator.Error);
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
    }
}
