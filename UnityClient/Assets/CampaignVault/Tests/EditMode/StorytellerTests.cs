using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>N2: the pure parts of the two-pass turn (brief, contract words, PC card).</summary>
    public class StorytellerTests
    {
        private static JsonValue Args(string json) { return JsonValue.Parse(json); }

        [Test]
        public void Brief_TurnsToolResultsIntoPlainFacts()
        {
            var brief = new TurnBrief();
            brief.Add("take_turn", Args("{\"narrative\":\"Mirelle kneels by the wheel and finds a bent nail\"}"),
                "{\"success\":true,\"summary\":\"Committed 2 changes.\",\"data\":{"
                + "\"summary\":[\"Mirelle: activity set to kneeling\"],"
                + "\"narrativeReminder\":\"Mirelle is still limping.\","
                + "\"context\":[\"The miller owes the Warden money.\"],"
                + "\"cards\":[{\"id\":\"chars/oswin\",\"name\":\"Oswin\",\"appearance\":\"flour-dusted, one eye clouded\",\"wants\":\"his daughter home\",\"stance\":\"Aric: wary (40)\"}],"
                + "\"scenes\":[{\"location\":{\"name\":\"The Old Mill\",\"description\":\"A timber mill over black water.\"},\"presentNPCs\":[{\"name\":\"Oswin\",\"activity\":\"sweeping\",\"mood\":\"anxious\"}]}],"
                + "\"worldStateDelta\":{\"newEvents\":[\"Rain swells the river.\"],\"worldPressure\":[\"never shown\"]},"
                + "\"guidanceHints\":[\"never shown\"]},\"guidance\":[\"never shown\"],\"worldPressure\":[\"never shown\"]}",
                true);
            string text = brief.Build("I ask Oswin about the nail.");

            StringAssert.StartsWith("The player says:\nI ask Oswin about the nail.", text);
            StringAssert.Contains("- Mirelle kneels by the wheel and finds a bent nail", text);
            StringAssert.Contains("- Mirelle: activity set to kneeling", text);
            StringAssert.Contains("The Old Mill (A timber mill over black water.)", text);
            StringAssert.Contains("Oswin (looks: flour-dusted, one eye clouded)", text);
            StringAssert.Contains("(wants: his daughter home)", text);
            StringAssert.Contains("Oswin (doing: sweeping) (mood: anxious)", text);
            StringAssert.Contains("- The miller owes the Warden money.", text);
            StringAssert.Contains("- Rain swells the river.", text);
            StringAssert.Contains("- Mirelle is still limping.", text);
            StringAssert.DoesNotContain("never shown", text);
            StringAssert.DoesNotContain("{", text);
            StringAssert.EndsWith("Write the scene.", text);
        }

        [Test]
        public void Brief_FailedCalls_AreFlagged_AndQuietTurnsSaySo()
        {
            var quiet = new TurnBrief();
            StringAssert.Contains("Nothing was committed this turn", quiet.Build("I nod."));

            var brief = new TurnBrief();
            brief.Add("take_turn", Args("{\"narrative\":\"The door gives\"}"), "{\"success\":false,\"summary\":\"Unknown location locations/cellar\"}", true);
            brief.Add("take_turn", Args("{}"), "error [MCP] timeout", false);
            string text = brief.Build("I kick the door.");
            StringAssert.Contains("Attempted but not applied", text);
            StringAssert.Contains("Unknown location locations/cellar", text);
            StringAssert.Contains("error [MCP] timeout", text);
            StringAssert.DoesNotContain("The door gives", text, "a rejected commit's narrative isn't a fact");
        }

        [Test]
        public void Brief_IsCapped_AndDeduplicated()
        {
            var brief = new TurnBrief();
            for (int i = 0; i < 40; i++)
            {
                brief.Add("take_turn", Args("{\"narrative\":\"" + new string('x', 300) + i + "\"}"),
                    "{\"success\":true,\"data\":{\"summary\":[\"same line\"]}}", true);
            }
            string text = brief.Build("Go.");
            Assert.LessOrEqual(text.Length, Storyteller.MaxBriefChars);
            Assert.AreEqual(1, text.Split(new[] { "same line" }, System.StringSplitOptions.None).Length - 1);
        }

        [TestCase("DONE", true)]
        [TestCase("  **Done.**  ", true)]
        [TestCase("", true)]
        [TestCase("Done with the search, you find nothing.", false)]
        public void IsDone(string content, bool expected)
        {
            Assert.AreEqual(expected, Storyteller.IsDone(content));
        }

        [TestCase("OOC: what's my AC?", true)]
        [TestCase("ooc what's my AC?", true)]
        [TestCase("((can I reroll?))", true)]
        [TestCase("// pause", true)]
        [TestCase("Oochre paint on the wall — I touch it.", false)]
        [TestCase("I look around.", false)]
        public void IsOocPlayer(string text, bool expected)
        {
            Assert.AreEqual(expected, Storyteller.IsOocPlayer(text));
        }

        [Test]
        public void LoopUserMessage_RoundTripsThroughStripScene()
        {
            Assert.AreEqual("Hi.", Storyteller.LoopUserMessage(string.Empty, "Hi."));
            string withScene = Storyteller.LoopUserMessage("Rain on the roof.", "I listen.");
            StringAssert.StartsWith(Storyteller.ScenePrefix, withScene);
            Assert.AreEqual("I listen.", Storyteller.StripScene(withScene));
            Assert.AreEqual("Plain.", Storyteller.StripScene("Plain."));
        }

        [Test]
        public void NarrationPrompt_HeaderSkillWithoutFrontMatter_TableAndPc()
        {
            string prompt = Storyteller.NarrationSystemPrompt("---\nname: dnd-narration\ndescription: x\n---\n# Narration\nWrite well.", "Aric, Fighter 5.", "CAMPAIGN: campaignName=\"scratch\"");
            StringAssert.StartsWith(Storyteller.NarrationHeader, prompt);
            StringAssert.Contains("# NARRATION GUIDE\n\n# Narration\nWrite well.", prompt);
            StringAssert.DoesNotContain("description: x", prompt);
            StringAssert.Contains("# TABLE\nCAMPAIGN: campaignName=\"scratch\"", prompt);
            StringAssert.EndsWith("# THE PLAYER'S CHARACTER\nAric, Fighter 5.", prompt);
        }

        [Test]
        public void NarrationPrompt_FromRealSkill_CarriesTheExamples()
        {
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "..", "claude_skills", "dnd-narration", "SKILL.md"));
            Assume.That(System.IO.File.Exists(path), path + " not in this checkout");
            string prompt = Storyteller.NarrationSystemPrompt(System.IO.File.ReadAllText(path), string.Empty, string.Empty);
            StringAssert.Contains("### Example: a search and a recall", prompt);
            StringAssert.Contains("### Example: a chase with a missed throw", prompt);
            StringAssert.DoesNotContain("name: dnd-narration", prompt);
        }

        [Test]
        public void PcCard_FromEntity()
        {
            var entity = JsonValue.Parse("{\"name\":\"Aric Thorne\",\"classLevel\":\"Human Fighter 5\",\"currentAppearance\":\"mud to the knees\","
                + "\"distinctiveFeatures\":[\"scar through the left brow\"],\"psychology\":{\"traits\":[\"dry humour\",\"patient\"],\"fears\":[\"deep water\"],\"currentMood\":\"tired\"}}");
            string card = Storyteller.PcCard(entity);
            StringAssert.StartsWith("Aric Thorne, Human Fighter 5.", card);
            StringAssert.Contains("Appearance: mud to the knees", card);
            StringAssert.Contains("Distinctive: scar through the left brow", card);
            StringAssert.Contains("Traits: dry humour; patient", card);
            StringAssert.Contains("Fears: deep water", card);
            Assert.AreEqual(string.Empty, Storyteller.PcCard(JsonValue.Null));
        }
    }
}
