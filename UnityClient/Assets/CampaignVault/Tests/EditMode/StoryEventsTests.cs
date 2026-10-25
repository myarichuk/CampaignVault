using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Table;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// Phase 5, the story log: muted event cards (combat, a downed PC, an engine warning), the story as plain
    /// text, and the chronicle's earlier pages.
    /// </summary>
    public class StoryEventsTests
    {
        private GameObject _go;
        private VaultBootstrap _boot;
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("StoryEventsTest");
            _boot = _go.AddComponent<VaultBootstrap>();
            _boot.Initialize(new MemoryPrefs());
            _dir = Path.Combine(Path.GetTempPath(), "vault-story-" + System.Guid.NewGuid().ToString("N"));
            _boot.State.Store = new TranscriptStore(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
            if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); }
        }

        private static JsonValue Parse(string json) { return JsonValue.Parse(json); }

        private List<TranscriptSegment> SystemLines()
        {
            return _boot.State.Transcript.Segments.Where(s => s.Kind == SegmentKind.System).ToList();
        }

        [Test]
        public void TheEngine_GetsMutedCards_CombatDownedAndWarnings()
        {
            var c = _boot.Controller;
            c.SelectCampaign("mill", "Dnd5e");
            c.OnDriverTool("start_session", "{\"success\":true,\"data\":{\"sessionNumber\":1,\"partyFingerprint\":\"chars/ari:12/12@locations/mill\","
                + "\"party\":[{\"id\":\"chars/ari\",\"name\":\"Ari\",\"isPc\":true,\"hp\":\"12/12\"}]}}");
            int before = SystemLines().Count;

            c.OnDriverTool("combat", Parse("{\"action\":\"start\",\"locationId\":\"locations/mill\"}"), "{\"success\":true,\"data\":{\"summary\":\"Combat started\"}}");
            var lines = SystemLines();
            Assert.AreEqual(before + 1, lines.Count);
            StringAssert.StartsWith("⚔", lines.Last().Text);
            Assert.AreEqual("attack", new SystemLineViewModel(lines.Last()).Icon);
            Assert.AreEqual("Combat begins. Initiative is rolled.", new SystemLineViewModel(lines.Last()).Text, "the glyph becomes the icon");

            c.OnDriverTool("take_turn", Parse("{\"request\":{\"changes\":[]}}"), "{\"success\":true,\"worldPressure\":[\"ENGINE WARNING: location locations/mill has no exits — commit {\\\"$type\\\":\\\"location_update\\\"}\"],"
                + "\"data\":{\"partyFingerprint\":\"chars/ari:0/12@locations/mill\"}}");
            lines = SystemLines();
            Assert.AreEqual(before + 3, lines.Count);
            Assert.AreEqual("☠ Ari is down. A death save is due on their turn.", lines[lines.Count - 2].Text);
            Assert.AreEqual("⚙ ENGINE WARNING: location locations/mill has no exits — commit", lines.Last().Text, "the JSON to paste is the DM's business");
            Assert.AreEqual("warning", new SystemLineViewModel(lines.Last()).Icon);
            Assert.IsFalse(new SystemLineViewModel(lines.Last()).Error, "muted, not red");

            c.OnDriverTool("take_turn", Parse("{}"), "{\"success\":true,\"worldPressure\":[\"ENGINE WARNING: location locations/mill has no exits — commit {}\"],\"data\":{\"partyFingerprint\":\"chars/ari:0/12@locations/mill\"}}");
            Assert.AreEqual(before + 3, SystemLines().Count, "the same warning twice in a row shows once; a PC already down is not downed again");

            c.OnDriverTool("combat", Parse("{\"action\":\"end\"}"), "{\"success\":true}");
            Assert.AreEqual("⚔ Combat ends.", SystemLines().Last().Text);
        }

        [Test]
        public void TheStory_CopiesAsPlainText()
        {
            var segments = new List<TranscriptSegment>
            {
                new TranscriptSegment { Kind = SegmentKind.Recap, Speaker = "Session 1 begins", Text = "Previously…" },
                new TranscriptSegment { Kind = SegmentKind.Player, Text = "I look." },
                new TranscriptSegment { Kind = SegmentKind.ToolData, Text = "take_turn …" },
                new TranscriptSegment { Kind = SegmentKind.Roll, Text = "x", Roll = new RollInfo { Label = "Perception", Detail = "17 vs DC 14", Verdict = "Success" } },
                new TranscriptSegment { Kind = SegmentKind.Narration, Text = "Dust." },
                new TranscriptSegment { Kind = SegmentKind.NpcVoice, Speaker = "Mara", Text = "Hm." },
                new TranscriptSegment { Kind = SegmentKind.System, Text = "✦ Ari joins the party." },
                new TranscriptSegment { Kind = SegmentKind.Narration, Text = "half", Streaming = true },
            };
            Assert.AreEqual("— Session 1 begins —\nPreviously…\n\n> I look.\n\n[Perception 17 vs DC 14: Success]\n\nDust.\n\nMara: “Hm.”", StoryText.PlainStory(segments));
        }

        [Test]
        public void TheChronicle_HasEarlierPages_ThatLandAboveTheStory()
        {
            var s = _boot.State;
            var c = _boot.Controller;
            var all = new List<TranscriptSegment>();
            for (int i = 0; i < TranscriptStore.LoadLimit + 5; i++)
            {
                all.Add(new TranscriptSegment { Kind = i % 2 == 0 ? SegmentKind.Player : SegmentKind.Narration, Text = "line " + i });
            }
            s.Store.Append("mill", all);
            Assert.AreEqual(TranscriptStore.LoadLimit + 5, s.Store.LineCount("mill"));
            CollectionAssert.AreEqual(new[] { "line 0", "line 1", "line 2" }, s.Store.Load("mill", 3, TranscriptStore.LoadLimit + 2).Select(x => x.Text), "the page before the last N lines");

            var log = new StoryLogViewModel(s, delegate { });
            c.SelectCampaign("mill", "Dnd5e");
            Assert.AreEqual("line 5", s.Transcript.Segments[0].Text, "the newest page is what loads");
            Assert.IsTrue(c.CanLoadEarlier);
            Assert.IsTrue(log.ShowEarlier);
            Assert.IsTrue(log.HasStory);

            c.OnDriverTool("start_session", "{\"success\":true,\"data\":{\"sessionNumber\":2}}");
            Assert.IsTrue(c.CanLoadEarlier, "lines written this run don't count as earlier");

            Assert.IsTrue(c.LoadEarlierHistory());
            Assert.AreEqual("line 0", s.Transcript.Segments[0].Text);
            Assert.AreEqual("line 4", s.Transcript.Segments[4].Text);
            Assert.AreEqual("line 5", s.Transcript.Segments[5].Text);
            Assert.AreEqual("line 0", ((PlayerViewModel)log.Stories[0]).Text, "the story shows the page at the top");
            Assert.IsFalse(c.CanLoadEarlier);
            Assert.IsFalse(log.ShowEarlier);
            Assert.IsFalse(c.LoadEarlierHistory());
            log.Dispose();
        }
    }
}
