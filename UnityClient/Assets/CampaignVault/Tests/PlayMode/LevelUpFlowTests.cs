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
    /// The level-up menu against a real server: a level 3 fighter with the XP for level 4. The party frame says so, the
    /// sheet offers the menu, the menu shows the improvement as the builder's cards, and gaining the level changes the
    /// character on the server. Scratch server, scratch campaign, deleted after.
    /// </summary>
    [Category("Integration")]
    public class LevelUpFlowTests
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

        private static void Click(VisualElement root, string name)
        {
            var button = root.Q<VaultButton>(name);
            Assert.IsNotNull(button, name + " is not on screen");
            button.command();
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator AFighterWithTheXp_LevelsUpFromTheSheet()
        {
            _h = new TableHarness();
            yield return _h.Start();
            yield return TableHarness.WaitFor(delegate { return _h.Ui.Overlays.AnyOpen && _h.State.CampaignsLoaded; }, 10f);
            while (_h.Ui.Overlays.CloseTop()) { }
            var s = _h.State;
            var ui = _h.Ui;
            const string slug = "levelup-dnd5e";

            var create = Args(slug);
            create.ObjectValue["name"] = JsonValue.FromString(slug);
            create.ObjectValue.Remove("campaignName");
            create.ObjectValue["initialSystem"] = JsonValue.FromString("Dnd5e");
            yield return _h.Call("build", "create_campaign", create, null);
            var build = Args(slug);
            build.ObjectValue["batch"] = JsonValue.Parse(File.ReadAllText(Path.Combine(
                Application.dataPath, "CampaignVault", "Tests", "PlayMode", "Fixtures", "levelup-dnd5e.json")));
            yield return _h.Call("build", "world_build", build, null);

            _h.Controller.SelectCampaign(slug, "Dnd5e");
            while (ui.Overlays.CloseTop()) { }
            yield return _h.Controller.StartSession("The first night");
            Assert.IsNotNull(s.Session, s.SessionStatus);
            Assert.AreEqual("chars/aric", s.PcId);

            // Before the XP, nothing to announce. The DM grants it as it would in play (xp_grant), and the next read says so.
            Assert.IsFalse(s.Session.Party.Find(m => m.Id == "chars/aric").LevelUpReady);
            var turn = Args(slug);
            turn.ObjectValue["request"] = JsonValue.Parse("{\"narrative\":\"The bandits are beaten.\",\"changes\":["
                + "{\"$type\":\"event\",\"category\":\"Combat\",\"summary\":\"The party beat the bandits at the crossing.\",\"involved\":[\"chars/aric\"]},"
                + "{\"$type\":\"xp_grant\",\"characterId\":\"chars/aric\",\"amount\":3000,\"reason\":\"the crossing\"}]}");
            yield return _h.Call("play", "take_turn", turn, null);
            yield return _h.Controller.RefreshTable();

            // The XP rule says the level is earned: the party frame carries the chip, and the player was told once.
            Assert.IsTrue(s.Session.Party.Find(m => m.Id == "chars/aric").LevelUpReady);
            Assert.IsTrue(s.LevelUpAnnounced.Contains("chars/aric@Fighter 3"));
            yield return TableHarness.Frames(10);
            yield return _h.Snap("levelup-party-chip");

            ui.OpenSheet("chars/aric");
            yield return TableHarness.WaitFor(delegate { return ui.Root.Q<VaultButton>("sheet-level-up") != null; }, 10f);
            yield return _h.Snap("levelup-sheet");
            Click(ui.Root, "sheet-level-up");
            yield return TableHarness.WaitFor(delegate { return s.LevelUp.Offer != null; }, 10f);
            yield return TableHarness.Frames(10);
            Assert.AreEqual("4.asiOrFeat", s.LevelUp.Offer.Slots[0].Id);
            Assert.IsFalse(ui.Root.Q<VaultButton>("levelup-apply").enabledSelf, "nothing is chosen yet");
            yield return _h.Snap("levelup-menu");

            // Strength and Constitution: +1 each.
            ui.Root.Q<VaultButton>("slot-4.asiOrFeat-Strength").command();
            ui.Root.Q<VaultButton>("slot-4.asiOrFeat-Constitution").command();
            yield return TableHarness.Frames(5);
            Assert.IsTrue(ui.Root.Q<VaultButton>("levelup-apply").enabledSelf);
            yield return _h.Snap("levelup-menu-picked");
            Click(ui.Root, "levelup-apply");
            yield return TableHarness.WaitFor(delegate { return s.LevelUp.Done; }, 20f);

            var get = Args(slug);
            get.ObjectValue["entityId"] = JsonValue.FromString("chars/aric");
            JsonValue entity = null;
            yield return _h.Call("play", "get_entity", get, delegate (UnityClient.Net.ToolPayload p) { entity = p.Data; });
            Assert.AreEqual("Fighter 4", entity.Get("character").GetString("classLevel", string.Empty));
            Assert.AreEqual(18, (int)entity.Get("character").Get("systemStats").GetNumber("strength", 0)); // 16, +1 as a human, +1 improvement
            Assert.AreEqual(17, (int)entity.Get("character").Get("systemStats").GetNumber("constitution", 0));
            Assert.IsFalse(entity.Get("levelUp").GetBool("ready", true), "the XP for level 5 is 6500");

            yield return _h.Controller.DeleteCampaign(slug);
        }
    }
}
