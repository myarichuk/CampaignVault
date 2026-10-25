using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>Phase 4 low-batch fixes, pinned: A11/A12 stream handling, A24 loop prompt, A25-A31 parsing and hosts.</summary>
    public class Phase4PinTests
    {
        [Test]
        public void LoopPrompt_DropsNarrationSections_KeepsTheRest()
        {
            string prompt = "You are a Game Master.\n\nNARRATION (mechanics never shorten this)\n- Load dnd-narration.\n- Write 350-600 words.\n\nEVERY take_turn\n- One beat.\n\nLAST STEP OF EVERY BEAT\n- Write the scene.";
            string loop = SystemPromptProvider.WithoutSections(prompt, SystemPromptProvider.LoopSkippedSections);
            StringAssert.DoesNotContain("NARRATION", loop);
            StringAssert.DoesNotContain("LAST STEP", loop);
            StringAssert.Contains("EVERY take_turn\n- One beat.", loop);
            StringAssert.StartsWith("You are a Game Master.", loop);
        }

        [Test]
        public void NarrationSkills_AreNotTheLoops()
        {
            Assert.IsTrue(SystemPromptProvider.IsNarrationSkill("dnd-narration"));
            Assert.IsTrue(SystemPromptProvider.IsNarrationSkill("dnd-narration-examples"));
            Assert.IsFalse(SystemPromptProvider.IsNarrationSkill("dnd-world-change"));
        }

        [Test]
        public void StreamErrors_RetryOnlyWhenTheProviderSaysSo()
        {
            Assert.IsTrue(OpenAiChatDriver.LooksTransient("Rate limit exceeded, try again in 2s"));
            Assert.IsTrue(OpenAiChatDriver.LooksTransient("The model is overloaded"));
            Assert.IsFalse(OpenAiChatDriver.LooksTransient("Invalid request: unknown parameter 'reasoning'"));
            Assert.IsFalse(OpenAiChatDriver.LooksTransient("Model not found"));
        }

        [Test]
        public void ReasoningFragments_MergeByIndex()
        {
            var fragments = new List<JsonValue>
            {
                JsonValue.Parse("{\"type\":\"reasoning.text\",\"index\":0,\"text\":\"The mill \"}"),
                JsonValue.Parse("{\"type\":\"reasoning.text\",\"index\":0,\"text\":\"is dark.\"}"),
                JsonValue.Parse("{\"type\":\"reasoning.summary\",\"index\":1,\"text\":\"Go in.\"}"),
            };
            var merged = ChatStreamAccumulator.MergeReasoningDetails(fragments);
            Assert.AreEqual(2, merged.Count);
            Assert.AreEqual("The mill is dark.", merged[0].GetString("text", string.Empty));
            Assert.AreEqual("Go in.", merged[1].GetString("text", string.Empty));
        }

        [Test]
        public void PlainJsonReply_IsRecognisedAsNotSse()
        {
            byte[] json = Encoding.UTF8.GetBytes("  {\"choices\":[]}");
            byte[] sse = Encoding.UTF8.GetBytes("data: {\"choices\":[]}\n\n");
            byte[] comment = Encoding.UTF8.GetBytes(": keep-alive\n");
            Assert.IsFalse(SseDownloadHandler.LooksLikeSse(json, json.Length));
            Assert.IsTrue(SseDownloadHandler.LooksLikeSse(sse, sse.Length));
            Assert.IsTrue(SseDownloadHandler.LooksLikeSse(comment, comment.Length));
        }

        [Test]
        public void SseFallback_PrefersTheFrameAnsweringTheRequest()
        {
            string raw = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"ok\":1}}\n\nevent: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}\n\n";
            StringAssert.Contains("\"id\":7", McpClient.ExtractJsonPayload(raw, 7));
            StringAssert.Contains("notifications/message", McpClient.ExtractJsonPayload(raw));
        }

        [Test]
        public void Truncation_NeverSplitsASurrogatePair()
        {
            string text = "ab\U0001F600cd"; // the emoji is two UTF-16 chars at index 2-3
            string cut = TextSanitizer.Clean(text, 3);
            StringAssert.StartsWith("ab…", cut);
            Assert.IsFalse(char.IsHighSurrogate(cut[cut.IndexOf('…') - 1]));
        }

        [Test]
        public void Json_RejectsNonAsciiDigits()
        {
            JsonValue parsed;
            Assert.IsTrue(JsonValue.TryParse("{\"n\":42}", out parsed));
            Assert.AreEqual(42, parsed.GetNumber("n", 0));
            Assert.IsFalse(JsonValue.TryParse("{\"n\":٤٢}", out parsed), "Arabic-Indic digits are not JSON numbers");
        }

        [Test]
        public void Ipv6Loopback_IsLocal()
        {
            string reason;
            Assert.IsTrue(ServerHostManager.IsLoopbackUrl("http://[::1]:5275"));
            Assert.IsTrue(TextSanitizer.IsAllowedHttpUrl("http://[::1]:5275/play", out reason), reason);
        }
    }
}
