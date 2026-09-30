using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Net
{
    /// <summary>Outcome of one MCP round trip. Errors carry a stable code, never secrets.</summary>
    public sealed class McpOutcome<T>
    {
        public bool Ok;
        public T Data;
        public string ErrorCode = string.Empty;
        public string ErrorMessage = string.Empty;

        public static McpOutcome<T> Success(T data) { return new McpOutcome<T> { Ok = true, Data = data }; }
        public static McpOutcome<T> Fail(string code, string message)
        {
            return new McpOutcome<T> { Ok = false, ErrorCode = code, ErrorMessage = message };
        }
    }

    public sealed class McpToolInfo
    {
        public string Name = string.Empty;
        public string Description = string.Empty;
        public JsonValue InputSchema;
    }

    /// <summary>
    /// A CampaignVault tool result unwrapped from its {success, data, summary}
    /// envelope. Data is the envelope's data (or the whole payload when a tool
    /// answers without one); Envelope keeps siblings like worldPressure.
    /// </summary>
    public sealed class ToolPayload
    {
        public JsonValue Data = JsonValue.Null;
        public JsonValue Envelope = JsonValue.Null;
        public string Summary = string.Empty;
    }

    /// <summary>
    /// Minimal MCP Streamable-HTTP client over UnityWebRequest (WebGL-safe: no
    /// sockets). Opens one MCP session per connector URL (initialize, then
    /// notifications/initialized, echoing Mcp-Session-Id on every later call;
    /// a stateless server simply returns no id) and speaks JSON-RPC 2.0
    /// tools/list and tools/call against the server's /play and /build
    /// connectors. An expired session (404) is reopened once transparently.
    /// </summary>
    public class McpClient : MonoBehaviour
    {
        public const int MaxResultChars = 20000;
        public const string ProtocolVersion = "2025-06-18";

        private sealed class McpSession
        {
            public string Id = string.Empty;
            public string Protocol = ProtocolVersion;
        }

        private sealed class RawReply
        {
            public bool TransportOk;
            public long Status;
            public string Body = string.Empty;
            public string SessionId = string.Empty;
            public string Error = string.Empty;
        }

        private int _nextId = 1;

        // Keyed by connector URL, so pointing the client at another server
        // (e.g. the embedded one) negotiates a fresh session.
        private readonly Dictionary<string, McpSession> _sessions = new Dictionary<string, McpSession>();
        private readonly HashSet<string> _opening = new HashSet<string>();

        // Requests on the wire, so Stop can cut them instead of waiting out the timeout.
        private readonly HashSet<UnityWebRequest> _inFlight = new HashSet<UnityWebRequest>();

        /// <summary>Drop every negotiated session (server URL or token changed).</summary>
        public void ResetSessions() { _sessions.Clear(); }

        /// <summary>
        /// Cuts every in-flight request (the player pressed Stop). Callers see a
        /// transport failure. A tools/call the server already received may still
        /// commit, which the driver says out loud.
        /// </summary>
        public void AbortAll()
        {
            foreach (var request in new List<UnityWebRequest>(_inFlight)) { request.Abort(); }
            _inFlight.Clear();
        }

        public IEnumerator ListTools(
            VaultClientConfig config, string connector, Action<McpOutcome<List<McpToolInfo>>> done)
        {
            McpOutcome<JsonValue> outcome = null;
            yield return PostRpc(config, connector, "tools/list", JsonValue.NewObject(),
                delegate (McpOutcome<JsonValue> o) { outcome = o; });
            if (!outcome.Ok) { done(McpOutcome<List<McpToolInfo>>.Fail(outcome.ErrorCode, outcome.ErrorMessage)); yield break; }
            var tools = new List<McpToolInfo>();
            foreach (var t in outcome.Data.GetArray("tools"))
            {
                tools.Add(new McpToolInfo
                {
                    Name = t.GetString("name", string.Empty),
                    Description = t.GetString("description", string.Empty),
                    InputSchema = t.Get("inputSchema"),
                });
            }
            done(McpOutcome<List<McpToolInfo>>.Success(tools));
        }

        /// <summary>
        /// Raw tool text for the model: a tool-level failure (isError or
        /// success:false) is still a successful round trip here, because the
        /// model needs the server's error text and retry hints verbatim.
        /// </summary>
        public IEnumerator CallTool(
            VaultClientConfig config, string connector, string toolName, JsonValue args,
            Action<McpOutcome<string>> done)
        {
            McpOutcome<JsonValue> outcome = null;
            yield return InvokeTool(config, connector, toolName, args, delegate (McpOutcome<JsonValue> o) { outcome = o; });
            if (!outcome.Ok) { done(McpOutcome<string>.Fail(outcome.ErrorCode, outcome.ErrorMessage)); yield break; }
            string text = ResultText(outcome.Data);
            if (text.Length > MaxResultChars)
            {
                text = text.Substring(0, MaxResultChars) + "\n… (result truncated)";
            }
            done(McpOutcome<string>.Success(text));
        }

        /// <summary>
        /// Envelope-aware call for panels: fails on isError or success:false
        /// (code = the envelope's error, message = its summary) and otherwise
        /// hands back the unwrapped data.
        /// </summary>
        public IEnumerator CallToolData(
            VaultClientConfig config, string connector, string toolName, JsonValue args,
            Action<McpOutcome<ToolPayload>> done)
        {
            McpOutcome<JsonValue> outcome = null;
            yield return InvokeTool(config, connector, toolName, args, delegate (McpOutcome<JsonValue> o) { outcome = o; });
            if (!outcome.Ok) { done(McpOutcome<ToolPayload>.Fail(outcome.ErrorCode, outcome.ErrorMessage)); yield break; }
            done(ReadEnvelope(outcome.Data));
        }

        /// <summary>Parses a tools/call result (content blocks + isError) into a panel outcome.</summary>
        internal static McpOutcome<ToolPayload> ReadEnvelope(JsonValue result)
        {
            string text = ResultText(result);
            bool isError = result.GetBool("isError", false);
            JsonValue parsed;
            if (!JsonValue.TryParse(text, out parsed) || parsed.Kind != JsonKind.Object)
            {
                if (isError) { return McpOutcome<ToolPayload>.Fail("TOOL_ERROR", TextSanitizer.Clean(text, 500)); }
                return McpOutcome<ToolPayload>.Fail("PROTOCOL", "Tool returned non-JSON text: " + TextSanitizer.Clean(text, 200));
            }
            string summary = parsed.GetString("summary", string.Empty);
            if (isError || !parsed.GetBool("success", true))
            {
                string code = parsed.GetString("error", "TOOL_ERROR");
                string message = string.IsNullOrEmpty(summary) ? code : summary;
                return McpOutcome<ToolPayload>.Fail(code, TextSanitizer.Clean(message, 500));
            }
            var data = parsed.Get("data");
            return McpOutcome<ToolPayload>.Success(new ToolPayload
            {
                Data = data.IsNull ? parsed : data,
                Envelope = parsed,
                Summary = summary,
            });
        }

        private static string ResultText(JsonValue result)
        {
            var sb = new StringBuilder();
            foreach (var block in result.GetArray("content"))
            {
                if (block.GetString("type", string.Empty) == "text")
                {
                    if (sb.Length > 0) { sb.Append('\n'); }
                    sb.Append(block.GetString("text", string.Empty));
                }
            }
            return sb.ToString();
        }

        private IEnumerator InvokeTool(
            VaultClientConfig config, string connector, string toolName, JsonValue args,
            Action<McpOutcome<JsonValue>> done)
        {
            var callParams = JsonValue.NewObject();
            callParams.ObjectValue["name"] = JsonValue.FromString(toolName);
            callParams.ObjectValue["arguments"] = args ?? JsonValue.NewObject();
            return PostRpc(config, connector, "tools/call", callParams, done);
        }

        /// <summary>Sends one JSON-RPC request inside an MCP session; done gets the result member.</summary>
        private IEnumerator PostRpc(
            VaultClientConfig config, string connector, string method, JsonValue rpcParams,
            Action<McpOutcome<JsonValue>> done)
        {
            string reason;
            if (!config.Validate(out reason))
            {
                done(McpOutcome<JsonValue>.Fail("BAD_CONFIG", reason));
                yield break;
            }
            string url = config.ConnectorPath(connector);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                string openFailure = null;
                yield return EnsureSession(config, url, delegate (string err) { openFailure = err; });
                if (openFailure != null)
                {
                    done(McpOutcome<JsonValue>.Fail(openFailure.StartsWith("AUTH", StringComparison.Ordinal) ? "AUTH" : "HTTP", openFailure));
                    yield break;
                }
                McpSession session;
                _sessions.TryGetValue(url, out session);

                RawReply reply = null;
                yield return Send(config, url, Envelope(method, rpcParams, true), session, delegate (RawReply r) { reply = r; });

                // 404 = the server forgot this session (restart, idle expiry): reopen once.
                if (reply.Status == 404 && session != null && !string.IsNullOrEmpty(session.Id) && attempt == 0)
                {
                    _sessions.Remove(url);
                    continue;
                }
                done(Interpret(reply, url));
                yield break;
            }
        }

        private IEnumerator EnsureSession(VaultClientConfig config, string url, Action<string> done)
        {
            // Another coroutine is already opening this session: wait for it.
            while (_opening.Contains(url)) { yield return null; }
            if (_sessions.ContainsKey(url)) { done(null); yield break; }

            _opening.Add(url);
            try
            {
                var initParams = JsonValue.NewObject();
                initParams.ObjectValue["protocolVersion"] = JsonValue.FromString(ProtocolVersion);
                initParams.ObjectValue["capabilities"] = JsonValue.NewObject();
                var clientInfo = JsonValue.NewObject();
                clientInfo.ObjectValue["name"] = JsonValue.FromString("campaignvault-unity");
                clientInfo.ObjectValue["version"] = JsonValue.FromString(Application.version);
                initParams.ObjectValue["clientInfo"] = clientInfo;

                RawReply reply = null;
                yield return Send(config, url, Envelope("initialize", initParams, true), null, delegate (RawReply r) { reply = r; });
                var init = Interpret(reply, url);
                if (!init.Ok)
                {
                    done((init.ErrorCode == "AUTH" ? "AUTH: " : "MCP initialize failed: ") + init.ErrorMessage);
                    yield break;
                }
                var session = new McpSession
                {
                    Id = reply.SessionId,
                    Protocol = init.Data.GetString("protocolVersion", ProtocolVersion),
                };

                RawReply ack = null;
                yield return Send(config, url, Envelope("notifications/initialized", null, false), session, delegate (RawReply r) { ack = r; });
                if (!ack.TransportOk && ack.Status != 202)
                {
                    done("MCP initialized notification failed: HTTP " + ack.Status);
                    yield break;
                }
                _sessions[url] = session;
                done(null);
            }
            finally
            {
                _opening.Remove(url);
            }
        }

        private JsonValue Envelope(string method, JsonValue rpcParams, bool isRequest)
        {
            var body = JsonValue.NewObject();
            body.ObjectValue["jsonrpc"] = JsonValue.FromString("2.0");
            if (isRequest) { body.ObjectValue["id"] = JsonValue.FromNumber(_nextId++); }
            body.ObjectValue["method"] = JsonValue.FromString(method);
            if (rpcParams != null) { body.ObjectValue["params"] = rpcParams; }
            return body;
        }

        private IEnumerator Send(
            VaultClientConfig config, string url, JsonValue body, McpSession session, Action<RawReply> done)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body.ToJson());
            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                _inFlight.Add(request);
                request.uploadHandler = new UploadHandlerRaw(payload);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json, text/event-stream");
                if (session != null)
                {
                    if (!string.IsNullOrEmpty(session.Id)) { request.SetRequestHeader("Mcp-Session-Id", session.Id); }
                    request.SetRequestHeader("MCP-Protocol-Version", session.Protocol);
                }
                bool toolCall = body.GetString("method", string.Empty) == "tools/call";
                request.timeout = Math.Max(5, toolCall ? config.ToolTimeoutSeconds : config.TimeoutSeconds);
                config.ApplyAuth(request);

                yield return request.SendWebRequest();
                _inFlight.Remove(request);

                done(new RawReply
                {
                    TransportOk = request.result == UnityWebRequest.Result.Success,
                    Status = request.responseCode,
                    Body = request.downloadHandler != null ? (request.downloadHandler.text ?? string.Empty) : string.Empty,
                    SessionId = HeaderValue(request, "Mcp-Session-Id"),
                    Error = request.error ?? string.Empty,
                });
            }
        }

        private static McpOutcome<JsonValue> Interpret(RawReply reply, string url)
        {
            if (!reply.TransportOk)
            {
                if (reply.Status == 401)
                {
                    return McpOutcome<JsonValue>.Fail("AUTH", "Server rejected credentials (401). Check the bearer token.");
                }
                string detail = reply.Status == 0 ? reply.Error : "HTTP " + reply.Status;
                string serverMessage = RpcErrorMessage(ExtractJsonPayload(reply.Body));
                if (!string.IsNullOrEmpty(serverMessage)) { detail += ": " + serverMessage; }
                return McpOutcome<JsonValue>.Fail("HTTP", TextSanitizer.Clean(detail + " contacting " + url, 500));
            }
            JsonValue envelope;
            if (!JsonValue.TryParse(ExtractJsonPayload(reply.Body), out envelope) || envelope.Kind != JsonKind.Object)
            {
                return McpOutcome<JsonValue>.Fail("PROTOCOL", "Server returned a non-JSON-RPC payload.");
            }
            if (!envelope.Get("error").IsNull)
            {
                return McpOutcome<JsonValue>.Fail("RPC_ERROR",
                    TextSanitizer.Clean(envelope.Get("error").GetString("message", "request failed"), 500));
            }
            return McpOutcome<JsonValue>.Success(envelope.Get("result"));
        }

        private static string RpcErrorMessage(string body)
        {
            JsonValue parsed;
            if (!JsonValue.TryParse(body, out parsed)) { return string.Empty; }
            return parsed.Get("error").GetString("message", string.Empty);
        }

        private static string HeaderValue(UnityWebRequest request, string name)
        {
            var headers = request.GetResponseHeaders();
            if (headers == null) { return string.Empty; }
            foreach (var kv in headers)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { return kv.Value ?? string.Empty; }
            }
            return string.Empty;
        }

        /// <summary>
        /// Streamable HTTP may answer as SSE (event:/data: frames). Take the last
        /// data frame; plain JSON passes through untouched.
        /// </summary>
        internal static string ExtractJsonPayload(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.IndexOf("data:", StringComparison.Ordinal) < 0)
            {
                return raw;
            }
            // One event = consecutive data: lines (joined with \n, per the SSE
            // spec) up to a blank line. The last complete event wins.
            string last = null;
            var current = new StringBuilder();
            bool inEvent = false;
            string[] lines = raw.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i <= lines.Length; i++)
            {
                string line = i < lines.Length ? lines[i] : string.Empty;
                if (line.Trim().Length == 0)
                {
                    if (inEvent)
                    {
                        string data = current.ToString();
                        if (data.Trim() != "[DONE]") { last = data; }
                    }
                    current.Length = 0;
                    inEvent = false;
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    string data = line.Substring(5);
                    if (data.StartsWith(" ", StringComparison.Ordinal)) { data = data.Substring(1); }
                    if (inEvent) { current.Append('\n'); }
                    current.Append(data);
                    inEvent = true;
                }
            }
            return last ?? raw;
        }
    }
}
