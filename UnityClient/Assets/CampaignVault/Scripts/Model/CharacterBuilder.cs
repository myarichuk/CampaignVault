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
            PickOne, PickN, AbilityScores, Allocate, Spells, Feats, Identity,
        };
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

        public static BuilderOption Parse(JsonValue v)
        {
            string id = v.GetString("id", string.Empty);
            return new BuilderOption
            {
                Id = id,
                Label = v.GetString("label", id),
                Description = v.GetString("description", string.Empty),
                Group = v.GetString("group", string.Empty),
            };
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
        public string Key = string.Empty;
        public string Label = string.Empty;
        /// <summary>int, text, list, abilities or attacks.</summary>
        public string Type = "text";
        public int Min = int.MinValue;
        public int Max = int.MaxValue;
        public string Group = string.Empty;
    }

    /// <summary>A stat block's fields (RulesetData/&lt;system&gt;/statblocks), for an identity step that names a schema.</summary>
    public sealed class StatBlockSchema
    {
        public string Name = string.Empty;
        public readonly List<StatBlockField> Fields = new List<StatBlockField>();

        public static StatBlockSchema Parse(JsonValue v)
        {
            var schema = new StatBlockSchema { Name = v.GetString("name", string.Empty) };
            foreach (var f in v.GetArray("fields"))
            {
                string key = f.GetString("key", string.Empty);
                if (key.Length == 0) { continue; }
                schema.Fields.Add(new StatBlockField
                {
                    Key = key,
                    Label = f.GetString("label", BuilderStep.PrettyKey(key)),
                    Type = f.GetString("type", "text"),
                    Min = f.Get("min").Kind == JsonKind.Number ? (int)f.GetNumber("min", 0) : int.MinValue,
                    Max = f.Get("max").Kind == JsonKind.Number ? (int)f.GetNumber("max", 0) : int.MaxValue,
                    Group = f.GetString("group", string.Empty),
                });
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
    /// Which steps read which: a step's countFrom/exclude/when paths name the step they read by their first segment
    /// ("class.skillChoices.count" reads class), and class-driven sources (classSkills, spells) read the class step.
    /// Changing a step clears the choices of the steps that read it, so a wizard's spells never ride along onto a fighter.
    /// </summary>
    public static class BuilderDependencies
    {
        public const string ClassKey = "class";

        public static List<string> Reads(BuilderStep step)
        {
            var keys = new List<string>();
            foreach (string path in new[] { step.CountFrom, step.Exclude, PathOf(step.When) }) { AddHead(keys, path); }
            if (step.Kind == StepKinds.Spells || string.Equals(step.Source, "classSkills", StringComparison.OrdinalIgnoreCase)
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
