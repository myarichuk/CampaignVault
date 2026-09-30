using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// Turns a byte stream into SSE event payloads: UTF-8 decoded across
    /// chunk boundaries (a multi-byte glyph can straddle two network reads),
    /// data: lines joined per event, comments (": keep-alive") ignored,
    /// [DONE] swallowed. Pure C#.
    /// </summary>
    public sealed class SseDecoder
    {
        private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
        private readonly StringBuilder _line = new StringBuilder();
        private readonly StringBuilder _data = new StringBuilder();
        private bool _hasData;
        private char[] _chars = new char[4096];

        /// <summary>Called with each complete event's data.</summary>
        public Action<string> OnEvent;

        public void Feed(byte[] bytes, int count)
        {
            int needed = _utf8.GetCharCount(bytes, 0, count);
            if (_chars.Length < needed) { _chars = new char[needed]; }
            int n = _utf8.GetChars(bytes, 0, count, _chars, 0);
            for (int i = 0; i < n; i++)
            {
                char c = _chars[i];
                if (c == '\n') { EndLine(); }
                else if (c != '\r') { _line.Append(c); }
            }
        }

        /// <summary>End of stream: flush a final event that had no trailing blank line.</summary>
        public void Finish()
        {
            if (_line.Length > 0) { EndLine(); }
            Dispatch();
        }

        private void EndLine()
        {
            string line = _line.ToString();
            _line.Length = 0;
            if (line.Length == 0) { Dispatch(); return; }
            if (line[0] == ':') { return; }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) { return; }
            string data = line.Substring(5);
            if (data.StartsWith(" ", StringComparison.Ordinal)) { data = data.Substring(1); }
            if (_hasData) { _data.Append('\n'); }
            _data.Append(data);
            _hasData = true;
        }

        private void Dispatch()
        {
            if (!_hasData) { return; }
            string payload = _data.ToString();
            _data.Length = 0;
            _hasData = false;
            if (payload.Trim() == "[DONE]") { return; }
            if (OnEvent != null) { OnEvent(payload); }
        }
    }

    /// <summary>
    /// Folds OpenAI-style chat.completion.chunk deltas back into the one
    /// message a non-streamed call would have returned, so the tool loop
    /// downstream doesn't care which way the reply came. Keeps what
    /// providers need replayed: reasoning_details (OpenRouter, incl. Gemini
    /// thought signatures) and per-call extra_content. Pure C#.
    /// </summary>
    public sealed class ChatStreamAccumulator
    {
        private sealed class CallBuilder
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public readonly StringBuilder Arguments = new StringBuilder();
            public JsonValue Extra;
        }

        private readonly StringBuilder _content = new StringBuilder();
        private readonly StringBuilder _reasoning = new StringBuilder();
        private readonly List<JsonValue> _reasoningDetails = new List<JsonValue>();
        private readonly SortedDictionary<int, CallBuilder> _calls = new SortedDictionary<int, CallBuilder>();

        public string FinishReason = string.Empty;
        /// <summary>The usage object, when the provider sends one (stream_options.include_usage, OpenRouter always).</summary>
        public JsonValue Usage;
        /// <summary>A mid-stream error object (OpenRouter sends these as a data event).</summary>
        public string Error;
        public int Chunks;

        public string Content { get { return _content.ToString(); } }
        public bool HasToolCalls { get { return _calls.Count > 0; } }

        /// <summary>Folds one event payload in; returns the visible content delta ("" if none).</summary>
        public string Feed(string payload)
        {
            JsonValue chunk;
            if (!JsonValue.TryParse(payload, out chunk) || chunk.Kind != JsonKind.Object) { return string.Empty; }
            Chunks++;
            var error = chunk.Get("error");
            if (!error.IsNull)
            {
                Error = error.Kind == JsonKind.Object ? error.GetString("message", "stream error") : error.ToJson();
                return string.Empty;
            }
            var usage = chunk.Get("usage");
            if (usage.Kind == JsonKind.Object) { Usage = usage; }
            var choices = chunk.GetArray("choices");
            if (choices.Count == 0) { return string.Empty; }
            var choice = choices[0];
            string finish = choice.GetString("finish_reason", null);
            if (!string.IsNullOrEmpty(finish)) { FinishReason = finish; }
            var delta = choice.Get("delta");
            if (delta.IsNull) { return string.Empty; }

            string reasoning = delta.GetString("reasoning_content", delta.GetString("reasoning", null));
            if (!string.IsNullOrEmpty(reasoning)) { _reasoning.Append(reasoning); }
            foreach (var detail in delta.GetArray("reasoning_details")) { _reasoningDetails.Add(detail); }

            foreach (var call in delta.GetArray("tool_calls"))
            {
                int index = (int)call.GetNumber("index", _calls.Count);
                CallBuilder builder;
                if (!_calls.TryGetValue(index, out builder))
                {
                    builder = new CallBuilder();
                    _calls[index] = builder;
                }
                string id = call.GetString("id", null);
                if (!string.IsNullOrEmpty(id)) { builder.Id = id; }
                var fn = call.Get("function");
                string name = fn.GetString("name", null);
                if (!string.IsNullOrEmpty(name)) { builder.Name = name; }
                string args = fn.GetString("arguments", null);
                if (!string.IsNullOrEmpty(args)) { builder.Arguments.Append(args); }
                var extra = call.Get("extra_content");
                if (!extra.IsNull) { builder.Extra = extra; }
            }

            string content = delta.GetString("content", null);
            if (string.IsNullOrEmpty(content)) { return string.Empty; }
            _content.Append(content);
            return content;
        }

        /// <summary>The assembled reply, shaped like a non-streamed response body.</summary>
        public JsonValue ToResponse()
        {
            var message = JsonValue.NewObject();
            message.ObjectValue["role"] = JsonValue.FromString("assistant");
            message.ObjectValue["content"] = JsonValue.FromString(_content.ToString());
            if (_reasoning.Length > 0) { message.ObjectValue["reasoning_content"] = JsonValue.FromString(_reasoning.ToString()); }
            if (_reasoningDetails.Count > 0)
            {
                var details = JsonValue.NewArray();
                details.ArrayValue.AddRange(_reasoningDetails);
                message.ObjectValue["reasoning_details"] = details;
            }
            if (_calls.Count > 0)
            {
                var calls = JsonValue.NewArray();
                int ordinal = 0;
                foreach (var builder in _calls.Values)
                {
                    var call = JsonValue.NewObject();
                    call.ObjectValue["id"] = JsonValue.FromString(string.IsNullOrEmpty(builder.Id) ? "call-" + ordinal : builder.Id);
                    call.ObjectValue["type"] = JsonValue.FromString("function");
                    var fn = JsonValue.NewObject();
                    fn.ObjectValue["name"] = JsonValue.FromString(builder.Name);
                    fn.ObjectValue["arguments"] = JsonValue.FromString(builder.Arguments.Length > 0 ? builder.Arguments.ToString() : "{}");
                    call.ObjectValue["function"] = fn;
                    if (builder.Extra != null) { call.ObjectValue["extra_content"] = builder.Extra; }
                    calls.ArrayValue.Add(call);
                    ordinal++;
                }
                message.ObjectValue["tool_calls"] = calls;
            }
            var choice = JsonValue.NewObject();
            choice.ObjectValue["message"] = message;
            choice.ObjectValue["finish_reason"] = JsonValue.FromString(FinishReason);
            var choices = JsonValue.NewArray();
            choices.ArrayValue.Add(choice);
            var response = JsonValue.NewObject();
            response.ObjectValue["choices"] = choices;
            if (Usage != null) { response.ObjectValue["usage"] = Usage; }
            return response;
        }
    }

    /// <summary>
    /// Feeds the network stream to an SseDecoder as it arrives. Also keeps
    /// the raw bytes (capped) so an error status still yields a readable body.
    /// </summary>
    public sealed class SseDownloadHandler : DownloadHandlerScript
    {
        private const int MaxRawBytes = 64 * 1024;
        private readonly SseDecoder _decoder;
        private readonly List<byte> _raw = new List<byte>();

        public SseDownloadHandler(SseDecoder decoder) : base(new byte[16 * 1024]) { _decoder = decoder; }

        public string RawText { get { return Encoding.UTF8.GetString(_raw.ToArray()); } }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) { return true; }
            int keep = Math.Min(dataLength, MaxRawBytes - _raw.Count);
            for (int i = 0; i < keep; i++) { _raw.Add(data[i]); }
            _decoder.Feed(data, dataLength);
            return true;
        }

        protected override void CompleteContent() { _decoder.Finish(); }
    }
}
