using System.Linq;
using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// Pins the pure-C# seams the UI rewrite leans on, so the rewrite can't
    /// silently change what the driver sends or what the panels read.
    /// </summary>
    public class ParsingPinTests
    {
        private static JsonValue Parse(string json)
        {
            JsonValue value;
            Assert.IsTrue(JsonValue.TryParse(json, out value), "fixture JSON must parse");
            return value;
        }

        // ---- SegmentSplitter ----

        [Test]
        public void AddNarration_PlainProse_IsOneNarrationSegment()
        {
            var transcript = new VaultTranscript();
            SegmentSplitter.AddNarration(transcript, "The mill wheel groans in the dark.");
            Assert.AreEqual(1, transcript.Segments.Count);
            Assert.AreEqual(SegmentKind.Narration, transcript.Segments[0].Kind);
        }

        [Test]
        public void AddNarration_SplitsSpeakerLines()
        {
            var transcript = new VaultTranscript();
            SegmentSplitter.AddNarration(transcript, "She turns. Mirelle: \"You're late.\" The door shuts.");
            var kinds = transcript.Segments.Select(s => s.Kind).ToArray();
            CollectionAssert.AreEqual(new[] { SegmentKind.Narration, SegmentKind.NpcVoice, SegmentKind.Narration }, kinds);
            Assert.AreEqual("Mirelle", transcript.Segments[1].Speaker);
            Assert.AreEqual("You're late.", transcript.Segments[1].Text);
        }

        [Test]
        public void AddNarration_StripsBidiOverrides()
        {
            var transcript = new VaultTranscript();
            SegmentSplitter.AddNarration(transcript, "a‮b");
            Assert.AreEqual("ab", transcript.Segments[0].Text);
        }

        // ---- Transcript cap ----

        [Test]
        public void Transcript_Cap_DropsToolDataFirst()
        {
            var transcript = new VaultTranscript();
            transcript.Add(new TranscriptSegment { Kind = SegmentKind.Narration, Text = "keep" });
            transcript.Add(new TranscriptSegment { Kind = SegmentKind.ToolData, Text = "drop" });
            for (int i = 0; i < VaultTranscript.MaxSegments - 1; i++)
            {
                transcript.Add(new TranscriptSegment { Kind = SegmentKind.Narration, Text = "n" + i });
            }
            Assert.AreEqual(VaultTranscript.MaxSegments, transcript.Segments.Count);
            Assert.AreEqual("keep", transcript.Segments[0].Text);
            Assert.IsFalse(transcript.Segments.Any(s => s.Kind == SegmentKind.ToolData));
        }

        // ---- OpenAiChatDriver seams ----

        [Test]
        public void ExtractContent_PlainString()
        {
            string content, reasoning;
            OpenAiChatDriver.ExtractContent(Parse("{\"role\":\"assistant\",\"content\":\"hi\"}"), out content, out reasoning);
            Assert.AreEqual("hi", content);
            Assert.AreEqual(string.Empty, reasoning);
        }

        [Test]
        public void ExtractContent_ContentBlocks_AreJoined()
        {
            string content, reasoning;
            OpenAiChatDriver.ExtractContent(
                Parse("{\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}"),
                out content, out reasoning);
            Assert.AreEqual("a\nb", content);
        }

        [Test]
        public void ExtractContent_NullContent_FallsBackToReasoning()
        {
            string content, reasoning;
            OpenAiChatDriver.ExtractContent(Parse("{\"content\":null,\"reasoning_content\":\"thinking\"}"), out content, out reasoning);
            Assert.AreEqual(string.Empty, content);
            Assert.AreEqual("thinking", reasoning);
        }

        [Test]
        public void CompactResult_UsesEnvelopeSummary()
        {
            string compacted = OpenAiChatDriver.CompactResult("take_turn", "{\"success\":true,\"summary\":\"Moved to the mill.\",\"data\":{}}");
            Assert.AreEqual("[earlier take_turn result] Moved to the mill.", compacted);
        }

        [Test]
        public void CompactResult_FlagsFailures()
        {
            string compacted = OpenAiChatDriver.CompactResult("take_turn", "{\"success\":false,\"summary\":\"No such NPC.\"}");
            Assert.AreEqual("[earlier take_turn result] FAILED: No such NPC.", compacted);
        }

        [Test]
        public void CompactResult_SkillLoads_BecomeReloadHint()
        {
            string compacted = OpenAiChatDriver.CompactResult(SystemPromptProvider.LoadSkillTool, "# long skill body");
            StringAssert.StartsWith("[earlier skill load", compacted);
        }

        [Test]
        public void CompactResult_IsIdempotent()
        {
            string once = OpenAiChatDriver.CompactResult("x", "plain text result");
            Assert.AreEqual(once, OpenAiChatDriver.CompactResult("x", once));
        }

        [Test]
        public void CompactResult_CapsLongText()
        {
            string compacted = OpenAiChatDriver.CompactResult("x", new string('a', 1000));
            Assert.LessOrEqual(compacted.Length, OpenAiChatDriver.CompactedResultChars + 40);
            StringAssert.EndsWith("…", compacted);
        }

        // ---- McpClient seams ----

        [Test]
        public void ExtractJsonPayload_PlainJson_PassesThrough()
        {
            Assert.AreEqual("{\"a\":1}", McpClient.ExtractJsonPayload("{\"a\":1}"));
        }

        [Test]
        public void ExtractJsonPayload_Sse_TakesLastDataFrame()
        {
            string raw = "event: message\ndata: {\"id\":1}\n\ndata: {\"id\":2}\n\ndata: [DONE]\n";
            Assert.AreEqual("{\"id\":2}", McpClient.ExtractJsonPayload(raw));
        }

        [Test]
        public void ReadEnvelope_Success_UnwrapsData()
        {
            var result = Parse("{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"success\\\":true,\\\"summary\\\":\\\"ok\\\",\\\"data\\\":{\\\"n\\\":3}}\"}]}");
            var outcome = McpClient.ReadEnvelope(result);
            Assert.IsTrue(outcome.Ok);
            Assert.AreEqual(3, outcome.Data.Data.GetNumber("n", 0));
            Assert.AreEqual("ok", outcome.Data.Summary);
        }

        [Test]
        public void ReadEnvelope_SuccessFalse_FailsWithCodeAndSummary()
        {
            var result = Parse("{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"success\\\":false,\\\"error\\\":\\\"NOT_FOUND\\\",\\\"summary\\\":\\\"No campaign.\\\"}\"}]}");
            var outcome = McpClient.ReadEnvelope(result);
            Assert.IsFalse(outcome.Ok);
            Assert.AreEqual("NOT_FOUND", outcome.ErrorCode);
            Assert.AreEqual("No campaign.", outcome.ErrorMessage);
        }

        // ---- SessionDigest ----

        [Test]
        public void SessionDigest_ReadsCampaignPartyAndQuests()
        {
            var root = Parse(@"{
                ""sessionNumber"": 4, ""resumed"": true, ""partyFingerprint"": ""fp1"",
                ""campaign"": { ""slug"": ""sunken-crown"", ""system"": ""dnd5e"",
                    ""pcs"": [ { ""id"": ""chars/ari"", ""name"": ""Ari"" } ],
                    ""companions"": [ { ""id"": ""chars/bo"", ""name"": ""Bo"" } ] },
                ""activeQuests"": [ { ""id"": ""q1"", ""title"": ""Find the heir"", ""openObjectives"": 2, ""deadlineDay"": 14 } ],
                ""party"": [ { ""id"": ""chars/ari"", ""name"": ""Ari"", ""isPc"": true, ""hp"": ""7/12"", ""conditions"": [""poisoned""] } ]
            }");
            var digest = SessionDigest.FromResult(root);
            Assert.AreEqual(4, digest.SessionNumber);
            Assert.IsTrue(digest.Resumed);
            Assert.AreEqual("fp1", digest.Fingerprint);
            Assert.AreEqual("sunken-crown", digest.CampaignSlug);
            Assert.AreEqual("dnd5e", digest.System);
            Assert.AreEqual("chars/ari", digest.Pcs[0].Id);
            Assert.AreEqual("chars/bo", digest.Companions[0].Id);
            Assert.AreEqual("Find the heir", digest.Quests[0].Title);
            Assert.AreEqual("day 14", digest.Quests[0].Deadline);
            Assert.AreEqual(7, digest.Party[0].CurHp);
            Assert.AreEqual(12, digest.Party[0].MaxHp);
            CollectionAssert.AreEqual(new[] { "poisoned" }, digest.Party[0].Conditions);
        }

        [Test]
        public void SessionDigest_NonObject_IsEmpty()
        {
            var digest = SessionDigest.FromResult(JsonValue.FromString("nope"));
            Assert.AreEqual(0, digest.Party.Count);
        }
    }
}
