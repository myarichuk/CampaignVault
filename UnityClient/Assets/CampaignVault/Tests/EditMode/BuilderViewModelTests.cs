using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Builder;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// The builder's view models without a panel: state change → property change → the values the template binds.
    /// The point of binding is that only what changed is raised, so these also pin what is *not* raised.
    /// </summary>
    public class BuilderViewModelTests
    {
        private const string StepsJson = "{\"system\":\"dnd5e\",\"kind\":\"pc\",\"steps\":["
            + "{\"key\":\"race\",\"kind\":\"pickOne\",\"prompt\":\"Race\",\"source\":\"races\",\"optional\":false},"
            + "{\"key\":\"class\",\"kind\":\"pickOne\",\"prompt\":\"Class\",\"source\":\"classes\",\"optional\":false},"
            + "{\"key\":\"abilities\",\"kind\":\"abilityScores\",\"prompt\":\"Ability scores\",\"optional\":false,\"methods\":{\"standardArray\":[15,14,13,12,10,8]}},"
            + "{\"key\":\"skills\",\"kind\":\"pickN\",\"prompt\":\"Skills\",\"source\":\"classSkills\",\"optional\":false},"
            + "{\"key\":\"omens\",\"kind\":\"tarotDraw\",\"prompt\":\"Omens\",\"optional\":true},"
            + "{\"key\":\"identity\",\"kind\":\"identity\",\"prompt\":\"Who they are\",\"optional\":false}]}";

        private VaultAppState _s;
        private BuilderViewModel _vm;

        [SetUp]
        public void SetUp()
        {
            _s = new VaultAppState();
            JsonValue steps;
            Assert.IsTrue(JsonValue.TryParse(StepsJson, out steps));
            _s.Builder.Slug = "scratch";
            VaultController.ReadSteps(_s.Builder, steps);
            _s.Builder.Current = "race";
            _s.Builder.Options["race"] = Options("dwarf", "elf", "human");
            // Commands aren't run here: no coroutine host.
            _vm = new BuilderViewModel(_s, new VaultController(_s, null, new MemoryPrefs()));
            _vm.Refresh();
        }

        [TearDown]
        public void TearDown() { _vm.Dispose(); }

        private static StepOptions Options(params string[] ids)
        {
            var opts = new StepOptions();
            foreach (string id in ids) { opts.Options.Add(new BuilderOption { Id = id, Label = char.ToUpperInvariant(id[0]) + id.Substring(1), Description = "The " + id + "." }); }
            return opts;
        }

        private static List<string> Raised(ViewModel vm)
        {
            var names = new List<string>();
            vm.propertyChanged += delegate (object sender, BindablePropertyChangedEventArgs e) { names.Add(e.propertyName.ToString()); };
            return names;
        }

        [Test]
        public void Refresh_FillsTheRailTheStepAndTheFoot()
        {
            CollectionAssert.AreEqual(new[] { "step-race", "step-class", "step-abilities", "step-skills", "step-omens", "step-identity" }, _vm.Steps.Select(p => p.Name).ToArray());
            Assert.IsTrue(_vm.Steps[0].Current);
            Assert.AreEqual("1  RACE", _vm.Steps[0].Label);
            Assert.AreEqual(string.Empty, _vm.Steps[0].Icon, "not reached, not done: no icon");
            Assert.IsTrue(_vm.HasStep);
            Assert.AreEqual("Race", _vm.StepTitle);

            var pick = _vm.StepWidget as PickStepViewModel;
            Assert.IsNotNull(pick);
            Assert.AreEqual("Builder/PickStep", pick.Template);
            CollectionAssert.AreEqual(new[] { "opt-dwarf", "opt-elf", "opt-human" }, pick.Options.Select(o => o.Name).ToArray());
            Assert.IsFalse(pick.ShowCount, "pickOne has no count line");
            Assert.AreEqual(string.Empty, pick.Notice);

            Assert.IsFalse(_vm.CanBack);
            Assert.IsTrue(_vm.ShowNext);
            Assert.AreEqual("SAVE CHARACTER", _vm.CommitLabel);
            Assert.AreEqual("Checking…", _vm.Status, "no errors yet, preview not back");
            StringAssert.StartsWith("Needs a working AI provider", _vm.AskHint);
        }

        [Test]
        public void AChoice_RaisesTheItemsThatChanged_AndNothingElse()
        {
            var pick = (PickStepViewModel)_vm.StepWidget;
            var human = pick.Options.First(o => o.Key == "opt-human");
            var dwarf = pick.Options.First(o => o.Key == "opt-dwarf");
            var page = Raised(_vm);
            var widget = Raised(pick);
            var humanRaised = Raised(human);
            var dwarfRaised = Raised(dwarf);
            var pillRaised = Raised(_vm.Steps[0]);
            var options = pick.Options;

            _s.Builder.Draft.Set("race", JsonValue.FromString("human"));
            _s.Notify(StateArea.Builder);

            CollectionAssert.Contains(humanRaised, "Selected");
            CollectionAssert.IsEmpty(dwarfRaised, "an unchanged card isn't touched");
            CollectionAssert.Contains(pillRaised, "Done");
            CollectionAssert.Contains(pillRaised, "Icon");
            Assert.AreSame(options, pick.Options, "same options: the list (and its elements) is kept");
            CollectionAssert.DoesNotContain(widget, "Options");
            CollectionAssert.DoesNotContain(page, "StepWidget", "same step: the widget is rebound, not rebuilt");
            CollectionAssert.DoesNotContain(page, "Steps");
            CollectionAssert.DoesNotContain(page, "AskText");
            Assert.AreEqual("check", _vm.Steps[0].Icon);
        }

        [Test]
        public void Typing_ReachesTheDraft_AndARefreshLeavesItAlone()
        {
            var raised = Raised(_vm);
            _vm.AskText = "something sturdy";
            Assert.AreEqual("something sturdy", _s.Builder.AskDraft);
            raised.Clear();

            _s.Notify(StateArea.Builder);

            Assert.AreEqual("something sturdy", _vm.AskText);
            CollectionAssert.DoesNotContain(raised, "AskText");
        }

        [Test]
        public void MovingToAnotherKind_SwapsTheWidget()
        {
            _s.Builder.Current = "abilities";
            _s.Notify(StateArea.Builder);

            var abilities = _vm.StepWidget as AbilityStepViewModel;
            Assert.IsNotNull(abilities);
            Assert.AreEqual("Builder/AbilityStep", abilities.Template);
            CollectionAssert.AreEqual(new[] { "method-standardArray" }, abilities.Methods.Select(m => m.Name).ToArray());
            Assert.IsTrue(abilities.ShowHint, "no method yet");
            Assert.IsEmpty(abilities.Rows);

            _s.Builder.Abilities.Method = "standardArray";
            _s.Builder.Abilities.Pool.AddRange(new[] { 15, 14, 13, 12, 10, 8 });
            _s.Builder.Abilities.Assigned["Strength"] = 0;
            _s.Notify(StateArea.Builder);

            Assert.IsFalse(abilities.ShowHint);
            Assert.IsTrue(abilities.Methods[0].Active);
            Assert.AreEqual("1 OF 6 ASSIGNED", abilities.AssignedText);
            var strength = abilities.Rows.First(r => r.Key == "Strength");
            Assert.AreEqual("assign-Strength-0", strength.Pool[0].Name);
            Assert.IsTrue(strength.Pool[0].Selected);
            Assert.IsTrue(abilities.Rows.First(r => r.Key == "Dexterity").Pool[0].Taken);
            Assert.AreEqual("15 (+2)", strength.FinalText);
            Assert.AreEqual(string.Empty, strength.BonusText, "no preview yet, no race bonus");
        }

        [Test]
        public void TheFilter_HidesCardsInPlace()
        {
            _s.Builder.Current = "skills";
            var many = new List<string>();
            for (int i = 0; i < 14; i++) { many.Add("skill" + i); }
            _s.Builder.Options["skills"] = Options(many.ToArray());
            _s.Builder.Options["skills"].Count = 2;
            _s.Notify(StateArea.Builder);
            var pick = (PickStepViewModel)_vm.StepWidget;
            Assert.IsTrue(pick.ShowFilter);
            Assert.IsTrue(pick.ShowCount);
            Assert.AreEqual("0 OF 2 CHOSEN", pick.CountText);

            pick.Filter = "skill1";

            Assert.AreEqual("skill1", _s.Builder.Filters["skills"], "kept across redraws and steps");
            CollectionAssert.AreEqual(new[] { "opt-skill1", "opt-skill10", "opt-skill11", "opt-skill12", "opt-skill13" },
                pick.Options.Where(o => o.Visible).Select(o => o.Name).ToArray());
        }

        [Test]
        public void UnknownStepKind_GetsAClearCard_InsteadOfFailing()
        {
            _s.Builder.Current = "omens";
            _s.Notify(StateArea.Builder);

            var card = _vm.StepWidget as UnsupportedStepViewModel;
            Assert.IsNotNull(card);
            Assert.AreEqual("Builder/UnsupportedStep", card.Template);
            StringAssert.Contains("doesn't support step 'tarotDraw' yet", card.Message);
            StringAssert.Contains("optional", card.Detail);
        }

        [Test]
        public void AtTheLevelCap_PlusIsDisabled_WithTheReasonOnItsWrapper()
        {
            Assert.IsTrue(_vm.CanLevelUp);
            Assert.AreEqual("One level higher", _vm.LevelUpHint);
            // The cap is the recipe's (the server's maxLevel), once the steps have loaded.
            _s.Builder.MaxLevel = 5;
            _s.Builder.Draft.Level = 5;
            _s.Notify(StateArea.Builder);

            Assert.IsFalse(_vm.CanLevelUp);
            Assert.AreEqual(string.Empty, _vm.LevelUpHint);
            StringAssert.Contains("goes up to level 5", _vm.LevelCapReason);
            Assert.AreEqual("5", _vm.LevelText);
        }

        [Test]
        public void TheLevelCap_BeforeTheRecipeLoads_IsTheRulesets_5eTo20_Pf2eTo3()
        {
            Assert.AreEqual(20, VaultController.MaxBuilderLevelFor("dnd5e"));
            Assert.AreEqual(3, VaultController.MaxBuilderLevelFor("pf2e"));
            Assert.AreEqual(1, VaultController.MaxBuilderLevelFor("narrative"));
            var c = new VaultController(_s, null, new MemoryPrefs());
            _s.Builder.System = "pf2e";
            _s.Builder.MaxLevel = 0;
            Assert.AreEqual(3, c.BuilderMaxLevel);
            _s.Builder.MaxLevel = 7;
            Assert.AreEqual(7, c.BuilderMaxLevel);
        }

        [Test]
        public void TheDmsPicks_BecomeButtons_AndNonOptionsAreListed()
        {
            _s.Builder.AskStep = "race";
            _s.Builder.AskReply = "A dwarf would suit.\nSUGGEST: dwarf";
            _s.Builder.Suggested.Add("dwarf");
            _s.Builder.NotOptions.Add("kobold");
            _s.Notify(StateArea.Builder);

            Assert.IsTrue(_vm.ShowAdvice);
            CollectionAssert.AreEqual(new[] { "use-dwarf" }, _vm.Suggestions.Select(s => s.Name).ToArray());
            Assert.AreEqual("USE DWARF", _vm.Suggestions[0].Label);
            StringAssert.Contains("kobold", _vm.NotOptionsText);
            Assert.IsTrue(((PickStepViewModel)_vm.StepWidget).Options.First(o => o.Key == "opt-dwarf").Suggested);
        }

        [Test]
        public void Disposing_StopsWatchingState()
        {
            var raised = Raised(_vm);
            _vm.Dispose();
            _s.Builder.Draft.Level = 2;
            _s.Notify(StateArea.Builder);
            CollectionAssert.IsEmpty(raised);
        }

        [Test]
        public void StatField_Skills_AreARowPerSkillSet_AndAnAddButtonPerSkillLeft()
        {
            var field = new StatBlockField { Key = "skillModifiers", Label = "Skills", Type = StatModifiers.Type, Min = -5, Max = 20 };
            field.Keys.AddRange(new[] { "Athletics", "Perception", "Stealth" });
            var sent = new List<string>();
            var vm = new StatFieldViewModel(field, delegate { Assert.Fail("a skills field never sends its whole text"); },
                delegate (StatFieldViewModel f, string skill, string text) { sent.Add(skill + "=" + text); });

            vm.ShowModifiers(JsonValue.Parse("{\"Stealth\":6,\"Perception\":5}"));

            Assert.IsTrue(vm.IsModifiers);
            Assert.AreEqual("bonus, -5–20", vm.Hint);
            CollectionAssert.AreEqual(new[] { "Perception", "Stealth" }, vm.Rows.Select(r => r.Key).ToArray(), "in the skill list's order");
            Assert.AreEqual("5", vm.Rows[0].Cell("value").Value);
            Assert.AreEqual("skill-perception", vm.Rows[0].Cell("value").Name);
            Assert.AreEqual("skill-remove-perception", vm.Rows[0].RemoveName);
            Assert.AreEqual("PERCEPTION", vm.Rows[0].Cell("name").Caption);
            Assert.IsFalse(vm.Rows[0].Stretches, "a skill row is as wide as its cells, its button beside the bonus");
            CollectionAssert.AreEqual(new[] { "skill-add-athletics" }, vm.Addable.Select(a => a.Name).ToArray());

            vm.Addable[0].Pick();
            vm.Rows[1].Cell("value").Value = "+7";
            vm.Rows[0].Remove();
            CollectionAssert.AreEqual(new[] { "Athletics=" + StatFieldViewModel.NewModifier, "Stealth=+7", "Perception=" }, sent);

            var rows = vm.Rows;
            vm.ShowModifiers(JsonValue.Parse("{\"Stealth\":7,\"Perception\":5}"));
            Assert.AreSame(rows, vm.Rows, "the same skills keep their rows (and focus)");
            Assert.AreEqual("7", vm.Rows[1].Cell("value").Value);
        }

        [Test]
        public void StatBlock_FieldsComeInGroups_NumbersAndMarkedTextCompact()
        {
            var b = new BuilderState();
            b.StatBlocks.Add(StatBlockSchema.Parse(JsonValue.Parse("{\"name\":\"companion\",\"fields\":["
                + "{\"key\":\"challengeRating\",\"label\":\"Challenge rating\",\"type\":\"text\",\"compact\":true,\"group\":\"defense\"},"
                + "{\"key\":\"statBlockHp\",\"label\":\"Hit points\",\"type\":\"int\",\"group\":\"defense\"},"
                + "{\"key\":\"strength\",\"label\":\"STR\",\"type\":\"int\",\"group\":\"abilities\"},"
                + "{\"key\":\"dexterity\",\"label\":\"DEX\",\"type\":\"int\",\"group\":\"abilities\"},"
                + "{\"key\":\"traits\",\"label\":\"Traits\",\"type\":\"text\",\"group\":\"notes\"}]}")));
            b.Draft.Choices["statblock"] = JsonValue.Parse("{\"strength\":13,\"challengeRating\":\"1/8\"}");
            var step = new BuilderStep { Key = "statblock", Kind = "identity", Schema = "companion" };
            var vm = new IdentityStepViewModel(step, null);

            vm.Update(step, b);

            CollectionAssert.AreEqual(new[] { "DEFENSE", "ABILITIES", "NOTES" }, vm.Groups.Select(g => g.Title).ToArray());
            CollectionAssert.AreEqual(new[] { "stat-strength", "stat-dexterity" }, vm.Groups[1].Fields.Select(f => f.Name).ToArray());
            CollectionAssert.AreEqual(new[] { true, true }, vm.Groups[0].Fields.Select(f => f.IsCompact).ToArray(), "a number, and text the schema marks compact");
            Assert.IsFalse(vm.Groups[2].Fields[0].IsCompact, "free text takes the full width");
            Assert.AreEqual("13", vm.Groups[1].Fields[0].Value);
            Assert.AreEqual("1/8", vm.Groups[0].Fields[0].Value);

            var strength = vm.Groups[1].Fields[0];
            var groups = vm.Groups;
            b.Draft.Choices["statblock"] = JsonValue.Parse("{\"strength\":14}");
            vm.Update(step, b);
            Assert.AreSame(groups, vm.Groups, "a value change rebuilds nothing");
            Assert.AreSame(strength, vm.Groups[1].Fields[0], "the field keeps its element (and focus)");
            Assert.AreEqual("14", strength.Value);
        }

        [Test]
        public void Nature_IsTheSchemasTitle_ItsFieldsHints_AndAListTheDmWroteReadsAsCommaText()
        {
            var b = new BuilderState();
            b.StatBlocks.Add(StatBlockSchema.Parse(JsonValue.Parse("{\"name\":\"nature\",\"title\":\"Nature\",\"fields\":["
                + "{\"key\":\"descriptors\",\"label\":\"Three descriptors\",\"type\":\"list\",\"min\":3,\"max\":3,\"hint\":\"three words, comma-separated\",\"group\":\"who they are\"},"
                + "{\"key\":\"fears\",\"label\":\"Fears\",\"type\":\"list\",\"group\":\"what moves them\"}]}")));
            b.Draft.Choices["identity"] = JsonValue.Parse("{\"descriptors\":[\"Wry\",\"restless\",\"loyal\"],\"fears\":\"Deep water\"}");
            var step = new BuilderStep { Key = "identity", Kind = "identity", Schema = "nature" };
            var vm = new IdentityStepViewModel(step, null);

            vm.Update(step, b);

            Assert.AreEqual("NATURE", vm.SchemaTitle);
            CollectionAssert.AreEqual(new[] { "WHO THEY ARE", "WHAT MOVES THEM" }, vm.Groups.Select(g => g.Title).ToArray());
            var descriptors = vm.Groups[0].Fields[0];
            Assert.IsTrue(descriptors.IsBox, "a list is written as one line of text");
            Assert.AreEqual("three words, comma-separated", descriptors.Hint);
            Assert.AreEqual("Wry, restless, loyal", descriptors.Value);
            Assert.AreEqual("Deep water", vm.Groups[1].Fields[0].Value);
            Assert.AreEqual("list", vm.Groups[1].Fields[0].Hint, "no hint in the schema: the type says it");
        }

        [Test]
        public void Level_IsShownForASystemWithLevels_NotForNarrative()
        {
            Assert.IsTrue(_vm.ShowLevel);
            _s.Builder.System = "narrative";
            _s.Notify(StateArea.Builder);
            Assert.IsFalse(_vm.ShowLevel);
        }

        [Test]
        public void StatBlock_WithoutATitle_IsCaptionedStatBlock()
        {
            var b = new BuilderState();
            b.StatBlocks.Add(StatBlockSchema.Parse(JsonValue.Parse("{\"name\":\"companion\",\"fields\":[{\"key\":\"statBlockHp\",\"type\":\"int\"}]}")));
            var step = new BuilderStep { Key = "statblock", Kind = "identity", Schema = "companion" };
            var vm = new IdentityStepViewModel(step, null);

            vm.Update(step, b);

            Assert.AreEqual("STAT BLOCK", vm.SchemaTitle);
        }

        [Test]
        public void StatField_Attacks_AreARowPerAttack_UnderColumnCaptions_WithAnAddButtonUpToTheMost()
        {
            var field = new StatBlockField { Key = "attacks", Label = "Attacks", Type = StatRows.Type, Item = "attack", Max = 2 };
            field.Columns.Add(new StatBlockColumn { Key = "name", Label = "Attack", Required = true });
            field.Columns.Add(new StatBlockColumn { Key = "bonus", Label = "To hit", Type = "int" });
            field.Columns.Add(new StatBlockColumn { Key = "damage", Label = "Damage", Type = "dice" });
            field.Columns.Add(new StatBlockColumn { Key = "notes", Label = "Notes" });
            var sent = new List<string>();
            var vm = new StatFieldViewModel(field, delegate { Assert.Fail("an attacks field never sends its whole text"); }, null, new StatRowEdits
            {
                Cell = delegate (StatFieldViewModel f, int row, StatBlockColumn column, string text) { sent.Add(row + "." + column.Key + "=" + text); },
                Add = delegate { sent.Add("add"); },
                Remove = delegate (StatFieldViewModel f, int row) { sent.Add("remove " + row); },
            });

            vm.ShowRows(JsonValue.FromString("Bite +3, 1d6+1 piercing, reach 5 ft., knocks prone"));

            Assert.IsTrue(vm.IsRows && vm.IsList);
            Assert.AreEqual(1, vm.Rows.Count, "the template's text reads as rows");
            var bite = vm.Rows[0];
            Assert.AreEqual("Bite", bite.Cell("name").Value);
            Assert.AreEqual("+3", bite.Cell("bonus").Value);
            Assert.AreEqual("1d6+1 piercing", bite.Cell("damage").Value);
            Assert.AreEqual("reach 5 ft., knocks prone", bite.Cell("notes").Value, "parts past the columns stay in the last one");
            Assert.AreEqual("attack-1-damage", bite.Cell("damage").Name);
            Assert.AreEqual("attack-remove-1", bite.RemoveName);
            Assert.IsTrue(bite.Cell("bonus").IsNarrow);
            Assert.IsTrue(bite.Cell("notes").IsWide);
            Assert.IsTrue(bite.Stretches, "the notes take the rest of the row");
            Assert.AreEqual(1, vm.Header.Count);
            CollectionAssert.AreEqual(new[] { "ATTACK", "TO HIT", "DAMAGE", "NOTES" }, vm.Header[0].Cells.Select(c => c.Caption).ToArray());
            Assert.IsTrue(vm.Header[0].Cells[1].IsNarrow, "captions take their column's width");
            CollectionAssert.AreEqual(new[] { "attack-add" }, vm.Addable.Select(a => a.Name).ToArray());

            bite.Cell("bonus").Value = "+4";
            vm.Addable[0].Pick();
            bite.Remove();
            CollectionAssert.AreEqual(new[] { "0.bonus=+4", "add", "remove 0" }, sent);

            vm.ShowRows(JsonValue.Parse("[{\"name\":\"Bite\",\"bonus\":4},{}]"));
            Assert.AreSame(bite, vm.Rows[0], "a row keeps its element while its place holds");
            Assert.AreEqual("+4", bite.Cell("bonus").Value);
            Assert.AreEqual(string.Empty, vm.Rows[1].Cell("name").Value, "a new row is blank");
            CollectionAssert.IsEmpty(vm.Addable, "no add button at the field's most");

            vm.ShowRows(JsonValue.Null);
            CollectionAssert.IsEmpty(vm.Rows);
            CollectionAssert.IsEmpty(vm.Header, "no captions over nothing");
        }

        [Test]
        public void Combo_FiltersAsYouType_ShowsAtMostTwelve_AndPickingClosesIt()
        {
            var picked = new List<string>();
            var combo = new ComboViewModel("stat-kind", "Choose a kind", delegate (string k) { picked.Add(k); });
            var options = new List<KeyValuePair<string, string>>();
            for (int i = 1; i <= 15; i++) { options.Add(new KeyValuePair<string, string>("Kind " + i, "Kind " + i)); }
            options.Add(new KeyValuePair<string, string>("Beast", "Beast"));
            combo.SetOptions(options, string.Empty);

            Assert.AreEqual("Choose a kind", combo.Label);
            Assert.IsFalse(combo.HasValue);
            Assert.IsFalse(combo.Open);
            Assert.AreEqual("stat-kind-open", combo.ToggleName);
            Assert.AreEqual(ComboViewModel.MaxShown, combo.Matches.Count);
            Assert.AreEqual("4 more: keep typing", combo.MoreText);

            combo.Toggle();
            Assert.IsTrue(combo.Open);
            Assert.AreEqual(1, combo.FocusRequest, "opening puts the caret in the search box");
            combo.Query = "BEA";
            CollectionAssert.AreEqual(new[] { "stat-kind-beast" }, combo.Matches.Select(m => m.Name).ToArray());
            Assert.AreEqual(string.Empty, combo.MoreText);
            combo.Query = "zz";
            Assert.AreEqual("Nothing matches.", combo.MoreText);
            combo.Query = "beast";

            combo.Matches[0].Pick();
            CollectionAssert.AreEqual(new[] { "Beast" }, picked);
            Assert.IsFalse(combo.Open);
            Assert.AreEqual(string.Empty, combo.Query, "closing clears the search");

            combo.SetOptions(options, "beast");
            Assert.AreEqual("Beast", combo.Label, "the pick in the list's spelling");
            Assert.IsTrue(combo.HasValue);
            Assert.IsTrue(combo.Matches.Exists(m => m.Name == "stat-kind-kind-1"), "the whole list again once the search is cleared");
        }

        [Test]
        public void StatField_Choice_IsASearchableList_ThatSendsTheName()
        {
            var field = new StatBlockField { Key = "creatureType", Label = "Creature type", Type = StatBlockField.ChoiceType };
            field.Keys.AddRange(new[] { "Beast", "Humanoid", "Undead" });
            var sent = new List<string>();
            var vm = new StatFieldViewModel(field, delegate (StatFieldViewModel f, string text) { sent.Add(text); });

            vm.ShowChoice(JsonValue.FromString("Beast"));

            Assert.IsNotNull(vm.Combo);
            Assert.IsFalse(vm.IsBox, "no free text box");
            Assert.AreEqual("Beast", vm.Combo.Label);
            Assert.AreEqual("stat-creatureType-open", vm.Combo.ToggleName);
            Assert.IsTrue(vm.Combo.Matches.Single(m => m.Key == "Beast").Selected);
            vm.Combo.Matches.Single(m => m.Key == "Undead").Pick();
            CollectionAssert.AreEqual(new[] { "Undead" }, sent);
        }
    }
}
