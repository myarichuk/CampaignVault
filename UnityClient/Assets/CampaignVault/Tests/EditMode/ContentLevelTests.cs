using NUnit.Framework;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>The content level on the client: what it sends the server, what it reads back, and that anything unrecognised stays on the safe side.</summary>
    public class ContentLevelTests
    {
        [TestCase("open", "open")]
        [TestCase("  OPEN ", "open")]
        [TestCase("Fade", "fade")]
        [TestCase("explicit", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void Normalize_OnlyKnowsFadeAndOpen(string given, string expected)
        {
            Assert.AreEqual(expected, ContentLevelRequest.Normalize(given));
        }

        [Test]
        public void Build_IsOneCampaignUpdate_WithThePlayersWords()
        {
            JsonValue request = ContentLevelRequest.Build("OPEN");
            var changes = request.GetArray("changes");
            Assert.AreEqual(1, changes.Count, "the server insists a player-owned setting is the commit's only change");
            Assert.AreEqual("campaign_update", changes[0].GetString("$type", string.Empty));
            Assert.AreEqual("open", changes[0].GetString("contentLevel", string.Empty));
            StringAssert.Contains("open", changes[0].GetString("playerRequest", string.Empty));
            Assert.IsNotEmpty(request.GetString("narrative", string.Empty));
        }

        [Test]
        public void Build_FallsBackToFade_NeverToOpen_ForAnUnknownLevel()
        {
            Assert.AreEqual("fade", ContentLevelRequest.Build("anything-goes").GetArray("changes")[0].GetString("contentLevel", string.Empty));
        }

        [TestCase("{\"campaign\":{\"slug\":\"mill\",\"contentLevel\":\"open\"}}", "open")]
        [TestCase("{\"campaign\":{\"slug\":\"mill\",\"ContentLevel\":\"FADE\"}}", "fade")]
        [TestCase("{\"campaign\":{\"slug\":\"mill\"}}", "")]
        [TestCase("{\"campaign\":{\"slug\":\"mill\",\"contentLevel\":\"anything-goes\"}}", "")]
        public void TheDigest_ReadsTheLevel_AndTreatsAnythingElseAsNeverChosen(string json, string expected)
        {
            JsonValue root;
            Assert.IsTrue(JsonValue.TryParse(json, out root));
            Assert.AreEqual(expected, SessionDigest.FromResult(root).ContentLevel);
        }
    }
}
