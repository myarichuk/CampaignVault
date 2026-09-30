using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.Tests
{
    public class AppLayerTests
    {
        private GameObject _go;
        private VaultBootstrap _boot;
        private MemoryPrefs _prefs;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("AppLayerTest");
            _boot = _go.AddComponent<VaultBootstrap>();
            _prefs = new MemoryPrefs();
            _boot.Initialize(_prefs);
        }

        [TearDown]
        public void TearDown() { Object.DestroyImmediate(_go); }

        private static JsonValue Parse(string json)
        {
            JsonValue v;
            Assert.IsTrue(JsonValue.TryParse(json, out v));
            return v;
        }

        [Test]
        public void Slugify_MatchesServerRule()
        {
            Assert.AreEqual("dragon-heist", VaultController.Slugify("  Dragon Heist! "));
            Assert.AreEqual("a-b-2", VaultController.Slugify("A -- b 2"));
            Assert.AreEqual(string.Empty, VaultController.Slugify("!!!"));
        }

        [Test]
        public void ParseQuestion_AnswerTypes()
        {
            var choice = VaultController.ParseQuestion(Parse("{\"key\":\"system\",\"text\":\"Rules?\",\"answerType\":1,\"enumOptions\":[\"Dnd5e\",\"Narrative\"]}"));
            Assert.AreEqual(AnswerType.Choice, choice.Type);
            CollectionAssert.AreEqual(new[] { "Dnd5e", "Narrative" }, choice.Options);
            Assert.AreEqual(AnswerType.YesNo, VaultController.ParseQuestion(Parse("{\"AnswerType\":\"Boolean\"}")).Type);
            Assert.AreEqual(AnswerType.List, VaultController.ParseQuestion(Parse("{\"answerType\":\"StringList\"}")).Type);
            // A choice with no options falls back to free text rather than an empty picker.
            Assert.AreEqual(AnswerType.Text, VaultController.ParseQuestion(Parse("{\"answerType\":1}")).Type);
        }

        [Test]
        public void ExtractCampaigns_SkipsRowsWithoutSlug()
        {
            var rows = VaultController.ExtractCampaigns(Parse("[{\"name\":\"a\",\"displayName\":\"A\",\"system\":\"Dnd5e\"},{\"displayName\":\"nameless\"}]"));
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("A", rows[0].Display);
        }

        [Test]
        public void SelectCampaign_ResetsScopedStateAndPersists()
        {
            var s = _boot.State;
            s.PcId = "chars/old";
            s.CompanionIds.Add("chars/pet");
            s.Session = new SessionDigest();
            StateArea seen = StateArea.None;
            s.Changed += delegate (StateArea a) { seen |= a; };

            _boot.Controller.SelectCampaign("new-table", "Dnd5e");

            Assert.AreEqual("new-table", s.CampaignSlug);
            Assert.AreEqual("Dnd5e", s.Ruleset);
            Assert.IsNull(s.Session);
            Assert.AreEqual(string.Empty, s.PcId);
            Assert.AreEqual(0, s.CompanionIds.Count);
            Assert.AreEqual("new-table", _prefs.GetString(PrefKeys.Campaign, null));
            Assert.IsTrue((seen & StateArea.Campaign) != 0);
            Assert.AreEqual(SegmentKind.System, s.Transcript.Segments[s.Transcript.Segments.Count - 1].Kind);
        }

        [Test]
        public void ApplySession_PicksPcTracksCompanionsFillsPrompt()
        {
            var s = _boot.State;
            var digest = SessionDigest.FromResult(Parse(@"{""partyFingerprint"":""fp"",
                ""campaign"":{""system"":""Pathfinder2e"",""pcs"":[{""id"":""chars/ari"",""name"":""Ari""}],""companions"":[{""id"":""chars/bo"",""name"":""Bo""}]}}"));
            _boot.Controller.ApplySession(digest);
            Assert.AreEqual("chars/ari", s.PcId);
            CollectionAssert.Contains(s.CompanionIds, "chars/bo");
            StringAssert.Contains("Ari", s.Prompts.PartyLine);
            Assert.AreEqual("fp", s.Prompts.PartyFingerprint);
            Assert.AreEqual("Pathfinder2e", s.Ruleset);
            Assert.AreEqual("chars/bo", _prefs.GetString(PrefKeys.Companions, null));
        }

        [Test]
        public void ToggleTool_SeedsAllowlistThenDisablesOne()
        {
            var s = _boot.State;
            s.Tools.Add(new ToolToggle { Name = "a" });
            s.Tools.Add(new ToolToggle { Name = "b" });
            Assert.IsTrue(s.IsToolEnabled("a"));
            _boot.Controller.ToggleTool("a");
            Assert.IsFalse(s.IsToolEnabled("a"));
            Assert.IsTrue(s.IsToolEnabled("b"));
            _boot.Controller.EnableAllTools();
            Assert.IsTrue(s.IsToolEnabled("a"));
        }

        [Test]
        public void SendWithoutProvider_RequestsSetup_AndAddsNothing()
        {
            var s = _boot.State;
            s.Byok.Active.Preset = "openai";
            s.Byok.Active.ApiKey = string.Empty;
            bool setup = false;
            string toast = null;
            s.SetupRequested += delegate { setup = true; };
            s.Toast += delegate (string m, ToastKind k) { toast = m; };
            int before = s.Transcript.Segments.Count;
            Assert.IsFalse(_boot.Controller.SendPlayerText("hello"));
            Assert.IsTrue(setup);
            StringAssert.Contains("AI provider", toast);
            Assert.AreEqual(before, s.Transcript.Segments.Count);
        }

        [Test]
        public void PluginExpectations_ReportWhatDidNotLand()
        {
            var loaded = new PluginEntry { Id = "a", Name = "Lanterns", Loaded = true };
            var broken = new PluginEntry { Id = "b", Name = "Candles", Loaded = false };
            broken.Errors.Add("bad yaml");
            var listed = new List<PluginEntry> { loaded, broken };

            Assert.IsNull(VaultController.PluginExpectationProblems(new Dictionary<string, bool> { { "a", true }, { "gone", false } }, listed));
            StringAssert.Contains("Candles didn't load: bad yaml", VaultController.PluginExpectationProblems(new Dictionary<string, bool> { { "b", true } }, listed));
            StringAssert.Contains("Lanterns is still loaded", VaultController.PluginExpectationProblems(new Dictionary<string, bool> { { "a", false } }, listed));
            StringAssert.Contains("x didn't load: the server didn't find it", VaultController.PluginExpectationProblems(new Dictionary<string, bool> { { "x", true } }, listed));
        }
    }
}
