using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Properties;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Table
{
    /// <summary>An empty or waiting state on its own (Templates/Common/Notice.uxml).</summary>
    public sealed class NoticeViewModel : ViewModel, ITemplated
    {
        public NoticeViewModel(string icon, string notice)
        {
            NoticeIcon = icon;
            Notice = notice;
        }

        public string Template { get { return "Common/Notice"; } }
        [CreateProperty] public string Notice { get; private set; }
        [CreateProperty] public string NoticeIcon { get; private set; }
    }

    /// <summary>A codex tab, named "codex-tab-&lt;key&gt;".</summary>
    public sealed class CodexTabViewModel : ViewModel, IKeyed
    {
        private bool _active;

        public CodexTabViewModel(string key, string label, Action select)
        {
            Key = key;
            Name = "codex-tab-" + key;
            Label = label.ToUpperInvariant();
            Icon = key;
            Select = select;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Label { get; private set; }
        [CreateProperty] public string Icon { get; private set; }
        [CreateProperty] public bool Active { get { return _active; } set { Set(ref _active, value); } }
        [CreateProperty] public Action Select { get; private set; }
    }

    /// <summary>
    /// The codex drawer (Templates/Table/Codex.uxml): Quests, Scene, Pack and Journal. Each tab is a page view model
    /// kept for the life of the table, so what's typed in one (a handoff, a search) survives switching tabs.
    /// </summary>
    public sealed class CodexViewModel : ViewModel
    {
        private const StateArea Relevant = StateArea.Session | StateArea.Campaign | StateArea.Pc | StateArea.Companions | StateArea.Search | StateArea.Busy;

        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private readonly CodexPage[] _pages;
        private readonly NoticeViewModel _noCampaign = new NoticeViewModel("campaigns", "Choose a campaign to open its codex.");
        private int _tab;
        private ViewModel _page;

        public CodexViewModel(VaultAppState state, VaultController controller, Action<string> openSheet)
        {
            _s = state;
            _c = controller;
            _pages = new CodexPage[]
            {
                new QuestsPage(state),
                new ScenePage(state, controller, openSheet),
                new PackPage(state, controller),
                new JournalPage(state, controller),
            };
            Tabs = new List<CodexTabViewModel>();
            string[] keys = { "quests", "scene", "pack", "journal" };
            for (int i = 0; i < keys.Length; i++)
            {
                int index = i;
                Tabs.Add(new CodexTabViewModel(keys[i], keys[i], delegate { Show(index); }));
            }
            Watch(state, Relevant);
        }

        [CreateProperty] public List<CodexTabViewModel> Tabs { get; private set; }
        /// <summary>The open tab's page, or the "choose a campaign" notice.</summary>
        [CreateProperty] public ViewModel Page { get { return _page; } private set { Set(ref _page, value); } }

        public int Tab { get { return _tab; } }

        public void Show(int tab)
        {
            _tab = Mathf.Clamp(tab, 0, _pages.Length - 1);
            Refresh();
            if (_tab == 1 && _s.HasCampaign && _s.CompanionIds.Count > 0 && _s.Companions.Count == 0 && !_s.IsBusy("companions") && _c != null)
            {
                _c.Run(_c.LoadCompanions());
            }
        }

        public override void Refresh()
        {
            for (int i = 0; i < Tabs.Count; i++) { Tabs[i].Active = i == _tab; }
            if (!_s.HasCampaign) { Page = _noCampaign; return; }
            _pages[_tab].Refresh();
            Page = _pages[_tab];
        }
    }

    /// <summary>A codex tab's page. Most need a session; without one they show why.</summary>
    public abstract class CodexPage : ViewModel, ITemplated
    {
        protected readonly VaultAppState State;
        private string _notice = string.Empty;
        private string _noticeIcon = string.Empty;

        protected CodexPage(VaultAppState state) { State = state; }

        public abstract string Template { get; }
        [CreateProperty] public string Notice { get { return _notice; } protected set { Set(ref _notice, value); } }
        [CreateProperty] public string NoticeIcon { get { return _noticeIcon; } protected set { Set(ref _noticeIcon, value); } }
        /// <summary>No notice: the page's own content shows.</summary>
        [CreateProperty] public bool HasContent { get { return _notice.Length == 0; } }

        protected void SetNotice(string icon, string notice)
        {
            NoticeIcon = icon;
            if (Set(ref _notice, notice ?? string.Empty, "Notice")) { Notify("HasContent"); }
        }

        protected bool NeedSession()
        {
            if (State.Session != null) { return true; }
            SetNotice("journal", State.SetupPending
                ? "Nothing to read yet: the table fills in once the party exists."
                : "Open the session (Journal) or send your first line to read the table.");
            return false;
        }

        internal static string Capitalize(string text)
        {
            return string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }
    }

    // ===================================================================== quests

    public sealed class QuestViewModel : ViewModel
    {
        public QuestViewModel(DashboardQuest q)
        {
            Title = DisplayText.Plain(q.Title);
            Overdue = q.Overdue;
            var notes = new List<string>();
            if (q.OpenObjectives > 0) { notes.Add(q.OpenObjectives == 1 ? "one thing left to do" : q.OpenObjectives + " things left to do"); }
            if (q.Overdue) { notes.Add("overdue"); }
            else if (q.Deadline.Length > 0) { notes.Add("due by " + q.Deadline); }
            Note = notes.Count > 0 ? DisplayText.Plain(CodexPage.Capitalize(string.Join(", ", notes.ToArray())) + ".") : string.Empty;
        }

        [CreateProperty] public string Title { get; private set; }
        [CreateProperty] public string Note { get; private set; }
        [CreateProperty] public bool Overdue { get; private set; }
    }

    /// <summary>A ledger: a seal per quest, the title, and what's left and when, in words.</summary>
    public sealed class QuestsPage : CodexPage
    {
        private List<QuestViewModel> _quests = new List<QuestViewModel>();
        private List<DashboardQuest> _shown;

        public QuestsPage(VaultAppState state) : base(state) { }

        public override string Template { get { return "Table/QuestsPage"; } }
        [CreateProperty] public List<QuestViewModel> Quests { get { return _quests; } private set { Set(ref _quests, value); } }

        public override void Refresh()
        {
            if (!NeedSession()) { Quests = new List<QuestViewModel>(); _shown = null; return; }
            var quests = State.Session.Quests;
            SetNotice("quests", quests.Count == 0 ? "No open quests. The world is waiting." : string.Empty);
            if (quests == _shown) { return; }
            _shown = quests;
            Quests = quests.ConvertAll(delegate (DashboardQuest q) { return new QuestViewModel(q); });
        }
    }

    // ====================================================================== scene

    /// <summary>A tracked companion: crest, name, what they are, HP. Click opens the stat block.</summary>
    public sealed class AllyViewModel : ViewModel, IKeyed
    {
        private string _error = string.Empty;
        private string _monogram = string.Empty;
        private string _title = string.Empty;
        private string _sub = string.Empty;
        private bool _hasHp;
        private float _hpFraction = 1f;
        private Action _open;
        private string _openHint = string.Empty;

        public AllyViewModel(string id, Action untrack)
        {
            Key = id;
            Untrack = untrack;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Error { get { return _error; } private set { Set(ref _error, value); } }
        [CreateProperty] public bool Found { get { return _error.Length == 0; } }
        [CreateProperty] public string Monogram { get { return _monogram; } private set { Set(ref _monogram, value); } }
        [CreateProperty] public string Title { get { return _title; } private set { Set(ref _title, value); } }
        [CreateProperty] public string Sub { get { return _sub; } private set { Set(ref _sub, value); } }
        [CreateProperty] public bool HasHp { get { return _hasHp; } private set { Set(ref _hasHp, value); } }
        [CreateProperty] public float HpFraction { get { return _hpFraction; } private set { Set(ref _hpFraction, value); } }
        [CreateProperty] public Action Open { get { return _open; } private set { Set(ref _open, value); } }
        [CreateProperty] public string OpenHint { get { return _openHint; } private set { Set(ref _openHint, value); } }
        [CreateProperty] public Action Untrack { get; private set; }

        public void Update(CompanionEntry entry, DashboardMember member, Action open)
        {
            if (entry.Companion == null)
            {
                if (Set(ref _error, DisplayText.Plain(Ui.PrettyId(entry.Id) + ": " + entry.Error), "Error")) { Notify("Found"); }
                Open = null;
                OpenHint = string.Empty;
                return;
            }
            if (Set(ref _error, string.Empty, "Error")) { Notify("Found"); }
            var c = entry.Companion;
            Monogram = Ui.Monogram(c.Name);
            Title = DisplayText.Plain(c.Name);
            string sub = member != null && member.ClassLevel.Length > 0 ? member.ClassLevel : c.Kind;
            if (c.IsMinion) { sub = (sub.Length > 0 ? sub + " · " : string.Empty) + "bound to you"; }
            if (c.Stance.Length > 0) { sub = (sub.Length > 0 ? sub + " · " : string.Empty) + c.Stance; }
            Sub = DisplayText.Plain(sub);
            HasHp = c.MaxHp > 0;
            HpFraction = c.MaxHp > 0 ? (float)(c.CurrentHp / c.MaxHp) : 1f;
            if (_open == null) { Open = open; }
            OpenHint = "Open the stat block";
        }
    }

    public sealed class FaceViewModel : ViewModel
    {
        public FaceViewModel(NpcStanceRow npc)
        {
            Title = DisplayText.Plain(Ui.PrettyId(npc.Id));
            Stance = npc.Stance.Length > 0 ? DisplayText.Plain(CodexPage.Capitalize(npc.Stance) + ".") : string.Empty;
        }

        [CreateProperty] public string Title { get; private set; }
        [CreateProperty] public string Stance { get; private set; }
    }

    /// <summary>Where the party stands, in a few lines of prose; allies open their stat blocks; known faces.</summary>
    public sealed class ScenePage : CodexPage
    {
        private readonly VaultController _c;
        private readonly Action<string> _openSheet;
        private string _place = string.Empty;
        private string _time = string.Empty;
        private string _doing = string.Empty;
        private string _unplaced = string.Empty;
        private string _alliesNote = string.Empty;
        private List<AllyViewModel> _allies = new List<AllyViewModel>();
        private List<FaceViewModel> _faces = new List<FaceViewModel>();
        private HandoffDigest _facesOf;
        private string _trackId = string.Empty;

        public ScenePage(VaultAppState state, VaultController controller, Action<string> openSheet) : base(state)
        {
            _c = controller;
            _openSheet = openSheet;
            Track = delegate { _c.Run(_c.TrackCompanion(_trackId)); };
        }

        public override string Template { get { return "Table/ScenePage"; } }
        [CreateProperty] public string Place { get { return _place; } private set { Set(ref _place, value); } }
        [CreateProperty] public string Time { get { return _time; } private set { Set(ref _time, value); } }
        [CreateProperty] public string Doing { get { return _doing; } private set { Set(ref _doing, value); } }
        /// <summary>Nothing known about where the party is.</summary>
        [CreateProperty] public string Unplaced { get { return _unplaced; } private set { Set(ref _unplaced, value); } }
        [CreateProperty] public string AlliesNote { get { return _alliesNote; } private set { Set(ref _alliesNote, value); } }
        [CreateProperty] public List<AllyViewModel> Allies { get { return _allies; } private set { SetList(ref _allies, value); } }
        [CreateProperty] public List<FaceViewModel> Faces { get { return _faces; } private set { Set(ref _faces, value); } }
        [CreateProperty] public string TrackId { get { return _trackId; } set { Set(ref _trackId, value ?? string.Empty); } }
        [CreateProperty] public Action Track { get; private set; }

        public override void Refresh()
        {
            if (!NeedSession()) { return; }
            SetNotice(string.Empty, string.Empty);
            var s = State.Session;
            var pc = State.PcMember;
            Place = pc != null && pc.Location.Length > 0 ? DisplayText.Plain(Ui.PrettyId(pc.Location)) : string.Empty;
            Time = s.Time.Length > 0 ? DisplayText.Plain(VaultClientUI.ShortTime(s.Time)) : string.Empty;
            Doing = pc != null && pc.Activity.Length > 0 ? DisplayText.Plain(DescribeActivity(pc.Name, pc.Activity)) : string.Empty;
            Unplaced = Place.Length + Time.Length + Doing.Length == 0 ? "The Dungeon Master hasn't placed the party yet." : string.Empty;

            AlliesNote = State.CompanionIds.Count == 0 ? "No companions travel with you."
                : State.IsBusy("companions") && State.Companions.Count == 0 ? "Finding them…" : string.Empty;
            Allies = ItemList.Sync(_allies, State.Companions, delegate (CompanionEntry e) { return e.Id; },
                delegate (CompanionEntry e) { string id = e.Id; return new AllyViewModel(id, delegate { _c.UntrackCompanion(id); }); },
                delegate (AllyViewModel vm, CompanionEntry e) { string id = e.Id; vm.Update(e, FindMember(id), delegate { _openSheet(id); }); });

            var handoff = s.Handoff;
            if (handoff != _facesOf)
            {
                _facesOf = handoff;
                Faces = handoff == null ? new List<FaceViewModel>() : handoff.Npcs.ConvertAll(delegate (NpcStanceRow n) { return new FaceViewModel(n); });
            }
        }

        private DashboardMember FindMember(string id)
        {
            if (State.Session == null) { return null; }
            foreach (var m in State.Session.Party) { if (m.Id == id) { return m; } }
            return null;
        }

        /// <summary>
        /// "Kaelen follows the coach" stays a sentence; "Sharpening a sickle"
        /// becomes "Kaelen — sharpening a sickle." Never "Kaelen: kaelen follows…".
        /// </summary>
        internal static string DescribeActivity(string name, string activity)
        {
            string a = (activity ?? string.Empty).Trim();
            if (a.Length == 0) { return string.Empty; }
            string n = (name ?? string.Empty).Trim();
            string first = n.Split(' ')[0];
            bool named = (n.Length > 0 && a.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                || (first.Length > 0 && a.StartsWith(first + " ", StringComparison.OrdinalIgnoreCase));
            string text = named
                ? (a.StartsWith(n, StringComparison.OrdinalIgnoreCase) && n.Length > 0 ? n + a.Substring(n.Length) : first + a.Substring(first.Length))
                : (n.Length > 0 ? n + " — " + char.ToLowerInvariant(a[0]) + a.Substring(1) : Capitalize(a));
            char end = text[text.Length - 1];
            return end == '.' || end == '!' || end == '?' || end == '…' ? text : text + ".";
        }
    }

    // ======================================================================= pack

    public sealed class PackCaptionViewModel : ViewModel, ITemplated
    {
        public PackCaptionViewModel(string text) { Text = text; }
        public string Template { get { return "Table/PackCaption"; } }
        [CreateProperty] public string Text { get; private set; }
    }

    public sealed class PackItemViewModel : ViewModel, ITemplated
    {
        public PackItemViewModel(InventoryItem item, VaultController c)
        {
            Title = DisplayText.Plain(item.Name);
            ToggleLabel = item.Equipped ? "UNEQUIP" : "EQUIP";
            Use = delegate { c.UseItem(item); };
            Toggle = delegate { c.ToggleEquip(item); };
        }

        public string Template { get { return "Table/PackItem"; } }
        [CreateProperty] public string Title { get; private set; }
        [CreateProperty] public string ToggleLabel { get; private set; }
        [CreateProperty] public Action Use { get; private set; }
        [CreateProperty] public Action Toggle { get; private set; }
    }

    /// <summary>What the player character carries: equipped, then carried. Use and equip go through the DM.</summary>
    public sealed class PackPage : CodexPage
    {
        private readonly VaultController _c;
        private List<ViewModel> _rows = new List<ViewModel>();
        private string _shownKey;

        public PackPage(VaultAppState state, VaultController controller) : base(state) { _c = controller; }

        public override string Template { get { return "Table/PackPage"; } }
        [CreateProperty] public List<ViewModel> Rows { get { return _rows; } private set { Set(ref _rows, value); } }

        public override void Refresh()
        {
            if (!NeedSession()) { Show(new List<InventoryItem>()); return; }
            if (State.PcMember == null) { SetNotice("character", "No player character selected."); Show(new List<InventoryItem>()); return; }
            var items = State.Inventory();
            SetNotice("pack", items.Count == 0 ? "Nothing carried yet." : string.Empty);
            Show(items);
        }

        private void Show(List<InventoryItem> items)
        {
            var key = new System.Text.StringBuilder();
            foreach (var item in items) { key.Append(item.Id).Append('|').Append(item.Name).Append('|').Append(item.Equipped).Append('\n'); }
            if (key.ToString() == _shownKey) { return; }
            _shownKey = key.ToString();
            var rows = new List<ViewModel>();
            bool? group = null;
            foreach (var item in items)
            {
                if (group != item.Equipped)
                {
                    group = item.Equipped;
                    rows.Add(new PackCaptionViewModel(item.Equipped ? "EQUIPPED" : "CARRIED"));
                }
                rows.Add(new PackItemViewModel(item, _c));
            }
            Rows = rows;
        }
    }

    // ==================================================================== journal

    /// <summary>A handoff field with a live character counter (limit 0 = no counter).</summary>
    public sealed class CountedFieldViewModel : ViewModel, IKeyed
    {
        private readonly int _limit;
        private readonly Action<string> _changed;
        private string _value;
        private string _counter = string.Empty;
        private bool _over;

        public CountedFieldViewModel(string key, string caption, string placeholder, string value, int limit, bool multiline, Action<string> changed)
        {
            Key = key;
            Name = "handoff-" + key;
            Caption = caption.ToUpperInvariant();
            Placeholder = placeholder;
            Multiline = multiline;
            _limit = limit;
            _changed = changed;
            _value = value ?? string.Empty;
            Count();
        }

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Caption { get; private set; }
        [CreateProperty] public string Placeholder { get; private set; }
        [CreateProperty] public bool Multiline { get; private set; }
        [CreateProperty] public bool HasCounter { get { return _limit > 0; } }
        [CreateProperty] public string Counter { get { return _counter; } private set { Set(ref _counter, value); } }
        [CreateProperty] public bool Over { get { return _over; } private set { Set(ref _over, value); } }

        [CreateProperty]
        public string Value
        {
            get { return _value; }
            set
            {
                if (!Set(ref _value, value ?? string.Empty)) { return; }
                _changed(_value);
                Count();
            }
        }

        private void Count()
        {
            if (_limit <= 0) { return; }
            Counter = _value.Length + " / " + _limit;
            Over = _value.Length > _limit;
        }
    }

    public sealed class SearchHitViewModel : ViewModel
    {
        public SearchHitViewModel(SearchHit hit)
        {
            Title = DisplayText.Plain(hit.Title);
            Id = hit.Id.Length > 0 && hit.Id != hit.Title ? DisplayText.Plain(hit.Id) : string.Empty;
            Detail = hit.Detail.Length > 0 ? DisplayText.Rich(hit.Detail.Length > 600 ? hit.Detail.Substring(0, 600) + "…" : hit.Detail) : string.Empty;
        }

        [CreateProperty] public string Title { get; private set; }
        [CreateProperty] public string Id { get; private set; }
        [CreateProperty] public string Detail { get; private set; }
    }

    /// <summary>
    /// The session's book: open or refresh it, the story so far and last session's handoff, downtime, writing the
    /// handoff (checkpoint or end), and searching the world's lore. The handoff draft lives here, so it survives
    /// everything except closing the app.
    /// </summary>
    public sealed class JournalPage : CodexPage
    {
        private readonly VaultController _c;
        private readonly HandoffDraft _draft = new HandoffDraft();
        private bool _writing;
        private int _days = 1;
        private string _daysText = "1";
        private string _query = string.Empty;

        private string _status = string.Empty;
        private bool _showOpen;
        private string _openLabel = string.Empty;
        private string _openIcon = string.Empty;
        private bool _openPrimary;
        private bool _canOpen;
        private string _story = string.Empty;
        private string _lastHeading = string.Empty;
        private string _last = string.Empty;
        private List<string> _threads = new List<string>();
        private string _intent = string.Empty;
        private string _lately = string.Empty;
        private bool _showWrite;
        private bool _showForm;
        private List<string> _issues = new List<string>();
        private bool _working;
        private string _searchError = string.Empty;
        private string _searchSummary = string.Empty;
        private List<SearchHitViewModel> _hits = new List<SearchHitViewModel>();
        private string _hitsKey = string.Empty;

        public JournalPage(VaultAppState state, VaultController controller) : base(state)
        {
            _c = controller;
            Fields = new List<CountedFieldViewModel>
            {
                new CountedFieldViewModel("last", "What happened this session", "required", _draft.LastSession, 600, true, delegate (string v) { _draft.LastSession = v; }),
                new CountedFieldViewModel("story", "The story so far", "the rolling summary (leave blank to keep the last one)", _draft.StorySoFar, 800, true, delegate (string v) { _draft.StorySoFar = v; }),
                new CountedFieldViewModel("threads", "Open threads", "one per line, up to 6", _draft.Threads, 0, true, delegate (string v) { _draft.Threads = v; }),
                new CountedFieldViewModel("npcs", "People in play", "one per line: id | stance", _draft.Npcs, 0, true, delegate (string v) { _draft.Npcs = v; }),
                new CountedFieldViewModel("intent", "Party intent", "what they mean to do next", _draft.Intent, 200, false, delegate (string v) { _draft.Intent = v; }),
                new CountedFieldViewModel("tone", "Tone", "how it should feel", _draft.Tone, 120, false, delegate (string v) { _draft.Tone = v; }),
            };
            Open = delegate { _c.Run(State.Session == null ? _c.StartSession(null) : _c.RefreshTable()); };
            Advance = delegate { _c.AdvanceDays(_days); };
            Write = delegate { _writing = true; Refresh(); };
            Checkpoint = delegate { _c.Run(_c.EndSession(_draft, true)); };
            End = delegate { _c.Run(_c.EndSession(_draft, false)); };
            Search = delegate { _c.Run(_c.SearchWorld(_query)); };
        }

        public override string Template { get { return "Table/JournalPage"; } }

        [CreateProperty] public string Status { get { return _status; } private set { Set(ref _status, value); } }
        [CreateProperty] public bool ShowOpen { get { return _showOpen; } private set { Set(ref _showOpen, value); } }
        [CreateProperty] public string OpenLabel { get { return _openLabel; } private set { Set(ref _openLabel, value); } }
        [CreateProperty] public string OpenIcon { get { return _openIcon; } private set { Set(ref _openIcon, value); } }
        [CreateProperty] public bool OpenPrimary { get { return _openPrimary; } private set { Set(ref _openPrimary, value); } }
        [CreateProperty] public bool CanOpen { get { return _canOpen; } private set { Set(ref _canOpen, value); } }
        [CreateProperty] public Action Open { get; private set; }

        [CreateProperty] public string Story { get { return _story; } private set { Set(ref _story, value); } }
        [CreateProperty] public string LastHeading { get { return _lastHeading; } private set { Set(ref _lastHeading, value); } }
        [CreateProperty] public string Last { get { return _last; } private set { Set(ref _last, value); } }
        [CreateProperty] public List<string> Threads { get { return _threads; } private set { SetList(ref _threads, value); } }
        [CreateProperty] public string Intent { get { return _intent; } private set { Set(ref _intent, value); } }
        [CreateProperty] public string Lately { get { return _lately; } private set { Set(ref _lately, value); } }

        /// <summary>Days of downtime, as typed (1–365 is what's sent).</summary>
        [CreateProperty]
        public string Days
        {
            get { return _daysText; }
            set
            {
                if (!Set(ref _daysText, value ?? string.Empty)) { return; }
                int d;
                if (int.TryParse(_daysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out d)) { _days = Mathf.Clamp(d, 1, 365); }
            }
        }

        [CreateProperty] public Action Advance { get; private set; }

        [CreateProperty] public bool ShowWrite { get { return _showWrite; } private set { Set(ref _showWrite, value); } }
        [CreateProperty] public Action Write { get; private set; }
        [CreateProperty] public bool ShowForm { get { return _showForm; } private set { Set(ref _showForm, value); } }
        [CreateProperty] public List<CountedFieldViewModel> Fields { get; private set; }
        [CreateProperty] public List<string> Issues { get { return _issues; } private set { SetList(ref _issues, value); } }
        [CreateProperty] public bool Working { get { return _working; } private set { Set(ref _working, value); } }
        [CreateProperty] public bool Idle { get { return !_working; } }
        [CreateProperty] public Action Checkpoint { get; private set; }
        [CreateProperty] public Action End { get; private set; }

        [CreateProperty] public string Query { get { return _query; } set { Set(ref _query, value ?? string.Empty); } }
        [CreateProperty] public Action Search { get; private set; }
        [CreateProperty] public string SearchError { get { return _searchError; } private set { Set(ref _searchError, value); } }
        [CreateProperty] public string SearchSummary { get { return _searchSummary; } private set { Set(ref _searchSummary, value); } }
        [CreateProperty] public List<SearchHitViewModel> Hits { get { return _hits; } private set { Set(ref _hits, value); } }

        public override void Refresh()
        {
            SetNotice(string.Empty, string.Empty);
            var digest = State.Session;
            Status = State.SessionStatus.Length > 0 ? DisplayText.Plain(State.SessionStatus) : "No session opened this run.";
            bool busy = State.IsBusy("session") || State.IsBusy("refresh");
            // start_session refuses a campaign with no party: don't offer it until there is one.
            ShowOpen = !(State.SetupPending && digest == null);
            OpenLabel = digest == null ? "OPEN SESSION" : "REFRESH";
            OpenIcon = digest == null ? "d20" : "refresh";
            OpenPrimary = digest == null;
            CanOpen = !busy;

            var handoff = digest != null ? digest.Handoff : null;
            Story = handoff != null ? DisplayText.Rich(handoff.StorySoFar) : string.Empty;
            LastHeading = handoff != null && handoff.Checkpoint ? "THIS SESSION, SO FAR" : "LAST SESSION";
            Last = handoff != null ? DisplayText.Rich(handoff.LastSession) : string.Empty;
            Threads = handoff != null ? handoff.OpenThreads.ConvertAll(delegate (string t) { return DisplayText.Plain(t); }) : new List<string>();
            Intent = handoff != null ? DisplayText.Plain(handoff.PartyIntent) : string.Empty;
            Lately = handoff == null && digest != null ? DisplayText.Rich(digest.RecentDigest) : string.Empty;

            bool form = _writing || State.HandoffIssues.Count > 0;
            ShowWrite = !form;
            ShowForm = form;
            Issues = State.HandoffIssues.ConvertAll(delegate (string i) { return DisplayText.Plain(i); });
            if (Set(ref _working, State.IsBusy("handoff"), "Working")) { Notify("Idle"); }

            SearchError = DisplayText.Plain(State.SearchError);
            SearchSummary = State.SearchError.Length == 0 && State.SearchResults.Count == 0 ? DisplayText.Plain(State.SearchSummary) : string.Empty;
            var key = new System.Text.StringBuilder();
            foreach (var h in State.SearchResults) { key.Append(h.Id).Append('|').Append(h.Title).Append('|').Append(h.Detail.Length).Append('\n'); }
            if (key.ToString() != _hitsKey)
            {
                _hitsKey = key.ToString();
                Hits = State.SearchResults.ConvertAll(delegate (SearchHit h) { return new SearchHitViewModel(h); });
            }
        }
    }
}
