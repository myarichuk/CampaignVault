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
        public const int MaxAnswerChars = 3000;

        /// <summary>Only free-text answers are worth brainstorming.</summary>
        public static bool Supports(OnboardingQuestion q)
        {
            return q != null && (q.Type == AnswerType.Text || q.Type == AnswerType.List) && q.Key != "campaign_name";
        }

        public static string SystemPrompt(OnboardingQuestion q, IDictionary<string, string> answersSoFar)
        {
            var sb = new StringBuilder();
            sb.Append("You are a game master helping a player prepare a new tabletop campaign before play starts. ");
            sb.Append("Right now you're brainstorming the answer to one setup question together.\n\n");
            sb.Append("Question: ").Append(q.Text).Append('\n');
            if (!string.IsNullOrEmpty(q.Help)) { sb.Append("About it: ").Append(q.Help).Append('\n'); }
            if (answersSoFar != null && answersSoFar.Count > 0)
            {
                sb.Append("\nAnswers so far (stay consistent with them):\n");
                foreach (var kv in answersSoFar) { sb.Append("- ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n'); }
            }
            sb.Append("\nHow to brainstorm: offer 2–4 concrete, distinct ideas at a time, each in a line or two; ");
            sb.Append("build on what the player likes and drop what they don't; ask at most one question per reply. ");
            sb.Append("Keep replies short. Don't narrate scenes and don't start the game. ");
            sb.Append("Nothing is decided until the player asks you to write the answer up.");
            return sb.ToString();
        }

        /// <summary>The last turn: the reply must be the answer itself, in the question's format.</summary>
        public static string FinalizeInstruction(OnboardingQuestion q)
        {
            var sb = new StringBuilder();
            sb.Append("Write up the final answer to the question \"").Append(q.Text).Append("\" from everything we settled. ");
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
            return text.Length > MaxAnswerChars ? text.Substring(0, MaxAnswerChars) : text;
        }
    }
}
