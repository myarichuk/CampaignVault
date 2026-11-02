using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// "Write backgrounds for this character" on the builder's background step. The shipped rules have few backgrounds
    /// (SRD 5.1 has only acolyte), so the DM writes a few that fit this character's concept and the campaign, as the
    /// YAML a background template holds. The client saves each through world_build (kind background); the server
    /// checks it against the ruleset's budget and refuses one that's over it, with the reason. Saved ones join the
    /// step's options, tagged homebrew, beside the shipped ones.
    /// </summary>
    public static class BackgroundWriter
    {
        /// <summary>How many the DM is asked for, and the most the client will save from one reply.</summary>
        public const int Count = 3;

        private static readonly Regex Fence = new Regex(@"```[ \t]*(?:ya?ml)?[ \t]*\r?\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex NameLine = new Regex(@"^name:[ \t]*[""']?([A-Za-z0-9_\-]+)[""']?[ \t]*$", RegexOptions.Multiline);

        /// <summary>The step a writer serves: a pick from the backgrounds source.</summary>
        public static bool Serves(BuilderStep step)
        {
            return step != null && step.Kind == StepKinds.PickOne && string.Equals(step.Source, "backgrounds", StringComparison.OrdinalIgnoreCase);
        }

        public static string SystemPrompt(string system, CharacterDraft draft, IList<BuilderOption> existing, IDictionary<string, string> answersSoFar)
        {
            string sys = (system ?? string.Empty).ToLowerInvariant();
            var sb = new StringBuilder();
            sb.Append("You are a game master writing ").Append(Count).Append(" character backgrounds for one player character, as YAML templates the game's rules engine loads. ");
            sb.Append("Each should be a different, plausible past for this exact character, grounded in the campaign below.\n\n");
            sb.Append("The character:\n");
            Line(sb, "Name", draft.Name);
            Line(sb, "Concept", draft.Concept);
            Line(sb, "Gender", draft.Gender);
            Line(sb, "Pronouns", draft.Pronouns);
            Line(sb, "Look", draft.Look);
            foreach (var kv in draft.Choices)
            {
                if (kv.Value.Kind == JsonKind.String) { Line(sb, kv.Key, kv.Value.StringValue); }
            }
            if (answersSoFar != null && answersSoFar.Count > 0)
            {
                sb.Append("\nThe campaign (stay consistent with it):\n");
                foreach (var kv in answersSoFar) { sb.Append("- ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n'); }
            }
            if (existing != null && existing.Count > 0)
            {
                sb.Append("\nAlready offered (don't repeat these ids): ");
                var ids = new List<string>();
                foreach (var o in existing) { ids.Add(o.Id); }
                sb.Append(string.Join(", ", ids.ToArray())).Append('\n');
            }
            sb.Append("\nGender and the setting may colour a background's story (what someone like this would have done in this world), but never its mechanics.\n");
            sb.Append("\nRules every background must follow, or the engine refuses it:\n");
            if (sys == "pf2e")
            {
                sb.Append("- boosts: two different abilities (Strength, Dexterity, Constitution, Intelligence, Wisdom, Charisma)\n");
                sb.Append("- exactly one trained skill: skillProficiencies: [OneSkill], or skillOptions: [SkillA, SkillB] for a choice\n");
                sb.Append("- lore: one Lore skill, e.g. \"Sailing Lore\"\n");
                sb.Append("- skillFeat: one Player Core skill feat id, e.g. assurance, experienced_smuggler, specialty_crafting\n");
                sb.Append("- no gold, toolProficiencies or languages\n");
            }
            else
            {
                sb.Append("- skillProficiencies: exactly 2 D&D 5e skills\n");
                sb.Append("- toolProficiencies and languages: at most 2 entries between them\n");
                sb.Append("- feature: a short name for one narrative perk; describe what it does in the description\n");
                sb.Append("- equipment: at most 8 entries, each { name: <a plain item> } with an optional quantity\n");
                sb.Append("- gold: 0 to 25\n");
            }
            sb.Append("- name: a new snake_case id; label: the display name; description: two or three sentences\n");
            sb.Append("\nReply with one short line per background saying why it fits, then each background in its own ```yaml fence. ");
            sb.Append("Example shape:\n```yaml\nname: river_pilot\nlabel: River Pilot\ndescription: ...\n");
            sb.Append(sys == "pf2e"
                ? "boosts: [Dexterity, Wisdom]\nskillOptions: [Athletics, Survival]\nlore: River Lore\nskillFeat: assurance\n```"
                : "skillProficiencies: [Athletics, Survival]\ntoolProficiencies: [Vehicles (water)]\nfeature: River Rights\nequipment:\n  - { name: Pole hook }\ngold: 10\n```");
            return sb.ToString();
        }

        public const string Request = "Write the backgrounds now.";

        /// <summary>The YAML of each fenced block that names itself, at most <see cref="Count"/>, in reply order.</summary>
        public static List<string> Extract(string reply)
        {
            var found = new List<string>();
            foreach (Match m in Fence.Matches(reply ?? string.Empty))
            {
                string yaml = m.Groups[1].Value.Trim();
                if (NameOf(yaml).Length == 0) { continue; }
                found.Add(yaml + "\n");
                if (found.Count == Count) { break; }
            }
            return found;
        }

        /// <summary>The template's name: line, or empty.</summary>
        public static string NameOf(string yaml)
        {
            var m = NameLine.Match(yaml ?? string.Empty);
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        /// <summary>The reply's prose: everything outside the fences, trimmed.</summary>
        public static string Prose(string reply)
        {
            return Fence.Replace(reply ?? string.Empty, string.Empty).Trim();
        }

        private static void Line(StringBuilder sb, string label, string value)
        {
            if (!string.IsNullOrEmpty(value)) { sb.Append("- ").Append(label).Append(": ").Append(value).Append('\n'); }
        }
    }
}
