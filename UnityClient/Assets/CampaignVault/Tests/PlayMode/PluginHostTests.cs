using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Server;
using Object = UnityEngine.Object;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// N6 gate: on the embedded server, a data-only plugin installed from a zip
    /// is loaded after a restart; disabled, it is listed but not loaded after
    /// the next one. Temp data folder only.
    /// </summary>
    [Category("Integration")]
    public class PluginHostTests
    {
        private string _root;
        private GameObject _go;
        private ServerHostManager _host;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "vault-plugins-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _go = new GameObject("PluginHostTest");
            _host = _go.AddComponent<ServerHostManager>();
            _host.Config = _go.AddComponent<VaultClientConfig>();
            _host.DataRoot = _root;
            _host.Port = EmbeddedServerSupport.FreePort();
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) { _host.StopEmbedded(); _host.WaitForStopped(30000); }
            Object.DestroyImmediate(_go);
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        private IEnumerator StartServer()
        {
            bool ok = false;
            string message = string.Empty;
            var lines = new List<string>();
            yield return _host.StartEmbedded(delegate (bool o, string m) { ok = o; message = m; }, lines.Add);
            Assert.IsTrue(ok, message + "\n" + string.Join("\n", lines.ToArray()));
        }

        private IEnumerator Restart()
        {
            _host.StopEmbedded();
            yield return StartServer();
        }

        private IEnumerator Listing(List<PluginEntry> into)
        {
            bool ok = false;
            string body = string.Empty;
            yield return _host.Config.GetText("/plugins", delegate (bool o, string b) { ok = o; body = b; });
            Assert.IsTrue(ok, body);
            List<PluginEntry> plugins;
            string engine;
            Assert.IsTrue(PluginPackages.TryParseListing(body, out plugins, out engine), body);
            into.Clear();
            into.AddRange(plugins);
        }

        private static PluginEntry Find(List<PluginEntry> plugins, string id)
        {
            return plugins.Find(p => p.Id == id);
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator DataPlugin_FromZip_LoadsAfterRestart_AndDisablingUnloadsIt()
        {
            string rid = ServerHostManager.EmbeddedRid();
            string staged = Path.Combine(Application.streamingAssetsPath, "CampaignVault", "Server", rid ?? "none", ServerHostManager.ExeName(rid ?? string.Empty));
            if (rid == null || !File.Exists(staged)) { Assert.Ignore("No staged server for this platform (run tools/embed-server.sh)."); }

            const string id = "com.example.scratch-lanterns";
            yield return StartServer();
            var plugins = new List<PluginEntry>();
            yield return Listing(plugins);
            Assert.IsNull(Find(plugins, id));

            string zip = Path.Combine(_root, "lanterns.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Write(archive, "Lanterns/plugin.json", "{\"id\":\"" + id + "\",\"displayName\":\"Scratch Lanterns\",\"version\":\"1.0.0\",\"description\":\"A test lantern.\"}");
                Write(archive, "Lanterns/RulesetData/dnd5e/items/scratch_lantern.yaml", "name: Scratch Lantern\n");
            }
            PluginZipInfo info;
            string error;
            Assert.IsTrue(PluginPackages.Inspect(zip, out info, out error), error);
            PluginPackages.Install(info, _host.UserPluginsDir);

            yield return Restart();
            yield return Listing(plugins);
            var lanterns = Find(plugins, id);
            Assert.IsNotNull(lanterns, "installed plugin listed after restart");
            Assert.IsTrue(lanterns.Loaded, string.Join("; ", lanterns.Errors.ToArray()));
            Assert.IsTrue(lanterns.IsUser);
            Assert.IsFalse(lanterns.IsCode);
            Assert.AreEqual("Scratch Lanterns", lanterns.Name);

            PluginPackages.SetDisabled(_host.DisabledPluginsFile, id, true);
            yield return Restart();
            yield return Listing(plugins);
            lanterns = Find(plugins, id);
            Assert.IsNotNull(lanterns);
            Assert.IsFalse(lanterns.Enabled);
            Assert.IsFalse(lanterns.Loaded, "disabled plugin isn't loaded");
        }

        [UnityTest, Timeout(400000)]
        public IEnumerator Controller_RestartsServerByItself_AfterPluginChanges_OncePerBurst()
        {
            string rid = ServerHostManager.EmbeddedRid();
            string staged = Path.Combine(Application.streamingAssetsPath, "CampaignVault", "Server", rid ?? "none", ServerHostManager.ExeName(rid ?? string.Empty));
            if (rid == null || !File.Exists(staged)) { Assert.Ignore("No staged server for this platform (run tools/embed-server.sh)."); }

            const string id = "com.example.scratch-candles";
            string providers = Path.Combine(_root, "providers.json");
            ByokSettings.PathOverride = providers;
            var go = new GameObject("VaultClient");
            go.SetActive(false);
            var boot = go.AddComponent<VaultBootstrap>();
            boot.Initialize(new MemoryPrefs());
            go.SetActive(true);
            var state = boot.State;
            var controller = boot.Controller;
            var toasts = new List<string>();
            state.Toast += delegate (string m, ToastKind k) { toasts.Add(k + ": " + m); Debug.Log("[PluginTest] toast " + k + ": " + m); };
            try
            {
                state.Server.AutoStart = false;
                state.Server.DataRoot = Path.Combine(_root, "client");
                int port = EmbeddedServerSupport.FreePort();
                state.Config.ServerUrl = "http://127.0.0.1:" + port;
                controller.PluginRestartDelay = 0.3f;

                yield return controller.StartEmbedded(port);
                Assert.IsTrue(state.Server.IsRunning, state.EmbeddedMessage);
                Assert.IsTrue(controller.CanManagePlugins);
                yield return controller.LoadPlugins();

                string zip = Path.Combine(_root, "candles.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    Write(archive, "Candles/plugin.json", "{\"id\":\"" + id + "\",\"displayName\":\"Scratch Candles\",\"version\":\"1.0.0\"}");
                    Write(archive, "Candles/RulesetData/dnd5e/items/scratch_candle.yaml", "name: Scratch Candle\n");
                }
                var info = controller.CheckPluginZip(zip);
                Assert.IsNotNull(info, string.Join("\n", toasts.ToArray()));
                Assert.IsTrue(controller.InstallPlugin(info));
                Assert.IsTrue(state.PluginsRestartNeeded, "a running server needs a restart");

                yield return WaitRestarted(state, toasts, 1);
                var candles = state.Plugins.Find(p => p.Id == id);
                Assert.IsNotNull(candles, "listed after the automatic restart");
                Assert.IsTrue(candles.Loaded, string.Join("; ", candles.Errors.ToArray()));
                Assert.AreEqual(ConnectionStatus.Healthy, state.Connection, state.ConnectionMessage);

                // Three flips in quick succession: one restart, ending disabled.
                controller.SetPluginEnabled(id, false);
                controller.SetPluginEnabled(id, true);
                controller.SetPluginEnabled(id, false);
                yield return WaitRestarted(state, toasts, 2);
                candles = state.Plugins.Find(p => p.Id == id);
                Assert.IsNotNull(candles);
                Assert.IsFalse(candles.Loaded, "disabled plugin unloaded by the automatic restart");
                // Give a stray second restart the chance to show up before counting.
                float until = Time.realtimeSinceStartup + 2f;
                while (Time.realtimeSinceStartup < until) { yield return null; }
                Assert.AreEqual(2, toasts.FindAll(t => t.Contains("plugin changes applied")).Count, string.Join("\n", toasts.ToArray()));
            }
            finally
            {
                if (state.Server != null) { state.Server.StopEmbedded(); state.Server.WaitForStopped(30000); }
                Object.DestroyImmediate(go);
                ByokSettings.PathOverride = null;
            }
        }

        private static IEnumerator WaitRestarted(VaultAppState state, List<string> toasts, int restarts)
        {
            float until = Time.realtimeSinceStartup + 150f;
            while (Time.realtimeSinceStartup < until
                   && toasts.FindAll(t => t.Contains("plugin changes applied") || t.Contains("didn't") || t.Contains("still loaded") || t.Contains("failed")).Count < restarts)
            {
                yield return null;
            }
            Assert.AreEqual(restarts, toasts.FindAll(t => t.Contains("plugin changes applied")).Count, string.Join("\n", toasts.ToArray()));
            Assert.IsFalse(state.PluginsRestartNeeded);
        }

        private static void Write(ZipArchive archive, string name, string text)
        {
            using (var w = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false))) { w.Write(text); }
        }
    }
}
