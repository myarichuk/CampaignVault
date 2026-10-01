using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Settings;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>Settings and first-run setup as view models: tabs, the provider editor, switches that call the controller.</summary>
    public class SettingsViewModelTests
    {
        private GameObject _go;
        private VaultBootstrap _boot;
        private VaultAppState _s;
        private VaultController _c;
        private string _providers;
        private readonly List<string> _asked = new List<string>();

        [SetUp]
        public void SetUp()
        {
            // The player's real provider profiles are never read or written.
            _providers = Path.Combine(Path.GetTempPath(), "vault-providers-" + System.Guid.NewGuid().ToString("N") + ".json");
            ByokSettings.PathOverride = _providers;
            _go = new GameObject("SettingsViewModelTests");
            _boot = _go.AddComponent<VaultBootstrap>();
            _boot.Initialize(new MemoryPrefs());
            _s = _boot.State;
            _c = _boot.Controller;
            _asked.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
            ByokSettings.PathOverride = null;
            if (File.Exists(_providers)) { File.Delete(_providers); }
        }

        private SettingsViewModel Settings()
        {
            return new SettingsViewModel(_s, _c, delegate (string title, string m, string c, bool d, System.Action a) { _asked.Add(title); }, delegate { });
        }

        [Test]
        public void Tabs_ShowTheirPage_AndMarkTheOpenOne()
        {
            var settings = Settings();
            settings.Show(0);
            Assert.IsInstanceOf<ProviderFormViewModel>(settings.Page);
            Assert.IsTrue(settings.Tabs[0].Active);
            settings.Show(SettingsViewModel.PluginsTab);
            Assert.IsInstanceOf<PluginsPage>(settings.Page);
            Assert.IsTrue(settings.Tabs[SettingsViewModel.PluginsTab].Active);
            Assert.IsFalse(settings.Tabs[0].Active);
            settings.Show(99);
            Assert.AreEqual(SettingsViewModel.AdvancedTab, settings.Tab, "out of range clamps to the last tab");
            settings.Dispose();
        }

        [Test]
        public void ProviderForm_PresetFillsTheFields_ButARefreshNeverUndoesTyping()
        {
            var form = new ProviderFormViewModel(_s, _c);
            var preset = form.Presets.First(p => p.Name == "preset-ollama");
            preset.Pick();
            Assert.AreEqual("http://localhost:11434/v1", form.BaseUrl);
            Assert.IsTrue(preset.Selected);

            form.Model = "my-own-model";
            _s.Notify(StateArea.Providers);
            Assert.AreEqual("my-own-model", form.Model, "a refresh the form didn't cause is not a new profile");
            form.Dispose();
        }

        [Test]
        public void ProviderForm_Save_WritesTheProfile_AndNeverEchoesTheKey()
        {
            var form = new ProviderFormViewModel(_s, _c);
            form.Presets.First(p => p.Name == "preset-openai").Pick();
            form.Name = "Table DM";
            form.Model = "gpt-test";
            form.Temperature = "0.7";
            form.MaxTokens = "800";
            form.Efforts.First(e => e.Name == "effort-high").Pick();
            form.Stream = false;
            form.Key = "sk-test-1234";
            form.Save();

            var saved = _s.Byok.Active;
            Assert.AreEqual("Table DM", saved.Name);
            Assert.AreEqual("gpt-test", saved.Model);
            Assert.AreEqual(0.7f, saved.Temperature, 0.001f);
            Assert.AreEqual(800, saved.MaxTokens);
            Assert.AreEqual("high", saved.ReasoningEffort);
            Assert.IsTrue(saved.DisableStreaming);
            Assert.AreEqual("sk-test-1234", saved.ApiKey);
            Assert.AreEqual(string.Empty, form.Key, "the pasted key is cleared once saved");
            StringAssert.Contains("1234", form.KeyState);
            StringAssert.DoesNotContain("sk-test", form.KeyState);
            Assert.AreEqual("leaf", form.StatusTone);
            form.Dispose();
        }

        [Test]
        public void ProviderForm_NewProfile_AddsOne_AndDeleteIsOfferedOnlyWithSeveral()
        {
            var form = new ProviderFormViewModel(_s, _c);
            Assert.IsFalse(form.CanDelete);
            form.NewProfile();
            Assert.AreEqual(2, form.Profiles.Count);
            Assert.IsTrue(form.CanDelete);
            Assert.IsTrue(form.Profiles[1].Selected);
            form.Dispose();
        }

        [Test]
        public void ServerPage_TypedAddressSurvivesRefresh_AndConnectorFollowsTheChoice()
        {
            var page = new ServerPage(_s, _c);
            page.Url = "http://elsewhere:9000";
            _s.Notify(StateArea.Connection);
            Assert.AreEqual("http://elsewhere:9000", page.Url);

            page.Connectors[1].Pick();
            Assert.AreEqual("build", _s.Config.ActiveConnector());
            Assert.IsTrue(page.Connectors[1].Selected);
            Assert.IsFalse(page.Connectors[0].Selected);
            page.Dispose();
        }

        [Test]
        public void FeelPage_SwitchesCallTheController_AndFollowState()
        {
            var page = new FeelPage(_s, _c);
            Assert.IsTrue(page.Fx);
            page.Fx = false;
            Assert.IsFalse(_s.FxEnabled);
            _s.FxEnabled = true;
            _s.Notify(StateArea.Preferences);
            Assert.IsTrue(page.Fx, "a change made elsewhere shows here");

            page.Sound = false;
            Assert.IsTrue(_s.SfxMuted);
            VaultSfx.Muted = false;
            page.Dispose();
        }

        [Test]
        public void TextSize_StepsAndStopsAtTheEnds()
        {
            var size = new TextSizeViewModel(_s, _c);
            _c.SetStoryTextSize(0);
            Assert.IsFalse(size.CanSmaller);
            Assert.IsTrue(size.CanLarger);
            size.Larger();
            Assert.AreEqual(1, _s.StoryTextSize);
            StringAssert.StartsWith("Small", size.Label);
            size.Dispose();
        }

        [Test]
        public void AdvancedPage_GroupsToolsBySubject_AndASwitchTogglesTheTool()
        {
            _s.Tools.Add(new ToolToggle { Name = "take_turn", Connector = "play", Description = "x" });
            _s.Tools.Add(new ToolToggle { Name = "take_turn", Connector = "build", Description = "x" });
            _s.Tools.Add(new ToolToggle { Name = "lookup", Connector = "play", Description = "y" });
            var page = new AdvancedPage(_s, _c, delegate { });
            CollectionAssert.AreEqual(new[] { "AT THE TABLE", "KNOWLEDGE" }, page.Groups.Select(g => g.Title).ToArray());
            var turn = page.Groups[0].Tools.Single();
            Assert.IsTrue(turn.Enabled);
            StringAssert.Contains("/play, /build", turn.Tooltip);

            turn.Enabled = false;
            Assert.IsFalse(_s.IsToolEnabled("take_turn"));
            Assert.IsTrue(_s.IsToolEnabled("lookup"));
            Assert.AreSame(turn, page.Groups[0].Tools.Single(), "the row is kept, so the switch animates");
            page.Dispose();
        }

        [Test]
        public void Setup_WalksTheSteps_AndHoldsTheProviderStepUntilItWorks()
        {
            _s.Byok.Active.Preset = "openai";
            _s.Byok.Active.ApiKey = string.Empty;
            var closed = 0;
            var setup = new SetupViewModel(_s, _c, delegate { closed++; }, delegate { }, delegate { });
            setup.Go(0);
            Assert.IsFalse(setup.ShowBack);
            Assert.AreEqual("NEXT", setup.NextLabel);
            StringAssert.StartsWith("STEP 1 OF 3", setup.StepTitle);

            setup.Next();
            Assert.AreEqual(1, setup.Step);
            Assert.IsTrue(setup.ShowBack);
            Assert.IsFalse(setup.CanNext, "no key yet");

            _s.Byok.Active.ApiKey = "sk-x";
            _s.Notify(StateArea.Providers);
            Assert.IsTrue(setup.CanNext);

            setup.Next();
            Assert.AreEqual("TO THE TABLE", setup.NextLabel);
            setup.Next();
            Assert.AreEqual(1, closed);
            setup.Dispose();
        }
    }
}
