using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Diagnostics;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Server;
using Debug = UnityEngine.Debug;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// The P2 gate: the whole smoke scenario, headless through the app layer,
    /// against the real embedded server binary on a throwaway scratch DB (never
    /// the player's data), with a scripted chat mock for the two DM turns.
    /// Skipped when no server is staged for this platform.
    /// </summary>
    [Category("Integration")]
    public class SmokeScenarioTests
    {
        private Process _server;
        private readonly StringBuilder _serverLog = new StringBuilder();
        private string _scratch;
        private HttpListener _llm;
        private volatile bool _stopping;
        private GameObject _go;
        private string _serverDir;
        private System.Collections.Generic.HashSet<string> _serverDirBefore;

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        [TearDown]
        public void TearDown()
        {
            _stopping = true;
            // Graceful: a plain Kill leaves RavenDB running for a while, holding the scratch DB.
            if (_server != null && !_server.HasExited) { EmbeddedServerSupport.BeginStop(_server); EmbeddedServerSupport.FinishStop(_server, 15000); }
            if (_llm != null) { try { _llm.Stop(); _llm.Close(); } catch (Exception) { } }
            if (_go != null) { UnityEngine.Object.DestroyImmediate(_go); }
            if (_scratch != null && Directory.Exists(_scratch)) { try { Directory.Delete(_scratch, true); } catch (Exception) { } }
            // The server runs in place (as README's smoke recipe does): anything
            // it wrote under StreamingAssets during the run goes, so Unity never
            // imports it. Files that were there before are never touched.
            if (_serverDirBefore != null)
            {
                foreach (string path in Directory.GetFileSystemEntries(_serverDir, "*", SearchOption.AllDirectories))
                {
                    if (_serverDirBefore.Contains(path)) { continue; }
                    Debug.LogWarning("[VaultSmoke] server left " + path + " behind; removing it.");
                    try
                    {
                        if (Directory.Exists(path)) { Directory.Delete(path, true); }
                        else if (File.Exists(path)) { File.Delete(path); }
                    }
                    catch (Exception) { }
                }
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator FullScenario_Headless_AgainstScratchServer()
        {
            string rid = ServerHostManager.EmbeddedRid();
            string serverDir = string.IsNullOrEmpty(rid) ? null : Path.Combine(Application.streamingAssetsPath, "CampaignVault", "Server", rid);
            if (serverDir == null || !File.Exists(Path.Combine(serverDir, ServerHostManager.ExeName(rid))))
            {
                Assert.Ignore("No embedded server staged for this platform.");
            }

            _serverDir = serverDir;
            _serverDirBefore = new System.Collections.Generic.HashSet<string>(Directory.GetFileSystemEntries(serverDir, "*", SearchOption.AllDirectories));
            _scratch = Path.Combine(Path.GetTempPath(), "vault-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            int port = FreePort();
            string serverUrl = "http://127.0.0.1:" + port;
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(serverDir, ServerHostManager.ExeName(rid)),
                WorkingDirectory = serverDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.EnvironmentVariables["MCP_PORT"] = port.ToString();
            start.EnvironmentVariables["MCP_BIND_ANY"] = string.Empty;
            start.EnvironmentVariables["BEARER_TOKEN"] = string.Empty;
            start.EnvironmentVariables["CAMPAIGN_DB_PATH"] = Path.Combine(_scratch, "db");
            _server = new Process { StartInfo = start };
            _server.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e) { lock (_serverLog) { _serverLog.AppendLine(e.Data); } };
            _server.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e) { lock (_serverLog) { _serverLog.AppendLine(e.Data); } };
            _server.Start();
            _server.BeginOutputReadLine();
            _server.BeginErrorReadLine();

            bool healthy = false;
            float until = Time.realtimeSinceStartup + 90f;
            while (!healthy && Time.realtimeSinceStartup < until && !_server.HasExited)
            {
                using (var probe = UnityWebRequest.Get(serverUrl + "/health"))
                {
                    probe.timeout = 2;
                    var op = probe.SendWebRequest();
                    while (!op.isDone) { yield return null; }
                    healthy = probe.result == UnityWebRequest.Result.Success;
                }
                if (!healthy)
                {
                    float wait = Time.realtimeSinceStartup + 0.5f;
                    while (Time.realtimeSinceStartup < wait) { yield return null; }
                }
            }
            Assert.IsTrue(healthy, "embedded server never became healthy:\n" + ServerLogTail());

            string llmUrl = StartChatMock();

            _go = new GameObject("SmokeBootstrap");
            var bootstrap = _go.AddComponent<VaultBootstrap>();
            bootstrap.Initialize(new MemoryPrefs());
            var scenario = new VaultSmokeScenario(bootstrap.State, bootstrap.Controller);
            scenario.Log = delegate (string line) { Debug.Log("[VaultSmoke] " + line); };
            yield return EditModeCoroutines.Drive(scenario.Run(serverUrl, llmUrl));

            string report = string.Join("\n", scenario.Lines.ToArray());
            Assert.AreEqual(0, scenario.Failed, report + "\n--- server log tail ---\n" + ServerLogTail());
            Assert.Greater(scenario.Passed, 12, report);
        }

        private string ServerLogTail()
        {
            lock (_serverLog)
            {
                string all = _serverLog.ToString();
                return all.Length > 3000 ? all.Substring(all.Length - 3000) : all;
            }
        }

        // ---- scripted chat mock (the "smoke-mock" the scenario expects) ----

        private static readonly Regex CampaignInPrompt = new Regex("campaignName=\"([^\"]*)\"");

        private string StartChatMock()
        {
            int port = FreePort();
            _llm = new HttpListener();
            _llm.Prefixes.Add("http://127.0.0.1:" + port + "/");
            _llm.Start();
            var thread = new Thread(delegate ()
            {
                while (!_stopping)
                {
                    HttpListenerContext ctx;
                    try { ctx = _llm.GetContext(); }
                    catch (Exception) { return; }
                    try { Answer(ctx); }
                    catch (Exception) { }
                }
            }) { IsBackground = true };
            thread.Start();
            return "http://127.0.0.1:" + port + "/v1";
        }

        // What the last bookkeeping request showed about compaction; the
        // storyteller's reply reports it so the scenario can check it.
        private static volatile bool _loopSawCompaction;
        private static volatile bool _storytellerSawJson;

        /// <summary>
        /// Two-pass turns. Loop (requests with tools): turn 1 calls load_skill +
        /// search_world, then says DONE; turn 2 records whether turn 1's tool
        /// results arrived compacted and says DONE. Storyteller (no tools):
        /// prose that reports what the mock saw.
        /// </summary>
        private static void Answer(HttpListenerContext ctx)
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) { body = reader.ReadToEnd(); }
            JsonValue request;
            JsonValue.TryParse(body, out request);
            var messages = request.GetArray("messages");
            int users = messages.Count(m => m.GetString("role", string.Empty) == "user");
            var last = messages.Count > 0 ? messages[messages.Count - 1] : JsonValue.Null;
            bool storyteller = request.Get("tools").Kind != JsonKind.Array;
            string reply;
            if (storyteller)
            {
                _storytellerSawJson |= messages.Any(m => m.GetString("role", string.Empty) == "tool")
                    || last.GetString("content", string.Empty).Contains("{\"");
                string clean = _storytellerSawJson ? " (storyteller saw JSON)" : " (clean brief)";
                reply = users == 1
                    ? Prose("The common room of the Smoke Inn is quiet tonight. (smoke-mock)" + clean)
                    : Prose((_loopSawCompaction ? "The barkeep pours. (history compacted)" : "The barkeep pours. (history NOT compacted)") + clean);
            }
            else if (users == 1 && last.GetString("role", string.Empty) == "user")
            {
                _loopSawCompaction = false;
                _storytellerSawJson = false;
                var match = CampaignInPrompt.Match(messages[0].GetString("content", string.Empty));
                string slug = match.Success ? match.Groups[1].Value : string.Empty;
                reply = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":["
                    + "{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"load_skill\",\"arguments\":\"{\\\"name\\\":\\\"dnd-exploration\\\"}\"}},"
                    + "{\"id\":\"c2\",\"type\":\"function\",\"function\":{\"name\":\"search_world\",\"arguments\":"
                    + JsonValue.FromString("{\"campaignName\":\"" + slug + "\",\"query\":\"inn\"}").ToJson() + "}}]}}]}";
            }
            else
            {
                if (users > 1 && last.GetString("role", string.Empty) == "user")
                {
                    _loopSawCompaction = messages.Any(m => m.GetString("role", string.Empty) == "tool"
                        && m.GetString("content", string.Empty).StartsWith("[earlier", StringComparison.Ordinal));
                }
                reply = Prose("DONE");
            }
            byte[] bytes = Encoding.UTF8.GetBytes(reply);
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private static string Prose(string text)
        {
            return "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + JsonValue.FromString(text).ToJson() + "}}]}";
        }
    }
}
