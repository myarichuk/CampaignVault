using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>A companion the DM drafted, before the player reviews it: the draft, and what the client changed or dropped.</summary>
    public sealed class DraftedCompanion
    {
        public CharacterDraft Draft = new CharacterDraft { Kind = "companion" };
        /// <summary>"Drafted at level 5, set to 2." "Dropped 'wingspan': not a stat block field." Shown on the card.</summary>
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// "DM drafts companions" on the party step: ONE model call for the whole group. The drafted members are NPC
    /// companions (not player characters) who share a history among themselves and with the characters the player
    /// built, each within one level of the party. The reply is JSON in the companion stat block's keys; the server's
    /// preview checks it like anything typed, and the player reviews each one in the builder before it is saved.
    /// </summary>
    public static class PartyDrafter
    {
        public const int MaxCompanions = 4;
        /// <summary>Levels the drafter may use: the party's, one either side, within what the builder supports.</summary>
        public const int Band = 1;

        public static int MinLevel(int partyLevel) { return Math.Max(1, partyLevel - Band); }

        public static int MaxLevel(int partyLevel, int builderMax) { return Math.Max(MinLevel(partyLevel), Math.Min(builderMax, partyLevel + Band)); }

        public static string SystemPrompt(string system, StatBlockSchema schema, IList<BuilderOption> templates, IList<PartyMember> built,
            int partyLevel, int builderMax, IDictionary<string, string> answersSoFar)
        {
            var sb = new StringBuilder();
            sb.Append("You are a game master preparing a new ").Append(SystemName(system)).Append(" campaign with a player. ");
            sb.Append("This is the same conversation you had with them while setting the campaign up.\n\n");
            sb.Append("Your job now: draft the companions who travel with the player's characters. They are non-player characters ");
            sb.Append("(hirelings, allies, pets, mounts), not player characters. Give them a shared history: with each other and with the ");
            sb.Append("player's characters below (how they met, what they owe each other, what they disagree on).\n");
            if (answersSoFar != null && answersSoFar.Count > 0)
            {
                sb.Append("\nCampaign answers (stay consistent with them):\n");
                foreach (var kv in answersSoFar) { sb.Append("- ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n'); }
            }
            sb.Append("\nThe player's characters:\n");
            foreach (var m in built)
            {
                if (m.Pending || m.Kind == "companion") { continue; }
                sb.Append("- ").Append(m.Name.Length > 0 ? m.Name : "Unnamed").Append(", ").Append(m.ClassLine.Length > 0 ? m.ClassLine : "level " + m.Level);
                if (m.Draft.Concept.Length > 0) { sb.Append(": ").Append(m.Draft.Concept); }
                sb.Append('\n');
            }
            int min = MinLevel(partyLevel), max = MaxLevel(partyLevel, builderMax);
            sb.Append("\nThe party is level ").Append(partyLevel).Append(". Each companion's level must be from ").Append(min).Append(" to ").Append(max)
                .Append(", and its stat block should be about that strong.\n");
            if (schema != null)
            {
                sb.Append("\nStat block fields (key: label, type):\n");
                foreach (var f in schema.Fields)
                {
                    bool modifiers = f.Type == StatModifiers.Type;
                    sb.Append("- ").Append(f.Key).Append(": ").Append(f.Label.Length > 0 ? f.Label : f.Key).Append(", ");
                    if (f.Type == StatRows.Type)
                    {
                        sb.Append("a list of ").Append(f.Max != int.MaxValue ? "up to " + f.Max + " " : string.Empty).Append("objects with keys ");
                        for (int c = 0; c < f.Columns.Count; c++)
                        {
                            var col = f.Columns[c];
                            if (c > 0) { sb.Append(", "); }
                            sb.Append(col.Key).Append(" (")
                                .Append(col.Type == "int" ? "whole number" + (col.Min != int.MinValue ? " " + col.Min + " to " + col.Max : string.Empty)
                                    : col.Type == "dice" ? "dice then damage type, like \"1d6+2 piercing\"" : "text")
                                .Append(col.Required ? ", required)" : ")");
                        }
                        if (f.Required) { sb.Append(", required"); }
                        sb.Append('\n');
                        continue;
                    }
                    sb.Append(f.Type == "int" ? "whole number" : modifiers ? "an object of name to whole-number bonus, like {\"Perception\": 4}"
                        : f.Type == StatBlockField.ChoiceType ? "one of: " + string.Join(", ", f.Keys.ToArray()) : "text");
                    if ((f.Type == "int" || modifiers) && f.Min != int.MinValue) { sb.Append(modifiers ? ", each " : " ").Append(f.Min).Append(" to ").Append(f.Max); }
                    if (modifiers && f.Keys.Count > 0) { sb.Append(", names from: ").Append(string.Join(", ", f.Keys.ToArray())); }
                    if (f.Required) { sb.Append(", required"); }
                    sb.Append('\n');
                }
            }
            if (templates != null && templates.Count > 0)
            {
                sb.Append("\nReference stat blocks (adapt one or write your own):\n");
                foreach (var t in templates)
                {
                    sb.Append("- ").Append(t.Label).Append(": ");
                    var parts = new List<string>();
                    foreach (var kv in t.Values) { parts.Add(kv.Key + " " + kv.Value); }
                    sb.Append(string.Join("; ", parts.ToArray())).Append('\n');
                }
            }
            return sb.ToString();
        }

        /// <summary>The one turn's request: the reply must be the JSON and nothing else.</summary>
        public static string Instruction()
        {
            return "Draft 1 to " + MaxCompanions + " companions for this party, filling what the party composition asks for that the player's characters don't cover. "
                + "Reply with ONLY a JSON array, no prose and no markdown, one object per companion: "
                + "{\"name\": \"...\", \"concept\": \"who they are and their shared history with the party, 2-3 sentences\", \"look\": \"one sentence\", "
                + "\"level\": 1, \"statblock\": {\"<field key>\": value, ...}}. Use only the stat block field keys listed; "
                + "fill every required one and \"stance\" (how they feel about the party). Numbers are bare numbers, bonuses included.";
        }

        /// <summary>
        /// Reads the reply into drafts. Nothing is fixed silently: a level outside the band is moved into it and says so,
        /// a key the stat block doesn't have is dropped and says so (the builder couldn't show it to be fixed), and a
        /// value that isn't a number stays as written for the preview to flag. False with an error when no draft is readable.
        /// </summary>
        public static bool Parse(string reply, StatBlockSchema schema, int partyLevel, int builderMax, List<DraftedCompanion> drafts, out string error)
        {
            error = string.Empty;
            string text = OnboardingBrainstorm.CleanAnswer(reply);
            int start = text.IndexOf('['), end = text.LastIndexOf(']');
            JsonValue root;
            if (start < 0 || end <= start || !JsonValue.TryParse(text.Substring(start, end - start + 1), out root) || root.Kind != JsonKind.Array)
            {
                error = "The DM's draft wasn't the JSON list it was asked for. Try again.";
                return false;
            }
            int min = MinLevel(partyLevel), max = MaxLevel(partyLevel, builderMax);
            int skipped = 0;
            foreach (var item in root.ArrayValue)
            {
                if (item.Kind != JsonKind.Object) { skipped++; continue; }
                if (drafts.Count == MaxCompanions) { skipped++; continue; }
                var d = new DraftedCompanion();
                d.Draft.Name = TextSanitizer.Clean(item.GetString("name", string.Empty), 80).Trim();
                d.Draft.Concept = TextSanitizer.Clean(item.GetString("concept", string.Empty), 1200).Trim();
                d.Draft.Look = TextSanitizer.Clean(item.GetString("look", string.Empty), 400).Trim();
                d.Draft.PartyLevel = partyLevel;
                int asked = (int)item.GetNumber("level", partyLevel);
                int level = Math.Max(min, Math.Min(max, asked));
                if (level != asked) { d.Notes.Add("Drafted at level " + asked + ", set to " + level + "."); }
                d.Draft.Level = level;
                var statblock = JsonValue.NewObject();
                var given = item.Get("statblock");
                if (given.Kind == JsonKind.Object)
                {
                    foreach (var kv in given.ObjectValue)
                    {
                        var field = FieldOf(schema, kv.Key);
                        if (schema != null && field == null) { d.Notes.Add("Dropped '" + kv.Key + "': not a stat block field."); continue; }
                        statblock.ObjectValue[field != null ? field.Key : kv.Key] = ValueFor(field, kv.Value);
                    }
                }
                if (statblock.ObjectValue.Count > 0) { d.Draft.Set(StatBlockKey, statblock); }
                drafts.Add(d);
            }
            if (drafts.Count == 0)
            {
                error = "The DM's draft had no companions in it. Try again.";
                return false;
            }
            if (skipped > 0) { drafts[drafts.Count - 1].Notes.Add(skipped + " more in the draft left out (at most " + MaxCompanions + ")."); }
            return true;
        }

        /// <summary>The companion recipe's stat block step key.</summary>
        public const string StatBlockKey = "statblock";

        private static StatBlockField FieldOf(StatBlockSchema schema, string key)
        {
            if (schema == null) { return null; }
            foreach (var f in schema.Fields) { if (string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) { return f; } }
            return null;
        }

        /// <summary>
        /// Numbers for number fields when they read as one (a "14" string too); a skills object (or "Perception +5" text)
        /// with its names matched; anything else as text, for the preview to judge.
        /// </summary>
        private static JsonValue ValueFor(StatBlockField field, JsonValue v)
        {
            if (field != null && field.Type == StatModifiers.Type && (v.Kind == JsonKind.Object || v.Kind == JsonKind.String)) { return StatModifiers.From(field, v); }
            if (field != null && field.Type == StatRows.Type && (v.Kind == JsonKind.Array || v.Kind == JsonKind.String))
            {
                var rows = StatRows.From(field, v.Kind == JsonKind.String ? JsonValue.FromString(TextSanitizer.Clean(v.StringValue, 1200)) : v);
                if (rows.Kind != JsonKind.Array) { return rows; }
                foreach (var row in rows.ArrayValue)
                {
                    if (row.Kind != JsonKind.Object) { continue; }
                    foreach (var key in new List<string>(row.ObjectValue.Keys))
                    {
                        var cell = row.ObjectValue[key];
                        if (cell.Kind == JsonKind.String) { row.ObjectValue[key] = JsonValue.FromString(TextSanitizer.Clean(cell.StringValue, 300).Trim()); }
                    }
                }
                return rows;
            }
            if (field != null && field.Type == "int")
            {
                if (v.Kind == JsonKind.Number) { return JsonValue.FromNumber(Math.Round(v.NumberValue)); }
                int n;
                if (v.Kind == JsonKind.String && int.TryParse(v.StringValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { return JsonValue.FromNumber(n); }
            }
            if (v.Kind == JsonKind.String) { return JsonValue.FromString(TextSanitizer.Clean(v.StringValue, 1200).Trim()); }
            if (v.Kind == JsonKind.Number) { return JsonValue.FromString(v.NumberValue.ToString(CultureInfo.InvariantCulture)); }
            return JsonValue.FromString(TextSanitizer.Clean(v.ToJson(), 1200));
        }

        /// <summary>What goes into the setup conversation instead of the raw JSON, so later questions know who was drafted.</summary>
        public static string ChatSummary(IList<DraftedCompanion> drafts)
        {
            var sb = new StringBuilder("I drafted these companions for you to review:");
            foreach (var d in drafts)
            {
                sb.Append("\n- ").Append(d.Draft.Name.Length > 0 ? d.Draft.Name : "Unnamed").Append(" (level ").Append(d.Draft.Level).Append(')');
                if (d.Draft.Concept.Length > 0) { sb.Append(": ").Append(d.Draft.Concept); }
            }
            return sb.ToString();
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
