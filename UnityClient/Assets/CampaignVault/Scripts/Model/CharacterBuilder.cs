using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Model
{
    /// <summary>
    /// The server's builder step kinds (CreationStepKinds). The client draws one widget per kind, so a new system or
    /// a plugin's recipe needs no client work; a kind this client doesn't know gets a "not supported yet" card.
    /// </summary>
    public static class StepKinds
    {
        public const string PickOne = "pickOne";
        public const string PickN = "pickN";
        public const string AbilityScores = "abilityScores";
        public const string Allocate = "allocate";
        public const string Spells = "spells";
        public const string Feats = "feats";
        public const string Identity = "identity";
        public const string LevelChoices = "levelChoices";

        /// <summary>The kinds this client draws a widget for.</summary>
        public static readonly HashSet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
        {
            PickOne, PickN, AbilityScores, Allocate, Spells, Feats, Identity, LevelChoices,
        };
    }

    /// <summary>
    /// One choice of a levelChoices step (the server's LevelChoiceSlot): a class's choice at a level up to the draft's,
    /// "2.subclass" or "4.asiOrFeat". Its options are the step's options whose group is its id.
    /// </summary>
    public sealed class LevelSlot
    {
        public string Id = string.Empty;
        public int Level;
        public string Key = string.Empty;
        /// <summary>"Level 2 · Arcane Tradition".</summary>
        public string Title = string.Empty;
        /// <summary>Enum, AsiOrFeat, FeatSelection or FreeText.</summary>
        public string Type = string.Empty;
        public bool Required;
        public int Picks = 1;
        /// <summary>An ability score improvement's ability options; its other options are feats.</summary>
        public readonly List<string> Abilities = new List<string>();

        public bool IsAsi { get { return Type == "AsiOrFeat"; } }

        public bool IsAbility(string id)
        {
            return Abilities.Exists(delegate (string a) { return string.Equals(a, id, StringComparison.OrdinalIgnoreCase); });
        }

        public static LevelSlot Parse(JsonValue v)
        {
            var slot = new LevelSlot
            {
                Id = v.GetString("id", string.Empty),
                Level = (int)v.GetNumber("level", 0),
                Key = v.GetString("key", string.Empty),
                Title = v.GetString("title", string.Empty),
                Type = v.GetString("type", string.Empty),
                Required = v.Get("required").Kind == JsonKind.Bool && v.Get("required").BoolValue,
                Picks = Math.Max(1, (int)v.GetNumber("picks", 1)),
            };
            foreach (var a in v.GetArray("abilities")) { if (a.Kind == JsonKind.String) { slot.Abilities.Add(a.StringValue); } }
            if (slot.Title.Length == 0) { slot.Title = slot.Id; }
            return slot;
        }
    }

    /// <summary>
    /// A levelChoices step's choice: an object of slot id → an option id, or a list (an ability score improvement: one
    /// ability for +2, two for +1 each, or one feat instead).
    /// </summary>
    public static class LevelChoices
    {
        /// <summary>Slot id → its picks (a string counts as one).</summary>
        public static Dictionary<string, List<string>> Picks(JsonValue choice)
        {
            var picks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (choice == null || choice.Kind != JsonKind.Object) { return picks; }
            foreach (var kv in choice.ObjectValue)
            {
                var list = CharacterDraft.Strings(kv.Value);
                if (list.Count > 0) { picks[kv.Key] = list; }
            }
            return picks;
        }

        public static JsonValue ToJson(Dictionary<string, List<string>> picks)
        {
            if (picks.Count == 0) { return null; }
            var value = JsonValue.NewObject();
            foreach (var kv in picks)
            {
                value.ObjectValue[kv.Key] = kv.Value.Count == 1 ? JsonValue.FromString(kv.Value[0]) : CharacterDraft.StringArray(kv.Value);
            }
            return value;
        }

        /// <summary>
        /// The choice after tapping an option of a slot. One pick: it becomes the pick. Several (two metamagic options): it
        /// goes in or out, up to the slot's count. An improvement: an ability goes in
        /// or out (two at most, each +1; one alone is +2), and a feat replaces the abilities (tapped again, it goes).
        /// </summary>
        public static JsonValue Toggle(JsonValue choice, LevelSlot slot, string optionId)
        {
            var picks = Picks(choice);
            List<string> current;
            if (!picks.TryGetValue(slot.Id, out current)) { current = new List<string>(); }
            int at = current.FindIndex(delegate (string p) { return string.Equals(p, optionId, StringComparison.OrdinalIgnoreCase); });
            var next = new List<string>();
            if (!slot.IsAsi && slot.Picks <= 1) { next.Add(optionId); }
            else if (!slot.IsAsi)
            {
                next.AddRange(current);
                if (at >= 0) { next.RemoveAt(at); }
                else if (next.Count >= slot.Picks) { return choice; }
                else { next.Add(optionId); }
            }
            else if (at >= 0) { next.AddRange(current); next.RemoveAt(at); }
            else if (!slot.IsAbility(optionId)) { next.Add(optionId); }
            else
            {
                foreach (string p in current) { if (slot.IsAbility(p)) { next.Add(p); } }
                if (next.Count >= 2) { return choice; }
                next.Add(optionId);
            }
            if (next.Count == 0) { picks.Remove(slot.Id); } else { picks[slot.Id] = next; }
            return ToJson(picks);
        }

        /// <summary>The choice without picks for levels above <paramref name="level"/> (the level went down).</summary>
        public static JsonValue Prune(JsonValue choice, int level)
        {
            var picks = Picks(choice);
            foreach (string id in new List<string>(picks.Keys))
            {
                int dot = id.IndexOf('.');
                int at;
                if (dot > 0 && int.TryParse(id.Substring(0, dot), out at) && at > level) { picks.Remove(id); }
            }
            return ToJson(picks);
        }

        /// <summary>"+2 INTELLIGENCE", "+1 STRENGTH · +1 CONSTITUTION", the feat's or option's name, or "NOT CHOSEN".</summary>
        public static string Summary(LevelSlot slot, List<string> picked, IList<BuilderOption> options)
        {
            if (picked == null || picked.Count == 0) { return "NOT CHOSEN"; }
            if (slot.IsAsi && slot.IsAbility(picked[0]))
            {
                if (picked.Count == 1) { return "+2 " + picked[0].ToUpperInvariant(); }
                var parts = new List<string>();
                foreach (string p in picked) { parts.Add("+1 " + p.ToUpperInvariant()); }
                return string.Join(" · ", parts.ToArray());
            }
            var labels = new List<string>();
            foreach (string p in picked)
            {
                string label = p;
                if (options != null) { foreach (var o in options) { if (string.Equals(o.Id, p, StringComparison.OrdinalIgnoreCase) && o.Group == slot.Id) { label = o.Label; } } }
                labels.Add(label.ToUpperInvariant());
            }
            return (slot.IsAsi ? "FEAT: " : string.Empty) + string.Join(", ", labels.ToArray());
        }
    }

    /// <summary>The parts of a spells step's choice, and its option groups.</summary>
    public static class SpellGroups
    {
        public const string Cantrips = "cantrips";
        public const string Known = "known";
        public const string Prepared = "prepared";
    }

    public sealed class PointBuyRules
    {
        public int Budget;
        public int Min = 8;
        public int Max = 15;
        /// <summary>Cumulative cost of each score from Min.</summary>
        public readonly Dictionary<int, int> Cost = new Dictionary<int, int>();

        /// <summary>What a score costs, or -1 when it's outside the table.</summary>
        public int CostOf(int score)
        {
            int cost;
            return Cost.TryGetValue(score, out cost) ? cost : -1;
        }

        public int Spent(IEnumerable<int> scores)
        {
            int spent = 0;
            foreach (int s in scores) { spent += Math.Max(0, CostOf(s)); }
            return spent;
        }
    }

    /// <summary>One step of the campaign system's creation recipe, as character_builder action=steps lists it.</summary>
    public sealed class BuilderStep
    {
        public string Key = string.Empty;
        public string Kind = string.Empty;
        public string Prompt = string.Empty;
        public string Source = string.Empty;
        /// <summary>A fixed pick count, or -1.</summary>
        public int Count = -1;
        public string CountFrom = string.Empty;
        public string Exclude = string.Empty;
        public string When = string.Empty;
        public bool Optional;
        public string Schema = string.Empty;
        /// <summary>The steps this one's choice depends on, as the server says (the steps result's "reads"); null when it didn't.</summary>
        public List<string> Reads;
        public readonly List<int> StandardArray = new List<int>();
        public PointBuyRules PointBuy;
        public string Roll = string.Empty;

        public string Title { get { return Prompt.Length > 0 ? Prompt : PrettyKey(Key); } }

        public static BuilderStep Parse(JsonValue v)
        {
            var step = new BuilderStep
            {
                Key = v.GetString("key", string.Empty),
                Kind = v.GetString("kind", string.Empty),
                Prompt = v.GetString("prompt", string.Empty),
                Source = v.GetString("source", string.Empty),
                Count = (int)v.GetNumber("count", -1),
                CountFrom = v.GetString("countFrom", string.Empty),
                Exclude = v.GetString("exclude", string.Empty),
                When = v.GetString("when", string.Empty),
                Optional = v.GetBool("optional", false),
                Schema = v.GetString("schema", string.Empty),
            };
            var methods = v.Get("methods");
            if (methods.Kind == JsonKind.Object)
            {
                foreach (var n in methods.GetArray("standardArray"))
                {
                    if (n.Kind == JsonKind.Number) { step.StandardArray.Add((int)n.NumberValue); }
                }
                var buy = methods.Get("pointBuy");
                if (buy.Kind == JsonKind.Object)
                {
                    step.PointBuy = new PointBuyRules
                    {
                        Budget = (int)buy.GetNumber("budget", 0),
                        Min = (int)buy.GetNumber("min", 8),
                        Max = (int)buy.GetNumber("max", 15),
                    };
                    var cost = buy.Get("cost");
                    if (cost.Kind == JsonKind.Object)
                    {
                        foreach (var kv in cost.ObjectValue)
                        {
                            int score;
                            if (kv.Value.Kind == JsonKind.Number && int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out score))
                            {
                                step.PointBuy.Cost[score] = (int)kv.Value.NumberValue;
                            }
                        }
                    }
                }
                step.Roll = methods.GetString("roll", string.Empty);
            }
            return step;
        }

        /// <summary>"spells" → "Spells", "deity_choice" → "Deity choice".</summary>
        public static string PrettyKey(string key)
        {
            string text = Regex.Replace(key ?? string.Empty, "([a-z])([A-Z])", "$1 $2").Replace('_', ' ').Replace('-', ' ').Trim().ToLowerInvariant();
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }
    }

    public sealed class BuilderOption
    {
        public string Id = string.Empty;
        public string Label = string.Empty;
        public string Description = string.Empty;
        public string Group = string.Empty;
        /// <summary>Not from the shipped free-licensed rules (a plugin's or the campaign's): shown with a "homebrew" tag.</summary>
        public bool Homebrew;
        /// <summary>A template's field values (a companion archetype's stat block), copied into the draft when picked.</summary>
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();

        public static BuilderOption Parse(JsonValue v)
        {
            string id = v.GetString("id", string.Empty);
            var option = new BuilderOption
            {
                Id = id,
                Label = v.GetString("label", id),
                Description = v.GetString("description", string.Empty),
                Group = v.GetString("group", string.Empty),
                Homebrew = v.GetBool("homebrew", false),
            };
            var values = v.Get("values");
            if (values.Kind == JsonKind.Object)
            {
                foreach (var kv in values.ObjectValue) { option.Values[kv.Key] = kv.Value.Kind == JsonKind.String ? kv.Value.StringValue : kv.Value.ToJson(); }
            }
            return option;
        }
    }

    /// <summary>A problem with the draft, tied to the step it belongs to.</summary>
    public sealed class BuilderIssue
    {
        public string Step = string.Empty;
        public string Message = string.Empty;
        public bool Warning;

        public static BuilderIssue Parse(JsonValue v)
        {
            return new BuilderIssue
            {
                Step = v.GetString("step", string.Empty),
                Message = v.GetString("message", string.Empty),
                Warning = v.GetBool("isWarning", false),
            };
        }
    }

    public sealed class StatBlockField
    {
        /// <summary>A choice field: one name from <see cref="Keys"/> (a creature type).</summary>
        public const string ChoiceType = "choice";

        public string Key = string.Empty;
        public string Label = string.Empty;
        /// <summary>
        /// int, text, modifiers (an object of name → whole number, its names from <see cref="Keys"/>), rows (a list of
        /// objects, one per row, its fields from <see cref="Columns"/>), choice, or list (short entries written as one
        /// text, separated by commas; the server splits it).
        /// </summary>
        public string Type = "text";
        /// <summary>modifiers only: the names it may use (the system's skills), in the server's spelling.</summary>
        public readonly List<string> Keys = new List<string>();
        public int Min = int.MinValue;
        public int Max = int.MaxValue;
        public string Group = string.Empty;
        /// <summary>The draft can't commit without it.</summary>
        public bool Required;
        /// <summary>What to write, shown in the empty field; empty means the type or range says it.</summary>
        public string Hint = string.Empty;
        /// <summary>Drawn as a narrow box beside its neighbours: numbers, and short text the schema marks (a challenge rating).</summary>
        public bool Compact;
        /// <summary>rows only: what each row holds, in order; the first is its name.</summary>
        public readonly List<StatBlockColumn> Columns = new List<StatBlockColumn>();
        /// <summary>rows only: what one row is called ("attack").</summary>
        public string Item = string.Empty;
    }

    /// <summary>One column of a rows field: text, int (a whole number in range), or dice (damage, like "1d6+2 piercing").</summary>
    public sealed class StatBlockColumn
    {
        public string Key = string.Empty;
        public string Label = string.Empty;
        public string Type = "text";
        public int Min = int.MinValue;
        public int Max = int.MaxValue;
        public bool Required;
    }

    /// <summary>
    /// A modifiers field's value (a companion's skills): a JSON object of name → whole number. Templates carry it as
    /// text ("Perception +5, Stealth +6"); names are matched to the field's spelling, and anything unreadable is kept
    /// as written for the server's preview to flag on the field.
    /// </summary>
    public static class StatModifiers
    {
        public const string Type = "modifiers";

        /// <summary>The field's spelling of a name ("perception" → "Perception"), or the name as given.</summary>
        public static string Canonical(StatBlockField field, string name)
        {
            name = (name ?? string.Empty).Trim();
            if (field != null) { foreach (var k in field.Keys) { if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { return k; } } }
            return name;
        }

        /// <summary>"Nature +4, Perception +5" (commas or semicolons) as an object; a part with no number keeps its text.</summary>
        public static JsonValue FromText(StatBlockField field, string text)
        {
            var value = JsonValue.NewObject();
            foreach (var raw in (text ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                if (part.Length == 0) { continue; }
                int cut = part.LastIndexOf(' ');
                int n;
                if (cut > 0 && int.TryParse(part.Substring(cut + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
                {
                    value.ObjectValue[Canonical(field, part.Substring(0, cut))] = JsonValue.FromNumber(n);
                }
                else { value.ObjectValue[Canonical(field, part)] = JsonValue.FromString(part); }
            }
            return value;
        }

        /// <summary>An object (names matched, "+4" strings read as numbers) or the template text form; anything else as it came.</summary>
        public static JsonValue From(StatBlockField field, JsonValue v)
        {
            if (v.Kind == JsonKind.String) { return FromText(field, v.StringValue); }
            if (v.Kind != JsonKind.Object) { return v; }
            var value = JsonValue.NewObject();
            foreach (var kv in v.ObjectValue)
            {
                int n;
                var entry = kv.Value;
                if (entry.Kind == JsonKind.Number) { entry = JsonValue.FromNumber(Math.Round(entry.NumberValue)); }
                else if (entry.Kind == JsonKind.String && int.TryParse(entry.StringValue.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) { entry = JsonValue.FromNumber(n); }
                value.ObjectValue[Canonical(field, kv.Key)] = entry;
            }
            return value;
        }

        /// <summary>"+5", "-1", "0": how a bonus is written on a stat block.</summary>
        public static string Signed(int n) { return n > 0 ? "+" + n : n.ToString(CultureInfo.InvariantCulture); }
    }

    /// <summary>
    /// A rows field's value (a companion's attacks): a JSON list of objects keyed by the field's columns. Templates carry it
    /// as text, rows split by ";" and columns by "," in column order ("Bite +3, 1d6+1 piercing, reach 5 ft."), the same
    /// form the server reads; leftover parts go to the last column.
    /// </summary>
    public static class StatRows
    {
        public const string Type = "rows";

        public static JsonValue FromText(StatBlockField field, string text)
        {
            var value = JsonValue.NewArray();
            var columns = field.Columns;
            if (columns.Count == 0) { return JsonValue.FromString(text ?? string.Empty); }
            foreach (var raw in (text ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                if (part.Length == 0) { continue; }
                var cells = new List<string>();
                foreach (var c in part.Split(',')) { cells.Add(c.Trim()); }
                int n;
                if (columns.Count > 1 && columns[1].Type == "int")
                {
                    int cut = cells[0].LastIndexOf(' ');
                    if (cut > 0 && int.TryParse(cells[0].Substring(cut + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
                    {
                        cells.Insert(1, cells[0].Substring(cut + 1));
                        cells[0] = cells[0].Substring(0, cut).Trim();
                    }
                }
                if (cells.Count > columns.Count)
                {
                    string rest = string.Join(", ", cells.GetRange(columns.Count - 1, cells.Count - columns.Count + 1).ToArray());
                    cells.RemoveRange(columns.Count - 1, cells.Count - columns.Count + 1);
                    cells.Add(rest);
                }
                var row = JsonValue.NewObject();
                for (int i = 0; i < cells.Count; i++)
                {
                    if (cells[i].Length > 0) { row.ObjectValue[columns[i].Key] = Cell(columns[i], cells[i]); }
                }
                value.ArrayValue.Add(row);
            }
            return value;
        }

        /// <summary>A list (each row's whole-number columns read from "+3" text) or the text form; anything else as it came.</summary>
        public static JsonValue From(StatBlockField field, JsonValue v)
        {
            if (v.Kind == JsonKind.String) { return FromText(field, v.StringValue); }
            if (v.Kind != JsonKind.Array) { return v; }
            var value = JsonValue.NewArray();
            foreach (var r in v.ArrayValue)
            {
                if (r.Kind != JsonKind.Object) { value.ArrayValue.Add(r); continue; }
                var row = JsonValue.NewObject();
                foreach (var kv in r.ObjectValue)
                {
                    var column = field.Columns.Find(delegate (StatBlockColumn c) { return string.Equals(c.Key, kv.Key, StringComparison.OrdinalIgnoreCase); });
                    row.ObjectValue[column != null ? column.Key : kv.Key] = column != null && kv.Value.Kind == JsonKind.String ? Cell(column, kv.Value.StringValue) : kv.Value;
                }
                value.ArrayValue.Add(row);
            }
            return value;
        }

        /// <summary>A cell as typed: a whole-number column's "+3" as 3, anything else (or unreadable) as text for the preview to flag.</summary>
        public static JsonValue Cell(StatBlockColumn column, string text)
        {
            text = (text ?? string.Empty).Trim();
            int n;
            if (column.Type == "int" && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) { return JsonValue.FromNumber(n); }
            return JsonValue.FromString(text);
        }

        /// <summary>How a cell shows in its box: a whole-number column signed ("+3").</summary>
        public static string Show(StatBlockColumn column, JsonValue v)
        {
            if (v.Kind == JsonKind.Number) { return column.Type == "int" ? StatModifiers.Signed((int)v.NumberValue) : ((int)v.NumberValue).ToString(CultureInfo.InvariantCulture); }
            return v.Kind == JsonKind.String ? v.StringValue : string.Empty;
        }
    }

    /// <summary>A stat block's fields (RulesetData/&lt;system&gt;/statblocks), for an identity step that names a schema.</summary>
    public sealed class StatBlockSchema
    {
        public string Name = string.Empty;
        /// <summary>The editor's caption: "Stat block" unless the schema names one (Narrative's "Nature").</summary>
        public string Title = "Stat block";
        public readonly List<StatBlockField> Fields = new List<StatBlockField>();

        public static StatBlockSchema Parse(JsonValue v)
        {
            var schema = new StatBlockSchema { Name = v.GetString("name", string.Empty) };
            string title = v.GetString("title", string.Empty);
            if (title.Length > 0) { schema.Title = title; }
            foreach (var f in v.GetArray("fields"))
            {
                string key = f.GetString("key", string.Empty);
                if (key.Length == 0) { continue; }
                var field = new StatBlockField
                {
                    Key = key,
                    Label = f.GetString("label", BuilderStep.PrettyKey(key)),
                    Type = f.GetString("type", "text"),
                    Min = f.Get("min").Kind == JsonKind.Number ? (int)f.GetNumber("min", 0) : int.MinValue,
                    Max = f.Get("max").Kind == JsonKind.Number ? (int)f.GetNumber("max", 0) : int.MaxValue,
                    Group = f.GetString("group", string.Empty),
                    Required = f.GetBool("required", false),
                    Hint = f.GetString("hint", string.Empty),
                };
                field.Compact = field.Type == "int" || f.GetBool("compact", false);
                field.Item = f.GetString("item", string.Empty);
                foreach (var c in f.GetArray("columns"))
                {
                    string ck = c.GetString("key", string.Empty);
                    if (ck.Length == 0) { continue; }
                    field.Columns.Add(new StatBlockColumn
                    {
                        Key = ck,
                        Label = c.GetString("label", BuilderStep.PrettyKey(ck)),
                        Type = c.GetString("type", "text"),
                        Min = c.Get("min").Kind == JsonKind.Number ? (int)c.GetNumber("min", 0) : int.MinValue,
                        Max = c.Get("max").Kind == JsonKind.Number ? (int)c.GetNumber("max", 0) : int.MaxValue,
                        Required = c.GetBool("required", false),
                    });
                }
                foreach (var k in f.GetArray("keys")) { if (k.Kind == JsonKind.String && k.StringValue.Length > 0) { field.Keys.Add(k.StringValue); } }
                schema.Fields.Add(field);
            }
            return schema;
        }
    }

    /// <summary>
    /// The character the player is building, sent whole on every character_builder call. Choices are keyed by recipe
    /// step key: a string (pickOne), a string list (pickN, feats, allocate), or an object (abilityScores, spells,
    /// identity with a stat block).
    /// </summary>
    public sealed class CharacterDraft
    {
        public string Id = string.Empty;
        public string Kind = "pc";
        public string System = string.Empty;
        public int Level = 1;
        /// <summary>A companion's party: the level of the player characters, for the server's power check. 0 means none.</summary>
        public int PartyLevel;
        public string Name = string.Empty;
        public string Concept = string.Empty;
        public string Look = string.Empty;
        public readonly Dictionary<string, JsonValue> Choices = new Dictionary<string, JsonValue>(StringComparer.OrdinalIgnoreCase);

        public JsonValue ToJson()
        {
            var o = JsonValue.NewObject();
            if (Id.Length > 0) { o.ObjectValue["id"] = JsonValue.FromString(Id); }
            o.ObjectValue["kind"] = JsonValue.FromString(Kind);
            if (System.Length > 0) { o.ObjectValue["system"] = JsonValue.FromString(System); }
            o.ObjectValue["level"] = JsonValue.FromNumber(Level);
            if (PartyLevel > 0) { o.ObjectValue["partyLevel"] = JsonValue.FromNumber(PartyLevel); }
            if (Name.Length > 0) { o.ObjectValue["name"] = JsonValue.FromString(Name); }
            if (Concept.Length > 0) { o.ObjectValue["concept"] = JsonValue.FromString(Concept); }
            if (Look.Length > 0) { o.ObjectValue["look"] = JsonValue.FromString(Look); }
            var choices = JsonValue.NewObject();
            foreach (var kv in Choices) { choices.ObjectValue[kv.Key] = kv.Value; }
            o.ObjectValue["choices"] = choices;
            return o;
        }

        public static CharacterDraft FromJson(JsonValue v)
        {
            var draft = new CharacterDraft
            {
                Id = v.GetString("id", string.Empty),
                Kind = v.GetString("kind", "pc"),
                System = v.GetString("system", string.Empty),
                Level = Math.Max(1, (int)v.GetNumber("level", 1)),
                PartyLevel = Math.Max(0, (int)v.GetNumber("partyLevel", 0)),
                Name = v.GetString("name", string.Empty),
                Concept = v.GetString("concept", string.Empty),
                Look = v.GetString("look", string.Empty),
            };
            var choices = v.Get("choices");
            if (choices.Kind == JsonKind.Object)
            {
                foreach (var kv in choices.ObjectValue) { draft.Choices[kv.Key] = kv.Value; }
            }
            return draft;
        }

        public bool Has(string key)
        {
            JsonValue v;
            if (!Choices.TryGetValue(key, out v) || v == null) { return false; }
            switch (v.Kind)
            {
                case JsonKind.Null: return false;
                case JsonKind.String: return v.StringValue.Length > 0;
                case JsonKind.Array: return v.ArrayValue.Count > 0;
                default: return true;
            }
        }

        public JsonValue Get(string key)
        {
            JsonValue v;
            return Choices.TryGetValue(key, out v) && v != null ? v : JsonValue.Null;
        }

        public string GetString(string key)
        {
            var v = Get(key);
            return v.Kind == JsonKind.String ? v.StringValue : string.Empty;
        }

        /// <summary>The choice as a string list; a single string counts as one.</summary>
        public List<string> GetList(string key)
        {
            return Strings(Get(key));
        }

        /// <summary>Sets a choice; null or an empty value removes it. True when it changed.</summary>
        public bool Set(string key, JsonValue value)
        {
            string before = Get(key).ToJson();
            if (value == null || value.IsNull || (value.Kind == JsonKind.String && value.StringValue.Length == 0)
                || (value.Kind == JsonKind.Array && value.ArrayValue.Count == 0))
            {
                Choices.Remove(key);
            }
            else
            {
                Choices[key] = value;
            }
            return before != Get(key).ToJson();
        }

        public static JsonValue StringArray(IEnumerable<string> items)
        {
            var a = JsonValue.NewArray();
            foreach (string s in items) { a.ArrayValue.Add(JsonValue.FromString(s)); }
            return a;
        }

        public static List<string> Strings(JsonValue v)
        {
            var list = new List<string>();
            if (v == null) { return list; }
            if (v.Kind == JsonKind.String && v.StringValue.Length > 0) { list.Add(v.StringValue); }
            if (v.Kind == JsonKind.Array)
            {
                foreach (var e in v.ArrayValue) { if (e.Kind == JsonKind.String) { list.Add(e.StringValue); } }
            }
            return list;
        }
    }

    /// <summary>
    /// Which steps read which. The server says, per step (a heritage reads the ancestry, trained skills the class,
    /// background and boosts); without that, a step's countFrom/exclude/when paths name the step they read by their first
    /// segment ("class.skillChoices.count" reads class), and class-driven sources (classSkills, spells) read the class step.
    /// Changing a step clears the choices of the steps that read it, so a wizard's spells never ride along onto a fighter.
    /// </summary>
    public static class BuilderDependencies
    {
        public const string ClassKey = "class";

        public static List<string> Reads(BuilderStep step)
        {
            if (step.Reads != null)
            {
                var told = new List<string>(step.Reads);
                told.Remove(step.Key);
                return told;
            }
            var keys = new List<string>();
            foreach (string path in new[] { step.CountFrom, step.Exclude, PathOf(step.When) }) { AddHead(keys, path); }
            if (step.Kind == StepKinds.Spells || step.Kind == StepKinds.LevelChoices || string.Equals(step.Source, "classSkills", StringComparison.OrdinalIgnoreCase)
                || string.Equals(step.Source, "spells", StringComparison.OrdinalIgnoreCase))
            {
                AddHead(keys, ClassKey);
            }
            keys.Remove(step.Key);
            return keys;
        }

        /// <summary>Every step that reads key, directly or through another step, in recipe order.</summary>
        public static List<string> Dependents(string key, IList<BuilderStep> steps)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var frontier = new Queue<string>();
            frontier.Enqueue(key);
            while (frontier.Count > 0)
            {
                string next = frontier.Dequeue();
                foreach (var step in steps)
                {
                    if (found.Contains(step.Key) || string.Equals(step.Key, key, StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (Reads(step).Exists(delegate (string r) { return string.Equals(r, next, StringComparison.OrdinalIgnoreCase); }))
                    {
                        found.Add(step.Key);
                        frontier.Enqueue(step.Key);
                    }
                }
            }
            var ordered = new List<string>();
            foreach (var step in steps) { if (found.Contains(step.Key)) { ordered.Add(step.Key); } }
            return ordered;
        }

        /// <summary>Clears the choices of every step that reads key; returns the steps that lost one, in recipe order.</summary>
        public static List<BuilderStep> ClearDependents(CharacterDraft draft, string key, IList<BuilderStep> steps)
        {
            var cleared = new List<BuilderStep>();
            foreach (string dependent in Dependents(key, steps))
            {
                if (!draft.Has(dependent)) { continue; }
                draft.Choices.Remove(dependent);
                cleared.Add(steps[IndexOf(steps, dependent)]);
            }
            return cleared;
        }

        /// <summary>"Changing class cleared: skills, spells." Empty when nothing was cleared.</summary>
        public static string ClearedNote(BuilderStep changed, IList<BuilderStep> cleared)
        {
            if (cleared == null || cleared.Count == 0) { return string.Empty; }
            var titles = new List<string>();
            foreach (var step in cleared) { titles.Add(step.Title.ToLowerInvariant()); }
            return "Changing " + changed.Title.ToLowerInvariant() + " cleared: " + string.Join(", ", titles.ToArray()) + ".";
        }

        public static int IndexOf(IList<BuilderStep> steps, string key)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (string.Equals(steps[i].Key, key, StringComparison.OrdinalIgnoreCase)) { return i; }
            }
            return -1;
        }

        /// <summary>"class.casterType != None" → "class.casterType".</summary>
        private static string PathOf(string when)
        {
            if (string.IsNullOrEmpty(when)) { return string.Empty; }
            int op = when.IndexOf("==", StringComparison.Ordinal);
            if (op < 0) { op = when.IndexOf("!=", StringComparison.Ordinal); }
            return (op < 0 ? when : when.Substring(0, op)).Trim();
        }

        private static void AddHead(List<string> keys, string path)
        {
            if (string.IsNullOrEmpty(path)) { return; }
            int dot = path.IndexOf('.');
            string head = (dot < 0 ? path : path.Substring(0, dot)).Trim();
            if (head.Length > 0 && !keys.Contains(head)) { keys.Add(head); }
        }
    }

    /// <summary>Dice for the roll method: "4d6dropLowest" and plain "NdM".</summary>
    public static class AbilityDice
    {
        private static readonly Regex Expression = new Regex(@"^\s*(\d+)d(\d+)\s*(dropLowest)?\s*$", RegexOptions.IgnoreCase);

        public static bool IsValid(string expression) { return Expression.IsMatch(expression ?? string.Empty); }

        /// <summary>One score: its total and a log line such as "4d6 drop lowest: 6 5 3 (1) = 14".</summary>
        public static int Roll(string expression, Random random, out string log)
        {
            var m = Expression.Match(expression ?? string.Empty);
            if (!m.Success) { log = "Can't roll '" + expression + "'."; return 0; }
            int count = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int sides = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            bool drop = m.Groups[3].Success && count > 1;
            var dice = new List<int>();
            for (int i = 0; i < count; i++) { dice.Add(random.Next(1, sides + 1)); }
            dice.Sort(delegate (int a, int b) { return b.CompareTo(a); });
            int total = 0;
            var parts = new List<string>();
            for (int i = 0; i < dice.Count; i++)
            {
                bool dropped = drop && i == dice.Count - 1;
                if (!dropped) { total += dice[i]; }
                parts.Add(dropped ? "(" + dice[i] + ")" : dice[i].ToString(CultureInfo.InvariantCulture));
            }
            log = count + "d" + sides + (drop ? " drop lowest" : string.Empty) + ": " + string.Join(" ", parts.ToArray()) + " = " + total;
            return total;
        }
    }
}
