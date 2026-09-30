using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Server;
using Object = UnityEngine.Object;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// N5 gate, the part a test can reach: ServerHostManager starts the staged
    /// server the way a shipped build does (unpack into a fresh data folder,
    /// Production env, loopback only), moves off a busy preferred port,
    /// reports the server version, and a second session stops a server the
    /// first one left running. The data folder is a temp dir, never the
    /// player's persistentDataPath.
    /// </summary>
    [Category("Integration")]
    public class EmbeddedHostTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private string _root;
        private TcpListener _blocker;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "vault-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
            {
                var host = go.GetComponent<ServerHostManager>();
                if (host != null) { host.StopEmbedded(); host.WaitForStopped(30000); }
                Object.DestroyImmediate(go);
            }
            _objects.Clear();
            if (_blocker != null) { _blocker.Stop(); }
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        private ServerHostManager NewHost(int port)
        {
            var go = new GameObject("EmbeddedHostTest");
            _objects.Add(go);
            var host = go.AddComponent<ServerHostManager>();
            host.Config = go.AddComponent<VaultClientConfig>();
            host.DataRoot = _root;
            host.Port = port;
            return host;
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator FreshDataFolder_BusyPort_StartsOnAnother_AndAnOrphanIsStoppedNextSession()
        {
            string rid = ServerHostManager.EmbeddedRid();
            string staged = Path.Combine(Application.streamingAssetsPath, "CampaignVault", "Server", rid ?? "none", ServerHostManager.ExeName(rid ?? string.Empty));
            if (rid == null || !File.Exists(staged)) { Assert.Ignore("No staged server for this platform (run tools/embed-server.sh)."); }

            _blocker = new TcpListener(IPAddress.Loopback, 0);
            _blocker.Start();
            int busy = ((IPEndPoint)_blocker.LocalEndpoint).Port;

            var first = NewHost(busy);
            var lines = new List<string>();
            bool ok = false;
            string message = string.Empty;
            yield return first.StartEmbedded(delegate (bool o, string m) { ok = o; message = m; }, lines.Add);
            Assert.IsTrue(ok, message + "\n" + string.Join("\n", lines.ToArray()));

            Assert.IsTrue(lines.Exists(l => l.StartsWith("Unpacking the server", StringComparison.Ordinal)), "first run shows unpack progress");
            Assert.IsTrue(lines.Exists(l => l.Contains("is in use")), "busy preferred port is reported");
            Assert.AreNotEqual(busy, first.ActivePort);
            Assert.AreEqual("http://127.0.0.1:" + first.ActivePort, first.Config.ServerUrl);
            string expected = ServerHostManager.ExpectedVersion();
            if (expected != null) { Assert.AreEqual(expected, first.ReportedVersion); }
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "CampaignData")), "campaign data lives under the data root");
            string pidFile = Path.Combine(_root, "Server", EmbeddedServerSupport.PidFileName);
            Assert.IsTrue(File.Exists(pidFile));
            string banner = ReadLog(rid);
            StringAssert.Contains("Environment: Production", banner);
            StringAssert.Contains("MCP Bind: localhost", banner);
            StringAssert.Contains("HTTPS Enabled: False", banner);

            // Simulate a crash: the first session never stops its server. The next session must clear it.
            var second = NewHost(busy);
            var secondLines = new List<string>();
            ok = false;
            yield return second.StartEmbedded(delegate (bool o, string m) { ok = o; message = m; }, secondLines.Add);
            Assert.IsTrue(ok, message + "\n" + string.Join("\n", secondLines.ToArray()));
            Assert.IsTrue(secondLines.Exists(l => l.StartsWith("Stopped a server", StringComparison.Ordinal)), string.Join("\n", secondLines.ToArray()));
            Assert.IsFalse(secondLines.Exists(l => l.StartsWith("Unpacking", StringComparison.Ordinal)), "an unchanged payload isn't copied again");
            Assert.IsFalse(first.IsRunning, "the orphan was stopped");

            second.StopEmbedded();
            Assert.IsFalse(second.IsRunning);
            Assert.IsTrue(second.WaitForStopped(30000), "the server shuts down within the grace period");
            Assert.IsFalse(File.Exists(pidFile), "a clean stop removes the pidfile");
        }

        /// <summary>server.log is held open for writing; read it shared.</summary>
        private string ReadLog(string rid)
        {
            using (var f = new FileStream(Path.Combine(_root, "Server", rid, "server.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(f)) { return r.ReadToEnd(); }
        }
    }
}
