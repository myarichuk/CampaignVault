using System;
using System.Collections.Generic;
using System.Text;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>A step the DM may fill, with the options it offers for the draft as it is.</summary>
    public sealed class FillTarget
    {
        public BuilderStep Step;
        public StepOptions Options;
    }

    /// <summary>What a fill reply comes to: each step's new choice, and the picks that weren't options (never applied).</summary>
    public sealed class FillResult
    {
        /// <summary>The DM's words about its picks, without the JSON.</summary>
        public string Prose = string.Empty;
        /// <summary>Step key → the step's whole choice after the fill (the player's picks kept, the DM's added).</summary>
        public readonly Dictionary<string, JsonValue> Choices = new Dictionary<string, JsonValue>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Step key → "pyromancy (Level 2 · Arcane Tradition)": picks that aren't options there, left open.</summary>
        public readonly Dictionary<string, List<string>> Rejected = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "The DM fills the rest" in the builder: ONE model call proposes option ids for every step still open (skills,
    /// spells, level choices...), never the ability scores or who the character is. The player's picks stay; the DM's are
    /// added only where they are options, and the rest are listed on their step, never fixed silently. Nothing is
    /// saved: the server previews the result like anything tapped, and the player reviews it before saving.
    /// </summary>
    public static class BuilderFiller
    {
        /// <summary>Options listed per step at most; a longer list says how many were left out.</summary>
        public const int MaxPromptOptions = 200;

        /// <summary>Steps chosen from options. Ability scores (a method and the dice) and identity (the player's words) aren't.</summary>
        public static bool CanFill(BuilderStep step)
        {
            switch (step.Kind)
            {
                case StepKinds.PickOne:
                case StepKinds.PickN:
                case StepKinds.Feats:
                case StepKinds.Allocate:
                case StepKinds.Spells:
                case StepKinds.LevelChoices:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The step still wants picks: nothing chosen, fewer than its count, or a level choice without one.</summary>
        public static bool IsOpen(BuilderStep step, StepOptions options, CharacterDraft draft)
        {
            switch (step.Kind)
            {
                case StepKinds.PickOne:
                    return !draft.Has(step.Key);
                case StepKinds.Spells:
                    foreach (string group in new[] { SpellGroups.Cantrips, SpellGroups.Known, SpellGroups.Prepared })
                    {
                        if (CharacterDraft.Strings(draft.Get(step.Key).Get(group)).Count < options.GroupCount(group)) { return true; }
                    }
                    return false;
                case StepKinds.LevelChoices:
                    var picks = LevelChoices.Picks(draft.Get(step.Key));
                    foreach (var slot in options.Slots) { if (SlotOpen(slot, picks)) { return true; } }
                    return false;
                default:
                    int count = Count(step, options);
                    return count > 0 ? draft.GetList(step.Key).Count < count : !draft.Has(step.Key);
            }
        }

        public static string SystemPrompt(string system, CharacterDraft draft, IList<FillTarget> targets, IDictionary<string, string> answersSoFar)
        {
            var sb = new StringBuilder();
            sb.Append("You are a game master helping a player build a ").Append(SystemName(system)).Append(" character for their campaign. ");
            sb.Append("This is the same conversation you had with them while setting the campaign up. The player asked you to fill in the rest of the character: ");
            sb.Append("every step below that is still open. They will review your picks before anything is saved.\n\n");
            sb.Append("Character so far: ").Append(DraftLine(draft)).Append('\n');
            if (answersSoFar != null && answersSoFar.Count > 0)
            {
                sb.Append("\nCampaign answers (stay consistent with them):\n");
                foreach (var kv in answersSoFar) { sb.Append("- ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n'); }
            }
            sb.Append("\nOpen steps:\n");
            foreach (var t in targets) { AppendTarget(sb, t, draft); }
            sb.Append("\nReply with one to three sentences on the character your picks make (how they'll play, how they fit the campaign), ");
            sb.Append("then ONE JSON object in a ```json block, keyed by step key. Use ids exactly as listed, and only for the open steps above:\n");
            sb.Append("- a pick-one step: \"key\": \"id\"\n- a pick-several step: \"key\": [\"id\", ...] (only the new picks)\n");
            sb.Append("- spells: \"key\": {\"cantrips\": [...], \"known\": [...], \"prepared\": [...]} (only the new picks; prepared spells come from the known ones)\n");
            sb.Append("- level choices: \"key\": {\"<slot>\": \"id\"}, and for an ability score improvement either one ability id (+2), a list of two (+1 each), or one feat id\n");
            sb.Append("Don't narrate scenes.");
            return sb.ToString();
        }

        /// <summary>The user turn that asks for the fill (the chat shows it).</summary>
        public static string Instruction(IList<FillTarget> targets)
        {
            var titles = new List<string>();
            foreach (var t in targets) { titles.Add(t.Step.Title.ToLowerInvariant()); }
            return "Fill in the rest for me: " + string.Join(", ", titles.ToArray()) + ".";
        }

        /// <summary>
        /// Reads the reply's JSON against the targets. False with an error when there is no JSON object in it. Every pick
        /// is matched to the step's options (by id, else by name); one that isn't an option goes to Rejected.
        /// </summary>
        public static bool Parse(string reply, IList<FillTarget> targets, CharacterDraft draft, FillResult result, out string error)
        {
            error = null;
            string text = reply ?? string.Empty;
            int start, end;
            JsonValue json = FindJson(text, out start, out end);
            if (json == null || json.Kind != JsonKind.Object)
            {
                error = "The DM's reply had no picks in it (no JSON object). Ask again, or pick by hand.";
                return false;
            }
            result.Prose = (text.Substring(0, start) + text.Substring(end)).Trim();
            foreach (var t in targets)
            {
                JsonValue value = null;
                foreach (var kv in json.ObjectValue) { if (string.Equals(kv.Key, t.Step.Key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; } }
                if (value == null || value.IsNull) { continue; }
                switch (t.Step.Kind)
                {
                    case StepKinds.PickOne: FillOne(t, value, draft, result); break;
                    case StepKinds.Spells: FillSpells(t, value, draft, result); break;
                    case StepKinds.LevelChoices: FillLevels(t, value, draft, result); break;
                    default: FillMany(t, value, draft, result); break;
                }
            }
            return true;
        }

        private static void FillOne(FillTarget t, JsonValue value, CharacterDraft draft, FillResult result)
        {
            string id = Match(Strings(value), t.Options.Options, null, t, null, result);
            if (id != null) { result.Choices[t.Step.Key] = JsonValue.FromString(id); }
        }

        private static void FillMany(FillTarget t, JsonValue value, CharacterDraft draft, FillResult result)
        {
            var picks = draft.GetList(t.Step.Key);
            int count = Count(t.Step, t.Options);
            foreach (string id in MatchAll(Strings(value), t.Options.Options, null, t, null, result))
            {
                if (Contains(picks, id)) { continue; }
                if (count > 0 && picks.Count >= count) { Reject(result, t.Step.Key, id + " (one too many)"); continue; }
                picks.Add(id);
            }
            result.Choices[t.Step.Key] = CharacterDraft.StringArray(picks);
        }

        private static void FillSpells(FillTarget t, JsonValue value, CharacterDraft draft, FillResult result)
        {
            if (value.Kind != JsonKind.Object) { Reject(result, t.Step.Key, "spells not given as cantrips, known and prepared"); return; }
            var current = draft.Get(t.Step.Key);
            var lists = new Dictionary<string, List<string>>();
            foreach (string group in new[] { SpellGroups.Cantrips, SpellGroups.Known, SpellGroups.Prepared })
            {
                lists[group] = CharacterDraft.Strings(current.Get(group));
            }
            foreach (string group in new[] { SpellGroups.Cantrips, SpellGroups.Known, SpellGroups.Prepared })
            {
                int count = t.Options.GroupCount(group);
                // A caster with a list of its own (a wizard's spellbook) prepares from it; others prepare from the class list.
                string from = group == SpellGroups.Prepared ? SpellGroups.Known : group;
                var offered = t.Options.Options.FindAll(delegate (BuilderOption o) { return o.Group == from; });
                foreach (string id in MatchAll(Strings(value.Get(group)), offered, group, t, null, result))
                {
                    var list = lists[group];
                    if (Contains(list, id)) { continue; }
                    if (group == SpellGroups.Prepared && t.Options.GroupCount(SpellGroups.Known) > 0 && !Contains(lists[SpellGroups.Known], id))
                    {
                        Reject(result, t.Step.Key, id + " (prepared, but not among the known spells)");
                        continue;
                    }
                    // The prepared count follows the casting ability, which this same fill may raise: the server checks it.
                    if (group != SpellGroups.Prepared && list.Count >= count) { Reject(result, t.Step.Key, id + " (one " + group + " too many)"); continue; }
                    list.Add(id);
                }
            }
            var choice = JsonValue.NewObject();
            foreach (var kv in lists) { choice.ObjectValue[kv.Key] = CharacterDraft.StringArray(kv.Value); }
            result.Choices[t.Step.Key] = choice;
        }

        /// <summary>No pick yet, or fewer than a several-pick slot's count (an improvement is done with one).</summary>
        private static bool SlotOpen(LevelSlot slot, Dictionary<string, List<string>> picks)
        {
            List<string> had;
            if (!picks.TryGetValue(slot.Id, out had)) { return true; }
            return !slot.IsAsi && had.Count < slot.Picks;
        }

        private static void FillLevels(FillTarget t, JsonValue value, CharacterDraft draft, FillResult result)
        {
            if (value.Kind != JsonKind.Object) { Reject(result, t.Step.Key, "level choices not given per slot"); return; }
            var picks = LevelChoices.Picks(draft.Get(t.Step.Key));
            foreach (var kv in value.ObjectValue)
            {
                LevelSlot slot = t.Options.Slots.Find(delegate (LevelSlot s) { return string.Equals(s.Id, kv.Key, StringComparison.OrdinalIgnoreCase); });
                if (slot == null) { Reject(result, t.Step.Key, PastLevel(kv.Key, draft.Level)); continue; }
                if (!SlotOpen(slot, picks)) { continue; }
                List<string> had;
                if (!picks.TryGetValue(slot.Id, out had)) { had = new List<string>(); }
                var offered = t.Options.Options.FindAll(delegate (BuilderOption o) { return o.Group == slot.Id; });
                var ids = MatchAll(Strings(kv.Value), offered, null, t, slot, result);
                // A several-pick slot keeps the player's picks and takes the DM's new ones up to its count.
                ids = ids.FindAll(delegate (string id) { return !Contains(had, id); });
                if (ids.Count == 0) { continue; }
                int abilities = ids.FindAll(slot.IsAbility).Count;
                bool fits = slot.IsAsi ? (abilities == ids.Count ? ids.Count <= 2 : ids.Count == 1) : had.Count + ids.Count <= slot.Picks;
                if (!fits || (ids.Count == 2 && string.Equals(ids[0], ids[1], StringComparison.OrdinalIgnoreCase)))
                {
                    Reject(result, t.Step.Key, string.Join(" + ", ids.ToArray()) + " (" + slot.Title + ": " + (slot.IsAsi ? "one ability, two, or one feat" : slot.Picks == 1 ? "one pick" : slot.Picks + " picks") + ")");
                    continue;
                }
                var merged = new List<string>(had);
                merged.AddRange(ids);
                picks[slot.Id] = merged;
            }
            var choice = LevelChoices.ToJson(picks);
            if (choice != null) { result.Choices[t.Step.Key] = choice; }
        }

        /// <summary>"6.asiOrFeat" → "a level 6 pick (the character is level 5)"; any other unknown slot as written.</summary>
        private static string PastLevel(string slotId, int level)
        {
            int dot = slotId.IndexOf('.');
            int at;
            if (dot > 0 && int.TryParse(slotId.Substring(0, dot), out at) && at > level)
            {
                return "a level " + at + " pick (the character is level " + level + ")";
            }
            return slotId + " (not a choice for this character)";
        }

        private static string Match(List<string> ids, List<BuilderOption> offered, string group, FillTarget t, LevelSlot slot, FillResult result)
        {
            var matched = MatchAll(ids, offered, group, t, slot, result);
            return matched.Count > 0 ? matched[0] : null;
        }

        /// <summary>The ids that are options (in the options' spelling); each other one goes to Rejected, with the slot's title.</summary>
        private static List<string> MatchAll(List<string> ids, List<BuilderOption> offered, string group, FillTarget t, LevelSlot slot, FillResult result)
        {
            var valid = new List<string>();
            var notOptions = new List<string>();
            BuilderAdvisor.Match(ids, offered, valid, notOptions);
            foreach (string id in notOptions)
            {
                Reject(result, t.Step.Key, id + (slot != null ? " (" + slot.Title + ")" : group != null ? " (" + group + ")" : string.Empty));
            }
            return valid;
        }

        private static void Reject(FillResult result, string key, string what)
        {
            List<string> list;
            if (!result.Rejected.TryGetValue(key, out list)) { list = result.Rejected[key] = new List<string>(); }
            list.Add(what);
        }

        private static int Count(BuilderStep step, StepOptions options)
        {
            return options != null && options.Count > 0 ? options.Count : step.Count;
        }

        private static List<string> Strings(JsonValue v)
        {
            return CharacterDraft.Strings(v);
        }

        private static bool Contains(List<string> list, string id)
        {
            return list.Exists(delegate (string p) { return string.Equals(p, id, StringComparison.OrdinalIgnoreCase); });
        }

        /// <summary>The last ```json block, else the outermost braces; null when neither parses to an object.</summary>
        private static JsonValue FindJson(string text, out int start, out int end)
        {
            start = end = 0;
            int fence = text.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (fence >= 0)
            {
                int bodyStart = fence + 7;
                int close = text.IndexOf("```", bodyStart, StringComparison.Ordinal);
                int bodyEnd = close >= 0 ? close : text.Length;
                var parsed = TryParse(text.Substring(bodyStart, bodyEnd - bodyStart));
                if (parsed != null) { start = fence; end = close >= 0 ? close + 3 : text.Length; return parsed; }
            }
            int open = text.IndexOf('{');
            int last = text.LastIndexOf('}');
            if (open < 0 || last <= open) { return null; }
            var json = TryParse(text.Substring(open, last - open + 1));
            if (json != null) { start = open; end = last + 1; }
            return json;
        }

        private static JsonValue TryParse(string text)
        {
            try
            {
                var v = JsonValue.Parse(text.Trim());
                return v != null && v.Kind == JsonKind.Object ? v : null;
            }
            catch (Exception) { return null; }
        }

        private static void AppendTarget(StringBuilder sb, FillTarget t, CharacterDraft draft)
        {
            var step = t.Step;
            var opts = t.Options;
            sb.Append("\n## ").Append(step.Key).Append(" (").Append(step.Title).Append(")\n");
            switch (step.Kind)
            {
                case StepKinds.Spells:
                    var choice = draft.Get(step.Key);
                    foreach (string group in new[] { SpellGroups.Cantrips, SpellGroups.Known, SpellGroups.Prepared })
                    {
                        int count = opts.GroupCount(group);
                        if (count == 0) { continue; }
                        var had = CharacterDraft.Strings(choice.Get(group));
                        sb.Append(group).Append(": pick ").Append(Math.Max(0, count - had.Count)).Append(" more");
                        if (had.Count > 0) { sb.Append(" (already: ").Append(string.Join(", ", had.ToArray())).Append(')'); }
                        if (group == SpellGroups.Prepared) { sb.Append(opts.GroupCount(SpellGroups.Known) > 0 ? ", from the known spells" : ", from the spell list"); }
                        sb.Append('\n');
                    }
                    AppendOptions(sb, "Cantrips", opts.Options.FindAll(delegate (BuilderOption o) { return o.Group == SpellGroups.Cantrips; }));
                    AppendOptions(sb, "Spells", opts.Options.FindAll(delegate (BuilderOption o) { return o.Group == SpellGroups.Known; }));
                    break;
                case StepKinds.LevelChoices:
                    var picks = LevelChoices.Picks(draft.Get(step.Key));
                    foreach (var slot in opts.Slots)
                    {
                        if (!SlotOpen(slot, picks)) { continue; }
                        sb.Append("slot \"").Append(slot.Id).Append("\" (").Append(slot.Title).Append(")");
                        if (slot.IsAsi) { sb.Append(": one ability id for +2, two for +1 each, or one feat id"); }
                        else if (slot.Picks > 1)
                        {
                            List<string> had;
                            int left = slot.Picks - (picks.TryGetValue(slot.Id, out had) ? had.Count : 0);
                            sb.Append(": a list of ").Append(left).Append(" different option ids");
                            if (had != null && had.Count > 0) { sb.Append(" (already: ").Append(string.Join(", ", had.ToArray())).Append(')'); }
                        }
                        sb.Append('\n');
                        AppendOptions(sb, null, opts.Options.FindAll(delegate (BuilderOption o) { return o.Group == slot.Id; }));
                    }
                    break;
                case StepKinds.PickOne:
                    sb.Append("pick one\n");
                    AppendOptions(sb, null, opts.Options);
                    break;
                default:
                    int n = Count(step, opts);
                    var already = draft.GetList(step.Key);
                    sb.Append(n > 0 ? "pick " + Math.Max(0, n - already.Count) + " more" : "pick any");
                    if (already.Count > 0) { sb.Append(" (already: ").Append(string.Join(", ", already.ToArray())).Append(')'); }
                    sb.Append('\n');
                    AppendOptions(sb, null, opts.Options);
                    break;
            }
        }

        private static void AppendOptions(StringBuilder sb, string heading, List<BuilderOption> options)
        {
            if (options.Count == 0) { return; }
            if (heading != null) { sb.Append(heading).Append(":\n"); }
            for (int i = 0; i < options.Count && i < MaxPromptOptions; i++)
            {
                var o = options[i];
                sb.Append("- ").Append(o.Id);
                if (!string.Equals(o.Label, o.Id, StringComparison.OrdinalIgnoreCase)) { sb.Append(" — ").Append(o.Label); }
                sb.Append('\n');
            }
            if (options.Count > MaxPromptOptions) { sb.Append("(and ").Append(options.Count - MaxPromptOptions).Append(" more)\n"); }
        }

        private static string DraftLine(CharacterDraft draft)
        {
            var parts = new List<string>();
            if (draft.Name.Length > 0) { parts.Add("name " + draft.Name); }
            parts.Add("level " + draft.Level);
            foreach (var kv in draft.Choices) { parts.Add(kv.Key + " " + kv.Value.ToJson()); }
            if (draft.Concept.Length > 0) { parts.Add("concept: " + draft.Concept); }
            return string.Join("; ", parts.ToArray());
        }

        private static string SystemName(string system)
        {
            switch ((system ?? string.Empty).ToLowerInvariant())
            {
                case "dnd5e": return "D&D 5e";
                case "pf2e": return "Pathfinder 2e";
                case "": return "tabletop";
                default: return system;
            }
        }
    }
}
