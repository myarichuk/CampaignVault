using NUnit.Framework;
using CampaignVault.UnityClient.AI;

namespace CampaignVault.UnityClient.Tests
{
    public class PartyLevelAdvisorTests
    {
        [Test]
        public void Parse_ReadsLevelAndReason_ThroughFences_AndClampsToTheBuilder()
        {
            int level; string reason;
            Assert.IsTrue(PartyLevelAdvisor.Parse("```json\n{\"level\": 4, \"reason\": \"A small raid.\"}\n```", 20, out level, out reason));
            Assert.AreEqual(4, level);
            Assert.AreEqual("A small raid.", reason);
            Assert.IsTrue(PartyLevelAdvisor.Parse("{\"level\": 12}", 3, out level, out reason));
            Assert.AreEqual(3, level);
        }

        [Test]
        public void Parse_RejectsProseAndMissingLevels()
        {
            int level; string reason;
            Assert.IsFalse(PartyLevelAdvisor.Parse("Level 5 sounds right.", 20, out level, out reason));
            Assert.IsFalse(PartyLevelAdvisor.Parse("{\"reason\": \"x\"}", 20, out level, out reason));
        }
    }
}
