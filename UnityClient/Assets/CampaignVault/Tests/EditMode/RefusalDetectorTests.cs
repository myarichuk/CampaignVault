using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>Refusal detection: the provider's own signals are trusted, the prose heuristic is narrow, and a good scene is never thrown away.</summary>
    public class RefusalDetectorTests
    {
        private static JsonValue Choice(string json)
        {
            JsonValue v;
            Assert.IsTrue(JsonValue.TryParse(json, out v), json);
            return v;
        }

        [TestCase("{\"message\":{\"content\":\"\"},\"finish_reason\":\"content_filter\"}")]
        [TestCase("{\"message\":{\"content\":null,\"refusal\":\"I can't help with that.\"},\"finish_reason\":\"stop\"}")]
        [TestCase("{\"message\":{\"content\":\"\"},\"stop_reason\":\"refusal\"}")]
        [TestCase("{\"message\":{\"content\":\"\"},\"finish_reason\":\"stop\",\"native_finish_reason\":\"refusal\"}")]
        public void TheProvidersOwnSignals_AreHardRefusals_EvenWhenProseDetectionIsOff(string choice)
        {
            Assert.AreEqual(RefusalKind.Hard, RefusalDetector.Inspect(Choice(choice), string.Empty, false, false).Kind);
        }

        [Test]
        public void AnOrdinaryReply_IsNotARefusal()
        {
            Assert.IsFalse(RefusalDetector.Inspect(Choice("{\"message\":{\"content\":\"x\"},\"finish_reason\":\"stop\"}"), "x", false, true).IsRefusal);
            Assert.IsFalse(RefusalDetector.Inspect(JsonValue.Null, string.Empty, false, true).IsRefusal);
        }

        [Test]
        public void ADeclineInProse_IsSoft_OnlyWhereProseIsExpectedAndNoToolsRode()
        {
            const string apology = "I'm sorry, but I can't continue with this scene.";
            var choice = Choice("{\"message\":{},\"finish_reason\":\"stop\"}");
            Assert.AreEqual(RefusalKind.Soft, RefusalDetector.Inspect(choice, apology, false, true).Kind);
            Assert.IsFalse(RefusalDetector.Inspect(choice, apology, false, false).IsRefusal, "OOC answers may legitimately say this");
            Assert.IsFalse(RefusalDetector.Inspect(choice, apology, true, true).IsRefusal, "a reply with tool calls is bookkeeping");
        }

        [TestCase("Sorry, this goes against my guidelines so I won't write that.", true)]
        [TestCase("I cannot assist with that request.", true)]
        [TestCase("The clerk shrugs. \"I can't help with that, it's against my guidelines,\" he says.", false)]
        [TestCase("\"I can't let you through,\" the guard says, hand on his hilt.", false)]
        [TestCase("You can't see the far wall; the guard says nothing and the lantern hisses.", false)]
        [TestCase("The guild posts its guidelines on the door; you read them twice.", false)]
        [TestCase("DONE", false)]
        [TestCase("", false)]
        public void TheHeuristic_CatchesDeclines_AndSparesScenes(string text, bool expected)
        {
            Assert.AreEqual(expected, RefusalDetector.LooksLikeSoftRefusal(text));
        }

        [Test]
        public void ALongReply_IsNeverASoftRefusal_EvenIfItOpensWithAnApology()
        {
            string scene = "I'm sorry, " + string.Join(" ", System.Linq.Enumerable.Repeat("The lantern hisses and you step through the door.", 40));
            Assert.IsFalse(RefusalDetector.LooksLikeSoftRefusal(scene));
        }

        [TestCase(403L, "HTTP 403 from chat endpoint", "{\"error\":{\"metadata\":{\"error_type\":\"content_policy_violation\"}}}", true)]
        [TestCase(400L, "Your prompt was blocked by the content filter", "", true)]
        [TestCase(403L, "HTTP 403 from chat endpoint", "", false)]
        [TestCase(403L, "HTTP 403 from chat endpoint.", "<html><meta content=\"x\"><body>Access blocked by security policy</body></html>", false)]
        [TestCase(200L, "Chat endpoint returned an empty stream.", "data: {\"choices\":[{\"delta\":{\"content\":\"The guard was flagged for a refusal of moderation\"}}]}", false)]
        [TestCase(400L, "HTTP 400 from chat endpoint: content must be a string", "", false)]
        [TestCase(403L, "Access from your region is blocked", "", false)]
        [TestCase(401L, "moderation key invalid", "content policy", false)]
        [TestCase(429L, "rate limit moderation", "", false)]
        [TestCase(0L, "Connection refused: refusal", "", false)]
        public void HttpFailures_AreRefusals_OnlyWithAContentSignal_AndNeverForAuthRateOrNetwork(long status, string raw, string body, bool expected)
        {
            Assert.AreEqual(expected, RefusalDetector.LooksLikeRefusalError(status, raw, body));
        }
    }
}
