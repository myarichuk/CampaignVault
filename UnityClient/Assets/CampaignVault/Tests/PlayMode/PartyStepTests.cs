using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.UI;

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
            B.Draft.Set("background", JsonValue.FromString("soldier"));
            B.Draft.Set("abilities", JsonValue.Parse("{\"method\":\"standardArray\",\"scores\":{\"Strength\":15,\"Dexterity\":14,\"Constitution\":13,\"Intelligence\":8,\"Wisdom\":10,\"Charisma\":12}}"));
            B.Draft.Set("skills", JsonValue.Parse("[\"Perception\",\"Survival\"]"));
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
            Assert.IsFalse(Root.Q<Button>("party-dm").enabledSelf, "drafting would leave the built character behind");
            yield return _h.Snap("p4-party-built");

            Press("party-edit-aric-thorne");
            Assert.IsInstanceOf<CharacterBuilderOverlay>(ui.Overlays.Top);
            yield return TableHarness.WaitFor(delegate { return B.Steps.Count > 0 && B.PreviewCurrent; }, 20f);
            Assert.AreEqual(id, B.CommittedId, "EDIT saves over the same character");
            Assert.AreEqual("Aric Thorne", B.Draft.Name);
            Assert.AreEqual("fighter", B.Draft.GetString("class"));
            ui.Overlays.CloseTop();
            yield return TableHarness.Frames(8);

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
            StringAssert.Contains("already built", s.Onboarding.SeedBrief);
            StringAssert.Contains("Do NOT world_build", s.Onboarding.SeedBrief);
            yield return _h.Snap("p4-party-done");

            while (ui.Overlays.CloseTop()) { }
            yield return c.DeleteCampaign("party-test");
        }
    }
}
