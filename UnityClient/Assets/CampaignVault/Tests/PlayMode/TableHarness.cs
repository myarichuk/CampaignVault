using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Tests;
using CampaignVault.UnityClient.UI;
using Object = UnityEngine.Object;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// The real UI rendering into a 1920x1080 texture, against the embedded
    /// server on a scratch DB and a scripted Dungeon Master. Provider file,
    /// prefs and app data all live under temp paths; Dispose removes them.
    /// </summary>
    public sealed class TableHarness : IDisposable
    {
        public ScratchServer Server { get; private set; }
        public ScriptedChat Chat { get; private set; }
        public VaultClientUI Ui { get; private set; }
        public VaultAppState State { get; private set; }
        public VaultController Controller { get; private set; }

        private GameObject _go;
        private RenderTexture _rt;
        private string _providers;
        private string _dataRoot;

        public static void Step(string what) { Debug.Log("[TableTest] " + what); }

        public static IEnumerator WaitFor(Func<bool> condition, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < until) { yield return null; }
        }

        public static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) { yield return null; } }

        /// <summary>Starts the server and hosts the UI. Ignores the test when no server is staged.</summary>
        public IEnumerator Start()
        {
            Server = new ScratchServer();
            if (!Server.Available) { Assert.Ignore("No embedded server staged for this platform."); }
            yield return Server.WaitHealthy();
            Assert.IsTrue(Server.Healthy, "server never became healthy:\n" + Server.LogTail());
            Step("server healthy");
            Chat = new ScriptedChat();
            _providers = Path.Combine(Path.GetTempPath(), "vault-providers-" + Guid.NewGuid().ToString("N") + ".json");
            ByokSettings.PathOverride = _providers;

            _go = new GameObject("VaultClient");
            _go.SetActive(false);
            var boot = _go.AddComponent<VaultBootstrap>();
            boot.Initialize(new MemoryPrefs());
            var doc = _go.AddComponent<UIDocument>();
            var settings = Object.Instantiate(Resources.Load<PanelSettings>("VaultUI/VaultPanelSettings"));
            _rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            settings.targetTexture = _rt;
            doc.panelSettings = settings;
            Ui = _go.AddComponent<VaultClientUI>();
            State = boot.State;
            Controller = boot.Controller;
            State.Toast += delegate (string m, ToastKind k) { Step("toast " + k + ": " + m); };
            _go.SetActive(true);
            // After Awake (ByokSettings loads its file there), before Start.
            State.Byok.Active.Preset = "ollama";
            State.Byok.BaseUrl = Chat.BaseUrl;
            State.Byok.Model = "scripted-dm";
            State.Server.AutoStart = false;
            // License and plugin folders under a temp root, never the player's app data.
            _dataRoot = Path.Combine(Path.GetTempPath(), "vault-table-" + Guid.NewGuid().ToString("N"));
            State.Server.DataRoot = _dataRoot;
            State.Config.ServerUrl = Server.Url;

            yield return Frames(2);
            Assert.IsNotNull(Ui.Root, "shell never built");
            yield return WaitFor(delegate { return State.Connection == ConnectionStatus.Healthy; }, 20f);
            Assert.AreEqual(ConnectionStatus.Healthy, State.Connection, State.ConnectionMessage);
        }

        /// <summary>Waits out transitions and font atlas fills, then saves Library/VaultSnapshots/name.png.</summary>
        public IEnumerator Snap(string name)
        {
            Step("snap " + name);
            // Real time, not frames: batchmode renders uncapped, so a frame count
            // is microseconds and every 200ms transition would be caught mid-way.
            // (And no WaitForEndOfFrame: it never resumes in batchmode.)
            float until = Time.realtimeSinceStartup + 0.8f;
            while (Time.realtimeSinceStartup < until) { yield return null; }
            var previous = RenderTexture.active;
            RenderTexture.active = _rt;
            var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            Directory.CreateDirectory(UiSnapshotTests.SnapshotDir);
            File.WriteAllBytes(Path.Combine(UiSnapshotTests.SnapshotDir, name + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex);
        }

        /// <summary>A tool call that must succeed; the payload lands in done.</summary>
        public IEnumerator Call(string connector, string tool, JsonValue args, Action<ToolPayload> done)
        {
            McpOutcome<ToolPayload> outcome = null;
            yield return State.Mcp.CallToolData(State.Config, connector, tool, args, delegate (McpOutcome<ToolPayload> o) { outcome = o; });
            Assert.IsNotNull(outcome, tool + ": no response");
            Assert.IsTrue(outcome.Ok, tool + ": " + outcome.ErrorMessage);
            if (done != null) { done(outcome.Data); }
        }

        public void Dispose()
        {
            if (_go != null) { Object.Destroy(_go); }
            if (_rt != null) { _rt.Release(); }
            if (Chat != null) { Chat.Dispose(); }
            if (Server != null) { Server.Dispose(); }
            ByokSettings.PathOverride = null;
            if (_providers != null && File.Exists(_providers)) { File.Delete(_providers); }
            if (_dataRoot != null) { try { Directory.Delete(_dataRoot, true); } catch (IOException) { } }
        }
    }
}
