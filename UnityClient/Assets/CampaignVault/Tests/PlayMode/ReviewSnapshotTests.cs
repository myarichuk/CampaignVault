using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.UI.Controls;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// Photographs the screens the homebrew and named-power work added, for review in Library/VaultSnapshots: the
    /// homebrew chip on a subclass, the optional Deity step, the PF2e cleric's Divine calling (with and without a deity)
    /// and the PF2e level-up menu. Scratch server, scratch campaigns, deleted after.
    /// </summary>
    [Category("Integration")]
    public class ReviewSnapshotTests
    {
        private TableHarness _h;

        [TearDown]
        public void TearDown()
        {
            if (_h != null) { _h.Dispose(); }
        }

        private static JsonValue Args(string slug)
        {
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(slug);
            return args;
        }

        private static string Fixture(string name)
        {
            return File.ReadAllText(Path.Combine(Application.dataPath, "CampaignVault", "Tests", "PlayMode", "Fixtures", name));
        }

        private static JsonValue Homebrew(string kind, string system, string yaml)
        {
            var o = JsonValue.NewObject();
            o.ObjectValue["kind"] = JsonValue.FromString(kind);
            o.ObjectValue["system"] = JsonValue.FromString(system);
            o.ObjectValue["yaml"] = JsonValue.FromString(yaml);
            return o;
        }

        private IEnumerator Table(string slug, string system, string fixture, params JsonValue[] homebrew)
        {
            var create = Args(slug);
            create.ObjectValue["name"] = JsonValue.FromString(slug);
            create.ObjectValue.Remove("campaignName");
            create.ObjectValue["initialSystem"] = JsonValue.FromString(system);
            yield return _h.Call("build", "create_campaign", create, null);
            var batch = JsonValue.Parse(Fixture(fixture));
            var list = JsonValue.NewArray();
            foreach (var h in homebrew) { list.ArrayValue.Add(h); }
            batch.ObjectValue["homebrew"] = list;
            var build = Args(slug);
            build.ObjectValue["batch"] = batch;
            yield return _h.Call("build", "world_build", build, null);
            _h.Controller.SelectCampaign(slug, system);
            while (_h.Ui.Overlays.CloseTop()) { }
        }

        private IEnumerator Settle()
        {
            var b = _h.State.Builder;
            yield return TableHarness.WaitFor(delegate { return b.PreviewCurrent && !_h.State.IsBusy("builder-commit"); }, 20f);
            yield return TableHarness.Frames(8);
        }

        private IEnumerator Go(string key)
        {
            _h.Controller.BuilderGoTo(key);
            yield return Settle();
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator HomebrewChip_Deity_DivineCalling_AndThePf2eLevelUpMenu_ArePhotographed()
        {
            _h = new TableHarness();
            yield return _h.Start();
            yield return TableHarness.WaitFor(delegate { return _h.Ui.Overlays.AnyOpen && _h.State.CampaignsLoaded; }, 10f);
            while (_h.Ui.Overlays.CloseTop()) { }
            var s = _h.State;
            var c = _h.Controller;
            var ui = _h.Ui;

            // ---- 5e: a homebrew subclass and a homebrew deity ----
            string five = "review-dnd5e";
            yield return Table(five, "Dnd5e", "levelup-dnd5e.json",
                Homebrew("classOption", "dnd5e", "name: ember_knight\nclass: fighter\nlabel: Ember Knight\ndescription: A knight of the kindled blade.\nfeatures:\n  3:\n    - { name: Kindled Blade, description: The blade burns. }\n"),
                Homebrew("power", "dnd5e", "name: ash_mother\ntype: deity\nlabel: The Ash Mother\nclasses: [cleric]\noffers: [light]\n"));
            ui.OpenBuilder("pc");
            yield return TableHarness.WaitFor(delegate { return s.Builder.Steps.Count > 0; }, 20f);
            yield return c.SetBuilderLevel(3);
            yield return c.BuilderChoose("race", JsonValue.FromString("human"));
            yield return c.BuilderChoose("class", JsonValue.FromString("fighter"));
            yield return Go("levels");
            Assert.IsTrue(s.Builder.Options.ContainsKey("levels"));
            yield return _h.Snap("review-5e-homebrew-subclass");
            yield return c.BuilderChoose("class", JsonValue.FromString("cleric"));
            Debug.Log("[Review] cleric steps: " + string.Join(", ", s.Builder.Steps.ConvertAll(x => x.Key).ToArray()));
            yield return Go("deity");
            yield return _h.Snap("review-5e-deity-step");
            yield return c.BuilderChoose("class", JsonValue.FromString("warlock"));
            Debug.Log("[Review] warlock steps: " + string.Join(", ", s.Builder.Steps.ConvertAll(x => x.Key).ToArray()));
            if (s.Builder.Step("patron") != null) { yield return Go("patron"); yield return _h.Snap("review-5e-patron-step"); }
            while (ui.Overlays.CloseTop()) { }
            yield return c.DeleteCampaign(five);

            // ---- PF2e: Divine calling with and without a deity, and the level-up menu ----
            string pf = "review-pf2e";
            yield return Table(pf, "Pf2e", "n7-pf2e.json",
                Homebrew("power", "pf2e", "name: ash_mother\ntype: deity\nlabel: The Ash Mother\nclasses: [cleric]\nnarrows: { domain: [sun, truth, healing], font: [healingFont] }\n"));
            ui.OpenBuilder("pc");
            yield return TableHarness.WaitFor(delegate { return s.Builder.Steps.Count > 0; }, 20f);
            yield return c.BuilderChoose("ancestry", JsonValue.FromString("human"));
            yield return c.BuilderChoose("class", JsonValue.FromString("cleric"));
            Debug.Log("[Review] pf2e cleric steps: " + string.Join(", ", s.Builder.Steps.ConvertAll(x => x.Key).ToArray()));
            yield return Go("classFeatures");
            yield return _h.Snap("review-pf2e-divine-calling");
            if (s.Builder.Step("deity") != null)
            {
                yield return Go("deity");
                yield return _h.Snap("review-pf2e-deity-step");
                yield return c.BuilderChoose("deity", JsonValue.FromString("ash_mother"));
                yield return Go("classFeatures");
                yield return _h.Snap("review-pf2e-divine-calling-narrowed");
            }
            while (ui.Overlays.CloseTop()) { }

            yield return c.StartSession("The first night");
            ui.OpenSheet("chars/liesl");
            yield return TableHarness.WaitFor(delegate { return ui.Root.Q<VaultButton>("sheet-level-up") != null; }, 10f);
            ui.Root.Q<VaultButton>("sheet-level-up").command();
            yield return TableHarness.WaitFor(delegate { return s.LevelUp.Offer != null; }, 10f);
            yield return TableHarness.Frames(10);
            yield return _h.Snap("review-pf2e-levelup-menu");
            while (ui.Overlays.CloseTop()) { }
            yield return c.DeleteCampaign(pf);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator TheSheetsClassFeatures_ReadAsParagraphs_OnAScratchCharacter()
        {
            _h = new TableHarness();
            yield return _h.Start();
            yield return TableHarness.WaitFor(delegate { return _h.Ui.Overlays.AnyOpen && _h.State.CampaignsLoaded; }, 10f);
            while (_h.Ui.Overlays.CloseTop()) { }
            string slug = "review-sheet";
            yield return Table(slug, "Dnd5e", "levelup-dnd5e.json");
            yield return _h.Controller.StartSession("The first night");
            _h.Ui.OpenSheet("chars/aric");
            yield return TableHarness.WaitFor(delegate { return _h.Ui.Root.Q<VaultButton>("sheet-level-up") != null; }, 10f);
            yield return TableHarness.Frames(10);
            yield return _h.Snap("review-sheet-class-features");
            while (_h.Ui.Overlays.CloseTop()) { }
            yield return _h.Controller.DeleteCampaign(slug);
        }
    }
}
