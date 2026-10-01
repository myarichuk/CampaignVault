using System.Collections.Generic;
using System.Linq;
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
        public void Fold_ShortMessage_ShownWhole()
        {
            Assert.IsNull(OnboardingOverlay.Fold("a half idea about smugglers", 600, 8));
            Assert.IsNull(OnboardingOverlay.Fold(string.Empty, 600, 8));
        }

        [Test]
        public void Fold_LongMessage_CutsAtAWord()
        {
            string text = string.Join(" ", Enumerable.Repeat("smuggler", 200));
            string folded = OnboardingOverlay.Fold(text, 100, 8);
            Assert.LessOrEqual(folded.Length, 102);
            StringAssert.EndsWith("smuggler …", folded);
        }

        [Test]
        public void Fold_ManyLines_KeepsTheFirstOnes()
        {
            string text = "one\ntwo\nthree\nfour";
            Assert.AreEqual("one\ntwo …", OnboardingOverlay.Fold(text, 600, 2));
        }

        private static List<KeyValuePair<string, string>> Chat(params int[] sizes)
        {
            var chat = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < sizes.Length; i++) { chat.Add(new KeyValuePair<string, string>(i % 2 == 0 ? "user" : "assistant", new string('x', sizes[i]))); }
            return chat;
        }

        [Test]
        public void Dropped_UnderBudget_SendsEverything()
        {
            Assert.IsEmpty(OnboardingBrainstorm.Dropped(Chat(100, 100, 100), 300));
            Assert.IsEmpty(OnboardingBrainstorm.Dropped(Chat(), 10));
        }

        [Test]
        public void Dropped_OverBudget_KeepsFirstAndNewest()
        {
            // 100 + [100, 100] + 100 + 100: the first and the last two fit in 300.
            var dropped = OnboardingBrainstorm.Dropped(Chat(100, 100, 100, 100, 100), 300);
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, dropped);
        }

        [Test]
        public void Dropped_NewestAloneOverBudget_StillSent()
        {
            var dropped = OnboardingBrainstorm.Dropped(Chat(100, 100, 500), 300);
            CollectionAssert.AreEquivalent(new[] { 1 }, dropped);
        }

        [Test]
        public void CleanAnswer_LongWriteUp_KeptWhole()
        {
            string longAnswer = new string('a', OnboardingBrainstorm.MaxAnswerChars + 500);
            Assert.AreEqual(longAnswer, OnboardingBrainstorm.CleanAnswer(longAnswer));
        }

        [Test]
        public void Chunks_SplitAtParagraphs_LoseNothing()
        {
            string para = string.Join(" ", Enumerable.Repeat("word", 30));
            string text = string.Join("\n\n", Enumerable.Repeat(para, 20));
            var chunks = Ui.Chunks(text, 400);
            Assert.Greater(chunks.Count, 1);
            foreach (var c in chunks) { Assert.LessOrEqual(c.Length, 400); }
            Assert.AreEqual(text.Replace("\n", string.Empty).Replace(" ", string.Empty), string.Concat(chunks).Replace("\n", string.Empty).Replace(" ", string.Empty));
        }

        [Test]
        public void Chunks_UnbrokenText_HardSplits()
        {
            var chunks = Ui.Chunks(new string('z', 1000), 400);
            Assert.AreEqual(3, chunks.Count);
            Assert.AreEqual(1000, chunks.Sum(c => c.Length));
        }

        [Test]
        public void Monogram_OddNames_NeverEmpty()
        {
            Assert.AreEqual("RJ", Ui.Monogram("\"Red\" Jack"));
            Assert.AreEqual("?", Ui.Monogram("   "));
            Assert.AreEqual("?", Ui.Monogram(null));
            Assert.AreEqual("LY", Ui.Monogram("Lyra"));
            Assert.AreEqual("K", Ui.Monogram("🐉 K"));
            Assert.AreEqual("SF", Ui.Monogram("Seraphim Fairwind the Third"));
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
        public void MoveToQuestion_ConversationCarriesOver_WithADivider()
        {
            var ob = new OnboardingState();
            VaultController.MoveToQuestion(ob, new OnboardingQuestion { Key = "homebrew_world_details", Text = "Describe the world." });
            ob.BrainstormChat.Add(new KeyValuePair<string, string>("user", "Red Wizards bribe Fairwind"));
            ob.BrainstormChat.Add(new KeyValuePair<string, string>("assistant", "Three ways to structure the reveal…"));
            ob.Draft = "a world answer";

            VaultController.MoveToQuestion(ob, new OnboardingQuestion { Key = "plot_direction", Text = "Describe your plot idea." });
            Assert.AreEqual(3, ob.BrainstormChat.Count, "the plot talk survives the move");
            Assert.AreEqual(OnboardingBrainstorm.MarkerRole, ob.BrainstormChat[2].Key);
            Assert.AreEqual("Describe your plot idea.", ob.BrainstormChat[2].Value);
            Assert.AreEqual(string.Empty, ob.Draft);

            // A rejected answer re-shows the same question: no second divider.
            VaultController.MoveToQuestion(ob, new OnboardingQuestion { Key = "plot_direction", Text = "Describe your plot idea." });
            Assert.AreEqual(3, ob.BrainstormChat.Count);
        }

        [Test]
        public void MoveToQuestion_NoTalkYet_NoDivider()
        {
            var ob = new OnboardingState();
            VaultController.MoveToQuestion(ob, new OnboardingQuestion { Key = "tone", Text = "Tone?" });
            VaultController.MoveToQuestion(ob, new OnboardingQuestion { Key = "starting_era", Text = "Era?" });
            Assert.IsEmpty(ob.BrainstormChat);
            Assert.IsFalse(OnboardingBrainstorm.HasTalk(ob.BrainstormChat));
        }

        [Test]
        public void ModelMessages_DividersBecomeNotes_DroppedLeftOut()
        {
            var chat = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("user", "pitch"),
                new KeyValuePair<string, string>("assistant", "ideas"),
                new KeyValuePair<string, string>(OnboardingBrainstorm.MarkerRole, "Describe your plot idea."),
                new KeyValuePair<string, string>("user", "the Fairwind one"),
            };
            var sent = OnboardingBrainstorm.ModelMessages(chat, new HashSet<int> { 1 });
            Assert.AreEqual(3, sent.Count);
            Assert.AreEqual("user", sent[1].Key);
            StringAssert.Contains("moved on to the next question", sent[1].Value);
            StringAssert.Contains("Describe your plot idea.", sent[1].Value);
            Assert.AreEqual("the Fairwind one", sent[2].Value);
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
