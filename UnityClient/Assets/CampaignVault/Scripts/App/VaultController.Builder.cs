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

        /// <summary>
        /// The highest level the builder builds at for a ruleset, before its recipe has loaded (the party step asks for a
        /// level first). The server's recipe says the same (maxLevel) and rejects a draft above it. D&amp;D 5e walks every
        /// level's choices; PF2e stops at 3 until its skill increases and later boosts are steps; Narrative has no levels.
        /// </summary>
        public static int MaxBuilderLevelFor(string ruleset)
        {
            switch ((ruleset ?? string.Empty).ToLowerInvariant())
            {
                case "dnd5e": return 20;
                case "narrative": return 1;
                default: return 3;
            }
        }

        /// <summary>The builder's level cap: the recipe's once its steps have loaded, else the ruleset's.</summary>
        public int BuilderMaxLevel
        {
            get { return _s.Builder.MaxLevel > 0 ? _s.Builder.MaxLevel : MaxBuilderLevelFor(_s.Builder.System); }
        }

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
                    // The draft that built the character, so the builder shows its choices and saves over it. A DM draft
                    // under review isn't saved yet: its id is a placeholder, and the commit replaces its card.
                    b.Reset(slug, system, member.Kind);
                    b.ForOnboarding = true;
                    b.Draft = CharacterDraft.FromJson(member.Draft.ToJson());
                    if (member.Pending) { b.Draft.Id = string.Empty; b.PendingKey = member.Id; }
                    else { b.Draft.Id = member.Id; b.CommittedId = member.Id; }
                    // The power check compares against the party as it is now, not as it was when this was drafted or built.
                    if (member.Kind == "companion") { b.Draft.PartyLevel = ob.PartyLevel; }
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
            if (forOnboarding)
            {
                b.Draft.Level = Math.Max(1, Math.Min(_s.Onboarding.PartyLevel, MaxBuilderLevelFor(system)));
                // A companion starts at the party's level; the server warns when it strays more than one from it.
                if (b.Draft.Kind == "companion") { b.Draft.PartyLevel = _s.Onboarding.PartyLevel; }
            }
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
            DropSpellCounts(b, step.Key);
            if (cleared.Count > 0) { b.ClearedNote = BuilderDependencies.ClearedNote(step, cleared); }
            _s.Notify(StateArea.Builder);
            yield return BuilderSteps();
            yield return BuilderOptions(b.Current);
            yield return BuilderPreview();
        }

        /// <summary>A levelChoices step: one option of one slot (see <see cref="LevelChoices.Toggle"/>).</summary>
        public IEnumerator BuilderLevelPick(string key, LevelSlot slot, string optionId)
        {
            var b = _s.Builder;
            var before = b.Draft.Get(key);
            var after = LevelChoices.Toggle(before, slot, optionId);
            if (after == before) { yield break; }
            yield return BuilderChoose(key, after);
        }

        /// <summary>
        /// Forgets a spells step's cached options (unless it is the step that changed): its prepared count follows the
        /// casting ability, which the ability scores and an ability score improvement change without the step reading them.
        /// </summary>
        private static void DropSpellCounts(BuilderState b, string changed)
        {
            foreach (var step in b.Steps)
            {
                if (step.Kind == StepKinds.Spells && !string.Equals(step.Key, changed, StringComparison.OrdinalIgnoreCase)) { b.Options.Remove(step.Key); }
            }
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

        /// <summary>
        /// One entry of a modifiers field (a companion's skill): set to the text's number (kept as text when it isn't one,
        /// for the preview to flag), or removed when the text is empty.
        /// </summary>
        public IEnumerator BuilderStatModifier(string key, StatBlockField field, string name, string text)
        {
            var current = _s.Builder.Draft.Get(key);
            var value = JsonValue.NewObject();
            if (current.Kind == JsonKind.Object) { foreach (var kv in current.ObjectValue) { value.ObjectValue[kv.Key] = kv.Value; } }
            var entries = JsonValue.NewObject();
            var had = value.Get(field.Key);
            if (had.Kind == JsonKind.Object) { foreach (var kv in had.ObjectValue) { entries.ObjectValue[kv.Key] = kv.Value; } }
            name = StatModifiers.Canonical(field, name);
            text = (text ?? string.Empty).Trim();
            int n;
            if (text.Length == 0) { entries.ObjectValue.Remove(name); }
            else if (int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out n)) { entries.ObjectValue[name] = JsonValue.FromNumber(n); }
            else { entries.ObjectValue[name] = JsonValue.FromString(text); }
            if (entries.ObjectValue.Count == 0) { value.ObjectValue.Remove(field.Key); } else { value.ObjectValue[field.Key] = entries; }
            yield return BuilderChoose(key, value.ObjectValue.Count == 0 ? null : value);
        }

        /// <summary>One cell of a rows field (a companion's attack): set to the text (a whole-number column as a number), or cleared when empty.</summary>
        public IEnumerator BuilderStatRowCell(string key, StatBlockField field, int index, StatBlockColumn column, string text)
        {
            return EditStatRows(key, field, delegate (List<JsonValue> rows)
            {
                if (index < 0 || index >= rows.Count || rows[index].Kind != JsonKind.Object) { return; }
                text = (text ?? string.Empty).Trim();
                if (text.Length == 0) { rows[index].ObjectValue.Remove(column.Key); }
                else { rows[index].ObjectValue[column.Key] = StatRows.Cell(column, text); }
            });
        }

        /// <summary>A new, empty row at the end (its name is asked for by the preview until it has one).</summary>
        public IEnumerator BuilderStatRowAdd(string key, StatBlockField field)
        {
            return EditStatRows(key, field, delegate (List<JsonValue> rows) { rows.Add(JsonValue.NewObject()); });
        }

        public IEnumerator BuilderStatRowRemove(string key, StatBlockField field, int index)
        {
            return EditStatRows(key, field, delegate (List<JsonValue> rows) { if (index >= 0 && index < rows.Count) { rows.RemoveAt(index); } });
        }

        /// <summary>A copy of the stat block with the field's rows (read from text if need be) edited; no rows left removes the field.</summary>
        private IEnumerator EditStatRows(string key, StatBlockField field, Action<List<JsonValue>> edit)
        {
            var current = _s.Builder.Draft.Get(key);
            var value = JsonValue.NewObject();
            if (current.Kind == JsonKind.Object) { foreach (var kv in current.ObjectValue) { value.ObjectValue[kv.Key] = kv.Value; } }
            var had = StatRows.From(field, value.Get(field.Key));
            var rows = new List<JsonValue>();
            if (had.Kind == JsonKind.Array)
            {
                foreach (var r in had.ArrayValue) { rows.Add(r.Kind == JsonKind.Object ? JsonValue.Parse(r.ToJson()) : r); }
            }
            edit(rows);
            if (rows.Count == 0) { value.ObjectValue.Remove(field.Key); }
            else
            {
                var list = JsonValue.NewArray();
                list.ArrayValue.AddRange(rows);
                value.ObjectValue[field.Key] = list;
            }
            yield return BuilderChoose(key, value.ObjectValue.Count == 0 ? null : value);
        }

        /// <summary>
        /// Picking a template on a stat block step: its values become the stat block (numbers where the field is a
        /// number), and the name comes along when the draft has none. Everything stays editable.
        /// </summary>
        public IEnumerator BuilderApplyTemplate(string key, BuilderOption template)
        {
            var step = _s.Builder.Step(key);
            StatBlockSchema schema = null;
            if (step != null) { foreach (var s in _s.Builder.StatBlocks) { if (string.Equals(s.Name, step.Schema, StringComparison.OrdinalIgnoreCase)) { schema = s; } } }
            var value = JsonValue.NewObject();
            foreach (var kv in template.Values)
            {
                StatBlockField field = null;
                if (schema != null) { foreach (var f in schema.Fields) { if (f.Key == kv.Key) { field = f; } } }
                int n;
                if (field != null && field.Type == "int" && int.TryParse(kv.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n))
                {
                    value.ObjectValue[kv.Key] = JsonValue.FromNumber(n);
                }
                else if (field != null && field.Type == StatModifiers.Type) { value.ObjectValue[kv.Key] = StatModifiers.FromText(field, kv.Value); }
                else if (field != null && field.Type == StatRows.Type) { value.ObjectValue[kv.Key] = StatRows.FromText(field, kv.Value); }
                else { value.ObjectValue[kv.Key] = JsonValue.FromString(kv.Value); }
            }
            if (_s.Builder.Draft.Name.Length == 0) { _s.Builder.Draft.Name = template.Label; }
            yield return BuilderChoose(key, value);
        }

        /// <summary>
        /// 1 to the cap. Counts (spells, skills) and level choices follow the level, so every step's options reload; picks
        /// for levels the character no longer reaches are dropped.
        /// </summary>
        public IEnumerator SetBuilderLevel(int level)
        {
            var b = _s.Builder;
            level = Math.Max(1, Math.Min(BuilderMaxLevel, level));
            if (b.Draft.Level == level) { yield break; }
            b.Draft.Level = level;
            foreach (var step in b.Steps)
            {
                if (step.Kind == StepKinds.LevelChoices) { b.Draft.Set(step.Key, LevelChoices.Prune(b.Draft.Get(step.Key), level)); }
            }
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
            yield return CallBuilder(_s.Builder.Slug, _s.Builder.Draft, action, step, done);
        }

        /// <summary>character_builder for a draft that isn't the builder's (the party step's DM drafts), leaving the builder alone.</summary>
        private IEnumerator CallBuilder(string slug, CharacterDraft draft, string action, string step, Action<McpOutcome<ToolPayload>> done)
        {
            var args = JsonValue.NewObject();
            args.ObjectValue["action"] = JsonValue.FromString(action);
            args.ObjectValue["draft"] = draft.ToJson();
            args.ObjectValue["campaignName"] = JsonValue.FromString(slug);
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
            var reads = data.Get("reads");
            foreach (var s in data.GetArray("steps"))
            {
                var step = BuilderStep.Parse(s);
                var told = reads.Get(step.Key);
                if (told.Kind == JsonKind.Array)
                {
                    step.Reads = new List<string>();
                    foreach (var key in told.ArrayValue) { if (key.Kind == JsonKind.String) { step.Reads.Add(key.StringValue); } }
                }
                b.Steps.Add(step);
            }
            b.StatBlocks.Clear();
            foreach (var s in data.GetArray("statBlocks")) { b.StatBlocks.Add(StatBlockSchema.Parse(s)); }
            b.MaxLevel = Math.Max(0, (int)data.GetNumber("maxLevel", 0));
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
            if (step == null || (step.Source.Length == 0 && step.Kind != StepKinds.Spells && step.Kind != StepKinds.LevelChoices) || b.Options.ContainsKey(step.Key)) { yield break; }
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
            foreach (var slot in data.GetArray("slots")) { options.Slots.Add(LevelSlot.Parse(slot)); }
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

        // ---- the DM fills the rest ----

        /// <summary>
        /// One model call for every step still open (skills, spells, level choices; never the ability scores or who the
        /// character is), in the onboarding conversation. The picks that are options are added to the draft and the
        /// preview checks them; the rest are listed on their step. Nothing is saved: the player reviews, then saves.
        /// </summary>
        public IEnumerator BuilderDmFill()
        {
            var b = _s.Builder;
            var ob = _s.Onboarding;
            if (b.FillBusy || b.Steps.Count == 0) { yield break; }
            string notReady;
            if (!_s.ProviderReady(out notReady))
            {
                _s.RaiseToast("The DM needs a working AI provider to fill in picks: " + notReady, ToastKind.Warning);
                yield break;
            }
            b.ClearFill();
            b.FillBusy = true;
            _s.Notify(StateArea.Builder);
            try
            {
                List<FillTarget> targets = null;
                yield return FillTargets(delegate (List<FillTarget> t) { targets = t; });
                if (targets.Count == 0)
                {
                    b.FillError = "Nothing is open that the DM can pick from options. Ability scores and who they are stay yours.";
                    yield break;
                }
                int revision = b.Revision;
                string marker = BuilderAdvisor.Marker(b.Draft.Name, "the DM fills the rest");
                if (LastMarker(ob.BrainstormChat) != marker) { ob.BrainstormChat.Add(new KeyValuePair<string, string>(OnboardingBrainstorm.MarkerRole, marker)); }
                string instruction = BuilderFiller.Instruction(targets);
                ob.BrainstormChat.Add(new KeyValuePair<string, string>("user", instruction));
                var messages = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("system", BuilderFiller.SystemPrompt(b.System, b.Draft, targets, ob.Answers)),
                };
                messages.AddRange(OnboardingBrainstorm.ModelMessages(ob.BrainstormChat, OnboardingBrainstorm.Dropped(ob.BrainstormChat, OnboardingBrainstorm.MaxConversationChars)));
                string reply = null;
                string error = null;
                yield return _s.Driver.Brainstorm(messages, null, delegate (string r, string e) { reply = r; error = e; });
                if (error != null) { b.FillError = TextSanitizer.Clean(error, 400); yield break; }
                if (revision != b.Revision)
                {
                    b.FillError = "The character changed while the DM was thinking, so its picks weren't used. Ask again.";
                    yield break;
                }
                ob.BrainstormChat.Add(new KeyValuePair<string, string>("assistant", TextSanitizer.Clean(reply, OnboardingBrainstorm.MaxReplyChars)));
                b.FillBusy = false;
                yield return ApplyDmFill(reply, targets);
            }
            finally
            {
                b.FillBusy = false;
                _s.Notify(StateArea.Builder);
            }
        }

        /// <summary>The fillable steps that are open and have options for the draft as it is (their options loaded first).</summary>
        private IEnumerator FillTargets(Action<List<FillTarget>> done)
        {
            var b = _s.Builder;
            var targets = new List<FillTarget>();
            foreach (var step in new List<BuilderStep>(b.Steps))
            {
                if (!BuilderFiller.CanFill(step)) { continue; }
                yield return BuilderOptions(step.Key);
                StepOptions options;
                if (!b.Options.TryGetValue(step.Key, out options) || options.Error.Length > 0 || options.Options.Count == 0) { continue; }
                if (BuilderFiller.IsOpen(step, options, b.Draft)) { targets.Add(new FillTarget { Step = step, Options = options }); }
            }
            done(targets);
        }

        /// <summary>
        /// The fill reply applied to the draft: each step's options-only picks added, the rest listed as rejected, then the
        /// steps, options and preview catch up. Split from the call so tests can feed a reply. Without targets, the open
        /// steps are worked out first.
        /// </summary>
        public IEnumerator ApplyDmFill(string reply, List<FillTarget> targets = null)
        {
            var b = _s.Builder;
            if (targets == null) { yield return FillTargets(delegate (List<FillTarget> t) { targets = t; }); }
            var result = new FillResult();
            string error;
            if (!BuilderFiller.Parse(reply, targets, b.Draft, result, out error))
            {
                b.FillError = error;
                _s.Notify(StateArea.Builder);
                yield break;
            }
            b.FillReply = TextSanitizer.Clean(result.Prose, OnboardingBrainstorm.MaxReplyChars);
            b.Filled.Clear();
            b.Rejected.Clear();
            foreach (var kv in result.Rejected) { b.Rejected[kv.Key] = kv.Value; }
            foreach (var t in targets)
            {
                JsonValue value;
                if (!result.Choices.TryGetValue(t.Step.Key, out value) || !b.Draft.Set(t.Step.Key, value)) { continue; }
                b.Filled.Add(t.Step.Title);
                // A step that reads this one has options for the old pick: they're fetched again (its picks, if any, stay to be checked).
                foreach (string dependent in BuilderDependencies.Dependents(t.Step.Key, b.Steps)) { b.Options.Remove(dependent); }
            }
            DropSpellCounts(b, null);
            b.Revision++;
            b.PreviewCurrent = false;
            _s.Notify(StateArea.Builder);
            yield return BuilderSteps();
            yield return BuilderOptions(b.Current);
            yield return BuilderPreview();
        }

        public void DismissDmFill()
        {
            _s.Builder.ClearFill();
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
