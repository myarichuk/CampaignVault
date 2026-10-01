using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Table;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>The party frames and the codex as view models: state in, bound values out, frames kept per id.</summary>
    public class TableViewModelTests
    {
        private GameObject _go;
        private VaultAppState _s;
        private VaultController _c;
        private readonly List<string> _opened = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TableViewModelTests");
            _s = new VaultAppState { Prompts = _go.AddComponent<SystemPromptProvider>() };
            _c = new VaultController(_s, null, new MemoryPrefs());
            _opened.Clear();
        }

        [TearDown]
        public void TearDown() { Object.DestroyImmediate(_go); }

        private void Campaign(string slug) { _s.Prompts.CampaignSlug = slug; }

        private static SessionDigest Digest(params DashboardMember[] party)
        {
            var d = new SessionDigest { Time = "Day 1, Dawn" };
            d.Party.AddRange(party);
            return d;
        }

        private static DashboardMember Member(string id, string name, double hp, double max, bool pc = false)
        {
            return new DashboardMember { Id = id, Name = name, CurHp = hp, MaxHp = max, IsPc = pc, ClassLevel = "Fighter 5" };
        }

        private static List<string> Raised(ViewModel vm)
        {
            var names = new List<string>();
            vm.propertyChanged += delegate (object sender, BindablePropertyChangedEventArgs e) { names.Add(e.propertyName.ToString()); };
            return names;
        }

        [Test]
        public void NoCampaign_OffersOne_AndNoParty()
        {
            var party = new PartyViewModel(_s, _c, _opened.Add, delegate { }, delegate { });
            party.Refresh();
            Assert.AreEqual("No campaign at the table.", party.Notice);
            Assert.IsTrue(party.ShowChoose);
            Assert.IsFalse(party.ShowBuild);
            Assert.IsEmpty(party.Members);
            party.Dispose();
        }

        [Test]
        public void SetupPending_OffersTheBuilder()
        {
            Campaign("scratch");
            _s.SetupPending = true;
            var party = new PartyViewModel(_s, _c, _opened.Add, delegate { }, delegate { });
            party.Refresh();
            Assert.IsTrue(party.ShowBuild);
            StringAssert.StartsWith("No player characters yet", party.Notice);
            party.Dispose();
        }

        [Test]
        public void Frames_AreKeptPerId_SoHpChangesAnimate()
        {
            Campaign("scratch");
            _s.PcId = "chars/aric";
            _s.Session = Digest(Member("chars/aric", "Aric Thorne", 38, 49, true), Member("chars/wren", "Sister Wren", 23, 27));
            var party = new PartyViewModel(_s, _c, _opened.Add, delegate { }, delegate { });
            party.Refresh();

            CollectionAssert.AreEqual(new[] { "member-chars/aric", "member-chars/wren" }, party.Members.Select(m => m.Name).ToArray());
            var aric = party.Members[0];
            Assert.AreEqual("AT", aric.Monogram);
            Assert.IsTrue(aric.IsPc);
            Assert.AreEqual("Fighter 5 · ally", party.Members[1].Sub);
            var raised = Raised(aric);
            var page = Raised(party);

            _s.Session = Digest(Member("chars/aric", "Aric Thorne", 20, 49, true), Member("chars/wren", "Sister Wren", 23, 27));
            _s.Notify(StateArea.Session);

            Assert.AreSame(aric, party.Members[0], "the same frame, so its bar animates");
            CollectionAssert.Contains(raised, "HpFraction");
            CollectionAssert.DoesNotContain(page, "Members");
            aric.Open();
            CollectionAssert.AreEqual(new[] { "chars/aric" }, _opened);
            party.Dispose();
        }

        [Test]
        public void TheCodex_ShowsAPagePerTab_AndAskForACampaignFirst()
        {
            var codex = new CodexViewModel(_s, _c, _opened.Add);
            codex.Show(0);
            Assert.IsInstanceOf<NoticeViewModel>(codex.Page);

            Campaign("scratch");
            _s.Notify(StateArea.Campaign);
            var quests = codex.Page as QuestsPage;
            Assert.IsNotNull(quests);
            StringAssert.StartsWith("Open the session", quests.Notice);

            _s.Session = Digest(Member("chars/aric", "Aric Thorne", 38, 49, true));
            _s.Session.Quests.Add(new DashboardQuest { Title = "The Drowned Miller", OpenObjectives = 2, Deadline = "day 4" });
            _s.Notify(StateArea.Session);
            Assert.AreEqual(string.Empty, quests.Notice);
            Assert.AreEqual("2 things left to do, due by day 4.", quests.Quests.Single().Note);

            codex.Show(3);
            Assert.IsInstanceOf<JournalPage>(codex.Page);
            Assert.IsTrue(codex.Tabs[3].Active);
            Assert.IsFalse(codex.Tabs[0].Active);
            codex.Dispose();
        }

        [Test]
        public void TheHandoff_SurvivesTabsAndRefreshes()
        {
            Campaign("scratch");
            _s.Session = Digest(Member("chars/aric", "Aric Thorne", 38, 49, true));
            var codex = new CodexViewModel(_s, _c, _opened.Add);
            codex.Show(3);
            var journal = (JournalPage)codex.Page;
            Assert.IsTrue(journal.ShowWrite);
            journal.Write();
            Assert.IsTrue(journal.ShowForm);

            var last = journal.Fields.First(f => f.Key == "last");
            last.Value = new string('x', 601);
            Assert.AreEqual("601 / 600", last.Counter);
            Assert.IsTrue(last.Over);

            codex.Show(0);
            _s.Notify(StateArea.Session);
            codex.Show(3);
            Assert.AreSame(journal, codex.Page, "one journal for the life of the table");
            Assert.AreEqual(601, journal.Fields.First(f => f.Key == "last").Value.Length);

            journal.Days = "900";
            journal.Days = "abc";
            Assert.AreEqual("abc", journal.Days, "what's typed stays as typed");
            codex.Dispose();
        }
    }
}
