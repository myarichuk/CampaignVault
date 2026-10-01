using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Tests;
using CampaignVault.UnityClient.UI;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// P7: the shell at awkward aspect ratios (breakpoints), and the story log
    /// under a long session. Offline: the server URL points at a dead port, so
    /// nothing here ever reads a real campaign.
    /// </summary>
    public class ShellLayoutTests
    {
        private GameObject _go;
        private RenderTexture _rt;
        private string _providers;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) { Object.Destroy(_go); }
            if (_rt != null) { _rt.Release(); }
            ByokSettings.PathOverride = null;
            if (_providers != null && File.Exists(_providers)) { File.Delete(_providers); }
        }

        private IEnumerator Host(int width, int height, Action<VaultClientUI, VaultAppState, VaultController> ready)
        {
            _providers = Path.Combine(Path.GetTempPath(), "vault-providers-" + Guid.NewGuid().ToString("N") + ".json");
            ByokSettings.PathOverride = _providers;
            _go = new GameObject("VaultClient");
            _go.SetActive(false);
            var boot = _go.AddComponent<VaultBootstrap>();
            boot.Initialize(new MemoryPrefs());
            var doc = _go.AddComponent<UIDocument>();
            var settings = Object.Instantiate(Resources.Load<PanelSettings>("VaultUI/VaultPanelSettings"));
            _rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            settings.targetTexture = _rt;
            doc.panelSettings = settings;
            var ui = _go.AddComponent<VaultClientUI>();
            _go.SetActive(true);
            var s = boot.State;
            s.Byok.Active.Preset = "ollama";
            s.Byok.BaseUrl = "http://127.0.0.1:" + TestPorts.Free() + "/v1";
            s.Byok.Model = "offline-dm";
            s.Server.AutoStart = false;
            s.Config.ServerUrl = "http://127.0.0.1:" + TestPorts.Free();
            yield return Settle(0.6f);
            while (ui.Overlays.CloseTop()) { }
            ready(ui, s, boot.Controller);
            yield return Settle(0.8f);
        }

        private static IEnumerator Settle(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until) { yield return null; }
        }

        private void Snap(string name)
        {
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

        /// <summary>A believable table without a server: campaign, session digest, a few story beats.</summary>
        private static void Stage(VaultAppState s, VaultController c)
        {
            c.SelectCampaign("the-sunken-crown", "Dnd5e");
            c.ApplySession(SessionDigest.FromResult(JsonValue.Parse(@"{
                ""sessionNumber"": 4, ""time"": ""Day 12, Month 3, Year 1492 (the flooded vale) — Dusk"", ""partyFingerprint"": ""fp"",
                ""campaign"": { ""slug"": ""the-sunken-crown"", ""displayName"": ""The Sunken Crown"", ""system"": ""Dnd5e"",
                    ""pcs"": [ { ""id"": ""chars/aric"", ""name"": ""Aric Thorne"" } ], ""companions"": [ { ""id"": ""chars/mirelle"", ""name"": ""Mirelle"" } ] },
                ""activeQuests"": [ { ""id"": ""q1"", ""title"": ""Find the miller before the new moon"", ""openObjectives"": 2, ""deadlineDay"": 14 },
                                     { ""id"": ""q2"", ""title"": ""Deliver the Warden's letter"", ""isOverdue"": true } ],
                ""party"": [
                    { ""id"": ""chars/aric"", ""name"": ""Aric Thorne"", ""isPc"": true, ""hp"": ""29/45"", ""classLevel"": ""Human Fighter 5"", ""locationId"": ""locations/old-mill"", ""conditions"": [""poisoned""], ""equipped"": [""Longsword"", ""Chain Mail""], ""carried"": [""Hempen Rope"", ""Tinderbox""] },
                    { ""id"": ""chars/mirelle"", ""name"": ""Mirelle"", ""hp"": ""7/31"", ""classLevel"": ""Elf Ranger 4"" } ]
            }")));
            SegmentSplitter.AddNarration(s.Transcript, "The mill wheel groans in the dark water. Flour lies spilled like **snow** across the boards.\n\nMirelle: “Those aren't rat tracks.”");
            foreach (var roll in SegmentSplitter.ExtractRolls("Search (Perception): Success. Rolled 17 vs DC 14.")) { s.Transcript.Add(roll); }
        }

        [UnityTest]
        public IEnumerator Breakpoints_NarrowAndCompact()
        {
            // 1100x1000 scales to ~1510 panel px wide: under the narrow breakpoint.
            yield return Host(1100, 1000, delegate (VaultClientUI ui, VaultAppState s, VaultController c) { Stage(s, c); });
            var shell = _go.GetComponent<UIDocument>().rootVisualElement.Q("Shell");
            Assert.IsTrue(shell.ClassListContains("cv-shell--narrow"), "narrow layout at 1100x1000");
            Assert.IsFalse(shell.ClassListContains("cv-shell--compact"));
            Snap("p7-narrow");
        }

        [UnityTest]
        public IEnumerator Breakpoints_Compact()
        {
            // 800x1200 scales to ~1176 panel px wide: compact (party collapses to crests).
            yield return Host(800, 1200, delegate (VaultClientUI ui, VaultAppState s, VaultController c) { Stage(s, c); });
            var shell = _go.GetComponent<UIDocument>().rootVisualElement.Q("Shell");
            Assert.IsTrue(shell.ClassListContains("cv-shell--compact"), "compact layout at 800x1200");
            Snap("p7-compact");
        }

        [UnityTest]
        public IEnumerator Wide_StagedTable()
        {
            yield return Host(1920, 1080, delegate (VaultClientUI ui, VaultAppState s, VaultController c) { Stage(s, c); });
            Snap("p7-wide");
        }

        [UnityTest]
        public IEnumerator StoryTextSize_MenuAndLargerText()
        {
            VaultClientUI host = null;
            yield return Host(1920, 1080, delegate (VaultClientUI ui, VaultAppState s, VaultController c) { Stage(s, c); c.SetStoryTextSize(4); host = ui; });
            host.ShowTextMenu(true);
            Assert.IsTrue(host.Root.ClassListContains("cv-story-4"));
            var narration = host.Root.Q<Label>(className: "cv-seg--narration");
            yield return Settle(0.3f);
            Assert.AreEqual(VaultAppState.StoryTextSizes[4], narration.resolvedStyle.fontSize, 0.5f, "the story text follows the setting");
            Snap("p7-textsize");
        }

        [UnityTest]
        public IEnumerator LongSession_LogStaysIncremental()
        {
            VaultClientUI host = null;
            VaultAppState state = null;
            yield return Host(1920, 1080, delegate (VaultClientUI ui, VaultAppState s, VaultController c) { host = ui; state = s; });
            var content = host.Log.ScrollView.Q<CampaignVault.UnityClient.UI.Controls.LiveRepeater>();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                state.Transcript.Add(new TranscriptSegment { Kind = i % 7 == 0 ? SegmentKind.ToolData : SegmentKind.Narration, Text = (i % 7 == 0 ? "⚙ take_turn " : "Beat ") + i + ". The lantern gutters." });
            }
            watch.Stop();
            yield return Settle(0.5f);
            Debug.Log("[ShellLayout] 1000 appends past the cap took " + watch.ElapsedMilliseconds + " ms");
            Assert.AreEqual(VaultTranscript.MaxSegments, state.Transcript.Segments.Count);
            Assert.AreEqual(state.Transcript.Segments.Count, host.Log.EntryCount, "one entry per segment, trimmed in step");
            Assert.LessOrEqual(content.childCount, VaultTranscript.MaxSegments, "no orphaned elements");
            Assert.AreEqual(host.Log.ViewModel.Stories.Count, content.childCount, "one element per item, patched in step");
            Assert.Less(watch.ElapsedMilliseconds, 1500, "appending stays cheap past the cap (no rebuilds)");
        }
    }
}
