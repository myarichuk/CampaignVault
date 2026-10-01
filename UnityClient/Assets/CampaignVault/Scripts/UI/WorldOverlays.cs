using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>The campaign book: every campaign on the server as a card. Play, or delete (two taps).</summary>
    public sealed class CampaignsOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private readonly Action _openOnboarding;
        private VisualElement _grid;
        private string _armed = string.Empty;

        public CampaignsOverlay(VaultAppState state, VaultController controller, Action openOnboarding)
        {
            _state = state;
            _controller = controller;
            _openOnboarding = openOnboarding;
        }

        protected override string Title { get { return "Your campaigns"; } }
        protected override string TitleIcon { get { return "campaigns"; } }

        protected override void BuildContent()
        {
            var actions = Ui.El("cv-row");
            actions.style.marginBottom = 18;
            var create = Ui.Button("NEW CAMPAIGN", "add", "cv-btn--primary", delegate { Close(); _openOnboarding(); });
            create.style.marginRight = 8;
            actions.Add(create);
            actions.Add(Ui.Button("REFRESH", "refresh", "cv-btn--ghost", delegate { _controller.Run(_controller.ListCampaigns()); }));
            Body.Add(actions);
            _grid = Ui.El("cv-campaigns");
            Body.Add(_grid);
        }

        public override void OnOpen()
        {
            _armed = string.Empty;
            _state.Changed += OnChanged;
            Render();
            _controller.Run(_controller.ListCampaigns());
        }

        public override void OnClose() { _state.Changed -= OnChanged; }

        private void OnChanged(StateArea area)
        {
            if ((area & (StateArea.Campaigns | StateArea.Campaign | StateArea.Busy)) != 0) { Render(); }
        }

        private void Render()
        {
            _grid.Clear();
            if (!_state.CampaignsLoaded || (_state.IsBusy("campaigns") && _state.Campaigns.Count == 0))
            {
                _grid.Add(Ui.Empty("campaigns", "Opening the campaign book…"));
                return;
            }
            if (_state.CampaignsError.Length > 0)
            {
                _grid.Add(Ui.Empty("warning", "Could not list campaigns: " + _state.CampaignsError));
                return;
            }
            if (_state.Campaigns.Count == 0)
            {
                _grid.Add(Ui.Empty("quests", "No campaigns yet. Create one to begin."));
                return;
            }
            foreach (var row in _state.Campaigns)
            {
                _grid.Add(Card(row));
            }
        }

        private VisualElement Card(CampaignRow row)
        {
            bool active = row.Slug == _state.CampaignSlug;
            var card = Ui.El("cv-campaign" + (active ? " cv-campaign--active" : string.Empty));
            card.Add(Ui.Text(row.Display, "cv-campaign__name"));
            card.Add(Ui.Text(row.Slug, "cv-campaign__slug"));
            var chips = Ui.El("cv-row");
            if (!string.IsNullOrEmpty(row.System)) { chips.Add(Ui.Chip(SystemLabel(row.System), "arcane", "d20")); }
            if (active) { chips.Add(Ui.Chip("At the table", "gold", "check")); }
            card.Add(chips);

            var actions = Ui.El("cv-campaign__actions");
            if (!active)
            {
                actions.Add(Ui.Button("PLAY", "d20", "cv-btn--primary cv-btn--small", delegate
                {
                    _controller.SelectCampaign(row.Slug, row.System);
                    Close();
                    _controller.Run(_controller.RefreshTable());
                }));
            }
            else
            {
                actions.Add(Ui.Button("RETURN", "chevron", "cv-btn--small", Close));
            }
            bool armed = _armed == row.Slug;
            bool deleting = _state.IsBusy("delete:" + row.Slug);
            var delete = Ui.Button(deleting ? "DELETING…" : armed ? "CONFIRM DELETE" : "DELETE", "trash",
                "cv-btn--small cv-btn--danger" + (armed ? " cv-btn--armed" : string.Empty), delegate
                {
                    if (_armed != row.Slug)
                    {
                        _armed = row.Slug;
                        Render();
                        // Disarm if they walk away.
                        _grid.schedule.Execute(() => { if (_armed == row.Slug) { _armed = string.Empty; Render(); } }).StartingIn(4000);
                        return;
                    }
                    _armed = string.Empty;
                    _controller.Run(_controller.DeleteCampaign(row.Slug));
                });
            delete.SetEnabled(!deleting);
            TooltipLayer.Attach(delete, "Deletes the campaign and everything in it, for good. Tap twice to confirm.");
            actions.Add(delete);
            card.Add(actions);
            return card;
        }

        internal static string SystemLabel(string system)
        {
            for (int i = 0; i < OnboardingState.SystemOptions.Length; i++)
            {
                if (string.Equals(OnboardingState.SystemOptions[i], system, StringComparison.OrdinalIgnoreCase)) { return OnboardingState.SystemLabels[i]; }
            }
            if (string.Equals(system, "dnd5e", StringComparison.OrdinalIgnoreCase)) { return "D&D 5e"; }
            if (string.Equals(system, "pf2e", StringComparison.OrdinalIgnoreCase)) { return "Pathfinder 2e"; }
            return system;
        }
    }

    /// <summary>Guided campaign creation: name and rules up front, then the server's questions one at a time.</summary>
    public sealed class OnboardingOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private VisualElement _content;
        private string _system = OnboardingState.SystemOptions[0];
        /// <summary>Brainstorm messages of yours the player unfolded (by index), kept across repaints.</summary>
        private readonly HashSet<int> _expanded = new HashSet<int>();

        // A pasted page of lore folds to this much in the chat history.
        private const int FoldChars = 600;
        private const int FoldLines = 8;

        public OnboardingOverlay(VaultAppState state, VaultController controller)
        {
            _state = state;
            _controller = controller;
        }

        protected override string Title { get { return "A new campaign"; } }
        protected override string TitleIcon { get { return "seal"; } }
        protected override bool Narrow { get { return true; } }

        protected override void BuildContent()
        {
            _content = Ui.El();
            Body.Add(_content);
        }

        public override void OnOpen()
        {
            var phase = _state.Onboarding.Phase;
            if (phase == OnboardingPhase.Done || phase == OnboardingPhase.Failed) { _controller.ResetOnboarding(); }
            _state.Changed += OnChanged;
            Render();
        }

        public override void OnClose() { _state.Changed -= OnChanged; }

        private void OnChanged(StateArea area) { if ((area & StateArea.Onboarding) != 0) { Render(); } }

        private void Render()
        {
            _content.Clear();
            Dock.Clear();
            Foot.Clear();
            var ob = _state.Onboarding;
            switch (ob.Phase)
            {
                case OnboardingPhase.Idle: RenderStart(ob); break;
                case OnboardingPhase.Working: _content.Add(Ui.Empty("seal", string.IsNullOrEmpty(ob.Status) ? "Working…" : ob.Status)); break;
                case OnboardingPhase.Question:
                    if (ob.Brainstorming) { RenderBrainstorm(ob); } else { RenderQuestion(ob); }
                    break;
                case OnboardingPhase.ReadyToFinalize: RenderFinalize(ob); break;
                case OnboardingPhase.Done: RenderDone(ob); break;
                default:
                    _content.Add(Ui.Empty("warning", ob.Error));
                    Foot.Add(Ui.Button("START OVER", "refresh", null, delegate { _controller.ResetOnboarding(); }));
                    break;
            }
        }

        private void RenderStart(OnboardingState ob)
        {
            _content.Add(Ui.Text("Name your campaign and choose its rules. The rules lock in when you finish; nothing is written to the world until then.", "cv-body"));
            var name = Ui.LabeledField(_content, "Campaign name", "e.g. The Sunken Crown");
            var display = Ui.LabeledField(_content, "Display name (optional)", "shown on the campaign card");
            _content.Add(Ui.Text("RULES", "cv-caption"));
            var systems = Ui.El("cv-row");
            systems.style.marginBottom = 16;
            var buttons = new List<Button>();
            for (int i = 0; i < OnboardingState.SystemOptions.Length; i++)
            {
                string option = OnboardingState.SystemOptions[i];
                var b = Ui.Button(OnboardingState.SystemLabels[i].ToUpperInvariant(), "d20", option == _system ? "cv-btn--selected" : null, null);
                b.clicked += delegate
                {
                    _system = option;
                    for (int j = 0; j < buttons.Count; j++) { buttons[j].EnableInClassList("cv-btn--selected", OnboardingState.SystemOptions[j] == option); }
                };
                b.style.marginRight = 8;
                buttons.Add(b);
                systems.Add(b);
            }
            _content.Add(systems);
            var slugPreview = Ui.Text(string.Empty, "cv-mono cv-muted");
            _content.Add(slugPreview);
            name.RegisterValueChangedCallback(delegate (ChangeEvent<string> e)
            {
                string slug = VaultController.Slugify(e.newValue);
                Ui.SetText(slugPreview, slug.Length > 0 ? "campaign id: " + slug : string.Empty);
            });
            if (ob.Error.Length > 0) { _content.Add(Ui.Text(ob.Error, "cv-body cv-text-blood")); }

            var resume = Ui.Card("Resume one you started");
            resume.style.marginTop = 18;
            var resumeRow = Ui.El("cv-row");
            var slugField = Ui.Field("campaign id", null, false, "cv-grow");
            slugField.style.marginBottom = 0;
            resumeRow.Add(slugField);
            var resumeBtn = Ui.Button("RESUME", "chevron", null, delegate { _controller.Run(_controller.ResumeOnboarding(slugField.value)); });
            resumeBtn.style.marginLeft = 8;
            resumeRow.Add(resumeBtn);
            resume.Add(resumeRow);
            _content.Add(resume);

            Foot.Add(Ui.Button("BEGIN", "seal", "cv-btn--primary", delegate
            {
                _controller.Run(_controller.BeginOnboarding(name.value, display.value, _system));
            }));
        }

        private void RenderQuestion(OnboardingState ob)
        {
            var q = ob.Question;
            _content.Add(Ui.Text(ob.Slug.ToUpperInvariant() + " · " + ob.Answered + " ANSWERED", "cv-caption"));
            if (ob.Progress.Length > 0) { _content.Add(Ui.Text(ob.Progress, "cv-body cv-muted")); }
            _content.Add(Ui.Text(q.Text, "cv-question"));
            if (q.Help.Length > 0) { _content.Add(Ui.Text(q.Help, "cv-body cv-muted cv-italic")); }
            var answer = Ui.El();
            answer.style.marginTop = 16;
            _content.Add(answer);

            switch (q.Type)
            {
                case AnswerType.Choice:
                    foreach (string option in q.Options)
                    {
                        string captured = option;
                        var b = Ui.Button(ChoiceLabel(option), null, null, delegate { Submit(captured); });
                        b.style.marginBottom = 8;
                        answer.Add(b);
                    }
                    break;
                case AnswerType.YesNo:
                    var row = Ui.El("cv-row");
                    var yes = Ui.Button("YES", "check", "cv-btn--primary", delegate { Submit("yes"); });
                    yes.style.marginRight = 8;
                    row.Add(yes);
                    row.Add(Ui.Button("NO", "close", null, delegate { Submit("no"); }));
                    answer.Add(row);
                    break;
                default:
                    bool list = q.Type == AnswerType.List;
                    bool number = q.Type == AnswerType.Number;
                    // A brainstormed write-up can run to a paragraph: give it room.
                    bool multiline = list || ob.Draft.Length > 80;
                    var field = Ui.Field(list ? "one entry per line" : number ? "a number" : "your answer", ob.Draft, multiline);
                    answer.Add(field);
                    // Long answers are kept whole; past the limit the field says why trimming helps instead of cutting it.
                    var answerCount = Ui.Text(string.Empty, "cv-counter");
                    var answerNote = Ui.Text("Over " + Count(OnboardingBrainstorm.MaxAnswerChars) + " characters. It's kept whole, but every later step re-reads your answers, so trimming it keeps setup quicker and cheaper.", "cv-body cv-muted cv-italic");
                    answer.Add(answerCount);
                    answer.Add(answerNote);
                    Action<string> paintAnswer = delegate (string v)
                    {
                        int n = v.Length;
                        bool over = n > OnboardingBrainstorm.MaxAnswerChars;
                        Ui.SetText(answerCount, Count(n) + " / " + Count(OnboardingBrainstorm.MaxAnswerChars));
                        answerCount.style.display = n > OnboardingBrainstorm.MaxAnswerChars / 2 ? DisplayStyle.Flex : DisplayStyle.None;
                        answerCount.EnableInClassList("cv-counter--over", over);
                        answerNote.style.display = over ? DisplayStyle.Flex : DisplayStyle.None;
                    };
                    paintAnswer(ob.Draft);
                    // Kept in state, so opening the brainstorm and coming back doesn't lose what was typed.
                    field.RegisterValueChangedCallback(delegate (ChangeEvent<string> e) { ob.Draft = e.newValue; paintAnswer(e.newValue); });
                    field.schedule.Execute(() => { field.Focus(); }).StartingIn(50);
                    if (OnboardingBrainstorm.Supports(q))
                    {
                        bool talked = OnboardingBrainstorm.HasTalk(ob.BrainstormChat);
                        var helpers = Ui.El("cv-row");
                        helpers.style.marginTop = 8;
                        helpers.style.flexWrap = Wrap.Wrap;
                        var brainstorm = Ui.Button(talked ? "CONTINUE THE CONVERSATION" : "BRAINSTORM WITH THE DM", "spark", "cv-btn--small",
                            delegate { _controller.OpenBrainstorm(); });
                        brainstorm.SetEnabled(!ob.BrainstormBusy);
                        helpers.Add(brainstorm);
                        TooltipLayer.Attach(brainstorm, "One conversation with the DM for the whole setup. When you're happy, it writes the answer into this field for you to edit.");
                        // What was already talked through (the plot, on the world question) answers this one without starting over.
                        if (talked)
                        {
                            bool replaces = ob.Draft.Trim().Length > 0;
                            var writeUp = Ui.Button(replaces ? "REWRITE FROM OUR CONVERSATION" : "WRITE IT UP FROM OUR CONVERSATION", "check", "cv-btn--small",
                                delegate { _controller.Run(_controller.WriteUpBrainstorm()); });
                            writeUp.style.marginLeft = 8;
                            writeUp.SetEnabled(!ob.BrainstormBusy);
                            helpers.Add(writeUp);
                            TooltipLayer.Attach(writeUp, replaces
                                ? "The DM drafts this answer from what you've discussed so far. It replaces what's in the field now."
                                : "The DM drafts this answer from what you've discussed so far, for you to edit.");
                        }
                        answer.Add(helpers);
                        if (ob.BrainstormBusy) { answer.Add(Ui.Text("The DM is writing it up…", "cv-body cv-muted cv-italic")); }
                        if (ob.BrainstormError.Length > 0) { answer.Add(Ui.Text(ob.BrainstormError, "cv-body cv-text-blood")); }
                    }
                    Foot.Add(Ui.Button("ANSWER", "chevron", "cv-btn--primary", delegate
                    {
                        Submit(list ? VaultController.FormatListAnswer(field.value) : field.value.Trim());
                    }));
                    break;
            }

            // Party questions: paste "Name — detail" lines once and use them as the answer.
            if (!AboutTheParty(q)) { return; }
            var roster = Ui.Card("Roster helper");
            roster.style.marginTop = 20;
            roster.Add(Ui.Text("Paste the party, one “Name — detail” per line, to answer a party question in one go.", "cv-body cv-muted"));
            var rosterField = Ui.Field("Lyra — elf ranger\nShade — familiar", null, true);
            roster.Add(rosterField);
            var preview = Ui.Text(string.Empty, "cv-body cv-muted");
            roster.Add(preview);
            var rosterRow = Ui.El("cv-row");
            var parse = Ui.Button("PREVIEW", "look", "cv-btn--small", delegate
            {
                var entries = RosterParser.Parse(rosterField.value);
                Ui.SetText(preview, entries.Count == 0 ? "Nothing parsed." : RosterParser.FormatPartyLine(entries));
            });
            parse.style.marginRight = 8;
            rosterRow.Add(parse);
            rosterRow.Add(Ui.Button("USE AS ANSWER", "check", "cv-btn--small", delegate { _controller.Run(_controller.SubmitRosterAnswer(rosterField.value)); }));
            roster.Add(rosterRow);
            _content.Add(roster);
        }

        /// <summary>A side chat with the model about the current question; its write-up becomes the draft answer.</summary>
        private void RenderBrainstorm(OnboardingState ob)
        {
            var q = ob.Question;
            bool talked = OnboardingBrainstorm.HasTalk(ob.BrainstormChat);
            _content.Add(Ui.Text("TALKING IT THROUGH WITH THE DM", "cv-caption"));
            _content.Add(Ui.Text(q.Text, "cv-question"));
            if (!talked)
            {
                _content.Add(Ui.Text("Say what you have in mind, even half an idea, or ask for suggestions. This conversation carries on through the whole setup, so nothing you settle here is lost when the next question comes. Nothing is saved until you answer.", "cv-body cv-muted cv-italic"));
            }
            if (ob.BrainstormChat.Count == 0) { _expanded.Clear(); }
            var dropped = OnboardingBrainstorm.Dropped(ob.BrainstormChat, OnboardingBrainstorm.MaxConversationChars);
            if (dropped.Count > 0)
            {
                var trimmed = Ui.Text("This conversation is over its " + Count(OnboardingBrainstorm.MaxConversationChars) + "-character limit, so the DM no longer sees "
                    + (dropped.Count == 1 ? "1 earlier message" : dropped.Count + " earlier messages") + " (marked below). Your first message and the latest ones are still sent. "
                    + "WRITE IT UP now to keep what you settled, or restate what matters in your next message.", "cv-body cv-text-blood");
                trimmed.style.marginTop = 12;
                _content.Add(trimmed);
            }
            for (int i = 0; i < ob.BrainstormChat.Count; i++)
            {
                var message = ob.BrainstormChat[i];
                bool gone = dropped.Contains(i);
                if (message.Key == OnboardingBrainstorm.MarkerRole)
                {
                    // Where the setup moved on: a divider, so the chat reads as one conversation across the questions.
                    string where = BuilderAdvisor.IsMarker(message.Value) ? message.Value : "NEXT QUESTION · " + message.Value;
                    var divider = Ui.Text(where.ToUpperInvariant(), "cv-caption cv-text-gold");
                    divider.style.marginTop = 20;
                    divider.style.whiteSpace = WhiteSpace.Normal;
                    if (gone) { divider.style.opacity = 0.45f; }
                    _content.Add(divider);
                    continue;
                }
                bool mine = message.Key == "user";
                var bubble = Ui.El();
                bubble.style.marginTop = 12;
                if (gone) { bubble.style.opacity = 0.45f; }
                bubble.Add(Ui.Text((mine ? "YOU" : "THE DM") + (gone ? " · NO LONGER SENT TO THE DM" : string.Empty), gone ? "cv-caption cv-text-blood" : "cv-caption"));
                string folded = mine && !_expanded.Contains(i) ? Fold(message.Value, FoldChars, FoldLines) : null;
                Ui.RichBlock(bubble, folded ?? message.Value, mine ? "cv-body cv-muted" : "cv-body");
                if (mine && (folded != null || _expanded.Contains(i)))
                {
                    int index = i;
                    var toggle = Ui.Button(folded != null ? "SHOW ALL" : "SHOW LESS", null, "cv-btn--ghost cv-btn--small", delegate
                    {
                        if (!_expanded.Remove(index)) { _expanded.Add(index); }
                        Render();
                    });
                    toggle.style.alignSelf = Align.FlexStart;
                    bubble.Add(toggle);
                }
                _content.Add(bubble);
            }
            if (ob.BrainstormBusy)
            {
                var thinking = Ui.Text("The DM is thinking…", "cv-body cv-muted cv-italic");
                thinking.style.marginTop = 12;
                _content.Add(thinking);
            }
            if (ob.BrainstormError.Length > 0) { _content.Add(Ui.Text(ob.BrainstormError, "cv-body cv-text-blood")); }

            // Docked under the chat, not in it: a long draft scrolls inside the box and the conversation stays in view.
            var field = Ui.Field(!talked ? "e.g. something with sea caves and smugglers" : "reply", ob.BrainstormDraft, true);
            field.style.marginTop = 12;
            field.style.marginBottom = 0;
            field.SetEnabled(!ob.BrainstormBusy);
            Dock.Add(field);
            var meter = Ui.El("cv-row");
            meter.style.marginTop = 4;
            int used = OnboardingBrainstorm.ConversationChars(ob.BrainstormChat);
            var conversation = Ui.Text("CONVERSATION " + Count(used) + " / " + Count(OnboardingBrainstorm.MaxConversationChars), "cv-counter cv-grow");
            conversation.style.unityTextAlign = TextAnchor.UpperLeft;
            conversation.style.marginTop = 0;
            conversation.EnableInClassList("cv-counter--over", dropped.Count > 0);
            TooltipLayer.Attach(conversation, "Every reply re-sends the whole conversation. Past the limit the middle messages stop being sent, and they're marked.");
            // Only worth the space once the chat is a real share of the budget.
            conversation.style.visibility = used > OnboardingBrainstorm.MaxConversationChars / 2 ? Visibility.Visible : Visibility.Hidden;
            meter.Add(conversation);
            var counter = Ui.Text(string.Empty, "cv-counter");
            counter.style.marginTop = 0;
            meter.Add(counter);
            Dock.Add(meter);
            field.schedule.Execute(() => { field.Focus(); }).StartingIn(50);
            // Keep the newest message in view.
            _content.schedule.Execute(() => { var scroll = _content.GetFirstAncestorOfType<ScrollView>(); if (scroll != null) { scroll.scrollOffset = new Vector2(0, float.MaxValue); } }).StartingIn(30);

            Foot.Add(Ui.Button("BACK TO THE QUESTION", "chevron", "cv-btn--ghost", delegate { _controller.CloseBrainstorm(); }));
            var writeUp = Ui.Button("WRITE IT UP", "check", null, delegate { _controller.Run(_controller.WriteUpBrainstorm()); });
            writeUp.SetEnabled(!ob.BrainstormBusy && talked);
            TooltipLayer.Attach(writeUp, "The DM turns what you settled on into the answer, ready for you to edit and submit.");
            Foot.Add(writeUp);
            var send = Ui.Button("SEND", "chevron", "cv-btn--primary", delegate { _controller.Run(_controller.SendBrainstorm(field.value)); });
            Foot.Add(send);
            Action<string> paint = delegate (string v)
            {
                int n = v.Length;
                bool over = n > OnboardingBrainstorm.MaxMessageChars;
                Ui.SetText(counter, over ? "TOO LONG TO SEND · " + Count(n) + " / " + Count(OnboardingBrainstorm.MaxMessageChars)
                    : Count(n) + " / " + Count(OnboardingBrainstorm.MaxMessageChars));
                counter.EnableInClassList("cv-counter--over", over);
                field.EnableInClassList("cv-field--invalid", over);
                send.SetEnabled(!ob.BrainstormBusy && !over);
            };
            paint(ob.BrainstormDraft);
            field.RegisterValueChangedCallback(delegate (ChangeEvent<string> e) { ob.BrainstormDraft = e.newValue; paint(e.newValue); });
        }

        private static string Count(int n) { return n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture); }

        /// <summary>The first lines of a long message, cut at a word; null when it is short enough to show whole.</summary>
        internal static string Fold(string text, int maxChars, int maxLines)
        {
            if (string.IsNullOrEmpty(text)) { return null; }
            int cut = text.Length;
            int lines = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' && ++lines > maxLines) { cut = i; break; }
            }
            if (cut > maxChars) { cut = maxChars; }
            if (cut >= text.Length) { return null; }
            int space = text.LastIndexOfAny(new[] { ' ', '\n' }, cut - 1, cut);
            if (space > maxChars / 2) { cut = space; }
            return text.Substring(0, cut).TrimEnd() + " …";
        }

        /// <summary>Server option ids read as plain words on the buttons.</summary>
        internal static string ChoiceLabel(string option)
        {
            switch (option)
            {
                case "describe-now": return "I'll describe them";
                case "dm-pregenerates": return "The DM makes them, I approve";
                case "build-at-table": return "Build them with the DM, step by step";
                case "user-provided": return "I have a plot idea";
                case "generated-surprise": return "Surprise me";
                case "generated-with-direction": return "Generate it, with my direction";
                case "party-existing": return "Party, existing world";
                case "party-homebrew": return "Party, homebrew world";
                default: return option;
            }
        }

        /// <summary>Only party questions get the roster helper.</summary>
        private static bool AboutTheParty(OnboardingQuestion q)
        {
            if (q.Type != AnswerType.Text && q.Type != AnswerType.List) { return false; }
            string text = (q.Key + " " + q.Text).ToLowerInvariant();
            foreach (string word in new[] { "party", "roster", "character", "companion", "hero", "player" })
            {
                if (text.Contains(word)) { return true; }
            }
            return false;
        }

        private void Submit(string answer)
        {
            _controller.Run(_controller.SubmitOnboardingAnswer(answer));
        }

        private void RenderFinalize(OnboardingState ob)
        {
            _content.Add(Ui.Empty("seal", "Every question is answered. Finalizing locks the campaign's rules, tone and setting. It doesn't write the world yet."));
            if (ob.Error.Length > 0) { _content.Add(Ui.Text(ob.Error, "cv-body cv-text-blood")); }
            Foot.Add(Ui.Button("FINALIZE", "seal", "cv-btn--primary", delegate { _controller.Run(_controller.FinalizeOnboarding()); }));
        }

        private void RenderDone(OnboardingState ob)
        {
            _content.Add(Ui.Text("THE CAMPAIGN IS READY", "cv-caption"));
            _content.Add(Ui.Rich(ob.DoneSummary, "cv-body"));
            foreach (string step in ob.NextSteps) { _content.Add(Ui.Rich("- " + step, "cv-body cv-muted")); }
            Foot.Add(Ui.Button("TO THE TABLE", null, "cv-btn--ghost", Close));
            Foot.Add(Ui.Button("SEED THE WORLD", "spark", "cv-btn--primary", delegate
            {
                if (_controller.SeedWorldThroughDm()) { Close(); }
            }));
            TooltipLayer.Attach(Foot[Foot.childCount - 1], "The Dungeon Master creates your characters and the starter places, people and quests from your answers. The first session opens as soon as the party exists.");
        }
    }
}
