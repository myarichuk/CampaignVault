using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Server;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Shell;
using CampaignVault.UnityClient.UI.Table;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>The toasts, the top bar, the command bar and the story log as view models.</summary>
    public class ShellViewModelTests
    {
        private GameObject _go;
        private VaultAppState _s;
        private VaultController _c;
        private readonly List<KeyValuePair<Action, long>> _later = new List<KeyValuePair<Action, long>>();

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("ShellViewModelTests");
            _s = new VaultAppState { Prompts = _go.AddComponent<SystemPromptProvider>(), FxEnabled = false };
            _c = new VaultController(_s, null, new MemoryPrefs());
            _later.Clear();
        }

        [TearDown]
        public void TearDown() { UnityEngine.Object.DestroyImmediate(_go); }

        private void Later(Action action, long ms) { _later.Add(new KeyValuePair<Action, long>(action, ms)); }

        /// <summary>Runs what was scheduled for this delay (the panel's clock, by hand).</summary>
        private void Elapse(long ms)
        {
            foreach (var due in _later.Where(l => l.Value == ms).ToList())
            {
                _later.Remove(due);
                due.Key();
            }
        }

        private static TranscriptSegment Seg(SegmentKind kind, string text, string speaker = "")
        {
            return new TranscriptSegment { Kind = kind, Text = text, Speaker = speaker };
        }

        // ------------------------------------------------------------------ toasts

        [Test]
        public void Toast_SlidesIn_Lingers_AndFadesOut()
        {
            var toasts = new ToastsViewModel(_s, Later);
            _s.RaiseToast("Saved <b>it</b>.", ToastKind.Success);
            var toast = toasts.Items.Single();
            Assert.AreEqual("success", toast.Kind);
            Assert.AreEqual("check", toast.Icon);
            Assert.AreEqual(DisplayText.Plain("Saved <b>it</b>."), toast.Message, "shown as typed, never interpreted");
            Assert.IsTrue(toast.Entering);

            Elapse(20);
            Assert.IsFalse(toast.Entering);
            Elapse(4200);
            Assert.IsTrue(toast.Leaving);
            Assert.AreEqual(1, toasts.Items.Count, "still there while it fades");
            Elapse(300);
            Assert.AreEqual(0, toasts.Items.Count);
            toasts.Dispose();
        }

        [Test]
        public void Toasts_ErrorsLingerLonger_AndOnlyFourShow()
        {
            var toasts = new ToastsViewModel(_s, Later);
            var changes = new List<NotifyCollectionChangedAction>();
            toasts.Items.CollectionChanged += delegate (object sender, NotifyCollectionChangedEventArgs e) { changes.Add(e.Action); };
            _s.RaiseToast("Boom.", ToastKind.Error);
            Assert.IsTrue(_later.Any(l => l.Value == 8000));
            for (int i = 0; i < 5; i++) { _s.RaiseToast("Note " + i, ToastKind.Info); }
            Assert.AreEqual(ToastsViewModel.MaxToasts, toasts.Items.Count);
            Assert.AreEqual("Note 4", toasts.Items.Last().Message);
            Assert.IsFalse(toasts.Items.Any(t => t.Kind == "error"), "the oldest went first");
            Assert.AreEqual(2, changes.Count(a => a == NotifyCollectionChangedAction.Remove), "removed one at a time");

            var second = toasts.Items[0];
            second.Dismiss();
            second.Dismiss();
            Assert.AreEqual(1, _later.Count(l => l.Value == 300), "dismissing twice fades once");
            toasts.Dispose();
        }

        // ----------------------------------------------------------------- top bar

        private TopBarViewModel Bar(List<string> log = null)
        {
            log = log ?? new List<string>();
            return new TopBarViewModel(_s, _c, delegate { log.Add("campaigns"); }, delegate { log.Add("codex"); },
                delegate { log.Add("settings"); }, delegate { log.Add("provider"); }, delegate { log.Add("leave"); });
        }

        [Test]
        public void TopBar_SaysWhereWeAre_AndFollowsTheServer()
        {
            var bar = Bar();
            bar.Refresh();
            Assert.AreEqual("NO CAMPAIGN AT THE TABLE", bar.ContextTitle);
            Assert.AreEqual("Open the campaign book to begin.", bar.ContextMeta);

            _s.Prompts.CampaignSlug = "drowned-mill";
            _s.Notify(StateArea.Campaign);
            Assert.AreEqual("DROWNED MILL", bar.ContextTitle);
            Assert.AreEqual("The session hasn't opened yet.", bar.ContextMeta);
            Assert.AreEqual("Drowned Mill\nThe session hasn't opened yet.", bar.ContextFull);

            Assert.IsFalse(bar.ServerOk);
            _s.Connection = ConnectionStatus.Healthy;
            _s.Notify(StateArea.Connection);
            Assert.IsTrue(bar.ServerOk);
            StringAssert.Contains("connected", bar.ServerTip);
            _s.Connection = ConnectionStatus.Down;
            _s.Notify(StateArea.Connection);
            Assert.IsTrue(bar.ServerBad);
            Assert.IsFalse(bar.ServerOk);
            bar.Dispose();
        }

        [Test]
        public void TopBar_CommandsReachThePages_AndTheTextMenuToggles()
        {
            var log = new List<string>();
            var bar = Bar(log);
            bar.OpenCampaigns(); bar.ToggleCodex(); bar.ToggleSettings(); bar.OpenProvider(); bar.Leave();
            CollectionAssert.AreEqual(new[] { "campaigns", "codex", "settings", "provider", "leave" }, log);

            var raised = new List<string>();
            bar.propertyChanged += delegate (object sender, BindablePropertyChangedEventArgs e) { raised.Add(e.propertyName.ToString()); };
            bar.ToggleTextMenu();
            Assert.IsTrue(bar.TextMenuOpen);
            bar.ShowTextMenu(true);
            bar.ShowTextMenu(false);
            Assert.IsFalse(bar.TextMenuOpen);
            Assert.AreEqual(2, raised.Count(n => n == "TextMenuOpen"), "setting the same value raises nothing");
            Assert.AreEqual("Day 1, Month 1, Year 1492 — Dawn", TopBarViewModel.ShortTime("Day 1, Month 1, Year 1492 (A drowned mill town…) — Dawn"));
            bar.Dispose();
        }

        // ------------------------------------------------------------- command bar

        [Test]
        public void CommandBar_GatesPlayUntilTheDmIsSetUp()
        {
            var bar = new CommandBarViewModel(_s, _c);
            bar.Refresh();
            Assert.IsTrue(bar.GateVisible);
            StringAssert.StartsWith("The Dungeon Master isn't set up", bar.GateText);
            Assert.IsFalse(bar.Ready);
            Assert.IsFalse(bar.CanAct);
            StringAssert.StartsWith("Set up the Dungeon Master", bar.Placeholder);
            Assert.AreEqual("ACT", bar.ActLabel);
            Assert.IsFalse(bar.Working);
            bar.Dispose();
        }

        [Test]
        public void CommandBar_QuickActions_StartTheSentence_OrActAtOnce()
        {
            var bar = new CommandBarViewModel(_s, _c);
            Assert.AreEqual(6, bar.Quicks.Count);
            var focus = bar.FocusRequest;
            bar.Quicks.First(q => q.Label == "ATTACK").Run();
            Assert.AreEqual("I attack ", bar.Draft);
            Assert.Greater(bar.FocusRequest, focus, "the box takes the keyboard");

            bar.Draft = "I draw my sword";
            bar.Quicks.First(q => q.Label == "REST").Run();
            Assert.AreEqual("I draw my sword We take a short rest.", bar.Draft);
            bar.Dispose();
        }

        [Test]
        public void CommandBar_UpRecallsSentLines_AndDownReturnsToTheDraft()
        {
            var bar = new CommandBarViewModel(_s, _c);
            Assert.IsNull(bar.RecallOlder("typing"), "nothing sent yet");
            bar.Remember("first");
            bar.Remember("second");
            bar.Remember("second");
            Assert.AreEqual("second", bar.RecallOlder("half a thought"));
            Assert.AreEqual("first", bar.RecallOlder("ignored"));
            Assert.AreEqual("first", bar.RecallOlder("ignored"), "stops at the oldest");
            Assert.AreEqual("second", bar.RecallNewer());
            Assert.AreEqual("half a thought", bar.RecallNewer(), "past the newest is what was being typed");
            Assert.IsNull(bar.RecallNewer());
            bar.Dispose();
        }

        // --------------------------------------------------------------- story log

        private StoryLogViewModel Log() { return new StoryLogViewModel(_s, Later); }

        [Test]
        public void StoryLog_OneItemPerSegment_AndActivityFoldsIntoAStrip()
        {
            var log = Log();
            _s.Transcript.Add(Seg(SegmentKind.Narration, "The mill groans."));
            _s.Transcript.Add(Seg(SegmentKind.ToolData, "⚙ search_world"));
            _s.Transcript.Add(Seg(SegmentKind.ToolData, "✦ the DM consults the ledger"));
            _s.Transcript.Add(Seg(SegmentKind.Notes, "A note."));
            _s.Transcript.Add(Seg(SegmentKind.NpcVoice, "Those aren't rat tracks.", "Mirelle"));

            Assert.AreEqual(5, log.EntryCount, "one entry per segment");
            Assert.AreEqual(3, log.Stories.Count, "narration, one strip, one voice");
            var strip = (ActivityStripViewModel)log.Stories[1];
            Assert.AreEqual(3, strip.Lines.Count);
            Assert.AreEqual("THE DM CONSULTS THE LEDGER · 2 · NOTES", strip.Title);
            Assert.AreEqual("calls search_world", strip.Lines[0].Text);
            Assert.AreEqual("Story/ActivityNotes", strip.Lines[2].Template);
            Assert.AreEqual("MIRELLE", ((VoiceViewModel)log.Stories[2]).Speaker);
            strip.Toggle();
            Assert.IsTrue(strip.Open);
            log.Dispose();
        }

        [Test]
        public void StoryLog_Streaming_UpdatesTheSameItem_AndAKindChangeSwapsIt()
        {
            var log = Log();
            var seg = Seg(SegmentKind.Narration, "The mill");
            seg.Streaming = true;
            _s.Transcript.Add(seg);
            var prose = (ProseViewModel)log.Stories[0];
            var raised = new List<string>();
            prose.propertyChanged += delegate (object sender, BindablePropertyChangedEventArgs e) { raised.Add(e.propertyName.ToString()); };
            var changes = new List<NotifyCollectionChangedAction>();
            log.Stories.CollectionChanged += delegate (object sender, NotifyCollectionChangedEventArgs e) { changes.Add(e.Action); };

            seg.Text = "The mill groans.";
            _s.Transcript.NotifyUpdated(seg);
            Assert.AreSame(prose, log.Stories[0], "the same item grows in place");
            CollectionAssert.Contains(raised, "Text");
            Assert.IsEmpty(changes);
            Assert.IsTrue(prose.Streaming);

            seg.Streaming = false;
            seg.Kind = SegmentKind.Aside;
            _s.Transcript.NotifyUpdated(seg);
            Assert.AreNotSame(prose, log.Stories[0]);
            Assert.AreEqual("Story/Aside", ((StoryItemViewModel)log.Stories[0]).Template);
            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Replace }, changes);
            log.Dispose();
        }

        [Test]
        public void StoryLog_TrimmingTheFront_RemovesOneItem_AndAStripLosesALine()
        {
            var log = Log();
            _s.Transcript.Add(Seg(SegmentKind.ToolData, "⚙ one"));
            _s.Transcript.Add(Seg(SegmentKind.ToolData, "⚙ two"));
            _s.Transcript.Add(Seg(SegmentKind.Narration, "Then."));
            var strip = (ActivityStripViewModel)log.Stories[0];
            var changes = new List<NotifyCollectionChangedAction>();
            log.Stories.CollectionChanged += delegate (object sender, NotifyCollectionChangedEventArgs e) { changes.Add(e.Action); };

            _s.Transcript.Remove(_s.Transcript.Segments[0]);
            Assert.AreEqual(1, strip.Lines.Count);
            Assert.AreEqual("THE DM CONSULTS THE LEDGER · 1", strip.Title);
            Assert.IsEmpty(changes, "the strip itself stays");
            _s.Transcript.Remove(_s.Transcript.Segments[0]);
            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Remove }, changes);
            Assert.AreEqual(1, log.Stories.Count);
            Assert.AreEqual(1, log.EntryCount);
            log.Dispose();
        }

        [Test]
        public void StoryLog_FollowsTheNewestLine_OnlyWhilePinned()
        {
            var log = Log();
            int ticks = log.ScrollTick;
            _s.Transcript.Add(Seg(SegmentKind.Narration, "One."));
            Assert.IsFalse(log.ShowJump);
            log.SetPinned(false);
            _s.Transcript.Add(Seg(SegmentKind.Narration, "Two."));
            Assert.IsTrue(log.ShowJump, "scrolled up and more arrived");
            Assert.AreEqual(ticks, log.ScrollTick);
            log.Jump();
            Assert.IsFalse(log.ShowJump);
            Assert.IsTrue(log.Pinned);
            Assert.Greater(log.ScrollTick, ticks);
            log.SetPinned(false);
            _s.Transcript.Add(Seg(SegmentKind.Player, "I look."));
            Assert.IsTrue(log.Pinned, "the player's own line always snaps down");
            log.Dispose();
        }

        [Test]
        public void StoryLog_Rolls_ShowTheTotalAndTheVerdict()
        {
            var log = Log();
            var seg = Seg(SegmentKind.Roll, string.Empty);
            seg.Roll = new RollInfo { Label = "Search (Perception)", Detail = "17 vs DC 14", Verdict = "CriticalSuccess", Outcome = RollOutcome.CriticalSuccess };
            _s.Transcript.Add(seg);
            var roll = (RollViewModel)log.Stories[0];
            Assert.AreEqual("crit-success", roll.Outcome);
            Assert.AreEqual("17", roll.Total);
            Assert.AreEqual("against DC 14", roll.Against);
            Assert.AreEqual("CRITICAL SUCCESS", roll.Verdict);
            Assert.IsFalse(roll.Entering, "no tumbling with effects off");
            Assert.AreEqual("none", StoryText.SpeakerHue(""));
            Assert.AreEqual(StoryText.SpeakerHue("Mirelle"), StoryText.SpeakerHue("Mirelle"));
            log.Dispose();
        }

        [Test]
        public void StoryLog_Clearing_EmptiesTheLog()
        {
            var log = Log();
            _s.Transcript.Add(Seg(SegmentKind.Narration, "One."));
            _s.Transcript.Clear();
            Assert.AreEqual(0, log.Stories.Count);
            Assert.AreEqual(0, log.EntryCount);
            log.Dispose();
        }
    }
}
