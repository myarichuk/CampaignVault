using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Server;
using CampaignVault.UnityClient.UI;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// The table's user story around a session: no campaign means no chat,
    /// a session opening shows its recap once, the DM's own start/end_session
    /// moves the table, and the chronicle survives a restart.
    /// </summary>
    public class SessionFlowTests
    {
        private GameObject _go;
        private VaultBootstrap _boot;
        private MemoryPrefs _prefs;
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("SessionFlowTest");
            _boot = _go.AddComponent<VaultBootstrap>();
            _prefs = new MemoryPrefs();
            _boot.Initialize(_prefs);
            _dir = Path.Combine(Path.GetTempPath(), "vault-chronicle-" + System.Guid.NewGuid().ToString("N"));
            _boot.State.Store = new TranscriptStore(_dir);
            // A usable provider, so only the table decides whether a line can go out.
            _boot.State.Byok.Active.Preset = "ollama";
            _boot.State.Byok.BaseUrl = "http://127.0.0.1:9";
            _boot.State.Byok.Model = "test";
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
            if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); }
        }

        private static JsonValue Parse(string json) { return JsonValue.Parse(json); }

        private static SessionDigest Digest(int number, bool resumed, string lastSession)
        {
            return SessionDigest.FromResult(Parse("{\"sessionNumber\":" + number + ",\"resumed\":" + (resumed ? "true" : "false")
                + ",\"handoff\":{\"lastSession\":" + JsonValue.FromString(lastSession).ToJson() + ",\"partyIntent\":\"find the miller\"},"
                + "\"campaign\":{\"pcs\":[{\"id\":\"chars/ari\",\"name\":\"Ari\"}]}}"));
        }

        private List<TranscriptSegment> Recaps()
        {
            return _boot.State.Transcript.Segments.Where(s => s.Kind == SegmentKind.Recap).ToList();
        }

        [Test]
        public void NoCampaign_NoChat_OpensTheCampaignBook()
        {
            var s = _boot.State;
            bool asked = false;
            string toast = null;
            s.CampaignsRequested += delegate { asked = true; };
            s.Toast += delegate (string m, ToastKind k) { toast = m; };
            int before = s.Transcript.Segments.Count;

            Assert.IsFalse(_boot.Controller.SendPlayerText("I look around."));

            Assert.IsTrue(asked);
            StringAssert.Contains("campaign", toast);
            Assert.AreEqual(before, s.Transcript.Segments.Count, "nothing reaches the log or the DM");
        }

        [Test]
        public void SessionOpening_ShowsItsRecapOnce_AndIsRemembered()
        {
            var c = _boot.Controller;
            c.SelectCampaign("mill", "Dnd5e");
            c.ApplySession(Digest(4, true, "Ari found tracks in the flour."));
            c.ApplySession(Digest(4, true, "Ari found tracks in the flour."));

            var recaps = Recaps();
            Assert.AreEqual(1, recaps.Count, "a refresh never repeats the recap");
            StringAssert.StartsWith("Session 4 · resumed", recaps[0].Speaker);
            StringAssert.Contains("Previously: Ari found tracks in the flour.", recaps[0].Text);
            StringAssert.Contains("You meant to: find the miller", recaps[0].Text);
            Assert.AreEqual(4, c.RememberedOpenSession, "a relaunch resumes it");
        }

        [Test]
        public void DmEndsTheSession_TableFollows_NextSessionGetsItsOwnRecap()
        {
            var c = _boot.Controller;
            var s = _boot.State;
            c.SelectCampaign("mill", "Dnd5e");
            c.OnDriverTool("start_session", "{\"success\":true,\"data\":{\"sessionNumber\":2,\"campaign\":{\"pcs\":[{\"id\":\"chars/ari\",\"name\":\"Ari\"}]}}}");
            Assert.IsNotNull(s.Session, "the DM's own start_session opens the table");
            Assert.AreEqual(2, s.Session.SessionNumber);

            c.OnDriverTool("end_session", "{\"success\":true,\"data\":{\"checkpoint\":true}}");
            Assert.IsNotNull(s.Session, "a checkpoint keeps the session open");

            c.OnDriverTool("end_session", "{\"success\":true,\"data\":{\"checkpoint\":false}}");
            Assert.IsNull(s.Session);
            Assert.AreEqual(0, c.RememberedOpenSession);
            Assert.AreEqual("Session 2 ends", Recaps().Last().Speaker);

            c.ApplySession(Digest(3, false, "They left the mill."));
            Assert.AreEqual("Session 3 begins", Recaps().Last().Speaker);
        }

        [Test]
        public void FailedTool_DoesNotMoveTheTable()
        {
            var c = _boot.Controller;
            c.SelectCampaign("mill", "Dnd5e");
            c.OnDriverTool("start_session", "{\"success\":false,\"error\":\"SLUG_NOT_FOUND\",\"summary\":\"no\"}");
            Assert.IsNull(_boot.State.Session);
        }

        [Test]
        public void Chronicle_RoundTrips_AndComesBackWithTheCampaign()
        {
            var s = _boot.State;
            var c = _boot.Controller;
            c.SelectCampaign("mill", "Dnd5e");
            c.ApplySession(Digest(1, false, string.Empty));
            var turn = new List<TranscriptSegment>
            {
                new TranscriptSegment { Kind = SegmentKind.Player, Text = "I knock." },
                new TranscriptSegment { Kind = SegmentKind.ToolData, Text = "⚙ take_turn" },
                new TranscriptSegment { Kind = SegmentKind.Notes, Text = "bookkeeping" },
                new TranscriptSegment { Kind = SegmentKind.Narration, Text = "The door opens a crack." },
                new TranscriptSegment { Kind = SegmentKind.NpcVoice, Speaker = "Hedda", Text = "Who's there?" },
                new TranscriptSegment { Kind = SegmentKind.Roll, Roll = new RollInfo { Label = "Perception", Detail = "17 vs DC 14", Verdict = "Success", Outcome = RollOutcome.Success, Total = 17 } },
            };
            Assert.IsTrue(s.Store.Append("mill", turn));
            File.AppendAllText(s.Store.PathFor("mill"), "not json\n");

            c.SelectCampaign("elsewhere", "Dnd5e");
            Assert.IsFalse(s.Transcript.Segments.Any(x => x.Text == "The door opens a crack."), "each campaign has its own log");

            c.SelectCampaign("mill", "Dnd5e");
            var kinds = s.Transcript.Segments.Where(x => x.Restored).Select(x => x.Kind).ToList();
            CollectionAssert.AreEqual(new[] { SegmentKind.Recap, SegmentKind.Player, SegmentKind.Narration, SegmentKind.NpcVoice, SegmentKind.Roll }, kinds,
                "the story comes back; tool lines, notes and bad lines don't");
            var roll = s.Transcript.Segments.First(x => x.Kind == SegmentKind.Roll).Roll;
            Assert.AreEqual(RollOutcome.Success, roll.Outcome);
            Assert.AreEqual(17, roll.Total);
            Assert.AreEqual("The door opens a crack.\n\nHedda: “Who's there?”", s.Driver.LastPassage, "the storyteller remembers the last scene");
        }

        [Test]
        public void Recap_NotRepeatedWhenTheChronicleAlreadyEndsOnIt()
        {
            var c = _boot.Controller;
            c.SelectCampaign("mill", "Dnd5e");
            c.ApplySession(Digest(5, true, "They rested."));
            // Relaunch: same campaign, the chronicle ends on that recap, and the session resumes again.
            c.SelectCampaign("other", "Dnd5e");
            c.SelectCampaign("mill", "Dnd5e");
            c.ApplySession(Digest(5, true, "They rested."));
            Assert.AreEqual(1, Recaps().Count);
        }

        [Test]
        public void ChangedTheWorld_IgnoresReadOnlyTurns()
        {
            var read = new AI.TurnRecord();
            read.Tools.Add("load_skill");
            read.Tools.Add("get_entity");
            Assert.IsFalse(VaultController.ChangedTheWorld(read));
            var write = new AI.TurnRecord();
            write.Tools.Add("take_turn");
            Assert.IsTrue(VaultController.ChangedTheWorld(write));
        }

        [Test]
        public void StoryTextSize_ClampsAndPersists()
        {
            var c = _boot.Controller;
            Assert.AreEqual(VaultAppState.DefaultStoryTextSize, _boot.State.StoryTextSize);
            Assert.AreEqual(VaultAppState.StoryTextSizes.Length - 1, c.SetStoryTextSize(99));
            Assert.AreEqual(0, c.SetStoryTextSize(-4));
            Assert.AreEqual(0, _prefs.GetInt(PrefKeys.StoryTextSize, -1));
            var root = new UnityEngine.UIElements.VisualElement();
            TextSizeControl.Apply(root, 3);
            TextSizeControl.Apply(root, 1);
            Assert.IsTrue(root.ClassListContains("cv-story-1"));
            Assert.IsFalse(root.ClassListContains("cv-story-3"));
        }

        [Test]
        public void SceneActivity_ReadsAsASentence()
        {
            Assert.AreEqual("Kaelen follows the coach north.", CodexView.DescribeActivity("Kaelen", "kaelen follows the coach north"));
            Assert.AreEqual("Hedda — sharpening a sickle on the step.", CodexView.DescribeActivity("Hedda", "Sharpening a sickle on the step"));
            Assert.AreEqual("Aric waits!", CodexView.DescribeActivity("Aric Thorne", "Aric waits!"));
        }

        [Test]
        public void FindDotnet_SearchesInstallFoldersAGuiAppsPathLacks()
        {
            var present = new HashSet<string> { Path.Combine("/usr/local/share/dotnet", "dotnet") };
            Assert.AreEqual("/usr/local/share/dotnet",
                EmbeddedServerSupport.FindDotnetDir(null, "/usr/bin:/bin", "/Users/x", false, present.Contains),
                "a Dock-launched app's bare PATH still finds the installer's dotnet");
            present.Add(Path.Combine("/opt/custom", "dotnet"));
            Assert.AreEqual("/opt/custom", EmbeddedServerSupport.FindDotnetDir("/opt/custom", "/usr/bin", "/Users/x", false, present.Contains), "DOTNET_ROOT wins");
            Assert.IsNull(EmbeddedServerSupport.FindDotnetDir(null, "/usr/bin", "/Users/x", false, delegate { return false; }));
            Assert.AreEqual("/d:/usr/bin", EmbeddedServerSupport.PathWithDotnet("/d", "/usr/bin", false));
        }

        [Test]
        public void StartupFailure_PrefersTheUnhandledException()
        {
            var lines = new List<string>
            {
                "Unhandled exception. System.InvalidOperationException: Unable to execute dotnet to retrieve list of installed runtimes.",
                "   at Raven.Embedded.RuntimeFrameworkVersionMatcher.GetFrameworkVersionsAsync",
            };
            StringAssert.StartsWith("System.InvalidOperationException: Unable to execute dotnet", EmbeddedServerSupport.StartupFailure(lines));
            Assert.AreEqual("boom", EmbeddedServerSupport.StartupFailure(new List<string> { "warn", "boom" }));
            Assert.IsNull(EmbeddedServerSupport.StartupFailure(new List<string>()));
        }
    }
}
