using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using CampaignVault.UnityClient.Model;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// "Ask the DM" on a builder step. It rides the onboarding conversation (one DM chat for the whole setup), so
    /// what the player settled about the world and the party is still in view. The DM names its picks as option ids
    /// on a SUGGEST line; ids the step doesn't offer are shown as such, never dropped silently.
    /// </summary>
    public static class BuilderAdvisor
    {
        /// <summary>Builder dividers in the conversation start with this ("Building Lyra · Class").</summary>
        public const string MarkerPrefix = "Building ";

        /// <summary>Options listed in the prompt at most; a longer list says how many were left out.</summary>
        public const int MaxPromptOptions = 160;

        private static readonly Regex SuggestLine = new Regex(@"^\s*\**\s*SUGGEST\s*\**\s*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public static string Marker(string characterName, string stepTitle)
        {
            string who = string.IsNullOrEmpty((characterName ?? string.Empty).Trim()) ? "a character" : characterName.Trim();
            return MarkerPrefix + who + " · " + stepTitle;
        }

        public static bool IsMarker(string text)
        {
            return (text ?? string.Empty).StartsWith(MarkerPrefix, StringComparison.Ordinal);
        }

        public static string SystemPrompt(string system, BuilderStep step, IList<BuilderOption> options, int count, CharacterDraft draft, IDictionary<string, string> answersSoFar)
        {
            var sb = new StringBuilder();
            sb.Append("You are a game master helping a player build a ").Append(SystemName(system)).Append(" character for their campaign, one step at a time. ");
            sb.Append("This is the same conversation you had with them while setting the campaign up; earlier messages may be about the world, the plot or the party.\n\n");
            sb.Append("Current step: ").Append(step.Title).Append('\n');
            if (count > 0) { sb.Append("The player picks ").Append(count).Append(".\n"); }
            sb.Append("Character so far: ").Append(DraftLine(draft)).Append('\n');
            if (answersSoFar != null && answersSoFar.Count > 0)
            {
                sb.Append("\nCampaign answers (stay consistent with them):\n");
                foreach (var kv in answersSoFar) { sb.Append("- ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n'); }
            }
            if (options != null && options.Count > 0)
            {
                sb.Append("\nThe options for this step, as id — name:\n");
                for (int i = 0; i < options.Count && i < MaxPromptOptions; i++)
                {
                    var o = options[i];
                    sb.Append("- ").Append(o.Id).Append(" — ").Append(o.Label);
                    if (o.Group.Length > 0) { sb.Append(" (").Append(o.Group).Append(')'); }
                    sb.Append('\n');
                }
                if (options.Count > MaxPromptOptions) { sb.Append("(and ").Append(options.Count - MaxPromptOptions).Append(" more)\n"); }
                sb.Append("\nSuggest only from these options. When you recommend some, end your reply with one line: SUGGEST: id, id (the ids exactly as listed). ");
            }
            else
            {
                sb.Append("\nThis step has no fixed options: help the player decide, briefly. ");
            }
            sb.Append("Explain each pick in a line, in terms of how they'll play and the campaign. Keep replies short, ask at most one question, and don't narrate scenes.");
            return sb.ToString();
        }

        /// <summary>The reply without its SUGGEST line, and the ids on it.</summary>
        public static string SplitSuggestions(string reply, List<string> ids)
        {
            string text = reply ?? string.Empty;
            var matches = SuggestLine.Matches(text);
            if (matches.Count == 0) { return text.Trim(); }
            var last = matches[matches.Count - 1];
            foreach (string part in last.Groups[1].Value.Split(','))
            {
                string id = part.Trim().Trim('`', '*', '"', '\'', '.').Trim();
                if (id.Length > 0 && !ids.Contains(id)) { ids.Add(id); }
            }
            return (text.Substring(0, last.Index) + text.Substring(last.Index + last.Length)).Trim();
        }

        /// <summary>Sorts suggested ids into the step's options (by id, else by name) and the ones it doesn't offer.</summary>
        public static void Match(IList<string> ids, IList<BuilderOption> options, List<string> valid, List<string> notOptions)
        {
            foreach (string id in ids)
            {
                BuilderOption hit = null;
                foreach (var o in options)
                {
                    if (string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)) { hit = o; break; }
                }
                if (hit == null)
                {
                    foreach (var o in options)
                    {
                        if (string.Equals(o.Label, id, StringComparison.OrdinalIgnoreCase)) { hit = o; break; }
                    }
                }
                if (hit != null) { if (!valid.Contains(hit.Id)) { valid.Add(hit.Id); } }
                else if (!notOptions.Contains(id)) { notOptions.Add(id); }
            }
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
