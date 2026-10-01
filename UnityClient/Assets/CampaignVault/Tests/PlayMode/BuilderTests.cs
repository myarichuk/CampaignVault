using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Tests;
using CampaignVault.UnityClient.UI;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// Phase 2's gate: a D&amp;D 5e level-1 fighter built through the builder overlay (taps on its option cards, the
    /// ability table and the step rail) against the embedded server, asking the scripted DM on the way, and saved.
    /// Every step is photographed into Library/VaultSnapshots.
    /// </summary>
    [Category("Integration")]
    public class BuilderTests
    {
        private TableHarness _h;

        [TearDown]
        public void TearDown()
        {
            if (_h != null) { _h.Dispose(); }
        }

        private static void Step(string what) { TableHarness.Step(what); }

        private VisualElement Root { get { return _h.Ui.Root; } }

        private BuilderState B { get { return _h.State.Builder; } }

        /// <summary>A real button press (NavigationSubmit, as Enter or a gamepad would), found by element name.</summary>
        private void Press(string name)
        {
            var button = Root.Q<Button>(name);
            Assert.IsNotNull(button, "no button '" + name + "' on screen (step " + B.Current + ")");
            Assert.IsTrue(button.enabledInHierarchy, "button '" + name + "' is disabled");
            using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = button; button.SendEvent(submit); }
        }

        /// <summary>Until the draft's preview is back and the current step's options (if it has any) are loaded.</summary>
        private IEnumerator Settle()
        {
            yield return TableHarness.WaitFor(delegate
            {
                var step = B.CurrentStep;
                bool needsOptions = step != null && (step.Source.Length > 0 || step.Kind == StepKinds.Spells);
                return B.PreviewCurrent && (!needsOptions || B.Options.ContainsKey(step.Key)) && !_h.State.IsBusy("builder-commit");
            }, 20f);
            Assert.IsTrue(B.PreviewCurrent, "the preview never caught up: " + B.Error);
            yield return TableHarness.Frames(3);
        }

        private IEnumerator Pick(string optionId)
        {
            Press("opt-" + optionId);
            yield return Settle();
        }

        private IEnumerator Next(string expected)
        {
            Press("builder-next");
            yield return Settle();
            Assert.AreEqual(expected, B.Current);
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator BuildA5eFighter_ThroughTheOverlay_AndSaveIt()
        {
            _h = new TableHarness();
            yield return _h.Start();
            var s = _h.State;
            var c = _h.Controller;
            var ui = _h.Ui;

            Step("campaign");
            yield return TableHarness.WaitFor(delegate { return ui.Overlays.AnyOpen && s.CampaignsLoaded; }, 10f);
            while (ui.Overlays.CloseTop()) { }
            yield return c.BeginOnboarding("Builder Test", "The Builder Test", "Dnd5e");
            for (int i = 0; i < 30 && s.Onboarding.Phase != OnboardingPhase.Done; i++)
            {
                if (s.Onboarding.Phase == OnboardingPhase.ReadyToFinalize) { yield return c.FinalizeOnboarding(); continue; }
                var q = s.Onboarding.Question;
                Assert.IsNotNull(q, s.Onboarding.Error);
                yield return c.SubmitOnboardingAnswer(q.Type == AnswerType.Choice ? q.Options[0] : q.Type == AnswerType.YesNo ? "yes"
                    : q.Type == AnswerType.Number ? "1"
                    : q.Type == AnswerType.Party ? VaultController.PartyAnswer(OnboardingState.PartyBuildAtTable, 1, null)
                    : "A lantern-lit river town");
            }
            Assert.AreEqual(OnboardingPhase.Done, s.Onboarding.Phase, s.Onboarding.Error);
            string slug = s.CampaignSlug;
            while (ui.Overlays.CloseTop()) { }
            yield return TableHarness.Frames(3);

            Step("open from the party frames");
            Press("PartyBuild");
            Assert.IsInstanceOf<CharacterBuilderOverlay>(ui.Overlays.Top);
            yield return Settle();
            Assert.AreEqual("dnd5e", B.System);
            Assert.AreEqual("race", B.Current);
            Assert.AreEqual("1", Root.Q<Label>("builder-level").text);
            yield return _h.Snap("builder-1-race");

            yield return Pick("human");
            Assert.AreEqual("human", B.Draft.GetString("race"));

            Step("class, asking the DM");
            yield return Next("class");
            _h.Chat.Respond = delegate
            {
                return new ScriptedChat.Reply { Body = ScriptedChat.Prose("A fighter suits a river-town guard; a paladin if they swore something.\nSUGGEST: fighter, paladin, swashbuckler") };
            };
            var ask = Root.Q<TextField>("builder-ask");
            ask.value = "Something sturdy for a town guard?";
            // Bound: the text reaches the view model on the next binding update, as it does between keystroke and click.
            yield return TableHarness.Frames(2);
            Assert.AreEqual("Something sturdy for a town guard?", B.AskDraft);
            Press("builder-ask-send");
            yield return TableHarness.WaitFor(delegate { return !B.AskBusy && B.AskReply.Length > 0; }, 20f);
            Assert.AreEqual(string.Empty, B.AskError);
            CollectionAssert.AreEqual(new[] { "fighter", "paladin" }, B.Suggested);
            CollectionAssert.AreEqual(new[] { "swashbuckler" }, B.NotOptions, "an id that isn't an option is shown, not dropped");
            Assert.IsTrue(s.Onboarding.BrainstormChat.Any(m => m.Value == "Building a character · Class"), "a divider marks the builder in the setup conversation");
            yield return TableHarness.Frames(3);
            yield return _h.Snap("builder-2-class-ask");
            Press("use-fighter");
            yield return Settle();
            Assert.AreEqual("fighter", B.Draft.GetString("class"));
            Assert.IsNull(B.Step("spells"), "a fighter has no spells step");
            yield return _h.Snap("builder-2-class");

            yield return Next("background");
            yield return Pick("soldier");
            yield return _h.Snap("builder-3-background");

            Step("abilities");
            yield return Next("abilities");
            Press("method-standardArray");
            yield return Settle();
            // 15 14 13 12 10 8 → Str Dex Con Cha Wis Int.
            string[] order = { "Strength", "Dexterity", "Constitution", "Charisma", "Wisdom", "Intelligence" };
            for (int i = 0; i < order.Length; i++)
            {
                Press("assign-" + order[i] + "-" + i);
                yield return Settle();
            }
            Assert.AreEqual(6, B.Abilities.Assigned.Count);
            Assert.IsFalse(B.IssuesFor("abilities", false).Any(), string.Join(" ", B.IssuesFor("abilities", false).Select(e => e.Message).ToArray()));
            Assert.AreEqual(16, B.Preview.Abilities.First(a => a.Name == "Strength").Score, "human +1 on the base 15");
            yield return _h.Snap("builder-4-abilities");

            Step("skills, then a class change clears them");
            yield return Next("skills");
            Assert.IsNull(Root.Q<Button>("opt-Athletics"), "the soldier's own skills aren't offered");
            yield return Pick("Perception");
            yield return Pick("Survival");
            Assert.AreEqual(2, B.Draft.GetList("skills").Count);
            yield return _h.Snap("builder-5-skills");

            Press("step-class");
            yield return Settle();
            yield return Pick("barbarian");
            Assert.IsFalse(B.Draft.Has("skills"));
            Assert.AreEqual("Changing class cleared: skills.", Root.Q<Label>("builder-cleared").text);
            yield return _h.Snap("builder-6-cleared");
            yield return Pick("fighter");
            Press("step-skills");
            yield return Settle();
            yield return Pick("Perception");
            yield return Pick("Survival");

            Step("identity");
            yield return Next("identity");
            Root.Q<TextField>("identity-name").value = "Aric Thorne";
            Root.Q<TextField>("identity-concept").value = "A town guard who stayed when the others ran.";
            Root.Q<TextField>("identity-look").value = "Broad, scarred knuckles, a lantern badge.";
            // The fields are bound: the values reach the draft on the panel's next binding update.
            yield return TableHarness.Frames(3);
            Assert.AreEqual("Aric Thorne", B.Draft.Name);
            yield return c.BuilderPreview();
            yield return Settle();
            Assert.AreEqual(0, B.Errors.Count, string.Join(" ", B.Errors.Select(e => "[" + e.Step + "] " + e.Message).ToArray()));
            Assert.AreEqual("Ready to save.", Root.Q<Label>("builder-status").text);
            Assert.AreEqual(12, (int)B.Preview.MaxHp, "d10 + Con 14 (+2)");
            yield return _h.Snap("builder-7-identity");

            Step("save");
            Press("builder-commit");
            yield return TableHarness.WaitFor(delegate { return B.CommittedId.Length > 0 || B.Error.Length > 0; }, 30f);
            Assert.AreEqual(string.Empty, B.Error);
            string id = B.CommittedId;
            StringAssert.StartsWith("chars/aric-thorne-", id);
            Assert.AreEqual(id, s.PcId, "the first character built is the one the player plays");
            yield return TableHarness.Frames(3);
            yield return _h.Snap("builder-8-saved");

            McpOutcome<JsonValue> stored = null;
            yield return c.GetCharacter(id, delegate (McpOutcome<JsonValue> o) { stored = o; });
            Assert.IsTrue(stored.Ok, stored.ErrorMessage);
            var sheet = CharacterSheet.FromPayload(stored.Data);
            Assert.IsTrue(sheet.IsPc);
            Assert.AreEqual(12, (int)sheet.MaxHp);
            StringAssert.Contains("Fighter", sheet.ClassLine);
            Assert.IsTrue(sheet.Skills.Any(k => k.Name == "Perception" && k.Rank > 0), "the picked class skill is proficient");
            Assert.IsTrue(sheet.Skills.Any(k => k.Name == "Athletics" && k.Rank > 0), "the soldier's skill is proficient");

            Step("cleanup");
            while (ui.Overlays.CloseTop()) { }
            yield return c.DeleteCampaign(slug);
        }
    }
}
