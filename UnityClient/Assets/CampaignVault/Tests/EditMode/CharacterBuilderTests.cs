using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Builder;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>The character builder's pure seams: the draft on the wire, step parsing, dependency clearing, fallbacks.</summary>
    public class CharacterBuilderTests
    {
        /// <summary>character_builder action=steps for a 5e pc, as the server sends it.</summary>
        private const string StepsJson = "{\"system\":\"dnd5e\",\"kind\":\"pc\",\"steps\":["
            + "{\"key\":\"race\",\"kind\":\"pickOne\",\"prompt\":\"Race\",\"source\":\"races\",\"optional\":false},"
            + "{\"key\":\"class\",\"kind\":\"pickOne\",\"prompt\":\"Class\",\"source\":\"classes\",\"optional\":false},"
            + "{\"key\":\"background\",\"kind\":\"pickOne\",\"prompt\":\"Background\",\"source\":\"backgrounds\",\"optional\":false},"
            + "{\"key\":\"abilities\",\"kind\":\"abilityScores\",\"prompt\":\"Ability scores\",\"optional\":false,\"methods\":{\"standardArray\":[15,14,13,12,10,8],"
            + "\"pointBuy\":{\"budget\":27,\"min\":8,\"max\":15,\"cost\":{\"8\":0,\"9\":1,\"10\":2,\"11\":3,\"12\":4,\"13\":5,\"14\":7,\"15\":9}},\"roll\":\"4d6dropLowest\"}},"
            + "{\"key\":\"skills\",\"kind\":\"pickN\",\"prompt\":\"Skills\",\"source\":\"classSkills\",\"countFrom\":\"class.skillChoices.count\",\"exclude\":\"background.skillProficiencies\",\"optional\":false},"
            + "{\"key\":\"spells\",\"kind\":\"spells\",\"prompt\":\"Spells\",\"source\":\"spells\",\"when\":\"class.casterType != None\",\"optional\":false},"
            + "{\"key\":\"identity\",\"kind\":\"identity\",\"prompt\":\"Who they are\",\"optional\":false}]}";

        private static JsonValue Parse(string json)
        {
            JsonValue v;
            Assert.IsTrue(JsonValue.TryParse(json, out v), json);
            return v;
        }

        private static List<BuilderStep> Steps()
        {
            return Parse(StepsJson).GetArray("steps").Select(BuilderStep.Parse).ToList();
        }

        private static CharacterDraft Wizard()
        {
            var d = new CharacterDraft { Name = "Ilsa Venn", Concept = "A sage who reads too much", Level = 1 };
            d.Set("race", JsonValue.FromString("human"));
            d.Set("class", JsonValue.FromString("wizard"));
            d.Set("background", JsonValue.FromString("sage"));
            d.Set("skills", CharacterDraft.StringArray(new[] { "Insight", "Investigation" }));
            d.Set("spells", Parse("{\"cantrips\":[\"fire_bolt\",\"light\",\"mage_hand\"],\"known\":[\"shield\"],\"prepared\":[]}"));
            return d;
        }

        [Test]
        public void Draft_RoundTripsThroughJson()
        {
            var d = Wizard();
            d.Id = "chars/ilsa-1234";
            var back = CharacterDraft.FromJson(Parse(d.ToJson().ToJson()));

            Assert.AreEqual("chars/ilsa-1234", back.Id);
            Assert.AreEqual("pc", back.Kind);
            Assert.AreEqual("Ilsa Venn", back.Name);
            Assert.AreEqual("A sage who reads too much", back.Concept);
            Assert.AreEqual(1, back.Level);
            Assert.AreEqual("wizard", back.GetString("class"));
            CollectionAssert.AreEqual(new[] { "Insight", "Investigation" }, back.GetList("skills"));
            CollectionAssert.AreEqual(new[] { "fire_bolt", "light", "mage_hand" }, CharacterDraft.Strings(back.Get("spells").Get("cantrips")));
            Assert.AreEqual(d.ToJson().ToJson(), back.ToJson().ToJson());
        }

        [Test]
        public void Draft_OnTheWire_OmitsEmptyFields_AndKeysChoicesByStep()
        {
            var json = new CharacterDraft().ToJson();
            Assert.IsTrue(json.Get("id").IsNull, "no id until committed");
            Assert.IsTrue(json.Get("name").IsNull);
            Assert.AreEqual("pc", json.GetString("kind", null));
            Assert.AreEqual(1, (int)json.GetNumber("level", 0));
            Assert.AreEqual(JsonKind.Object, json.Get("choices").Kind);
        }

        [Test]
        public void Draft_Set_EmptyValueRemoves_AndReportsChange()
        {
            var d = new CharacterDraft();
            Assert.IsTrue(d.Set("race", JsonValue.FromString("elf")));
            Assert.IsFalse(d.Set("race", JsonValue.FromString("elf")), "same value is no change");
            Assert.IsTrue(d.Set("race", CharacterDraft.StringArray(new string[0])));
            Assert.IsFalse(d.Has("race"));
            Assert.IsFalse(d.Choices.ContainsKey("race"));
        }

        [Test]
        public void Step_Parse_ReadsMethodsAndPointBuyTable()
        {
            var abilities = Steps().Single(s => s.Key == "abilities");
            Assert.AreEqual(StepKinds.AbilityScores, abilities.Kind);
            CollectionAssert.AreEqual(new[] { 15, 14, 13, 12, 10, 8 }, abilities.StandardArray);
            Assert.AreEqual(27, abilities.PointBuy.Budget);
            Assert.AreEqual(9, abilities.PointBuy.CostOf(15));
            Assert.AreEqual(-1, abilities.PointBuy.CostOf(16));
            Assert.AreEqual(27, abilities.PointBuy.Spent(new[] { 15, 15, 15, 8, 8, 8 }));
            Assert.AreEqual("4d6dropLowest", abilities.Roll);
            Assert.AreEqual("Ability scores", abilities.Title);
            Assert.AreEqual("Deity choice", BuilderStep.PrettyKey("deity_choice"));
        }

        [Test]
        public void Dependents_FollowPathsAndClassDrivenSources()
        {
            var steps = Steps();
            CollectionAssert.AreEqual(new[] { "skills", "spells" }, BuilderDependencies.Dependents("class", steps));
            CollectionAssert.AreEqual(new[] { "skills" }, BuilderDependencies.Dependents("background", steps));
            CollectionAssert.IsEmpty(BuilderDependencies.Dependents("race", steps));
            CollectionAssert.IsEmpty(BuilderDependencies.Dependents("identity", steps));
        }

        [Test]
        public void ChangingClass_ClearsSkillsAndSpells_AndSaysSo()
        {
            var steps = Steps();
            var d = Wizard();

            var cleared = BuilderDependencies.ClearDependents(d, "class", steps);

            CollectionAssert.AreEqual(new[] { "skills", "spells" }, cleared.Select(s => s.Key).ToArray());
            Assert.IsFalse(d.Has("skills"));
            Assert.IsFalse(d.Has("spells"));
            Assert.AreEqual("sage", d.GetString("background"), "steps that don't read class keep their choice");
            Assert.AreEqual("Changing class cleared: skills, spells.", BuilderDependencies.ClearedNote(steps[1], cleared));
        }

        [Test]
        public void ClearedNote_IsEmpty_WhenNothingHadAChoice()
        {
            var steps = Steps();
            var d = new CharacterDraft();
            d.Set("class", JsonValue.FromString("fighter"));
            var cleared = BuilderDependencies.ClearDependents(d, "class", steps);
            CollectionAssert.IsEmpty(cleared);
            Assert.AreEqual(string.Empty, BuilderDependencies.ClearedNote(steps[1], cleared));
        }

        [Test]
        public void ReadSteps_DropsTheChoiceOfAStepThatNoLongerShows()
        {
            var b = new BuilderState();
            b.Reset("scratch", "dnd5e", "pc");
            VaultController.ReadSteps(b, Parse(StepsJson));
            b.Current = "spells";
            b.Draft.Set("spells", Parse("{\"cantrips\":[\"light\"]}"));
            b.Draft.Set("race", JsonValue.FromString("dwarf"));

            // A fighter: the server's step list has no spells step.
            var fighter = Parse(StepsJson.Replace("{\"key\":\"spells\",\"kind\":\"spells\",\"prompt\":\"Spells\",\"source\":\"spells\",\"when\":\"class.casterType != None\",\"optional\":false},", string.Empty));
            VaultController.ReadSteps(b, fighter);

            Assert.IsNull(b.Step("spells"));
            Assert.IsFalse(b.Draft.Has("spells"));
            Assert.AreEqual("dwarf", b.Draft.GetString("race"));
            Assert.AreEqual("identity", b.Current, "the current step moves to the one that took its place");
        }

        [Test]
        public void ReadPreview_SplitsErrorsAndWarnings_AndReadsTheSheet()
        {
            var b = new BuilderState();
            b.Draft.Name = "Ilsa";
            VaultController.ReadPreview(b, Parse("{\"character\":{\"id\":\"chars/preview\",\"name\":\"Ilsa\",\"isPc\":true,\"maxHp\":8,\"currentHp\":8,"
                + "\"systemStats\":{\"$system\":\"dnd5e\",\"intelligence\":16,\"level\":1}},"
                + "\"errors\":[{\"step\":\"skills\",\"message\":\"Pick 2 (1 so far).\"}],"
                + "\"warnings\":[{\"step\":\"abilities\",\"message\":\"Point buy: 2 points unspent.\",\"isWarning\":true}],\"notes\":[\"Derived MaxHp=8\"]}"));

            Assert.AreEqual(1, b.Errors.Count);
            Assert.AreEqual("skills", b.IssuesFor("skills", false)[0].Step);
            Assert.AreEqual(1, b.IssuesFor("abilities", true).Count);
            CollectionAssert.AreEqual(new[] { "Derived MaxHp=8" }, b.Notes);
            Assert.AreEqual("Ilsa", b.Preview.Name);
            Assert.AreEqual(8, b.Preview.MaxHp);

            // A nameless draft: the server says "Unnamed", the sheet's crest shows "?" rather than "UN".
            b.Draft.Name = string.Empty;
            VaultController.ReadPreview(b, Parse("{\"character\":{\"id\":\"chars/preview\",\"name\":\"Unnamed\",\"isPc\":true}}"));
            Assert.AreEqual(string.Empty, b.Preview.Name);
        }

        [Test]
        public void UnknownStepKind_IsNotSupported()
        {
            // The card itself: BuilderViewModelTests.UnknownStepKind_GetsAClearCard_InsteadOfFailing.
            Assert.IsFalse(StepKinds.Supported.Contains("tarotDraw"));
            Assert.IsInstanceOf<UnsupportedStepViewModel>(StepViewModel.For(BuilderStep.Parse(Parse("{\"key\":\"omens\",\"kind\":\"tarotDraw\"}")), null));
        }

        [Test]
        public void AbilityChoice_CarriesMethodAndOnlyAssignedScores()
        {
            var work = new AbilityWork { Method = "standardArray" };
            work.Pool.AddRange(new[] { 15, 14, 13, 12, 10, 8 });
            work.Assigned["Intelligence"] = 0;
            work.Assigned["Constitution"] = 1;

            var choice = VaultController.AbilityChoice(work);

            Assert.AreEqual("standardArray", choice.GetString("method", null));
            Assert.AreEqual(15, (int)choice.Get("scores").GetNumber("Intelligence", 0));
            Assert.AreEqual(14, (int)choice.Get("scores").GetNumber("Constitution", 0));
            Assert.AreEqual(2, choice.Get("scores").ObjectValue.Count);
            Assert.IsNull(VaultController.AbilityChoice(new AbilityWork()), "no method, no choice");
        }

        [Test]
        public void Dice_4d6DropLowest_StaysInRange_AndLogsTheDroppedDie()
        {
            var random = new Random(7);
            for (int i = 0; i < 200; i++)
            {
                string log;
                int total = AbilityDice.Roll("4d6dropLowest", random, out log);
                Assert.That(total, Is.InRange(3, 18));
                StringAssert.Contains("drop lowest", log);
                StringAssert.Contains("(", log);
                StringAssert.EndsWith("= " + total, log);
            }
            Assert.IsFalse(AbilityDice.IsValid("roll well"));
        }

        [Test]
        public void Advisor_SplitsTheSuggestLine_AndKeepsIdsThatAreNotOptions()
        {
            var ids = new List<string>();
            string shown = BuilderAdvisor.SplitSuggestions("A wizard fits a sage.\nOr a cleric.\nSUGGEST: wizard, Cleric, necromancer", ids);
            Assert.AreEqual("A wizard fits a sage.\nOr a cleric.", shown);
            CollectionAssert.AreEqual(new[] { "wizard", "Cleric", "necromancer" }, ids);

            var options = new List<BuilderOption>
            {
                new BuilderOption { Id = "wizard", Label = "Wizard" },
                new BuilderOption { Id = "cleric", Label = "Cleric" },
            };
            var valid = new List<string>();
            var notOptions = new List<string>();
            BuilderAdvisor.Match(ids, options, valid, notOptions);
            CollectionAssert.AreEqual(new[] { "wizard", "cleric" }, valid);
            CollectionAssert.AreEqual(new[] { "necromancer" }, notOptions);
        }

        [Test]
        public void Advisor_PromptListsTheStepsOptionIds_AndMarkerReadsAsBuilding()
        {
            var steps = Steps();
            var options = new List<BuilderOption> { new BuilderOption { Id = "half_orc", Label = "Half Orc" } };
            string prompt = BuilderAdvisor.SystemPrompt("dnd5e", steps[0], options, 1, Wizard(), new Dictionary<string, string> { { "tone", "grim" } });
            StringAssert.Contains("half_orc — Half Orc", prompt);
            StringAssert.Contains("SUGGEST:", prompt);
            StringAssert.Contains("D&D 5e", prompt);
            StringAssert.Contains("tone: grim", prompt);

            string marker = BuilderAdvisor.Marker("Lyra", "Class");
            Assert.AreEqual("Building Lyra · Class", marker);
            var chat = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(OnboardingBrainstorm.MarkerRole, marker) };
            StringAssert.Contains("character builder", OnboardingBrainstorm.ModelMessages(chat, null)[0].Value);
        }
    }
}
