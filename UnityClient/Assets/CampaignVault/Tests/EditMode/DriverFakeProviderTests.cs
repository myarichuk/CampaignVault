using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// Runs the real driver against a local fake: one HttpListener serving
    /// both an OpenAI-style chat endpoint (scripted replies, streamed or not)
    /// and a minimal MCP /play endpoint. Covers the streamed tool loop,
    /// stream fallback, Stop (B1) and the dangling-user-message fix (B9).
    /// </summary>
    public class DriverFakeProviderTests
    {
        private sealed class Reply
        {
            public int Status = 200;
            public string ContentType = "application/json";
            public string Body = "{}";
            public int DelayMs;
        }

        private HttpListener _listener;
        private Thread _thread;
        private string _base;
        private readonly Queue<Func<JsonValue, Reply>> _script = new Queue<Func<JsonValue, Reply>>();
        private readonly List<JsonValue> _chatRequests = new List<JsonValue>();
        private readonly List<string> _toolCalls = new List<string>();
        private string _toolResult = "{\"success\":true,\"summary\":\"ok\"}";
        private volatile bool _stopping;

        private GameObject _go;
        private OpenAiChatDriver _driver;
        private VaultTranscript _transcript;

        [SetUp]
        public void SetUp()
        {
            // NUnit reuses one fixture instance across tests.
            _script.Clear();
            _chatRequests.Clear();
            _toolCalls.Clear();
            _toolResult = "{\"success\":true,\"summary\":\"ok\"}";
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _base = "http://127.0.0.1:" + port;
            _listener = new HttpListener();
            _listener.Prefixes.Add(_base + "/");
            _listener.Start();
            _stopping = false;
            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();

            _go = new GameObject("DriverTest");
            var config = _go.AddComponent<VaultClientConfig>();
            config.ServerUrl = _base;
            var byok = _go.AddComponent<ByokSettings>();
            byok.Active.Preset = "ollama"; // keyless preset
            byok.BaseUrl = _base + "/v1";
            byok.Model = "fake-model";
            // The B-series tests pin the single-pass loop; two-pass tests switch it off.
            byok.Active.SinglePass = true;
            _driver = _go.AddComponent<OpenAiChatDriver>();
            _driver.Byok = byok;
            _driver.Vault = config;
            _driver.Mcp = _go.AddComponent<McpClient>();
            _driver.Prompts = _go.AddComponent<SystemPromptProvider>();
            _transcript = new VaultTranscript();
        }

        [TearDown]
        public void TearDown()
        {
            _stopping = true;
            try { _listener.Stop(); _listener.Close(); } catch (Exception) { }
            UnityEngine.Object.DestroyImmediate(_go);
        }

        // ---- fake server ----

        private void Serve()
        {
            while (!_stopping)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) { body = reader.ReadToEnd(); }
                JsonValue json;
                JsonValue.TryParse(body, out json);
                Reply reply;
                string path = ctx.Request.Url.AbsolutePath;
                if (path == "/v1/chat/completions")
                {
                    Func<JsonValue, Reply> next;
                    lock (_script)
                    {
                        _chatRequests.Add(json);
                        next = _script.Count > 0 ? _script.Dequeue() : null;
                    }
                    reply = next != null ? next(json) : new Reply { Status = 500, Body = "{\"error\":{\"message\":\"script exhausted\"}}" };
                }
                else if (path == "/play")
                {
                    reply = Mcp(json, ctx.Response);
                }
                else
                {
                    reply = new Reply { Status = 404 };
                }
                if (reply.DelayMs > 0) { Thread.Sleep(reply.DelayMs); }
                if (_stopping) { return; }
                ctx.Response.StatusCode = reply.Status;
                ctx.Response.ContentType = reply.ContentType;
                byte[] bytes = Encoding.UTF8.GetBytes(reply.Body ?? string.Empty);
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception) { /* client aborted: expected in the Stop test */ }
        }

        private Reply Mcp(JsonValue rpc, HttpListenerResponse response)
        {
            string method = rpc.GetString("method", string.Empty);
            string id = rpc.Get("id").IsNull ? "null" : rpc.Get("id").ToJson();
            switch (method)
            {
                case "initialize":
                    response.AddHeader("Mcp-Session-Id", "s1");
                    return Rpc(id, "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}}");
                case "notifications/initialized":
                    return new Reply { Status = 202, Body = string.Empty };
                case "tools/list":
                    return Rpc(id, "{\"tools\":[{\"name\":\"take_turn\",\"description\":\"act\",\"inputSchema\":{\"type\":\"object\"}}]}");
                case "tools/call":
                    lock (_toolCalls) { _toolCalls.Add(rpc.Get("params").ToJson()); }
                    var text = JsonValue.NewObject();
                    text.ObjectValue["type"] = JsonValue.FromString("text");
                    text.ObjectValue["text"] = JsonValue.FromString(_toolResult);
                    var content = JsonValue.NewArray();
                    content.ArrayValue.Add(text);
                    var result = JsonValue.NewObject();
                    result.ObjectValue["content"] = content;
                    return Rpc(id, result.ToJson());
                default:
                    return Rpc(id, "{}");
            }
        }

        private static Reply Rpc(string id, string result)
        {
            return new Reply { Body = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + result + "}" };
        }

        private static Reply Sse(params string[] chunks)
        {
            var sb = new StringBuilder();
            foreach (string c in chunks) { sb.Append("data: ").Append(c).Append("\n\n"); }
            sb.Append("data: [DONE]\n\n");
            return new Reply { ContentType = "text/event-stream", Body = sb.ToString() };
        }

        private static string Delta(string deltaJson) { return "{\"choices\":[{\"delta\":" + deltaJson + "}]}"; }

        private static string Plain(string content)
        {
            return "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + JsonValue.FromString(content).ToJson() + "}}]}";
        }

        private IEnumerator Send(string text, Func<bool> tick = null)
        {
            _transcript.Add(new TranscriptSegment { Kind = SegmentKind.Player, Text = text });
            return EditModeCoroutines.Drive(_driver.SendPlayerText(text, _transcript, delegate { }), tick);
        }

        private int UserMessages(JsonValue request)
        {
            return request.GetArray("messages").Count(m => m.GetString("role", string.Empty) == "user");
        }

        // ---- tests ----

        [UnityTest]
        public IEnumerator StreamedToolLoop_AsideRollAndVoices()
        {
            _toolResult = "{\"success\":true,\"summary\":\"Search (Perception): Success. Rolled 17 vs DC 14.\"}";
            _script.Enqueue(req => Sse(
                Delta("{\"role\":\"assistant\",\"content\":\"Let me \"}"),
                Delta("{\"content\":\"check.\"}"),
                Delta("{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"take_turn\",\"arguments\":\"{\\\"act\"}}]}"),
                Delta("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"ion\\\":\\\"search\\\"}\"}}]}")));
            _script.Enqueue(req => Sse(
                Delta("{\"content\":\"The wheel groans. \"}"),
                Delta("{\"content\":\"Mirelle: “You're late.”\"}")));

            yield return Send("I search the mill.");

            Assert.IsFalse(_driver.IsBusy);
            Assert.IsTrue(_chatRequests[0].GetBool("stream", false), "streams by default");
            var kinds = _transcript.Segments.Select(s => s.Kind).ToList();
            Assert.IsFalse(_transcript.Segments.Any(s => s.Streaming), "no live segment left behind");
            var aside = _transcript.Segments.First(s => s.Kind == SegmentKind.Aside);
            Assert.AreEqual("Let me check.", aside.Text);
            var roll = _transcript.Segments.First(s => s.Kind == SegmentKind.Roll).Roll;
            Assert.AreEqual("Perception", roll.Label);
            Assert.AreEqual(RollOutcome.Success, roll.Outcome);
            Assert.AreEqual("You're late.", _transcript.Segments.First(s => s.Kind == SegmentKind.NpcVoice).Text);
            Assert.Less(kinds.IndexOf(SegmentKind.Aside), kinds.IndexOf(SegmentKind.Roll));

            StringAssert.Contains("\"action\":\"search\"", _toolCalls[0], "streamed argument fragments reassembled");
            var replay = _chatRequests[1].GetArray("messages");
            var assistant = replay.First(m => m.GetString("role", null) == "assistant");
            Assert.AreEqual("call_1", assistant.GetArray("tool_calls")[0].GetString("id", null));
            Assert.AreEqual("call_1", replay.First(m => m.GetString("role", null) == "tool").GetString("tool_call_id", null));
        }

        [UnityTest]
        public IEnumerator StreamRejected_FallsBackOnce_ThenStaysNonStreamed()
        {
            // An endpoint that refuses stream:true outright: the driver first
            // drops stream_options, then streaming, and remembers.
            Func<JsonValue, Reply> refuseStreams = req => req.GetBool("stream", false)
                ? new Reply { Status = 400, Body = "{\"error\":{\"message\":\"stream not supported\"}}" }
                : new Reply { Body = Plain("The rain keeps falling.") };
            for (int i = 0; i < 4; i++) { _script.Enqueue(refuseStreams); }

            yield return Send("I wait.");
            yield return Send("I wait more.");

            Assert.AreEqual(4, _chatRequests.Count);
            Assert.IsTrue(_chatRequests[0].GetBool("stream", false));
            Assert.IsFalse(_chatRequests[0].Get("stream_options").IsNull, "asks for usage when streaming");
            Assert.IsTrue(_chatRequests[1].GetBool("stream", false));
            Assert.IsTrue(_chatRequests[1].Get("stream_options").IsNull, "retries without stream_options first");
            Assert.IsFalse(_chatRequests[2].GetBool("stream", false));
            Assert.IsFalse(_chatRequests[3].GetBool("stream", false), "remembered: no second stream attempt");
            Assert.AreEqual(2, _transcript.Segments.Count(s => s.Kind == SegmentKind.Narration));
        }

        [UnityTest]
        public IEnumerator StreamOptionsRejected_KeepsStreamingWithoutThem()
        {
            Func<JsonValue, Reply> refuseOptions = req => !req.Get("stream_options").IsNull
                ? new Reply { Status = 400, Body = "{\"error\":{\"message\":\"unknown field stream_options\"}}" }
                : Sse(Delta("{\"content\":\"Mist.\"}"));
            for (int i = 0; i < 3; i++) { _script.Enqueue(refuseOptions); }

            yield return Send("I look.");
            yield return Send("I look again.");

            Assert.AreEqual(3, _chatRequests.Count);
            Assert.IsTrue(_chatRequests[2].GetBool("stream", false), "still streams");
            Assert.IsTrue(_chatRequests[2].Get("stream_options").IsNull, "remembered: no stream_options");
        }

        [UnityTest]
        public IEnumerator Usage_IsRecordedPerTurnAndSession()
        {
            _toolResult = "{\"success\":true,\"summary\":\"Search (Perception): Success. Rolled 17 vs DC 14.\"}";
            _script.Enqueue(req => Sse(
                Delta("{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"take_turn\",\"arguments\":\"{}\"}}]}"),
                "{\"choices\":[],\"usage\":{\"prompt_tokens\":900,\"completion_tokens\":30,\"prompt_tokens_details\":{\"cached_tokens\":800}}}"));
            _script.Enqueue(req => new Reply { Body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"The wheel groans.\"}}],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":200}}" });

            yield return Send("I search the mill.");

            Assert.AreEqual(1, _driver.Turns.Count);
            var turn = _driver.Turns[0];
            Assert.AreEqual(1900, turn.Usage.Prompt);
            Assert.AreEqual(230, turn.Usage.Completion);
            Assert.AreEqual(800, turn.Usage.Cached);
            Assert.AreEqual(2, turn.Usage.Calls);
            Assert.AreEqual("The wheel groans.", turn.Narration.ToString());
            Assert.AreEqual("I search the mill.", turn.Player);
            CollectionAssert.AreEqual(new[] { "take_turn" }, turn.Tools);
            Assert.AreEqual(1, turn.Rolls.Count);
            StringAssert.StartsWith("Perception", turn.Rolls[0]);
            Assert.AreEqual(1900, _driver.SessionUsage.Prompt);

            _driver.ResetConversation();
            Assert.AreEqual(0, _driver.Turns.Count);
            Assert.IsTrue(_driver.SessionUsage.IsEmpty);
        }

        [UnityTest]
        public IEnumerator B1_Stop_AbortsInFlightCallQuickly()
        {
            _script.Enqueue(req => new Reply { Body = Plain("too late"), DelayMs = 20000 });
            float started = Time.realtimeSinceStartup;
            bool cancelled = false;
            yield return Send("I hesitate.", delegate
            {
                if (!cancelled && _chatRequests.Count == 1 && Time.realtimeSinceStartup - started > 0.3f)
                {
                    cancelled = true;
                    _driver.Cancel();
                }
                return true;
            });
            float elapsed = Time.realtimeSinceStartup - started;

            Assert.IsTrue(cancelled);
            Assert.IsFalse(_driver.IsBusy);
            Assert.Less(elapsed, 3f, "Stop must not wait out the request");
            Assert.IsTrue(_transcript.Segments.Any(s => s.Kind == SegmentKind.System && s.Text.StartsWith("Stopped")));
            Assert.AreEqual(0, _driver.History.Count, "unanswered player line is dropped (B9)");
        }

        [UnityTest]
        public IEnumerator B9_FailedFirstCall_DoesNotStackUserMessages()
        {
            _script.Enqueue(req => new Reply { Status = 500, Body = "{\"error\":{\"message\":\"boom\"}}" });
            _script.Enqueue(req => Sse(Delta("{\"content\":\"Better now.\"}")));

            yield return Send("First try.");
            Assert.IsTrue(_transcript.Segments.Any(s => s.Kind == SegmentKind.System && s.Text.Contains("boom")));
            yield return Send("Second try.");

            Assert.AreEqual(1, UserMessages(_chatRequests[1]));
            Assert.AreEqual("Better now.", _transcript.Segments.Last(s => s.Kind == SegmentKind.Narration).Text);
        }

        [UnityTest]
        public IEnumerator B8_AsideAlone_IsPromotedToNarration()
        {
            // The model narrates alongside its tool call, then goes quiet.
            _script.Enqueue(req => Sse(
                Delta("{\"content\":\"You push the door open.\"}"),
                Delta("{\"tool_calls\":[{\"index\":0,\"id\":\"c\",\"function\":{\"name\":\"take_turn\",\"arguments\":\"{}\"}}]}")));
            _script.Enqueue(req => Sse(Delta("{\"content\":\"\"}")));

            yield return Send("I open the door.");

            Assert.IsFalse(_transcript.Segments.Any(s => s.Kind == SegmentKind.Aside));
            Assert.AreEqual("You push the door open.", _transcript.Segments.First(s => s.Kind == SegmentKind.Narration).Text);
            Assert.IsFalse(_transcript.Segments.Any(s => s.Kind == SegmentKind.System && s.Text.Contains("empty reply")));
        }

        [UnityTest]
        public IEnumerator B11_EmptyReply_IsReported()
        {
            _script.Enqueue(req => new Reply { Body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\"}}]}" });
            yield return Send("Hello?");
            Assert.IsTrue(_transcript.Segments.Any(s => s.Kind == SegmentKind.System && s.Text.Contains("empty reply")));
        }

        // ---- two-pass: bookkeeping loop, then the storyteller (N2) ----

        private static Reply ToolCallReply(string id, string name, string argsJson)
        {
            return new Reply
            {
                Body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"" + id
                    + "\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":" + JsonValue.FromString(argsJson).ToJson() + "}}]}}]}",
            };
        }

        private void TwoPass() { _driver.Byok.Active.SinglePass = false; }

        private static bool HasTools(JsonValue request) { return request.Get("tools").Kind == JsonKind.Array; }

        private static string Content(JsonValue message) { return message.GetString("content", string.Empty); }

        [UnityTest]
        public IEnumerator TwoPass_LoopSaysDone_StorytellerWritesFromBrief()
        {
            TwoPass();
            _toolResult = "{\"success\":true,\"summary\":\"Search (Perception): Success. Rolled 17 vs DC 14.\",\"data\":{\"summary\":[\"Found: a torn flour sack\"],"
                + "\"physicalStateNudges\":[\"Aric's hands are white with flour\"],\"guidance\":[\"never shown\"]}}";
            _script.Enqueue(req => ToolCallReply("c1", "take_turn", "{\"narrative\":\"Aric searches the mill floor and finds a torn sack\"}"));
            _script.Enqueue(req => new Reply { Body = Plain("DONE") });
            _script.Enqueue(req => Sse(Delta("{\"content\":\"Flour drifts like snow \"}"), Delta("{\"content\":\"across the boards.\"}")));

            yield return Send("I search the mill.");

            Assert.IsFalse(_driver.IsBusy);
            Assert.AreEqual(3, _chatRequests.Count);
            StringAssert.Contains("TURN CONTRACT", Content(_chatRequests[0].GetArray("messages")[0]), "the loop gets the contract");
            Assert.IsTrue(HasTools(_chatRequests[0]));
            var narration = _chatRequests[2];
            Assert.IsFalse(HasTools(narration), "the storyteller gets no tools");
            var messages = narration.GetArray("messages");
            Assert.IsFalse(messages.Any(m => m.GetString("role", null) == "tool"), "no tool messages");
            StringAssert.StartsWith("You are the storyteller", Content(messages[0]));
            string brief = Content(messages[messages.Count - 1]);
            StringAssert.Contains("I search the mill.", brief);
            StringAssert.Contains("Aric searches the mill floor and finds a torn sack", brief);
            StringAssert.Contains("Perception: Success", brief);
            StringAssert.Contains("Found: a torn flour sack", brief);
            StringAssert.Contains("white with flour", brief);
            StringAssert.DoesNotContain("{", brief, "no JSON in the brief");
            StringAssert.DoesNotContain("never shown", brief, "guidance is for the loop only");

            Assert.AreEqual("Flour drifts like snow across the boards.", _transcript.Segments.Last(s => s.Kind == SegmentKind.Narration).Text);
            Assert.IsFalse(_transcript.Segments.Any(s => s.Text.Contains("DONE")), "DONE never reaches the log");
            Assert.AreEqual("Flour drifts like snow across the boards.", _driver.Turns[0].Narration.ToString());
            Assert.AreEqual("DONE", Content(_driver.History.Last()), "the loop's history keeps DONE, not the prose");
            Assert.IsFalse(_transcript.Segments.Any(s => s.Kind == SegmentKind.System && s.Text.StartsWith("⚠")));
        }

        [UnityTest]
        public IEnumerator TwoPass_ScenesFlowToNextTurns_OnlyNewestLoopMessageCarriesOne()
        {
            TwoPass();
            for (int i = 1; i <= 3; i++)
            {
                int n = i;
                _script.Enqueue(req => new Reply { Body = Plain("DONE") });
                _script.Enqueue(req => new Reply { Body = Plain("Scene " + n + ".") });
            }
            yield return Send("First.");
            yield return Send("Second.");
            yield return Send("Third.");

            Assert.AreEqual(6, _chatRequests.Count);
            var loop2 = _chatRequests[2].GetArray("messages");
            StringAssert.StartsWith(Storyteller.ScenePrefix, Content(loop2.Last()));
            StringAssert.Contains("Scene 1.", Content(loop2.Last()));
            StringAssert.EndsWith("Second.", Content(loop2.Last()));

            var loop3 = _chatRequests[4].GetArray("messages").Where(m => m.GetString("role", null) == "user").ToList();
            Assert.AreEqual("First.", Content(loop3[0]));
            Assert.AreEqual("Second.", Content(loop3[1]), "older loop messages drop their scene");
            StringAssert.Contains("Scene 2.", Content(loop3[2]));

            var story3 = _chatRequests[5].GetArray("messages");
            var roles = story3.Select(m => m.GetString("role", null)).ToArray();
            CollectionAssert.AreEqual(new[] { "system", "user", "assistant", "user", "assistant", "user" }, roles);
            Assert.AreEqual("Scene 1.", Content(story3[2]));
            Assert.AreEqual("Scene 2.", Content(story3[4]));
        }

        [UnityTest]
        public IEnumerator TwoPass_OocPlayer_AnswerShownAsIs_NoStoryteller()
        {
            TwoPass();
            _script.Enqueue(req => new Reply { Body = Plain("You have three hit dice left.") });
            yield return Send("OOC: how many hit dice do I have?");
            Assert.AreEqual(1, _chatRequests.Count);
            Assert.AreEqual("You have three hit dice left.", _transcript.Segments.Last(s => s.Kind == SegmentKind.Narration).Text);
        }

        [UnityTest]
        public IEnumerator TwoPass_ModelOocPrefix_SkipsStoryteller()
        {
            TwoPass();
            _script.Enqueue(req => new Reply { Body = Plain("OOC: Grappling uses Athletics.") });
            yield return Send("Can I grapple the ghoul?");
            Assert.AreEqual(1, _chatRequests.Count);
            Assert.AreEqual("Grappling uses Athletics.", _transcript.Segments.Last(s => s.Kind == SegmentKind.Narration).Text);
        }

        [UnityTest]
        public IEnumerator TwoPass_LoopNarratesAnyway_KeptAsNotes_StorytellerStillWrites()
        {
            TwoPass();
            _script.Enqueue(req => new Reply { Body = Plain("You push the door open.\nDONE") });
            _script.Enqueue(req => new Reply { Body = Plain("The hinges shriek.") });
            yield return Send("I open the door.");
            Assert.AreEqual(2, _chatRequests.Count);
            Assert.AreEqual("You push the door open.", _transcript.Segments.First(s => s.Kind == SegmentKind.Notes).Text);
            Assert.IsFalse(_transcript.Segments.Any(s => s.Kind == SegmentKind.Aside), "the story is told once, by the storyteller");
            Assert.AreEqual("The hinges shriek.", _transcript.Segments.Last(s => s.Kind == SegmentKind.Narration).Text);
        }

        [UnityTest]
        public IEnumerator TwoPass_LoopWordsWithToolCalls_AreNotes_NeverStreamedIntoTheStory()
        {
            TwoPass();
            _script.Enqueue(req => Sse(
                Delta("{\"content\":\"The mill is dark. \"}"),
                Delta("{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"take_turn\",\"arguments\":\"{}\"}}]}")));
            _script.Enqueue(req => new Reply { Body = Plain("DONE") });
            _script.Enqueue(req => new Reply { Body = Plain("Dust hangs in the lantern light.") });
            int liveNarration = 0;
            _transcript.Added += delegate (TranscriptSegment seg) { if (seg.Streaming && seg.Kind != SegmentKind.Narration) { liveNarration++; } };
            var succeeded = new List<string>();
            _driver.ToolSucceeded += delegate (string tool, string result) { succeeded.Add(tool); };

            yield return Send("I step inside.");

            Assert.AreEqual(0, liveNarration, "loop text is not streamed into the log");
            Assert.AreEqual("The mill is dark.", _transcript.Segments.First(s => s.Kind == SegmentKind.Notes).Text);
            Assert.IsFalse(_transcript.Segments.Any(s => s.Kind == SegmentKind.Aside));
            Assert.AreEqual(1, _transcript.Segments.Count(s => s.Kind == SegmentKind.Narration));
            CollectionAssert.AreEqual(new[] { "take_turn" }, succeeded, "the app layer hears about committed tool calls");
        }

        [UnityTest]
        public IEnumerator RestorePassages_FeedsTheStorytellerAfterARestart()
        {
            TwoPass();
            _driver.RestorePassages(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("I knock.", "The door opens a crack."),
            });
            Assert.AreEqual("The door opens a crack.", _driver.LastPassage);
            var messages = _driver.BuildNarrationMessages("I push in.", new TurnBrief()).ArrayValue;
            Assert.IsTrue(messages.Any(m => m.GetString("role", null) == "assistant" && m.GetString("content", null) == "The door opens a crack."));
            yield break;
        }

        [UnityTest]
        public IEnumerator TwoPass_BothCallsUseTheProfileModel()
        {
            TwoPass();
            _script.Enqueue(req => new Reply { Body = Plain("DONE") });
            _script.Enqueue(req => new Reply { Body = Plain("Rain.") });
            yield return Send("I wait.");
            Assert.AreEqual("fake-model", _chatRequests[0].GetString("model", null));
            Assert.AreEqual("fake-model", _chatRequests[1].GetString("model", null));
            Assert.AreEqual("fake-model", _driver.Turns[0].Model);
        }

        [UnityTest]
        public IEnumerator TwoPass_StopDuringStoryteller_IsQuick()
        {
            TwoPass();
            _script.Enqueue(req => new Reply { Body = Plain("DONE") });
            _script.Enqueue(req => new Reply { Body = Plain("too late"), DelayMs = 20000 });
            float started = Time.realtimeSinceStartup;
            bool cancelled = false;
            yield return Send("I hesitate.", delegate
            {
                if (!cancelled && _chatRequests.Count == 2 && Time.realtimeSinceStartup - started > 0.3f)
                {
                    cancelled = true;
                    _driver.Cancel();
                }
                return true;
            });
            Assert.IsTrue(cancelled);
            Assert.IsFalse(_driver.IsBusy);
            Assert.Less(Time.realtimeSinceStartup - started, 3f);
            Assert.IsTrue(_transcript.Segments.Any(s => s.Kind == SegmentKind.System && s.Text.StartsWith("Stopped")));
            Assert.IsFalse(_transcript.Segments.Any(s => s.Kind == SegmentKind.Narration && s.Text.Contains("too late")));
        }

        [UnityTest]
        public IEnumerator TwoPass_EmptyScene_IsReported()
        {
            TwoPass();
            _script.Enqueue(req => new Reply { Body = Plain("DONE") });
            _script.Enqueue(req => new Reply { Body = Plain("   ") });
            yield return Send("Hello?");
            Assert.IsTrue(_transcript.Segments.Any(s => s.Kind == SegmentKind.System && s.Text.Contains("empty scene")));
        }
    }
}
