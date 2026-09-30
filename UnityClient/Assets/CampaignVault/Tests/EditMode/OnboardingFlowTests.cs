using System.Collections.Generic;
using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.UI;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>Onboarding → seeding → first scene, brainstorming, and the provider gate: the pure seams.</summary>
    public class OnboardingFlowTests
    {
        private static JsonValue Parse(string json)
        {
            JsonValue v;
            Assert.IsTrue(JsonValue.TryParse(json, out v), json);
            return v;
        }

        [Test]
        public void ParseQuestion_NumberType_FromEnumIndexOrName()
        {
            Assert.AreEqual(AnswerType.Number, VaultController.ParseQuestion(Parse("{\"key\":\"starting_level\",\"answerType\":4}")).Type);
            Assert.AreEqual(AnswerType.Number, VaultController.ParseQuestion(Parse("{\"answerType\":\"Number\"}")).Type);
        }

        [Test]
        public void ReadAnswers_TakesStringsAndNumbers()
        {
            var into = new Dictionary<string, string> { { "stale", "x" } };
            VaultController.ReadAnswers(Parse("{\"collectedAnswers\":{\"tone\":\"grim\",\"starting_level\":3}}"), into);
            Assert.AreEqual(2, into.Count);
            Assert.AreEqual("grim", into["tone"]);
            Assert.AreEqual("3", into["starting_level"]);
        }

        [Test]
        public void SeedMessage_CarriesTheBriefAndIsOutOfCharacter()
        {
            string brief = "CAMPAIGN SETUP BRIEF: ...\n- Player characters: Lyra — elf ranger";
            string line = VaultController.SeedMessage("sunken-crown", brief);
            Assert.IsTrue(Storyteller.IsOocPlayer(line));
            StringAssert.Contains("Lyra — elf ranger", line);
            StringAssert.Contains("Skip start_session", line);

            // An older server without a brief still asks for the player characters.
            string fallback = VaultController.SeedMessage("sunken-crown", string.Empty);
            Assert.IsTrue(Storyteller.IsOocPlayer(fallback));
            StringAssert.Contains("isPc=true", fallback);
        }

        [Test]
        public void IsNoPartyError_MatchesStartSessionRefusal()
        {
            Assert.IsTrue(VaultController.IsNoPartyError("Campaign 'x' has no party members. Seed at least one character…"));
            Assert.IsFalse(VaultController.IsNoPartyError("Campaign 'x' does not exist."));
            Assert.IsFalse(VaultController.IsNoPartyError(null));
        }

        [Test]
        public void ChoiceLabel_ReadsAsWords_UnknownPassesThrough()
        {
            Assert.AreEqual("Build them with the DM, step by step", OnboardingOverlay.ChoiceLabel("build-at-table"));
            Assert.AreEqual("Dnd5e", OnboardingOverlay.ChoiceLabel("Dnd5e"));
        }

        [Test]
        public void Brainstorm_SupportsFreeTextOnly()
        {
            Assert.IsTrue(OnboardingBrainstorm.Supports(new OnboardingQuestion { Key = "pc_roster", Type = AnswerType.List }));
            Assert.IsTrue(OnboardingBrainstorm.Supports(new OnboardingQuestion { Key = "homebrew_world_details", Type = AnswerType.Text }));
            Assert.IsFalse(OnboardingBrainstorm.Supports(new OnboardingQuestion { Key = "campaign_name", Type = AnswerType.Text }));
            Assert.IsFalse(OnboardingBrainstorm.Supports(new OnboardingQuestion { Key = "system", Type = AnswerType.Choice }));
            Assert.IsFalse(OnboardingBrainstorm.Supports(new OnboardingQuestion { Key = "starting_level", Type = AnswerType.Number }));
            Assert.IsFalse(OnboardingBrainstorm.Supports(null));
        }

        [Test]
        public void Brainstorm_PromptsCarryQuestionAndAnswersSoFar()
        {
            var q = new OnboardingQuestion { Key = "pc_roster", Text = "Describe each player character", Type = AnswerType.List };
            string system = OnboardingBrainstorm.SystemPrompt(q, new Dictionary<string, string> { { "tone", "grim sea horror" } });
            StringAssert.Contains("Describe each player character", system);
            StringAssert.Contains("grim sea horror", system);
            StringAssert.Contains("One player character per line", OnboardingBrainstorm.FinalizeInstruction(q));
        }

        [Test]
        public void Brainstorm_CleanAnswer_StripsWrappers()
        {
            Assert.AreEqual("Lyra — elf ranger", OnboardingBrainstorm.CleanAnswer("```\nLyra — elf ranger\n```"));
            Assert.AreEqual("A drowned city", OnboardingBrainstorm.CleanAnswer("Answer: \"A drowned city\""));
            // Inner quotes are content, not a wrapper.
            Assert.AreEqual("\"Red\" Jack and \"Blue\" Nell", OnboardingBrainstorm.CleanAnswer("\"Red\" Jack and \"Blue\" Nell"));
        }

        [Test]
        public void ProviderProblemFor_OnlySettingsFailuresBlock()
        {
            StringAssert.Contains("rejected the API key", OpenAiChatDriver.ProviderProblemFor(401, "gpt-x"));
            StringAssert.Contains("gpt-x", OpenAiChatDriver.ProviderProblemFor(404, "gpt-x"));
            Assert.AreEqual(string.Empty, OpenAiChatDriver.ProviderProblemFor(429, "gpt-x"));
            Assert.AreEqual(string.Empty, OpenAiChatDriver.ProviderProblemFor(500, "gpt-x"));
            Assert.AreEqual(string.Empty, OpenAiChatDriver.ProviderProblemFor(0, "gpt-x"));
        }

        [Test]
        public void IsSettingsProblem_IgnoresUnreachable()
        {
            Assert.IsTrue(VaultController.IsSettingsProblem("The provider rejected the API key (HTTP 401)."));
            Assert.IsTrue(VaultController.IsSettingsProblem("Connected, but model \"x\" is not in the provider's model list."));
            Assert.IsFalse(VaultController.IsSettingsProblem("Cannot reach https://api.example.com (timeout)."));
        }
    }
}
