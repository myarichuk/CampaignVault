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
            _s.Builder.Draft.Level = VaultController.MaxBuilderLevel;
            _s.Notify(StateArea.Builder);

            Assert.IsFalse(_vm.CanLevelUp);
            Assert.AreEqual(string.Empty, _vm.LevelUpHint);
            StringAssert.Contains("Levels above " + VaultController.MaxBuilderLevel, _vm.LevelCapReason);
            Assert.AreEqual(VaultController.MaxBuilderLevel.ToString(), _vm.LevelText);
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
    }
}
