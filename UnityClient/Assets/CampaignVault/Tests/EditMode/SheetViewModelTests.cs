using System.Linq;
using NUnit.Framework;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.UI.Sheet;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>The sheet, the stat block and the character page as view models: what the templates bind, without a panel.</summary>
    public class SheetViewModelTests
    {
        private const string Fighter = "{\"character\":{\"id\":\"chars/aric\",\"name\":\"Aric Thorne\",\"isPc\":true,\"maxHp\":44,\"currentHp\":11,"
            + "\"conditions\":[\"poisoned by the long-forgotten venom of the drowned mill\"],"
            + "\"systemStats\":{\"$system\":\"dnd5e\",\"level\":5,\"classLevels\":[{\"class\":\"Fighter\",\"level\":5}],\"race\":\"Human\",\"armorClass\":16,"
            + "\"strength\":16,\"dexterity\":14,\"constitution\":15,\"intelligence\":10,\"wisdom\":12,\"charisma\":8}}}";

        private const string Hound = "{\"character\":{\"id\":\"chars/tam\",\"name\":\"Tam\",\"isPartyCompanion\":true,\"maxHp\":13,\"currentHp\":13,"
            + "\"systemStats\":{\"$system\":\"dnd5e\",\"armorClass\":12,\"movement\":40,\"strength\":13,\"dexterity\":14,\"constitution\":12,"
            + "\"intelligence\":3,\"wisdom\":12,\"charisma\":6,\"challenge\":\"1/4\",\"attacks\":[{\"name\":\"Bite\",\"bonus\":3,\"damage\":\"1d6+1\"}]}}}";

        private static JsonValue Parse(string json)
        {
            JsonValue v;
            Assert.IsTrue(JsonValue.TryParse(json, out v), json);
            return v;
        }

        private static CharacterSheet Sheet(string json) { return CharacterSheet.FromPayload(Parse(json)); }

        [Test]
        public void ThePlayersSheet_LeadsWithHitPoints_AndTwoColumnSaves()
        {
            var vm = new SheetViewModel(Sheet(Fighter));

            Assert.AreEqual("Sheet/CharacterSheet", vm.Template);
            Assert.AreEqual("AT", vm.Monogram);
            var hp = vm.Vitals[0] as HpVitalViewModel;
            Assert.IsNotNull(hp, "hit points first, in their own box");
            Assert.AreEqual("11", hp.Current);
            Assert.AreEqual("/ 44", hp.Max);
            Assert.AreEqual(0.25f, hp.Fraction, 0.001f);
            var armor = vm.Vitals.OfType<VitalViewModel>().First();
            Assert.AreEqual("ARMOR", armor.Caption);
            Assert.AreEqual("ac", armor.Tone);
            Assert.AreEqual(6, vm.Abilities.Count);
            Assert.AreEqual("+3", vm.Abilities[0].Mod);
            Assert.IsTrue(vm.SavesTwoColumns, "5e saves sit in two columns");
            Assert.IsFalse(vm.Saves[0].Ranked, "5e has proficiency pips, not rank letters");
        }

        [Test]
        public void ALongCondition_IsCut_AndTheHoverKeepsItWhole()
        {
            var badge = new SheetViewModel(Sheet(Fighter)).Badges.Single();
            Assert.AreEqual("blood", badge.Tone);
            Assert.AreEqual("condition", badge.Icon);
            StringAssert.EndsWith("…", badge.Text);
            Assert.AreEqual("poisoned by the long-forgotten venom of the drowned mill", badge.Tooltip);
        }

        [Test]
        public void AStatBlock_DrawsTheSchemasExtraFields()
        {
            var schema = StatBlockSchema.Parse(Parse("{\"name\":\"companion\",\"fields\":["
                + "{\"key\":\"statBlockHp\",\"label\":\"Hit points\",\"type\":\"int\"},"
                + "{\"key\":\"strength\",\"label\":\"STR\",\"type\":\"int\"},"
                + "{\"key\":\"challenge\",\"label\":\"Challenge\",\"type\":\"text\"},"
                + "{\"key\":\"attacks\",\"label\":\"Attacks\",\"type\":\"attacks\"},"
                + "{\"key\":\"stance\",\"label\":\"Stance\",\"type\":\"text\"}]}"));

            var vm = new StatBlockViewModel(Sheet(Hound), schema);

            Assert.AreEqual("Sheet/StatBlock", vm.Template);
            CollectionAssert.AreEqual(new[] { "Armor Class", "Hit Points", "Speed" }, vm.TopLines.Select(l => l.Key).ToArray());
            Assert.IsTrue(vm.TopLines[1].HasBar);
            Assert.AreEqual("13 (+1)", vm.Abilities[0].Value);
            var extras = vm.Lines.ToDictionary(l => l.Key, l => l.Value);
            Assert.AreEqual("1/4", extras["Challenge"]);
            Assert.AreEqual("Bite +3 (1d6+1)", extras["Attacks"]);
            Assert.IsFalse(extras.ContainsKey("STR"), "fields with a fixed line aren't repeated");
            Assert.IsFalse(extras.ContainsKey("Stance"), "an unset field is left out");
        }

        [Test]
        public void ThePage_ShowsTheStatBlock_ThenTheSheetOncePlayed()
        {
            var state = new VaultAppState();
            var page = new SheetPageViewModel(state, new VaultController(state, null, new MemoryPrefs()), "chars/tam");
            page.Refresh();
            Assert.AreEqual("Reading the sheet…", page.Notice);
            Assert.IsNull(page.Content);

            page.Show(McpOutcome<JsonValue>.Success(Parse(Hound)));
            Assert.IsInstanceOf<StatBlockViewModel>(page.Content);
            Assert.AreEqual(string.Empty, page.Notice);
            Assert.IsTrue(page.CanPlayAs);
            Assert.AreEqual("Companion", page.Title);

            page.PlayAs();
            Assert.AreEqual("chars/tam", state.PcId);
            Assert.IsInstanceOf<SheetViewModel>(page.Content, "the played character gets the full sheet");
            Assert.IsTrue(page.IsYours);
            Assert.IsFalse(page.CanPlayAs);
            Assert.AreEqual("Tam", page.Title);
            page.Dispose();
        }

        [Test]
        public void AFailedLoad_SaysWhy()
        {
            var state = new VaultAppState();
            var page = new SheetPageViewModel(state, new VaultController(state, null, new MemoryPrefs()), "chars/ghost");
            page.Show(McpOutcome<JsonValue>.Fail("NOT_FOUND", "no such character"));
            StringAssert.Contains("no such character", page.Notice);
            Assert.AreEqual("warning", page.NoticeIcon);
            page.Dispose();
        }
    }
}
