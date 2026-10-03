using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Table;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>The failure card: friendly line, copyable details, RETRY or OPEN SETTINGS, in the story and beside the other model calls.</summary>
    public class ErrorCardTests
    {
        private GameObject _go;
        private VaultAppState _s;
        private int _retries;
        private int _settings;
        private readonly List<string> _notes = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("ErrorCardTests");
            _s = new VaultAppState { Prompts = _go.AddComponent<SystemPromptProvider>(), FxEnabled = false };
            _retries = 0;
            _settings = 0;
            _notes.Clear();
        }

        [TearDown]
        public void TearDown() { UnityEngine.Object.DestroyImmediate(_go); }

        private static LlmFailure Failure(long status, string raw = "provider said no")
        {
            return LlmFailure.From(status, raw, new LlmFailure.Context { Endpoint = "https://example.test/v1/chat/completions", Model = "m-1", Attempts = 3 });
        }

        private ErrorCardViewModel Card(LlmFailure failure, System.Func<bool> canRetry = null)
        {
            return new ErrorCardViewModel(failure, delegate { _retries++; }, delegate { _settings++; }, delegate (string note) { _notes.Add(note); }, canRetry);
        }

        [Test]
        public void TransientFailure_OffersRetry_NotSettings_AndShowsTheFriendlyLine()
        {
            var card = Card(Failure(503));
            Assert.IsTrue(card.ShowRetry);
            Assert.IsFalse(card.ShowSettings);
            StringAssert.Contains("press RETRY", card.Friendly);
            card.Retry();
            Assert.AreEqual(1, _retries);
        }

        [TestCase(401L)]
        [TestCase(404L)]
        public void SettingsFailure_OffersOpenSettings_InsteadOfRetry(long status)
        {
            var card = Card(Failure(status));
            Assert.IsFalse(card.ShowRetry, "the same request fails the same way until Settings change");
            Assert.IsTrue(card.ShowSettings);
            card.OpenSettings();
            Assert.AreEqual(1, _settings);
        }

        [Test]
        public void Details_StartFolded_ToggleOpen_AndCopyPutsTheTechnicalBlockOnTheClipboard()
        {
            var card = Card(Failure(429));
            Assert.IsFalse(card.Open);
            card.Toggle();
            Assert.IsTrue(card.Open);
            StringAssert.Contains("http status: 429", card.Technical);
            string before = GUIUtility.systemCopyBuffer;
            try
            {
                card.Copy();
                Assert.AreEqual(card.Technical, GUIUtility.systemCopyBuffer);
                Assert.AreEqual(1, _notes.Count);
            }
            finally { GUIUtility.systemCopyBuffer = before; }
        }

        [Test]
        public void Retry_IsOfferedOnlyWhileTheRequestCanStillBeResent()
        {
            bool can = true;
            var card = Card(Failure(503), delegate { return can; });
            Assert.IsTrue(card.ShowRetry);
            can = false;
            card.Refresh();
            Assert.IsFalse(card.ShowRetry, "an older failure's line has moved on");
        }

        [Test]
        public void Story_ShowsAFailedSegmentAsACard_AndAPlainWarningAsALine()
        {
            var log = new StoryLogViewModel(_s, delegate { });
            _s.Transcript.Add(new TranscriptSegment { Kind = SegmentKind.System, Text = "⚠ plain problem" });
            _s.Transcript.Add(new TranscriptSegment { Kind = SegmentKind.System, Text = "⚠ the model failed", Failure = Failure(503) });
            Assert.IsInstanceOf<SystemLineViewModel>(log.Stories[0]);
            var card = (ErrorCardViewModel)log.Stories[1];
            Assert.AreEqual("Common/ErrorCard", card.Template);

            _s.RetryTurnRequested += delegate { _retries++; };
            _s.SettingsRequested += delegate { _settings++; };
            _s.TurnRetryAvailable = delegate { return true; };
            card.Refresh();
            Assert.IsTrue(card.ShowRetry);
            card.Retry();
            Assert.AreEqual(1, _retries, "RETRY asks the controller to resend the last line");
            log.Dispose();
        }

        [Test]
        public void Story_RetryHides_OnceTheLastLineIsNoLongerRetryable()
        {
            var log = new StoryLogViewModel(_s, delegate { });
            _s.TurnRetryAvailable = delegate { return true; };
            _s.Transcript.Add(new TranscriptSegment { Kind = SegmentKind.System, Text = "⚠ failed", Failure = Failure(503) });
            var card = (ErrorCardViewModel)log.Stories.Single();
            Assert.IsTrue(card.ShowRetry);
            _s.TurnRetryAvailable = delegate { return false; };
            _s.Notify(StateArea.Driver);
            Assert.IsFalse(card.ShowRetry);
            log.Dispose();
        }

        [Test]
        public void Slot_KeepsOneCardPerFailure_AndDropsItWhenTheErrorTextIsGone()
        {
            var slot = new ErrorCardSlot();
            var call = new FailedCall { Failure = Failure(503), Message = "boom", Retry = delegate { _retries++; } };
            var first = slot.Sync(call, "boom");
            Assert.IsNotNull(first);
            first.Toggle();
            Assert.AreSame(first, slot.Sync(call, "boom"), "a repaint keeps the card (and its open details)");
            Assert.IsNull(slot.Sync(call, string.Empty), "the screen cleared the error");
            Assert.IsNull(slot.Sync(call, "another error"), "a different error isn't this call's");
            Assert.IsNull(slot.Sync(null, "boom"));
            Assert.IsTrue(slot.Sync(call, "boom").ShowRetry);
            slot.Sync(call, "boom").Retry();
            Assert.AreEqual(1, _retries);
        }

        [Test]
        public void Template_LoadsAndShowsTheFriendlyLineAndTheDetails()
        {
            var card = Card(Failure(503));
            var root = Templates.Clone(card.Template);
            Assert.IsNotNull(root, "Common/ErrorCard.uxml loads");
            root.dataSource = card;
            Assert.IsNotNull(root.Q(className: "cv-errcard__text"));
            Assert.IsNotNull(root.Q(className: "cv-errcard__technical"));
            Assert.AreEqual(3, root.Query<CampaignVault.UnityClient.UI.Controls.VaultButton>().ToList().Count, "COPY DETAILS, RETRY and OPEN SETTINGS");
        }
    }
}
