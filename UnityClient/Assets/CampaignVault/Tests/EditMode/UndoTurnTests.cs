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
