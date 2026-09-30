using NUnit.Framework;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>N7: player-facing words for tools and roll cards.</summary>
    public class TableWordsTests
    {
        [Test]
        public void ToolCatalog_KnownToolsHaveHumanNames()
        {
            var turn = ToolCatalog.Describe("take_turn", "🚨 model-facing text");
            Assert.AreEqual("Play a turn", turn.Name);
            Assert.AreEqual("At the table", turn.Group);
            StringAssert.DoesNotContain("🚨", turn.Blurb);
            Assert.AreEqual("Build the world", ToolCatalog.Describe("world_build", null).Name);
        }

        [Test]
        public void ToolCatalog_UnknownToolFallsBackToIdAndFirstSentence()
        {
            var plugin = ToolCatalog.Describe("brew_potion", "Brews a potion from gathered herbs. Requires an alchemist's kit and an hour.");
            Assert.AreEqual("Brew Potion", plugin.Name);
            Assert.AreEqual(ToolCatalog.OtherGroup, plugin.Group);
            Assert.AreEqual("Brews a potion from gathered herbs.", plugin.Blurb);
        }

        [Test]
        public void RollCard_TotalTargetAndVerdictWords()
        {
            var roll = new RollInfo { Label = "Perception", Detail = "17 vs DC 14", Verdict = "CriticalSuccess" };
            Assert.AreEqual(17, StoryLogView.RollTotal(roll));
            Assert.AreEqual("against DC 14", StoryLogView.RollAgainst(roll));
            Assert.AreEqual("Critical Success", StoryLogView.VerdictWords(roll.Verdict));
            Assert.AreEqual("Critical hit", StoryLogView.VerdictWords("Critical hit"));
            Assert.AreEqual(21, StoryLogView.RollTotal(new RollInfo { Total = 21, Detail = "vs AC 15" }));
            Assert.AreEqual(0, StoryLogView.RollTotal(new RollInfo { Detail = "no number" }));
        }
    }
}
