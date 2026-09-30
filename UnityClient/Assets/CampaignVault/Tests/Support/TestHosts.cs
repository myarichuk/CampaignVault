using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Server;
using Debug = UnityEngine.Debug;

namespace CampaignVault.UnityClient.Tests
{
    public static class TestPorts
    {
        public static int Free()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    /// <summary>
    /// The real embedded server binary on a throwaway DB under the temp dir
    /// (never the player's campaigns). Runs in place, as the README's smoke
    /// recipe does; anything it writes under StreamingAssets is removed on
    /// Dispose. Available is false when no server is staged for this platform.
    /// </summary>
    public sealed class ScratchServer : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _log = new StringBuilder();
        private readonly string _scratch;
        private readonly string _dir;
        private readonly HashSet<string> _before;

        public bool Available { get; private set; }
        public bool Healthy { get; private set; }
        public string Url { get; private set; }

        public ScratchServer()
        {
            string rid = ServerHostManager.EmbeddedRid();
            _dir = string.IsNullOrEmpty(rid) ? null : Path.Combine(Application.streamingAssetsPath, "CampaignVault", "Server", rid);
            if (_dir == null || !File.Exists(Path.Combine(_dir, ServerHostManager.ExeName(rid)))) { return; }
            Available = true;
            _before = new HashSet<string>(Directory.GetFileSystemEntries(_dir, "*", SearchOption.AllDirectories));
            _scratch = Path.Combine(Path.GetTempPath(), "vault-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            int port = TestPorts.Free();
            Url = "http://127.0.0.1:" + port;
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(_dir, ServerHostManager.ExeName(rid)),
                WorkingDirectory = _dir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.EnvironmentVariables["MCP_PORT"] = port.ToString();
            start.EnvironmentVariables["GRPC_PORT"] = TestPorts.Free().ToString();
            start.EnvironmentVariables["MCP_BIND_ANY"] = string.Empty;
            start.EnvironmentVariables["BEARER_TOKEN"] = string.Empty;
            start.EnvironmentVariables["CAMPAIGN_DB_PATH"] = Path.Combine(_scratch, "db");
            // Only the bundled plugins, whatever the developer's shell has set.
            start.EnvironmentVariables["CAMPAIGN_PLUGIN_DIRS"] = string.Empty;
            start.EnvironmentVariables["CAMPAIGN_PLUGINS_DISABLED"] = string.Empty;
            _process = new Process { StartInfo = start };
            _process.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { lock (_log) { _log.AppendLine(e.Data); } };
            _process.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { lock (_log) { _log.AppendLine(e.Data); } };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public IEnumerator WaitHealthy(float seconds = 90f)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!Healthy && Time.realtimeSinceStartup < until && !_process.HasExited)
            {
                using (var probe = UnityWebRequest.Get(Url + "/health"))
                {
                    probe.timeout = 2;
                    var op = probe.SendWebRequest();
                    while (!op.isDone) { yield return null; }
                    Healthy = probe.result == UnityWebRequest.Result.Success;
                }
                if (!Healthy)
                {
                    float wait = Time.realtimeSinceStartup + 0.5f;
                    while (Time.realtimeSinceStartup < wait) { yield return null; }
                }
            }
        }

        public string LogTail()
        {
            lock (_log)
            {
                string all = _log.ToString();
                return all.Length > 3000 ? all.Substring(all.Length - 3000) : all;
            }
        }

        public void Dispose()
        {
            // Graceful, like the client: a plain Kill leaves RavenDB running for a while, holding the scratch DB.
            if (_process != null && !_process.HasExited) { EmbeddedServerSupport.BeginStop(_process); EmbeddedServerSupport.FinishStop(_process, 15000); }
            if (_scratch != null && Directory.Exists(_scratch)) { try { Directory.Delete(_scratch, true); } catch (Exception) { } }
            if (_before == null) { return; }
            foreach (string path in Directory.GetFileSystemEntries(_dir, "*", SearchOption.AllDirectories))
            {
                if (_before.Contains(path)) { continue; }
                Debug.LogWarning("[VaultTest] server left " + path + " behind; removing it.");
                try
                {
                    if (Directory.Exists(path)) { Directory.Delete(path, true); }
                    else if (File.Exists(path)) { File.Delete(path); }
                }
                catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// An OpenAI-style chat endpoint on loopback that answers from a script:
    /// each request is handed to Respond, which returns (status, body, delayMs).
    /// Bodies are plain JSON; the driver accepts a non-SSE 200 to a stream request.
    /// </summary>
    public sealed class ScriptedChat : IDisposable
    {
        public sealed class Reply
        {
            public int Status = 200;
            public string Body = "{}";
            public int DelayMs;
        }

        private readonly HttpListener _listener;
        private volatile bool _stopping;
        public readonly List<JsonValue> Requests = new List<JsonValue>();
        public Func<JsonValue, int, Reply> Respond;
        public string BaseUrl { get; private set; }

        public ScriptedChat()
        {
            int port = TestPorts.Free();
            BaseUrl = "http://127.0.0.1:" + port + "/v1";
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            _listener.Start();
            var thread = new Thread(Serve) { IsBackground = true };
            thread.Start();
        }

        public static string Prose(string text)
        {
            return "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + JsonValue.FromString(text).ToJson() + "}}]}";
        }

        public static string ToolCall(string id, string name, string argumentsJson)
        {
            return "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"" + id
                + "\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":" + JsonValue.FromString(argumentsJson).ToJson() + "}}]}}]}";
        }

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
                JsonValue request;
                JsonValue.TryParse(body, out request);
                int index;
                lock (Requests) { Requests.Add(request); index = Requests.Count - 1; }
                var reply = Respond != null ? Respond(request, index) : new Reply { Status = 500, Body = "{\"error\":{\"message\":\"no script\"}}" };
                if (reply.DelayMs > 0) { Thread.Sleep(reply.DelayMs); }
                if (_stopping) { return; }
                byte[] bytes = Encoding.UTF8.GetBytes(reply.Body ?? string.Empty);
                ctx.Response.StatusCode = reply.Status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception) { /* the client aborted (Stop): expected */ }
        }

        public void Dispose()
        {
            _stopping = true;
            try { _listener.Stop(); _listener.Close(); } catch (Exception) { }
        }
    }
}
