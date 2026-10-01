using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Sheet;

namespace CampaignVault.UnityClient.UI.Table
{
    /// <summary>One party frame, named "member-&lt;id&gt;". Kept per id across refreshes, so HP changes animate.</summary>
    public sealed class MemberViewModel : ViewModel, IKeyed
    {
        private string _monogram = string.Empty;
        private string _title = string.Empty;
        private string _sub = string.Empty;
        private bool _isPc;
        private bool _hasHp;
        private float _hpFraction = 1f;
        private string _hpText = string.Empty;
        private List<BadgeViewModel> _conditions = new List<BadgeViewModel>();
        private string _conditionsKey = string.Empty;

        public MemberViewModel(string id, Action open)
        {
            Key = id;
            Name = "member-" + id;
            Open = open;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public Action Open { get; private set; }
        [CreateProperty] public string Monogram { get { return _monogram; } private set { Set(ref _monogram, value); } }
        [CreateProperty] public string Title { get { return _title; } private set { Set(ref _title, value); } }
        [CreateProperty] public string Sub { get { return _sub; } private set { Set(ref _sub, value); } }
        [CreateProperty] public bool IsPc { get { return _isPc; } private set { Set(ref _isPc, value); } }
        /// <summary>Unknown HP (a narrative ruleset, or not bootstrapped yet): no bar at all rather than a bare dash.</summary>
        [CreateProperty] public bool HasHp { get { return _hasHp; } private set { Set(ref _hasHp, value); } }
        [CreateProperty] public float HpFraction { get { return _hpFraction; } private set { Set(ref _hpFraction, value); } }
        [CreateProperty] public string HpText { get { return _hpText; } private set { Set(ref _hpText, value); } }
        [CreateProperty] public List<BadgeViewModel> Conditions { get { return _conditions; } private set { Set(ref _conditions, value); } }

        public void Update(DashboardMember m)
        {
            Monogram = Ui.Monogram(m.Name);
            Title = DisplayText.Plain(m.Name);
            string sub = m.ClassLevel;
            if (!m.IsPc) { sub = (sub.Length > 0 ? sub + " · " : string.Empty) + "ally"; }
            Sub = DisplayText.Plain(sub);
            IsPc = m.IsPc;
            HasHp = m.MaxHp > 0;
            HpFraction = (float)m.HpFraction;
            HpText = m.HpText;
            string key = string.Join("\n", m.Conditions.ToArray());
            if (key == _conditionsKey) { return; }
            _conditionsKey = key;
            var chips = new List<BadgeViewModel>();
            for (int i = 0; i < m.Conditions.Count && i < 3; i++) { chips.Add(new BadgeViewModel(m.Conditions[i], "blood", null)); }
            if (m.Conditions.Count > 3) { chips.Add(new BadgeViewModel("+" + (m.Conditions.Count - 3), "blood", null)); }
            Conditions = chips;
        }
    }

    /// <summary>The party frames down the left edge (Templates/Table/Party.uxml), and what to do when there's no party.</summary>
    public sealed class PartyViewModel : ViewModel
    {
        private readonly VaultAppState _s;
        private List<MemberViewModel> _members = new List<MemberViewModel>();
        private string _notice = string.Empty;
        private string _noticeIcon = "party";
        private bool _showChoose;
        private bool _showBuild;
        private readonly Action<string> _openSheet;

        public PartyViewModel(VaultAppState state, VaultController controller, Action<string> openSheet, Action openCampaigns, Action openBuilder)
        {
            _s = state;
            _openSheet = openSheet;
            Build = openBuilder;
            Choose = openCampaigns;
            RefreshTable = delegate { controller.Run(controller.RefreshTable()); };
            Watch(state, StateArea.Session | StateArea.Campaign | StateArea.Pc | StateArea.Busy);
        }

        [CreateProperty] public List<MemberViewModel> Members { get { return _members; } private set { SetList(ref _members, value); } }
        [CreateProperty] public string Notice { get { return _notice; } private set { Set(ref _notice, value); } }
        [CreateProperty] public string NoticeIcon { get { return _noticeIcon; } private set { Set(ref _noticeIcon, value); } }
        [CreateProperty] public bool ShowChoose { get { return _showChoose; } private set { Set(ref _showChoose, value); } }
        [CreateProperty] public bool ShowBuild { get { return _showBuild; } private set { Set(ref _showBuild, value); } }
        [CreateProperty] public Action Build { get; private set; }
        [CreateProperty] public Action Choose { get; private set; }
        /// <summary>Re-reads the table (HP, quests, time) from the server.</summary>
        [CreateProperty] public Action RefreshTable { get; private set; }

        public override void Refresh()
        {
            var session = _s.Session;
            if (session == null || session.Party.Count == 0)
            {
                Members = new List<MemberViewModel>();
                ShowChoose = !_s.HasCampaign;
                bool busy = _s.IsBusy("refresh") || _s.IsBusy("session");
                ShowBuild = _s.HasCampaign && session == null && _s.SetupPending && !busy;
                NoticeIcon = _s.HasCampaign ? "party" : "campaigns";
                if (!_s.HasCampaign) { Notice = "No campaign at the table."; }
                // One OPEN SESSION lives in the Journal; the first line sent opens it too.
                else if (session == null)
                {
                    Notice = busy ? "Gathering the party…"
                        : _s.SetupPending ? "No player characters yet. Build one, or the DM creates them from your answers."
                        : "The party gathers when the session opens.";
                }
                else { Notice = "No party on record yet."; }
                return;
            }
            Notice = string.Empty;
            ShowChoose = false;
            ShowBuild = false;
            Members = ItemList.Sync(_members, session.Party, delegate (DashboardMember m) { return m.Id; },
                delegate (DashboardMember m) { string id = m.Id; return new MemberViewModel(id, delegate { _openSheet(id); }); },
                delegate (MemberViewModel vm, DashboardMember m) { vm.Update(m); });
        }
    }
}
