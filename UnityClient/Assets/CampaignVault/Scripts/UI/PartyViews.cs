using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Party frames down the left edge: monogram, name, class, HP with a
    /// lagging damage ghost, conditions. Frames are keyed by character id and
    /// updated in place, so HP changes animate instead of redrawing.
    /// </summary>
    public sealed class PartyFramesView
    {
        private readonly VisualElement _list;
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private readonly Action<string> _openSheet;
        private readonly Action _openCampaigns;
        private readonly Dictionary<string, VisualElement> _frames = new Dictionary<string, VisualElement>();

        public PartyFramesView(VisualElement host, VaultAppState state, VaultController controller, Action<string> openSheet, Action openCampaigns)
        {
            _state = state;
            _controller = controller;
            _openSheet = openSheet;
            _openCampaigns = openCampaigns;
            var head = Ui.El("cv-party__head");
            head.Add(Ui.Text("THE PARTY", "cv-caption cv-grow"));
            head.Add(Ui.IconButton("refresh", "Refresh the table (HP, quests, time)", "cv-btn--ghost cv-btn--small", delegate { _controller.Run(_controller.RefreshTable()); }));
            host.Add(head);
            _list = Ui.El("cv-party__list");
            host.Add(_list);
            state.Changed += delegate (StateArea area) { if ((area & (StateArea.Session | StateArea.Campaign | StateArea.Pc | StateArea.Busy)) != 0) { Render(); } };
            Render();
        }

        private void Render()
        {
            var session = _state.Session;
            if (session == null || session.Party.Count == 0)
            {
                _frames.Clear();
                _list.Clear();
                if (!_state.HasCampaign)
                {
                    _list.Add(Ui.Empty("campaigns", "No campaign at the table."));
                    _list.Add(Ui.Button("CHOOSE ONE", "campaigns", "cv-btn--small", _openCampaigns));
                }
                else if (session == null)
                {
                    bool busy = _state.IsBusy("refresh") || _state.IsBusy("session");
                    _list.Add(Ui.Empty("party", busy ? "Gathering the party…" : "The party gathers when the session opens."));
                    if (!busy) { _list.Add(Ui.Button("OPEN SESSION", "d20", "cv-btn--primary cv-btn--small", delegate { _controller.Run(_controller.StartSession(null)); })); }
                }
                else
                {
                    _list.Add(Ui.Empty("party", "No party on record yet."));
                }
                return;
            }
            var seen = new HashSet<string>();
            int order = 0;
            foreach (var member in session.Party)
            {
                seen.Add(member.Id);
                VisualElement frame;
                if (!_frames.TryGetValue(member.Id, out frame))
                {
                    frame = Build(member);
                    _frames[member.Id] = frame;
                }
                Update(frame, member);
                if (_list.IndexOf(frame) != order) { _list.Insert(order, frame); }
                order++;
            }
            foreach (var id in new List<string>(_frames.Keys))
            {
                if (seen.Contains(id)) { continue; }
                _frames[id].RemoveFromHierarchy();
                _frames.Remove(id);
            }
            for (int i = _list.childCount - 1; i >= order; i--) { _list.RemoveAt(i); }
        }

        private VisualElement Build(DashboardMember member)
        {
            var frame = Ui.El("cv-member");
            frame.Add(Ui.Text(Ui.Monogram(member.Name), "cv-member__crest"));
            var body = Ui.El("cv-member__body");
            body.Add(Ui.Text(member.Name, "cv-member__name"));
            body.Add(Ui.Text(string.Empty, "cv-member__sub"));
            var hp = Ui.El("cv-member__hp");
            hp.Add(Ui.Bar(member.HpFraction));
            hp.Add(Ui.Text(string.Empty, "cv-member__hptext"));
            body.Add(hp);
            body.Add(Ui.El("cv-member__conditions"));
            frame.Add(body);
            string id = member.Id;
            frame.RegisterCallback<ClickEvent>(delegate { VaultSfx.Play(VaultSfx.Cue.Click); _openSheet(id); });
            TooltipLayer.Attach(frame, "Open the character sheet");
            return frame;
        }

        private void Update(VisualElement frame, DashboardMember m)
        {
            frame.EnableInClassList("cv-member--pc", m.IsPc);
            Ui.SetText(frame.Q<Label>(className: "cv-member__name"), m.Name);
            string sub = m.ClassLevel;
            if (!m.IsPc) { sub = (sub.Length > 0 ? sub + " · " : string.Empty) + "ally"; }
            var subLabel = frame.Q<Label>(className: "cv-member__sub");
            Ui.SetText(subLabel, sub);
            subLabel.style.display = sub.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            // Unknown HP (a narrative ruleset, or not bootstrapped yet): no bar at all rather than a bare dash.
            var hp = frame.Q(className: "cv-member__hp");
            hp.style.display = m.MaxHp > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (m.MaxHp > 0)
            {
                Ui.SetBar(frame.Q(className: "cv-bar"), m.HpFraction);
                Ui.SetText(frame.Q<Label>(className: "cv-member__hptext"), m.HpText);
            }
            var conditions = frame.Q(className: "cv-member__conditions");
            conditions.Clear();
            for (int i = 0; i < m.Conditions.Count && i < 3; i++) { conditions.Add(Ui.Chip(m.Conditions[i], "blood")); }
            if (m.Conditions.Count > 3) { conditions.Add(Ui.Chip("+" + (m.Conditions.Count - 3), "blood")); }
        }
    }

    /// <summary>The codex drawer: Quests, Scene, Pack and Journal (session lifecycle, handoff, lore search).</summary>
    public sealed class CodexView
    {
        private static readonly string[] Tabs = { "QUESTS", "SCENE", "PACK", "JOURNAL" };
        private static readonly string[] Icons = { "quests", "scene", "pack", "journal" };

        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private readonly Button[] _tabButtons = new Button[Tabs.Length];
        private readonly VisualElement _body;
        private int _tab;
        private readonly HandoffDraft _draft = new HandoffDraft();
        private string _searchQuery = string.Empty;
        private int _days = 1;

        private readonly Action<string> _openSheet;
        private bool _handoffOpen;

        public CodexView(VisualElement host, VaultAppState state, VaultController controller, Action<string> openSheet)
        {
            _state = state;
            _controller = controller;
            _openSheet = openSheet;
            var tabs = Ui.El("cv-tabs");
            for (int i = 0; i < Tabs.Length; i++)
            {
                int index = i;
                var b = Ui.Button(Tabs[i], Icons[i], null, delegate { Show(index); });
                b.RemoveFromClassList("cv-btn");
                b.AddToClassList("cv-tab");
                _tabButtons[i] = b;
                tabs.Add(b);
            }
            host.Add(tabs);
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("cv-codex__body");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            host.Add(scroll);
            _body = scroll.contentContainer;
            state.Changed += OnChanged;
            Show(0);
        }

        public void Show(int tab)
        {
            _tab = Mathf.Clamp(tab, 0, Tabs.Length - 1);
            for (int i = 0; i < _tabButtons.Length; i++) { _tabButtons[i].EnableInClassList("cv-tab--active", i == _tab); }
            Render();
            if (_tab == 1 && _state.HasCampaign && _state.CompanionIds.Count > 0 && _state.Companions.Count == 0 && !_state.IsBusy("companions"))
            {
                _controller.Run(_controller.LoadCompanions());
            }
        }

        private void OnChanged(StateArea area)
        {
            const StateArea relevant = StateArea.Session | StateArea.Campaign | StateArea.Pc | StateArea.Companions | StateArea.Search | StateArea.Busy;
            if ((area & relevant) == 0) { return; }
            // The journal holds a half-typed handoff: only repaint it for the parts it shows.
            if (_tab == 3 && (area & (StateArea.Session | StateArea.Search | StateArea.Campaign)) == 0) { return; }
            Render();
        }

        private void Render()
        {
            _body.Clear();
            if (!_state.HasCampaign)
            {
                _body.Add(Ui.Empty("campaigns", "Choose a campaign to open its codex."));
                return;
            }
            switch (_tab)
            {
                case 0: RenderQuests(); break;
                case 1: RenderScene(); break;
                case 2: RenderPack(); break;
                default: RenderJournal(); break;
            }
        }

        private bool NeedSession()
        {
            if (_state.Session != null) { return true; }
            _body.Add(Ui.Empty("journal", "Open the session to read the table."));
            _body.Add(Ui.Button("OPEN SESSION", "d20", "cv-btn--primary cv-btn--small", delegate { _controller.Run(_controller.StartSession(null)); }));
            return false;
        }

        /// <summary>A ledger: a seal per quest, the title, and what's left and when, in words.</summary>
        private void RenderQuests()
        {
            if (!NeedSession()) { return; }
            var quests = _state.Session.Quests;
            if (quests.Count == 0) { _body.Add(Ui.Empty("quests", "No open quests. The world is waiting.")); return; }
            foreach (var q in quests)
            {
                var row = Ui.El("cv-ledger-entry" + (q.Overdue ? " cv-ledger-entry--overdue" : string.Empty));
                row.Add(Ui.El("cv-ledger-entry__seal"));
                var col = Ui.El("cv-grow");
                col.Add(Ui.Text(q.Title, "cv-ledger-entry__title"));
                var notes = new List<string>();
                if (q.OpenObjectives > 0) { notes.Add(q.OpenObjectives == 1 ? "one thing left to do" : q.OpenObjectives + " things left to do"); }
                if (q.Overdue) { notes.Add("overdue"); }
                else if (q.Deadline.Length > 0) { notes.Add("due by " + q.Deadline); }
                if (notes.Count > 0) { col.Add(Ui.Text(Capitalize(string.Join(", ", notes.ToArray())) + ".", "cv-ledger-entry__note")); }
                row.Add(col);
                _body.Add(row);
            }
        }

        private static string Capitalize(string text)
        {
            return string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>Where the party stands, in a few lines of prose; allies open their stat blocks.</summary>
        private void RenderScene()
        {
            if (!NeedSession()) { return; }
            var s = _state.Session;
            var pc = _state.PcMember;
            var here = Ui.El("cv-scene");
            if (pc != null && pc.Location.Length > 0) { here.Add(Ui.Text(Ui.PrettyId(pc.Location), "cv-scene__place")); }
            if (s.Time.Length > 0) { here.Add(Ui.Text(VaultClientUI.ShortTime(s.Time), "cv-scene__time")); }
            if (pc != null && pc.Activity.Length > 0) { here.Add(Ui.Text(pc.Name + ": " + Lower(pc.Activity) + ".", "cv-scene__doing")); }
            if (here.childCount == 0) { here.Add(Ui.Text("The Dungeon Master hasn't placed the party yet.", "cv-body cv-muted")); }
            _body.Add(here);

            _body.Add(Heading("Allies"));
            if (_state.CompanionIds.Count == 0) { _body.Add(Ui.Text("No companions travel with you.", "cv-codex__quiet")); }
            else if (_state.IsBusy("companions") && _state.Companions.Count == 0) { _body.Add(Ui.Text("Finding them…", "cv-codex__quiet")); }
            foreach (var entry in _state.Companions)
            {
                string id = entry.Id;
                var row = Ui.El("cv-ally");
                if (entry.Companion == null)
                {
                    row.Add(Ui.Text(Ui.PrettyId(entry.Id) + ": " + entry.Error, "cv-ally__name cv-text-blood"));
                }
                else
                {
                    var c = entry.Companion;
                    row.Add(Ui.Text(Ui.Monogram(c.Name), "cv-ally__crest"));
                    var col = Ui.El("cv-grow");
                    col.Add(Ui.Text(c.Name, "cv-ally__name"));
                    var member = FindMember(id);
                    string sub = member != null && member.ClassLevel.Length > 0 ? member.ClassLevel : c.Kind;
                    if (c.IsMinion) { sub = (sub.Length > 0 ? sub + " · " : string.Empty) + "bound to you"; }
                    if (c.Stance.Length > 0) { sub = (sub.Length > 0 ? sub + " · " : string.Empty) + c.Stance; }
                    if (sub.Length > 0) { col.Add(Ui.Text(sub, "cv-ally__sub")); }
                    if (c.MaxHp > 0) { col.Add(Ui.Bar(c.CurrentHp / c.MaxHp)); }
                    row.Add(col);
                    row.RegisterCallback<ClickEvent>(delegate { VaultSfx.Play(VaultSfx.Cue.Click); _openSheet(id); });
                    TooltipLayer.Attach(row, "Open the stat block");
                }
                var untrack = Ui.IconButton("close", "Stop tracking", "cv-btn--ghost cv-btn--small", delegate { _controller.UntrackCompanion(id); });
                untrack.RegisterCallback<ClickEvent>(delegate (ClickEvent e) { e.StopPropagation(); });
                row.Add(untrack);
                _body.Add(row);
            }
            var track = Ui.El("cv-row");
            track.style.marginTop = 8;
            var idField = Ui.Field("track someone by id (a mount, a hireling…)", null, false, "cv-grow");
            idField.style.marginBottom = 0;
            track.Add(idField);
            var add = Ui.IconButton("add", "Track", "cv-btn--small", delegate { _controller.Run(_controller.TrackCompanion(idField.value)); });
            add.style.marginLeft = 6;
            track.Add(add);
            _body.Add(track);

            if (s.Handoff != null && s.Handoff.Npcs.Count > 0)
            {
                _body.Add(Heading("Known faces"));
                foreach (var npc in s.Handoff.Npcs)
                {
                    var face = Ui.El("cv-face");
                    face.Add(Ui.Text(Ui.PrettyId(npc.Id), "cv-face__name"));
                    if (npc.Stance.Length > 0) { face.Add(Ui.Text(Capitalize(npc.Stance) + ".", "cv-face__stance")); }
                    _body.Add(face);
                }
            }
        }

        private DashboardMember FindMember(string id)
        {
            if (_state.Session == null) { return null; }
            foreach (var m in _state.Session.Party) { if (m.Id == id) { return m; } }
            return null;
        }

        private static string Lower(string text)
        {
            return string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text.Substring(1);
        }

        private static VisualElement Heading(string text)
        {
            return Ui.Text(text.ToUpperInvariant(), "cv-caption cv-codex__heading");
        }

        private void RenderPack()
        {
            if (!NeedSession()) { return; }
            var items = _state.Inventory();
            if (_state.PcMember == null) { _body.Add(Ui.Empty("character", "No player character selected.")); return; }
            if (items.Count == 0) { _body.Add(Ui.Empty("pack", "Nothing carried yet.")); return; }
            bool? group = null;
            foreach (var item in items)
            {
                if (group != item.Equipped)
                {
                    group = item.Equipped;
                    var caption = Ui.Text(item.Equipped ? "EQUIPPED" : "CARRIED", "cv-caption");
                    caption.style.marginTop = 10;
                    _body.Add(caption);
                }
                var row = Ui.El("cv-item");
                row.Add(Ui.Text(item.Name, "cv-item__name"));
                var captured = item;
                row.Add(Ui.Button("USE", null, "cv-btn--small cv-btn--ghost", delegate { _controller.UseItem(captured); }));
                row.Add(Ui.Button(item.Equipped ? "UNEQUIP" : "EQUIP", null, "cv-btn--small cv-btn--ghost", delegate { _controller.ToggleEquip(captured); }));
                _body.Add(row);
            }
            var note = Ui.Text("Use and equip go through the Dungeon Master, so they're committed and narrated.", "cv-body cv-muted");
            note.style.fontSize = 13;
            note.style.marginTop = 10;
            _body.Add(note);
        }

        private void RenderJournal()
        {
            var session = Ui.El("cv-journal__session");
            string status = _state.SessionStatus.Length > 0 ? _state.SessionStatus : "No session opened this run.";
            session.Add(Ui.Text(status, "cv-codex__quiet"));
            bool busy = _state.IsBusy("session") || _state.IsBusy("refresh");
            var open = Ui.Button(_state.Session == null ? "OPEN SESSION" : "REFRESH", _state.Session == null ? "d20" : "refresh", _state.Session == null ? "cv-btn--primary cv-btn--small" : "cv-btn--small cv-btn--ghost", delegate
            {
                _controller.Run(_state.Session == null ? _controller.StartSession(null) : _controller.RefreshTable());
            });
            open.SetEnabled(!busy);
            session.Add(open);
            _body.Add(session);

            var digest = _state.Session;
            var handoff = digest != null ? digest.Handoff : null;
            if (handoff != null && handoff.StorySoFar.Length > 0)
            {
                _body.Add(Heading("The story so far"));
                _body.Add(Ui.Rich(handoff.StorySoFar, "cv-journal__story"));
            }
            if (handoff != null && handoff.LastSession.Length > 0)
            {
                _body.Add(Heading(handoff.Checkpoint ? "This session, so far" : "Last session"));
                _body.Add(Ui.Rich(handoff.LastSession, "cv-journal__entry"));
            }
            if (handoff != null && handoff.OpenThreads.Count > 0)
            {
                _body.Add(Heading("Loose threads"));
                foreach (string thread in handoff.OpenThreads)
                {
                    var row = Ui.El("cv-thread");
                    row.Add(Ui.El("cv-thread__mark"));
                    row.Add(Ui.Text(thread, "cv-thread__text"));
                    _body.Add(row);
                }
            }
            if (handoff != null && handoff.PartyIntent.Length > 0)
            {
                _body.Add(Heading("What you meant to do"));
                _body.Add(Ui.Text(handoff.PartyIntent, "cv-journal__entry"));
            }
            if (handoff == null && digest != null && digest.RecentDigest.Length > 0)
            {
                _body.Add(Heading("Lately"));
                _body.Add(Ui.Rich(digest.RecentDigest, "cv-journal__entry"));
            }

            var downtime = Ui.Card("Downtime");
            var dRow = Ui.El("cv-row");
            var days = Ui.Field("days", _days.ToString(CultureInfo.InvariantCulture));
            days.style.width = 80;
            days.style.marginBottom = 0;
            days.RegisterValueChangedCallback(delegate (ChangeEvent<string> e) { int d; if (int.TryParse(e.newValue, out d)) { _days = Mathf.Clamp(d, 1, 365); } });
            dRow.Add(days);
            var advance = Ui.Button("LET TIME PASS", "time", "cv-btn--small", delegate { _controller.AdvanceDays(_days); });
            advance.style.marginLeft = 8;
            TooltipLayer.Attach(advance, "Days pass, resources recover, rumors fade and deadlines approach. The Dungeon Master narrates what changed.");
            dRow.Add(advance);
            downtime.Add(dRow);
            _body.Add(downtime);

            var handoffCard = Ui.Card("End of session");
            handoffCard.Add(Ui.Text("The handoff is what the next session starts from, instead of the raw event log. Checkpoint before a long pause.", "cv-body cv-muted"));
            _body.Add(handoffCard);
            if (!_handoffOpen && _state.HandoffIssues.Count == 0)
            {
                var write = Ui.Button("WRITE THE HANDOFF", "journal", "cv-btn--small", delegate { _handoffOpen = true; Render(); });
                write.style.marginTop = 8;
                handoffCard.Add(write);
                RenderLore();
                return;
            }
            Counted(handoffCard, "What happened this session", "required", _draft.LastSession, 600, true, delegate (string v) { _draft.LastSession = v; });
            Counted(handoffCard, "The story so far", "the rolling summary (leave blank to keep the last one)", _draft.StorySoFar, 800, true, delegate (string v) { _draft.StorySoFar = v; });
            Counted(handoffCard, "Open threads", "one per line, up to 6", _draft.Threads, 0, true, delegate (string v) { _draft.Threads = v; });
            Counted(handoffCard, "People in play", "one per line: id | stance", _draft.Npcs, 0, true, delegate (string v) { _draft.Npcs = v; });
            Counted(handoffCard, "Party intent", "what they mean to do next", _draft.Intent, 200, false, delegate (string v) { _draft.Intent = v; });
            Counted(handoffCard, "Tone", "how it should feel", _draft.Tone, 120, false, delegate (string v) { _draft.Tone = v; });
            foreach (string issue in _state.HandoffIssues) { handoffCard.Add(Ui.Text(issue, "cv-body cv-text-blood")); }
            var hRow = Ui.El("cv-row");
            hRow.style.marginTop = 8;
            bool working = _state.IsBusy("handoff");
            var checkpoint = Ui.Button("CHECKPOINT", "seal", "cv-btn--small", delegate { _controller.Run(_controller.EndSession(_draft, true)); });
            checkpoint.SetEnabled(!working);
            checkpoint.style.marginRight = 8;
            hRow.Add(checkpoint);
            var end = Ui.Button("END SESSION", "exit", "cv-btn--small cv-btn--primary", delegate { _controller.Run(_controller.EndSession(_draft, false)); });
            end.SetEnabled(!working);
            hRow.Add(end);
            handoffCard.Add(hRow);
            RenderLore();
        }

        private void RenderLore()
        {
            var lore = Ui.Card("Search the world");
            var lRow = Ui.El("cv-row");
            var query = Ui.Field("a name, a place, a rumor…", _searchQuery, false, "cv-grow");
            query.style.marginBottom = 0;
            query.RegisterValueChangedCallback(delegate (ChangeEvent<string> e) { _searchQuery = e.newValue; });
            lRow.Add(query);
            var go = Ui.IconButton("search", "Search", "cv-btn--small", delegate { _controller.Run(_controller.SearchWorld(_searchQuery)); });
            go.style.marginLeft = 6;
            lRow.Add(go);
            lore.Add(lRow);
            if (_state.SearchError.Length > 0) { lore.Add(Ui.Text(_state.SearchError, "cv-body cv-text-blood")); }
            else if (_state.SearchResults.Count == 0 && _state.SearchSummary.Length > 0) { lore.Add(Ui.Text(_state.SearchSummary, "cv-body cv-muted")); }
            foreach (var hit in _state.SearchResults)
            {
                var h = Ui.El("cv-quest");
                h.Add(Ui.Text(hit.Title, "cv-quest__title"));
                if (hit.Id.Length > 0 && hit.Id != hit.Title) { h.Add(Ui.Text(hit.Id, "cv-mono cv-muted")); }
                if (hit.Detail.Length > 0) { h.Add(Ui.Rich(hit.Detail.Length > 600 ? hit.Detail.Substring(0, 600) + "…" : hit.Detail, "cv-body")); }
                lore.Add(h);
            }
            _body.Add(lore);
        }

        /// <summary>A field with a live character counter (limit 0 = no counter).</summary>
        private static void Counted(VisualElement parent, string caption, string placeholder, string value, int limit, bool multiline, Action<string> onChange)
        {
            var field = Ui.LabeledField(parent, caption, placeholder, value, multiline);
            if (multiline) { field.style.minHeight = 64; }
            if (limit <= 0)
            {
                field.RegisterValueChangedCallback(delegate (ChangeEvent<string> e) { onChange(e.newValue); });
                return;
            }
            var counter = Ui.Text(string.Empty, "cv-counter");
            parent.Add(counter);
            Action<string> paint = delegate (string v)
            {
                int n = (v ?? string.Empty).Length;
                Ui.SetText(counter, n + " / " + limit);
                counter.EnableInClassList("cv-counter--over", n > limit);
                field.EnableInClassList("cv-field--invalid", n > limit);
            };
            paint(value);
            field.RegisterValueChangedCallback(delegate (ChangeEvent<string> e) { onChange(e.newValue); paint(e.newValue); });
        }
    }
}
