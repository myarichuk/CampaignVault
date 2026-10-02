using System.Linq;
using NUnit.Framework;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Sheet;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// The level-up menu without a panel: the server's options reply → the builder's sections and cards, the picks the
    /// builder's own rule (LevelChoices.Toggle), and the button that waits for a complete set.
    /// </summary>
    public class LevelUpTests
    {
        /// <summary>character_level_up's options for a fighter reaching level 4 in 5e.</summary>
        private const string FighterJson = "{\"characterId\":\"chars/hild\",\"name\":\"Hild\",\"className\":\"Human Fighter\","
            + "\"status\":{\"possible\":true,\"ready\":true,\"level\":3,\"targetLevel\":4,\"xp\":3000,\"xpNeeded\":2700},"
            + "\"features\":[{\"level\":4,\"name\":\"Ability Score Improvement\",\"description\":\"Raise two scores.\",\"from\":\"Fighter\"}],"
            + "\"slots\":[{\"id\":\"4.asiOrFeat\",\"title\":\"Level 4 · Ability Score Improvement\",\"type\":\"AsiOrFeat\",\"required\":true,\"picks\":2,"
            + "\"abilities\":[\"Strength\",\"Constitution\"],\"options\":["
            + "{\"id\":\"Strength\",\"label\":\"Strength\"},{\"id\":\"Constitution\",\"label\":\"Constitution\"},"
            + "{\"id\":\"grappler\",\"label\":\"Grappler\",\"description\":\"Prerequisite: Strength 13 or higher.\",\"homebrew\":true}]}]}";

        private VaultAppState _s;
        private LevelUpViewModel _vm;
        private bool _closed;

        [SetUp]
        public void SetUp()
        {
            _s = new VaultAppState();
            _s.LevelUp.CharacterId = "chars/hild";
            _s.LevelUp.Offer = LevelUpOffer.Parse(JsonValue.Parse(FighterJson));
            _vm = new LevelUpViewModel(_s, new VaultController(_s, null, new MemoryPrefs()), delegate { _closed = true; });
            _vm.Refresh();
        }

        [TearDown]
        public void TearDown() { _vm.Dispose(); }

        private static LevelSlot Slot(VaultAppState s) { return s.LevelUp.Offer.Slots[0]; }

        [Test]
        public void TheOfferIsTheBuildersSlots_WithTheirCards()
        {
            var offer = _s.LevelUp.Offer;
            Assert.AreEqual("Human Fighter", offer.ClassName);
            Assert.IsTrue(offer.Status.Ready);
            Assert.AreEqual("4.asiOrFeat", Slot(_s).Id);
            Assert.IsTrue(Slot(_s).IsAsi);
            Assert.AreEqual(new[] { "Strength", "Constitution", "grappler" }, offer.OptionsOf(Slot(_s)).Select(o => o.Id).ToArray());
            Assert.IsTrue(offer.OptionsOf(Slot(_s)).Single(o => o.Id == "grappler").Homebrew);

            Assert.AreEqual("Hild · Human Fighter · level 3 → 4", _vm.Intro);
            Assert.AreEqual("3,000 XP of 2,700 needed for level 4.", _vm.XpLine);
            StringAssert.Contains("Ability Score Improvement (Fighter). Raise two scores.", _vm.Gains);
            var section = _vm.Sections.Single();
            Assert.AreEqual("LEVEL 4 · ABILITY SCORE IMPROVEMENT", section.Title);
            Assert.AreEqual("NOT CHOSEN", section.CountText);
            Assert.AreEqual(3, section.Options.Count);
            Assert.AreEqual("slot-4.asiOrFeat-Strength", section.Options[0].Name);
        }

        [Test]
        public void TheButtonWaitsForAChoice_ThenAskForTheLevel()
        {
            Assert.IsFalse(_vm.CanApply);
            StringAssert.Contains("Level 4 · Ability Score Improvement", _vm.Status);

            _s.LevelUp.Choice = LevelChoices.Toggle(_s.LevelUp.Choice, Slot(_s), "Strength");
            _vm.Refresh();

            Assert.IsTrue(_vm.CanApply);
            Assert.AreEqual("GAIN LEVEL 4", _vm.ApplyLabel);
            Assert.AreEqual("+2 STRENGTH", _vm.Sections[0].CountText);
            Assert.IsTrue(_vm.Sections[0].Options[0].Selected);
        }

        [Test]
        public void AnImprovementTakesTwoAbilitiesAtMost_AndAFeatReplacesThem()
        {
            var slot = Slot(_s);
            _s.LevelUp.Choice = LevelChoices.Toggle(_s.LevelUp.Choice, slot, "Strength");
            _s.LevelUp.Choice = LevelChoices.Toggle(_s.LevelUp.Choice, slot, "Constitution");
            _vm.Refresh();
            Assert.AreEqual("+1 STRENGTH · +1 CONSTITUTION", _vm.Sections[0].CountText);
            Assert.IsTrue(_vm.CanApply);

            _s.LevelUp.Choice = LevelChoices.Toggle(_s.LevelUp.Choice, slot, "grappler");
            _vm.Refresh();
            Assert.IsTrue(_vm.CanApply);
            StringAssert.StartsWith("FEAT:", _vm.Sections[0].CountText);
        }

        [Test]
        public void ThePicksTheServerReads_AreAListPerSlot()
        {
            var slot = Slot(_s);
            _s.LevelUp.Choice = LevelChoices.Toggle(_s.LevelUp.Choice, slot, "Strength");

            Assert.AreEqual("{\"4.asiOrFeat\":[\"Strength\"]}", LevelUpOffer.PicksArgument(_s.LevelUp.Choice).ToJson());
        }

        [Test]
        public void ARefusalIsShownAndTheMenuStaysOpen()
        {
            _s.LevelUp.Error = "Level 4 · Ability Score Improvement: Strength would be 21; ability score improvements stop at 20.";
            _vm.Refresh();

            StringAssert.Contains("stop at 20", _vm.Error);
            Assert.IsFalse(_closed);
        }

        [Test]
        public void GainingTheLevelClosesTheMenu()
        {
            _s.LevelUp.Done = true;
            _vm.Refresh();

            Assert.IsTrue(_closed);
        }

        [Test]
        public void ALevelWithNothingToChoose_JustComes()
        {
            _s.LevelUp.Offer = LevelUpOffer.Parse(JsonValue.Parse(
                "{\"characterId\":\"chars/hild\",\"name\":\"Hild\",\"status\":{\"possible\":true,\"level\":1,\"targetLevel\":2},\"features\":[],\"slots\":[]}"));
            _vm.Refresh();

            Assert.IsTrue(_vm.CanApply);
            StringAssert.Contains("just comes", _vm.Notice);
        }

        [Test]
        public void TheSheetKnowsWhetherTheLevelIsEarned_AndAMilestoneIsNeverEarnedByXp()
        {
            var earned = LevelUpStatus.Parse(JsonValue.Parse("{\"possible\":true,\"ready\":true,\"level\":3,\"targetLevel\":4,\"xp\":3000,\"xpNeeded\":2700}"));
            var milestone = LevelUpStatus.Parse(JsonValue.Parse("{\"possible\":true,\"ready\":false,\"level\":3,\"targetLevel\":4,\"xp\":0}"));

            Assert.IsTrue(earned.Ready);
            Assert.AreEqual("Level 4 by milestone: the story decides.", milestone.XpLine);
            Assert.IsNull(LevelUpStatus.Parse(JsonValue.Parse("null")));
        }

        [Test]
        public void ThePartyFrameCarriesTheLevelUpChip_FromStartSession()
        {
            var digest = SessionDigest.FromResult(JsonValue.Parse(
                "{\"party\":[{\"id\":\"chars/hild\",\"name\":\"Hild\",\"isPc\":true,\"hp\":\"28/28\",\"levelUpReady\":true},"
                + "{\"id\":\"chars/bran\",\"name\":\"Bran\",\"isPc\":false,\"hp\":\"9/9\"}]}"));

            Assert.IsTrue(digest.Party[0].LevelUpReady);
            Assert.IsFalse(digest.Party[1].LevelUpReady);
        }
    }
}
