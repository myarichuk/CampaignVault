using System;
using System.Collections;
using System.Collections.Generic;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.App
{
    /// <summary>
    /// The character builder: walks the campaign system's recipe through character_builder (steps → options →
    /// preview → commit) on the build connector. The server owns the rules; the client only collects choices and
    /// shows what the server derived and objected to.
    /// </summary>
    public sealed partial class VaultController
    {
        public const string BuilderTool = "character_builder";

        /// <summary>Levels above this need the level-up choices step (phase 8); the stepper stops here until then.</summary>
        public const int MaxBuilderLevel = 3;

        private readonly Random _dice = new Random();

        /// <summary>
        /// Opens the builder for the campaign at the table. The draft is kept while the app runs: reopening resumes
        /// it, and a committed character stays loaded so changes update it.
        /// </summary>
        public IEnumerator BeginBuilder(string kind, bool forOnboarding = false, string editId = null)
        {
            var b = _s.Builder;
            var ob = _s.Onboarding;
            kind = string.IsNullOrEmpty(kind) ? "pc" : kind;
            string slug = forOnboarding ? ob.Slug : _s.CampaignSlug;
            if (slug.Length == 0)
            {
                b.Error = forOnboarding ? "Start the campaign first." : "Pick a campaign first.";
                _s.Notify(StateArea.Builder);
                yield break;
            }
            string system = forOnboarding ? RulesetOf(ob.System) : _s.Ruleset;
            if (forOnboarding && !string.IsNullOrEmpty(editId))
            {
                var member = ob.Party.Find(delegate (PartyMember m) { return m.Id == editId; });
                if (member != null)
                {
                    // The draft that built the character, so the builder shows its choices and saves over it.
                    b.Reset(slug, system, member.Kind);
                    b.ForOnboarding = true;
                    b.Draft = CharacterDraft.FromJson(member.Draft.ToJson());
                    b.Draft.Id = member.Id;
                    b.CommittedId = member.Id;
                    yield return LoadDraftSteps();
                    yield break;
                }
            }
            bool resume = b.ForOnboarding == forOnboarding && b.Slug == slug && b.Draft.Kind == kind && b.Steps.Count > 0
                && !(forOnboarding && b.CommittedId.Length > 0);
            if (resume) { yield break; }
            yield return StartDraft(kind, slug, system, forOnboarding);
        }

        /// <summary>Starts over with an empty draft (BUILD ANOTHER), for the same campaign as the last one.</summary>
        public IEnumerator NewBuilderDraft(string kind)
        {
            var b = _s.Builder;
            bool forOnboarding = b.ForOnboarding;
            yield return StartDraft(kind, forOnboarding ? _s.Onboarding.Slug : _s.CampaignSlug,
                forOnboarding ? RulesetOf(_s.Onboarding.System) : _s.Ruleset, forOnboarding);
        }

        private IEnumerator StartDraft(string kind, string slug, string system, bool forOnboarding)
        {
            var b = _s.Builder;
            b.Reset(slug, system, string.IsNullOrEmpty(kind) ? "pc" : kind);
            b.ForOnboarding = forOnboarding;
            if (forOnboarding) { b.Draft.Level = _s.Onboarding.PartyLevel; }
            yield return LoadDraftSteps();
        }

        private IEnumerator LoadDraftSteps()
        {
            var b = _s.Builder;
            _s.Notify(StateArea.Builder);
            yield return BuilderSteps();
            if (b.Steps.Count == 0) { yield break; }
            b.Current = b.Steps[0].Key;
            _s.Notify(StateArea.Builder);
            yield return BuilderOptions(b.Current);
            yield return BuilderPreview();
        }

        /// <summary>The onboarding "system" answer as the ruleset id the server reports.</summary>
        internal static string RulesetOf(string onboardingSystem)
        {
            switch (onboardingSystem)
            {
                case "Pathfinder2e": return "pf2e";
                case "Narrative": return "narrative";
                default: return "dnd5e";
            }
        }

        public void BuilderGoTo(string key)
        {
            var b = _s.Builder;
            if (b.Step(key) == null || b.Current == key) { return; }
            b.Current = b.Step(key).Key;
            b.AskReply = string.Empty;
            b.AskError = string.Empty;
            b.Suggested.Clear();
            b.NotOptions.Clear();
            _s.Notify(StateArea.Builder);
            Run(BuilderOptions(b.Current));
            if (!b.PreviewCurrent) { Run(BuilderPreview()); }
        }

        /// <summary>The next (or previous) step, by the recipe's order.</summary>
        public void BuilderStepBy(int delta)
        {
            var b = _s.Builder;
            int i = BuilderDependencies.IndexOf(b.Steps, b.Current) + delta;
            if (i >= 0 && i < b.Steps.Count) { BuilderGoTo(b.Steps[i].Key); }
        }

        public void DismissBuilderNote()
        {
            _s.Builder.ClearedNote = string.Empty;
            _s.Notify(StateArea.Builder);
        }

        // ---- choices ----

        /// <summary>
        /// Records a step's choice. Steps that read it (skills read class, spells read class) lose their choices, and
        /// the dialog says which; then the step list (a condition may flip) and the preview catch up.
        /// </summary>
        public IEnumerator BuilderChoose(string key, JsonValue value)
        {
            var b = _s.Builder;
            var step = b.Step(key);
            if (step == null || !b.Draft.Set(step.Key, value)) { yield break; }
            b.Revision++;
            b.PreviewCurrent = false;
            var cleared = BuilderDependencies.ClearDependents(b.Draft, step.Key, b.Steps);
            foreach (string dependent in BuilderDependencies.Dependents(step.Key, b.Steps)) { b.Options.Remove(dependent); }
            if (cleared.Count > 0) { b.ClearedNote = BuilderDependencies.ClearedNote(step, cleared); }
            _s.Notify(StateArea.Builder);
            yield return BuilderSteps();
            yield return BuilderOptions(b.Current);
            yield return BuilderPreview();
        }

        /// <summary>pickN, feats, allocate: adds or removes one option, never past the step's count.</summary>
        public IEnumerator BuilderToggle(string key, string optionId)
        {
            var b = _s.Builder;
            var picked = b.Draft.GetList(key);
            int at = picked.FindIndex(delegate (string p) { return string.Equals(p, optionId, StringComparison.OrdinalIgnoreCase); });
            if (at >= 0) { picked.RemoveAt(at); }
            else
            {
                int count = PickCount(key);
                if (count > 0 && picked.Count >= count) { yield break; }
                picked.Add(optionId);
            }
            yield return BuilderChoose(key, CharacterDraft.StringArray(picked));
        }

        /// <summary>A spells step: one spell in or out of a group. Dropping a known spell drops it from prepared too.</summary>
        public IEnumerator BuilderToggleSpell(string key, string group, string spellId)
        {
            var b = _s.Builder;
            var choice = b.Draft.Get(key);
            var lists = new Dictionary<string, List<string>>
            {
                { SpellGroups.Cantrips, CharacterDraft.Strings(choice.Get(SpellGroups.Cantrips)) },
                { SpellGroups.Known, CharacterDraft.Strings(choice.Get(SpellGroups.Known)) },
                { SpellGroups.Prepared, CharacterDraft.Strings(choice.Get(SpellGroups.Prepared)) },
            };
            List<string> list;
            if (!lists.TryGetValue(group, out list)) { yield break; }
            if (!list.Remove(spellId))
            {
                StepOptions options;
                int count = b.Options.TryGetValue(key, out options) ? options.GroupCount(group) : 0;
                if (count > 0 && list.Count >= count) { yield break; }
                list.Add(spellId);
            }
            else if (group == SpellGroups.Known)
            {
                lists[SpellGroups.Prepared].Remove(spellId);
            }
            var value = JsonValue.NewObject();
            foreach (var kv in lists) { value.ObjectValue[kv.Key] = CharacterDraft.StringArray(kv.Value); }
            bool empty = lists[SpellGroups.Cantrips].Count + lists[SpellGroups.Known].Count + lists[SpellGroups.Prepared].Count == 0;
            yield return BuilderChoose(key, empty ? null : value);
        }

        /// <summary>Picks the ability-score method; switching starts its pool afresh (rolls already made stay in the log).</summary>
        public IEnumerator BuilderAbilityMethod(string key, string method)
        {
            var b = _s.Builder;
            var step = b.Step(key);
            var work = b.Abilities;
            if (step == null || work.Method == method) { yield break; }
            work.Method = method;
            work.Assigned.Clear();
            work.Pool.Clear();
            if (method == "standardArray") { work.Pool.AddRange(step.StandardArray); }
            if (method == "pointBuy" && step.PointBuy != null)
            {
                foreach (string ability in AbilityWork.Abilities) { work.Bought[ability] = step.PointBuy.Min; }
            }
            yield return BuilderChoose(key, AbilityChoice(work));
        }

        /// <summary>Array or roll: gives an ability one value from the pool, swapping with whoever had it.</summary>
        public IEnumerator BuilderAssignAbility(string key, string ability, int poolIndex)
        {
            var work = _s.Builder.Abilities;
            if (poolIndex < 0 || poolIndex >= work.Pool.Count) { yield break; }
            int previous;
            bool had = work.Assigned.TryGetValue(ability, out previous);
            foreach (string other in AbilityWork.Abilities)
            {
                int index;
                if (other != ability && work.Assigned.TryGetValue(other, out index) && index == poolIndex)
                {
                    if (had) { work.Assigned[other] = previous; } else { work.Assigned.Remove(other); }
                }
            }
            work.Assigned[ability] = poolIndex;
            yield return BuilderChoose(key, AbilityChoice(work));
        }

        /// <summary>Point buy: one step up or down, inside the table's range and the budget.</summary>
        public IEnumerator BuilderBuyAbility(string key, string ability, int delta)
        {
            var b = _s.Builder;
            var step = b.Step(key);
            var work = b.Abilities;
            if (step == null || step.PointBuy == null) { yield break; }
            var rules = step.PointBuy;
            int score;
            if (!work.Bought.TryGetValue(ability, out score)) { score = rules.Min; }
            int next = score + delta;
            if (next < rules.Min || next > rules.Max || rules.CostOf(next) < 0) { yield break; }
            var after = new Dictionary<string, int>(work.Bought);
            after[ability] = next;
            if (rules.Spent(after.Values) > rules.Budget) { yield break; }
            work.Bought[ability] = next;
            yield return BuilderChoose(key, AbilityChoice(work));
        }

        /// <summary>Rolls the six scores; every roll goes in the log, which stays on screen.</summary>
        public IEnumerator BuilderRollAbilities(string key)
        {
            var b = _s.Builder;
            var step = b.Step(key);
            if (step == null || !AbilityDice.IsValid(step.Roll)) { yield break; }
            var work = b.Abilities;
            work.Method = "roll";
            work.Pool.Clear();
            work.Assigned.Clear();
            int set = 1;
            foreach (string line in work.RollLog) { if (line.StartsWith("Set ", StringComparison.Ordinal)) { set++; } }
            work.RollLog.Add("Set " + set + ":");
            for (int i = 0; i < AbilityWork.Abilities.Length; i++)
            {
                string log;
                work.Pool.Add(AbilityDice.Roll(step.Roll, _dice, out log));
                work.RollLog.Add("  " + log);
            }
            yield return BuilderChoose(key, AbilityChoice(work));
        }

        internal static JsonValue AbilityChoice(AbilityWork work)
        {
            if (string.IsNullOrEmpty(work.Method)) { return null; }
            var value = JsonValue.NewObject();
            value.ObjectValue["method"] = JsonValue.FromString(work.Method);
            var scores = JsonValue.NewObject();
            foreach (var kv in work.Scores()) { scores.ObjectValue[kv.Key] = JsonValue.FromNumber(kv.Value); }
            value.ObjectValue["scores"] = scores;
            return value;
        }

        /// <summary>Name, concept and look, as typed: no server call per keystroke. The preview catches up on BuilderPreview.</summary>
        public void BuilderIdentity(string name, string concept, string look)
        {
            var d = _s.Builder.Draft;
            if (name != null) { d.Name = name.Trim(); }
            if (concept != null) { d.Concept = concept.Trim(); }
            if (look != null) { d.Look = look.Trim(); }
            _s.Builder.PreviewCurrent = false;
        }

        /// <summary>A stat block field of an identity step with a schema: the choice is an object of field → value.</summary>
        public IEnumerator BuilderStatField(string key, StatBlockField field, string text)
        {
            var current = _s.Builder.Draft.Get(key);
            var value = JsonValue.NewObject();
            if (current.Kind == JsonKind.Object) { foreach (var kv in current.ObjectValue) { value.ObjectValue[kv.Key] = kv.Value; } }
            text = (text ?? string.Empty).Trim();
            int n;
            if (text.Length == 0) { value.ObjectValue.Remove(field.Key); }
            else if (field.Type == "int" && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n))
            {
                value.ObjectValue[field.Key] = JsonValue.FromNumber(n);
            }
            else { value.ObjectValue[field.Key] = JsonValue.FromString(text); }
            yield return BuilderChoose(key, value.ObjectValue.Count == 0 ? null : value);
        }

        /// <summary>1 to MaxBuilderLevel. Counts (spells, skills) follow the level, so every step's options reload.</summary>
        public IEnumerator SetBuilderLevel(int level)
        {
            var b = _s.Builder;
            level = Math.Max(1, Math.Min(MaxBuilderLevel, level));
            if (b.Draft.Level == level) { yield break; }
            b.Draft.Level = level;
            b.Revision++;
            b.PreviewCurrent = false;
            b.Options.Clear();
            _s.Notify(StateArea.Builder);
            yield return BuilderSteps();
            yield return BuilderOptions(b.Current);
            yield return BuilderPreview();
        }

        private int PickCount(string key)
        {
            var b = _s.Builder;
            StepOptions options;
            if (b.Options.TryGetValue(key, out options) && options.Count > 0) { return options.Count; }
            var step = b.Step(key);
            return step != null ? step.Count : -1;
        }

        // ---- server calls ----

        private IEnumerator CallBuilder(string action, string step, Action<McpOutcome<ToolPayload>> done)
        {
            var b = _s.Builder;
            var args = JsonValue.NewObject();
            args.ObjectValue["action"] = JsonValue.FromString(action);
            args.ObjectValue["draft"] = b.Draft.ToJson();
            args.ObjectValue["campaignName"] = JsonValue.FromString(b.Slug);
            if (!string.IsNullOrEmpty(step)) { args.ObjectValue["step"] = JsonValue.FromString(step); }
            yield return _s.Mcp.CallToolData(_s.Config, "build", BuilderTool, args, done);
        }

        /// <summary>The recipe's steps for the draft. A step that drops out (its condition no longer holds) takes its choice with it.</summary>
        public IEnumerator BuilderSteps()
        {
            var b = _s.Builder;
            int revision = b.Revision;
            McpOutcome<ToolPayload> result = null;
            yield return CallBuilder("steps", null, delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (revision != b.Revision) { yield break; }
            if (result == null || !result.Ok)
            {
                b.Error = "Couldn't load the builder: " + (result != null ? result.ErrorMessage : "no response");
                _s.Notify(StateArea.Builder);
                yield break;
            }
            ReadSteps(b, result.Data.Data);
            _s.Notify(StateArea.Builder);
        }

        internal static void ReadSteps(BuilderState b, JsonValue data)
        {
            string system = data.GetString("system", string.Empty);
            if (system.Length > 0) { b.System = system; }
            int at = Math.Max(0, BuilderDependencies.IndexOf(b.Steps, b.Current));
            b.Steps.Clear();
            foreach (var s in data.GetArray("steps")) { b.Steps.Add(BuilderStep.Parse(s)); }
            b.StatBlocks.Clear();
            foreach (var s in data.GetArray("statBlocks")) { b.StatBlocks.Add(StatBlockSchema.Parse(s)); }
            foreach (string key in new List<string>(b.Draft.Choices.Keys))
            {
                if (b.Step(key) == null) { b.Draft.Choices.Remove(key); }
            }
            if (b.Steps.Count > 0 && b.Step(b.Current) == null) { b.Current = b.Steps[Math.Min(at, b.Steps.Count - 1)].Key; }
            b.Error = string.Empty;
        }

        /// <summary>What a step offers for the draft as it is; cached until a step it reads changes.</summary>
        public IEnumerator BuilderOptions(string key)
        {
            var b = _s.Builder;
            var step = b.Step(key);
            if (step == null || (step.Source.Length == 0 && step.Kind != StepKinds.Spells) || b.Options.ContainsKey(step.Key)) { yield break; }
            string busy = "builder-options:" + step.Key;
            if (!_s.TryBeginBusy(busy)) { yield break; }
            try
            {
                // A reply about an older draft is dropped and asked again.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int revision = b.Revision;
                    McpOutcome<ToolPayload> result = null;
                    yield return CallBuilder("options", step.Key, delegate (McpOutcome<ToolPayload> o) { result = o; });
                    if (revision != b.Revision) { continue; }
                    b.Options[step.Key] = ReadOptions(result);
                    _s.Notify(StateArea.Builder);
                    yield break;
                }
            }
            finally { _s.EndBusy(busy); }
        }

        internal static StepOptions ReadOptions(McpOutcome<ToolPayload> result)
        {
            var options = new StepOptions();
            if (result == null || !result.Ok)
            {
                options.Error = result != null ? result.ErrorMessage : "no response";
                return options;
            }
            var data = result.Data.Data;
            foreach (var o in data.GetArray("options")) { options.Options.Add(BuilderOption.Parse(o)); }
            options.Count = (int)data.GetNumber("count", -1);
            var groups = data.Get("groupCounts");
            if (groups.Kind == JsonKind.Object)
            {
                foreach (var kv in groups.ObjectValue)
                {
                    if (kv.Value.Kind == JsonKind.Number) { options.GroupCounts[kv.Key] = (int)kv.Value.NumberValue; }
                }
            }
            return options;
        }

        /// <summary>The derived sheet and every problem, for the draft as it is. Nothing is saved.</summary>
        public IEnumerator BuilderPreview()
        {
            var b = _s.Builder;
            if (b.Slug.Length == 0) { yield break; }
            int revision = b.Revision;
            string name = b.Draft.Name + "\n" + b.Draft.Concept + "\n" + b.Draft.Look;
            McpOutcome<ToolPayload> result = null;
            yield return CallBuilder("preview", null, delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (revision != b.Revision) { yield break; }
            if (result == null || !result.Ok)
            {
                b.Error = "Preview failed: " + (result != null ? result.ErrorMessage : "no response");
                _s.Notify(StateArea.Builder);
                yield break;
            }
            ReadPreview(b, result.Data.Data);
            b.PreviewCurrent = name == b.Draft.Name + "\n" + b.Draft.Concept + "\n" + b.Draft.Look;
            b.Error = string.Empty;
            _s.Notify(StateArea.Builder);
        }

        internal static void ReadPreview(BuilderState b, JsonValue data)
        {
            if (data.Get("character").Kind == JsonKind.Object) { b.Preview = CharacterSheet.FromPayload(data); }
            // The server calls a nameless draft "Unnamed"; the sheet's crest shouldn't read "UN".
            if (b.Preview != null && b.Draft.Name.Length == 0) { b.Preview.Name = string.Empty; }
            b.Errors.Clear();
            b.Warnings.Clear();
            b.Notes.Clear();
            foreach (var e in data.GetArray("errors")) { b.Errors.Add(BuilderIssue.Parse(e)); }
            foreach (var w in data.GetArray("warnings")) { var issue = BuilderIssue.Parse(w); issue.Warning = true; b.Warnings.Add(issue); }
            foreach (var n in data.GetArray("notes")) { if (n.Kind == JsonKind.String) { b.Notes.Add(n.StringValue); } }
        }

        /// <summary>
        /// Saves the character through world_build (the server re-validates; errors block it). Only offered once the
        /// preview is clean. The returned id stays on the draft, so committing again updates the same character.
        /// </summary>
        public IEnumerator BuilderCommit()
        {
            var b = _s.Builder;
            if (!_s.TryBeginBusy("builder-commit")) { yield break; }
            try
            {
                yield return BuilderPreview();
                if (b.Errors.Count > 0)
                {
                    b.Error = "Not saved yet: fix the marked steps first.";
                    var first = b.Step(b.Errors[0].Step);
                    if (first != null) { b.Current = first.Key; }
                    _s.Notify(StateArea.Builder);
                    yield break;
                }
                McpOutcome<ToolPayload> result = null;
                yield return CallBuilder("commit", null, delegate (McpOutcome<ToolPayload> o) { result = o; });
                if (result == null || !result.Ok)
                {
                    b.Error = "Not saved: " + (result != null ? result.ErrorMessage : "no response");
                    _s.Notify(StateArea.Builder);
                    yield break;
                }
                var data = result.Data.Data;
                string id = data.Get("character").GetString("id", string.Empty);
                bool update = b.CommittedId.Length > 0;
                b.Draft.Id = id;
                b.CommittedId = id;
                if (data.Get("character").Kind == JsonKind.Object) { b.Preview = CharacterSheet.FromPayload(data); }
                b.Error = string.Empty;
                string who = b.Draft.Name.Length > 0 ? b.Draft.Name : "The character";
                _s.RaiseToast(update ? who + " is updated." : who + " is saved to the campaign.", ToastKind.Success);
                // The first player character built is the one the player plays.
                if (b.ForOnboarding) { RecordPartyMember(b, id); }
                else if (b.Draft.Kind == "pc" && string.IsNullOrEmpty(_s.PcId) && id.Length > 0) { SetPcId(id); }
                _s.Notify(StateArea.Builder);
            }
            finally { _s.EndBusy("builder-commit"); }
        }

        // ---- asking the DM ----

        /// <summary>
        /// Asks the DM about the current step, in the onboarding conversation (a "Building Lyra · Class" divider marks
        /// where). Suggested ids the step offers become one-tap picks; others are listed as not options.
        /// </summary>
        public IEnumerator AskDmAboutStep(string text)
        {
            var b = _s.Builder;
            var ob = _s.Onboarding;
            text = TextSanitizer.Clean(text, 0).Trim();
            var step = b.CurrentStep;
            if (text.Length == 0 || b.AskBusy || step == null) { yield break; }
            string notReady;
            if (!_s.ProviderReady(out notReady))
            {
                _s.RaiseToast("Asking the DM needs a working AI provider: " + notReady, ToastKind.Warning);
                yield break;
            }
            if (text.Length > OnboardingBrainstorm.MaxMessageChars)
            {
                b.AskError = "That message is over " + OnboardingBrainstorm.MaxMessageChars + " characters. Trim it or send it in parts.";
                _s.Notify(StateArea.Builder);
                yield break;
            }
            yield return BuilderOptions(step.Key);
            StepOptions options;
            b.Options.TryGetValue(step.Key, out options);
            var offered = options != null ? options.Options : new List<BuilderOption>();

            string marker = BuilderAdvisor.Marker(b.Draft.Name, step.Title);
            if (LastMarker(ob.BrainstormChat) != marker) { ob.BrainstormChat.Add(new KeyValuePair<string, string>(OnboardingBrainstorm.MarkerRole, marker)); }
            ob.BrainstormChat.Add(new KeyValuePair<string, string>("user", text));
            b.AskDraft = string.Empty;
            b.AskBusy = true;
            b.AskError = string.Empty;
            b.AskStep = step.Key;
            _s.Notify(StateArea.Builder);

            var messages = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("system", BuilderAdvisor.SystemPrompt(b.System, step, offered, PickCount(step.Key), b.Draft, ob.Answers)),
            };
            messages.AddRange(OnboardingBrainstorm.ModelMessages(ob.BrainstormChat, OnboardingBrainstorm.Dropped(ob.BrainstormChat, OnboardingBrainstorm.MaxConversationChars)));
            string reply = null;
            string error = null;
            yield return _s.Driver.Brainstorm(messages, null, delegate (string r, string e) { reply = r; error = e; });
            b.AskBusy = false;
            if (b.AskStep != step.Key || b.Current != step.Key) { _s.Notify(StateArea.Builder); yield break; }
            if (error != null)
            {
                b.AskError = TextSanitizer.Clean(error, 400);
            }
            else
            {
                string clean = TextSanitizer.Clean(reply, OnboardingBrainstorm.MaxReplyChars);
                ob.BrainstormChat.Add(new KeyValuePair<string, string>("assistant", clean));
                var ids = new List<string>();
                b.AskReply = BuilderAdvisor.SplitSuggestions(clean, ids);
                b.Suggested.Clear();
                b.NotOptions.Clear();
                BuilderAdvisor.Match(ids, offered, b.Suggested, b.NotOptions);
            }
            _s.Notify(StateArea.Builder);
        }

        private static string LastMarker(List<KeyValuePair<string, string>> chat)
        {
            for (int i = chat.Count - 1; i >= 0; i--)
            {
                if (chat[i].Key == OnboardingBrainstorm.MarkerRole) { return chat[i].Value; }
            }
            return null;
        }
    }
}
