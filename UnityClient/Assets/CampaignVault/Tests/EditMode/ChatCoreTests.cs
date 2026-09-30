using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>P1 fixes: each test names the bug it guards (see UNITY_UI_PLAN.md).</summary>
    public class ChatCoreTests
    {
        private static JsonValue Parse(string json)
        {
            JsonValue value;
            Assert.IsTrue(JsonValue.TryParse(json, out value), "fixture JSON must parse: " + json);
            return value;
        }

        private static RollInfo OnlyRoll(string toolText)
        {
            var rolls = SegmentSplitter.ExtractRolls(toolText);
            Assert.AreEqual(1, rolls.Count, "expected exactly one roll in: " + toolText);
            return rolls[0].Roll;
        }

        // ---- B2: rolls carry the server's verdict ----

        [Test]
        public void B2_Dnd5eCheck_LabelIsSkillNotRolled()
        {
            var roll = OnlyRoll("{\"summary\":\"Search (Perception): Success. Rolled 17 vs DC 14. d20 14 + 3\"}");
            Assert.AreEqual("Perception", roll.Label);
            Assert.AreEqual("17 vs DC 14", roll.Detail);
            Assert.AreEqual(RollOutcome.Success, roll.Outcome);
        }

        [Test]
        public void B2_Pf2eCriticalSuccess_BelowDc_IsStillCritical()
        {
            // Nat 20 steps a failure up: the server's word wins over total >= dc.
            var roll = OnlyRoll("Recall Knowledge (Arcana): CriticalSuccess. Rolled 12 vs DC 15. nat 20");
            Assert.AreEqual(RollOutcome.CriticalSuccess, roll.Outcome);
            Assert.IsTrue(roll.Success);
            Assert.IsTrue(roll.Critical);
            Assert.AreEqual("Critical Success", roll.Verdict);
        }

        [Test]
        public void B2_Pf2eFailureAboveDc_IsFailure()
        {
            var roll = OnlyRoll("Climb (Athletics): Failure. Rolled 16 vs DC 15. nat 1");
            Assert.AreEqual(RollOutcome.Failure, roll.Outcome);
        }

        [Test]
        public void B2_Save_KeepsSaveLabel()
        {
            var roll = OnlyRoll("Poison Needle (Constitution Save): Failure. Rolled 9 vs DC 13. 3 damage.");
            Assert.AreEqual("Constitution Save", roll.Label);
            Assert.AreEqual(RollOutcome.Failure, roll.Outcome);
        }

        [Test]
        public void B2_TargetedSave()
        {
            var roll = OnlyRoll("Fireball vs Goblin: Saved (Dexterity 16 vs DC 15) — 7 damage.");
            Assert.AreEqual("Goblin · Dexterity save", roll.Label);
            Assert.AreEqual(RollOutcome.Success, roll.Outcome);
        }

        [Test]
        public void B2_Pf2eManeuver_NamedDc()
        {
            var roll = OnlyRoll("Grapple: CriticalSuccess. Rolled 25 vs Fortitude DC 15. Restrained.");
            Assert.AreEqual("Grapple", roll.Label);
            Assert.AreEqual("25 vs Fortitude DC 15", roll.Detail);
            Assert.AreEqual(RollOutcome.CriticalSuccess, roll.Outcome);
        }

        [Test]
        public void B2_Dnd5eAttack_HitAndCrit()
        {
            var hit = OnlyRoll("Longsword vs Goblin: Hit for 7 damage. (Attack 17 vs AC 13).");
            Assert.AreEqual("Longsword vs Goblin", hit.Label);
            Assert.AreEqual("17 vs AC 13", hit.Detail);
            Assert.AreEqual(RollOutcome.Success, hit.Outcome);

            var crit = OnlyRoll("Longsword vs Goblin: Hit for 14 damage. (Attack 25 vs AC 13). CRITICAL HIT! Added 6 extra damage.");
            Assert.AreEqual(RollOutcome.CriticalSuccess, crit.Outcome);
        }

        [Test]
        public void B2_Dnd5eMiss_AndAcidSplash()
        {
            Assert.AreEqual(RollOutcome.Failure, OnlyRoll("Dagger vs Rat: Missed. Attack 8 vs AC 12. d20 5 + 3").Outcome);
            var splash = OnlyRoll("Acid Flask vs Ooze: Missed, but the acid still splashes for 2 damage. Attack 9 vs AC 10.");
            Assert.AreEqual(RollOutcome.Failure, splash.Outcome);
        }

        [Test]
        public void B2_SeveralRollsInOneResult_AllCardsInTextOrder()
        {
            // Found by the P4 snapshot: the crit note after an attack swallowed the next roll.
            var rolls = SegmentSplitter.ExtractRolls("Search (Perception): Success. Rolled 17 vs DC 14. "
                + "Longsword vs Ghoul: Hit for 14 damage. (Attack 25 vs AC 13). CRITICAL HIT! Added 6 extra damage. "
                + "Poison Needle (Constitution Save): Failure. Rolled 9 vs DC 13.");
            CollectionAssert.AreEqual(new[] { "Perception", "Longsword vs Ghoul", "Constitution Save" }, rolls.Select(r => r.Roll.Label).ToArray());
            Assert.AreEqual("Critical Hit", rolls[1].Roll.Verdict);
            Assert.AreEqual(RollOutcome.CriticalSuccess, rolls[1].Roll.Outcome);
            Assert.AreEqual(RollOutcome.Failure, rolls[2].Roll.Outcome);
        }

        [Test]
        public void B2_CritNoteOfALaterAttack_DoesNotUpgradeAnEarlierHit()
        {
            var rolls = SegmentSplitter.ExtractRolls("{\"events\":[\"Dagger vs Rat: Hit for 3 damage. (Attack 14 vs AC 12).\","
                + "\"Axe vs Rat: Hit for 12 damage. (Attack 22 vs AC 12). CRITICAL HIT! Added 5 extra damage.\"]}");
            Assert.AreEqual(2, rolls.Count);
            Assert.AreEqual(RollOutcome.Success, rolls[0].Roll.Outcome);
            Assert.AreEqual(RollOutcome.CriticalSuccess, rolls[1].Roll.Outcome);
        }

        [Test]
        public void B2_Pf2eAttack_UsesDegree()
        {
            var roll = OnlyRoll("Shortbow vs Wolf: Hit for 9 damage. (CriticalSuccess) Attack 27 vs AC 16.");
            Assert.AreEqual(RollOutcome.CriticalSuccess, roll.Outcome);
        }

        [Test]
        public void B2_EchoedRoll_IsOneCard()
        {
            string line = "Search (Perception): Success. Rolled 17 vs DC 14.";
            var rolls = SegmentSplitter.ExtractRolls("{\"summary\":\"" + line + "\",\"events\":[\"" + line + "\"]}");
            Assert.AreEqual(1, rolls.Count);
        }

        [Test]
        public void B2_BareNumbersInProse_MakeNoCard()
        {
            Assert.AreEqual(0, SegmentSplitter.ExtractRolls("Last week Investigation 10 vs DC 14 went badly.").Count);
            Assert.AreEqual(0, SegmentSplitter.ExtractRolls("The trap is still armed (12 vs DC 15).").Count);
        }

        [Test]
        public void B2_PrecedingSentence_DoesNotLeakIntoLabel()
        {
            var roll = OnlyRoll("Round 2 begins. Longsword vs Goblin: Missed. Attack 3 vs AC 13.");
            Assert.AreEqual("Longsword vs Goblin", roll.Label);
        }

        // ---- B3: voice lines ----

        [Test]
        public void B3_CurlyQuotes_AreVoiceLines()
        {
            var segs = SegmentSplitter.SplitNarration("The door creaks. Mirelle: “You're late.” She waits.");
            Assert.AreEqual(SegmentKind.NpcVoice, segs[1].Kind);
            Assert.AreEqual("Mirelle", segs[1].Speaker);
            Assert.AreEqual("You're late.", segs[1].Text);
        }

        [Test]
        public void B3_BoldSpeaker_MultiWordName()
        {
            var segs = SegmentSplitter.SplitNarration("**Old Tom:** \"Aye, the mill's cursed.\"");
            Assert.AreEqual(1, segs.Count);
            Assert.AreEqual("Old Tom", segs[0].Speaker);
        }

        [Test]
        public void B3_NoteLabel_IsNotASpeaker()
        {
            var segs = SegmentSplitter.SplitNarration("Note: \"this is a reminder\" for later.");
            Assert.IsTrue(segs.All(s => s.Kind == SegmentKind.Narration));
        }

        [Test]
        public void B3_MidSentenceColon_IsNotASpeaker()
        {
            var segs = SegmentSplitter.SplitNarration("The sign reads Welcome: \"All travellers\" in faded paint.");
            Assert.IsTrue(segs.All(s => s.Kind == SegmentKind.Narration));
        }

        // ---- B4: markdown and rich-text injection ----

        [Test]
        public void B4_TagsInSource_AreNeutralized()
        {
            string rich = MarkdownLite.ToRichText("a <size=300>huge</size> b");
            Assert.IsFalse(rich.Contains("<size=300>"), rich);
            StringAssert.Contains(MarkdownLite.EscapedLessThan + "size=300>", rich);
        }

        [Test]
        public void B4_BoldItalic()
        {
            Assert.AreEqual("a <b>bold</b> and <i>soft</i> word", MarkdownLite.ToRichText("a **bold** and *soft* word"));
            Assert.AreEqual("snake_case_name stays", MarkdownLite.ToRichText("snake_case_name stays"));
            Assert.AreEqual("2 * 3 * 4", MarkdownLite.ToRichText("2 * 3 * 4"));
        }

        [Test]
        public void B4_BlockSyntax()
        {
            Assert.AreEqual("<b><size=115%>The Mill</size></b>", MarkdownLite.ToRichText("## The Mill"));
            Assert.AreEqual("  • rope", MarkdownLite.ToRichText("- rope"));
            Assert.AreEqual("  2. torch", MarkdownLite.ToRichText("2. torch"));
            Assert.AreEqual("<i>│ quoted</i>", MarkdownLite.ToRichText("> quoted"));
        }

        // ---- B5: incremental transcript ----

        [Test]
        public void B5_CapTrim_RaisesRemovedWithIndex_NotRebuild()
        {
            var transcript = new VaultTranscript();
            var removed = new List<int>();
            int added = 0;
            transcript.Removed += delegate (int index, TranscriptSegment s) { removed.Add(index); };
            transcript.Added += delegate (TranscriptSegment s) { added++; };
            for (int i = 0; i < VaultTranscript.MaxSegments + 3; i++)
            {
                transcript.Add(new TranscriptSegment { Kind = SegmentKind.Narration, Text = "n" + i });
            }
            Assert.AreEqual(VaultTranscript.MaxSegments + 3, added);
            CollectionAssert.AreEqual(new[] { 0, 0, 0 }, removed);
            Assert.AreEqual("n3", transcript.Segments[0].Text);
        }

        [Test]
        public void B5_RemoveAndUpdate_Events()
        {
            var transcript = new VaultTranscript();
            var a = new TranscriptSegment { Text = "a" };
            var b = new TranscriptSegment { Text = "b" };
            transcript.Add(a);
            transcript.Add(b);
            int removedAt = -1;
            TranscriptSegment updated = null;
            transcript.Removed += delegate (int index, TranscriptSegment s) { removedAt = index; };
            transcript.Updated += delegate (TranscriptSegment s) { updated = s; };
            transcript.NotifyUpdated(b);
            Assert.AreSame(b, updated);
            Assert.IsTrue(transcript.Remove(b));
            Assert.AreEqual(1, removedAt);
            Assert.IsFalse(transcript.Remove(b));
        }

        // ---- Streaming: SSE decoding ----

        private static List<string> Decode(params byte[][] chunks)
        {
            var events = new List<string>();
            var decoder = new SseDecoder { OnEvent = events.Add };
            foreach (var chunk in chunks) { decoder.Feed(chunk, chunk.Length); }
            decoder.Finish();
            return events;
        }

        [Test]
        public void Sse_MultibyteGlyph_SplitAcrossReads()
        {
            byte[] all = Encoding.UTF8.GetBytes("data: {\"c\":\"“é”\"}\n\n");
            int cut = System.Array.IndexOf(all, (byte)0xE2) + 1; // inside the 3-byte “
            var events = Decode(all.Take(cut).ToArray(), all.Skip(cut).ToArray());
            CollectionAssert.AreEqual(new[] { "{\"c\":\"“é”\"}" }, events);
        }

        [Test]
        public void Sse_CommentsDoneAndCrLf()
        {
            var events = Decode(Encoding.UTF8.GetBytes(": OPENROUTER PROCESSING\r\n\r\ndata: {\"a\":1}\r\n\r\ndata: [DONE]\r\n\r\n"));
            CollectionAssert.AreEqual(new[] { "{\"a\":1}" }, events);
        }

        [Test]
        public void Sse_MultiLineData_JoinedAndFlushedWithoutTrailingBlank()
        {
            var events = Decode(Encoding.UTF8.GetBytes("data: {\"a\":\ndata: 1}"));
            CollectionAssert.AreEqual(new[] { "{\"a\":\n1}" }, events);
        }

        [Test]
        public void B13_McpSse_MultiLineFrameJoined()
        {
            Assert.AreEqual("{\"id\":\n2}", McpClient.ExtractJsonPayload("event: message\ndata: {\"id\":\ndata: 2}\n\n"));
        }

        // ---- Streaming: delta accumulation ----

        [Test]
        public void Accumulator_ContentAndToolCallDeltas_FoldToOneMessage()
        {
            var acc = new ChatStreamAccumulator();
            Assert.AreEqual("Let ", acc.Feed("{\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"Let \"}}]}"));
            Assert.AreEqual("me look.", acc.Feed("{\"choices\":[{\"delta\":{\"content\":\"me look.\"}}]}"));
            acc.Feed("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"take_turn\",\"arguments\":\"{\\\"act\"}}]}}]}");
            acc.Feed("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"ion\\\":1}\"}}]}}]}");
            acc.Feed("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_2\",\"function\":{\"name\":\"get_entity\",\"arguments\":\"{}\"},\"extra_content\":{\"google\":{\"thought_signature\":\"sig\"}}}]}}]}");
            acc.Feed("{\"choices\":[{\"delta\":{\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"data\":\"x\"}]},\"finish_reason\":\"tool_calls\"}]}");

            var message = acc.ToResponse().GetArray("choices")[0].Get("message");
            Assert.AreEqual("Let me look.", message.GetString("content", null));
            var calls = message.GetArray("tool_calls");
            Assert.AreEqual(2, calls.Count);
            Assert.AreEqual("call_1", calls[0].GetString("id", null));
            Assert.AreEqual("{\"action\":1}", calls[0].Get("function").GetString("arguments", null));
            Assert.AreEqual("sig", calls[1].Get("extra_content").Get("google").GetString("thought_signature", null));
            Assert.AreEqual(1, message.GetArray("reasoning_details").Count);
            Assert.AreEqual("tool_calls", acc.FinishReason);
        }

        [Test]
        public void Accumulator_MidStreamError()
        {
            var acc = new ChatStreamAccumulator();
            acc.Feed("{\"error\":{\"message\":\"upstream overloaded\"}}");
            Assert.AreEqual("upstream overloaded", acc.Error);
        }

        // ---- B10: replay keeps what providers need ----

        [Test]
        public void B10_Prune_KeepsBlockContentReasoningDetailsAndExtraContent()
        {
            var message = Parse("{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],"
                + "\"reasoning_details\":[{\"type\":\"reasoning.text\",\"text\":\"t\"}],"
                + "\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"x\",\"arguments\":\"{}\"},"
                + "\"extra_content\":{\"google\":{\"thought_signature\":\"s\"}}}],\"refusal\":null}");
            string content, reasoning;
            OpenAiChatDriver.ExtractContent(message, out content, out reasoning);
            var pruned = OpenAiChatDriver.PruneMessage(message, content);
            Assert.AreEqual("hi", pruned.GetString("content", null));
            Assert.AreEqual(1, pruned.GetArray("reasoning_details").Count);
            Assert.AreEqual("s", pruned.GetArray("tool_calls")[0].Get("extra_content").Get("google").GetString("thought_signature", null));
            Assert.IsTrue(pruned.Get("refusal").IsNull, "protocol leftovers are dropped");
        }
    }
}
