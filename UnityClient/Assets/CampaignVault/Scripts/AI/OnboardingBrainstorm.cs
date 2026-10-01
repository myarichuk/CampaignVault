using System.Collections.Generic;
using System.Text;
using CampaignVault.UnityClient.App;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// Prompts for brainstorming one onboarding answer with the model before
    /// submitting it: a free chat on the question, then a "write it up" turn
    /// whose reply becomes the answer draft the player edits and submits.
    /// </summary>
    public static class OnboardingBrainstorm
    {
        /// <summary>Longer write-ups are kept whole, but the field warns: every later step re-reads the answers.</summary>
        public const int MaxAnswerChars = 6000;
        /// <summary>One player message; over it SEND is disabled and the counter says so.</summary>
        public const int MaxMessageChars = 16000;
        /// <summary>A DM reply is stored (and re-sent) up to this; the cut shows as "(truncated)".</summary>
        public const int MaxReplyChars = 6000;
        /// <summary>What one turn re-sends of the chat; past it the middle drops out, visibly.</summary>
        public const int MaxConversationChars = 48000;

        /// <summary>Chat role for "the setup moved to the next question": a divider on screen, a short note to the model.</summary>
        public const string MarkerRole = "marker";

        /// <summary>Only free-text answers are worth brainstorming.</summary>
        public static bool Supports(OnboardingQuestion q)
        {
            return q != null && (q.Type == AnswerType.Text || q.Type == AnswerType.List) && q.Key != "campaign_name";
        }

        public static string SystemPrompt(OnboardingQuestion q, IDictionary<string, string> answersSoFar)
        {
            var sb = new StringBuilder();
            sb.Append("You are a game master helping a player prepare a new tabletop campaign before play starts. ");
            sb.Append("You talk the whole setup through with them in one conversation, one setup question at a time; ");
            sb.Append("earlier messages may belong to earlier questions, or already cover later ones.\n\n");
            sb.Append("Current question: ").Append(q.Text).Append('\n');
            if (!string.IsNullOrEmpty(q.Help)) { sb.Append("About it: ").Append(q.Help).Append('\n'); }
            if (answersSoFar != null && answersSoFar.Count > 0)
            {
                sb.Append("\nAnswers so far (stay consistent with them):\n");
                foreach (var kv in answersSoFar) { sb.Append("- ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n'); }
            }
            sb.Append("\nHow to brainstorm: offer 2–4 concrete, distinct ideas at a time, each in a line or two; ");
            sb.Append("build on what the player likes and drop what they don't; ask at most one question per reply. ");
            sb.Append("Keep replies short. Don't narrate scenes and don't start the game. ");
            // The conversation carries across questions, so drifting ahead isn't lost: say so, and come back.
            sb.Append("Focus on the current question: the world, the plot, the party, the characters, the opening scene and the factions each get their own. ");
            sb.Append("If the player drifts into another one, keep what they said (it stays in this conversation for when that question comes), say so in a line, and return to the current one. ");
            sb.Append("If the conversation already settled the current question, say so and suggest writing it up rather than starting over. ");
            sb.Append("Nothing is decided until the player asks you to write the answer up.");
            return sb.ToString();
        }

        /// <summary>The last turn: the reply must be the answer itself, in the question's format.</summary>
        public static string FinalizeInstruction(OnboardingQuestion q)
        {
            var sb = new StringBuilder();
            sb.Append("Write up the final answer to the question \"").Append(q.Text).Append("\" from everything we settled in this conversation that bears on it; leave out what belongs to other questions. ");
            sb.Append("Reply with ONLY the answer text: no preamble, no options, no markdown headings. ");
            if (q.Key == "pc_roster")
            {
                sb.Append("One player character per line, as: Name — ancestry and class, one line of concept.");
            }
            else if (q.Type == AnswerType.List)
            {
                sb.Append("One entry per line, each a short name with an optional few words after a dash.");
            }
            else
            {
                sb.Append("Keep it under 120 words.");
            }
            return sb.ToString();
        }

        /// <summary>Strips wrappers models add anyway (code fences, "Answer:" labels, quotes).</summary>
        public static string CleanAnswer(string reply)
        {
            string text = (reply ?? string.Empty).Trim();
            if (text.StartsWith("```"))
            {
                int firstBreak = text.IndexOf('\n');
                text = firstBreak >= 0 ? text.Substring(firstBreak + 1) : string.Empty;
                if (text.EndsWith("```")) { text = text.Substring(0, text.Length - 3); }
                text = text.Trim();
            }
            if (text.StartsWith("Answer:", System.StringComparison.OrdinalIgnoreCase)) { text = text.Substring(7).Trim(); }
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"' && text.IndexOf('"', 1) == text.Length - 1)
            {
                text = text.Substring(1, text.Length - 2).Trim();
            }
            return text;
        }

        /// <summary>The chat as the model sees it: dropped messages left out, question dividers as short user notes.</summary>
        public static List<KeyValuePair<string, string>> ModelMessages(IList<KeyValuePair<string, string>> chat, HashSet<int> dropped)
        {
            var messages = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < chat.Count; i++)
            {
                if (dropped != null && dropped.Contains(i)) { continue; }
                var m = chat[i];
                if (m.Key != MarkerRole) { messages.Add(m); continue; }
                messages.Add(new KeyValuePair<string, string>("user", BuilderAdvisor.IsMarker(m.Value)
                    ? "(Setup note: we're now on the character builder: " + m.Value + ")"
                    : "(Setup note: we've moved on to the next question: \"" + m.Value + "\")"));
            }
            return messages;
        }

        /// <summary>True once the player and the DM have actually talked (dividers alone don't count).</summary>
        public static bool HasTalk(IList<KeyValuePair<string, string>> chat)
        {
            foreach (var m in chat) { if (m.Key == "user") { return true; } }
            return false;
        }

        public static int ConversationChars(IList<KeyValuePair<string, string>> chat)
        {
            int total = 0;
            foreach (var m in chat) { total += m.Value.Length; }
            return total;
        }

        /// <summary>
        /// Indexes of the messages a turn no longer sends: over the budget, the
        /// first message (usually the player's pitch) and the newest ones stay,
        /// the middle goes. Empty while the chat fits.
        /// </summary>
        public static HashSet<int> Dropped(IList<KeyValuePair<string, string>> chat, int budget)
        {
            var dropped = new HashSet<int>();
            if (chat.Count == 0 || ConversationChars(chat) <= budget) { return dropped; }
            int used = chat[0].Value.Length;
            int keepFrom = chat.Count;
            for (int i = chat.Count - 1; i > 0; i--)
            {
                // The newest message always goes, even alone over the budget.
                if (i < chat.Count - 1 && used + chat[i].Value.Length > budget) { break; }
                used += chat[i].Value.Length;
                keepFrom = i;
            }
            for (int i = 1; i < keepFrom; i++) { dropped.Add(i); }
            return dropped;
        }
    }
}
