using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI;
using CampaignVault.UnityClient.UI.Builder;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// Phase 4's gate: the onboarding party step. A fighter is built in the builder (opened over the setup, for the
    /// campaign being set up), appears as a card, can be edited, and goes into the answer; the brief the server writes
    /// names the built character and tells the DM not to build it again.
    /// </summary>
    [Category("Integration")]
    public class PartyStepTests
    {
        private TableHarness _h;

        [TearDown]
        public void TearDown()
        {
            if (_h != null) { _h.Dispose(); }
        }

        private VisualElement Root { get { return _h.Ui.Root; } }

        private BuilderState B { get { return _h.State.Builder; } }

        private void Press(string name)
        {
            var button = Root.Q<Button>(name);
            Assert.IsNotNull(button, "no button '" + name + "' on screen");
            Assert.IsTrue(button.enabledInHierarchy, "button '" + name + "' is disabled");
            using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = button; button.SendEvent(submit); }
        }

        /// <summary>On screen: there, and neither it nor anything above it hidden (a closed list keeps its elements).</summary>
        private bool Shown(string name)
        {
            for (var e = Root.Q(name); e != null; e = e.parent)
            {
                if (e.resolvedStyle.display == DisplayStyle.None) { return false; }
                if (e == Root) { return true; }
            }
            return false;
        }

        private static string AnswerFor(OnboardingQuestion q)
        {
            return q.Type == AnswerType.Choice ? q.Options[0] : q.Type == AnswerType.YesNo ? "yes"
                : q.Type == AnswerType.Number ? "1" : "A drowned mill town under a copper sky";
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator PartyStep_BuildsACharacterInTheBuilder_AndTheBriefDoesNotRebuildIt()
        {
            _h = new TableHarness();
            yield return _h.Start();
            var s = _h.State;
            var c = _h.Controller;
            var ui = _h.Ui;
            yield return TableHarness.Frames(3);

            TableHarness.Step("onboarding up to the party question");
            yield return TableHarness.WaitFor(delegate { return ui.Overlays.AnyOpen && s.CampaignsLoaded; }, 10f);
            while (ui.Overlays.CloseTop()) { }
            yield return c.BeginOnboarding("Party Test", "The Party Test", "Dnd5e");
            for (int i = 0; i < 30 && s.Onboarding.Phase == OnboardingPhase.Question && s.Onboarding.Question.Key != "party"; i++)
            {
                yield return c.SubmitOnboardingAnswer(AnswerFor(s.Onboarding.Question));
            }
            Assert.AreEqual(OnboardingPhase.Question, s.Onboarding.Phase, s.Onboarding.Error);
            Assert.AreEqual("party", s.Onboarding.Question.Key);
            Assert.AreEqual(AnswerType.Party, s.Onboarding.Question.Type);

            ui.OpenOnboarding();
            yield return TableHarness.Frames(4);
            Assert.IsNotNull(Root.Q<Button>("party-add"));
            Assert.IsNull(Root.Q<Button>("party-use"), "nothing to use yet");
            yield return _h.Snap("p4-party-empty");

            TableHarness.Step("add a character: the builder opens over the setup, for this campaign");
            Press("party-add");
            Assert.IsInstanceOf<CharacterBuilderOverlay>(ui.Overlays.Top);
            yield return TableHarness.WaitFor(delegate { return B.Steps.Count > 0 && B.PreviewCurrent; }, 20f);
            Assert.IsTrue(B.ForOnboarding);
            Assert.AreEqual("party-test", B.Slug);
            Assert.AreEqual("dnd5e", B.System);
            Assert.AreEqual(1, B.Draft.Level);

            B.Draft.Set("race", JsonValue.FromString("human"));
            B.Draft.Set("class", JsonValue.FromString("fighter"));
            B.Draft.Set("background", JsonValue.FromString("acolyte"));
            B.Draft.Set("abilities", JsonValue.Parse("{\"method\":\"standardArray\",\"scores\":{\"Strength\":15,\"Dexterity\":14,\"Constitution\":13,\"Intelligence\":8,\"Wisdom\":10,\"Charisma\":12}}"));
            B.Draft.Set("skills", JsonValue.Parse("[\"Perception\",\"Survival\"]"));
            B.Draft.Set("levels", JsonValue.Parse("{\"1.fightingStyle\":\"defense\"}"));
            c.BuilderIdentity("Aric Thorne", "A town guard who stayed when the others ran.", "Broad, scarred knuckles.");
            yield return c.BuilderSteps();
            yield return c.BuilderPreview();
            Assert.AreEqual(0, B.Errors.Count, B.Errors.Count > 0 ? "[" + B.Errors[0].Step + "] " + B.Errors[0].Message : string.Empty);
            yield return TableHarness.Frames(3);
            Press("builder-commit");
            yield return TableHarness.WaitFor(delegate { return B.CommittedId.Length > 0 || B.Error.Length > 0; }, 30f);
            Assert.AreEqual(string.Empty, B.Error);
            string id = B.CommittedId;
            Assert.AreEqual(1, s.Onboarding.Party.Count);
            Assert.AreEqual("Aric Thorne", s.Onboarding.Party[0].Name);
            Assert.AreEqual(string.Empty, s.PcId, "a character for the campaign being set up isn't the one at the table");

            TableHarness.Step("back on the party step: the card, EDIT, and USE THIS PARTY");
            ui.Overlays.CloseTop();
            yield return TableHarness.Frames(8);
            Assert.IsInstanceOf<OnboardingOverlay>(ui.Overlays.Top);
            Assert.IsNotNull(Root.Q<Button>("party-edit-aric-thorne"));
            Assert.IsNotNull(Root.Q<Button>("party-use"));
            Assert.IsTrue(Root.Q<Button>("party-dm").enabledSelf, "with a character built, the DM can draft companions around it");
            Assert.IsFalse(Root.Q<Button>("party-table").enabledSelf, "skipping would leave the built character behind");
            yield return _h.Snap("p4-party-built");

            Press("party-edit-aric-thorne");
            Assert.IsInstanceOf<CharacterBuilderOverlay>(ui.Overlays.Top);
            yield return TableHarness.WaitFor(delegate { return B.Steps.Count > 0 && B.PreviewCurrent; }, 20f);
            Assert.AreEqual(id, B.CommittedId, "EDIT saves over the same character");
            Assert.AreEqual("Aric Thorne", B.Draft.Name);
            Assert.AreEqual("fighter", B.Draft.GetString("class"));
            ui.Overlays.CloseTop();
            yield return TableHarness.Frames(8);

            TableHarness.Step("add a companion from a template: the stat block fills in, and it joins as a companion");
            Press("party-add-companion");
            Assert.IsInstanceOf<CharacterBuilderOverlay>(ui.Overlays.Top);
            yield return TableHarness.WaitFor(delegate { return B.Steps.Count > 0 && B.PreviewCurrent && B.Options.ContainsKey("statblock"); }, 20f);
            Assert.AreEqual("companion", B.Draft.Kind);
            Assert.AreEqual(1, B.Draft.PartyLevel);
            yield return TableHarness.Frames(3);
            Assert.IsFalse(Shown("template-mastiff"), "the creatures are behind the search");
            Press("template-open");
            yield return TableHarness.Frames(3);
            Press("template-mastiff");
            yield return TableHarness.WaitFor(delegate { return B.PreviewCurrent && B.Draft.Has("statblock"); }, 20f);
            Assert.AreEqual("Mastiff", B.Draft.Name);
            Assert.AreEqual(5, (int)B.Draft.Get("statblock").Get("statBlockHp").NumberValue);
            Assert.AreEqual(0, B.Errors.Count, B.Errors.Count > 0 ? "[" + B.Errors[0].Step + "] " + B.Errors[0].Message : string.Empty);
            Assert.AreEqual(3, (int)B.Draft.Get("statblock").Get("skillModifiers").Get("Perception").NumberValue, "the template's \"Perception +3\" is a skill, not text");
            Assert.AreEqual("1/8", B.Draft.Get("statblock").GetString("challengeRating", string.Empty));
            Assert.AreEqual("Beast", B.Draft.Get("statblock").GetString("creatureType", string.Empty));

            TableHarness.Step("creature type: a searchable list");
            yield return TableHarness.Frames(3);
            Assert.IsFalse(Shown("template-mastiff"), "picking a creature closes its list");
            Press("stat-creatureType-open");
            yield return TableHarness.Frames(3);
            Root.Q<TextField>("stat-creatureType-search").value = "on";
            yield return TableHarness.Frames(4);
            Assert.IsTrue(Shown("stat-creatureType-construct"));
            Assert.IsNull(Root.Q("stat-creatureType-beast"), "filtered out by the search");
            var typeScroller = Root.Q("stat-creatureType-open").GetFirstAncestorOfType<ScrollView>();
            if (typeScroller != null) { typeScroller.ScrollTo(Root.Q("stat-creatureType-construct")); }
            yield return TableHarness.Frames(4);
            yield return _h.Snap("p5-companion-type");
            Press("stat-creatureType-monstrosity");
            yield return TableHarness.WaitFor(delegate { return B.PreviewCurrent && B.Draft.Get("statblock").GetString("creatureType", string.Empty) == "Monstrosity"; }, 20f);
            Assert.AreEqual(0, B.Errors.Count, B.Errors.Count > 0 ? "[" + B.Errors[0].Step + "] " + B.Errors[0].Message : string.Empty);
            yield return TableHarness.Frames(3);
            Assert.IsFalse(Shown("stat-creatureType-search"), "picking closes the list");
            yield return TableHarness.Frames(3);
            Assert.IsNotNull(Root.Q("skill-perception"), "a row per skill set");
            Assert.IsNull(Root.Q("skill-add-perception"), "no add button for a skill already set");
            var str = Root.Q("stat-strength");
            var cha = Root.Q("stat-charisma");
            Assert.AreEqual(str.worldBound.y, cha.worldBound.y, 1f, "the six scores sit in one row");
            Assert.Less(str.worldBound.width, 200f, "a score is a small box, not a full-width line");

            TableHarness.Step("skills: add one, edit its bonus, take it off");
            Press("skill-add-stealth");
            yield return TableHarness.WaitFor(delegate { return B.PreviewCurrent && B.Draft.Get("statblock").Get("skillModifiers").Get("Stealth").Kind == JsonKind.Number; }, 20f);
            Assert.AreEqual(StatFieldViewModel.NewModifier, (int)B.Draft.Get("statblock").Get("skillModifiers").Get("Stealth").NumberValue);
            StatBlockField skillsField = null;
            foreach (var f in B.StatBlocks[0].Fields) { if (f.Key == "skillModifiers") { skillsField = f; } }
            yield return c.BuilderStatModifier("statblock", skillsField, "stealth", "+4");
            yield return c.BuilderPreview();
            Assert.AreEqual(4, (int)B.Draft.Get("statblock").Get("skillModifiers").Get("Stealth").NumberValue, "names keep the server's spelling");
            Assert.AreEqual(0, B.Errors.Count, B.Errors.Count > 0 ? "[" + B.Errors[0].Step + "] " + B.Errors[0].Message : string.Empty);
            yield return TableHarness.Frames(6);
            var stealthRow = Root.Q("skill-stealth");
            Assert.IsNotNull(stealthRow);
            var scroller = stealthRow.GetFirstAncestorOfType<ScrollView>();
            // The last add button, so the rows above it and the buttons are both in the picture.
            if (scroller != null) { scroller.ScrollTo(Root.Q("skill-add-survival")); }
            yield return TableHarness.Frames(4);
            yield return _h.Snap("p5-companion-skills");
            Press("skill-remove-stealth");
            yield return TableHarness.WaitFor(delegate { return B.PreviewCurrent && B.Draft.Get("statblock").Get("skillModifiers").Get("Stealth").Kind == JsonKind.Null; }, 20f);
            yield return TableHarness.Frames(3);
            Assert.IsNull(Root.Q("skill-stealth"));
            Assert.IsNotNull(Root.Q("skill-add-stealth"));

            TableHarness.Step("attacks: the template's bite is a row; add one, fill it in, take it off");
            Assert.AreEqual("Bite", Root.Q<TextField>("attack-1-name").value);
            Assert.AreEqual("+3", Root.Q<TextField>("attack-1-bonus").value);
            Press("attack-add");
            yield return TableHarness.WaitFor(delegate { return B.PreviewCurrent && B.Draft.Get("statblock").Get("attacks").ArrayValue.Count == 2; }, 20f);
            StatBlockField attacksField = null;
            foreach (var f in B.StatBlocks[0].Fields) { if (f.Key == "attacks") { attacksField = f; } }
            Assert.IsTrue(B.Errors.Exists(delegate (BuilderIssue e) { return e.Message == "Attacks: row 2 needs its attack."; }), "a blank row asks for its name");
            yield return c.BuilderStatRowCell("statblock", attacksField, 1, attacksField.Columns[0], "Shove");
            yield return c.BuilderStatRowCell("statblock", attacksField, 1, attacksField.Columns[1], "+3");
            yield return c.BuilderStatRowCell("statblock", attacksField, 1, attacksField.Columns[2], "1d4 bludgeoning");
            yield return c.BuilderPreview();
            Assert.AreEqual(0, B.Errors.Count, B.Errors.Count > 0 ? "[" + B.Errors[0].Step + "] " + B.Errors[0].Message : string.Empty);
            yield return TableHarness.Frames(6);
            Assert.AreEqual("Shove", Root.Q<TextField>("attack-2-name").value);
            var attackScroller = Root.Q("attack-add").GetFirstAncestorOfType<ScrollView>();
            if (attackScroller != null) { attackScroller.ScrollTo(Root.Q("attack-add")); }
            yield return TableHarness.Frames(4);
            yield return _h.Snap("p5-companion-attacks");
            Press("attack-remove-2");
            yield return TableHarness.WaitFor(delegate { return B.PreviewCurrent && B.Draft.Get("statblock").Get("attacks").ArrayValue.Count == 1; }, 20f);
            yield return TableHarness.Frames(3);
            Assert.IsNull(Root.Q("attack-2-name"));
            yield return _h.Snap("p5-companion-template");
            Press("builder-commit");
            yield return TableHarness.WaitFor(delegate { return s.Onboarding.Party.Count == 2 || B.Error.Length > 0; }, 30f);
            Assert.AreEqual(string.Empty, B.Error);
            Assert.AreEqual("companion", s.Onboarding.Party[1].Kind);
            string companionId = s.Onboarding.Party[1].Id;
            ui.Overlays.CloseTop();
            yield return TableHarness.Frames(8);
            Assert.IsNotNull(Root.Q<Button>("party-edit-mastiff"));
            yield return _h.Snap("p5-party-with-companion");

            TableHarness.Step("the DM drafts companions (a canned reply for the one call): a card to review, USE waits for it");
            yield return c.ApplyCompanionDrafts("```json\n[{\"name\":\"Brann Holt\",\"concept\":\"Aric's sergeant in the Copper Watch; he owes Aric a life.\","
                + "\"look\":\"Grey beard, dented helm.\",\"level\":5,\"statblock\":{\"statBlockHp\":16,\"armorClass\":16,\"movement\":\"30 ft\","
                + "\"skillModifiers\":{\"athletics\":\"+3\"},\"attacks\":\"Spear +3, 1d6+1 piercing\",\"stance\":\"Loyal to Aric, wary of the mastiff\"}}]\n```");
            Assert.AreEqual(string.Empty, s.Onboarding.DraftError);
            var drafted = s.Onboarding.Party[2];
            Assert.IsTrue(drafted.Pending);
            Assert.AreEqual(2, drafted.Draft.Level, "drafted at 5, moved into the party's band");
            CollectionAssert.Contains(drafted.Issues, "Drafted at level 5, set to 2.");
            CollectionAssert.Contains(drafted.Issues, "Fix: Speed (ft): must be a whole number.", "the preview's objection is on the card before review");
            yield return TableHarness.Frames(8);
            Assert.IsFalse(Root.Q<Button>("party-use").enabledSelf, "a draft isn't saved yet");
            Assert.IsNotNull(Root.Q<Button>("party-discard"));
            yield return _h.Snap("p5-party-drafted");

            Press("party-review-brann-holt");
            Assert.IsInstanceOf<CharacterBuilderOverlay>(ui.Overlays.Top);
            yield return TableHarness.WaitFor(delegate { return B.Steps.Count > 0 && B.PreviewCurrent; }, 20f);
            Assert.AreEqual(string.Empty, B.CommittedId, "a draft has nothing saved to update");
            Assert.AreEqual("Brann Holt", B.Draft.Name);
            Assert.AreEqual(16, (int)B.Draft.Get("statblock").Get("statBlockHp").NumberValue);
            Assert.AreEqual(3, (int)B.Draft.Get("statblock").Get("skillModifiers").Get("Athletics").NumberValue, "the model's \"athletics\": \"+3\" read as the Athletics skill");
            // "30 ft" in a number field: flagged on the stat block, not fixed behind the player's back.
            Assert.AreEqual(1, B.Errors.Count);
            Assert.AreEqual("statblock", B.Errors[0].Step);
            StringAssert.Contains("whole number", B.Errors[0].Message);
            yield return TableHarness.Frames(3);
            yield return _h.Snap("p5-draft-review");
            StatBlockField speed = null;
            foreach (var f in B.StatBlocks[0].Fields) { if (f.Key == "movement") { speed = f; } }
            yield return c.BuilderStatField("statblock", speed, "30");
            yield return c.BuilderPreview();
            Assert.AreEqual(0, B.Errors.Count, B.Errors.Count > 0 ? "[" + B.Errors[0].Step + "] " + B.Errors[0].Message : string.Empty);
            yield return TableHarness.Frames(3);
            Press("builder-commit");
            yield return TableHarness.WaitFor(delegate { return !s.Onboarding.Party[2].Pending || B.Error.Length > 0; }, 30f);
            Assert.AreEqual(string.Empty, B.Error);
            Assert.AreEqual(3, s.Onboarding.Party.Count, "the reviewed draft replaces its card");
            string draftedId = s.Onboarding.Party[2].Id;
            StringAssert.StartsWith("chars/", draftedId);
            ui.Overlays.CloseTop();
            yield return TableHarness.Frames(8);
            Assert.IsNotNull(Root.Q<Button>("party-edit-brann-holt"));
            Assert.IsTrue(Root.Q<Button>("party-use").enabledSelf);

            TableHarness.Step("use the party, finish the setup");
            Press("party-use");
            for (int i = 0; i < 30 && s.Onboarding.Phase != OnboardingPhase.Done; i++)
            {
                yield return TableHarness.WaitFor(delegate { return s.Onboarding.Phase != OnboardingPhase.Working; }, 20f);
                if (s.Onboarding.Phase == OnboardingPhase.ReadyToFinalize) { yield return c.FinalizeOnboarding(); continue; }
                Assert.AreEqual(OnboardingPhase.Question, s.Onboarding.Phase, s.Onboarding.Error);
                yield return c.SubmitOnboardingAnswer(AnswerFor(s.Onboarding.Question));
            }
            Assert.AreEqual(OnboardingPhase.Done, s.Onboarding.Phase, s.Onboarding.Error);
            StringAssert.Contains(id, s.Onboarding.SeedBrief);
            StringAssert.Contains(companionId, s.Onboarding.SeedBrief);
            StringAssert.Contains(draftedId, s.Onboarding.SeedBrief);
            StringAssert.Contains("already built", s.Onboarding.SeedBrief);
            StringAssert.Contains("Do NOT world_build", s.Onboarding.SeedBrief);
            yield return _h.Snap("p4-party-done");

            while (ui.Overlays.CloseTop()) { }
            yield return c.DeleteCampaign("party-test");
        }

        /// <summary>Goes to a step, waits for its options, presses an option card, and waits for the draft and preview.</summary>
        private IEnumerator Pick(string step, string option)
        {
            _h.Controller.BuilderGoTo(step);
            yield return TableHarness.WaitFor(delegate { return B.Current == step && B.Options.ContainsKey(step); }, 20f);
            yield return TableHarness.Frames(3);
            Press("opt-" + option);
            yield return TableHarness.WaitFor(delegate
            {
                return B.PreviewCurrent && (B.Draft.GetString(step) == option || B.Draft.GetList(step).Contains(option));
            }, 20f);
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Pf2eParty_BuildsAFighterStepByStep_AndAStatBlockCompanion()
        {
            _h = new TableHarness();
            yield return _h.Start();
            var s = _h.State;
            var c = _h.Controller;
            var ui = _h.Ui;
            yield return TableHarness.Frames(3);

            TableHarness.Step("a Pathfinder 2e campaign, up to the party question");
            yield return TableHarness.WaitFor(delegate { return ui.Overlays.AnyOpen && s.CampaignsLoaded; }, 10f);
            while (ui.Overlays.CloseTop()) { }
            yield return c.BeginOnboarding("Pf2e Party", "The Pf2e Party", "Pathfinder2e");
            for (int i = 0; i < 30 && s.Onboarding.Phase == OnboardingPhase.Question && s.Onboarding.Question.Key != "party"; i++)
            {
                yield return c.SubmitOnboardingAnswer(AnswerFor(s.Onboarding.Question));
            }
            Assert.AreEqual("party", s.Onboarding.Question.Key, s.Onboarding.Error);
            ui.OpenOnboarding();
            yield return TableHarness.Frames(4);
            Press("party-add");
            yield return TableHarness.WaitFor(delegate { return B.Steps.Count > 0 && B.PreviewCurrent; }, 20f);
            Assert.AreEqual("pf2e", B.System);

            TableHarness.Step("every PF2e step draws a widget");
            foreach (var step in new System.Collections.Generic.List<BuilderStep>(B.Steps))
            {
                c.BuilderGoTo(step.Key);
                yield return TableHarness.Frames(3);
                Assert.IsNull(Root.Q("builder-unsupported"), "step '" + step.Key + "' (" + step.Kind + ") has no widget");
            }

            TableHarness.Step("a dwarf fighter, through the option cards");
            yield return Pick("ancestry", "dwarf");
            yield return Pick("heritage", "rock_dwarf");
            yield return Pick("background", "martial_disciple");
            Assert.IsNotNull(B.Step("backgroundSkill"), "Martial Disciple trains Acrobatics or Athletics: a step to pick it");
            yield return Pick("backgroundSkill", "Athletics");
            yield return Pick("class", "fighter");
            yield return Pick("ancestryBoosts", "Strength");
            Assert.IsFalse(B.Options["ancestryBoosts"].Options.Exists(delegate (BuilderOption o) { return o.Id == "Constitution"; }),
                "the dwarf boosts Constitution already");
            yield return Pick("backgroundBoosts", "Strength");
            yield return Pick("backgroundBoosts", "Dexterity");
            yield return Pick("keyAbility", "Strength");
            foreach (string ability in new[] { "Strength", "Dexterity", "Constitution", "Wisdom" }) { yield return Pick("boosts", ability); }
            yield return TableHarness.Frames(3);
            yield return _h.Snap("p6-pf2e-boosts");
            foreach (string skill in new[] { "Intimidation", "Medicine", "Survival", "Society" }) { yield return Pick("skills", skill); }
            Assert.AreEqual(4, B.Options["skills"].Count, "a fighter's 3 more plus Acrobatics-or-Athletics, Int +0");
            yield return Pick("ancestryFeats", "dwarven_lore");
            yield return Pick("classFeats", "reactive_shield");
            Assert.IsNull(B.Step("skillFeats"), "a level-1 fighter has no skill feat to pick (the background's)");
            yield return TableHarness.Frames(3);
            yield return _h.Snap("p6-pf2e-class-feats");

            c.BuilderIdentity("Brakka Stonefist", "Drilled at the old war school.", "Braided beard, notched shield.");
            yield return c.BuilderSteps();
            yield return c.BuilderPreview();
            Assert.AreEqual(0, B.Errors.Count, B.Errors.Count > 0 ? "[" + B.Errors[0].Step + "] " + B.Errors[0].Message : string.Empty);
            yield return TableHarness.Frames(3);
            yield return _h.Snap("p6-pf2e-fighter");
            Press("builder-commit");
            yield return TableHarness.WaitFor(delegate { return B.CommittedId.Length > 0 || B.Error.Length > 0; }, 30f);
            Assert.AreEqual(string.Empty, B.Error);
            Assert.AreEqual("Brakka Stonefist", s.Onboarding.Party[0].Name);
            ui.Overlays.CloseTop();
            yield return TableHarness.Frames(8);

            TableHarness.Step("a companion: the PF2e stat block (modifiers, saves, Perception, Strikes)");
            Press("party-add-companion");
            yield return TableHarness.WaitFor(delegate { return B.Steps.Count > 0 && B.PreviewCurrent && B.StatBlocks.Count > 0; }, 20f);
            yield return TableHarness.Frames(3);
            Assert.IsNotNull(Root.Q("stat-strengthMod"));
            Assert.IsNotNull(Root.Q("skill-add-fortitude"), "saves are a list of their own");
            Assert.IsNotNull(Root.Q("skill-add-perception"), "Perception is among a creature's skills");
            StatBlockField hp = null;
            StatBlockField strength = null;
            foreach (var f in B.StatBlocks[0].Fields)
            {
                if (f.Key == "statBlockHp") { hp = f; }
                if (f.Key == "strengthMod") { strength = f; }
            }
            c.BuilderIdentity("Ash", "The ranger's wolf.", "Grey, one torn ear.");
            yield return c.BuilderStatField("statblock", hp, "24");
            yield return c.BuilderStatField("statblock", strength, "3");
            Press("skill-add-fortitude");
            yield return TableHarness.WaitFor(delegate { return B.PreviewCurrent && B.Draft.Get("statblock").Get("savingThrowModifiers").Get("Fortitude").Kind == JsonKind.Number; }, 20f);
            Press("skill-add-perception");
            yield return TableHarness.WaitFor(delegate { return B.PreviewCurrent && B.Draft.Get("statblock").Get("skillModifiers").Get("Perception").Kind == JsonKind.Number; }, 20f);
            yield return c.BuilderPreview();
            Assert.AreEqual(2, B.Preview.Perception, "the stat block's Perception, read from its skills");
            Assert.AreEqual(0, B.Errors.Count, B.Errors.Count > 0 ? "[" + B.Errors[0].Step + "] " + B.Errors[0].Message : string.Empty);
            yield return TableHarness.Frames(6);
            yield return _h.Snap("p6-pf2e-companion");
            Press("builder-commit");
            yield return TableHarness.WaitFor(delegate { return s.Onboarding.Party.Count == 2 || B.Error.Length > 0; }, 30f);
            Assert.AreEqual(string.Empty, B.Error);
            Assert.AreEqual("companion", s.Onboarding.Party[1].Kind);

            while (ui.Overlays.CloseTop()) { }
            yield return c.DeleteCampaign("pf2e-party");
        }

        /// <summary>Phase 7's gate: a Narrative party step builds and commits a character with no stat fields.</summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator NarrativeParty_BuildsWhoTheyAre_WithNoStats()
        {
            _h = new TableHarness();
            yield return _h.Start();
            var s = _h.State;
            var c = _h.Controller;
            var ui = _h.Ui;
            yield return TableHarness.Frames(3);

            TableHarness.Step("a Narrative campaign, up to the party question");
            yield return TableHarness.WaitFor(delegate { return ui.Overlays.AnyOpen && s.CampaignsLoaded; }, 10f);
            while (ui.Overlays.CloseTop()) { }
            yield return c.BeginOnboarding("Narrative Party", "The Narrative Party", "Narrative");
            for (int i = 0; i < 30 && s.Onboarding.Phase == OnboardingPhase.Question && s.Onboarding.Question.Key != "party"; i++)
            {
                yield return c.SubmitOnboardingAnswer(AnswerFor(s.Onboarding.Question));
            }
            Assert.AreEqual("party", s.Onboarding.Question.Key, s.Onboarding.Error);
            ui.OpenOnboarding();
            yield return TableHarness.Frames(4);
            Assert.IsFalse(Shown("party-level-1"), "a narrative game has no level");
            Press("party-add");
            yield return TableHarness.WaitFor(delegate { return B.Steps.Count > 0 && B.PreviewCurrent && B.StatBlocks.Count > 0; }, 20f);
            Assert.AreEqual("narrative", B.System);
            Assert.AreEqual(1, B.Steps.Count, "who they are is the whole recipe");
            yield return TableHarness.Frames(3);
            Assert.IsNotNull(Root.Q("identity-name"));
            Assert.IsNotNull(Root.Q("stat-descriptors"));
            Assert.IsNull(Root.Q("stat-strength"), "no stat fields");

            TableHarness.Step("name, concept, look and nature");
            c.BuilderIdentity("Wren Hollis", "A ferry pilot looking for her brother.", "Tar-black hands, a coat two sizes too big.");
            var fields = new System.Collections.Generic.Dictionary<string, StatBlockField>();
            foreach (var f in B.StatBlocks[0].Fields) { fields[f.Key] = f; }
            yield return c.BuilderStatField("identity", fields["descriptors"], "Wry, restless");
            yield return c.BuilderPreview();
            Assert.AreEqual(1, B.Errors.Count, "two descriptors of three");
            StringAssert.Contains("you gave 2", B.Errors[0].Message);
            yield return c.BuilderStatField("identity", fields["descriptors"], "Wry, restless, loyal to a fault");
            yield return c.BuilderStatField("identity", fields["drives"], "Find her brother");
            yield return c.BuilderStatField("identity", fields["fears"], "Deep water; being forgotten");
            yield return c.BuilderPreview();
            Assert.AreEqual(0, B.Errors.Count, B.Errors.Count > 0 ? "[" + B.Errors[0].Step + "] " + B.Errors[0].Message : string.Empty);
            Assert.IsFalse(B.Preview.HasStats);
            Assert.AreEqual(3, B.Preview.NatureLines().Count);
            yield return TableHarness.Frames(6);
            yield return _h.Snap("p7-narrative-pc");

            Press("builder-commit");
            yield return TableHarness.WaitFor(delegate { return B.CommittedId.Length > 0 || B.Error.Length > 0; }, 30f);
            Assert.AreEqual(string.Empty, B.Error);
            Assert.AreEqual("Wren Hollis", s.Onboarding.Party[0].Name);
            ui.Overlays.CloseTop();
            yield return TableHarness.Frames(8);
            yield return _h.Snap("p7-narrative-party");

            while (ui.Overlays.CloseTop()) { }
            yield return c.DeleteCampaign("narrative-party");
        }
    }
}
