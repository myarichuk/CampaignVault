using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Table;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// Phase 5: the Journal's handoff. The session remembers every NPC it showed, the DM's write-up lands in
    /// the draft clipped to the server's caps and limited to those people, and END asks first.
    /// </summary>
    public class JournalHandoffTests
    {
        private static JsonValue Parse(string json) { return JsonValue.Parse(json); }

        private static SessionDigest Table()
        {
            return SessionDigest.FromResult(Parse("{\"sessionNumber\":2,\"partyFingerprint\":\"chars/ari:12/12@locations/mill\","
                + "\"party\":[{\"id\":\"chars/ari\",\"name\":\"Ari\",\"isPc\":true,\"hp\":\"12/12\"}],"
                + "\"handoff\":{\"fromSession\":1,\"lastSession\":\"They reached the mill.\",\"npcsInPlay\":[{\"id\":\"chars/old-tam\",\"stance\":\"wary\"}]},"
                + "\"scene\":{\"presentNPCs\":[{\"id\":\"chars/mara\",\"name\":\"Mara\",\"currentMood\":\"tired\"},{\"id\":\"chars/ari\",\"name\":\"Ari\",\"isPc\":true}]}}"));
        }

        [Test]
        public void TheSession_RemembersEveryNpcItShowed()
        {
            var table = Table();
            CollectionAssert.AreEqual(new[] { "chars/old-tam", "chars/mara" }, table.SeenNpcs.Select(n => n.Id), "the handoff's people, then the opening roster; never a PC");
            Assert.AreEqual("wary", table.SeenNpcs[0].Stance);

            var update = table.ApplyTurn(null, Parse("{\"npcs\":[{\"characterId\":\"chars/old-tam\",\"name\":\"Old Tam\"}],"
                + "\"cards\":[{\"id\":\"chars/gull\",\"name\":\"Gull\",\"stance\":\"Ari: friendly (60)\"}],"
                + "\"fullScene\":{\"presentNPCs\":[{\"id\":\"chars/mara\",\"name\":\"Mara\"},{\"id\":\"chars/ari\",\"name\":\"Ari\",\"isPc\":true}]}}"));
            Assert.IsTrue(update.People);
            Assert.IsTrue(update.Any);
            CollectionAssert.AreEqual(new[] { "chars/old-tam", "chars/mara", "chars/gull" }, table.SeenNpcs.Select(n => n.Id));
            Assert.AreEqual("Old Tam", table.SeenNpcs[0].Name, "a later sighting names the id-only entry");
            Assert.AreEqual("Ari: friendly (60)", table.SeenNpcs[2].Stance);

            var again = table.ApplyTurn(null, Parse("{\"cards\":[{\"id\":\"chars/gull\",\"name\":\"Gull\"}]}"));
            Assert.IsFalse(again.People, "nothing new");
            Assert.IsNull(table.SeenNpcs.FirstOrDefault(n => n.Id == "chars/ari"), "party members are not NPCs");
        }

        [Test]
        public void TheDraft_TakesTheDmsJson_ClipsIt_AndKeepsOnlyPeopleTheSessionShowed()
        {
            var table = Table();
            var draft = new HandoffDraft();
            string error;
            string reply = "Here you go:\n```json\n{\"lastSession\":\"" + new string('a', 700) + "\",\"storySoFar\":\"The mill, then the cellar.\","
                + "\"openThreads\":[\"Who paid the miller\",\"\",\"The ledger\",\"t3\",\"t4\",\"t5\",\"t6\",\"t7\"],"
                + "\"npcsInPlay\":[{\"id\":\"chars/old-tam\",\"stance\":\"still wary\"},{\"name\":\"Mara\",\"stance\":\"grateful\"},{\"id\":\"chars/nobody\",\"stance\":\"x\"},{\"id\":\"chars/mara\",\"stance\":\"twice\"}],"
                + "\"partyIntent\":\"Follow the ledger to town.\",\"tone\":\"uneasy\"}\n```";
            Assert.IsTrue(HandoffDrafter.TryApply(reply, table, draft, out error), error);
            Assert.AreEqual(HandoffBuilder.LastSessionMax, draft.LastSession.Length, "clipped to the server's cap, ellipsis included");
            Assert.AreEqual("The mill, then the cellar.", draft.StorySoFar);
            Assert.AreEqual(6, draft.Threads.Split('\n').Length, "blank threads dropped, at most six kept");
            CollectionAssert.AreEqual(new[] { "chars/old-tam", "chars/mara" }, draft.People.Select(p => p.Id), "a name resolves, an unknown id is dropped, a repeat is dropped");
            Assert.AreEqual("grateful", draft.People[1].Stance);
            Assert.AreEqual("Follow the ledger to town.", draft.Intent);
            Assert.AreEqual("uneasy", draft.Tone);
            Assert.AreEqual(0, HandoffBuilder.Validate(draft.StorySoFar, draft.LastSession, RosterParser.ParseLines(draft.Threads), draft.People, draft.Intent, draft.Tone).Count, "what the DM drafted passes the form's own validation");

            Assert.IsFalse(HandoffDrafter.TryApply("Sorry, I can't.", table, draft, out error));
            StringAssert.Contains("JSON", error);
            Assert.IsFalse(HandoffDrafter.TryApply("{\"storySoFar\":\"x\"}", table, draft, out error));
            StringAssert.Contains("lastSession", error);
        }

        [Test]
        public void TheDmsContext_IsThisSessionsChronicle_NewestKept()
        {
            var segments = new List<TranscriptSegment>
            {
                new TranscriptSegment { Kind = SegmentKind.Narration, Text = "Last session's prose." },
                new TranscriptSegment { Kind = SegmentKind.Recap, Speaker = "Session 2", Text = "Previously…" },
                new TranscriptSegment { Kind = SegmentKind.Player, Text = "I open the door." },
                new TranscriptSegment { Kind = SegmentKind.Narration, Text = "It gives." },
                new TranscriptSegment { Kind = SegmentKind.NpcVoice, Speaker = "Mara", Text = "Don't." },
                new TranscriptSegment { Kind = SegmentKind.Roll, Text = "Perception 17 vs DC 14: Success" },
                new TranscriptSegment { Kind = SegmentKind.System, Text = "✦ Ari is now level 2." },
                new TranscriptSegment { Kind = SegmentKind.Narration, Text = "Streaming…", Streaming = true },
            };
            string chronicle = HandoffDrafter.Chronicle(segments, 10000);
            StringAssert.DoesNotContain("Last session's prose", chronicle);
            StringAssert.DoesNotContain("Perception 17", chronicle);
            StringAssert.DoesNotContain("Streaming", chronicle);
            StringAssert.Contains("PLAYER: I open the door.", chronicle);
            StringAssert.Contains("Mara: “Don't.”", chronicle);
            StringAssert.Contains("✦ Ari is now level 2.", chronicle);

            string clipped = HandoffDrafter.Chronicle(segments, 30);
            StringAssert.StartsWith("[earlier beats omitted]", clipped);
            StringAssert.Contains("level 2", clipped);
            StringAssert.DoesNotContain("I open the door", clipped);

            var table = Table();
            string user = HandoffDrafter.UserMessage(table, segments, "chars/ari — Ari");
            StringAssert.Contains("LAST SESSION: They reached the mill.", user);
            StringAssert.Contains("chars/mara — Mara", user);
            StringAssert.Contains("chars/old-tam — Old tam (was: wary)", user);
            StringAssert.Contains("PARTY: chars/ari — Ari", user);
        }

        [Test]
        public void TheJournal_PicksPeople_ReloadsTheDmsDraft_AndAsksBeforeEnding()
        {
            var go = new GameObject("journal-test");
            try
            {
                var s = new VaultAppState { Prompts = go.AddComponent<SystemPromptProvider>() };
                // The prompt provider doubles as the coroutine host: EndSession's validation runs before its first yield.
                var c = new VaultController(s, s.Prompts, new MemoryPrefs());
                s.Prompts.CampaignSlug = "scratch";
                s.Session = Table();
                var journal = new JournalPage(s, c);
                journal.Refresh();
                journal.Write();
                Assert.IsNull(journal.Fields.FirstOrDefault(f => f.Key == "npcs"), "no more typing internal ids");
                CollectionAssert.AreEqual(new[] { "chars/old-tam", "chars/mara" }, journal.Candidates.Select(ch => ch.Key));
                Assert.AreEqual("Mara", journal.Candidates[1].Label);
                Assert.AreEqual("Old tam", journal.Candidates[0].Label, "an id-only person reads as a name");
                Assert.IsTrue(journal.HasCandidates);
                Assert.IsFalse(journal.HasPeople);

                journal.Candidates[1].Pick();
                Assert.AreEqual(1, journal.People.Count);
                Assert.AreEqual("chars/mara", journal.People[0].Key);
                Assert.AreEqual(1, journal.Candidates.Count, "picked people leave the chips");
                journal.People[0].Stance = "grateful";

                string title = null;
                Action confirm = null;
                s.ConfirmRequested += delegate (string t, string m, string label, Action onConfirm) { title = t; confirm = onConfirm; };
                journal.End();
                Assert.AreEqual("End the session?", title);
                Assert.AreEqual(0, s.HandoffIssues.Count, "nothing sent before the confirmation");
                confirm();
                CollectionAssert.AreEqual(new[] { "lastSession is required." }, s.HandoffIssues, "confirmed: the form's own validation ran (no server to reach)");

                var draft = new HandoffDraft { LastSession = "The DM wrote this.", People = new List<NpcStanceRow> { new NpcStanceRow { Id = "chars/old-tam", Stance = "wary still" } } };
                // The controller's DraftHandoff bumps Version after filling the page's own draft; the page reloads on the next refresh.
                var field = journal.Fields.First(f => f.Key == "last");
                field.Value = "typed";
                var own = (HandoffDraft)typeof(JournalPage).GetField("_draft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(journal);
                own.LastSession = draft.LastSession;
                own.People = draft.People;
                own.Version++;
                journal.Refresh();
                Assert.AreEqual("The DM wrote this.", field.Value);
                CollectionAssert.AreEqual(new[] { "chars/old-tam" }, journal.People.Select(p => p.Key));
                CollectionAssert.AreEqual(new[] { "chars/mara" }, journal.Candidates.Select(ch => ch.Key));
                journal.Dispose();
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
