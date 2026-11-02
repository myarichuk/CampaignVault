using System.Collections.Generic;
using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>Homebrew backgrounds the DM writes for one character, and gender and pronouns on the draft and the party line.</summary>
    public class BackgroundWriterTests
    {
        private static CharacterDraft Kael()
        {
            var d = new CharacterDraft { Kind = "pc", Name = "Kael", Concept = "A dockside thief who owes the wrong guild.", Gender = "man", Pronouns = "he/him" };
            d.Choices["race"] = JsonValue.FromString("half_elf");
            d.Choices["class"] = JsonValue.FromString("rogue");
            return d;
        }

        [Test]
        public void Serves_OnlyTheBackgroundsPick()
        {
            Assert.IsTrue(BackgroundWriter.Serves(new BuilderStep { Key = "background", Kind = StepKinds.PickOne, Source = "backgrounds" }));
            Assert.IsFalse(BackgroundWriter.Serves(new BuilderStep { Key = "race", Kind = StepKinds.PickOne, Source = "races" }));
            Assert.IsFalse(BackgroundWriter.Serves(null));
        }

        [Test]
        public void Prompt_CarriesTheCharacter_TheCampaign_AndTheRulesetsBudget()
        {
            var existing = new List<BuilderOption> { new BuilderOption { Id = "acolyte", Label = "Acolyte" } };
            var answers = new Dictionary<string, string> { { "setting", "Neverwinter after the fall" } };

            string dnd = BackgroundWriter.SystemPrompt("dnd5e", Kael(), existing, answers);
            StringAssert.Contains("A dockside thief", dnd);
            StringAssert.Contains("Gender: man", dnd);
            StringAssert.Contains("Pronouns: he/him", dnd);
            StringAssert.Contains("Neverwinter after the fall", dnd);
            StringAssert.Contains("acolyte", dnd, "the shipped ids aren't repeated");
            StringAssert.Contains("exactly 2 D&D 5e skills", dnd);
            StringAssert.Contains("gold: 0 to 25", dnd);
            StringAssert.Contains("never its mechanics", dnd);

            string pf2 = BackgroundWriter.SystemPrompt("pf2e", Kael(), existing, answers);
            StringAssert.Contains("skillFeat", pf2);
            StringAssert.Contains("lore:", pf2);
            StringAssert.DoesNotContain("gold: 0 to 25", pf2);
        }

        [Test]
        public void Extract_TakesNamedYamlFences_AtMostThree_AndLeavesTheProse()
        {
            string reply = "Here are some.\n"
                + "```yaml\nname: harbor_runner\nlabel: Harbor Runner\nskillProficiencies: [Stealth, Athletics]\n```\n"
                + "```\nlabel: no name, skipped\n```\n"
                + "```yml\nname: \"guild_debtor\"\nlabel: Guild Debtor\n```\n"
                + "```yaml\nname: fence_apprentice\n```\n"
                + "```yaml\nname: one_too_many\n```\n";

            var found = BackgroundWriter.Extract(reply);

            Assert.AreEqual(3, found.Count);
            Assert.AreEqual("harbor_runner", BackgroundWriter.NameOf(found[0]));
            Assert.AreEqual("guild_debtor", BackgroundWriter.NameOf(found[1]));
            Assert.AreEqual("fence_apprentice", BackgroundWriter.NameOf(found[2]));
            Assert.AreEqual("Here are some.", BackgroundWriter.Prose(reply));
            Assert.AreEqual(0, BackgroundWriter.Extract("no fences at all").Count);
        }

        [Test]
        public void TheBatch_IsOneHomebrewBackground_ForTheCampaign()
        {
            var args = VaultController.BackgroundBatch("sunken-crown", "Dnd5e", "name: harbor_runner\n");

            Assert.AreEqual("sunken-crown", args.GetString("campaignName", null));
            var entry = args.Get("batch").GetArray("homebrew")[0];
            Assert.AreEqual("background", entry.GetString("kind", null));
            Assert.AreEqual("dnd5e", entry.GetString("system", null));
            Assert.AreEqual("name: harbor_runner\n", entry.GetString("yaml", null));
        }

        [Test]
        public void TheNote_SaysWhatWasSaved_AndWhatTheRulesRefused()
        {
            string note = VaultController.WriteNote("They fit the docks.", new List<string> { "harbor_runner" },
                new List<string> { "guild_debtor (A dnd5e background grants exactly 2 skill proficiencies (got 3).)" });

            StringAssert.StartsWith("They fit the docks.", note);
            StringAssert.Contains("Added: harbor_runner.", note);
            StringAssert.Contains("Not saved: guild_debtor (A dnd5e background grants exactly 2", note);
        }

        [Test]
        public void GenderAndPronouns_RideTheDraft_BothWays()
        {
            var json = Kael().ToJson();
            Assert.AreEqual("man", json.GetString("gender", null));
            Assert.AreEqual("he/him", json.GetString("pronouns", null));

            var back = CharacterDraft.FromJson(json);
            Assert.AreEqual("man", back.Gender);
            Assert.AreEqual("he/him", back.Pronouns);

            var bare = new CharacterDraft { Name = "Odo" }.ToJson();
            Assert.IsTrue(bare.Get("pronouns").IsNull, "nothing is sent when they weren't given");
        }

        [Test]
        public void ThePartysPronouns_ComeFromStartSession()
        {
            var digest = SessionDigest.FromResult(JsonValue.Parse(
                "{\"sessionNumber\":1,\"party\":[{\"id\":\"chars/kael\",\"name\":\"Kael\",\"isPc\":true,\"pronouns\":\"he/him\"},"
                + "{\"id\":\"chars/odo\",\"name\":\"Odo\",\"isPc\":true}]}"));

            Assert.AreEqual("he/him", digest.Pcs[0].Pronouns);
            Assert.AreEqual(string.Empty, digest.Pcs[1].Pronouns);
        }
    }
}
