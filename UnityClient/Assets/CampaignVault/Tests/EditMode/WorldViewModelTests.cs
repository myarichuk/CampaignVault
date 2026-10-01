using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Dialogs;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.World;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>The campaign book, the onboarding pages and the small dialogs as view models.</summary>
    public class WorldViewModelTests
    {
        private GameObject _go;
        private VaultAppState _s;
        private VaultController _c;
        private int _closed;
        private readonly List<KeyValuePair<Action, long>> _later = new List<KeyValuePair<Action, long>>();

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("WorldViewModelTests");
            _s = new VaultAppState { Prompts = _go.AddComponent<SystemPromptProvider>() };
            _c = new VaultController(_s, null, new MemoryPrefs());
            _closed = 0;
            _later.Clear();
        }

        [TearDown]
        public void TearDown() { UnityEngine.Object.DestroyImmediate(_go); }

        private CampaignsViewModel Book()
        {
            return new CampaignsViewModel(_s, _c, delegate { _closed++; }, delegate { },
                delegate (Action action, long ms) { _later.Add(new KeyValuePair<Action, long>(action, ms)); });
        }

        private OnboardingViewModel Onboarding() { return new OnboardingViewModel(_s, _c, delegate { _closed++; }); }

        private static OnboardingQuestion Question(AnswerType type, string key = "world", string text = "Describe the world.", params string[] options)
        {
            var q = new OnboardingQuestion { Key = key, Text = text, Help = "A paragraph.", Type = type };
            q.Options.AddRange(options);
            return q;
        }

        private void Ask(OnboardingQuestion q)
        {
            var ob = _s.Onboarding;
            ob.Phase = OnboardingPhase.Question;
            ob.Slug = "lantern";
            ob.Question = q;
            _s.Notify(StateArea.Onboarding);
        }

        [Test]
        public void TheBook_SaysWhyItIsEmpty_ThenListsCardsPerSlug()
        {
            var book = Book();
            book.Refresh();
            Assert.AreEqual("Opening the campaign book…", book.Notice);
            Assert.IsEmpty(book.Cards);

            _s.CampaignsLoaded = true;
            _s.Notify(StateArea.Campaigns);
            StringAssert.StartsWith("No campaigns yet", book.Notice);

            _s.Campaigns.Add(new CampaignRow { Slug = "lantern", Display = "The Lantern", System = "Dnd5e" });
            _s.Campaigns.Add(new CampaignRow { Slug = "tide", Display = "Tide", System = "Narrative" });
            _s.Notify(StateArea.Campaigns);
            Assert.AreEqual(string.Empty, book.Notice);
            CollectionAssert.AreEqual(new[] { "lantern", "tide" }, book.Cards.Select(c => c.Slug).ToArray());
            Assert.AreEqual("D&D 5E", book.Cards[0].System);
            var lantern = book.Cards[0];

            _s.Campaigns.Insert(0, new CampaignRow { Slug = "aaa", Display = "A" });
            _s.Notify(StateArea.Campaigns);
            Assert.AreSame(lantern, book.Cards[1], "a card is kept across refreshes");
            book.Dispose();
        }

        [Test]
        public void Delete_NeedsTwoTaps_AndDisarmsWhenYouWalkAway()
        {
            _s.CampaignsLoaded = true;
            _s.Campaigns.Add(new CampaignRow { Slug = "lantern", Display = "The Lantern" });
            var book = Book();
            book.Refresh();
            var card = book.Cards[0];
            Assert.AreEqual("DELETE", card.DeleteLabel);

            card.Delete();
            Assert.AreEqual("CONFIRM DELETE", card.DeleteLabel);
            Assert.IsTrue(card.Armed);
            Assert.AreEqual(1, _later.Count);

            _later[0].Key();
            Assert.AreEqual("DELETE", card.DeleteLabel, "the arming lapses");
            Assert.IsFalse(card.Armed);
            book.Dispose();
        }

        [Test]
        public void StartPage_PreviewsTheCampaignId_AndPicksTheRules()
        {
            var vm = Onboarding();
            vm.Refresh();
            var start = (StartPageViewModel)vm.Page;
            start.Name = "The Sunken Crown!";
            Assert.AreEqual("campaign id: the-sunken-crown", start.SlugPreview);
            start.Systems.First(s => s.Name == "system-Narrative").Pick();
            Assert.AreEqual("Narrative", start.System);
            Assert.IsTrue(start.Systems.First(s => s.Key == "Narrative").Selected);
            CollectionAssert.AreEqual(new[] { "onboarding-begin" }, vm.Actions.Select(a => a.Name).ToArray());
            vm.Dispose();
        }

        [Test]
        public void Questions_GetAPagePerKind_AndTheAnswerButtonOnlyForWrittenOnes()
        {
            var vm = Onboarding();
            Ask(Question(AnswerType.Choice, "mode", "How?", "describe-now", "dm-pregenerates"));
            var choice = (ChoicePageViewModel)vm.Page;
            CollectionAssert.AreEqual(new[] { "I'll describe them", "The DM makes them, I approve" }, choice.Options.Select(o => o.Label).ToArray());
            Assert.IsEmpty(vm.Actions);

            Ask(Question(AnswerType.YesNo, "tragic", "Tragic?"));
            Assert.IsInstanceOf<YesNoPageViewModel>(vm.Page);

            Ask(Question(AnswerType.Text));
            Assert.IsInstanceOf<TextPageViewModel>(vm.Page);
            CollectionAssert.AreEqual(new[] { "onboarding-answer" }, vm.Actions.Select(a => a.Name).ToArray());
            vm.Dispose();
        }

        [Test]
        public void PartyStep_ListsBuiltCharacters_AndOffersTheThreeWaysToFillTheParty()
        {
            var vm = Onboarding();
            _s.Onboarding.System = "Dnd5e";
            Ask(Question(AnswerType.Party, "party", "Who is in the party?"));
            var page = (PartyPageViewModel)vm.Page;
            Assert.IsEmpty(vm.Actions, "the page carries its own buttons");
            CollectionAssert.AreEqual(new[] { "party-add", "party-dm", "party-table" }, page.Actions.Select(a => a.Name).ToArray());
            Assert.IsTrue(page.Empty);
            Assert.IsTrue(page.ShowLevel);

            string asked = null;
            _s.PartyBuilderRequested += delegate (string id) { asked = id; };
            page.Actions[0].Run();
            Assert.AreEqual(string.Empty, asked, "ADD opens the builder for a new character");

            _s.Onboarding.Party.Add(new PartyMember { Id = "chars/lyra-1", Name = "Lyra", ClassLine = "Ranger 1", Level = 1 });
            _s.Notify(StateArea.Onboarding);
            Assert.AreSame(page, vm.Page, "building a character doesn't rebuild the page");
            Assert.AreEqual(1, page.Members.Count);
            Assert.AreEqual("Lyra", page.Members[0].Title);
            Assert.AreEqual("Ranger 1", page.Members[0].Line);
            Assert.AreEqual("party-edit-lyra", page.Members[0].EditName);
            CollectionAssert.AreEqual(new[] { "party-add", "party-use", "party-dm", "party-table" }, page.Actions.Select(a => a.Name).ToArray());
            Assert.IsFalse(page.Actions.First(a => a.Name == "party-dm").Enabled, "drafting or skipping would leave the built characters behind");
            page.Members[0].Edit();
            Assert.AreEqual("chars/lyra-1", asked, "EDIT opens the builder on that character");

            var member = page.Members[0];
            _s.Onboarding.Party[0].Name = "Lyra Vale";
            _s.Notify(StateArea.Onboarding);
            Assert.AreSame(member, page.Members[0], "an edited card keeps its element");
            Assert.AreEqual("Lyra Vale", page.Members[0].Title);
            vm.Dispose();
        }

        [Test]
        public void PartyStep_Narrative_HasNoLevelAndNoBuilder()
        {
            var vm = Onboarding();
            _s.Onboarding.System = "Narrative";
            Ask(Question(AnswerType.Party, "party"));
            var page = (PartyPageViewModel)vm.Page;
            Assert.IsFalse(page.ShowLevel);
            Assert.IsFalse(page.Actions.First(a => a.Name == "party-add").Enabled);
            vm.Dispose();
        }

        [Test]
        public void PartyStep_Level_IsOneChoiceInStateAndCappedAtWhatTheBuilderSupports()
        {
            var vm = Onboarding();
            _s.Onboarding.System = "Dnd5e";
            Ask(Question(AnswerType.Party, "party"));
            var page = (PartyPageViewModel)vm.Page;
            Assert.AreEqual(VaultController.MaxBuilderLevel, page.Levels.Count);
            page.Levels[2].Pick();
            Assert.AreEqual(3, _s.Onboarding.PartyLevel);
            Assert.IsTrue(page.Levels[2].Selected);
            Assert.IsFalse(page.Levels[0].Selected);
            _c.SetPartyLevel(99);
            Assert.AreEqual(VaultController.MaxBuilderLevel, _s.Onboarding.PartyLevel);
            vm.Dispose();
        }

        [Test]
        public void PartyAnswer_SplitsCharactersFromCompanions_AndParses()
        {
            var built = new List<PartyMember>
            {
                new PartyMember { Id = "chars/lyra", Kind = "pc" },
                new PartyMember { Id = "chars/dog", Kind = "companion" }
            };
            var answer = Parse(VaultController.PartyAnswer(OnboardingState.PartyBuildNow, 2, built));
            Assert.AreEqual("build-now", answer.GetString("mode", string.Empty));
            Assert.AreEqual(2, (int)answer.GetNumber("level", 0));
            Assert.AreEqual("chars/lyra", answer.GetArray("characterIds")[0].StringValue);
            Assert.AreEqual("chars/dog", answer.GetArray("companionIds")[0].StringValue);
            Assert.AreEqual(0, Parse(VaultController.PartyAnswer(OnboardingState.PartyBuildAtTable, 1, null)).GetArray("characterIds").Count);
        }

        [Test]
        public void ParseQuestion_PartyType_FromEnumIndexOrName()
        {
            Assert.AreEqual(AnswerType.Party, VaultController.ParseQuestion(Parse("{\"key\":\"party\",\"answerType\":5}")).Type);
            Assert.AreEqual(AnswerType.Party, VaultController.ParseQuestion(Parse("{\"answerType\":\"Party\"}")).Type);
        }

        private static CampaignVault.UnityClient.Json.JsonValue Parse(string json)
        {
            CampaignVault.UnityClient.Json.JsonValue v;
            Assert.IsTrue(CampaignVault.UnityClient.Json.JsonValue.TryParse(json, out v), json);
            return v;
        }

        [Test]
        public void TheAnswerField_KeepsTyping_AndTakesAWriteUpFromState()
        {
            var vm = Onboarding();
            var q = Question(AnswerType.Text);
            Ask(q);
            var page = (TextPageViewModel)vm.Page;
            page.Draft = "A drowned mill";
            _s.Notify(StateArea.Onboarding);
            Assert.AreEqual("A drowned mill", page.Draft);
            Assert.AreEqual("A drowned mill", _s.Onboarding.Draft, "the draft lives in state, so the brainstorm round trip keeps it");
            Assert.AreSame(page, vm.Page, "the same question keeps its page");

            _s.Onboarding.Draft = new string('x', 90);
            _s.Notify(StateArea.Onboarding);
            Assert.AreEqual(90, page.Draft.Length, "a write-up from the brainstorm lands in the field");
            Assert.IsTrue(page.Multiline, "a long answer gets room");

            Ask(Question(AnswerType.Text, "tone", "Tone?"));
            Assert.AreNotSame(page, vm.Page, "a new question gets a fresh page");
            vm.Dispose();
        }

        [Test]
        public void TheAnswerCounter_AppearsHalfWayAndWarnsOverTheLimit()
        {
            var vm = Onboarding();
            Ask(Question(AnswerType.Text));
            var page = (TextPageViewModel)vm.Page;
            Assert.IsFalse(page.CountVisible);
            page.Draft = new string('x', OnboardingBrainstorm.MaxAnswerChars / 2 + 1);
            Assert.IsTrue(page.CountVisible);
            Assert.IsFalse(page.Over);
            page.Draft = new string('x', OnboardingBrainstorm.MaxAnswerChars + 1);
            Assert.IsTrue(page.Over);
            StringAssert.Contains("6,001 / 6,000", page.Count);
            vm.Dispose();
        }

        [Test]
        public void Brainstorm_DocksTheComposer_AndSendWaitsForAMessageThatFits()
        {
            var vm = Onboarding();
            Ask(Question(AnswerType.Text));
            var ob = _s.Onboarding;
            ob.Brainstorming = true;
            ob.BrainstormChat.Add(new KeyValuePair<string, string>("user", "sea caves and smugglers"));
            ob.BrainstormChat.Add(new KeyValuePair<string, string>("assistant", "How about **a drowned mill**?"));
            _s.Notify(StateArea.Onboarding);

            Assert.IsInstanceOf<BrainstormPageViewModel>(vm.Page);
            var composer = (BrainstormComposerViewModel)vm.Dock;
            CollectionAssert.AreEqual(new[] { "onboarding-back", "onboarding-writeup", "onboarding-send" }, vm.Actions.Select(a => a.Name).ToArray());
            var chat = ((BrainstormPageViewModel)vm.Page).Items;
            Assert.AreEqual(2, chat.Count);
            Assert.IsTrue(((ChatBubbleViewModel)chat[0]).Mine);
            var send = vm.Actions.Single(a => a.Name == "onboarding-send");
            Assert.IsTrue(send.Enabled);

            composer.Draft = new string('x', OnboardingBrainstorm.MaxMessageChars + 1);
            Assert.IsFalse(send.Enabled, "too long to send");
            StringAssert.StartsWith("TOO LONG TO SEND", composer.Count);
            composer.Draft = "shorter";
            Assert.IsTrue(send.Enabled);

            ob.Brainstorming = false;
            _s.Notify(StateArea.Onboarding);
            Assert.IsNull(vm.Dock);
            vm.Dispose();
        }

        [Test]
        public void TheChat_FoldsALongMessageOfYours_UntilYouUnfoldIt()
        {
            var vm = Onboarding();
            Ask(Question(AnswerType.Text));
            var ob = _s.Onboarding;
            ob.Brainstorming = true;
            ob.BrainstormChat.Add(new KeyValuePair<string, string>("user", string.Join("\n", Enumerable.Range(0, 30).Select(i => "line " + i).ToArray())));
            ob.BrainstormChat.Add(new KeyValuePair<string, string>("assistant", "ok"));
            _s.Notify(StateArea.Onboarding);

            var bubble = (ChatBubbleViewModel)((BrainstormPageViewModel)vm.Page).Items[0];
            Assert.IsTrue(bubble.CanToggle);
            Assert.AreEqual("SHOW ALL", bubble.ToggleLabel);
            StringAssert.EndsWith("…", bubble.Paragraphs.Last().TrimEnd());
            bubble.Toggle();
            Assert.AreEqual("SHOW LESS", bubble.ToggleLabel);
            Assert.IsFalse(((ChatBubbleViewModel)((BrainstormPageViewModel)vm.Page).Items[1]).CanToggle, "only your own long messages fold");
            vm.Dispose();
        }

        [Test]
        public void TheOtherPhases_ShowWhatTheyAreAndTheirButtons()
        {
            var vm = Onboarding();
            var ob = _s.Onboarding;
            ob.Phase = OnboardingPhase.Working;
            ob.Status = "Creating the campaign…";
            _s.Notify(StateArea.Onboarding);
            Assert.AreEqual("Creating the campaign…", ((StatusPageViewModel)vm.Page).Notice);
            Assert.IsEmpty(vm.Actions);

            ob.Phase = OnboardingPhase.ReadyToFinalize;
            _s.Notify(StateArea.Onboarding);
            CollectionAssert.AreEqual(new[] { "onboarding-finalize" }, vm.Actions.Select(a => a.Name).ToArray());

            ob.Phase = OnboardingPhase.Done;
            ob.DoneSummary = "**Lantern** is ready.";
            ob.NextSteps.Add("Seed the world");
            _s.Notify(StateArea.Onboarding);
            var done = (DonePageViewModel)vm.Page;
            Assert.AreEqual(1, done.Steps.Count);
            CollectionAssert.AreEqual(new[] { "onboarding-table", "onboarding-seed" }, vm.Actions.Select(a => a.Name).ToArray());
            vm.Actions[0].Run();
            Assert.AreEqual(1, _closed);

            ob.Phase = OnboardingPhase.Failed;
            ob.Error = "The server said no.";
            _s.Notify(StateArea.Onboarding);
            Assert.AreEqual("The server said no.", ((StatusPageViewModel)vm.Page).Notice);
            CollectionAssert.AreEqual(new[] { "onboarding-restart" }, vm.Actions.Select(a => a.Name).ToArray());
            vm.Dispose();
        }

        [Test]
        public void Confirm_ReportsTheChoice_AndDangerIsNotPrimary()
        {
            int cancelled = 0, confirmed = 0;
            var vm = new ConfirmViewModel("Gone for good.", "DELETE", true, delegate { cancelled++; }, delegate { confirmed++; });
            Assert.IsTrue(vm.Danger);
            Assert.IsFalse(vm.Primary);
            vm.Cancel();
            vm.Confirm();
            Assert.AreEqual(1, cancelled);
            Assert.AreEqual(1, confirmed);
        }
    }
}
