using System;
using System.Collections.Generic;
using System.Text;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// "What level should the party start at?" on the party step: the DM reads the plot and world settled so far and
    /// suggests one level with a line of why. It is a suggestion only; the player's stepper decides.
    /// </summary>
    public static class PartyLevelAdvisor
    {
        public static string SystemPrompt(string system, int max, IDictionary<string, string> answersSoFar)
        {
            var sb = new StringBuilder();
            sb.Append("You are a game master preparing a new ").Append(system).Append(" campaign with a player. ");
            sb.Append("This is the same conversation you had with them while setting the campaign up.\n");
            if (answersSoFar != null && answersSoFar.Count > 0)
            {
                sb.Append("\nCampaign answers so far:\n");
                foreach (var kv in answersSoFar) { sb.Append("- ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n'); }
            }
            sb.Append("\nYour job: suggest the level the player characters should start at, so the plot's first threats and stakes fit them. ");
            sb.Append("Levels run 1 to ").Append(max).Append(". Prefer the lowest level that suits the story.");
            return sb.ToString();
        }

        public static string Instruction()
        {
            return "Reply with ONLY a JSON object, no prose and no markdown: {\"level\": 3, \"reason\": \"one sentence on why this level fits the plot\"}.";
        }

        /// <summary>The suggestion, clamped to 1..max. False when the reply holds no readable level.</summary>
        public static bool Parse(string reply, int max, out int level, out string reason)
        {
            level = 0;
            reason = string.Empty;
            string text = OnboardingBrainstorm.CleanAnswer(reply);
            int start = text.IndexOf('{'), end = text.LastIndexOf('}');
            JsonValue root;
            if (start < 0 || end <= start || !JsonValue.TryParse(text.Substring(start, end - start + 1), out root) || root.Kind != JsonKind.Object) { return false; }
            int asked = (int)root.GetNumber("level", 0);
            if (asked < 1) { return false; }
            level = Math.Max(1, Math.Min(Math.Max(1, max), asked));
            reason = TextSanitizer.Clean(root.GetString("reason", string.Empty), 240).Trim();
            return true;
        }
    }
}
