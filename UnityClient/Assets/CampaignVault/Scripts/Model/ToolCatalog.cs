using System;
using System.Collections.Generic;

namespace CampaignVault.UnityClient.Model
{
    /// <summary>
    /// Player-facing names for the server's tools, grouped by what they let the
    /// Dungeon Master do. The raw id and the model-facing description stay in
    /// a tooltip. Unknown tools (plugins) get a title-cased id and the first
    /// sentence of their description, under "Other".
    /// </summary>
    public static class ToolCatalog
    {
        public sealed class Entry
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public string Blurb = string.Empty;
            public string Group = string.Empty;
        }

        public const string OtherGroup = "Other";

        /// <summary>Groups in display order.</summary>
        public static readonly string[] Groups = { "At the table", "Knowledge", "Sessions", "World building", "Campaigns", OtherGroup };

        private static readonly Dictionary<string, string[]> Known = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "take_turn", new[] { "At the table", "Play a turn", "Commits what happens: moves, rolls, damage, conversations." } },
            { "combat", new[] { "At the table", "Run a fight", "Starts and runs combat: initiative, attacks and turns." } },
            { "advance_world", new[] { "At the table", "Let time pass", "Moves the calendar; the world carries on while you rest or travel." } },
            { "get_entity", new[] { "Knowledge", "Look it up", "Reads a character, place, item or quest in full." } },
            { "search_world", new[] { "Knowledge", "Search the world", "Finds people, places and rumors by name or topic." } },
            { "recall_history", new[] { "Knowledge", "Remember", "Recalls what happened earlier in the campaign." } },
            { "lookup", new[] { "Knowledge", "Consult the rules", "Rules, items, spells and the Dungeon Master's handbook." } },
            { "start_session", new[] { "Sessions", "Open the session", "Reads the table at the start of a session." } },
            { "end_session", new[] { "Sessions", "Close the session", "Writes the handoff the next session starts from." } },
            { "world_build", new[] { "World building", "Build the world", "Adds new people, places, items and quests." } },
            { "start_campaign_onboarding", new[] { "World building", "Start campaign setup", "Begins the questions that shape a new campaign." } },
            { "submit_onboarding_answer", new[] { "World building", "Answer a setup question", "Records an answer during campaign setup." } },
            { "finalize_campaign_onboarding", new[] { "World building", "Finish campaign setup", "Locks in the answers and creates the campaign." } },
            { "create_campaign", new[] { "Campaigns", "Create a campaign", "Starts a new campaign with a chosen ruleset." } },
            { "list_campaigns", new[] { "Campaigns", "List campaigns", "Shows the campaigns on this server." } },
            { "delete_campaign", new[] { "Campaigns", "Delete a campaign", "Removes a campaign and everything in it." } },
            { "get_config", new[] { "Campaigns", "Read campaign settings", "Reads the ruleset and options a campaign runs with." } },
        };

        public static Entry Describe(string id, string description)
        {
            string[] known;
            if (id != null && Known.TryGetValue(id, out known))
            {
                return new Entry { Id = id, Group = known[0], Name = known[1], Blurb = known[2] };
            }
            return new Entry { Id = id ?? string.Empty, Group = OtherGroup, Name = CharacterSheet.Title((id ?? string.Empty).Replace('_', ' ')), Blurb = FirstSentence(description) };
        }

        /// <summary>The first sentence, capped at 140 characters.</summary>
        public static string FirstSentence(string text)
        {
            string t = (text ?? string.Empty).Trim();
            int end = t.IndexOf(". ", StringComparison.Ordinal);
            if (end > 0) { t = t.Substring(0, end + 1); }
            return t.Length > 140 ? t.Substring(0, 139) + "…" : t;
        }
    }
}
