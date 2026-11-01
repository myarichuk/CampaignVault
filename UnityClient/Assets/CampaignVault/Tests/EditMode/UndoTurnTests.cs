using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using CampaignVault.UnityClient.Model;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>Phase 5: undoing the last turn rewrites the chronicle file to before its player line.</summary>
    public class UndoTurnTests
    {
        private string _dir;

        [SetUp] public void SetUp() { _dir = Path.Combine(Path.GetTempPath(), "vault-undo-" + System.Guid.NewGuid().ToString("N")); }
        [TearDown] public void TearDown() { if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); } }

        [Test]
        public void LineCount_IsCached_AndFollowsEveryWrite()
        {
            // LineCount runs on every streamed token ("is there an earlier page"): it must not read the file each time,
            // and the cached figure must still match the file after each kind of write.
            var store = new TranscriptStore(_dir);
            store.Append("mill", new[] { Seg(SegmentKind.Player, "a"), Seg(SegmentKind.Narration, "b") });
            Assert.AreEqual(2, store.LineCount("mill"));
            store.Append("mill", new[] { Seg(SegmentKind.Player, "c"), Seg(SegmentKind.Aside, "not kept"), Seg(SegmentKind.Narration, "d\nstill one line") });
            Assert.AreEqual(4, store.LineCount("mill"), "an append bumps the cached count by the lines it wrote");
            Assert.AreEqual(4, new TranscriptStore(_dir).LineCount("mill"), "and agrees with a fresh count of the file");

            Assert.IsTrue(store.DropLastTurn("mill"));
            Assert.AreEqual(2, store.LineCount("mill"));
            Assert.AreEqual(2, File.ReadAllLines(store.PathFor("mill")).Length);

            store.Delete("mill");
            Assert.AreEqual(0, store.LineCount("mill"));
            store.Append("mill", new[] { Seg(SegmentKind.Player, "e") });
            Assert.AreEqual(1, store.LineCount("mill"));
        }

        private static TranscriptSegment Seg(SegmentKind kind, string text) { return new TranscriptSegment { Kind = kind, Text = text }; }

        private static List<TranscriptSegment> Turn(string player, string scene)
        {
            return new List<TranscriptSegment>
            {
                new TranscriptSegment { Kind = SegmentKind.Player, Text = player },
                new TranscriptSegment { Kind = SegmentKind.Narration, Text = scene },
                new TranscriptSegment { Kind = SegmentKind.NpcVoice, Speaker = "Mara", Text = "Hm." },
            };
        }

        [Test]
        public void TheChronicle_LosesItsLastTurn_ButNeverAClosedSession()
        {
            var store = new TranscriptStore(_dir);
            store.Append("mill", Turn("I look.", "Dust."));
            store.Append("mill", Turn("I listen.", "Silence."));
            Assert.IsTrue(store.DropLastTurn("mill"));
            var left = store.Load("mill");
            CollectionAssert.AreEqual(new[] { "I look.", "Dust.", "Hm." }, left.Select(s => s.Text));

            store.Append("mill", new[] { new TranscriptSegment { Kind = SegmentKind.Recap, Speaker = "Session 1 ends", Text = "Done." } });
            Assert.IsFalse(store.DropLastTurn("mill"), "a recap after the turn closes it");
            Assert.AreEqual(4, store.Load("mill").Count);

            Assert.IsTrue(store.DropLastTurn("mill") == false);
            Assert.IsFalse(store.DropLastTurn("nowhere"), "no file, nothing cut");
            store.Delete("mill");
            store.Append("mill", new[] { new TranscriptSegment { Kind = SegmentKind.Narration, Text = "No player line." } });
            Assert.IsFalse(store.DropLastTurn("mill"));
        }
    }
}
