using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.UI.Builder;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// Phase 8 on the client: the level choices step (a level-5 wizard's arcane tradition and ability score improvement),
    /// the level cap from the recipe, and "the DM fills the rest" read against the open steps' options.
    /// </summary>
    public class LevelChoicesTests
    {
        private const string StepsJson = "{\"system\":\"dnd5e\",\"kind\":\"pc\",\"maxLevel\":20,\"steps\":["
            + "{\"key\":\"class\",\"kind\":\"pickOne\",\"prompt\":\"Class\",\"source\":\"classes\"},"
            + "{\"key\":\"skills\",\"kind\":\"pickN\",\"prompt\":\"Skills\",\"source\":\"classSkills\"},"
            + "{\"key\":\"levels\",\"kind\":\"levelChoices\",\"prompt\":\"Level choices\"},"
            + "{\"key\":\"spells\",\"kind\":\"spells\",\"prompt\":\"Spells\",\"source\":\"spells\"},"
            + "{\"key\":\"identity\",\"kind\":\"identity\",\"prompt\":\"Who they are\"}],"
            + "\"reads\":{\"skills\":[\"class\"],\"levels\":[\"class\"],\"spells\":[\"class\"]}}";

        /// <summary>What the server's options call returns for a level-5 wizard's levels step.</summary>
        private const string LevelOptionsJson = "{\"options\":["
            + "{\"id\":\"evocation\",\"label\":\"School of Evocation\",\"group\":\"2.subclass\"},"
            + "{\"id\":\"divination\",\"label\":\"School of Divination\",\"group\":\"2.subclass\"},"
            + "{\"id\":\"Strength\",\"label\":\"Strength\",\"group\":\"4.asiOrFeat\"},"
            + "{\"id\":\"Constitution\",\"label\":\"Constitution\",\"group\":\"4.asiOrFeat\"},"
            + "{\"id\":\"Intelligence\",\"label\":\"Intelligence\",\"group\":\"4.asiOrFeat\"},"
            + "{\"id\":\"grappler\",\"label\":\"Grappler\",\"description\":\"Prerequisite: Strength 13 or higher.\",\"group\":\"4.asiOrFeat\"}],"
            + "\"slots\":["
            + "{\"id\":\"2.subclass\",\"level\":2,\"key\":\"subclass\",\"title\":\"Level 2 · Arcane Tradition\",\"type\":\"Enum\",\"required\":true,\"picks\":1},"
            + "{\"id\":\"4.asiOrFeat\",\"level\":4,\"key\":\"asiOrFeat\",\"title\":\"Level 4 · Ability Score Improvement\",\"type\":\"AsiOrFeat\",\"required\":true,\"picks\":2,"
            + "\"abilities\":[\"Strength\",\"Constitution\",\"Intelligence\"]}]}";

        private VaultAppState _s;

        [SetUp]
        public void SetUp()
        {
            _s = new VaultAppState();
            _s.Builder.Slug = "scratch";
            _s.Builder.Draft.Level = 5;
            VaultController.ReadSteps(_s.Builder, JsonValue.Parse(StepsJson));
            _s.Builder.Options["levels"] = Options(LevelOptionsJson);
            _s.Builder.Draft.Set("class", JsonValue.FromString("wizard"));
        }

        private static StepOptions Options(string json)
        {
            return VaultController.ReadOptions(new McpOutcome<ToolPayload> { Ok = true, Data = new ToolPayload { Data = JsonValue.Parse(json) } });
        }

        private LevelSlot Slot(string id) { return _s.Builder.Options["levels"].Slots.Find(s => s.Id == id); }

        private JsonValue Levels { get { return _s.Builder.Draft.Get("levels"); } }

        [Test]
        public void TheRecipesMaxLevel_AndTheSlots_AreRead()
        {
            Assert.AreEqual(20, _s.Builder.MaxLevel);
            var slots = _s.Builder.Options["levels"].Slots;
            Assert.AreEqual(new[] { "2.subclass", "4.asiOrFeat" }, slots.Select(s => s.Id).ToArray());
            Assert.IsTrue(slots[1].IsAsi);
            Assert.AreEqual(2, slots[1].Picks);
            Assert.IsTrue(slots[1].IsAbility("intelligence"));
            Assert.IsFalse(slots[1].IsAbility("grappler"));
            Assert.AreEqual(new[] { "class" }, _s.Builder.Step("levels").Reads.ToArray());
        }

        [Test]
        public void Toggle_ASlotThatTakesTwo_KeepsBoth_RefusesAThird_AndDropsOneTappedAgain()
        {
            var slot = LevelSlot.Parse(JsonValue.Parse("{\"id\":\"3.metamagic\",\"level\":3,\"key\":\"metamagic\",\"type\":\"Enum\",\"required\":true,\"picks\":2}"));
            Assert.AreEqual(2, slot.Picks);

            var v = LevelChoices.Toggle(null, slot, "careful");
            v = LevelChoices.Toggle(v, slot, "subtle");
            Assert.AreEqual(new[] { "careful", "subtle" }, LevelChoices.Picks(v)["3.metamagic"].ToArray());

            Assert.AreSame(v, LevelChoices.Toggle(v, slot, "twinned"));

            v = LevelChoices.Toggle(v, slot, "careful");
            Assert.AreEqual(new[] { "subtle" }, LevelChoices.Picks(v)["3.metamagic"].ToArray());
        }

        [Test]
        public void Toggle_OnePickSlot_ThePickIsReplaced()
        {
            var v = LevelChoices.Toggle(Levels, Slot("2.subclass"), "evocation");
            v = LevelChoices.Toggle(v, Slot("2.subclass"), "divination");

            Assert.AreEqual("{\"2.subclass\":\"divination\"}", v.ToJson());
        }

        [Test]
        public void Toggle_Improvement_OneAbilityIsPlus2_TwoArePlus1_AThirdIsRefused_AFeatReplacesThem()
        {
            var asi = Slot("4.asiOrFeat");
            var v = LevelChoices.Toggle(Levels, asi, "Intelligence");
            Assert.AreEqual("+2 INTELLIGENCE", LevelChoices.Summary(asi, LevelChoices.Picks(v)["4.asiOrFeat"], null));

            v = LevelChoices.Toggle(v, asi, "Constitution");
            Assert.AreEqual("+1 INTELLIGENCE · +1 CONSTITUTION", LevelChoices.Summary(asi, LevelChoices.Picks(v)["4.asiOrFeat"], null));

            var full = LevelChoices.Toggle(v, asi, "Strength");
            Assert.AreSame(v, full, "a third ability changes nothing");

            v = LevelChoices.Toggle(v, asi, "grappler");
            Assert.AreEqual("{\"4.asiOrFeat\":\"grappler\"}", v.ToJson());
            Assert.AreEqual("FEAT: GRAPPLER", LevelChoices.Summary(asi, LevelChoices.Picks(v)["4.asiOrFeat"], _s.Builder.Options["levels"].Options));

            Assert.IsNull(LevelChoices.Toggle(v, asi, "grappler"), "tapping the feat again takes it back");
        }

        [Test]
        public void Prune_DropsPicksForLevelsTheCharacterNoLongerReaches()
        {
            var v = JsonValue.Parse("{\"2.subclass\":\"evocation\",\"4.asiOrFeat\":\"Intelligence\"}");

            Assert.AreEqual("{\"2.subclass\":\"evocation\"}", LevelChoices.Prune(v, 3).ToJson());
            Assert.IsNull(LevelChoices.Prune(v, 1));
        }

        [Test]
        public void Widget_IsOneSectionPerSlot_WithItsCards_AndTwoAbilitiesFillTheImprovement()
        {
            _s.Builder.Draft.Set("levels", JsonValue.Parse("{\"2.subclass\":\"evocation\",\"4.asiOrFeat\":[\"Intelligence\",\"Constitution\"]}"));
            var step = _s.Builder.Step("levels");
            var widget = (LevelChoicesStepViewModel)StepViewModel.For(step, null);
            widget.Update(step, _s.Builder);

            Assert.AreEqual("Builder/SpellsStep", widget.Template);
            Assert.AreEqual(new[] { "LEVEL 2 · ARCANE TRADITION", "LEVEL 4 · ABILITY SCORE IMPROVEMENT" }, widget.Sections.Select(s => s.Title).ToArray());
            var tradition = widget.Sections[0];
            Assert.AreEqual("SCHOOL OF EVOCATION", tradition.CountText);
            Assert.IsTrue(tradition.Complete);
            Assert.IsTrue(tradition.Options.Single(o => o.Name == "slot-2.subclass-evocation").Selected);
            var asi = widget.Sections[1];
            Assert.AreEqual(LevelChoicesStepViewModel.AsiHint, asi.Hint);
            Assert.AreEqual("+1 INTELLIGENCE · +1 CONSTITUTION", asi.CountText);
            var strength = asi.Options.Single(o => o.Name == "slot-4.asiOrFeat-Strength");
            Assert.IsTrue(strength.Full);
            Assert.AreEqual(LevelChoicesStepViewModel.FullHint, strength.FullHint);
            Assert.IsFalse(asi.Options.Single(o => o.Name == "slot-4.asiOrFeat-grappler").Full, "a feat replaces the abilities");
            Assert.IsFalse(widget.ShowFilter, "six options need no filter");
        }

        // ---- the DM fills the rest ----

        private List<FillTarget> WizardTargets()
        {
            var spells = Options("{\"options\":["
                + "{\"id\":\"fire_bolt\",\"label\":\"Fire Bolt\",\"group\":\"cantrips\"},{\"id\":\"light\",\"label\":\"Light\",\"group\":\"cantrips\"},"
                + "{\"id\":\"shield\",\"label\":\"Shield\",\"group\":\"known\"},{\"id\":\"fireball\",\"label\":\"Fireball\",\"group\":\"known\"},"
                + "{\"id\":\"misty_step\",\"label\":\"Misty Step\",\"group\":\"known\"}],"
                + "\"groupCounts\":{\"cantrips\":2,\"known\":2,\"prepared\":2}}");
            _s.Builder.Options["spells"] = spells;
            return new List<FillTarget>
            {
                new FillTarget { Step = _s.Builder.Step("levels"), Options = _s.Builder.Options["levels"] },
                new FillTarget { Step = _s.Builder.Step("spells"), Options = spells },
            };
        }

        [Test]
        public void Fill_OpenSteps_AreTheOnesWithPicksLeft()
        {
            var targets = WizardTargets();
            Assert.IsTrue(BuilderFiller.IsOpen(targets[0].Step, targets[0].Options, _s.Builder.Draft));
            _s.Builder.Draft.Set("levels", JsonValue.Parse("{\"2.subclass\":\"evocation\",\"4.asiOrFeat\":\"Intelligence\"}"));
            Assert.IsFalse(BuilderFiller.IsOpen(targets[0].Step, targets[0].Options, _s.Builder.Draft));
            Assert.IsTrue(BuilderFiller.IsOpen(targets[1].Step, targets[1].Options, _s.Builder.Draft));
            Assert.IsFalse(BuilderFiller.CanFill(new BuilderStep { Key = "abilities", Kind = StepKinds.AbilityScores }));
            Assert.IsFalse(BuilderFiller.CanFill(new BuilderStep { Key = "identity", Kind = StepKinds.Identity }));

            string prompt = BuilderFiller.SystemPrompt("dnd5e", _s.Builder.Draft, targets, null);
            StringAssert.Contains("## spells (Spells)", prompt);
            StringAssert.Contains("prepared: pick 2 more, from the known spells", prompt);
            StringAssert.DoesNotContain("slot \"2.subclass\"", prompt, "a chosen slot isn't asked again");
        }

        [Test]
        public void Fill_ThePicksThatAreOptionsAreApplied_TheRestAreRejected_AndThePlayersPicksStay()
        {
            var targets = WizardTargets();
            _s.Builder.Draft.Set("spells", JsonValue.Parse("{\"cantrips\":[\"light\"],\"known\":[],\"prepared\":[]}"));
            string reply = "An evoker who trusts fire over words.\n```json\n{"
                + "\"levels\":{\"2.subclass\":\"pyromancy\",\"4.asiOrFeat\":[\"Intelligence\"]},"
                + "\"spells\":{\"cantrips\":[\"Fire Bolt\",\"light\"],\"known\":[\"fireball\",\"shield\"],\"prepared\":[\"fireball\",\"misty_step\"]}"
                + "}\n```";
            var result = new FillResult();
            string error;

            Assert.IsTrue(BuilderFiller.Parse(reply, targets, _s.Builder.Draft, result, out error), error);

            Assert.AreEqual("An evoker who trusts fire over words.", result.Prose);
            // The made-up school stays open and is listed; the improvement is applied.
            Assert.AreEqual("{\"4.asiOrFeat\":\"Intelligence\"}", result.Choices["levels"].ToJson());
            CollectionAssert.AreEqual(new[] { "pyromancy (Level 2 · Arcane Tradition)" }, result.Rejected["levels"]);
            // Matched by name too ("Fire Bolt"); the player's light stays; misty step isn't in the spellbook.
            var spells = result.Choices["spells"];
            CollectionAssert.AreEqual(new[] { "light", "fire_bolt" }, CharacterDraft.Strings(spells.Get("cantrips")));
            CollectionAssert.AreEqual(new[] { "fireball", "shield" }, CharacterDraft.Strings(spells.Get("known")));
            CollectionAssert.AreEqual(new[] { "fireball" }, CharacterDraft.Strings(spells.Get("prepared")));
            CollectionAssert.AreEqual(new[] { "misty_step (prepared, but not among the known spells)" }, result.Rejected["spells"]);
        }

        [Test]
        public void Fill_AnImprovementWithAFeatAndAnAbility_IsRejected_AndNoJsonIsAnError()
        {
            var targets = WizardTargets();
            var result = new FillResult();
            string error;
            Assert.IsTrue(BuilderFiller.Parse("{\"levels\":{\"2.subclass\":\"evocation\",\"4.asiOrFeat\":[\"grappler\",\"Strength\"]}}", targets, _s.Builder.Draft, result, out error));
            Assert.AreEqual("{\"2.subclass\":\"evocation\"}", result.Choices["levels"].ToJson());
            StringAssert.Contains("one ability, two, or one feat", result.Rejected["levels"][0]);

            Assert.IsFalse(BuilderFiller.Parse("Take evocation, it's fun.", targets, _s.Builder.Draft, new FillResult(), out error));
            StringAssert.Contains("no picks", error);
        }

        [Test]
        public void FillCard_SaysWhatWasFilled_AndTheRejectedPicksAreWarningsOnTheirStep()
        {
            _s.Builder.Filled.Add("Level choices");
            _s.Builder.Rejected["levels"] = new List<string> { "pyromancy (Level 2 · Arcane Tradition)" };
            _s.Builder.FillReply = "An evoker.";
            _s.Builder.Current = "levels";
            var vm = new BuilderViewModel(_s, new VaultController(_s, null, new MemoryPrefs()));
            vm.Refresh();

            Assert.IsTrue(vm.ShowFilled);
            StringAssert.Contains("The DM filled level choices. Look them over before you save.", vm.FilledText);
            StringAssert.Contains("weren't used (see level choices)", vm.FilledText);
            Assert.IsTrue(vm.Warnings.Any(w => w.Contains("pyromancy (Level 2 · Arcane Tradition)")));
            Assert.AreEqual("DM FILLS THE REST", vm.FillLabel);
            vm.Dispose();
        }
    }
}
